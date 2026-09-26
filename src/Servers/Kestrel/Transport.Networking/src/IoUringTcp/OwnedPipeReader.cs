// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks.Sources;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp;

internal sealed class OwnedPipeReader(Action<int> release) : PipeReader, IValueTaskSource<ReadResult>
{
    private readonly Lock _gate = new();
    private Page? _head, _tail;
    private int _offset;
    private bool _ended, _completed, _waiting, _cancelled;
    private bool _examinedAll;
    private Exception? _error;
    private ManualResetValueTaskSourceCore<ReadResult> _source = new() { RunContinuationsAsynchronously = true };

    internal unsafe void Append(void* pointer, int length, int id)
    {
        lock (_gate)
        {
            if (_completed)
            {
                release(id);
                return;
            }
            var page = new Page(pointer, length, id);
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

    private ReadResult Result()
    {
        if (_error is not null)
        {
            throw _error;
        }
        var buffer = _head is null ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(_head, _offset, _tail!, _tail!.Memory.Length);
        var cancelled = _cancelled;
        _cancelled = false;
        return new ReadResult(buffer, cancelled, _ended || _completed);
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
            if (_waiting)
            {
                throw new InvalidOperationException("Concurrent reads are not supported.");
            }
            if ((_head is not null && !_examinedAll) || _ended || _completed || _cancelled)
            {
                return ValueTask.FromResult(Result());
            }
            _source.Reset();
            _waiting = true;
            return AwaitRead(cancellationToken);
        }
    }

    private async ValueTask<ReadResult> AwaitRead(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.UnsafeRegister(static reader => ((OwnedPipeReader)reader!).CancelPendingRead(), this);
        return await new ValueTask<ReadResult>(this, _source.Version).ConfigureAwait(false);
    }

    public override bool TryRead(out ReadResult result)
    {
        lock (_gate)
        {
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
            _examinedAll = _tail is not null && ReferenceEquals(examined.GetObject(), _tail) && examined.GetInteger() == _tail.Memory.Length;
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
        release(page.Id);
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

    private sealed unsafe class Page : ReadOnlySequenceSegment<byte>
    {
        private readonly NativeMemoryView _owner;
        internal readonly int Id;
        internal Page? Following => (Page?)Next;
        internal Page(void* data, int length, int id)
        {
            _owner = new NativeMemoryView(data, length);
            Memory = _owner.Memory;
            Id = id;
        }
        internal void Link(Page next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }

    private sealed unsafe class NativeMemoryView(void* pointer, int length) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => new(pointer, length);
        public override MemoryHandle Pin(int elementIndex = 0) => new((byte*)pointer + elementIndex);
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
