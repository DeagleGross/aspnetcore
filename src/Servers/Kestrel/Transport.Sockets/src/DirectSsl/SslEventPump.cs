// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Uncomment the following line to enable debug counters for SSL diagnostics
// #define DIRECTSSL_DEBUG_COUNTERS

#pragma warning disable IDE0011, IDE0005 // brace/using analyzer noise from prototype merge

using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Connection;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Interop;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl;

// ─────────────────────────────────────────────────────────────────────────────
// SSL event pump that handles accept, handshake, and I/O events on a dedicated
// thread. Uses EPOLLEXCLUSIVE on the listen socket to distribute accept load
// across workers.
//
// All epoll bookkeeping has been moved behind SafeEpollHandle (proposal 2):
//   * No raw epoll_create1 / epoll_ctl / epoll_wait P/Invokes
//   * No EpollEvent / EpollData wire-format struct usage
//   * No EPOLL_* magic numbers — typed EpollEvents / EpollOptions enums
// ─────────────────────────────────────────────────────────────────────────────
internal sealed class SslEventPump : IDisposable
{
    private readonly ILogger? _logger;
    private readonly int _id;

    private readonly SafeEpollHandle _epoll;

    // Established connections (handshake complete) - use fd as key
    private readonly ConcurrentDictionary<int, SslConnectionState> _connections = new();

    // Connections still handshaking - local to pump thread, no sync needed
    private readonly Dictionary<int, HandshakingConnection> _handshaking = new();

    private readonly Thread _pumpThread;
    private volatile bool _running = true;

    // Listen socket (added with EPOLLEXCLUSIVE)
    private SafeSocketHandle? _listenSocket;
    private int _listenFd = -1;
    private SafeSslContextHandle? _sslCtx;
    private ChannelWriter<DirectSslConnection>? _readyConnections;
    private MemoryPool<byte>? _memoryPool;
    private bool _noDelay;

    // Cached loggers for connection creation (initialized in StartWithListenSocket)
    private ILogger<SslConnectionState>? _sslConnectionStateLogger;
    private ILogger<DirectSslConnection>? _directSslConnectionLogger;

    // Cached listen endpoint to avoid getsockname syscall per connection
    private EndPoint? _listenEndPoint;

    /// <summary>
    /// Lightweight struct to track SSL connections during handshake.
    /// </summary>
    private struct HandshakingConnection
    {
        public int Fd;
        public SafeSslHandle Ssl;
        public SafeSocketHandle Socket;
        public IPEndPoint? RemoteEndPoint;
    }

    public SslEventPump(ILogger? sslPumpLogger, int id)
    {
        _id = id;
        _logger = sslPumpLogger;

        _epoll = SafeEpollHandle.Create();

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
        SafeSocketHandle listenSocket,
        SafeSslContextHandle sslCtx,
        ChannelWriter<DirectSslConnection> readyConnections,
        MemoryPool<byte> memoryPool,
        ILoggerFactory loggerFactory,
        bool noDelay)
    {
        _listenSocket = listenSocket;
        _listenFd = (int)listenSocket.DangerousGetHandle();
        _sslCtx = sslCtx;
        _readyConnections = readyConnections;
        _memoryPool = memoryPool;
        _noDelay = noDelay;

        _sslConnectionStateLogger = loggerFactory.CreateLogger<SslConnectionState>();
        _directSslConnectionLogger = loggerFactory.CreateLogger<DirectSslConnection>();

        // Cache listen endpoint once to avoid getsockname syscall per connection
        using (var tempSocket = new Socket(new SafeSocketHandle((IntPtr)_listenFd, ownsHandle: false)))
        {
            _listenEndPoint = tempSocket.LocalEndPoint;
        }

        // Register listen socket with EPOLLEXCLUSIVE — only one worker wakes per connection.
        // Use fd-as-token: we'll compare against _listenFd in the dispatch loop.
        _epoll.Add(
            listenSocket,
            EpollEvents.Read,
            EpollOptions.ExclusiveWakeup,
            token: (IntPtr)_listenFd);

        _logger?.LogDebug("Pump {Id}: Added listen socket fd={Fd} with EPOLLEXCLUSIVE", _id, _listenFd);

        _pumpThread.Start();
    }

