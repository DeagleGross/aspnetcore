// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp;

internal static unsafe partial class Native
{
    // Commands are managed handoffs, not a native engine ABI.
    internal struct Command
    {
        public int Kind, Length;
        public ulong Connection;
        public void* Data;
        public int Page, Unused;
    }
    internal enum Operation
    {
        Accept, ReadPoll, Receive, WritePoll, Send, Cancel, WakeRead, LinkedSend
    }
    internal enum TlsOperation
    {
        Handshake, Read, Write, Shutdown
    }
    internal enum TlsStatus
    {
        Complete, WantRead, WantWrite, Closed, PeerAbort, Failed
    }
    internal const uint Buffer = 1, More = 2;
    internal const int Canceled = -125, NoBuffers = -105, NotConnected = -107;
    [StructLayout(LayoutKind.Sequential)]
    internal struct Completion
    {
        public ulong Id; public int Result; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct TlsResult
    {
        public TlsStatus Status; public int Length, Pending, Error; public ulong Reason;
    }

    [LibraryImport("networkproto2", EntryPoint = "ur_create")]
    internal static partial nint Create(int defer, int buffers, out int error);
    [LibraryImport("networkproto2", EntryPoint = "ur_prepare")]
    internal static partial int Prepare(nint ring, Operation kind, int fd, nint data, int length, ulong id, ulong target, int flags);
    [LibraryImport("networkproto2", EntryPoint = "ur_collect")]
    internal static partial int Collect(nint ring, Completion* output, int capacity, int wait);
    [LibraryImport("networkproto2", EntryPoint = "ur_offer")]
    internal static partial void Offer(nint ring, nint data, int length, int page);
    [LibraryImport("networkproto2", EntryPoint = "ur_destroy")]
    internal static partial void Destroy(nint ring);
    [LibraryImport("networkproto2", EntryPoint = "ur_cpu")]
    internal static partial int SetCpu(int cpu);
    [LibraryImport("networkproto2", EntryPoint = "ur_listener")]
    internal static partial int Listen(int port);
    [LibraryImport("networkproto2", EntryPoint = "ur_option")]
    internal static partial int Option(int fd, int option, int value);
    [LibraryImport("networkproto2", EntryPoint = "ur_shutdown")]
    internal static partial int Shutdown(int fd);
    [LibraryImport("networkproto2", EntryPoint = "ur_close")]
    internal static partial int Close(int fd);
    [LibraryImport("networkproto2", EntryPoint = "ur_eventfd")]
    internal static partial int EventFd();
    [LibraryImport("networkproto2", EntryPoint = "ur_wake")]
    internal static partial int Wake(int fd);
    [LibraryImport("networkproto2", EntryPoint = "ur_context", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint Context(string certificate, string key, int ktls, out ulong error);
    [LibraryImport("networkproto2", EntryPoint = "ur_session")]
    internal static partial nint Session(nint context, int fd);
    [LibraryImport("networkproto2", EntryPoint = "ur_tls")]
    internal static partial void Tls(nint session, TlsOperation operation, nint data, int length, out TlsResult result);
    [LibraryImport("networkproto2", EntryPoint = "ur_ktls")]
    internal static partial int Ktls(nint session);
    [LibraryImport("networkproto2", EntryPoint = "ur_free_session")]
    internal static partial void FreeSession(nint session);
    [LibraryImport("networkproto2", EntryPoint = "ur_free_context")]
    internal static partial void FreeContext(nint context);
    [LibraryImport("networkproto2", EntryPoint = "ur_version")]
    internal static partial nint Version();
    internal static int Check(int result, string operation)
    {
        if (result < 0)
        {
            throw new Win32Exception(-result, $"io_uring {operation} failed.");
        }
        return result;
    }
}
