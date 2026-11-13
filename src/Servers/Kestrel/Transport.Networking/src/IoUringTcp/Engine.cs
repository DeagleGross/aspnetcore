// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;
using BioNative = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringBio.Native;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp;

internal sealed class Engine
{
    private const int ServerPageCount = 2048, ServerPageSize = 16384;
    private static readonly int ClientPageBytes = int.Parse(Environment.GetEnvironmentVariable("NETWORKPROTO_CLIENT_PAGE_KB") ?? "64", CultureInfo.InvariantCulture) * 1024;
    private static readonly int ClientPageCount = int.Parse(Environment.GetEnvironmentVariable("NETWORKPROTO_CLIENT_PAGES") ?? "128", CultureInfo.InvariantCulture);
    private const ulong AcceptId = 1, WakeId = 2, CancelAcceptId = 3, CancelWakeId = 4;
    private readonly ConcurrentQueue<Native.Command> _commands = new();
    private readonly Dictionary<ulong, RingConnection> _connections = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ChannelWriter<IProviderConnection> _accepted;
    private readonly IPEndPoint? _endpoint;
    private readonly ConcurrentQueue<(int Fd, IPEndPoint Remote, TaskCompletionSource<IProviderConnection> Completion)> _connects = new();
    private readonly int _cpu;
    private readonly Lock _wakeGate = new();
    private readonly bool _coalesced;
    private readonly int _pageCount;
    internal readonly int PageBytes;
    private nint _ring, _pages, _cipherPages, _wakeBuffer;
    private byte[] _pageStorage = [], _cipherStorage = [];
    private int _listener = -1, _wakeFd = -1, _pumpThread, _wakeScheduled, _pendingRejections;
    private bool _stop, _unbound, _disposed, _acceptActive, _wakeActive, _cancelAccept, _cancelWake, _resumePages;
    private ulong _nextId;
    private long _acceptRetryAt;
    private bool _acceptFailing;
    private const int AcceptRetryMilliseconds = 50;
    private readonly Stack<int> _freePages;
    private readonly bool[] _leasedPages;
    private readonly bool[] _cipherLeases;
    private long _wakes, _iterations, _commandsProcessed, _pagesRead, _bytesRead, _sendCommands, _finalCommands;
    private long _rejected, _rejectedDisposed;
    internal readonly bool Tls, Layered, RequireKtls, GuardPageReads;
    internal readonly bool Shared;
    internal readonly int FinalMode, SendBufferSize;
    internal readonly ILogger Logger;
    internal Action<ReadOnlySpan<byte>>? HandshakePrefixCallback { get; }
    internal readonly Counters Metrics = new();
    internal NativeMemoryPool? OutputPool { get; }
    internal nint Context
    {
        get; private set;
    }
    internal nint BioMethod
    {
        get; private set;
    }
    internal SessionTlsContext? SessionContext
    {
        get; private set;
    }
    internal bool BioTls => Tls && Layered;
    internal bool FinalSendEnabled => FinalMode != 0;
    internal Task Started => _started.Task;
    internal Task Stopped => _stopped.Task;

    // A null endpoint makes this a client engine: it never accepts and only adopts sockets connected by RingClient.
    internal Engine(IPEndPoint? endpoint, int cpu, bool tls, string cert, string key, ILogger logger, ChannelWriter<IProviderConnection> accepted, bool layered, bool shared = false,
        Action<ReadOnlySpan<byte>>? handshakePrefixCallback = null)
    {
        _endpoint = endpoint;
        _cpu = cpu;
        Tls = tls;
        var client = endpoint is null;
        PageBytes = client ? ClientPageBytes : ServerPageSize;
        _pageCount = client ? ClientPageCount : ServerPageCount;
        _freePages = new Stack<int>(Enumerable.Range(0, _pageCount));
        _leasedPages = new bool[_pageCount];
        _cipherLeases = new bool[_pageCount];
        // Joining a pending wake is a pure saving, so it is the default; NETWORKPROTO_COALESCE=0 restores a wake per command.
        _coalesced = Environment.GetEnvironmentVariable("NETWORKPROTO_COALESCE") is not "0";
        // The runtime's fd-bound TlsSession owns the ciphertext, so the ring-owned BIO layer never applies.
        Layered = layered && !(tls && SessionTlsContext.IsSelected);
        Logger = logger;
        _accepted = accepted;
        Shared = shared;
        HandshakePrefixCallback = handshakePrefixCallback;
        if (shared && AdaptiveOutputPool.Enabled)
        {
            OutputPool = new NativeMemoryPool(null, 512 * 1024 * 1024, pageSize: 4096);
        }
        RequireKtls = Environment.GetEnvironmentVariable("NETWORKPROTO_KTLS") == "1";
        GuardPageReads = Environment.GetEnvironmentVariable("NETWORKPROTO_GUARD_PAGE_READ") == "1";
        FinalMode = Environment.GetEnvironmentVariable("NETWORKPROTO_FINAL_SEND") switch
        {
            "1" => 1,
            "2" => 2,
            "3" => 3,
            _ => 0
        };
        SendBufferSize = int.Parse(Environment.GetEnvironmentVariable("NETWORKPROTO_URING_SNDBUF") ?? "0", CultureInfo.InvariantCulture);
        using (ExecutionContext.SuppressFlow())
        {
            new Thread(() => Run(cert, key)) { IsBackground = true, Name = $"Managed io_uring CPU {cpu}" }.Start();
        }
    }

