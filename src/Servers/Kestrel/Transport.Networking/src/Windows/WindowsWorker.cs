// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Rio;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

internal sealed class WindowsWorker
{
    internal enum CommandKind { Accepted, OutputReady, ReturnPage, Close, Forget, Unbind, Stop, DisposalCompleted, HandshakeCompleted }
    internal readonly record struct Command(CommandKind Kind, ConnectionState? State = null,
        IMemoryOwner<byte>? Page = null, SocketHandle? Socket = null, Exception? Error = null);

    private readonly WindowsListener _listener;
    private readonly int _cpu;
    private readonly RioApi? _rio;
    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly Dictionary<nuint, ConnectionState> _connections = new();
    private readonly Dictionary<nint, IoOperation> _operations = new();
    private readonly Dictionary<nint, AcceptRequest> _accepts = new();
    private readonly Queue<(nint Operation, int Bytes)> _immediate = new();
    private readonly HashSet<ConnectionState> _poolWaiters = new();
    private readonly Lock _wakeGate = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _unbound = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _sendBufferSize = int.Parse(Environment.GetEnvironmentVariable("NETWORKPROTO_WINDOWS_SNDBUF") ?? "0", CultureInfo.InvariantCulture);
    private PortHandle? _port;
    private NativeAllocation? _rioNotification;
    private RioApi.CompletionQueueHandle? _completionQueue;
    private bool _notifyArmed, _stopping, _disposed;
    private int _wakeScheduled, _threadId, _pendingIo, _pendingDisposals;
    private nuint _nextId = 16;
    private Exception? _failure;
    private long _stopStarted;
    private int _pendingHandshakes;

    internal int Index { get; }
    internal string Backend => IsRio ? "rio" : "iocp";
    internal bool IsRio => _rio is not null;
    internal bool Shared => _listener.Shared;
    internal bool TlsTiming { get; } = Environment.GetEnvironmentVariable("NETWORKPROTO_TLS_TIMING") == "1";
    internal Schannel.Credential? TlsCredential => _listener.TlsCredential;
    internal HandshakeScheduler? Handshakes => _listener.Handshakes;
    internal bool IsCurrentThread => Environment.CurrentManagedThreadId == _threadId;
    internal bool ZeroByteReads { get; } = Environment.GetEnvironmentVariable("NETWORKPROTO_IOCP_ZERO_BYTE") != "0";
    internal NativeMemoryPool InputPool { get; private set; } = null!;
    internal NativeMemoryPool OutputPool { get; private set; } = null!;
    internal ILogger Logger { get; }
    internal Counters Metrics { get; } = new();
    internal Task Started => _started.Task;
    internal Task Stopped => _stopped.Task;
    internal Task Unbound => _unbound.Task;

    internal WindowsWorker(WindowsListener listener, int index, int cpu, RioApi? rio, ILogger logger)
    {
        _listener = listener;
        Index = index;
        _cpu = cpu;
        _rio = rio;
        Logger = logger;
        ArgumentOutOfRangeException.ThrowIfNegative(_sendBufferSize);
    }

    internal void Start()
    {
        using (ExecutionContext.SuppressFlow())
        {
            new Thread(Run) { IsBackground = true, Name = $"NetworkProto {Backend} worker {Index}" }.Start();
        }
    }

    internal void Enqueue(Command command)
    {
        if (command.Kind == CommandKind.OutputReady)
        {
            command.State!.OutputQueued();
        }
        _commands.Enqueue(command);
        if (Environment.CurrentManagedThreadId == _threadId || Interlocked.Exchange(ref _wakeScheduled, 1) != 0)
        {
            return;
        }
        lock (_wakeGate)
        {
            if (!_disposed && _port is not null)
            {
                Native.Wake(_port);
                Interlocked.Increment(ref Metrics.Wakes);
            }
        }
    }

