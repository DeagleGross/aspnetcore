// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks.Sources;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

internal sealed class LeasedPipeReader(Action<IMemoryOwner<byte>> release) : PipeReader, IValueTaskSource<ReadResult>
{
    private readonly Lock _gate = new();
    private Page? _head, _tail;
    private int _offset;
    private bool _ended, _completed, _waiting, _reading, _cancelled, _examinedAll;
    private Exception? _error;
    private ReadOnlySequence<byte> _readBuffer;
    private ManualResetValueTaskSourceCore<ReadResult> _source = new() { RunContinuationsAsynchronously = true };

    internal void Append(IMemoryOwner<byte> owner, int length)
    {
        lock (_gate)
        {
            if (_completed)
            {
                release(owner);
                return;
            }
            var page = new Page(owner, length);
            _examinedAll = false;
            if (_tail is null)
            {
                _head = _tail = page;
            }
            else
            {
                _tail.Link(page);
                _tail = page;
            }
            Signal();
        }
    }

    internal void End(Exception? error)
    {
        lock (_gate)
        {
            _ended = true;
            _error ??= error;
            Signal();
        }
    }

    private ReadOnlySequence<byte> Buffer => _head is null
        ? ReadOnlySequence<byte>.Empty
        : new ReadOnlySequence<byte>(_head, _offset, _tail!, _tail!.Memory.Length);

    private ReadResult Result()
    {
        if (_error is not null)
        {
            throw _error;
        }
        _reading = true;
        _readBuffer = Buffer;
        var cancelled = _cancelled;
        _cancelled = false;
        return new ReadResult(_readBuffer, cancelled, _ended || _completed);
    }

    private void Signal()
    {
        if (_waiting)
        {
            _waiting = false;
            if (_error is not null)
            {
                _source.SetException(_error);
            }
            else
            {
                _source.SetResult(Result());
            }
        }
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_waiting || _reading)
            {
                throw new InvalidOperationException("Consume the previous read before starting another read.");
            }
            if ((_head is not null && !_examinedAll) || _ended || _completed || _cancelled)
            {
                return ValueTask.FromResult(Result());
            }
            _source.Reset();
            _waiting = true;
            return cancellationToken.CanBeCanceled ? AwaitRead(cancellationToken) : new ValueTask<ReadResult>(this, _source.Version);
        }
    }

    private async ValueTask<ReadResult> AwaitRead(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.UnsafeRegister(static reader => ((LeasedPipeReader)reader!).CancelPendingRead(), this);
        return await new ValueTask<ReadResult>(this, _source.Version).ConfigureAwait(false);
    }

    public override bool TryRead(out ReadResult result)
    {
        lock (_gate)
        {
            if (_waiting || _reading)
            {
                throw new InvalidOperationException("Concurrent pipe reads are not supported.");
            }
            if ((_head is null || _examinedAll) && !_ended && !_completed && !_cancelled)
            {
                result = default;
                return false;
            }
            result = Result();
            return true;
        }
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        lock (_gate)
        {
            if (!_reading)
            {
                throw new InvalidOperationException("AdvanceTo requires an outstanding read.");
            }
            var origin = _readBuffer.GetOffset(_readBuffer.Start);
            var used = _readBuffer.GetOffset(consumed) - origin;
            var seen = _readBuffer.GetOffset(examined) - origin;
            if (used < 0 || seen < used || seen > _readBuffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(consumed));
            }
            _reading = false;
            _examinedAll = _tail is not null && ReferenceEquals(examined.GetObject(), _tail)
                && examined.GetInteger() == _tail.Memory.Length;
            _readBuffer = default;
            var target = consumed.GetObject() as Page;
            while (_head is not null && _head != target)
            {
                ReleaseHead();
            }
            if (_head is not null)
            {
                _offset = consumed.GetInteger();
                if (_offset == _head.Memory.Length)
                {
                    ReleaseHead();
                }
            }
        }
    }

    private void ReleaseHead()
    {
        var page = _head!;
        _head = page.Following;
        if (_head is null)
        {
            _tail = null;
        }
        _offset = 0;
        release(page.Owner);
    }

    public override void CancelPendingRead()
    {
        lock (_gate)
        {
            _cancelled = true;
            Signal();
        }
    }

    public override void Complete(Exception? exception = null)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }
            _completed = true;
            _error ??= exception;
            while (_head is not null)
            {
                ReleaseHead();
            }
            Signal();
        }
    }

    ReadResult IValueTaskSource<ReadResult>.GetResult(short token) => _source.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<ReadResult>.GetStatus(short token) => _source.GetStatus(token);
    void IValueTaskSource<ReadResult>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _source.OnCompleted(continuation, state, token, flags);

    private sealed class Page : ReadOnlySequenceSegment<byte>
    {
        internal IMemoryOwner<byte> Owner { get; }
        internal Page? Following => (Page?)Next;

        internal Page(IMemoryOwner<byte> owner, int length)
        {
            Owner = owner;
            Memory = owner.Memory[..length];
        }

        internal void Link(Page next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}
