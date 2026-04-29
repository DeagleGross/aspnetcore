// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ─────────────────────────────────────────────────────────────────────────────
// PROPOSAL-3 LEFTOVER — TCP_DEFER_ACCEPT setsockopt.
// In proposal 3 (".test/kestrel tls proposals/03-socket-low-level-accept.md")
// this becomes:
//     listenSocket.SetSocketOption(
//         SocketOptionLevel.Tcp,
//         SocketOptionName.TcpDeferAccept,
//         optionValue: 1);
// and this whole file is deleted.
// ─────────────────────────────────────────────────────────────────────────────

using System.Runtime.InteropServices;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Interop;

internal static partial class NativeLibc
{
    public static int SetSocketOption(int sockfd, ref int optval, uint optlen)
    {
        return NativeLibc.setsockopt(sockfd, IPPROTO_TCP, TCP_DEFER_ACCEPT, ref optval, optlen);
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setsockopt(int sockfd, int level, int optname, ref int optval, uint optlen);

    private const int IPPROTO_TCP = 6;
    private const int TCP_DEFER_ACCEPT = 9;
}