    private unsafe void Run()
    {
        try
        {
            _threadId = Environment.CurrentManagedThreadId;
            Native.PinThread(_cpu);
            _port = Native.CreatePort();
            InputPool = new NativeMemoryPool(_rio, 32 * 1024 * 1024);
            OutputPool = new NativeMemoryPool(_rio, 128 * 1024 * 1024);
            if (_rio is not null)
            {
                _rioNotification = new NativeAllocation(sizeof(Native.Overlapped), zero: true);
                _completionQueue = _rio.CreateCompletionQueue(_port, _rioNotification, 8192);
                ArmNotification();
            }
            if (Index == 0)
            {
                StartAccepts();
            }
            _started.TrySetResult();
            var completions = stackalloc Native.Completion[128];
            var rioResults = stackalloc RioApi.Result[128];
            long nextSweep = 0;
            while (!_stopping || _connections.Count != 0 || _pendingIo != 0 || !_commands.IsEmpty || Volatile.Read(ref _pendingDisposals) != 0)
            {
                Volatile.Write(ref _wakeScheduled, 0);
                DrainCommands(1024);
                for (var i = 0; i < 1024 && _immediate.TryDequeue(out var completed); i++)
                {
                    Complete(completed.Operation, completed.Bytes, 0);
                    DrainCommands(16);
                }
                if (_rio is not null)
                {
                    DrainRio(rioResults);
                    ArmNotification();
                }
                if (_stopping && _pendingIo == 0 && _connections.Count == 0 && _commands.IsEmpty && Volatile.Read(ref _pendingDisposals) == 0)
                {
                    break;
                }
                Metrics.Polls++;
                var count = Native.Poll(_port, completions, 128, _commands.IsEmpty && _immediate.Count == 0 ? 100u : 0u);
                for (var i = 0; i < count; i++)
                {
                    var completion = completions[i];
                    if (completion.Key == Native.WakeKey)
                    {
                        continue;
                    }
                    if (completion.Key == Native.RioKey)
                    {
                        _notifyArmed = false;
                        Metrics.RioNotifications++;
                        continue;
                    }
                    Complete((nint)completion.Operation, checked((int)completion.Bytes), Native.CompletionError(completion.Status));
                    ServiceLocalWork();
                }
                var now = Environment.TickCount64;
                if (now >= nextSweep)
                {
                    nextSweep = now + 100;
                    foreach (var state in _connections.Values.ToArray())
                    {
                        state.CheckDeadline(now);
                    }
                }
                if (_stopping && Environment.TickCount64 - _stopStarted > 15000 && (_pendingIo != 0 || _connections.Count != 0 || _pendingHandshakes != 0))
                {
                    Environment.FailFast("Windows transport shutdown did not drain native ownership; refusing to release live buffers.");
                }
            }
        }
        catch (Exception error)
        {
            _failure = error;
            Logger.LogError(error, "{Backend} worker {Worker} failed.", Backend, Index);
            _started.TrySetException(error);
            _listener.Failed(error);
            if (_pendingIo != 0 || _pendingHandshakes != 0)
            {
                Environment.FailFast("Windows completion worker failed while native I/O still owns memory.", error);
            }
        }
        finally
        {
            lock (_wakeGate)
            {
                _disposed = true;
                _completionQueue?.Dispose();
                _port?.Dispose();
                _rioNotification?.Dispose();
            }
            try
            {
                foreach (var accept in _accepts.Values)
                {
                    accept.Dispose();
                }
                InputPool?.Dispose();
                OutputPool?.Dispose();
            }
            catch (Exception cleanupError)
            {
                _failure ??= cleanupError;
                Logger.LogError(cleanupError, "{Backend} ownership cleanup failed.", Backend);
            }
            _unbound.TrySetResult();
            Console.WriteLine("WINDOWS_TRANSPORT " + JsonSerializer.Serialize(new
            {
                backend = Backend, shared = Shared, nativeTls = TlsCredential is not null, worker = Index, cpu = _cpu,
                accepted = Metrics.Accepted, closed = Metrics.Closed, rejected = Metrics.Rejected,
                polls = Metrics.Polls, wakes = Metrics.Wakes, completions = Metrics.Completions,
                probes = Metrics.Probes, receives = Metrics.Receives, sends = Metrics.Sends,
                synchronousCompletions = Metrics.SynchronousCompletions,
                receivedBytes = Metrics.ReceivedBytes, sentBytes = Metrics.SentBytes,
                sendAdapterCopyBytes = Metrics.SendAdapterCopyBytes,
                partialSends = Metrics.PartialSends, backpressure = Metrics.ReceiveBackpressure,
                maxLeasedPages = Metrics.MaxLeasedPages, rioNotifications = Metrics.RioNotifications,
                connectionErrors = Metrics.ConnectionErrors, pendingIo = _pendingIo,
                firstConnectionError = Metrics.FirstConnectionError,
                tlsHandshakes = Metrics.TlsHandshakes, tlsEncryptCalls = Metrics.TlsEncryptCalls, tlsDecryptCalls = Metrics.TlsDecryptCalls,
                tlsPlaintextBytes = Metrics.TlsPlaintextBytes, tlsReceiveCopyBytes = Metrics.TlsReceiveCopyBytes,
                tlsSendCopyBytes = Metrics.TlsSendCopyBytes, tlsHandshakeCopyBytes = Metrics.TlsHandshakeCopyBytes,
                tlsIncompleteRecords = Metrics.TlsIncompleteRecords, tlsCloseNotify = Metrics.TlsCloseNotify,
                tlsSendBufferRentals = Metrics.TlsSendBufferRentals,
                tlsFinalSendHints = Metrics.TlsFinalSendHints,
                tlsTiming = TlsTiming,
                tlsHandshakeToPlainTicks = Metrics.TlsHandshakeToPlainTicks, tlsTimedPlaintext = Metrics.TlsTimedPlaintext,
                tlsApplicationTicks = Metrics.TlsApplicationTicks, tlsTimedApplication = Metrics.TlsTimedApplication,
                tlsEncryptTicks = Metrics.TlsEncryptTicks, tlsSendTicks = Metrics.TlsSendTicks, tlsTimedSend = Metrics.TlsTimedSend,
                tlsCloseTicks = Metrics.TlsCloseTicks, tlsTimedClose = Metrics.TlsTimedClose,
                tlsOutputQueueTicks = Metrics.TlsOutputQueueTicks, tlsOutputQueueRuns = Metrics.TlsOutputQueueRuns,
                tlsOutputQueueMaxTicks = Metrics.TlsOutputQueueMaxTicks,
                tlsHandshakeTicks = Metrics.TlsHandshakeTicks, tlsHandshakeCalls = Metrics.TlsHandshakeCalls,
                tlsInfoTicks = Metrics.TlsInfoTicks,
                handshakeOffload = Handshakes is not null, pendingHandshakes = _pendingHandshakes,
                offloadedHandshakes = Metrics.OffloadedHandshakes, cancelledHandshakeWork = Metrics.CancelledHandshakeWork,
                cancelledRunningHandshake = Metrics.CancelledRunningHandshake, tlsHandshakeQueueTicks = Metrics.TlsHandshakeQueueTicks,
                timestampFrequency = System.Diagnostics.Stopwatch.Frequency,
                gracefulTimeouts = Metrics.GracefulTimeouts,
                connections = _connections.Count, inputLeases = InputPool?.Outstanding ?? 0,
                outputLeases = OutputPool?.Outstanding ?? 0,
                inputPoolBytes = InputPool?.AllocatedBytes ?? 0, outputPoolBytes = OutputPool?.AllocatedBytes ?? 0
            }));
            if (_failure is null)
            {
                _stopped.TrySetResult();
            }
            else
            {
                _stopped.TrySetException(_failure);
            }
        }
    }

