// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net.Sockets;
using System.Threading.Tasks.Sources;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUring;

internal sealed partial class IoUringEngine
{
    internal sealed class ReusableOperation : PendingOperation, IValueTaskSource<int>
    {
        private readonly IoUringEngine _engine;
        private readonly SafeSocketHandle _socket;
        private readonly int _kind;
        private Action? _submit;
        private ManualResetValueTaskSourceCore<int> _completion = new() { RunContinuationsAsynchronously = true };
        private MemoryHandle _pin;
        private CancellationTokenRegistration _registration;
        private CancellationToken _token;
        private uint _length;
        private ulong _id;
        private int _inUse;
        private int _slotIndex = -1;

        public ReusableOperation(IoUringEngine engine, int kind, Socket socket)
        {
            _engine = engine;
            _kind = kind;
            _socket = socket.SafeHandle;
            if (engine.DiagnosticsEnabled)
            {
                Interlocked.Increment(ref engine._reusableCreated);
            }
        }

        public ValueTask<int> Start(Memory<byte> memory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_engine._gate)
            {
                ObjectDisposedException.ThrowIf(_engine._stopping, _engine);
                if (Interlocked.Exchange(ref _inUse, 1) != 0)
                {
                    throw new InvalidOperationException("An io_uring operation was reused before its result was consumed.");
                }
                _completion.Reset();
                var added = false;
                try
                {
                    _socket.DangerousAddRef(ref added);
                    _pin = memory.Pin();
                }
                catch
                {
                    if (added)
                    {
                        _socket.DangerousRelease();
                    }
                    Volatile.Write(ref _inUse, 0);
                    throw;
                }
                _length = (uint)memory.Length;
                _token = cancellationToken;
                if (_engine.DiagnosticsEnabled)
                {
                    _engine._reusableSubmissions++;
                }
                if (_engine._directSubmissions)
                {
                    try
                    {
                        _id = _engine.Activate(this, ref _slotIndex);
                        _registration = _token.UnsafeRegister(static state =>
                        {
                            var operation = (ReusableOperation)state!;
                            operation._engine.CancelDirect(operation._id);
                        }, this);
                        _engine.StageOrQueue(this);
                    }
                    catch (Exception error)
                    {
                        // An SQE may already reference this buffer; do not unwind its owner.
                        Environment.FailFast("NetworkProto io_uring submission failed.", error);
                    }
                }
                else
                {
                    var wake = _engine._commands.IsEmpty;
                    _engine._commands.Enqueue(_submit ??= Submit);
                    if (wake)
                    {
                        _engine.Wake();
                    }
                }
                return new ValueTask<int>(this, _completion.Version);
            }
        }

        private unsafe void Submit()
        {
            if (_engine.DiagnosticsEnabled)
            {
                switch (_kind)
                {
                    case 0: _engine._accepts++; break;
                    case 1: _engine._receives++; break;
                    case 2: _engine._sends++; break;
                }
            }
            _id = _engine._nextId++;
            _engine._pending.Add(_id, this);
            _registration = _token.UnsafeRegister(static state =>
            {
                var operation = (ReusableOperation)state!;
                operation._engine.EnqueueCancel(operation._id);
            }, this);
            var result = Native.Enqueue(_engine._ring, _kind, _socket.DangerousGetHandle().ToInt32(), _pin.Pointer, _length, _id);
            if (result < 0)
            {
                _engine._pending.Remove(_id);
                Complete(result);
            }
        }

        internal unsafe bool TryStage()
        {
            var result = Native.Stage(_engine._ring, _kind, _socket.DangerousGetHandle().ToInt32(), _pin.Pointer, _length, _id);
            Check(result);
            if (result == 0)
            {
                return false;
            }
            if (_engine.DiagnosticsEnabled)
            {
                switch (_kind)
                {
                    case 0: _engine._accepts++; break;
                    case 1: _engine._receives++; break;
                    case 2:
                        _engine._sends++;
                        _engine._largestSend = Math.Max(_engine._largestSend, _length);
                        break;
                }
            }
            return true;
        }

        internal void ReleaseSlot()
        {
            lock (_engine._gate)
            {
                if (_inUse != 0)
                {
                    throw new InvalidOperationException("Cannot release an unconsumed io_uring operation.");
                }
                if (_slotIndex >= 0)
                {
                    _engine._slots[_slotIndex]!.Operation = null;
                    _engine._freeSlots.Push(_slotIndex);
                    _slotIndex = -1;
                }
            }
        }

        public override void Complete(int result)
        {
            if (_engine.DiagnosticsEnabled && _kind == 2 && result > 0 && result < _length)
            {
                _engine._sendPartial++;
            }
            // Finish the old callback before permitting another generation to reuse _id.
            _registration.Dispose();
            _registration = default;
            _pin.Dispose();
            _pin = default;
            _socket.DangerousRelease();
            var token = _token;
            _token = default;
            // Only small receive progress may run on the pump. The input pipe
            // still schedules Kestrel readers on the ThreadPool.
            _completion.RunContinuationsAsynchronously = !_engine._inlineReceive || _kind != 1 || result <= 0 || result >= 4096;
            if (result == -125)
            {
                _completion.SetException(new OperationCanceledException(token));
            }
            else if (result < 0)
            {
                _completion.SetException(CreateIoException(_kind, result));
            }
            else
            {
                _completion.SetResult(result);
            }
        }

        public int GetResult(short token)
        {
            if (Volatile.Read(ref _inUse) == 0 || _completion.GetStatus(token) == ValueTaskSourceStatus.Pending)
            {
                throw new InvalidOperationException("The io_uring result is not available for consumption.");
            }
            try
            {
                return _completion.GetResult(token);
            }
            finally
            {
                Volatile.Write(ref _inUse, 0);
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _completion.OnCompleted(continuation, state, token, flags);
    }
}
