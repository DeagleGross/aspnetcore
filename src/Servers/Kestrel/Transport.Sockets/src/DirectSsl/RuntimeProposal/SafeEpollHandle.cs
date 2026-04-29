// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0073, IDE0011, IDE0005, CA1822

using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace System.Net.Sockets;

// PROTOTYPE — would live in dotnet/runtime as System.Net.Sockets.SafeEpollHandle
//
// Wraps an epoll fd. Created via epoll_create1(EPOLL_CLOEXEC), released via close(epollFd).
// Linux only. All operations are instance methods; Wait fills a caller-supplied
// Span<EpollNotification> with up to N events.

internal sealed class SafeEpollHandle : SafeHandle
{
    public static bool IsSupported => OperatingSystem.IsLinux();

    public SafeEpollHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public SafeEpollHandle(IntPtr handle, bool ownsHandle) : base(IntPtr.Zero, ownsHandle)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero || (long)handle == -1;

    protected override bool ReleaseHandle()
    {
        if (!IsInvalid)
        {
            Native.close((int)handle);
            SetHandle(IntPtr.Zero);
        }
        return true;
    }

    /// <summary>epoll_create1(EPOLL_CLOEXEC). Throws on failure.</summary>
    public static SafeEpollHandle Create()
    {
        int fd = Native.epoll_create1(0);
        if (fd < 0)
        {
            int errno = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"epoll_create1 failed: errno={errno}");
        }
        return new SafeEpollHandle((IntPtr)fd, ownsHandle: true);
    }

    /// <summary>epoll_ctl(EPOLL_CTL_ADD, socket, { events|options, token }).</summary>
    public void Add(SafeSocketHandle socket, EpollEvents events, EpollOptions options, IntPtr token)
    {
        var ev = new Native.EpollEvent
        {
            Events = (uint)events | (uint)options,
            Data = new Native.EpollData { Ptr = token }
        };
        int fd = (int)socket.DangerousGetHandle();
        if (Native.epoll_ctl((int)handle, Native.EPOLL_CTL_ADD, fd, ref ev) < 0)
        {
            int errno = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"epoll_ctl(ADD, fd={fd}) failed: errno={errno}");
        }
    }

    /// <summary>epoll_ctl(EPOLL_CTL_MOD, socket, { events|options, token }).</summary>
    public void Modify(SafeSocketHandle socket, EpollEvents events, EpollOptions options, IntPtr token)
    {
        var ev = new Native.EpollEvent
        {
            Events = (uint)events | (uint)options,
            Data = new Native.EpollData { Ptr = token }
        };
        int fd = (int)socket.DangerousGetHandle();
        if (Native.epoll_ctl((int)handle, Native.EPOLL_CTL_MOD, fd, ref ev) < 0)
        {
            int errno = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"epoll_ctl(MOD, fd={fd}) failed: errno={errno}");
        }
    }

    /// <summary>epoll_ctl(EPOLL_CTL_DEL, socket, NULL).</summary>
    public void Remove(SafeSocketHandle socket)
    {
        int fd = (int)socket.DangerousGetHandle();
        Native.epoll_ctl((int)handle, Native.EPOLL_CTL_DEL, fd, IntPtr.Zero);
    }

    /// <summary>
    /// epoll_wait(epoll, events, max, timeoutMs). Fills <paramref name="notifications"/>
    /// with up to <c>notifications.Length</c> events. Returns the number written.
    /// </summary>
    public int Wait(Span<EpollNotification> notifications, int timeoutMs)
    {
        // The kernel struct (on x86_64) is __attribute__((packed)) so 12 bytes per event.
        // The runtime wrapper allocates a per-arch-correct buffer and copies into the
        // public EpollNotification shape (no Pack=1 footgun in the public API).
        int max = notifications.Length;
        var native = ArrayPool<Native.EpollEvent>.Shared.Rent(max);
        try
        {
            int n = Native.epoll_wait((int)handle, native, max, timeoutMs);
            if (n < 0)
            {
                int errno = Marshal.GetLastWin32Error();
                if (errno == 4 /* EINTR */) return 0;
                throw new InvalidOperationException($"epoll_wait failed: errno={errno}");
            }

            for (int i = 0; i < n; i++)
            {
                notifications[i] = new EpollNotification(
                    token: native[i].Data.Ptr,
                    events: (EpollEvents)(native[i].Events & 0x1F));
            }
            return n;
        }
        finally
        {
            ArrayPool<Native.EpollEvent>.Shared.Return(native);
        }
    }
}
