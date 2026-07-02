// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Uncomment the following line to enable debug counters for SSL diagnostics
#define DIRECTSSL_DEBUG_COUNTERS

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Engines.OpenSslDirect.Connection;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Engines.OpenSslDirect.Interop;
using Microsoft.Extensions.Logging;
// HEAD has a global-namespace 'NativeSsl' that would shadow ours; alias ensures we always
// resolve to our OpenSslDirect-namespaced version.
using OSsl = Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Engines.OpenSslDirect.Interop.NativeSsl;
using EpollEvent = Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Engines.OpenSslDirect.Interop.EpollEvent;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Engines.OpenSslDirect;

/// <summary>
/// SSL event pump that handles accept, handshake, and I/O events on a dedicated thread.
/// Uses EPOLLEXCLUSIVE on the listen socket to distribute accept load across workers.
/// </summary>
internal sealed partial class SslEventPump : IDisposable
{
    private readonly ILogger? _logger;
    private readonly int _id;

    private readonly int _epollFd;

    // Established connections (handshake complete) - flat array indexed by fd.
    // Linux fds are small integers (typically < 65536), so direct indexing is O(1)
    // with no hashing or locking - mirrors nginx's ngx_cycle->files[fd] pattern.
    private const int MaxFd = 65536;
    private readonly SslConnectionState?[] _connections = new SslConnectionState?[MaxFd];

    // Connections still handshaking - local to pump thread, no sync needed
    private readonly Dictionary<int, HandshakingConnection> _handshaking = new();

    private readonly Thread _pumpThread;
    private volatile bool _running = true;

    // Listen socket (added with EPOLLEXCLUSIVE)
    private int _listenFd = -1;
    private IntPtr _sslCtx = IntPtr.Zero;
    private ChannelWriter<DirectSslConnection>? _readyConnections;
    private MemoryPool<byte>? _memoryPool;
    private ILoggerFactory? _loggerFactory;
    private bool _noDelay;

    // Cached loggers for connection creation (initialized in StartWithListenSocket)
    private ILogger<SslConnectionState>? _sslConnectionStateLogger;
    private ILogger<DirectSslConnection>? _directSslConnectionLogger;

    // Cached listen endpoint to avoid getsockname syscall per connection
    private EndPoint? _listenEndPoint;

#if DIRECTSSL_DEBUG_COUNTERS
    // Instance counters for this pump
    private long _totalRegistered;
    private long _totalUnregistered;
    private long _totalErrors;
    private long _totalRdhup;
    private long _totalRdhupWithData;
    private long _totalAccepted;
    private long _totalHandshakeComplete;
    private long _totalHandshakeFailed;
    private DateTime _lastLogTime = DateTime.UtcNow;

    // Static counters that can be incremented from connection state
    public static long TotalWriteEof;
    public static long TotalReadEof;
    public static long TotalWriteErrors;
    public static long TotalReadErrors;
    public static long TotalSslErrorSyscall;
    public static long TotalSslErrorSyscallImmediate;  // SYSCALL on initial ReadAsync call
    public static long TotalSslErrorSyscallAfterEpoll; // SYSCALL after TryCompleteRead
    public static long TotalSslErrorSyscallRet0;       // SSL_read returned 0 (unexpected EOF)
    public static long TotalSslErrorSyscallRetNeg1;    // SSL_read returned -1 (syscall error)
    public static long TotalSslErrorSyscallErrno0;     // errno was 0
    public static long TotalSslErrorSyscallErrno11;    // errno was EAGAIN (11)
    public static long TotalSslErrorSyscallErrnoOther; // errno was something else
    public static long TotalSslErrorZeroReturn;
    public static long TotalSslErrorSsl;
    public static long TotalSslErrorOther;
    public static long TotalWriteWouldBlock;
    public static long TotalWriteImmediate;
    public static long TotalRequestsCompleted;  // Track completed request/response cycles

    // Handshake-cost instrumentation (mirror of TlsSession-engine pump):
    public static long TotalHandshakeStarted;
    public static long TotalHandshakeSyncComplete;
    public static long TotalHandshakeCallCount;
    public static long TotalHandshakeWallTicks;
    public static long TotalHandshakeBusyTicks;