    public void Start() => _pumpThread.Start();

    public void Register(SslConnectionState conn)
    {
        _logger?.LogDebug("Registering fd={Fd} with epoll", conn.Fd);

        conn.Pump = this;
        _connections[conn.Fd] = conn;

        _epoll.Add(
            conn.Socket,
            EpollEvents.Read | EpollEvents.PeerClose,
            EpollOptions.None,
            token: (IntPtr)conn.Fd);

        _logger?.LogDebug("Successfully registered fd={Fd} with epoll", conn.Fd);
    }

    public void Unregister(SafeSocketHandle socket, int fd)
    {
        _connections.TryRemove(fd, out _);
        _epoll.Remove(socket);
    }

    /// <summary>
    /// Modify the epoll events for a socket. Used to dynamically toggle Write
    /// when SSL_write would block.
    /// </summary>
    public void ModifyEvents(SafeSocketHandle socket, int fd, EpollEvents events)
    {
        _epoll.Modify(socket, events | EpollEvents.PeerClose, EpollOptions.None, token: (IntPtr)fd);
    }

    private void PumpLoop()
    {
        const int MaxEvents = 256;
        Span<EpollNotification> events = stackalloc EpollNotification[MaxEvents];

        while (_running)
        {
            int timeout = _handshaking.Count > 0 ? 10 : 1000;
            int numEvents;
            try
            {
                numEvents = _epoll.Wait(events, timeout);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "epoll_wait failed");
                break;
            }

            for (int i = 0; i < numEvents; i++)
            {
                int fd = (int)events[i].Token;
                EpollEvents mask = events[i].Events;

                if (fd == _listenFd)
                {
                    AcceptConnections();
                    continue;
                }

                if (_handshaking.TryGetValue(fd, out var handshakingConn))
                {
                    TryAdvanceHandshake(fd, handshakingConn);
                    continue;
                }

                if (!_connections.TryGetValue(fd, out var conn))
                {
                    continue;
                }

                if ((mask & (EpollEvents.Error | EpollEvents.HangUp)) != 0)
                {
                    // Error events — handle in at least one active handler
                    mask |= EpollEvents.Read | EpollEvents.Write;
                }

                // Process Read first — even if PeerClose is set, there may be data to read
                if ((mask & EpollEvents.Read) != 0)
                {
                    conn.OnReadable();
                }

                if ((mask & EpollEvents.Write) != 0)
                {
                    conn.OnWritable();
                }

                if ((mask & EpollEvents.PeerClose) != 0)
                {
                    if ((mask & EpollEvents.Read) == 0)
                    {
                        // No data to read, peer closed - signal error
                        _connections.TryRemove(fd, out _);
                        conn.OnError(new IOException("Peer closed connection"));
                    }
                }
            }
        }

