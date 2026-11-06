// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Rio;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

/// <summary>
/// A bounded pool of native (optionally RIO-registered) blocks in power-of-two size classes from <paramref name="pageSize"/> up.
/// Blocks are never moved, so pinning a lease is free.
/// </summary>
internal sealed class NativeMemoryPool(RioApi? rio, int maximumBytes, int pageSize = NativeMemoryPool.DefaultPageSize) : MemoryPool<byte>
{
    internal const int DefaultPageSize = 16384, SlabSize = 4 * 1024 * 1024;
    private readonly Lock _gate = new();
    private readonly int _pageShift = System.Numerics.BitOperations.Log2((uint)pageSize);
    private readonly ConcurrentStack<Slot>[] _classes = CreateClasses(pageSize, maximumBytes);
    private readonly List<Slab> _slabs = new();
    private bool _disposed;
    private int _bytes, _outstanding, _peakBytes, _nextSlabId;
    private long _trimmedSlabs, _trimmedBytes;

    public override int MaxBufferSize => maximumBytes;
    internal OutputBlockHint OutputHint { get; } = new();
    internal int AllocatedBytes => Volatile.Read(ref _bytes);
    internal int PeakBytes => Volatile.Read(ref _peakBytes);
    internal int Outstanding => Volatile.Read(ref _outstanding);
    internal long TrimmedSlabs => Volatile.Read(ref _trimmedSlabs);
    internal long TrimmedBytes => Volatile.Read(ref _trimmedBytes);

