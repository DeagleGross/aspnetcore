// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

#pragma warning disable CA1416 // Created only by the Windows listener.

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

// Posts AcceptEx through the .NET I/O thread pool instead of a pinned worker's completion port. Accept syscalls then run on
// whichever pool thread dequeues the completion, so no connection worker carries the accept cost. Accepted sockets are
// not associated with any port yet; the adopting worker binds them to its own port exactly as before.
internal sealed unsafe class PoolAcceptor : IDisposable
{
    private const int OutstandingAccepts = 32;
    private static readonly IOCompletionCallback s_completed = OnCompleted;

    private readonly SocketHandle _listener;
    private readonly bool _rio;
    private readonly int _sendBufferSize;
    private readonly Action<SocketHandle> _dispatch;
    private readonly Action<Exception> _failed;
    private readonly ILogger _logger;
    private readonly ThreadPoolBoundHandle _bound;
    private readonly nint _function;
    private readonly Request[] _requests = new Request[OutstandingAccepts];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pending, _closing;
    private long _accepted, _rejected;

    internal PoolAcceptor(SocketHandle listener, bool rio, Action<SocketHandle> dispatch, Action<Exception> failed, ILogger logger)
    {
        _listener = listener;
        _rio = rio;
        _dispatch = dispatch;
        _failed = failed;
        _logger = logger;
        _sendBufferSize = int.Parse(Environment.GetEnvironmentVariable("NETWORKPROTO_WINDOWS_SNDBUF") ?? "0", CultureInfo.InvariantCulture);
        _function = Native.GetFunction(listener, new Guid("b5367df1-cbac-11cf-95ca-00805f48a192"));
        _bound = ThreadPoolBoundHandle.BindHandle(listener);
        for (var i = 0; i < _requests.Length; i++)
        {
            _requests[i] = new Request(this);
        }
    }

    internal Task Drained => _drained.Task;
    internal long Accepted => Interlocked.Read(ref _accepted);
    internal long Rejected => Interlocked.Read(ref _rejected);

    internal void Start()
    {
        foreach (var request in _requests)
        {
            Submit(request);
        }
    }

    internal void Stop()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }
        Native.Cancel(_listener);
        _listener.Dispose();
        if (Volatile.Read(ref _pending) == 0)
        {
            _drained.TrySetResult();
        }
    }

    private void Submit(Request request)
    {
        if (Volatile.Read(ref _closing) != 0)
        {
            return;
        }
        var socket = Native.CreateSocket(_rio);
        var overlapped = _bound.AllocateNativeOverlapped(request.Preallocated);
        var referenced = false;
        // Count before posting: the completion can run on another thread before AcceptEx returns.
        Interlocked.Increment(ref _pending);
        try
        {
            _listener.DangerousAddRef(ref referenced);
            request.Socket = socket;
            uint bytes = 0;
            var function = (delegate* unmanaged[Stdcall]<nuint, nuint, void*, uint, uint, uint, uint*, NativeOverlapped*, int>)_function;
            var result = function((nuint)_listener.DangerousGetHandle(), (nuint)socket.DangerousGetHandle(),
                (void*)request.Address.DangerousGetHandle(), 0, 32, 32, &bytes, overlapped);
            if (result == 0 && Native.WSAGetLastError() != Native.IoPending)
            {
                throw Native.SocketError("AcceptEx");
            }
        }
        catch
        {
            request.Socket = null;
            socket.Dispose();
            _bound.FreeNativeOverlapped(overlapped);
            Release();
            throw;
        }
        finally
        {
            if (referenced)
            {
                _listener.DangerousRelease();
            }
        }
    }

    private static void OnCompleted(uint error, uint bytes, NativeOverlapped* overlapped)
    {
        var request = (Request)ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped)!;
        request.Owner.Complete(request, overlapped, error);
    }

    private void Complete(Request request, NativeOverlapped* overlapped, uint error)
    {
        _bound.FreeNativeOverlapped(overlapped);
        var socket = request.Socket;
        request.Socket = null;
        var closing = Volatile.Read(ref _closing) != 0;
        if (!closing)
        {
            if (error == 0 && socket is not null)
            {
                try
                {
                    Native.SetAcceptContext(socket, _listener);
                    if (_sendBufferSize > 0)
                    {
                        Native.SetSendBuffer(socket, _sendBufferSize);
                    }
                    _dispatch(socket);
                    socket = null;
                    Interlocked.Increment(ref _accepted);
                }
                catch (SocketException acceptError)
                {
                    _logger.LogDebug(acceptError, "Accepted peer closed before socket initialization.");
                    Interlocked.Increment(ref _rejected);
                }
            }
            else
            {
                _logger.LogDebug("AcceptEx failed with error {Error}.", error);
                Interlocked.Increment(ref _rejected);
            }
        }
        socket?.Dispose();
        try
        {
            Submit(request);
        }
        catch (Exception submitError) when (Volatile.Read(ref _closing) == 0)
        {
            _failed(submitError);
        }
        catch (Exception)
        {
            // The listener closed while re-posting; the shutdown path owns the remaining requests.
        }
        finally
        {
            Release();
        }
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _pending) == 0 && Volatile.Read(ref _closing) != 0)
        {
            _drained.TrySetResult();
        }
    }

    public void Dispose()
    {
        foreach (var request in _requests)
        {
            request.Dispose();
        }
        _bound.Dispose();
    }

    private sealed class Request : IDisposable
    {
        internal Request(PoolAcceptor owner)
        {
            Owner = owner;
            Preallocated = PreAllocatedOverlapped.UnsafeCreate(s_completed, this, pinData: null);
        }

        internal PoolAcceptor Owner { get; }
        internal PreAllocatedOverlapped Preallocated { get; }
        internal NativeAllocation Address { get; } = new(64);
        internal SocketHandle? Socket { get; set; }

        public void Dispose()
        {
            Preallocated.Dispose();
            Socket?.Dispose();
            Address.Dispose();
        }
    }
}