    private void DrainCommands(int budget)
    {
        for (var index = 0; index < budget && _commands.TryDequeue(out var command); index++)
        {
            Process(command);
        }
    }

    private void ServiceLocalWork()
    {
        DrainCommands(16);
        if (_immediate.TryDequeue(out var completion))
        {
            Complete(completion.Operation, completion.Bytes, 0);
        }
    }

    private void Process(Command command)
    {
        switch (command.Kind)
        {
            case CommandKind.Accepted:
                Adopt(command.Socket!);
                break;
            case CommandKind.OutputReady:
                command.State!.OutputDequeued();
                command.State!.Application.PumpOutput();
                break;
            case CommandKind.ReturnPage:
                command.State!.ReturnPage(command.Page!);
                break;
            case CommandKind.Close:
                command.State!.Close(command.Error, abortive: true);
                break;
            case CommandKind.Forget:
                command.State!.Forget();
                break;
            case CommandKind.Unbind:
                StopAccepts();
                break;
            case CommandKind.Stop:
                if (_stopping)
                {
                    break;
                }
                StopAccepts();
                _stopping = true;
                _stopStarted = Environment.TickCount64;
                foreach (var state in _connections.Values.ToArray())
                {
                    state.Close(new ConnectionAbortedException("The Windows listener is stopping."), abortive: true);
                    _ = DisposeRejected(state.Application);
                }
                break;
            case CommandKind.DisposalCompleted:
                break;
            case CommandKind.HandshakeCompleted:
                command.State!.HandshakeCompleted();
                break;
        }
    }

