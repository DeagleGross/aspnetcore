// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll;

internal static unsafe partial class Native
{
    internal const int Again = -11, Interrupted = -4, ConnectionAborted = -103, NotConnected = -107;
    internal const int ConnectionReset = -104, BrokenPipe = -32;
    internal const int Add = 1, Delete = 2, Modify = 3;
    internal const uint Readable = 1, Writable = 4, Error = 8, Hangup = 16;
    internal enum SocketOption { NoDelay, Cork, SendBuffer }
    internal enum TlsOperation { Handshake, Read, Write, Shutdown }
    internal enum TlsStatus { Complete, WantRead, WantWrite, Closed, PeerAbort, Failed }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ReadyEvent
    {
        internal ulong Id;
        internal uint Events, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TlsResult
    {
        internal TlsStatus Status;
        internal int Length, Pending, Error;
        internal ulong Reason;
    }

    [LibraryImport("networkprotoepoll", EntryPoint = "ep_set_cpu")]
    internal static partial int SetCpu(int cpu);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_create")]
    internal static partial int Create();
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_listener")]
    internal static partial int Listen(int port);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_accept")]
    internal static partial int Accept(int listener);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_socket_option")]
    internal static partial int SetSocketOption(int fd, SocketOption option, int value);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_shutdown")]
    internal static partial int Shutdown(int fd);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_close")]
    internal static partial int Close(int fd);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_recv")]
    internal static partial int Receive(int fd, nint buffer, int length);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_send")]
    internal static partial int Send(int fd, nint buffer, int length);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_eventfd")]
    internal static partial int CreateWake();
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_wake")]
    internal static partial int Wake(int fd);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_drain_wake")]
    internal static partial int DrainWake(int fd);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_control")]
    internal static partial int Control(int epollfd, int operation, int fd, uint events, ulong id);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_wait")]
    internal static partial int Wait(int epollfd, ReadyEvent* events, int capacity, int timeout);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_context", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint CreateContext(string cert, string key, out ulong error);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_session")]
    internal static partial nint CreateSession(nint context, int fd, out ulong error);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_tls")]
    internal static partial void Tls(nint session, TlsOperation operation, nint buffer, int length, out TlsResult result);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_free_session")]
    internal static partial void FreeSession(nint session);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_free_context")]
    internal static partial void FreeContext(nint context);
    [LibraryImport("networkprotoepoll", EntryPoint = "ep_version")]
    internal static partial nint Version();

    internal static int Check(int result, string operation)
    {
        if (result < 0)
        {
            throw new Win32Exception(-result, $"Epoll {operation} failed.");
        }
        return result;
    }
}