    // Per-call Read/Write instrumentation (added for stall diagnosis)
    public static long TotalReadCallCount;     // # of Session.Read / SSL_read invocations (all paths)
    public static long TotalReadBytes;         // sum of bytes returned (>0 only)
    public static long TotalReadBusyTicks;     // sum of Stopwatch ticks inside the SSL call
    public static long MaxReadBusyTicks;       // longest single-call wall ticks
    public static long TotalReadComplete;      // # of calls that returned Complete with bytes>0
    public static long TotalReadWantRead;      // # of calls that returned WantRead
    
    public static long TotalReadAsyncEntries;     // # times ReadAsync was called
    public static long TotalReadAsyncBodyTicks;   // total ticks spent in ReadAsync body (entry to return)
    public static long TotalReadAsyncGapTicks;    // total ticks between consecutive ReadAsync entries per connection
    public static long TotalReadAsyncGapCount;    // # of gaps measured

    public static long TotalWriteCallCount;
    public static long TotalWriteBytes;
    public static long TotalWriteBusyTicks;
    public static long MaxWriteBusyTicks;

    private readonly Dictionary<int, (long StartTicks, int CallCount, long BusyTicks)> _handshakeState = new();
#endif

    /// <summary>
    /// Lightweight struct to track SSL connections during handshake.
    /// Uses less memory than SslConnectionState since we don't need full read/write machinery.
    /// NOTE: We don't create the Socket wrapper - use fd directly to avoid syscall overhead.
    /// </summary>
    private struct HandshakingConnection
    {
        public int Fd;
        public IntPtr Ssl;
        public System.Net.IPEndPoint? RemoteEndPoint;  // Captured from accept4 to avoid getpeername syscall
    }

    public SslEventPump(ILogger? sslPumpLogger, int id)
    {
        _id = id;
        _logger = sslPumpLogger;

        _epollFd = OSsl.epoll_create1(0);
        if (_epollFd < 0)
        {
            throw new InvalidOperationException($"epoll_create1 failed: {Marshal.GetLastWin32Error()}");
        }

        _pumpThread = new Thread(PumpLoop)
        {
            Name = $"SslEventPump-{id}",
            IsBackground = true
        };
    }

    /// <summary>
    /// Start the pump with a listen socket. The listen socket is registered with EPOLLEXCLUSIVE
    /// so that only one worker wakes per incoming connection (prevents thundering herd).
    /// </summary>
    public void StartWithListenSocket(
        int listenFd,
        IntPtr sslCtx,
        ChannelWriter<DirectSslConnection> readyConnections,
        MemoryPool<byte> memoryPool,
        ILoggerFactory loggerFactory,
        bool noDelay)
    {
        _listenFd = listenFd;
        _sslCtx = sslCtx;
        _readyConnections = readyConnections;
        _memoryPool = memoryPool;
        _loggerFactory = loggerFactory;
        _noDelay = noDelay;

        // Cache loggers for connection creation
        _sslConnectionStateLogger = loggerFactory.CreateLogger<SslConnectionState>();
        _directSslConnectionLogger = loggerFactory.CreateLogger<DirectSslConnection>();

        // Cache listen endpoint once to avoid getsockname syscall per connection
        // We need a temporary Socket wrapper to get the endpoint (this is a one-time cost)
        using (var tempSocket = new Socket(new SafeSocketHandle((IntPtr)listenFd, ownsHandle: false)))
        {
            _listenEndPoint = tempSocket.LocalEndPoint;
        }

        // Add listen socket with EPOLLEXCLUSIVE - only one worker wakes per connection
        var ev = new EpollEvent
        {
            Events = OSsl.EPOLLIN | OSsl.EPOLLEXCLUSIVE,
            Data = new EpollData { Fd = listenFd }
        };

        int result = OSsl.epoll_ctl(_epollFd, OSsl.EPOLL_CTL_ADD, listenFd, ref ev);
        if (result < 0)
        {
            int errno = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to add listen socket to epoll: errno={errno}");
        }

        _logger?.LogDebug("Pump {Id}: Added listen socket fd={Fd} with EPOLLEXCLUSIVE", _id, listenFd);

        // Start the pump thread
        _pumpThread.Start();
    }

    /// <summary>
    /// Start the pump without a listen socket.
    /// </summary>
    public void Start()
    {
        _pumpThread.Start();
    }