    internal void Enqueue(Native.Command command)
    {
        _commands.Enqueue(command);
        if (_coalesced && (Environment.CurrentManagedThreadId == _pumpThread || Interlocked.Exchange(ref _wakeScheduled, 1) != 0))
        {
            return;
        }
        lock (_wakeGate)
        {
            if (!_disposed && _wakeFd >= 0)
            {
                Interlocked.Increment(ref _wakes);
                Native.Check(Native.Wake(_wakeFd), "eventfd wake");
            }
        }
    }
    internal void Stop() => Enqueue(new Native.Command { Kind = 6 });

    private unsafe void Run(string cert, string key)
    {
        Exception? failure = null;
        try
        {
            _pumpThread = Environment.CurrentManagedThreadId;
            if (_cpu >= 0)
            {
                Native.Check(Native.SetCpu(_cpu), "affinity");
            }
            ArgumentOutOfRangeException.ThrowIfNegative(SendBufferSize);
            if (RequireKtls && !Tls)
            {
                throw new NotSupportedException("kTLS requires TLS.");
            }
            _pageStorage = GC.AllocateUninitializedArray<byte>(_pageCount * PageBytes, pinned: true);
            fixed (byte* pointer = _pageStorage)
            {
                _pages = (nint)pointer;
            }
            _wakeBuffer = (nint)NativeMemory.AllocZeroed(8);
            if (BioTls)
            {
                _cipherStorage = GC.AllocateUninitializedArray<byte>(_pageCount * PageBytes, pinned: true);
                fixed (byte* pointer = _cipherStorage)
                {
                    _cipherPages = (nint)pointer;
                }
            }
            _ring = Native.Create(Environment.GetEnvironmentVariable("NETWORKPROTO_DEFER") == "0" ? 0 : 1, !Tls || BioTls ? 1 : 0, out var error);
            if (_ring == 0)
            {
                throw new Win32Exception(error, "io_uring creation failed.");
            }
            if (!Tls || BioTls)
            {
                for (var page = 0; page < _pageCount; page++)
                {
                    Native.Offer(_ring, Buffer(page, BioTls), PageBytes, page);
                }
            }
            if (Tls && SessionTlsContext.IsSelected)
            {
                if (RequireKtls)
                {
                    throw new NotSupportedException("kTLS is not available through the runtime TlsSession.");
                }
                SessionContext = new SessionTlsContext(cert, key);
                if (TransportDiagnostics.Enabled)
                {
                    Console.WriteLine($"TLS_LIBRARY impl=session managed=true cpu={_cpu}");
                }
            }
            else if (Tls)
            {
                Context = Native.Context(cert, key, RequireKtls ? 1 : 0, out var tlsError);
                if (Context == 0)
                {
                    throw new IOException($"OpenSSL context failed: 0x{tlsError:x}.");
                }
                if (BioTls)
                {
                    BioMethod = BioNative.Method();
                    if (BioMethod == 0)
                    {
                        throw new IOException("OpenSSL BIO method creation failed.");
                    }
                }
                if (TransportDiagnostics.Enabled)
                {
                    Console.WriteLine($"TLS_LIBRARY version=\"{Marshal.PtrToStringUTF8(Native.Version())}\" managed=true cpu={_cpu}");
                }
            }
            _wakeFd = Native.Check(Native.EventFd(), "eventfd");
            if (_endpoint is not null)
            {
                _listener = Native.Check(Native.Listen(BinaryPrimitives.ReadUInt32BigEndian(_endpoint.Address.GetAddressBytes()), _endpoint.Port), "listen");
                StartAccept();
            }
            StartWake();
            _started.SetResult();
            var completions = new Native.Completion[1024];
            while (!_stop || _connections.Count != 0 || !_commands.IsEmpty || _acceptActive || _wakeActive
                || _cancelAccept || _cancelWake || Volatile.Read(ref _pendingRejections) != 0)
            {
                Volatile.Write(ref _wakeScheduled, 0);
                if (_acceptRetryAt != 0 && Environment.TickCount64 >= _acceptRetryAt)
                {
                    _acceptRetryAt = 0;
                    if (!_acceptActive && !_unbound && !_stop)
                    {
                        StartAccept();
                    }
                }
                for (var i = 0; i < 1024 && _commands.TryDequeue(out var command); i++)
                {
                    _commandsProcessed++;
                    Process(command);
                }
                if (_resumePages)
                {
                    _resumePages = false;
                    foreach (var state in _connections.Values.ToArray())
                    {
                        state.ResumeReader();
                    }
                }
                fixed (Native.Completion* pointer = completions)
                {
                    var count = Native.Check(Native.Collect(_ring, pointer, completions.Length, _commands.IsEmpty ? 1 : 0), "submit/collect");
                    _iterations++;
                    Metrics.Cqes += count;
                    for (var i = 0; i < count; i++)
                    {
                        Dispatch(completions[i]);
                    }
                }
            }
        }
        catch (Exception error)
        {
            failure = error;
            Logger.LogError(error, "Managed io_uring worker failed.");
            _started.TrySetException(error);
            _accepted.TryComplete(error);
            if (_connections.Count != 0)
            {
                // A broken CQ drain cannot safely release kernel-owned send pins/BIO buffers.
                // This non-shipping prototype terminates rather than report false completion.
                Environment.FailFast("io_uring worker failed with outstanding connection ownership.", error);
            }
        }
        finally
        {
            lock (_wakeGate)
            {
                _disposed = true;
                if (_ring != 0)
                {
                    Native.Destroy(_ring);
                    _ring = 0;
                }
                CloseFd(ref _wakeFd);
            }
            CloseFd(ref _listener);
            if (BioMethod != 0)
            {
                BioNative.FreeMethod(BioMethod);
            }

            if (Context != 0)
            {
                Native.FreeContext(Context);
            }
            SessionContext?.Dispose();

            GC.KeepAlive(_pageStorage);
            GC.KeepAlive(_cipherStorage);
            if (OutputPool is { Outstanding: 0 } outputPool)
            {
                outputPool.Dispose();
            }
            NativeMemory.Free((void*)_wakeBuffer);
            Report();
            if (failure is null)
            {
                _stopped.TrySetResult();
            }
            else
            {
                _stopped.TrySetException(failure);
            }
        }
    }

