// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Globalization;
using System.Numerics;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

/// <summary>
/// A per-connection view over a worker's block pool. The output pipe renders a response into whatever block size the pool returns,
/// and the transport sends one block per native operation, so block size decides how many sends a response costs.
/// The pool learns the size from evidence it can see without a public hook: the pipe rents another block only after the current one is full,
/// so every block of a burst except the last was filled completely. The next burst's first block is sized from that lower bound,
/// and a new connection starts from the worker's <see cref="OutputBlockHint"/> so one-request connections do not relearn it.
/// </summary>
internal sealed class AdaptiveOutputPool(NativeMemoryPool inner, int firstBlock, int maxBlock) : MemoryPool<byte>
{
    private const int QuietBurstsBeforeShrink = 16;
    private const int MaxQuietBurstsBeforeShrink = 4096;

    // NETWORKPROTO_OUT_MAX overrides every provider's largest block; 0 disables the adaptive pools so providers keep their previous output memory for A/B runs.
    private static readonly int? MaxBlockOverride = int.TryParse(Environment.GetEnvironmentVariable("NETWORKPROTO_OUT_MAX"), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    internal static bool Enabled => MaxBlockOverride != 0;

    private int _buffered;
    // The pipe serializes Rent calls, so the burst state needs no synchronization.
    private int _hint, _lastCapacity, _burstFull, _quietBursts, _shrinkAfter = QuietBurstsBeforeShrink;
    private bool _probing, _started, _grew;
    private int _startHint;

    public override int MaxBufferSize => inner.MaxBufferSize;

    internal static AdaptiveOutputPool Create(NativeMemoryPool inner, int firstBlock, int maxBlock) => new(inner, firstBlock, Math.Max(firstBlock, MaxBlockOverride ?? maxBlock));

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        var buffered = Volatile.Read(ref _buffered);
        int target;
        if (buffered == 0)
        {
            EndBurst();
            if (!_started)
            {
                // A new connection starts from what the worker's recent connections needed instead of from the smallest block.
                _started = true;
                _hint = Math.Max(_hint, inner.OutputHint.Value);
                _startHint = _hint;
            }
            target = Math.Max(_hint, firstBlock);
        }
        else
        {
            _burstFull += _lastCapacity;
            _grew = true;
            inner.OutputHint.Raise((int)Math.Min(maxBlock, BitOperations.RoundUpToPowerOf2((uint)_burstFull + 1)));
            target = buffered;
        }
        var floor = Math.Max(minBufferSize, firstBlock);
        var size = Math.Min(Math.Max(minBufferSize, Math.Clamp(target, firstBlock, maxBlock)), inner.MaxBufferSize);
        var owner = inner.TryRent(size);
        while (owner is null && size > floor)
        {
            size = Math.Max(floor, size >> 1);
            owner = inner.TryRent(size);
        }
        owner ??= inner.Rent(size);
        _lastCapacity = owner.Memory.Length;
        Interlocked.Add(ref _buffered, _lastCapacity);
        return new Block(this, owner, _lastCapacity);
    }

    /// <summary>A connection that began from the shared hint and never filled a block shows the hint was larger than it needed.</summary>
    internal void EndConnection()
    {
        if (_started && !_grew && _startHint > firstBlock)
        {
            inner.OutputHint.Quiet(firstBlock);
        }
        _started = false;
    }

    private void EndBurst()
    {
        if (_burstFull > 0)
        {
            _hint = (int)Math.Min(maxBlock, BitOperations.RoundUpToPowerOf2((uint)_burstFull + 1));
            if (_probing)
            {
                _shrinkAfter = Math.Min(_shrinkAfter * 2, MaxQuietBurstsBeforeShrink);
                _probing = false;
            }
            _quietBursts = 0;
        }
        else
        {
            if (_probing)
            {
                _shrinkAfter = QuietBurstsBeforeShrink;
                _probing = false;
            }
            if (++_quietBursts >= _shrinkAfter && _hint > firstBlock)
            {
                _hint = Math.Max(firstBlock, _hint >> 1);
                _probing = true;
                _quietBursts = 0;
            }
        }
        _burstFull = 0;
    }

    protected override void Dispose(bool disposing)
    {
    }

    private sealed class Block(AdaptiveOutputPool pool, IMemoryOwner<byte> owner, int length) : IMemoryOwner<byte>
    {
        private int _disposed;
        public Memory<byte> Memory => owner.Memory;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Add(ref pool._buffered, -length);
                owner.Dispose();
            }
        }
    }
}
