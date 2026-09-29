// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll;

internal sealed class Engine
{
    internal const int PageCount = 2048, PageSize = 16384;
    private const ulong ListenerId = 1, WakeId = 2;
    internal enum CommandKind { Send, ReturnPage, Close, Forget, Unbind, Stop }
    internal readonly record struct Command(CommandKind Kind, ulong Id = 0, nint Data = 0, int Length = 0, int Page = 0, bool Final = false);

    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly Dictionary<ulong, ConnectionState> _connections = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _wakeGate = new();
    private readonly ChannelWriter<ConnectionContext> _accepted;
    private readonly IPEndPoint _endpoint;
    private readonly int _cpu;
    private readonly byte[] _pages = GC.AllocateUninitializedArray<byte>(PageCount * PageSize, pinned: true);
    private readonly ulong[] _pageOwners = new ulong[PageCount];
    private readonly Stack<int> _freePages = new(Enumerable.Range(0, PageCount));
    private int _epoll = -1, _listener = -1, _wake = -1, _pumpThread, _wakeScheduled;
    private nint _context;
    private bool _disposed, _stopping, _resumePages;
    private ulong _nextId = WakeId;
    private int _pendingRejections;
    private long _iterations, _wakes, _commandsProcessed, _pagesRead, _bytes, _sendCommands, _finalSendCommands;
    private long _rejectedAccepts, _rejectedDisposed;
    private readonly bool _coalesced = Environment.GetEnvironmentVariable("NETWORKPROTO_COALESCE") == "1";
    private readonly int _sendBufferSize = int.Parse(Environment.GetEnvironmentVariable("NETWORKPROTO_EPOLL_SNDBUF") ?? "0", CultureInfo.InvariantCulture);
    internal readonly bool FinalSendEnabled = Environment.GetEnvironmentVariable("NETWORKPROTO_FINAL_SEND") is "1" or "2" or "3";
    internal readonly bool CorkFinal = Environment.GetEnvironmentVariable("NETWORKPROTO_FINAL_SEND") == "3";
    internal readonly Counters Metrics = new();
    internal readonly ILogger Logger;
    internal bool Tls { get; }

    internal Task Started => _started.Task;
    internal Task Stopped => _stopped.Task;

    internal Engine(IPEndPoint endpoint, int cpu, bool tls, string certificate, string key, ChannelWriter<ConnectionContext> accepted, ILogger logger)
    {
        _endpoint = endpoint;
        _cpu = cpu;
        Tls = tls;
        _accepted = accepted;
        Logger = logger;
        using (ExecutionContext.SuppressFlow())
        {
            new Thread(() => Run(certificate, key)) { IsBackground = true, Name = $"Epoll {(tls ? "TLS" : "TCP")} CPU {cpu}" }.Start();
        }
    }

    internal void Enqueue(Command command)
    {
        _commands.Enqueue(command);
        if (_coalesced && (Environment.CurrentManagedThreadId == _pumpThread || Interlocked.Exchange(ref _wakeScheduled, 1) != 0))
        {
            return;
        }
        lock (_wakeGate)
        {
            if (!_disposed && _wake >= 0)
            {
                Interlocked.Increment(ref _wakes);
                Native.Check(Native.Wake(_wake), "wake");
            }
        }
    }

    internal void Stop() => Enqueue(new(CommandKind.Stop));

