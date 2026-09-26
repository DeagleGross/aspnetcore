// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUring;

internal sealed partial class IoUringEngine : IAsyncDisposable
{
    private readonly RingHandle _ring;
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly Dictionary<ulong, PendingOperation> _pending = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopping;
    private ulong _nextId = 2;
    private int _cancellations;
    private readonly bool _synchronousSend = ReadSwitch("NETWORKPROTO_SYNC_SEND", defaultValue: false);
    internal static bool SynchronousAcceptEnabled { get; } = ReadSwitch("NETWORKPROTO_SYNC_ACCEPT", defaultValue: false);
    internal bool ShutdownOnClose { get; } = ReadSwitch("NETWORKPROTO_SHUTDOWN_CLOSE", defaultValue: true);
    internal bool DiagnosticsEnabled { get; } = ReadSwitch("NETWORKPROTO_DIAGNOSTICS", defaultValue: false);
    private readonly bool _reuseOperations = ReadSwitch("NETWORKPROTO_REUSE_OPERATIONS", defaultValue: true);
    private readonly bool _inlineReceive = ReadSwitch("NETWORKPROTO_INLINE_RECEIVE", defaultValue: true);
    internal bool CompactConnections { get; } = ReadSwitch("NETWORKPROTO_COMPACT_CONNECTIONS", defaultValue: true);
    private readonly bool _directSubmissions = ReadSwitch("NETWORKPROTO_DIRECT_SQ", defaultValue: false);
    private readonly bool _stageOnPump = ReadSwitch("NETWORKPROTO_STAGE_ON_PUMP", defaultValue: true);
    private readonly bool _batchSubmissions = ReadSwitch("NETWORKPROTO_BATCH_SUBMISSIONS", defaultValue: false);
    private readonly bool _combinedWait = ReadSwitch("NETWORKPROTO_COMBINED_WAIT", defaultValue: false);
    private readonly bool _deferredTaskrun = ReadSwitch("NETWORKPROTO_DEFER_TASKRUN", defaultValue: true);
    internal string AcceptMode { get; } = Environment.GetEnvironmentVariable("NETWORKPROTO_ACCEPT_MODE") ?? "queued";
    internal int AcceptPauseAt { get; }
    private long _accepts;
    private long _receives;
    private long _sends;
    private long _waits;
    private long _completions;
    private long _wakes;
    private long _directAttempts;
    private long _directCompletions;
    private long _directPartial;
    private long _directWouldBlock;
    private long _directAcceptAttempts;
    private long _directAcceptCompletions;
    private long _cancellationRequests;
    private long _reusableCreated;
    private long _reusableSubmissions;

    private static bool ReadSwitch(string name, bool defaultValue) => Environment.GetEnvironmentVariable(name) switch
    {
        null => defaultValue,
        "0" => false,
        "1" => true,
        _ => throw new ArgumentException($"{name} must be 0 or 1.")
    };