    internal void Prepare(Native.Operation operation, int fd, nint data, int length, ulong id, ulong target = 0, int flags = 0)
    {
        Native.Check(Native.Prepare(_ring, operation, fd, data, length, id, target, flags), "prepare");
        switch (operation)
        {
            case Native.Operation.Accept:
                Metrics.AcceptSqes++;
                break;
            case Native.Operation.Receive:
                Metrics.ReceiveSqes++;
                break;
            case Native.Operation.Send:
            case Native.Operation.LinkedSend:
                Metrics.SendSqes++;
                break;
            case Native.Operation.ReadPoll:
            case Native.Operation.WritePoll:
                Metrics.PollSqes++;
                break;
        }
    }
    private void StartAccept()
    {
        Prepare(Native.Operation.Accept, _listener, 0, 0, AcceptId, flags: Tls && !Layered ? 1 : 0);
        _acceptActive = true;
    }
    private void StartWake()
    {
        Prepare(Native.Operation.WakeRead, _wakeFd, _wakeBuffer, 8, WakeId);
        _wakeActive = true;
    }
    private void Unbind()
    {
        _unbound = true;
        _acceptRetryAt = 0;
        if (_acceptActive && !_cancelAccept)
        {
            Prepare(Native.Operation.Cancel, 0, 0, 0, CancelAcceptId, AcceptId);
            _cancelAccept = true;
        }
    }
    private void Dispatch(Native.Completion completion)
    {
        var terminal = (completion.Flags & Native.More) == 0;
        switch (completion.Id)
        {
            case AcceptId:
                if (terminal)
                {
                    _acceptActive = false;
                }

                if (completion.Result >= 0)
                {
                    _acceptFailing = false;
                    var fd = completion.Result;
                    var local = _unbound ? null : SocketEndpoints.TryGet(fd, peer: false);
                    var remote = local is null ? null : SocketEndpoints.TryGet(fd, peer: true);
                    if (local is null || remote is null)
                    {
                        // The listener is unbound, or the peer reset before the worker adopted the socket.
                        CloseFd(ref fd);
                    }
                    else
                    {
                        var state = new RingConnection(this, ++_nextId, fd, local, remote);
                        _connections.Add(state.Id, state);
                        state.Start();
                    }
                }
                else if (completion.Result != Native.Canceled)
                {
                    // Running out of descriptors or memory ends the multishot accept but is transient: the connection waits in the kernel backlog until accepting is armed again.
                    if (!Native.IsTransientAcceptFailure(completion.Result))
                    {
                        Native.Check(completion.Result, "accept");
                    }
                    Metrics.AcceptFailures++;
                    if (!_acceptFailing)
                    {
                        _acceptFailing = true;
                        Logger.LogWarning("io_uring accept failed with errno {Error}; accepting pauses for {Delay} ms and retries.", -completion.Result, AcceptRetryMilliseconds);
                    }
                    _acceptRetryAt = Environment.TickCount64 + AcceptRetryMilliseconds;
                }
                if (!_acceptActive && !_unbound && _acceptRetryAt == 0)
                {
                    StartAccept();
                }

                break;
            case WakeId:
                _wakeActive = false;
                if (!_stop)
                {
                    StartWake();
                }

                break;
            case CancelAcceptId:
                _cancelAccept = false;
                break;
            case CancelWakeId:
                _cancelWake = false;
                break;
            default:
                if (!_connections.TryGetValue(completion.Id >> 4, out var connection))
                {
                    throw new InvalidOperationException("Completion outlived its managed connection.");
                }
                connection.Complete((RingConnection.Operation)(completion.Id & 15), completion.Result, completion.Flags);
                break;
        }
    }
    private unsafe void Process(Native.Command command)
    {
        if (command.Kind == 5)
        {
            Unbind();
            return;
        }
        if (command.Kind == 6)
        {
            _stop = true;
            Unbind();
            foreach (var connection in _connections.Values.ToArray())
            {
                connection.Fail(new ConnectionAbortedException("Worker stopped."));
                connection.Application?.Abort(new ConnectionAbortedException("Worker stopped."));
            }
            if (_wakeActive && !_cancelWake)
            {
                Prepare(Native.Operation.Cancel, 0, 0, 0, CancelWakeId, WakeId);
                _cancelWake = true;
            }
            return;
        }
        if (command.Kind == 8)
        {
            if (_connects.TryDequeue(out var connect))
            {
                AdoptConnected(connect.Fd, connect.Remote, connect.Completion);
            }
            return;
        }
        if (!_connections.TryGetValue(command.Connection, out var state))
        {
            if (command.Kind == 3)
            {
                return;
            }

            throw new InvalidOperationException("Command targeted a released connection.");
        }
        switch (command.Kind)
        {
            case 1:
                _sendCommands++;
                if (command.Unused != 0)
                {
                    _finalCommands++;
                }

                state.Send((nint)command.Data, command.Length, command.Unused != 0);
                break;
            case 2:
                state.ReturnPage(command.Page);
                break;
            case 3:
                state.RequestClose();
                break;
            case 4:
                state.Dispose();
                _connections.Remove(state.Id);
                break;
            case 7:
                state.Application!.PumpOutput();
                break;
            default:
                throw new InvalidOperationException("Unknown transport command.");
        }
    }