    private static ConcurrentStack<Slot>[] CreateClasses(int pageSize, int maximumBytes)
    {
        if (!System.Numerics.BitOperations.IsPow2(pageSize))
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "The page size must be a power of two.");
        }
        var count = System.Numerics.BitOperations.Log2(System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(maximumBytes, pageSize))) - System.Numerics.BitOperations.Log2((uint)pageSize) + 1;
        var classes = new ConcurrentStack<Slot>[count];
        for (var i = 0; i < classes.Length; i++)
        {
            classes[i] = new ConcurrentStack<Slot>();
        }
        return classes;
    }

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        return TryRent(minBufferSize) ?? throw new InvalidOperationException("The transport's bounded native memory pool is exhausted.");
    }

    internal IMemoryOwner<byte>? TryRent(int minBufferSize = -1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(minBufferSize, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minBufferSize, maximumBytes);
        var size = minBufferSize <= pageSize ? pageSize : checked((int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)minBufferSize));
        var classIndex = System.Numerics.BitOperations.Log2((uint)size) - _pageShift;
        var stack = _classes[classIndex];
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
                    var slab = new Slab(_nextSlabId++, classIndex, allocation / size, allocation, rio);
                    _slabs.Add(slab);
                    _bytes += allocation;
                    _peakBytes = Math.Max(_peakBytes, _bytes);
                    for (var offset = size; offset <= allocation - size; offset += size)
                    {
                        stack.Push(new Slot(slab, offset, size));
                    }
                    slot = new Slot(slab, 0, size);
                }
            }
        }
        slot.Slab.Leased();
        Interlocked.Increment(ref _outstanding);
        return new Lease(this, stack, slot);
    }

    private void Return(ConcurrentStack<Slot> stack, Slot slot)
    {
        stack.Push(slot);
        slot.Slab.Returned();
        Interlocked.Decrement(ref _outstanding);
    }

    /// <summary>
    /// Unregisters and frees slabs that have had no leased block for <paramref name="idleMilliseconds"/>, at most <paramref name="maximumSlabs"/> per call.
    /// Any thread may rent or return concurrently: a slab is freed only after every one of its slots has been popped from the free stack under the pool gate,
    /// which proves that no lease, and therefore no pending I/O, references it.
    /// </summary>
    internal int TrimIdle(long nowMilliseconds, long idleMilliseconds, int maximumSlabs)
    {
        List<Slab> released;
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }
            ulong candidateClasses = 0;
            foreach (var slab in _slabs)
            {
                if (slab.IsIdle(nowMilliseconds, idleMilliseconds))
                {
                    candidateClasses |= 1UL << slab.ClassIndex;
                }
            }
            if (candidateClasses == 0)
            {
                return 0;
            }
            released = new List<Slab>();
            for (var index = 0; index < _classes.Length && released.Count < maximumSlabs; index++)
            {
                if ((candidateClasses & (1UL << index)) != 0)
                {
                    TrimClass(index, nowMilliseconds, idleMilliseconds, maximumSlabs, released);
                }
            }
        }
        foreach (var slab in released)
        {
            ((IDisposable)slab).Dispose();
            Interlocked.Increment(ref _trimmedSlabs);
            Interlocked.Add(ref _trimmedBytes, slab.Length);
        }
        return released.Count;
    }

    private void TrimClass(int index, long nowMilliseconds, long idleMilliseconds, int maximumSlabs, List<Slab> released)
    {
        var stack = _classes[index];
        var expected = 0;
        foreach (var slab in _slabs)
        {
            if (slab.ClassIndex == index)
            {
                slab.Drained = 0;
                expected += slab.SlotCount;
            }
        }
        // A renter that finds the stack empty waits on the gate this thread holds, then pops again, so draining cannot fail a rent.
        var free = new List<Slot>();
        while (free.Count < expected && stack.TryPop(out var slot))
        {
            free.Add(slot);
            slot.Slab.Drained++;
        }
        for (var i = _slabs.Count - 1; i >= 0 && released.Count < maximumSlabs; i--)
        {
            var slab = _slabs[i];
            if (slab.ClassIndex == index && slab.Drained == slab.SlotCount && slab.IsIdle(nowMilliseconds, idleMilliseconds))
            {
                slab.Trimmed = true;
                _slabs.RemoveAt(i);
                _bytes -= slab.Length;
                released.Add(slab);
            }
        }
        free.RemoveAll(static slot => slot.Slab.Trimmed);
        // Lowest slab ids end up on top, so new rentals keep filling older slabs and the newest ones can go idle.
        free.Sort(static (left, right) => right.Slab.Id.CompareTo(left.Slab.Id));
        foreach (var slot in free)
        {
            stack.Push(slot);
        }
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
        if (!TryGetRegisteredBuffer(memory, out var buffer))
        {
            throw new InvalidOperationException("A RIO send must reference a live registered transport slab.");
        }
        return buffer;
    }

    internal static bool TryGetRegisteredBuffer(ReadOnlyMemory<byte> memory, out RioApi.Buffer buffer)
    {
        if (MemoryMarshal.TryGetMemoryManager<byte, Slab>(memory, out var slab, out var offset, out var length) && slab.IsRegistered)
        {
            buffer = new RioApi.Buffer { Id = slab.Registration, Offset = checked((uint)offset), Length = checked((uint)length) };
            return true;
        }
        buffer = default;
        return false;
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
        private int _rented;
        private long _lastUsed = Environment.TickCount64;
        internal nint Registration => _registration?.DangerousGetHandle() ?? 0;
        internal bool IsRegistered => _registration is not null;
        internal int Id { get; }
        internal int ClassIndex { get; }
        internal int SlotCount { get; }
        internal int Length => _length;
        internal int Drained { get; set; }
        internal bool Trimmed { get; set; }

        internal Slab(int id, int classIndex, int slotCount, int length, RioApi? rio)
        {
            Id = id;
            ClassIndex = classIndex;
            SlotCount = slotCount;
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

        internal void Leased() => Interlocked.Increment(ref _rented);

        internal void Returned()
        {
            Volatile.Write(ref _lastUsed, Environment.TickCount64);
            Interlocked.Decrement(ref _rented);
        }

        // A cheap filter only: whether the slab is really free is decided by draining its slots from the free stack.
        internal bool IsIdle(long nowMilliseconds, long idleMilliseconds)
            => Volatile.Read(ref _rented) == 0 && nowMilliseconds - Volatile.Read(ref _lastUsed) >= idleMilliseconds;

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