    public IoUringEngine()
    {
        if (AcceptMode is not ("oneshot" or "queued" or "multishot"))
        {
            throw new ArgumentException("NETWORKPROTO_ACCEPT_MODE must be oneshot, queued or multishot.");
        }
        if (AcceptMode != "oneshot" && (_directSubmissions || SynchronousAcceptEnabled))
        {
            throw new ArgumentException("Queued accept modes require NETWORKPROTO_DIRECT_SQ=0 and NETWORKPROTO_SYNC_ACCEPT=0.");
        }
        var pause = Environment.GetEnvironmentVariable("NETWORKPROTO_ACCEPT_PAUSE_AT") ?? "256";
        if (!int.TryParse(pause, out var pauseAt) || pauseAt < 1 || pauseAt > 512)
        {
            throw new ArgumentException("NETWORKPROTO_ACCEPT_PAUSE_AT must be from 1 through 512.");
        }
        AcceptPauseAt = pauseAt;
        if (ReadSwitch("NETWORKPROTO_SYNC_RECEIVE", defaultValue: false))
        {
            throw new ArgumentException("Direct receive was removed. NETWORKPROTO_SYNC_RECEIVE must be unset or 0; all receives use io_uring.");
        }
        if ((_batchSubmissions || _combinedWait) && (!_directSubmissions || !_stageOnPump))
        {
            throw new ArgumentException("Batched submissions and combined wait require NETWORKPROTO_DIRECT_SQ=1 and NETWORKPROTO_STAGE_ON_PUMP=1.");
        }
        if (_directSubmissions && !_reuseOperations)
        {
            throw new ArgumentException("NETWORKPROTO_DIRECT_SQ requires NETWORKPROTO_REUSE_OPERATIONS=1.");
        }
        if (_inlineReceive && !_reuseOperations)
        {
            throw new ArgumentException("NETWORKPROTO_INLINE_RECEIVE requires NETWORKPROTO_REUSE_OPERATIONS=1.");
        }
        var entries = Environment.GetEnvironmentVariable("NETWORKPROTO_RING_ENTRIES");
        if (entries is not null && (!uint.TryParse(entries, out var size) || size < 2 || size > 4096 || (size & (size - 1)) != 0))
        {
            throw new ArgumentException("NETWORKPROTO_RING_ENTRIES must be a power of two from 2 through 4096.");
        }
        _ringEntries = entries is null ? 1024 : uint.Parse(entries, System.Globalization.CultureInfo.InvariantCulture);
        _ring = Native.CreateConfigured(_ringEntries, _deferredTaskrun ? 1 : 0, out var error);
        if (_ring.IsInvalid)
        {
            _ring.Dispose();
            throw new Win32Exception(error, "io_uring setup failed; there is no sockets fallback.");
        }
        new Thread(_directSubmissions ? PumpDirect : Pump) { IsBackground = true, Name = "NetworkProto io_uring" }.Start();
    }