    private unsafe void StartAccepts()
    {
        Native.Associate(_listener.Socket, _port!, Native.AcceptKey);
        var function = Native.GetFunction(_listener.Socket, new Guid("b5367df1-cbac-11cf-95ca-00805f48a192"));
        for (var i = 0; i < 32; i++)
        {
            var request = new AcceptRequest(function);
            _accepts.Add((nint)request.Operation.Pointer, request);
            _operations.Add((nint)request.Operation.Pointer, request.Operation);
            SubmitAccept(request);
        }
    }

    private unsafe void SubmitAccept(AcceptRequest request)
    {
        request.Socket = Native.CreateSocket(IsRio);
        request.Operation.Reset();
        uint bytes = 0;
        var function = (delegate* unmanaged[Stdcall]<nuint, nuint, void*, uint, uint, uint, uint*, Native.Overlapped*, int>)request.Function;
        var result = function((nuint)_listener.Socket.DangerousGetHandle(), (nuint)request.Socket.DangerousGetHandle(),
            (void*)request.Address.DangerousGetHandle(), 0, 32, 32, &bytes, request.Operation.Pointer);
        if (result == 0 && Native.WSAGetLastError() != Native.IoPending)
        {
            throw Native.SocketError("AcceptEx");
        }
        request.Operation.Outstanding = true;
        _pendingIo++;
    }

    private void StopAccepts()
    {
        if (Index != 0 || _listener.Socket.IsClosed)
        {
            _unbound.TrySetResult();
            return;
        }
        Native.Cancel(_listener.Socket);
        _listener.Socket.Dispose();
        if (_accepts.Values.All(request => !request.Operation.Outstanding))
        {
            _unbound.TrySetResult();
        }
    }

    private unsafe void Complete(nint operationPointer, int bytes, int error)
    {
        if (!_operations.TryGetValue(operationPointer, out var operation) || !operation.Outstanding)
        {
            throw new InvalidOperationException("Unknown or duplicate native completion.");
        }
        operation.Outstanding = false;
        _pendingIo--;
        Metrics.Completions++;
        if (operation.Kind == IoKind.Accept)
        {
            var request = _accepts[operationPointer];
            var socket = request.Socket!;
            request.Socket = null;
            if (error == 0 && !_listener.Socket.IsClosed)
            {
                try
                {
                    Native.SetAcceptContext(socket, _listener.Socket);
                    if (_sendBufferSize > 0)
                    {
                        Native.SetSendBuffer(socket, _sendBufferSize);
                    }
                    _listener.Dispatch(socket);
                    socket = null!;
                }
                catch (SocketException acceptError)
                {
                    Logger.LogDebug(acceptError, "Accepted peer closed before socket initialization.");
                    Metrics.Rejected++;
                }
            }
            else if (!_listener.Socket.IsClosed)
            {
                Logger.LogDebug("AcceptEx failed with error {Error}.", error);
                Metrics.Rejected++;
            }
            socket?.Dispose();
            if (!_listener.Socket.IsClosed)
            {
                SubmitAccept(request);
            }
            else if (_accepts.Values.All(value => !value.Operation.Outstanding))
            {
                _unbound.TrySetResult();
            }
            return;
        }
        if (operation.Kind == IoKind.Send)
        {
            operation.Connection!.Sent(bytes, error);
        }
        else
        {
            operation.Connection!.Received(operation.Kind, bytes, error);
        }
    }