    public void Register(SslConnectionState conn)
    {
        _logger?.LogDebug("Registering fd={Fd} with epoll", conn.Fd);

        conn.Pump = this;
        _connections[conn.Fd] = conn;
#if DIRECTSSL_DEBUG_COUNTERS
        Interlocked.Increment(ref _totalRegistered);
#endif

        // Register for EPOLLIN initially - EPOLLOUT will be added dynamically when needed
        // Using level-triggered mode (no EPOLLET) for stability
        var ev = new EpollEvent
        {
            Events = OSsl.EPOLLIN | OSsl.EPOLLRDHUP,
            Data = new EpollData { Fd = conn.Fd }
        };

        int result = OSsl.epoll_ctl(_epollFd, OSsl.EPOLL_CTL_ADD, conn.Fd, ref ev);
        if (result < 0)
        {
            int errno = Marshal.GetLastWin32Error();
            _logger?.LogError("epoll_ctl ADD failed for fd={Fd}: errno={Errno}", conn.Fd, errno);
            throw new InvalidOperationException($"epoll_ctl ADD failed: {errno}");
        }

        _logger?.LogDebug("Successfully registered fd={Fd} with epoll", conn.Fd);
    }

    public void Unregister(int fd)
    {
        if ((uint)fd < MaxFd)
        {
            Volatile.Write(ref _connections[fd], null);
        }
#if DIRECTSSL_DEBUG_COUNTERS
        Interlocked.Increment(ref _totalUnregistered);
#endif

        OSsl.epoll_ctl(_epollFd, OSsl.EPOLL_CTL_DEL, fd, IntPtr.Zero);
    }

    /// <summary>
    /// Modify the epoll events for a file descriptor.
    /// Used to dynamically add EPOLLOUT when a write would block.
    /// </summary>
    public void ModifyEvents(int fd, uint events)
    {
        // Using level-triggered mode (no EPOLLET) for stability
        var ev = new EpollEvent
        {
            Events = events | OSsl.EPOLLRDHUP,
            Data = new EpollData { Fd = fd }
        };

        int result = OSsl.epoll_ctl(_epollFd, OSsl.EPOLL_CTL_MOD, fd, ref ev);
        if (result < 0)
        {
            int errno = Marshal.GetLastWin32Error();
            _logger?.LogWarning("epoll_ctl MOD failed for fd={Fd}: errno={Errno}", fd, errno);
        }
    }

