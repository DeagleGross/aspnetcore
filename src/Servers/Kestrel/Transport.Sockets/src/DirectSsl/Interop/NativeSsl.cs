// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Interop;

internal static partial class NativeSsl
{
    private const string LIBC = "libc.so.6";

    // Epoll
    [LibraryImport(LIBC)] public static partial int epoll_create1(int flags);
    [LibraryImport(LIBC)] public static partial int epoll_ctl(int epfd, int op, int fd, ref EpollEvent ev);
    [LibraryImport(LIBC)] public static partial int epoll_ctl(int epfd, int op, int fd, IntPtr ev);
    [LibraryImport(LIBC)] public static partial int epoll_wait(int epfd, EpollEvent[] events, int maxevents, int timeout);
    [LibraryImport(LIBC)] public static partial int close(int fd);
    [LibraryImport(LIBC)] public static partial int fcntl(int fd, int cmd, int arg);

    // Epoll constants
    public const int EPOLL_CTL_ADD = 1;
    public const int EPOLL_CTL_DEL = 2;
    public const int EPOLL_CTL_MOD = 3;
    public const uint EPOLLIN = 0x001;
    public const uint EPOLLOUT = 0x004;
    public const uint EPOLLERR = 0x008;
    public const uint EPOLLHUP = 0x010;
    public const uint EPOLLET = 0x80000000;
    public const uint EPOLLRDHUP = 0x2000;
    public const uint EPOLLEXCLUSIVE = 0x10000000;  // Prevents thundering herd - only one worker wakes per event

    // Socket accept
    public const int SOCK_NONBLOCK = 0x800;  // O_NONBLOCK for socket
    [LibraryImport(LIBC, SetLastError = true)]
    public static unsafe partial int accept4(int sockfd, void* addr, void* addrlen, int flags);

    // sockaddr structures for accept4 with address capture
    public const int AF_INET = 2;
    public const int AF_INET6 = 10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SockAddrIn
    {
        public ushort sin_family;
        public ushort sin_port;      // Network byte order (big-endian)
        public uint sin_addr;        // Network byte order (big-endian)
        public ulong sin_zero;       // Padding
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SockAddrIn6
    {
        public ushort sin6_family;
        public ushort sin6_port;     // Network byte order (big-endian)
        public uint sin6_flowinfo;
        public fixed byte sin6_addr[16];
        public uint sin6_scope_id;
    }

    // Union-like storage for sockaddr (large enough for IPv6)
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SockAddrStorage
    {
        public ushort ss_family;
        public fixed byte data[126];  // Large enough for any sockaddr
    }

    /// <summary>
    /// Accept a connection from the listen socket using accept4 with SOCK_NONBLOCK.
    /// Also captures the peer address to avoid a separate getpeername syscall.
    /// Returns the client fd on success, -1 if EAGAIN (no pending connections), or -2 on error.
    /// </summary>
    public static unsafe (int fd, System.Net.IPEndPoint? remoteEndPoint) AcceptNonBlockingWithPeerAddress(int listenFd)
    {
        SockAddrStorage addr = default;
        int addrLen = sizeof(SockAddrStorage);

        int clientFd = accept4(listenFd, &addr, &addrLen, SOCK_NONBLOCK);
        if (clientFd < 0)
        {
            int errno = Marshal.GetLastWin32Error();
            // EAGAIN (11) or EWOULDBLOCK (same on Linux) - no pending connections
            if (errno == 11)
            {
                return (-1, null);
            }
            // Other error
            return (-2, null);
        }

        // Parse the sockaddr to IPEndPoint
        System.Net.IPEndPoint? remoteEndPoint = null;
        try
        {
            if (addr.ss_family == AF_INET)
            {
                var addr4 = *(SockAddrIn*)&addr;
                ushort port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)addr4.sin_port);
                var ipBytes = BitConverter.GetBytes(addr4.sin_addr);
                var ipAddress = new System.Net.IPAddress(ipBytes);
                remoteEndPoint = new System.Net.IPEndPoint(ipAddress, port);
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
                var ipAddress = new System.Net.IPAddress(ipBytes, addr6.sin6_scope_id);
                remoteEndPoint = new System.Net.IPEndPoint(ipAddress, port);
            }
        }
        catch
        {
            // If we can't parse the address, just return null - connection still works
        }

        return (clientFd, remoteEndPoint);
    }

    /// <summary>
    /// Accept a connection from the listen socket using accept4 with SOCK_NONBLOCK.
    /// Returns the client fd on success, -1 if EAGAIN (no pending connections), or -2 on error.
    /// </summary>
    public static unsafe int AcceptNonBlocking(int listenFd)
    {
        int clientFd = accept4(listenFd, null, null, SOCK_NONBLOCK);
        if (clientFd < 0)
        {
            int errno = Marshal.GetLastWin32Error();
            if (errno == 11)
            {
                return -1;
            }
            return -2;
        }
        return clientFd;
    }

    // fcntl
    public const int F_GETFL = 3;
    public const int F_SETFL = 4;
    public const int O_NONBLOCK = 2048;

    // Socket options
    public const int SOL_TCP = 6;
    public const int TCP_NODELAY = 1;
    [LibraryImport(LIBC, SetLastError = true)]
    public static unsafe partial int setsockopt(int sockfd, int level, int optname, void* optval, int optlen);

    // Socket shutdown constants
    public const int SHUT_RD = 0;
    public const int SHUT_WR = 1;
    public const int SHUT_RDWR = 2;

    [LibraryImport(LIBC, SetLastError = true)]
    public static partial int shutdown(int sockfd, int how);

    /// <summary>
    /// Set TCP_NODELAY on a socket to disable Nagle's algorithm.
    /// </summary>
    public static unsafe void SetTcpNoDelay(int fd)
    {
        int optval = 1;
        setsockopt(fd, SOL_TCP, TCP_NODELAY, &optval, sizeof(int));
    }

    public static void SetNonBlocking(int fd)
    {
        int flags = fcntl(fd, F_GETFL, 0);
        fcntl(fd, F_SETFL, flags | O_NONBLOCK);
    }
}