    internal void Connected(int fd, IPEndPoint remote, TaskCompletionSource<IProviderConnection> completion)
    {
        _connects.Enqueue((fd, remote, completion));
        Enqueue(new Native.Command { Kind = 8 });
    }

    private void AdoptConnected(int fd, IPEndPoint remote, TaskCompletionSource<IProviderConnection> completion)
    {
        if (_stop)
        {
            CloseFd(ref fd);
            completion.TrySetException(new ObjectDisposedException(nameof(Engine)));
            return;
        }
        var local = SocketEndpoints.TryGet(fd, peer: false);
        if (local is null)
        {
            CloseFd(ref fd);
            completion.TrySetException(new IOException("The connected socket has no local endpoint."));
            return;
        }
        var state = new RingConnection(this, ++_nextId, fd, local, remote, completion);
        _connections.Add(state.Id, state);
        state.Start();
    }

    internal IProviderConnection PublishClient(RingConnection state, TaskCompletionSource<IProviderConnection> completion)
    {
        var connection = ProviderNetworkConnection.ForRing(this, state);
        if (!completion.TrySetResult(connection))
        {
            connection.Abort(new ConnectionAbortedException("Connect was abandoned."));
            Interlocked.Increment(ref _pendingRejections);
            _ = DisposeRejectedAsync(connection);
        }
        return connection;
    }

