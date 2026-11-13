// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Runtime.InteropServices;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

/// <summary>Reads the local or peer IPv4 endpoint of a connected Linux socket, so a published connection reports its real addresses.</summary>
internal static unsafe partial class SocketEndpoints
{
    private const int SockaddrInLength = 16;
    private const ushort AfInet = 2;

    /// <summary>Returns the endpoint of <paramref name="fd"/>, or <see langword="null"/> when the socket is no longer connected or is not IPv4.</summary>
    internal static IPEndPoint? TryGet(int fd, bool peer)
    {
        var address = stackalloc byte[SockaddrInLength];
        var length = (uint)SockaddrInLength;
        var result = peer ? getpeername(fd, address, &length) : getsockname(fd, address, &length);
        if (result != 0 || length < 8 || *(ushort*)address != AfInet)
        {
            return null;
        }
        // sockaddr_in: family (2 bytes), port in network byte order (2 bytes), IPv4 address (4 bytes).
        return new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(address + 4, 4)), (address[2] << 8) | address[3]);
    }

    [LibraryImport("libc", EntryPoint = "getsockname")]
    private static partial int getsockname(int fd, byte* address, uint* length);

    [LibraryImport("libc", EntryPoint = "getpeername")]
    private static partial int getpeername(int fd, byte* address, uint* length);
}
