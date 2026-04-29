// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

// ─────────────────────────────────────────────────────────────────────────────
// PROPOSAL-3 LEFTOVERS — these P/Invokes are NOT covered by RuntimeProposal/
// yet because they correspond to APIs proposed in proposal 3 of the runtime
// proposals (see ".test/kestrel tls proposals/03-socket-low-level-accept.md"):
//
//   * accept4 + sockaddr capture        →  Socket.TryAcceptNonBlocking(out handle, out endpoint)
//   * setsockopt(TCP_DEFER_ACCEPT)      →  SocketOptionName.TcpDeferAccept (in NativeLibc.cs)
//   * setsockopt(TCP_NODELAY) on raw fd →  Socket.NoDelay (already in runtime; this version
//                                          avoids the per-connection Socket allocation)
//
// Once proposal 3 lands and is wired into RuntimeProposal/, this entire file
// (and the entire Interop/ folder) can be deleted.
// ─────────────────────────────────────────────────────────────────────────────

internal static partial class NativeSsl
{
    private const string LIBC = "libc.so.6";

    // ── accept4 + sockaddr capture ────────────────────────────────────────
    public const int SOCK_NONBLOCK = 0x800;

    [LibraryImport(LIBC, SetLastError = true)]
    public static unsafe partial int accept4(int sockfd, void* addr, void* addrlen, int flags);

    public const int AF_INET = 2;
    public const int AF_INET6 = 10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SockAddrIn
    {
        public ushort sin_family;
        public ushort sin_port;
        public uint sin_addr;
        public ulong sin_zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SockAddrIn6
    {
        public ushort sin6_family;
        public ushort sin6_port;
        public uint sin6_flowinfo;
        public fixed byte sin6_addr[16];
        public uint sin6_scope_id;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SockAddrStorage
    {
        public ushort ss_family;
        public fixed byte data[126];
    }

    /// <summary>
    /// Accept a connection from the listen socket using accept4 with SOCK_NONBLOCK,
    /// also capturing the peer address to avoid a separate getpeername syscall.
    /// Returns the client fd on success, -1 if EAGAIN, or -2 on error.
    /// </summary>
    public static unsafe (int fd, System.Net.IPEndPoint? remoteEndPoint) AcceptNonBlockingWithPeerAddress(int listenFd)
    {
        SockAddrStorage addr = default;
        int addrLen = sizeof(SockAddrStorage);

        int clientFd = accept4(listenFd, &addr, &addrLen, SOCK_NONBLOCK);
        if (clientFd < 0)
        {
            int errno = Marshal.GetLastWin32Error();
            return errno == 11 ? (-1, null) : (-2, null);
        }

        System.Net.IPEndPoint? remoteEndPoint = null;
        try
        {
            if (addr.ss_family == AF_INET)
            {
                var addr4 = *(SockAddrIn*)&addr;
                ushort port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)addr4.sin_port);
                var ipBytes = BitConverter.GetBytes(addr4.sin_addr);
                remoteEndPoint = new System.Net.IPEndPoint(new System.Net.IPAddress(ipBytes), port);
            }
            else if (addr.ss_family == AF_INET6)
            {
                var addr6 = *(SockAddrIn6*)&addr;
                ushort port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)addr6.sin6_port);
                var ipBytes = new byte[16];
                for (int i = 0; i < 16; i++)
                {
                    ipBytes[i] = addr6.sin6_addr[i];
                }
                remoteEndPoint = new System.Net.IPEndPoint(new System.Net.IPAddress(ipBytes, addr6.sin6_scope_id), port);
            }
        }
        catch
        {
            // Best-effort
        }

        return (clientFd, remoteEndPoint);
    }

    // ── setsockopt(TCP_NODELAY) on raw fd ─────────────────────────────────
    // (Could use Socket.NoDelay, but that requires wrapping the fd in a Socket
    // per connection. The prototype prefers the raw P/Invoke for perf.)
    public const int SOL_TCP = 6;
    public const int TCP_NODELAY = 1;

    [LibraryImport(LIBC, SetLastError = true)]
    public static unsafe partial int setsockopt(int sockfd, int level, int optname, void* optval, int optlen);

    public static unsafe void SetTcpNoDelay(int fd)
    {
        int optval = 1;
        setsockopt(fd, SOL_TCP, TCP_NODELAY, &optval, sizeof(int));
    }
}
