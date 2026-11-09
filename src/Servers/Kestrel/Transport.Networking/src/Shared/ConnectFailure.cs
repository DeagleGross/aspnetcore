// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Net.Sockets;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

internal static class ConnectFailure
{
    // Maps a negative errno from a native connect() to the exception HttpClient and other callers expect from an outbound connect.
    internal static Exception FromErrno(int result, string provider) => -result switch
    {
        111 => new SocketException((int)SocketError.ConnectionRefused),
        110 => new SocketException((int)SocketError.TimedOut),
        101 => new SocketException((int)SocketError.NetworkUnreachable),
        113 => new SocketException((int)SocketError.HostUnreachable),
        _ => new IOException($"{provider} connect failed.", new Win32Exception(-result))
    };
}
