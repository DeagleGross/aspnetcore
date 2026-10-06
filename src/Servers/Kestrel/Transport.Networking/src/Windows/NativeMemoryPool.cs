// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Rio;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

internal sealed class NativeMemoryPool(RioApi? rio, int maximumBytes) : MemoryPool<byte>
{
    internal const int PageSize = 16384, SlabSize = 4 * 1024 * 1024;
    private readonly Lock _gate = new();
    private readonly ConcurrentStack<Slot> _free = new();
    private readonly Dictionary<int, ConcurrentStack<Slot>> _large = new();
    private readonly List<Slab> _slabs = new();
    private bool _disposed;
    private int _bytes, _outstanding;

    public override int MaxBufferSize => maximumBytes;
    internal int AllocatedBytes => Volatile.Read(ref _bytes);
    internal int Outstanding => Volatile.Read(ref _outstanding);

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        return TryRent(minBufferSize) ?? throw new InvalidOperationException("The Windows transport's bounded native memory pool is exhausted.");
    }

    internal IMemoryOwner<byte>? TryRent(int minBufferSize = -1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(minBufferSize, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minBufferSize, maximumBytes);
        var size = minBufferSize <= PageSize ? PageSize : checked((int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)minBufferSize));
        var stack = _free;
        if (size > PageSize)
        {
            lock (_gate)
            {
                if (!_large.TryGetValue(size, out stack))
                {
                    stack = new ConcurrentStack<Slot>();
                    _large.Add(size, stack);
                }
            }
        }
        if (!stack.TryPop(out var slot))
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!stack.TryPop(out slot))
                {
                    var allocation = Math.Max(SlabSize, size);
                    if (allocation > maximumBytes - _bytes)
                    {
                        return null;
                    }
                    var slab = new Slab(allocation, rio);
                    _slabs.Add(slab);
                    _bytes += allocation;
                    for (var offset = size; offset <= allocation - size; offset += size)
                    {
                        stack.Push(new Slot(slab, offset, size));
                    }
                    slot = new Slot(slab, 0, size);
                }
            }
        }
        Interlocked.Increment(ref _outstanding);
        return new Lease(this, stack, slot);
    }

    private void Return(ConcurrentStack<Slot> stack, Slot slot)
    {
        stack.Push(slot);
        Interlocked.Decrement(ref _outstanding);
    }

    protected override void Dispose(bool disposing)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (_outstanding != 0)
            {
                throw new InvalidOperationException($"Native pool disposal with {_outstanding} outstanding leases.");
            }
            _disposed = true;
            foreach (var slab in _slabs)
            {
                ((IDisposable)slab).Dispose();
            }
        }
    }

    internal static RioApi.Buffer RegisteredBuffer(ReadOnlyMemory<byte> memory)
    {
        if (!MemoryMarshal.TryGetMemoryManager<byte, Slab>(memory, out var slab, out var offset, out var length) || !slab.IsRegistered)
        {
            throw new InvalidOperationException("A RIO send must reference a live registered transport slab.");
        }
        return new RioApi.Buffer { Id = slab.Registration, Offset = checked((uint)offset), Length = checked((uint)length) };
    }

    private sealed record Slot(Slab Slab, int Offset, int Length);

    private sealed class Lease(NativeMemoryPool pool, ConcurrentStack<Slot> stack, Slot slot) : IMemoryOwner<byte>
    {
        private int _disposed;
        public Memory<byte> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                return slot.Slab.Memory.Slice(slot.Offset, slot.Length);
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                pool.Return(stack, slot);
            }
        }
    }

    private sealed unsafe class Slab : MemoryManager<byte>
    {
        private readonly NativeAllocation _memory;
        private readonly int _length;
        private readonly RioApi.BufferRegistration? _registration;
        private bool _disposed;
        internal nint Registration => _registration?.DangerousGetHandle() ?? 0;
        internal bool IsRegistered => _registration is not null;

        internal Slab(int length, RioApi? rio)
        {
            _length = length;
            _memory = new NativeAllocation(length);
            try
            {
                _registration = rio?.Register(_memory, length);
            }
            catch
            {
                _memory.Dispose();
                throw;
            }
        }

        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new Span<byte>((void*)_memory.DangerousGetHandle(), _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)_length);
            return new MemoryHandle((byte*)_memory.DangerousGetHandle() + elementIndex);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _registration?.Dispose();
                _memory.Dispose();
            }
        }
    }
}