    public Task<int> ExecuteAsync(int kind, Socket socket, Memory<byte> memory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var operation = new Operation(kind, socket, memory);
            var wake = _commands.IsEmpty;
            _commands.Enqueue(() =>
            {
                if (DiagnosticsEnabled)
                {
                    switch (kind)
                    {
                        case 0: _accepts++; break;
                        case 1: _receives++; break;
                        case 2: _sends++; break;
                    }
                }
                var id = _nextId++;
                _pending.Add(id, operation);
                operation.Register(cancellationToken, () => EnqueueCancel(id));
                unsafe
                {
                    var result = Native.Enqueue(_ring, kind, operation.FileDescriptor, operation.Pointer, (uint)memory.Length, id);
                    if (result < 0)
                    {
                        _pending.Remove(id);
                        operation.Complete(result);
                    }
                }
            });
            if (wake)
            {
                Wake();
            }
            return operation.Completion.Task;
        }
    }

    private ValueTask<int> ExecuteAsync(int kind, Socket socket, Memory<byte> memory, CancellationToken cancellationToken, ref ReusableOperation? operation)
    {
        if (_reuseOperations)
        {
            operation ??= new ReusableOperation(this, kind, socket);
            return operation.Start(memory, cancellationToken);
        }
        return new ValueTask<int>(ExecuteAsync(kind, socket, memory, cancellationToken));
    }

    public ValueTask<int> AcceptAsync(Socket socket, CancellationToken cancellationToken, ref ReusableOperation? operation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SynchronousAcceptEnabled)
        {
            if (DiagnosticsEnabled)
            {
                Interlocked.Increment(ref _directAcceptAttempts);
            }
            var result = Native.TryAccept(socket.SafeHandle);
            if (result >= 0)
            {
                if (DiagnosticsEnabled)
                {
                    Interlocked.Increment(ref _directAcceptCompletions);
                }
                return ValueTask.FromResult(result);
            }
            if (result != -11)
            {
                return ValueTask.FromException<int>(CreateIoException(0, result));
            }
        }
        return ExecuteAsync(0, socket, Memory<byte>.Empty, cancellationToken, ref operation);
    }

    public ValueTask<int> ReceiveAsync(Socket socket, Memory<byte> memory, CancellationToken cancellationToken, ref ReusableOperation? operation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteAsync(1, socket, memory, cancellationToken, ref operation);
    }

    public unsafe ValueTask<int> SendAsync(Socket socket, Memory<byte> memory, CancellationToken cancellationToken, ref ReusableOperation? operation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_synchronousSend && memory.Length < 16 * 1024)
        {
            if (DiagnosticsEnabled)
            {
                Interlocked.Increment(ref _directAttempts);
            }
            int result;
            fixed (byte* buffer = memory.Span)
            {
                // SafeHandle marshalling holds the fd alive for this nonblocking call.
                result = Native.TrySend(socket.SafeHandle, buffer, (uint)memory.Length);
            }
            if (result >= 0)
            {
                if (DiagnosticsEnabled)
                {
                    Interlocked.Increment(ref _directCompletions);
                    if (result < memory.Length)
                    {
                        Interlocked.Increment(ref _directPartial);
                    }
                }
                return ValueTask.FromResult(result);
            }
            if (result != -11) // EAGAIN/EWOULDBLOCK: retain the existing asynchronous path.
            {
                return ValueTask.FromException<int>(CreateIoException(2, result));
            }
            if (DiagnosticsEnabled)
            {
                Interlocked.Increment(ref _directWouldBlock);
            }
        }
        return ExecuteAsync(2, socket, memory, cancellationToken, ref operation);
    }

    internal string GetDiagnostics() => JsonSerializer.Serialize(new
    {
        synchronousSend = _synchronousSend,
        synchronousAccept = SynchronousAcceptEnabled,
        synchronousReceive = false,
        acceptMode = AcceptMode,
        deferredTaskrun = _deferredTaskrun,
        acceptResults = _acceptResults,
        acceptMore = _acceptMore,
        acceptTerminals = _acceptTerminals,
        acceptPauses = _acceptPauses,
        acceptResumes = _acceptResumes,
        acceptQueueDepth = _acceptQueueDepth,
        acceptQueueHighWater = _acceptQueueHighWater,
        acceptQueueDisposed = _acceptQueueDisposed,
        acceptQueueOverflows = _acceptQueueOverflows,
        shutdownOnClose = ShutdownOnClose,
        reuseOperations = _reuseOperations,
        inlineReceive = _inlineReceive,
        compactConnections = CompactConnections,
        directSubmissions = _directSubmissions,
        pendingOperations = _pending.Count,
        queuedActions = _commands.Count,
        cancellationAcknowledgments = _cancellations,
        stageOnPump = _stageOnPump,
        batchSubmissions = _batchSubmissions,
        combinedWait = _combinedWait,
        submissionBatches = _submissionBatches,
        largestSubmissionBatch = _largestSubmissionBatch,
        queuedSubmissions = _queuedSubmissions,
        ringEntries = _ringEntries,
        stagedImmediately = _stagedImmediately,
        overflowEnqueued = _overflowEnqueued,
        overflowHighWater = _overflowHighWater,
        slotCapacity = _slotCount,
        activeOperations = _activeDirect,
        slotsInUse = _slotCount - _freeSlots.Count,
        completedOperations = _completedDirect,
        sendPartial = _sendPartial,
        largestSend = _largestSend,
        accepts = _accepts,
        receives = _receives,
        sends = _sends,
        waits = _waits,
        completions = _completions,
        wakes = _wakes,
        directAttempts = _directAttempts,
        directCompletions = _directCompletions,
        directPartial = _directPartial,
        directWouldBlock = _directWouldBlock,
        directAcceptAttempts = _directAcceptAttempts,
        directAcceptCompletions = _directAcceptCompletions,
        directReceiveAttempts = 0,
        directReceiveCompletions = 0,
        cancellationRequests = _cancellationRequests,
        reusableCreated = _reusableCreated,
        reusableSubmissions = _reusableSubmissions
    });

    private static Exception CreateIoException(int kind, int result) => result == -104
        ? new ConnectionResetException("io_uring: connection reset by peer (ECONNRESET, errno 104).")
        : new IOException($"io_uring operation {kind}: {new Win32Exception(-result).Message} (errno {-result}).");

    private void EnqueueCancel(ulong id)
    {
        lock (_gate)
        {
            if (_stopped.Task.IsCompleted)
            {
                return;
            }
            var wake = _commands.IsEmpty;
            _commands.Enqueue(() =>
            {
                if (_pending.ContainsKey(id))
                {
                    Check(Native.Cancel(_ring, id));
                    _cancellations++;
                    if (DiagnosticsEnabled)
                    {
                        _cancellationRequests++;
                    }
                }
            });
            if (wake)
            {
                Wake();
            }
        }
    }

    private void Wake()
    {
        if (DiagnosticsEnabled)
        {
            _wakes++;
        }
        Check(Native.Wake(_ring));
    }

    private static void Check(int result)
    {
        if (result < 0)
        {
            throw new Win32Exception(-result, "io_uring operation failed.");
        }
    }

    private unsafe void Pump()
    {
        var ids = stackalloc ulong[64];
        var results = stackalloc int[64];
        var flags = stackalloc uint[64];
        try
        {
            Check(Native.Enable(_ring));
            while (true)
            {
                while (true)
                {
                    Action? command;
                    lock (_gate)
                    {
                        // Serialize dequeue with the empty-to-nonempty wake
                        // decision. Otherwise a producer can suppress a wake
                        // just as the pump removes the previous last command.
                        if (!_commands.TryDequeue(out command))
                        {
                            break;
                        }
                    }
                    command();
                }
                lock (_gate)
                {
                    if (_stopping && _pending.Count == 0 && _cancellations == 0 && _commands.IsEmpty)
                    {
                        _stopped.TrySetResult();
                        break;
                    }
                }
                var count = Native.Wait(_ring, ids, results, 64, flags);
                Check(count);
                if (DiagnosticsEnabled)
                {
                    _waits++;
                    _completions += count;
                }
                for (var index = 0; index < count; index++)
                {
                    var id = ids[index];
                    var result = results[index];
                    if (id == 1)
                    {
                        _cancellations--;
                        if (result is not (0 or -2 or -114)) // completed, ENOENT, or EALREADY
                        {
                            Check(result);
                        }
                    }
                    else if (id >= 2)
                    {
                        var operation = _pending[id];
                        if ((flags[index] & Native.More) == 0)
                        {
                            _pending.Remove(id);
                        }
                        operation.OnCompletion(result, flags[index]);
                    }
                }
            }
        }
        catch (Exception error)
        {
            // This bounded prototype cannot recover from a broken completion
            // ring. Do not free pinned buffers still potentially in kernel use.
            Environment.FailFast("NetworkProto io_uring completion pump failed.", error);
        }
        finally
        {
            _ring.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_directSubmissions)
        {
            await StopDirectAsync().ConfigureAwait(false);
            return;
        }
        lock (_gate)
        {
            if (!_stopping)
            {
                _stopping = true;
                _commands.Enqueue(() =>
                {
                    foreach (var id in _pending.Keys)
                    {
                        Check(Native.Cancel(_ring, id));
                        _cancellations++;
                        if (DiagnosticsEnabled)
                        {
                            _cancellationRequests++;
                        }
                    }
                });
                Wake();
            }
        }
        await _stopped.Task.ConfigureAwait(false);
    }

    internal abstract class PendingOperation
    {
        public abstract void Complete(int result);

        public virtual void OnCompletion(int result, uint flags)
        {
            if ((flags & Native.More) != 0)
            {
                throw new InvalidOperationException("A one-shot operation received a nonterminal CQE.");
            }
            Complete(result);
        }
    }

    private sealed class Operation : PendingOperation
    {
        private readonly SafeSocketHandle _socket;
        private MemoryHandle _pin;
        private CancellationTokenRegistration _registration;
        private readonly int _kind;
        public TaskCompletionSource<int> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FileDescriptor => _socket.DangerousGetHandle().ToInt32();
        public unsafe void* Pointer => _pin.Pointer;

        public Operation(int kind, Socket socket, Memory<byte> memory)
        {
            _kind = kind;
            _socket = socket.SafeHandle;
            var added = false;
            _socket.DangerousAddRef(ref added);
            try
            {
                _pin = memory.Pin();
            }
            catch
            {
                _socket.DangerousRelease();
                throw;
            }
        }

        public void Register(CancellationToken token, Action cancel) => _registration = token.Register(cancel);

        public override void Complete(int result)
        {
            Release();
            if (result == -125)
            {
                Completion.TrySetCanceled();
            }
            else if (result < 0)
            {
                Completion.TrySetException(CreateIoException(_kind, result));
            }
            else
            {
                Completion.TrySetResult(result);
            }
        }

        private void Release()
        {
            _registration.Dispose();
            _pin.Dispose();
            _socket.DangerousRelease();
        }
    }
}