    private void PumpLoop()
    {
        // Pin this pump thread to a specific CPU core (like nginx worker_cpu_affinity).
        // This ensures the pump thread stays on one core, keeping CPU caches warm
        // and reducing involuntary context switches from OS scheduler migration.
        TrySetCpuAffinity(_id);

        const int MaxEvents = 256;
        var events = new EpollEvent[MaxEvents];

        // Fairness instrumentation (measurement only, no rotate-start for OSD baseline).
        long _batchN0 = 0, _batchN1 = 0, _batchN2 = 0, _batchN3 = 0, _batchN4 = 0, _batchN5plus = 0;
        var _firstFdHits = new System.Collections.Generic.Dictionary<int, long>(64);
        long _totalFirstFdHits = 0;
        var _diagStart = System.Diagnostics.Stopwatch.StartNew();
        bool _diagEmitted = false;

        while (_running)
        {
            // Use shorter timeout when there are handshaking connections
            int timeout = _handshaking.Count > 0 ? 10 : 1000;
            int numEvents = OSsl.epoll_wait(_epollFd, events, MaxEvents, timeout);

            // Track batch-size distribution.
            if (numEvents > 0)
            {
                if (numEvents == 1) { _batchN1++; }
                else if (numEvents == 2) { _batchN2++; }
                else if (numEvents == 3) { _batchN3++; }
                else if (numEvents == 4) { _batchN4++; }
                else { _batchN5plus++; }
                int _firstFd = events[0].Data.Fd;
                _firstFdHits.TryGetValue(_firstFd, out var _h);
                _firstFdHits[_firstFd] = _h + 1;
                _totalFirstFdHits++;
            }
            else if (numEvents == 0)
            {
                _batchN0++;
            }

            // Emit fairness diag once after 13 seconds of run (mid-wrk load).
            if (!_diagEmitted && _diagStart.Elapsed.TotalSeconds >= 13)
            {
                _diagEmitted = true;
                long _totalBatches = _batchN1 + _batchN2 + _batchN3 + _batchN4 + _batchN5plus;
                double _avgBatch = _totalBatches > 0
                    ? (double)(_batchN1 * 1 + _batchN2 * 2 + _batchN3 * 3 + _batchN4 * 4 + _batchN5plus * 5) / _totalBatches
                    : 0.0;
                Console.WriteLine(
                    $"[Pump-diag id={_id}] batches: timeout={_batchN0} n=1:{_batchN1} n=2:{_batchN2} n=3:{_batchN3} n=4:{_batchN4} n>=5:{_batchN5plus} avgBatch={_avgBatch:F2}");
                var _firstFdList = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, long>>(_firstFdHits);
                _firstFdList.Sort((a, b) => b.Value.CompareTo(a.Value));
                var _sb = new System.Text.StringBuilder();
                _sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"[Pump-diag id={_id}] firstFd totalBatches={_totalFirstFdHits}");
                int _n = 0;
                foreach (var kv in _firstFdList)
                {
                    if (_n++ >= 10) { break; }
                    double _pct = _totalFirstFdHits > 0 ? (100.0 * kv.Value / _totalFirstFdHits) : 0.0;
                    _sb.Append(System.Globalization.CultureInfo.InvariantCulture, $" fd={kv.Key}:{kv.Value}({_pct:F1}%)");
                }
                Console.WriteLine(_sb.ToString());
                Console.Out.Flush();
            }

#if DIRECTSSL_DEBUG_COUNTERS
            // Log stats every 5 seconds
            var now = DateTime.UtcNow;
            if ((now - _lastLogTime).TotalSeconds >= 5)
            {
                _lastLogTime = now;
                Console.WriteLine($"[Pump {_id}] Handshaking: {_handshaking.Count}, Accepted: {_totalAccepted}");
                Console.WriteLine($"[Pump {_id}] Handshake: Complete={_totalHandshakeComplete}, Failed={_totalHandshakeFailed}");
                Console.WriteLine($"[Pump {_id}] Registered: {_totalRegistered}, Unregistered: {_totalUnregistered}, Errors: {_totalErrors}");
            }
#endif

            if (numEvents < 0)
            {
                int errno = Marshal.GetLastWin32Error();
                if (errno == 4)
                {
                    continue; // EINTR
                }
                _logger?.LogError("epoll_wait failed: errno={Errno}", errno);
                break;
            }

            for (int i = 0; i < numEvents; i++)
            {
                int fd = events[i].Data.Fd;
                uint mask = events[i].Events;

                if (fd == 0 && mask == 0)
                {
                    continue;
                }

                // Check if this is the listen socket
                if (fd == _listenFd)
                {
                    AcceptConnections();
                    continue;
                }

                // Check if this is a handshaking connection
                if (_handshaking.TryGetValue(fd, out var handshakingConn))
                {
                    TryAdvanceHandshake(fd, handshakingConn);
                    continue;
                }

                // Check if this is an established connection (direct array lookup - O(1), no hashing)
                var conn = (uint)fd < MaxFd ? Volatile.Read(ref _connections[fd]) : null;
                if (conn is null)
                {
                    continue;
                }

                if ((mask & (OSsl.EPOLLERR | OSsl.EPOLLHUP)) != 0)
                {
                    // When error events occur, add EPOLLIN|EPOLLOUT
                    // to handle the events in at least one active handler.
                    mask |= OSsl.EPOLLIN | OSsl.EPOLLOUT;
#if DIRECTSSL_DEBUG_COUNTERS
                    Interlocked.Increment(ref _totalErrors);
#endif
                }

                // Process EPOLLIN first - even if EPOLLRDHUP is set, there may be data to read
                if ((mask & OSsl.EPOLLIN) != 0)
                {
                    conn.OnReadable();
                }

                if ((mask & OSsl.EPOLLOUT) != 0)
                {
                    conn.OnWritable();
                }

                // Handle EPOLLRDHUP - peer closed their write side
                if ((mask & OSsl.EPOLLRDHUP) != 0)
                {
#if DIRECTSSL_DEBUG_COUNTERS
                    if ((mask & OSsl.EPOLLIN) != 0)
                    {
                        Interlocked.Increment(ref _totalRdhupWithData);
                    }
                    else
                    {
                        Interlocked.Increment(ref _totalRdhup);
                    }
#endif
                    if ((mask & OSsl.EPOLLIN) == 0)
                    {
                        // No data to read, peer closed - signal error
                        if ((uint)fd < MaxFd)
                        {
                            Volatile.Write(ref _connections[fd], null);
                        }
#if DIRECTSSL_DEBUG_COUNTERS
                        Interlocked.Increment(ref _totalErrors);
#endif
                        conn.OnError(new IOException("Peer closed connection"));
                    }
                }
            }
        }

