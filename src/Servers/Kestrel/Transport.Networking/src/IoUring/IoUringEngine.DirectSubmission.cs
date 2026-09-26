// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUring;

internal sealed partial class IoUringEngine
{
    private OperationSlot?[] _slots = new OperationSlot?[256];
    private int _slotCount;
    private readonly Stack<int> _freeSlots = [];
    private readonly Queue<ReusableOperation> _overflow = new();
    private Queue<ReusableOperation> _submissions = new();
    private readonly Queue<ulong> _cancelIds = new();
    private readonly uint _ringEntries;
    private int _activeDirect;
    private int _pumpThreadId;
    private bool _pumpWaiting;
    private long _stagedImmediately;
    private long _queuedSubmissions;
    private long _submissionBatches;
    private int _largestSubmissionBatch;
    private long _overflowEnqueued;
    private int _overflowHighWater;
    private long _completedDirect;
    private long _sendPartial;
    private uint _largestSend;

    private sealed class OperationSlot
    {
        public ReusableOperation? Operation;
        public uint Generation;
        public bool Active;
    }

    // Called with _gate held. The same lock serializes every SQ producer and flush.
    private ulong Activate(ReusableOperation operation, ref int index)
    {
        if (index < 0)
        {
            if (!_freeSlots.TryPop(out index))
            {
                index = _slotCount++;
                if (index == _slots.Length)
                {
                    var slots = new OperationSlot?[_slots.Length * 2];
                    Array.Copy(_slots, slots, _slots.Length);
                    Volatile.Write(ref _slots, slots);
                }
                _slots[index] = new OperationSlot();
            }
            _slots[index]!.Operation = operation;
        }
        var slot = _slots[index]!;
        if (slot.Active || slot.Generation == uint.MaxValue)
        {
            throw new InvalidOperationException("An io_uring slot is active or exhausted its generation counter.");
        }
        slot.Generation++;
        Volatile.Write(ref slot.Active, true);
        Interlocked.Increment(ref _activeDirect);
        return ((ulong)slot.Generation << 32) | ((uint)index + 2);
    }

    private OperationSlot? FindActive(ulong id)
    {
        var token = (uint)id;
        var slots = Volatile.Read(ref _slots);
        if (token < 2 || token - 2 >= slots.Length)
        {
            return null;
        }
        var slot = slots[(int)(token - 2)];
        return slot is not null && Volatile.Read(ref slot.Active) && slot.Generation == (uint)(id >> 32) ? slot : null;
    }

    private void StageOrQueue(ReusableOperation operation)
    {
        if (_stageOnPump && Environment.CurrentManagedThreadId != _pumpThreadId)
        {
            _submissions.Enqueue(operation);
            if (DiagnosticsEnabled)
            {
                _queuedSubmissions++;
            }
            WakeWaitingPump();
            return;
        }
        // Preserve FIFO once the SQ overflows rather than letting newer producers overtake it.
        if (_overflow.Count == 0 && operation.TryStage())
        {
            if (DiagnosticsEnabled)
            {
                _stagedImmediately++;
            }
        }
        else
        {
            _overflow.Enqueue(operation);
            if (DiagnosticsEnabled)
            {
                _overflowEnqueued++;
                _overflowHighWater = Math.Max(_overflowHighWater, _overflow.Count);
            }
        }
        WakeWaitingPump();
    }

    private void WakeWaitingPump()
    {
        if (_pumpWaiting && Environment.CurrentManagedThreadId != _pumpThreadId)
        {
            _pumpWaiting = false;
            Wake();
        }
    }

    private void CancelDirect(ulong id)
    {
        lock (_gate)
        {
            if (FindActive(id) is null)
            {
                return;
            }
            _cancelIds.Enqueue(id);
            WakeWaitingPump();
        }
    }

