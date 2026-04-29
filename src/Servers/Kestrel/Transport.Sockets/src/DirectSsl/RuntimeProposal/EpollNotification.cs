// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0073, IDE0011, IDE0005, CA1822

using System.Runtime.InteropServices;

namespace System.Net.Sockets;

// PROTOTYPE — would live in dotnet/runtime as System.Net.Sockets.EpollNotification

/// <summary>One readiness notification returned from <see cref="SafeEpollHandle.Wait"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct EpollNotification
{
    public EpollNotification(IntPtr token, EpollEvents events)
    {
        Token = token;
        Events = events;
    }

    /// <summary>
    /// The token supplied to <see cref="SafeEpollHandle.Add"/>/<see cref="SafeEpollHandle.Modify"/>.
    /// Typically <c>(IntPtr)fd</c>, a <see cref="GCHandle"/>, or an index.
    /// </summary>
    public IntPtr Token { get; }

    public EpollEvents Events { get; }
}
