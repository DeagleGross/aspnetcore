// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

internal sealed class HandshakeScheduler(int workers)
{
    private readonly Lock _gate = new();
    private readonly Queue<Work> _queued = new();
    private readonly int _maximumActive = checked(workers * 2);
    private readonly int _maximumQueued = checked(workers * 128);
    private int _active, _peakActive, _peakQueued;
    private long _submitted, _completed, _rejected;
    private TaskCompletionSource? _drained;

    internal bool TryQueue(Work work)
    {
        lock (_gate)
        {
            if (_active == _maximumActive)
            {
                if (_queued.Count == _maximumQueued)
                {
                    _rejected++;
                    return false;
                }
                _queued.Enqueue(work);
                _peakQueued = Math.Max(_peakQueued, _queued.Count);
                _submitted++;
                return true;
            }
            if (_active == 0)
            {
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _active++;
            _submitted++;
            _peakActive = Math.Max(_peakActive, _active);
        }
        ThreadPool.UnsafeQueueUserWorkItem(work, preferLocal: false);
        return true;
    }

    private Work? ReleaseSlot()
    {
        lock (_gate)
        {
            _completed++;
            if (_queued.TryDequeue(out var next))
            {
                return next;
            }
            _active--;
            if (_active == 0)
            {
                _drained!.TrySetResult();
            }
            return null;
        }
    }

    internal Task DrainAsync()
    {
        lock (_gate)
        {
            return _active == 0 ? Task.CompletedTask : _drained!.Task;
        }
    }

    internal object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                maximumActive = _maximumActive, maximumQueued = _maximumQueued,
                active = _active, queued = _queued.Count, peakActive = _peakActive, peakQueued = _peakQueued,
                submitted = _submitted, completed = _completed, rejected = _rejected
            };
        }
    }

    internal sealed class Work(HandshakeScheduler scheduler, WindowsWorker worker, ConnectionState state,
        Schannel.Credential credential, Schannel.SecurityHandle context, Memory<byte> input) : IThreadPoolWorkItem
    {
        private int _phase;
        private readonly long _queuedAt = worker.TlsTiming ? Stopwatch.GetTimestamp() : 0;
        internal int Status, Extra, OutputLength;
        internal nint Output;
        internal Exception? Error;
        internal long CallTicks, QueueTicks;
        internal bool Called;
        internal int InputLength => input.Length;

        internal bool Cancel()
        {
            return Interlocked.CompareExchange(ref _phase, 3, 0) == 1;
        }

        void IThreadPoolWorkItem.Execute()
        {
            try
            {
                if (Interlocked.CompareExchange(ref _phase, 1, 0) != 0)
                {
                    return;
                }
                if (worker.Logger.IsEnabled(LogLevel.Debug))
                {
                    worker.Logger.LogDebug(new EventId(1001, "NativeHandshakeStarted"),
                        "Native TLS handshake {ConnectionId} started on the bounded handshake executor.", state.Id);
                }
                var started = worker.TlsTiming ? Stopwatch.GetTimestamp() : 0;
                QueueTicks = worker.TlsTiming ? started - _queuedAt : 0;
                Called = true;
                Status = Schannel.Handshake(credential, context, input, out Extra, out Output, out OutputLength);
                CallTicks = worker.TlsTiming ? Stopwatch.GetTimestamp() - started : 0;
            }
            catch (Exception error)
            {
                Error = error;
            }
            finally
            {
                Volatile.Write(ref _phase, 2);
                var next = scheduler.ReleaseSlot();
                try
                {
                    // The owner still holds the context/input. Only this command may release the work hold.
                    worker.Enqueue(new(WindowsWorker.CommandKind.HandshakeCompleted, state));
                }
                finally
                {
                    if (next is not null)
                    {
                        ThreadPool.UnsafeQueueUserWorkItem(next, preferLocal: false);
                    }
                }
            }
        }
    }
}