    private unsafe void Run(string certificate, string key)
    {
        Exception? failure = null;
        try
        {
            _pumpThread = Environment.CurrentManagedThreadId;
            ArgumentOutOfRangeException.ThrowIfNegative(_sendBufferSize);
            Native.Check(Native.SetCpu(_cpu), "affinity");
            if (Tls)
            {
                _context = Native.CreateContext(certificate, key, out var error);
                if (_context == 0)
                {
                    throw new IOException($"OpenSSL context creation failed: 0x{error:x}.");
                }
            }
            _epoll = Native.Check(Native.Create(), "create");
            _wake = Native.Check(Native.CreateWake(), "eventfd");
            _listener = Native.Check(Native.Listen(_endpoint.Port), "listen");
            Control(Native.Add, _wake, Native.Readable, WakeId);
            Control(Native.Add, _listener, Native.Readable, ListenerId);
            if (Tls)
            {
                Console.WriteLine($"EPOLL_TLS_LIBRARY version=\"{Marshal.PtrToStringUTF8(Native.Version())}\" cpu={_cpu} batch=128 managed=true");
            }
            _started.SetResult();
            var events = new Native.ReadyEvent[128];
            long nextSweep = 0;
            while (!_stopping || _connections.Count != 0 || !_commands.IsEmpty || Volatile.Read(ref _pendingRejections) != 0)
            {
                try
                {
                    // A producer either joins this drain or leaves a wake for the next wait.
                    Volatile.Write(ref _wakeScheduled, 0);
                    for (var i = 0; i < 1024 && _commands.TryDequeue(out var command); i++)
                    {
                        _commandsProcessed++;
                        Process(command);
                    }
                    ResumeReaders();
                    fixed (Native.ReadyEvent* pointer = events)
                    {
                        Metrics.WaitCalls++;
                        var count = Native.Check(Native.Wait(_epoll, pointer, events.Length, _commands.IsEmpty ? 10 : 0), "wait");
                        for (var i = 0; i < count; i++)
                        {
                            Dispatch(events[i]);
                        }
                    }
                    _iterations++;
                    var now = Environment.TickCount64;
                    if (Tls && now >= nextSweep)
                    {
                        nextSweep = now + 1000;
                        foreach (var state in _connections.Values.ToArray())
                        {
                            state.CheckDeadline(now);
                        }
                    }
                }
                catch (Exception errorInPump)
                {
                    failure ??= errorInPump;
                    Logger.LogError(errorInPump, "Epoll worker failed; stopping its connections.");
                    _accepted.TryComplete(errorInPump);
                    StopConnections(errorInPump);
                }
            }
        }
        catch (Exception error)
        {
            failure ??= error;
            Logger.LogError(error, "Epoll worker initialization failed.");
            _started.TrySetException(error);
            _accepted.TryComplete(error);
        }
        finally
        {
            lock (_wakeGate)
            {
                _disposed = true;
                CloseDescriptor(ref _listener);
                CloseDescriptor(ref _wake);
                CloseDescriptor(ref _epoll);
            }
            if (_context != 0)
            {
                Native.FreeContext(_context);
            }
            if (_connections.Count != 0 || _freePages.Count != PageCount)
            {
                failure ??= new InvalidOperationException("Epoll stopped with outstanding connection/page ownership.");
                Logger.LogError(failure, "Epoll ownership did not drain.");
            }
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

    private void Dispatch(Native.ReadyEvent ready)
    {
        Metrics.ReadyEvents++;
        if (ready.Id == WakeId)
        {
            Native.Check(Native.DrainWake(_wake), "drain wake");
            Metrics.WakeReads++;
        }
        else if (ready.Id == ListenerId)
        {
            Accept();
        }
        else if (_connections.TryGetValue(ready.Id, out var state))
        {
            var mask = ready.Events;
            if ((mask & (Native.Error | Native.Hangup)) != 0)
            {
                mask |= Native.Readable | Native.Writable;
            }
            state.Drive(mask);
        }
        // A closed/reused fd cannot alias an old event: registrations carry monotonic IDs.
    }

    private void Accept()
    {
        for (var i = 0; i < 64 && _listener >= 0; i++)
        {
            var fd = Native.Accept(_listener);
            if (fd == Native.Again)
            {
                return;
            }
            if (fd is Native.Interrupted or Native.ConnectionAborted)
            {
                continue;
            }
            Native.Check(fd, "accept");
            Metrics.Accepts++;
            nint session = 0;
            try
            {
                Native.Check(Native.SetSocketOption(fd, Native.SocketOption.NoDelay, 1), "TCP_NODELAY");
                if (_sendBufferSize > 0)
                {
                    Native.Check(Native.SetSocketOption(fd, Native.SocketOption.SendBuffer, _sendBufferSize), "SO_SNDBUF");
                }
                if (Tls)
                {
                    session = Native.CreateSession(_context, fd, out var error);
                    if (session == 0)
                    {
                        throw new IOException($"OpenSSL session creation failed: 0x{error:x}.");
                    }
                }
            }
            catch
            {
                if (session != 0)
                {
                    Native.FreeSession(session);
                }
                CloseDescriptor(ref fd);
                Metrics.Closed++;
                throw;
            }
            var id = checked(++_nextId);
            ConnectionState state = Tls
                ? new TlsConnection(this, id, fd, session)
                : new TcpConnection(this, id, fd);
            _connections.Add(state.Id, state);
            state.Drive(Native.Readable | Native.Writable);
        }
    }

    private void Process(Command command)
    {
        if (command.Kind == CommandKind.Unbind)
        {
            StopAccepting();
            return;
        }
        if (command.Kind == CommandKind.Stop)
        {
            StopConnections(null);
            return;
        }
        if (!_connections.TryGetValue(command.Id, out var state))
        {
            if (command.Kind == CommandKind.Close)
            {
                return;
            }
            throw new InvalidOperationException($"Command {command.Kind} targeted a released epoll connection.");
        }
        switch (command.Kind)
        {
            case CommandKind.Send:
                _sendCommands++;
                if (command.Final)
                {
                    _finalSendCommands++;
                }
                state.Send(command.Data, command.Length, command.Final);
                break;
            case CommandKind.ReturnPage:
                state.ReturnPage(command.Page);
                break;
            case CommandKind.Close:
                state.RequestClose();
                break;
            case CommandKind.Forget:
                if (!state.Closed || state.Leased != 0)
                {
                    throw new InvalidOperationException("Epoll connection forgotten before its memory was returned.");
                }
                _connections.Remove(state.Id);
                break;
            default:
                throw new InvalidOperationException($"Unexpected epoll command {command.Kind}.");
        }
    }

    private void StopAccepting()
    {
        if (_listener >= 0)
        {
            Control(Native.Delete, _listener, 0, ListenerId);
            CloseDescriptor(ref _listener);
        }
    }

    private void StopConnections(Exception? error)
    {
        _stopping = true;
        StopAccepting();
        foreach (var state in _connections.Values.ToArray())
        {
            state.Finish(error ?? new ConnectionAbortedException("Epoll worker stopped."));
            state.Application?.Abort(new ConnectionAbortedException("Epoll worker stopped."));
        }
    }

    internal void Control(int operation, int fd, uint events, ulong id)
    {
        Metrics.ControlCalls++;
        Native.Check(Native.Control(_epoll, operation, fd, events, id), "control");
    }

    internal Connection Publish(ConnectionState state)
    {
        var connection = new Connection(this, state.Id, _endpoint);
        if (!_accepted.TryWrite(connection))
        {
            _rejectedAccepts++;
            Interlocked.Increment(ref _pendingRejections);
            connection.Abort(new ConnectionAbortedException("Listener stopped."));
            _ = DisposeRejectedAsync(connection);
        }
        return connection;
    }

    private async Task DisposeRejectedAsync(Connection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            Interlocked.Increment(ref _rejectedDisposed);
        }
        catch (Exception error)
        {
            Logger.LogError(error, "Rejected epoll connection disposal failed.");
            _stopped.TrySetException(error);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingRejections);
        }
    }