    private unsafe void PumpDirect()
    {
        var ids = stackalloc ulong[64];
        var results = stackalloc int[64];
        var submissionBatch = new Queue<ReusableOperation>();
        _pumpThreadId = Environment.CurrentManagedThreadId;
        try
        {
            Check(Native.Enable(_ring));
            while (true)
            {
                bool wait;
                if (_stageOnPump)
                {
                    // Only the pump touches the SQ in this mode. Kernel submission
                    // must not hold the producer lock.
                    if (_batchSubmissions)
                    {
                        lock (_gate)
                        {
                            (_submissions, submissionBatch) = (submissionBatch, _submissions);
                            if (DiagnosticsEnabled && submissionBatch.Count != 0)
                            {
                                _submissionBatches++;
                                _largestSubmissionBatch = Math.Max(_largestSubmissionBatch, submissionBatch.Count);
                            }
                        }
                        // A bounded snapshot keeps arrivals from indefinitely delaying CQ dispatch.
                        while (submissionBatch.TryDequeue(out var queued))
                        {
                            StageQueued(queued);
                        }
                    }
                    else
                    {
                        while (true)
                        {
                            ReusableOperation? queued;
                            lock (_gate)
                            {
                                if (!_submissions.TryDequeue(out queued))
                                {
                                    break;
                                }
                            }
                            StageQueued(queued);
                        }
                    }
                    while (_overflow.TryPeek(out var queued) && queued.TryStage())
                    {
                        _overflow.Dequeue();
                    }
                    lock (_gate)
                    {
                        DrainCancellations();
                        if (CanStopDirect())
                        {
                            break;
                        }
                        wait = _submissions.Count == 0 && _overflow.Count == 0 && _cancelIds.Count == 0;
                        _pumpWaiting = wait;
                    }
                    if (!_combinedWait)
                    {
                        Check(Native.Flush(_ring));
                    }
                }
                else
                {
                    lock (_gate)
                    {
                        while (_overflow.TryPeek(out var queued) && queued.TryStage())
                        {
                            _overflow.Dequeue();
                        }
                        DrainCancellations();
                        if (CanStopDirect())
                        {
                            break;
                        }
                        Check(Native.Flush(_ring));
                        wait = _overflow.Count == 0 && _cancelIds.Count == 0;
                        _pumpWaiting = wait;
                    }
                }

                var count = _combinedWait
                    ? Native.SubmitAndCollect(_ring, ids, results, 64, wait ? 1 : 0)
                    : Native.Collect(_ring, ids, results, 64, wait ? 1 : 0);
                Check(count);
                lock (_gate)
                {
                    _pumpWaiting = false;
                }
                if (DiagnosticsEnabled)
                {
                    _waits++;
                    _completions += count;
                }
                for (var index = 0; index < count; index++)
                {
                    var id = ids[index];
                    if (id == 0)
                    {
                        continue;
                    }
                    var result = results[index];
                    if (id == 1)
                    {
                        _cancellations--;
                        if (result is not (0 or -2 or -114))
                        {
                            Check(result);
                        }
                    }
                    else
                    {
                        // This slot cannot be reused until Complete signals its consumer.
                        var slot = FindActive(id) ?? throw new InvalidOperationException($"CQE {id} has no active owner.");
                        var operation = slot.Operation!;
                        Volatile.Write(ref slot.Active, false);
                        Interlocked.Decrement(ref _activeDirect);
                        if (DiagnosticsEnabled)
                        {
                            _completedDirect++;
                        }
                        // Registration disposal and continuations must never run under _gate.
                        operation.Complete(result);
                    }
                }
            }
        }
        catch (Exception error)
        {
            Environment.FailFast("NetworkProto io_uring completion pump failed.", error);
        }
        finally
        {
            _ring.Dispose();
            _stopped.TrySetResult();
        }
    }

    private void StageQueued(ReusableOperation operation)
    {
        if (!operation.TryStage())
        {
            Check(Native.Flush(_ring));
            if (!operation.TryStage())
            {
                throw new InvalidOperationException("SQ remained full after submission.");
            }
        }
    }

    private bool CanStopDirect() => _stopping && Volatile.Read(ref _activeDirect) == 0 && _cancellations == 0 && _cancelIds.Count == 0;

    private unsafe void DrainCancellations()
    {
        // Cancellation must follow submission, including queued work.
        while (_submissions.Count == 0 && _overflow.Count == 0 && _cancelIds.TryPeek(out var id))
        {
            if (FindActive(id) is not null)
            {
                if (Native.Stage(_ring, 3, 0, null, 0, id) == 0)
                {
                    break;
                }
                _cancellations++;
                if (DiagnosticsEnabled)
                {
                    _cancellationRequests++;
                }
            }
            _cancelIds.Dequeue();
        }
    }

    private Task StopDirectAsync()
    {
        lock (_gate)
        {
            if (!_stopping)
            {
                _stopping = true;
                for (var index = 0; index < _slotCount; index++)
                {
                    var slot = _slots[index]!;
                    if (Volatile.Read(ref slot.Active))
                    {
                        _cancelIds.Enqueue(((ulong)slot.Generation << 32) | ((uint)index + 2));
                    }
                }
                WakeWaitingPump();
            }
        }
        return _stopped.Task;
    }
}