    internal IProviderConnection Publish(RingConnection state)
    {
        IProviderConnection connection = Shared ? ProviderNetworkConnection.ForRing(this, state) : new Connection(this, state.Id, state.LocalEndPoint, state.RemoteEndPoint, Tls);
        if (!_accepted.TryWrite(connection))
        {
            _rejected++;
            Interlocked.Increment(ref _pendingRejections);
            connection.Abort(new ConnectionAbortedException("Listener stopped."));
            _ = DisposeRejectedAsync(connection);
        }
        return connection;
    }
    private async Task DisposeRejectedAsync(IProviderConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            Interlocked.Increment(ref _rejectedDisposed);
        }
        catch (Exception error)
        {
            Logger.LogError(error, "Rejected connection disposal failed.");
            _stopped.TrySetException(error);
        }
        finally { Interlocked.Decrement(ref _pendingRejections); }
    }
    internal void ForgetUnpublished(ulong id) => Enqueue(new Native.Command { Kind = 4, Connection = id });
    internal nint Buffer(int page, bool cipher = false) => (cipher ? _cipherPages : _pages) + page * PageBytes;
    internal int RentPage()
    {
        if (!_freePages.TryPop(out var page))
        {
            return -1;
        }

        ClaimPage(page, false);
        return page;
    }
    internal void ClaimPage(int page, bool cipher)
    {
        var leases = cipher ? _cipherLeases : _leasedPages;
        if ((uint)page >= _pageCount || leases[page])
        {
            throw new InvalidOperationException("Receive page was already leased.");
        }

        leases[page] = true;
        if (cipher)
        {
            Metrics.CipherReceived++;
        }
    }
    internal void OfferPage(int page, bool cipher = false)
    {
        var leases = cipher ? _cipherLeases : _leasedPages;
        if ((uint)page >= _pageCount || !leases[page])
        {
            throw new InvalidOperationException("Receive page returned twice.");
        }

        leases[page] = false;
        if (Tls && !cipher)
        {
            _resumePages |= _freePages.Count == 0;
            _freePages.Push(page);
        }
        else
        {
            Native.Offer(_ring, Buffer(page, cipher), PageBytes, page);
        }
        if (cipher)
        {
            Metrics.CipherReturned++;
        }
    }
    internal unsafe void Received(IProviderConnection connection, int page, int length)
    {
        _pagesRead++;
        _bytesRead += length;
        if (connection is ProviderNetworkConnection shared)
        {
            shared.Receive(Buffer(page), length, page);
        }
        else
        {
            ((Connection)connection).Input.Append((void*)Buffer(page), length, page);
        }
    }
    internal void CloseFd(ref int fd)
    {
        if (fd < 0)
        {
            return;
        }

        var result = Native.Close(fd);
        fd = -1;
        if (result < 0)
        {
            Logger.LogError("Socket close failed: {Error}", result);
        }
    }
    private void Report()
    {
        if (!TransportDiagnostics.Enabled)
        {
            return;
        }
        Console.WriteLine("OWNED_METRICS " + JsonSerializer.Serialize(new
        {
            cpu = _cpu,
            tls = Tls,
            layered = Layered,
            managedEngine = true,
            coalesced = _coalesced,
            stepInterop = 0,
            collectInterop = _iterations,
            wakeInterop = _wakes,
            commands = _commandsProcessed,
            pages = _pagesRead,
            bytes = _bytesRead,
            acceptSqes = Metrics.AcceptSqes,
            receiveSqes = Metrics.ReceiveSqes,
            sendSqes = Metrics.SendSqes,
            pollSqes = Metrics.PollSqes,
            cqes = Metrics.Cqes,
            nativeSubmitWaitCalls = _iterations,
            returnedPages = Metrics.ReturnedPages,
            sslReads = Metrics.SslReads,
            receiveAdapterCopyBytes = 0,
            rejectedAccepts = _rejected,
            rejectedAcceptsDisposed = _rejectedDisposed,
            sendCommands = _sendCommands,
            finalSendCommands = _finalCommands,
            liveConnections = _connections.Count,
            leasedPages = _leasedPages.Count(x => x),
            leasedCipherPages = _cipherLeases.Count(x => x)
        }));
        Console.WriteLine("KTLS_METRICS " + JsonSerializer.Serialize(new
        {
            mode = BioTls ? "custom-bio" : "fd",
            required = RequireKtls ? 1 : 0,
            handshakes = Metrics.Handshakes,
            rx = Metrics.KtlsRx,
            tx = Metrics.KtlsTx,
            rejected = Metrics.KtlsRejected
        }));
        Console.WriteLine("TLS_READ_METRICS " + JsonSerializer.Serialize(new
        {
            guard = GuardPageReads ? 1 : 0,
            pageDrives = Metrics.PageDrives,
            pageSkips = Metrics.PageSkips,
            sslReads = Metrics.SslReads,
            successfulReads = Metrics.ReadSuccess,
            wantReadAllOperations = Metrics.WantRead,
            writeRetries = Metrics.TlsWriteRetries,
            tlsErrors = Metrics.TlsErrors,
            peerAborts = Metrics.PeerAborts
        }));
        if (Layered)
        {
            Console.WriteLine("BIO_METRICS " + JsonSerializer.Serialize(new
            {
                cpu = _cpu,
                tls = Tls,
                inputBioCopyBytes = Metrics.InputCopied,
                outputBioCopyBytes = Metrics.OutputCopied,
                ciphertextPagesReceived = Metrics.CipherReceived,
                ciphertextPagesReturned = Metrics.CipherReturned,
                partialCiphertextSends = Metrics.PartialCipherSends,
                peerAborts = Metrics.PeerAborts,
                tlsErrors = Metrics.TlsErrors,
                memoryBioStagingBytes = 0
            }));
            Console.WriteLine("BIO_FINAL_SEND_METRICS " + JsonSerializer.Serialize(new
            {
                shutdowns = Metrics.FinalShutdowns,
                closeNotify = Metrics.CloseNotify,
                shutdownFailures = Metrics.TlsShutdownFailures,
                corks = Metrics.Corks,
                shutdownNotConnected = Metrics.NotConnected
            }));
        }
        else
        {
            Console.WriteLine("FINAL_SEND_METRICS " + JsonSerializer.Serialize(new
            {
                shutdowns = Metrics.FinalShutdowns,
                partialSends = Metrics.PartialSends,
                links = Metrics.Links,
                cancelledLinks = Metrics.CanceledLinks,
                shutdownErrors = Metrics.ShutdownErrors,
                shutdownAfterClose = Metrics.ShutdownAfterClose,
                shutdownNotConnected = Metrics.NotConnected,
                tlsFinalShutdowns = Metrics.TlsFinalShutdowns,
                tlsCloseNotify = Metrics.CloseNotify,
                tlsShutdownFailures = Metrics.TlsShutdownFailures,
                tlsCorks = Metrics.Corks
            }));
        }
    }
    internal sealed class Counters
    {
        internal long AcceptSqes, AcceptFailures, ReceiveSqes, SendSqes, PollSqes, Cqes, ReturnedPages, SslReads, ReadSuccess, WantRead;
        internal long Handshakes, KtlsRx, KtlsTx, KtlsRejected, PageDrives, PageSkips, TlsWriteRetries;
        internal long FinalShutdowns, PartialSends, Links, CanceledLinks, ShutdownErrors, ShutdownAfterClose, NotConnected;
        internal long TlsFinalShutdowns, CloseNotify, TlsShutdownFailures, Corks;
        internal long CipherReceived, CipherReturned, PartialCipherSends, PeerAborts, TlsErrors;
        internal ulong InputCopied, OutputCopied;
    }
}