    internal void RemoveUnpublished(ulong id) => _connections.Remove(id);

    internal int RentPage(ulong owner)
    {
        if (!_freePages.TryPop(out var page))
        {
            return -1;
        }
        if (_pageOwners[page] != 0)
        {
            throw new InvalidOperationException("Epoll page was already owned.");
        }
        _pageOwners[page] = owner;
        return page;
    }

    internal Memory<byte> Page(int page) => _pages.AsMemory(page * PageSize, PageSize);

    internal void OfferPage(ulong owner, int page)
    {
        if ((uint)page >= PageCount || _pageOwners[page] != owner)
        {
            throw new InvalidOperationException("Epoll page returned by the wrong connection.");
        }
        _pageOwners[page] = 0;
        _resumePages |= _freePages.Count == 0;
        _freePages.Push(page);
    }

    private void ResumeReaders()
    {
        if (!_resumePages)
        {
            return;
        }
        _resumePages = false;
        foreach (var state in _connections.Values.ToArray())
        {
            if (_freePages.Count == 0)
            {
                break;
            }
            state.ResumeForPool();
        }
    }

    internal void Received(Connection application, int page, int length)
    {
        _pagesRead++;
        _bytes += length;
        application.Input.Append(Page(page)[..length], page);
    }

    internal void CloseDescriptor(ref int fd)
    {
        if (fd >= 0)
        {
            var result = Native.Close(fd);
            fd = -1;
            if (result < 0)
            {
                Logger.LogError("Epoll close failed: {Error}", result);
            }
        }
    }

    private void Report()
    {
        Console.WriteLine("OWNED_METRICS " + JsonSerializer.Serialize(new
        {
            cpu = _cpu, tls = Tls, layered = false, epoll = true, managedEngine = true, coalesced = _coalesced,
            pumpIterations = _iterations, stepInterop = 0, wakeInterop = _wakes, commands = _commandsProcessed,
            pages = _pagesRead, bytes = _bytes, acceptSqes = 0, receiveSqes = 0, sendSqes = 0, pollSqes = 0, cqes = 0,
            nativeSubmitWaitCalls = 0, returnedPages = Metrics.ReturnedPages, sslReads = Metrics.SslReads,
            receiveAdapterCopyBytes = 0, rejectedAccepts = _rejectedAccepts, rejectedAcceptsDisposed = _rejectedDisposed,
            sendCommands = _sendCommands, finalSendCommands = _finalSendCommands,
            liveConnections = _connections.Count, leasedPages = PageCount - _freePages.Count
        }));
        Console.WriteLine("EPOLL_METRICS " + JsonSerializer.Serialize(Metrics, new JsonSerializerOptions { IncludeFields = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.WriteLine("EPOLL_FINAL_SEND_METRICS " + JsonSerializer.Serialize(new
        {
            shutdowns = Metrics.FinalShutdowns, closeNotify = Metrics.CloseNotify, shutdownFailures = Metrics.ShutdownFailures,
            corks = Metrics.Corks, shutdownNotConnected = Metrics.NotConnected
        }));
    }

    internal sealed class Counters
    {
        public long Accepts, Handshakes, WaitCalls, ReadyEvents, ControlCalls, WakeReads;
        public long SslReads, ReadSuccess, SslWrites, WriteSuccess, WantRead, WantWrite, WriteRetries, ReturnedPages;
        public long Closed, PeerAborts, TlsErrors, HandshakeTimeouts, ShutdownTimeouts, ReadPauses, PageResumes;
        public long Corks, FinalShutdowns, CloseNotify, ShutdownFailures, NotConnected;
        public long RecvCalls, SendCalls, RecvBytes, SendBytes, ReceiveWouldBlock, SendWouldBlock, PartialSends, ReadEofs, SocketErrors;
    }
}
