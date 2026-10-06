// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

internal enum IoKind { Accept, Probe, Receive, Send }

internal sealed unsafe class IoOperation : IDisposable
{
    private readonly NativeAllocation _allocation = new(sizeof(Native.Overlapped), zero: true);
    internal Native.Overlapped* Pointer => (Native.Overlapped*)_allocation.DangerousGetHandle();
    internal ConnectionState? Connection { get; }
    internal IoKind Kind { get; set; }
    internal bool Outstanding { get; set; }

    internal IoOperation(ConnectionState? connection, IoKind kind)
    {
        Connection = connection;
        Kind = kind;
    }

    internal void Reset()
    {
        if (Outstanding)
        {
            throw new InvalidOperationException("An OVERLAPPED cannot be reused before terminal completion.");
        }
        *Pointer = default;
    }

    public void Dispose()
    {
        if (Outstanding)
        {
            throw new InvalidOperationException("Disposing an operation still owned by Winsock.");
        }
        _allocation.Dispose();
    }
}