    private unsafe void Adopt(SocketHandle socket)
    {
        if (_stopping || _connections.Count >= 4096)
        {
            Metrics.Rejected++;
            socket.Dispose();
            return;
        }
        ConnectionState state;
        try
        {
            state = new ConnectionState(this, ++_nextId, socket);
        }
        catch (SocketException error)
        {
            Logger.LogDebug(error, "Peer closed before adoption.");
            Metrics.Rejected++;
            socket.Dispose();
            return;
        }
        _connections.Add(state.Id, state);
        _operations.Add((nint)state.Read.Pointer, state.Read);
        _operations.Add((nint)state.Write.Pointer, state.Write);
        try
        {
            if (_rio is not null)
            {
                state.RequestQueue = _rio.CreateRequestQueue(socket, _completionQueue!.DangerousGetHandle(), state.Id);
            }
            else
            {
                Native.Associate(socket, _port!, state.Id);
                Native.SkipSynchronousNotifications(socket);
            }
            Metrics.Accepted++;
            state.StartRead();
            if (!state.HasTls)
            {
                Publish(state);
            }
        }
        catch (Exception error)
        {
            Logger.LogError(error, "Windows socket adoption failed.");
            Metrics.Rejected++;
            state.Close(error, abortive: true);
        }
    }

    internal void Publish(ConnectionState state)
    {
        if (_stopping || !_listener.Publish(state.Application))
        {
            Metrics.Rejected++;
            state.Close(new ConnectionAbortedException("The accepted-connection queue is closed or full."), abortive: true);
        }
        else
        {
            state.Published = true;
        }
    }

    internal void DisposeUnpublished(IProviderConnection connection) => _ = DisposeRejected(connection);
    internal void HandshakeQueued()
    {
        _pendingHandshakes++;
        Metrics.OffloadedHandshakes++;
    }
    internal void HandshakeFinished()
    {
        if (--_pendingHandshakes < 0)
        {
            throw new InvalidOperationException("Native TLS handshake ownership underflow.");
        }
    }

    internal unsafe void SubmitRead(ConnectionState state, void* data, int length, bool probe, Memory<byte> memory = default)
    {
        var operation = state.Read;
        operation.Kind = probe ? IoKind.Probe : IoKind.Receive;
        operation.Reset();
        var synchronous = false;
        uint bytes = 0;
        if (_rio is not null)
        {
            _rio.Receive(state.RequestQueue, NativeMemoryPool.RegisteredBuffer(memory), (nint)operation.Pointer);
        }
        else
        {
            var buffer = new Native.SocketBuffer { Data = (byte*)data, Length = checked((uint)length) };
            uint flags = 0;
            var result = Native.WSARecv(state.Socket, &buffer, 1, &bytes, &flags, operation.Pointer, 0);
            if (result == -1
                && Native.WSAGetLastError() != Native.IoPending)
            {
                throw Native.SocketError("WSARecv");
            }
            synchronous = result == 0;
        }
        operation.Outstanding = true;
        _pendingIo++;
        if (synchronous)
        {
            Metrics.SynchronousCompletions++;
            _immediate.Enqueue(((nint)operation.Pointer, checked((int)bytes)));
        }
        if (probe)
        {
            Metrics.Probes++;
        }
        else
        {
            Metrics.Receives++;
        }
    }

    internal unsafe void SubmitSend(ConnectionState state, ReadOnlyMemory<byte> memory, nint pinnedAddress = 0)
    {
        var operation = state.Write;
        operation.Reset();
        var synchronous = false;
        uint bytes = 0;
        if (_rio is not null)
        {
            if (!state.HasTls && Logger.IsEnabled(LogLevel.Trace))
            {
                using var pin = memory.Pin();
                NativeSendTrace.Write(Logger, (nint)pin.Pointer, memory.Length);
            }
            _rio.Send(state.RequestQueue, NativeMemoryPool.RegisteredBuffer(memory), (nint)operation.Pointer);
        }
        else
        {
            using var pin = pinnedAddress == 0 ? memory.Pin() : default;
            var buffer = new Native.SocketBuffer { Data = pinnedAddress == 0 ? (byte*)pin.Pointer : (byte*)pinnedAddress, Length = checked((uint)memory.Length) };
            var result = Native.WSASend(state.Socket, &buffer, 1, &bytes, 0, operation.Pointer, 0);
            if (result == -1
                && Native.WSAGetLastError() != Native.IoPending)
            {
                throw Native.SocketError("WSASend");
            }
            if (!state.HasTls)
            {
                NativeSendTrace.Write(Logger, (nint)buffer.Data, memory.Length);
            }
            synchronous = result == 0;
        }
        operation.Outstanding = true;
        _pendingIo++;
        if (synchronous)
        {
            Metrics.SynchronousCompletions++;
            _immediate.Enqueue(((nint)operation.Pointer, checked((int)bytes)));
        }
        Metrics.Sends++;
    }

