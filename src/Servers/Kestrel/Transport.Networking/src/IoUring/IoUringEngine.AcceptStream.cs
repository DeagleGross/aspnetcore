// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUring;

internal sealed partial class IoUringEngine
{
    private long _acceptResults;
    private long _acceptMore;
    private long _acceptTerminals;
    private long _acceptPauses;
    private long _acceptResumes;
    private int _acceptQueueDepth;
    private int _acceptQueueHighWater;
    private long _acceptQueueDisposed;
    private long _acceptQueueOverflows;

    private void EnqueueAcceptAction(Action action)
    {
        lock (_gate)
        {
            if (_stopped.Task.IsCompleted)
            {
                return;
            }
            var wake = _commands.IsEmpty;
            _commands.Enqueue(action);
            if (wake)
            {
                Wake();
            }
        }
    }

    internal sealed class AcceptStream : PendingOperation, IDisposable
    {
        private const int Capacity = 1024;
        private readonly IoUringEngine _engine;
        private readonly SafeSocketHandle _listener;
        private readonly ILogger _logger;
        private readonly CancellationToken _stopToken;
        private readonly CancellationTokenRegistration _registration;
        private readonly bool _multishot;
        private readonly int _pauseAt;
        private readonly Channel<SafeSocketHandle> _accepted = Channel.CreateBounded<SafeSocketHandle>(new BoundedChannelOptions(Capacity)
        {
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        private readonly Action _resume;
        private readonly Action _stop;
        private ulong _id;
        private bool _active;
        private bool _cancelSent;
        private int _stopRequested;
        private int _paused;
        private int _resumeQueued;

        public AcceptStream(IoUringEngine engine, Socket listener, CancellationToken stopToken, ILogger logger)
        {
            _engine = engine;
            _listener = listener.SafeHandle;
            _logger = logger;
            _stopToken = stopToken;
            _multishot = engine.AcceptMode == "multishot";
            _pauseAt = engine.AcceptPauseAt;
            _resume = Resume;
            _stop = StopOnPump;
            _registration = stopToken.UnsafeRegister(static state => ((AcceptStream)state!).RequestStop(), this);
            engine.EnqueueAcceptAction(Submit);
        }

        public async ValueTask<SafeSocketHandle> ReadAsync(CancellationToken cancellationToken)
        {
            var handle = await _accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var remaining = Interlocked.Decrement(ref _engine._acceptQueueDepth);
            if (remaining <= _pauseAt / 2 && Volatile.Read(ref _paused) != 0 &&
                Interlocked.Exchange(ref _resumeQueued, 1) == 0)
            {
                _engine.EnqueueAcceptAction(_resume);
            }
            return handle;
        }

        private unsafe void Submit()
        {
            if (_active || Volatile.Read(ref _stopRequested) != 0 || _engine._stopping)
            {
                return;
            }
            var added = false;
            _listener.DangerousAddRef(ref added);
            _active = true;
            _cancelSent = false;
            _id = _engine._nextId++;
            _engine._pending.Add(_id, this);
            if (_engine.DiagnosticsEnabled)
            {
                _engine._accepts++;
            }
            var result = Native.Enqueue(_engine._ring, _multishot ? 4 : 0, _listener.DangerousGetHandle().ToInt32(), null, 0, _id);
            if (result < 0)
            {
                _engine._pending.Remove(_id);
                OnCompletion(result, 0);
            }
        }

        public override void Complete(int result) => OnCompletion(result, 0);

        public override void OnCompletion(int result, uint flags)
        {
            var more = (flags & Native.More) != 0;
            if (!more)
            {
                _active = false;
                _listener.DangerousRelease();
                if (_engine.DiagnosticsEnabled)
                {
                    _engine._acceptTerminals++;
                }
            }
            else if (_engine.DiagnosticsEnabled)
            {
                _engine._acceptMore++;
            }

            if (result >= 0)
            {
                if (_engine.DiagnosticsEnabled)
                {
                    _engine._acceptResults++;
                }
                var handle = new SafeSocketHandle(result, ownsHandle: true);
                if (Volatile.Read(ref _stopRequested) != 0 || _engine._stopping)
                {
                    handle.Dispose();
                    if (_engine.DiagnosticsEnabled)
                    {
                        _engine._acceptQueueDisposed++;
                    }
                }
                else
                {
                    var depth = Interlocked.Increment(ref _engine._acceptQueueDepth);
                    if (_engine.DiagnosticsEnabled)
                    {
                        _engine._acceptQueueHighWater = Math.Max(_engine._acceptQueueHighWater, depth);
                    }
                    if (depth >= _pauseAt && Interlocked.Exchange(ref _paused, 1) == 0 && _engine.DiagnosticsEnabled)
                    {
                        _engine._acceptPauses++;
                    }
                    if (!_accepted.Writer.TryWrite(handle))
                    {
                        Interlocked.Decrement(ref _engine._acceptQueueDepth);
                        handle.Dispose();
                        _engine._acceptQueueOverflows++;
                        Fail(new IOException("The bounded accept queue overflowed while multishot cancellation was in flight."));
                    }
                }
            }
            else if (result != -125 || (Volatile.Read(ref _stopRequested) == 0 && Volatile.Read(ref _paused) == 0 && !_engine._stopping))
            {
                Fail(CreateIoException(0, result));
            }

            if (Volatile.Read(ref _stopRequested) != 0 || _engine._stopping)
            {
                StopOnPump();
            }
            else if (Volatile.Read(ref _paused) != 0)
            {
                CancelOnPump();
                Resume();
            }
            else if (!more)
            {
                Submit();
            }
        }

        private void Resume()
        {
            Volatile.Write(ref _resumeQueued, 0);
            if (_active || Volatile.Read(ref _engine._acceptQueueDepth) > _pauseAt / 2)
            {
                return;
            }
            if (Interlocked.Exchange(ref _paused, 0) != 0 && _engine.DiagnosticsEnabled)
            {
                _engine._acceptResumes++;
            }
            Submit();
        }

        private void CancelOnPump()
        {
            if (_active && !_cancelSent)
            {
                Check(Native.Cancel(_engine._ring, _id));
                _cancelSent = true;
                _engine._cancellations++;
                if (_engine.DiagnosticsEnabled)
                {
                    _engine._cancellationRequests++;
                }
            }
        }

        private void Fail(Exception error)
        {
            _logger.LogError(error, "io_uring accept stream failed");
            Volatile.Write(ref _stopRequested, 1);
            _accepted.Writer.TryComplete(error);
        }

        private void RequestStop()
        {
            Volatile.Write(ref _stopRequested, 1);
            _engine.EnqueueAcceptAction(_stop);
        }

        private void StopOnPump()
        {
            Volatile.Write(ref _stopRequested, 1);
            CancelOnPump();
            _accepted.Writer.TryComplete(new OperationCanceledException(_stopToken));
            DrainAccepted();
        }

        private void DrainAccepted()
        {
            while (_accepted.Reader.TryRead(out var handle))
            {
                Interlocked.Decrement(ref _engine._acceptQueueDepth);
                handle.Dispose();
                if (_engine.DiagnosticsEnabled)
                {
                    _engine._acceptQueueDisposed++;
                }
            }
        }

        public void Dispose()
        {
            _registration.Dispose();
            if (_active)
            {
                throw new InvalidOperationException("AcceptStream disposal requires its terminal CQE.");
            }
            DrainAccepted();
        }
    }
}
