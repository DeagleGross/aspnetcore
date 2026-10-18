// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace System.Net.Transport;

/// <summary>Describes storage; the provider still validates registration and address lifetime.</summary>
public enum NetworkMemoryKind
{
    /// <summary>Ordinary managed memory, pinned while native I/O references it.</summary>
    Managed,
    /// <summary>Address-stable managed memory, such as a pinned-object-heap page.</summary>
    Pinned,
    /// <summary>Address-stable unmanaged storage.</summary>
    Native,
    /// <summary>Storage additionally registered with its native provider.</summary>
    Registered
}

/// <summary>Owns one reference to a reusable storage region.</summary>
public sealed class NetworkBuffer : IDisposable
{
    internal sealed class Storage(IMemoryOwner<byte> owner, NetworkMemoryKind kind, NetworkConnection? receiveConnection = null)
    {
        private int _references = 1;
        internal NetworkMemoryKind Kind { get; } = kind;
        internal Memory<byte> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _references) == 0, this);
                return owner.Memory;
            }
        }

        internal void Retain()
        {
            while (true)
            {
                var count = Volatile.Read(ref _references);
                ObjectDisposedException.ThrowIf(count == 0, this);
                if (Interlocked.CompareExchange(ref _references, checked(count + 1), count) == count)
                {
                    return;
                }
            }
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
            {
                owner.Dispose();
                receiveConnection?.ReceivePageReleased();
            }
        }
    }

    private Storage? _storage;
    private readonly int _offset, _length;

    private NetworkBuffer(Storage storage, int offset, int length)
    {
        _storage = storage;
        _offset = offset;
        _length = length;
    }

    /// <summary>Takes ownership of an allocator/provider lease without copying its bytes.</summary>
    public static NetworkBuffer TakeOwnership(IMemoryOwner<byte> owner, NetworkMemoryKind kind)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new NetworkBuffer(new Storage(owner, kind), 0, owner.Memory.Length);
    }

    /// <summary>Gets writable storage while this ownership reference remains valid.</summary>
    public Memory<byte> Memory => GetStorage().Memory.Slice(_offset, _length);

    /// <summary>Gets a borrowed slice valid until this reference is disposed.</summary>
    public NetworkBufferSlice Slice => new(GetStorage(), _offset, _length);

    /// <summary>Gets the provider-reported memory kind.</summary>
    public NetworkMemoryKind Kind => GetStorage().Kind;

    internal static NetworkBuffer Retain(Storage storage, int offset, int length)
    {
        storage.Retain();
        return new NetworkBuffer(storage, offset, length);
    }

    private Storage GetStorage()
    {
        return _storage ?? throw new ObjectDisposedException(nameof(NetworkBuffer));
    }

    /// <summary>Releases this reference; storage returns only after all retained references drain.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _storage, null)?.Release();
    }
}

/// <summary>A borrowed view which does not add a reference merely when copied.</summary>
public readonly struct NetworkBufferSlice
{
    private readonly NetworkBuffer.Storage? _storage;
    private readonly int _offset;

    internal NetworkBufferSlice(NetworkBuffer.Storage storage, int offset, int length)
    {
        _storage = storage;
        _offset = offset;
        Length = length;
    }

    /// <summary>Gets the valid byte count.</summary>
    public int Length { get; }

    /// <summary>Gets a read-only view; a retained reference is needed beyond receive advancement.</summary>
    public ReadOnlyMemory<byte> Memory => GetStorage().Memory.Slice(_offset, Length);

    /// <summary>Gets the provider-reported memory kind.</summary>
    public NetworkMemoryKind Kind => GetStorage().Kind;

    /// <summary>Returns a borrowed sub-slice without changing ownership.</summary>
    public NetworkBufferSlice Slice(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, Length - length);
        return new NetworkBufferSlice(GetStorage(), _offset + start, length);
    }

    /// <summary>Adds independent ownership so this slice can survive receive advancement.</summary>
    public NetworkBuffer Retain()
    {
        return NetworkBuffer.Retain(GetStorage(), _offset, Length);
    }

    private NetworkBuffer.Storage GetStorage()
    {
        return _storage ?? throw new InvalidOperationException("A default buffer slice has no storage.");
    }
}

/// <summary>Owns a retained, possibly multi-page payload without linearizing its bytes.</summary>
public sealed class NetworkPayload : IDisposable
{
    private NetworkBuffer[]? _owners;
    private readonly ReadOnlySequence<byte> _sequence;

    internal NetworkPayload(NetworkBuffer[] owners)
    {
        _owners = owners;
        Segment? head = null, tail = null;
        foreach (var owner in owners)
        {
            var segment = new Segment(owner.Memory);
            if (head is null)
            {
                head = tail = segment;
            }
            else
            {
                tail!.Link(segment);
                tail = segment;
            }
        }
        _sequence = head is null ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(head, 0, tail!, tail!.Memory.Length);
    }

    /// <summary>Gets a sequence over the retained pages, valid until disposal.</summary>
    public ReadOnlySequence<byte> Sequence
    {
        get
        {
            ObjectDisposedException.ThrowIf(_owners is null, this);
            return _sequence;
        }
    }

    /// <summary>Adds independent ownership of every payload page without copying its bytes.</summary>
    public NetworkPayload Retain()
    {
        var owners = _owners ?? throw new ObjectDisposedException(nameof(NetworkPayload));
        var retained = new NetworkBuffer[owners.Length];
        var count = 0;
        try
        {
            for (; count < owners.Length; count++)
            {
                retained[count] = owners[count].Slice.Retain();
            }
            return new NetworkPayload(retained);
        }
        catch
        {
            for (var i = 0; i < count; i++)
            {
                retained[i].Dispose();
            }
            throw;
        }
    }

    /// <summary>Releases all independently retained page references.</summary>
    public void Dispose()
    {
        var owners = Interlocked.Exchange(ref _owners, null);
        if (owners is not null)
        {
            foreach (var owner in owners)
            {
                owner.Dispose();
            }
        }
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        internal void Link(Segment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}