    private unsafe void DrainRio(RioApi.Result* results)
    {
        for (var batch = 0; batch < 8; batch++)
        {
            var count = _rio!.Dequeue(_completionQueue!.DangerousGetHandle(), results, 128);
            for (var i = 0; i < count; i++)
            {
                var result = results[i];
                Complete((nint)result.Request, checked((int)result.Bytes), result.Status);
                ServiceLocalWork();
            }
            if (count < 128)
            {
                return;
            }
        }
    }

    private void ArmNotification()
    {
        if (!_notifyArmed)
        {
            _rio!.Notify(_completionQueue!.DangerousGetHandle());
            _notifyArmed = true;
        }
    }

    internal void WaitForPool(ConnectionState state) => _poolWaiters.Add(state);

    internal void ResumePoolWaiters()
    {
        if (_poolWaiters.Count == 0)
        {
            return;
        }
        var waiting = _poolWaiters.ToArray();
        _poolWaiters.Clear();
        foreach (var state in waiting)
        {
            state.StartRead();
        }
    }

    internal unsafe void Forget(ConnectionState state)
    {
        _poolWaiters.Remove(state);
        _operations.Remove((nint)state.Read.Pointer);
        _operations.Remove((nint)state.Write.Pointer);
        if (!_connections.Remove(state.Id))
        {
            throw new InvalidOperationException("Connection ownership was forgotten twice.");
        }
        state.Dispose();
        Metrics.Closed++;
    }

    private async Task DisposeRejected(IProviderConnection connection)
    {
        Interlocked.Increment(ref _pendingDisposals);
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref _failure, error, null);
            Logger.LogError(error, "Unpublished Windows connection disposal failed.");
            _listener.Failed(error);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingDisposals);
            Enqueue(new Command(CommandKind.DisposalCompleted));
        }
    }

    private sealed class AcceptRequest(nint function) : IDisposable
    {
        internal nint Function { get; } = function;
        internal IoOperation Operation { get; } = new(null, IoKind.Accept);
        internal NativeAllocation Address { get; } = new(64);
        internal SocketHandle? Socket { get; set; }
        public void Dispose()
        {
            Operation.Dispose();
            Socket?.Dispose();
            Address.Dispose();
        }
    }

    internal sealed class Counters
    {
        internal long Accepted, Closed, Rejected, Polls, Wakes, Completions, Probes, Receives, Sends;
        internal long ReceivedBytes, SentBytes, PartialSends, ReceiveBackpressure, RioNotifications, ConnectionErrors, SendAdapterCopyBytes;
        internal long SynchronousCompletions;
        internal long GracefulTimeouts;
        internal long TlsHandshakes, TlsEncryptCalls, TlsDecryptCalls, TlsPlaintextBytes;
        internal long TlsReceiveCopyBytes, TlsSendCopyBytes, TlsHandshakeCopyBytes, TlsIncompleteRecords, TlsCloseNotify;
        internal long TlsSendBufferRentals;
        internal long TlsFinalSendHints;
        internal long TlsHandshakeToPlainTicks, TlsApplicationTicks, TlsEncryptTicks, TlsSendTicks, TlsCloseTicks;
        internal long TlsTimedPlaintext, TlsTimedApplication, TlsTimedSend, TlsTimedClose;
        internal long TlsOutputQueueTicks, TlsOutputQueueRuns, TlsOutputQueueMaxTicks;
        internal long TlsHandshakeTicks, TlsHandshakeCalls, TlsInfoTicks;
        internal long OffloadedHandshakes, CancelledHandshakeWork, CancelledRunningHandshake, TlsHandshakeQueueTicks;
        internal string? FirstConnectionError;
        internal int MaxLeasedPages;
    }
}