        // Cleanup handshaking connections
        foreach (var kvp in _handshaking)
        {
            var conn = kvp.Value;
            if (conn.Ssl != IntPtr.Zero)
            {
                OSsl.SSL_free(conn.Ssl);
            }
            OSsl.close(conn.Fd);
        }
        _handshaking.Clear();
    }

    /// <summary>
    /// Accept new connections from the listen socket.
    /// Loops until EAGAIN (no more pending connections).
    /// Captures peer address from accept4 to avoid getpeername syscall later.
    /// </summary>
    private void AcceptConnections()
    {
        while (true)
        {
            // Use accept4 with address capture to avoid separate getpeername syscall
            var (clientFd, remoteEndPoint) = OSsl.AcceptNonBlockingWithPeerAddress(_listenFd);

            if (clientFd == -1)
            {
                // EAGAIN - no more pending connections
                break;
            }

            if (clientFd == -2)
            {
                // Error - continue trying
                continue;
            }

#if DIRECTSSL_DEBUG_COUNTERS
            Interlocked.Increment(ref _totalAccepted);
#endif

            // Set TCP_NODELAY for low latency
            if (_noDelay)
            {
                OSsl.SetTcpNoDelay(clientFd);
            }

            // Create SSL and bind to socket
            IntPtr ssl = OSsl.SSL_new(_sslCtx);
            if (ssl == IntPtr.Zero)
            {
#if DIRECTSSL_DEBUG_COUNTERS
                Interlocked.Increment(ref _totalHandshakeFailed);
#endif
                OSsl.close(clientFd);
                continue;
            }

            OSsl.SSL_set_fd(ssl, clientFd);
            OSsl.SSL_set_accept_state(ssl);

            // Register client socket with epoll for handshake events
            var ev = new EpollEvent
            {
                Events = OSsl.EPOLLIN | OSsl.EPOLLRDHUP,
                Data = new EpollData { Fd = clientFd }
            };

            int result = OSsl.epoll_ctl(_epollFd, OSsl.EPOLL_CTL_ADD, clientFd, ref ev);
            if (result < 0)
            {
                int errno = Marshal.GetLastWin32Error();
                _logger?.LogWarning("epoll_ctl ADD failed for handshaking fd={Fd}: errno={Errno}", clientFd, errno);
#if DIRECTSSL_DEBUG_COUNTERS
                Interlocked.Increment(ref _totalHandshakeFailed);
#endif
                OSsl.SSL_free(ssl);
                OSsl.close(clientFd);
                continue;
            }

            // Track handshaking connection with captured remote endpoint
            _handshaking[clientFd] = new HandshakingConnection
            {
                Fd = clientFd,
                Ssl = ssl,
                RemoteEndPoint = remoteEndPoint
            };

            // Try handshake immediately (might complete for resumed sessions)
            TryAdvanceHandshake(clientFd, _handshaking[clientFd]);
        }
    }

    /// <summary>
    /// Try to advance the TLS handshake for a connection.
    /// </summary>
    private void TryAdvanceHandshake(
        int fd,
        HandshakingConnection conn)
    {
#if DIRECTSSL_DEBUG_COUNTERS
        long callStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_handshakeState.TryGetValue(fd, out var hs))
        {
            hs = (StartTicks: callStartTicks, CallCount: 0, BusyTicks: 0L);
            Interlocked.Increment(ref TotalHandshakeStarted);
        }
        hs.CallCount++;
#endif
        OSsl.ERR_clear_error();
        int n = OSsl.SSL_do_handshake(conn.Ssl);
#if DIRECTSSL_DEBUG_COUNTERS
        hs.BusyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - callStartTicks;
        _handshakeState[fd] = hs;
