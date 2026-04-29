// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0073, IDE0011, IDE0005, CA1822, IDE0044

using System.Runtime.InteropServices;

namespace System.Net.Sockets;

// PROTOTYPE — represents the runtime-internal interop for epoll.
// In the real ship, these P/Invokes already exist as native PAL functions in
// `src/native/libs/System.Native/pal_networking.c` (used by SocketAsyncEngine);
// the public SafeEpollHandle would be a thin managed wrapper layered on the same
// PAL exports.

internal static unsafe partial class Native
{
    private const string LibC = "libc.so.6";

    [LibraryImport(LibC, SetLastError = true)] public static partial int epoll_create1(int flags);
    [LibraryImport(LibC, SetLastError = true)] public static partial int epoll_ctl(int epfd, int op, int fd, ref EpollEvent ev);
    [LibraryImport(LibC, SetLastError = true)] public static partial int epoll_ctl(int epfd, int op, int fd, IntPtr ev);
    [LibraryImport(LibC, SetLastError = true)] public static partial int epoll_wait(int epfd, EpollEvent[] events, int maxevents, int timeout);
    [LibraryImport(LibC, SetLastError = true)] public static partial int close(int fd);

    public const int EPOLL_CTL_ADD = 1;
    public const int EPOLL_CTL_DEL = 2;
    public const int EPOLL_CTL_MOD = 3;

    // Kernel wire-format struct. NOT exposed publicly — only used internally
    // by SafeEpollHandle.Wait for the native call. The public API copies into
    // EpollNotification (which has no Pack=1 / union footgun).
    //
    // Pack=1 is correct on x86_64. On aarch64 the kernel does NOT pack this struct,
    // so a real runtime impl must branch on architecture. The prototype targets x86_64.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EpollEvent
    {
        public uint Events;
        public EpollData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct EpollData
    {
        [FieldOffset(0)] public IntPtr Ptr;
        [FieldOffset(0)] public int Fd;
        [FieldOffset(0)] public long U64;
    }
}
