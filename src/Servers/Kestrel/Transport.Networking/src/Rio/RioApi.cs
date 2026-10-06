// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Rio;

internal sealed unsafe class RioApi
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Buffer
    {
        internal nint Id;
        internal uint Offset, Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Result
    {
        internal int Status;
        internal uint Bytes;
        internal nuint Connection, Request;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Notification
    {
        internal int Type;
        internal nint Port, Key, Overlapped;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Functions
    {
        internal uint Size;
        internal delegate* unmanaged[Stdcall]<nint, Buffer*, uint, uint, nint, int> Receive;
        internal nint ReceiveEx;
        internal delegate* unmanaged[Stdcall]<nint, Buffer*, uint, uint, nint, int> Send;
        internal nint SendEx;
        internal delegate* unmanaged[Stdcall]<nint, void> CloseCompletionQueue;
        internal delegate* unmanaged[Stdcall]<uint, Notification*, nint> CreateCompletionQueue;
        internal delegate* unmanaged[Stdcall]<nuint, uint, uint, uint, uint, nint, nint, nuint, nint> CreateRequestQueue;
        internal delegate* unmanaged[Stdcall]<nint, Result*, uint, uint> Dequeue;
        internal delegate* unmanaged[Stdcall]<nint, void> Deregister;
        internal delegate* unmanaged[Stdcall]<nint, int> Notify;
        internal delegate* unmanaged[Stdcall]<void*, uint, nint> Register;
        internal delegate* unmanaged[Stdcall]<nint, uint, int> ResizeCompletionQueue;
        internal nint ResizeRequestQueue;
    }

    private readonly Functions _functions;

    internal RioApi(SocketHandle socket)
    {
        if (sizeof(Functions) != 112 || sizeof(Notification) != 32 || sizeof(Buffer) != 16 || sizeof(Result) != 24)
        {
            throw new InvalidOperationException("RIO SDK interop layout mismatch.");
        }
        var id = new Guid("8509e081-96dd-4005-b165-9e2ee8c79e3f");
        var functions = new Functions { Size = (uint)sizeof(Functions) };
        if (Native.WSAIoctl(socket, Native.GetMultipleExtensions, &id, sizeof(Guid), &functions, sizeof(Functions), out _, null, 0) == -1)
        {
            throw new PlatformNotSupportedException("The selected Winsock provider does not expose registered I/O.", Native.SocketError("RIO extension lookup"));
        }
        _functions = functions;
    }

    internal BufferRegistration Register(NativeAllocation memory, int length)
    {
        var id = _functions.Register((void*)memory.DangerousGetHandle(), checked((uint)length));
        if ((nuint)id == uint.MaxValue)
        {
            throw Native.SocketError("RIORegisterBuffer");
        }
        return new BufferRegistration(this, memory, id);
    }

    internal CompletionQueueHandle CreateCompletionQueue(PortHandle port, NativeAllocation notificationMemory, uint capacity)
    {
        var notification = new Notification
        {
            Type = 2,
            Port = port.DangerousGetHandle(),
            Key = (nint)Native.RioKey,
            Overlapped = notificationMemory.DangerousGetHandle()
        };
        var queue = _functions.CreateCompletionQueue(capacity, &notification);
        if (queue == 0)
        {
            throw Native.SocketError("RIOCreateCompletionQueue");
        }
        return new CompletionQueueHandle(this, notificationMemory, queue);
    }

    internal nint CreateRequestQueue(SocketHandle socket, nint completionQueue, nuint connection)
    {
        var queue = _functions.CreateRequestQueue((nuint)socket.DangerousGetHandle(), 1, 1, 1, 1, completionQueue, completionQueue, connection);
        if (queue == 0)
        {
            throw Native.SocketError("RIOCreateRequestQueue");
        }
        return queue;
    }

    internal void Receive(nint queue, in Buffer buffer, nint request)
    {
        var value = buffer;
        if (_functions.Receive(queue, &value, 1, 0, request) == 0)
        {
            throw Native.SocketError("RIOReceive");
        }
    }

    internal void Send(nint queue, in Buffer buffer, nint request)
    {
        var value = buffer;
        if (_functions.Send(queue, &value, 1, 0, request) == 0)
        {
            throw Native.SocketError("RIOSend");
        }
    }

    internal int Dequeue(nint queue, Result* results, uint capacity)
    {
        var count = _functions.Dequeue(queue, results, capacity);
        if (count == uint.MaxValue)
        {
            throw new InvalidOperationException("RIO reported a corrupt completion queue.");
        }
        return checked((int)count);
    }

    internal void Notify(nint queue)
    {
        var error = _functions.Notify(queue);
        if (error != 0)
        {
            throw new SocketException(error) { Source = "RIONotify" };
        }
    }

    internal sealed class CompletionQueueHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly RioApi _api;
        private readonly NativeAllocation _notification;

        internal CompletionQueueHandle(RioApi api, NativeAllocation notification, nint value) : base(true)
        {
            _api = api;
            _notification = notification;
            SetHandle(value);
        }

        protected override bool ReleaseHandle()
        {
            _api._functions.CloseCompletionQueue(handle);
            GC.KeepAlive(_notification);
            return true;
        }
    }

    internal sealed class BufferRegistration : SafeHandle
    {
        private readonly RioApi _api;
        private readonly NativeAllocation _memory;

        internal BufferRegistration(RioApi api, NativeAllocation memory, nint value) : base(unchecked((nint)(nuint)uint.MaxValue), true)
        {
            _api = api;
            _memory = memory;
            SetHandle(value);
        }

        public override bool IsInvalid => (nuint)handle == uint.MaxValue;
        protected override bool ReleaseHandle()
        {
            _api._functions.Deregister(handle);
            GC.KeepAlive(_memory);
            return true;
        }
    }
}