#endif

        if (n == 1)
        {
            // Handshake complete! Create connection and enqueue to Kestrel
#if DIRECTSSL_DEBUG_COUNTERS
            Interlocked.Increment(ref _totalHandshakeComplete);
            long wallTicks = System.Diagnostics.Stopwatch.GetTimestamp() - hs.StartTicks;
            Interlocked.Add(ref TotalHandshakeWallTicks, wallTicks);
            Interlocked.Add(ref TotalHandshakeBusyTicks, hs.BusyTicks);
            Interlocked.Add(ref TotalHandshakeCallCount, hs.CallCount);
            if (hs.CallCount == 1)
            {
                Interlocked.Increment(ref TotalHandshakeSyncComplete);
            }
            _handshakeState.Remove(fd);
#endif
            _handshaking.Remove(fd);

            // Create SslConnectionState for the established connection
            var connectionState = new SslConnectionState(fd, conn.Ssl, _sslConnectionStateLogger);
            connectionState.SetHandshakeComplete();

            // Register with our connections dictionary and epoll
            connectionState.Pump = this;
            _connections[fd] = connectionState;

            // Update epoll to use standard connection events (already registered, just confirm)
            var ev = new EpollEvent
            {
                Events = OSsl.EPOLLIN | OSsl.EPOLLRDHUP,
                Data = new EpollData { Fd = fd }
            };
            OSsl.epoll_ctl(_epollFd, OSsl.EPOLL_CTL_MOD, fd, ref ev);

            // Create DirectSslConnection using fd directly (no Socket wrapper)
            // This avoids ~5+ syscalls per connection (fstat, getsockopt, fcntl, etc.)
            if (_readyConnections != null && _memoryPool != null)
            {
                var directConnection = new DirectSslConnection(
                    fd,                           // Use fd directly - no Socket wrapper
                    connectionState,
                    this,
                    _listenEndPoint,              // Cached - avoids getsockname syscall
                    conn.RemoteEndPoint,          // Captured from accept4 - avoids getpeername syscall
                    _memoryPool,
                    _directSslConnectionLogger!);

                directConnection.Start();

                if (!_readyConnections.TryWrite(directConnection))
                {
                    // Channel closed (shutting down) - dispose connection
                    directConnection.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            return;
        }

        int error = OSsl.SSL_get_error(conn.Ssl, n);

        if (error == OSsl.SSL_ERROR_WANT_READ)
        {
            // Already registered for EPOLLIN, just wait
            return;
        }

        if (error == OSsl.SSL_ERROR_WANT_WRITE)
        {
            // Need to write - add EPOLLOUT
            var ev = new EpollEvent
            {
                Events = OSsl.EPOLLIN | OSsl.EPOLLOUT | OSsl.EPOLLRDHUP,
                Data = new EpollData { Fd = fd }
            };
            OSsl.epoll_ctl(_epollFd, OSsl.EPOLL_CTL_MOD, fd, ref ev);
            return;
        }

        // Handshake failed - cleanup
        _logger?.LogDebug("Handshake failed for fd={Fd}: error={Error}", fd, error);
#if DIRECTSSL_DEBUG_COUNTERS
        Interlocked.Increment(ref _totalHandshakeFailed);
        _handshakeState.Remove(fd);
#endif
        _handshaking.Remove(fd);
        OSsl.epoll_ctl(_epollFd, OSsl.EPOLL_CTL_DEL, fd, IntPtr.Zero);
        OSsl.SSL_free(conn.Ssl);
        OSsl.close(fd);
    }

    public void Dispose()
    {
        _running = false;
        _pumpThread.Join(2000);
        OSsl.close(_epollFd);
    }

    /// <summary>
    /// Pin the current thread to a specific CPU core using sched_setaffinity.
    /// Falls back gracefully if the core index exceeds available CPUs.
    /// </summary>
    private void TrySetCpuAffinity(int coreIndex)
    {
        try
        {
            int cpuCount = Environment.ProcessorCount;
            int targetCore = coreIndex % cpuCount;

            // cpu_set_t is a bitmask, we need at least 8 bytes (64 CPUs)
            // For simplicity, use 128 bytes (1024 CPUs max)
            const int CpuSetSize = 128;
            Span<byte> cpuSet = stackalloc byte[CpuSetSize];
            cpuSet.Clear();

            // Set the bit for our target core
            cpuSet[targetCore / 8] = (byte)(1 << (targetCore % 8));

            unsafe
            {
                fixed (byte* ptr = cpuSet)
                {
                    // pid=0 means current thread
                    int result = sched_setaffinity(0, (nuint)CpuSetSize, ptr);
                    if (result == 0)
                    {
                        Console.WriteLine($"Pump {_id}: pinned to CPU core {targetCore}");
                        _logger?.LogDebug("Pump {Id}: pinned to CPU core {Core}", _id, targetCore);
                    }
                    else
                    {
                        int errno = Marshal.GetLastWin32Error();
                        Console.WriteLine($"Pump {_id}: failed to pin to core {targetCore}: errno={errno}");
                        _logger?.LogDebug("Pump {Id}: failed to pin to core {Core}: errno={Errno}", _id, targetCore, errno);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Pump {_id}: CPU affinity not supported: {ex}");
            _logger?.LogDebug(ex, "Pump {Id}: CPU affinity not supported", _id);
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int sched_setaffinity(int pid, nuint cpusetsize, byte* mask);
}