// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

internal static unsafe partial class Native
{
    internal const int IoPending = 997, OperationAborted = 995, NotFound = 1168, WaitTimeout = 258;
    internal const int SocketLevel = 0xffff, SendBuffer = 0x1001, UpdateAcceptContext = 0x700b;
    internal const uint OverlappedSocket = 1, RegisteredSocket = 0x100;
    internal const uint GetExtension = 0xc8000006, GetMultipleExtensions = 0xc8000024;
    internal const nuint WakeKey = 1, RioKey = 2, AcceptKey = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Overlapped
    {
        internal nuint Internal, InternalHigh;
        internal uint Offset, OffsetHigh;
        internal nint Event;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Completion
    {
        internal nuint Key;
        internal Overlapped* Operation;
        internal nuint Status;
        internal uint Bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SocketBuffer
    {
        internal uint Length;
        internal byte* Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SocketAddress
    {
        internal ushort Family, Port;
        internal uint Address;
        internal ulong Padding;
    }

    internal static void ValidatePlatform()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("The Windows transport prototypes currently require Windows x64.");
        }
        if (sizeof(Overlapped) != 32 || sizeof(Completion) != 32 || sizeof(SocketBuffer) != 16 || sizeof(SocketAddress) != 16)
        {
            throw new InvalidOperationException("Windows SDK interop layout mismatch.");
        }
    }

    internal static SocketHandle CreateSocket(bool rio)
    {
        var socket = WSASocketW(2, 1, 6, 0, 0, OverlappedSocket | (rio ? RegisteredSocket : 0));
        if (socket == unchecked((nuint)(-1)))
        {
            throw SocketError("WSASocketW");
        }
        return new SocketHandle((nint)socket);
    }

    internal static SocketHandle Listen(IPEndPoint endpoint, bool rio)
    {
        var socket = CreateSocket(rio);
        try
        {
            var exclusive = 1;
            CheckSocket(setsockopt(socket, SocketLevel, unchecked((int)0xfffffffb), &exclusive, sizeof(int)), "SO_EXCLUSIVEADDRUSE");
            var address = Address(endpoint);
            CheckSocket(bind(socket, &address, sizeof(SocketAddress)), "bind");
            CheckSocket(listen(socket, 512), "listen");
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static SocketAddress Address(IPEndPoint endpoint)
    {
        var bytes = endpoint.Address.GetAddressBytes();
        return new SocketAddress
        {
            Family = 2,
            Port = (ushort)IPAddress.HostToNetworkOrder((short)endpoint.Port),
            Address = BitConverter.ToUInt32(bytes)
        };
    }

    internal static IPEndPoint GetEndPoint(SocketHandle socket, bool peer)
    {
        var address = new SocketAddress();
        var length = sizeof(SocketAddress);
        CheckSocket(peer ? getpeername(socket, &address, &length) : getsockname(socket, &address, &length), "socket endpoint");
        return new IPEndPoint(new IPAddress(BitConverter.GetBytes(address.Address)), (ushort)IPAddress.NetworkToHostOrder((short)address.Port));
    }

    internal static PortHandle CreatePort()
    {
        var port = CreateIoCompletionPort(-1, 0, 0, 1);
        if (port == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort");
        }
        return new PortHandle(port);
    }

    internal static void Associate(SocketHandle socket, PortHandle port, nuint key)
    {
        if (CreateIoCompletionPort(socket.DangerousGetHandle(), port.DangerousGetHandle(), key, 1) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Associate socket with IOCP");
        }
    }

    internal static void SkipSynchronousNotifications(SocketHandle socket)
    {
        if (SetFileCompletionNotificationModes(socket, 1) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "FILE_SKIP_COMPLETION_PORT_ON_SUCCESS");
        }
    }

    internal static int Poll(PortHandle port, Completion* entries, uint capacity, uint timeout)
    {
        if (GetQueuedCompletionStatusEx(port, entries, capacity, out var removed, timeout, 0) != 0)
        {
            return checked((int)removed);
        }
        var error = Marshal.GetLastPInvokeError();
        if (error != WaitTimeout)
        {
            throw new Win32Exception(error, "GetQueuedCompletionStatusEx");
        }
        return 0;
    }

    internal static void Wake(PortHandle port)
    {
        if (PostQueuedCompletionStatus(port, 0, WakeKey, null) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "PostQueuedCompletionStatus");
        }
    }

    internal static int CompletionError(nuint status)
    {
        var error = unchecked((int)status) >= 0 ? 0 : checked((int)RtlNtStatusToDosError((uint)status));
        return error switch
        {
            64 => (int)System.Net.Sockets.SocketError.ConnectionReset,
            1236 => (int)System.Net.Sockets.SocketError.ConnectionAborted,
            121 => (int)System.Net.Sockets.SocketError.TimedOut,
            _ => error
        };
    }

    internal static nint GetFunction(SocketHandle socket, Guid id)
    {
        nint function = 0;
        CheckSocket(WSAIoctl(socket, GetExtension, &id, sizeof(Guid), &function, sizeof(nint), out _, null, 0), "WSAIoctl extension");
        return function;
    }

    internal static void SetAcceptContext(SocketHandle socket, SocketHandle listener)
    {
        var handle = listener.DangerousGetHandle();
        CheckSocket(setsockopt(socket, SocketLevel, UpdateAcceptContext, &handle, sizeof(nint)), "SO_UPDATE_ACCEPT_CONTEXT");
        var noDelay = 1;
        CheckSocket(setsockopt(socket, 6, 1, &noDelay, sizeof(int)), "TCP_NODELAY");
    }

    internal static void SetSendBuffer(SocketHandle socket, int size)
    {
        CheckSocket(setsockopt(socket, SocketLevel, SendBuffer, &size, sizeof(int)), "SO_SNDBUF");
    }

    internal static void ShutdownSend(SocketHandle socket)
    {
        CheckSocket(shutdown(socket, 1), "shutdown(SD_SEND)");
    }

    internal static void Cancel(SocketHandle socket)
    {
        if (CancelIoEx(socket, null) == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != NotFound)
            {
                throw new Win32Exception(error, "CancelIoEx");
            }
        }
    }

