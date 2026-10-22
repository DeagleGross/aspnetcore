// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

/// <summary>
/// The first-block size that recent connections on one worker needed. A connection has no history of its own when it starts, so with one request per connection
/// every response would walk up from the smallest block and pay a native send for each step. Connections that fill a block raise the hint at once;
/// connections that close without filling a block vote it down, and a failed probe backs the next vote off, like the per-connection hint does.
/// </summary>
internal sealed class OutputBlockHint
{
    private const int QuietBeforeShrink = 16;
    private const int MaxQuietBeforeShrink = 4096;

    private readonly object _gate = new();
    private int _value, _quiet, _shrinkAfter = QuietBeforeShrink;
    private bool _probing;

    internal int Value => Volatile.Read(ref _value);

    internal void Raise(int candidate)
    {
        if (candidate <= Value)
        {
            return;
        }
        lock (_gate)
        {
            if (candidate <= _value)
            {
                return;
            }
            if (_probing)
            {
                _shrinkAfter = Math.Min(_shrinkAfter * 2, MaxQuietBeforeShrink);
                _probing = false;
            }
            _quiet = 0;
            Volatile.Write(ref _value, candidate);
        }
    }

    internal void Quiet(int floor)
    {
        lock (_gate)
        {
            if (_probing)
            {
                _shrinkAfter = QuietBeforeShrink;
                _probing = false;
            }
            if (++_quiet >= _shrinkAfter && _value > floor)
            {
                _quiet = 0;
                _probing = true;
                Volatile.Write(ref _value, Math.Max(floor, _value >> 1));
            }
        }
    }
}