        // Cleanup handshaking connections
        foreach (var kvp in _handshaking)
        {
            var conn = kvp.Value;
            conn.Ssl?.Dispose();
            conn.Socket?.Dispose();
        }
        _handshaking.Clear();
    }

    /// <summary>
    /// Accept new connections from the listen socket.
    /// Loops until EAGAIN (no more pending connections).
    /// </summary>
    private void AcceptConnections()
    {
        while (true)
        {
            // ── Proposal 3 territory: accept4 + peer-address capture ──
            // This still uses the prototype's Interop/NativeSsl.cs accept4 wrapper because
            // proposal 3 (SocketOptionName.TcpDeferAccept + Socket.TryAcceptNonBlocking)
            // hasn't been built into RuntimeProposal/ yet.
            var (clientFd, remoteEndPoint) = NativeSsl.AcceptNonBlockingWithPeerAddress(_listenFd);

            if (clientFd == -1) break;       // EAGAIN
            if (clientFd == -2) continue;    // Error

            if (_noDelay)
            {
                NativeSsl.SetTcpNoDelay(clientFd);
            }

            // Wrap fd as SafeSocketHandle so SafeSslHandle can hold a strong ref.
            var clientSocket = new SafeSocketHandle((IntPtr)clientFd, ownsHandle: true);

            SafeSslHandle ssl;
            try
            {
                ssl = SafeSslHandle.CreateForSocket(_sslCtx!, clientSocket, isServer: true);
            }
            catch (SslException ex)
            {
                _logger?.LogWarning(ex, "Failed to create SSL handle for fd={Fd}", clientFd);
                clientSocket.Dispose();
                continue;
            }

            try
            {
                _epoll.Add(
                    clientSocket,
                    EpollEvents.Read | EpollEvents.PeerClose,
                    EpollOptions.None,
                    token: (IntPtr)clientFd);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "epoll Add failed for handshaking fd={Fd}", clientFd);
                ssl.Dispose();
                clientSocket.Dispose();
                continue;
            }

            _handshaking[clientFd] = new HandshakingConnection
            {
                Fd = clientFd,
                Ssl = ssl,
                Socket = clientSocket,
                RemoteEndPoint = remoteEndPoint
            };

            // Try handshake immediately (might complete for resumed sessions)
            TryAdvanceHandshake(clientFd, _handshaking[clientFd]);
        }
    }

    /// <summary>
    /// Try to advance the TLS handshake for a connection.
    /// </summary>
    private void TryAdvanceHandshake(int fd, HandshakingConnection conn)
    {
        SslOperationStatus status;
        try
        {
            status = conn.Ssl.Handshake();
        }
        catch (SslException ex)
        {
            _logger?.LogDebug(ex, "Handshake failed for fd={Fd}", fd);
            CleanupHandshaking(fd, conn);
            return;
        }

        switch (status)
        {
            case SslOperationStatus.Complete:
                _handshaking.Remove(fd);

                var connectionState = new SslConnectionState(fd, conn.Ssl, conn.Socket, _sslConnectionStateLogger);
                connectionState.SetHandshakeComplete();
                connectionState.Pump = this;
                _connections[fd] = connectionState;

                // Confirm epoll registration with steady-state event mask
                _epoll.Modify(
                    conn.Socket,
                    EpollEvents.Read | EpollEvents.PeerClose,
                    EpollOptions.None,
                    token: (IntPtr)fd);

                if (_readyConnections != null && _memoryPool != null)
                {
                    var directConnection = new DirectSslConnection(
                        fd,
                        connectionState,
                        this,
                        _listenEndPoint,
                        conn.RemoteEndPoint,
                        _memoryPool,
                        _directSslConnectionLogger!);

                    directConnection.Start();

                    if (!_readyConnections.TryWrite(directConnection))
                    {
                        directConnection.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                }
                return;

            case SslOperationStatus.WantRead:
                // Already registered for Read, just wait
                return;

            case SslOperationStatus.WantWrite:
                _epoll.Modify(
                    conn.Socket,
                    EpollEvents.Read | EpollEvents.Write | EpollEvents.PeerClose,
                    EpollOptions.None,
                    token: (IntPtr)fd);
                return;

            case SslOperationStatus.Closed:
            default:
                _logger?.LogDebug("Handshake closed/failed for fd={Fd}", fd);
                CleanupHandshaking(fd, conn);
                return;
        }
    }

    private void CleanupHandshaking(int fd, HandshakingConnection conn)
    {
        _handshaking.Remove(fd);
        _epoll.Remove(conn.Socket);
        conn.Ssl.Dispose();
        conn.Socket.Dispose();
    }

    public void Dispose()
    {
        _running = false;
        _pumpThread.Join(2000);
        _epoll.Dispose();
    }
}