    internal static void PinThread(int cpu)
    {
        if (cpu < 0)
        {
            return;
        }
        if (cpu >= 64 || SetThreadAffinityMask(GetCurrentThread(), (nuint)1 << cpu) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Worker affinity CPU {cpu}");
        }
    }

    internal static SocketException SocketError(string operation)
    {
        return new SocketException(WSAGetLastError()) { Source = operation };
    }

    internal static void CheckSocket(int result, string operation)
    {
        if (result == -1)
        {
            throw SocketError(operation);
        }
    }

    [LibraryImport("ws2_32.dll")]
    internal static partial int WSAStartup(ushort version, byte* data);
    [LibraryImport("ws2_32.dll")]
    internal static partial int WSACleanup();
    [LibraryImport("ws2_32.dll")]
    internal static partial int WSAGetLastError();
    [LibraryImport("ws2_32.dll")]
    private static partial nuint WSASocketW(int family, int type, int protocol, nint info, uint group, uint flags);
    [LibraryImport("ws2_32.dll")]
    internal static partial int closesocket(nint socket);
    [LibraryImport("ws2_32.dll")]
    private static partial int bind(SocketHandle socket, SocketAddress* address, int length);
    [LibraryImport("ws2_32.dll")]
    private static partial int listen(SocketHandle socket, int backlog);
    [LibraryImport("ws2_32.dll")]
    private static partial int getsockname(SocketHandle socket, SocketAddress* address, int* length);
    [LibraryImport("ws2_32.dll")]
    private static partial int getpeername(SocketHandle socket, SocketAddress* address, int* length);
    [LibraryImport("ws2_32.dll")]
    internal static partial int setsockopt(SocketHandle socket, int level, int option, void* value, int length);
    [LibraryImport("ws2_32.dll")]
    internal static partial int shutdown(SocketHandle socket, int how);
    [LibraryImport("ws2_32.dll")]
    internal static partial int WSAIoctl(SocketHandle socket, uint code, void* input, int inputLength, void* output, int outputLength, out uint bytes, Overlapped* overlapped, nint callback);
    [LibraryImport("ws2_32.dll")]
    internal static partial int WSARecv(SocketHandle socket, SocketBuffer* buffers, uint count, uint* bytes, uint* flags, Overlapped* overlapped, nint callback);
    [LibraryImport("ws2_32.dll")]
    internal static partial int WSASend(SocketHandle socket, SocketBuffer* buffers, uint count, uint* bytes, uint flags, Overlapped* overlapped, nint callback);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateIoCompletionPort(nint file, nint port, nuint key, uint concurrency);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetQueuedCompletionStatusEx(PortHandle port, Completion* entries, uint capacity, out uint removed, uint timeout, int alertable);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int PostQueuedCompletionStatus(PortHandle port, uint bytes, nuint key, Overlapped* overlapped);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CancelIoEx(SocketHandle socket, Overlapped* overlapped);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetFileCompletionNotificationModes(SocketHandle socket, byte flags);
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint SetThreadAffinityMask(nint thread, nuint mask);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int CloseHandle(nint handle);
    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(uint status);
}

internal sealed class SocketHandle : SafeHandleMinusOneIsInvalid
{
    internal SocketHandle(nint value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() => Native.closesocket(handle) == 0;
}

internal sealed class PortHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal PortHandle(nint value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() => Native.CloseHandle(handle) != 0;
}

internal sealed unsafe class NativeAllocation : SafeHandleZeroOrMinusOneIsInvalid
{
    internal NativeAllocation(int length, bool zero = false) : base(true)
    {
        var memory = zero ? NativeMemory.AllocZeroed((nuint)length) : NativeMemory.Alloc((nuint)length);
        if (memory is null)
        {
            throw new InvalidOperationException("Native transport memory allocation failed.");
        }
        SetHandle((nint)memory);
    }
    protected override bool ReleaseHandle()
    {
        NativeMemory.Free((void*)handle);
        return true;
    }
}

internal sealed unsafe class WinsockSession : IDisposable
{
    private bool _disposed;
    internal WinsockSession()
    {
        var data = stackalloc byte[512];
        var error = Native.WSAStartup(0x202, data);
        if (error != 0)
        {
            throw new SocketException(error);
        }
    }
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Native.CheckSocket(Native.WSACleanup(), "WSACleanup");
        }
    }
}
