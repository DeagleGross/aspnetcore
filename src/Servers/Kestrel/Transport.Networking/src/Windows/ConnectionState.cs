// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

internal sealed class ConnectionState : IDisposable
{
    private readonly WindowsWorker _worker;
    private IMemoryOwner<byte>? _receiveOwner;
    private MemoryHandle _receivePin;
    private ReadOnlyMemory<byte> _sendMemory;
    private int _sendOffset, _leased;
    private bool _closing, _signalled, _readEof, _ready, _forgotten, _sendShutdown;
    private long _shutdownDeadline;
    private Exception? _error;

    internal nuint Id { get; }
    internal SocketHandle Socket { get; }
    internal nint RequestQueue { get; set; }
    internal IoOperation Read { get; }
    internal IoOperation Write { get; }
    internal Connection Application { get; }
    internal IPEndPoint LocalEndPoint { get; }
    internal IPEndPoint RemoteEndPoint { get; }
    internal bool CanForget => _forgotten && _signalled && _leased == 0;

    internal ConnectionState(WindowsWorker worker, nuint id, SocketHandle socket)
    {
        _worker = worker;
        Id = id;
        Socket = socket;
        LocalEndPoint = Native.GetEndPoint(socket, peer: false);
        RemoteEndPoint = Native.GetEndPoint(socket, peer: true);
        Read = new IoOperation(this, IoKind.Receive);
        Write = new IoOperation(this, IoKind.Send);
        Application = new Connection(worker, this);
    }

    internal unsafe void StartRead()
    {
        if (_closing || _readEof || Read.Outstanding || _leased >= 4)
        {
            return;
        }
        try
        {
            if (!_worker.IsRio && _worker.ZeroByteReads && !_ready)
            {
                _worker.SubmitRead(this, null, 0, probe: true);
                return;
            }
            _receiveOwner = _worker.InputPool.TryRent();
            if (_receiveOwner is null)
            {
                _worker.WaitForPool(this);
                return;
            }
            _receivePin = _receiveOwner.Memory.Pin();
            unsafe
            {
                _worker.SubmitRead(this, _receivePin.Pointer, _receiveOwner.Memory.Length, probe: false, _receiveOwner.Memory);
            }
            _ready = false;
        }
        catch (Exception error)
        {
            _receivePin.Dispose();
            _receiveOwner?.Dispose();
            _receiveOwner = null;
            Close(error, abortive: true);
        }
    }

    internal void Received(IoKind kind, int count, int error)
    {
        _receivePin.Dispose();
        var owner = _receiveOwner;
        _receiveOwner = null;
        if (_closing)
        {
            owner?.Dispose();
            TrySignalClosed();
            return;
        }
        if (error != 0)
        {
            owner?.Dispose();
            Close(new SocketException(error), abortive: true);
            return;
        }
        if (kind == IoKind.Probe)
        {
            _ready = true;
            StartRead();
            return;
        }
        if (owner is null || count < 0 || count > owner.Memory.Length)
        {
            throw new InvalidOperationException("Invalid native receive completion.");
        }
        if (count == 0)
        {
            owner.Dispose();
            _readEof = true;
            Application.Input.End(null);
            if (_sendShutdown)
            {
                Close(null, abortive: false);
            }
            return;
        }
        _leased++;
        _worker.Metrics.ReceivedBytes += count;
        _worker.Metrics.MaxLeasedPages = Math.Max(_worker.Metrics.MaxLeasedPages, _leased);
        if (_leased == 4)
        {
            _worker.Metrics.ReceiveBackpressure++;
        }
        Application.Input.Append(owner, count);
        StartRead();
    }

    internal void ReturnPage(IMemoryOwner<byte> owner)
    {
        if (_leased <= 0)
        {
            throw new InvalidOperationException("Input page ownership was returned twice.");
        }
        owner.Dispose();
        _leased--;
        StartRead();
        _worker.ResumePoolWaiters();
        TryForget();
    }

    internal void Send(ReadOnlyMemory<byte> memory)
    {
        if (_closing || _sendShutdown)
        {
            throw new IOException("The Windows connection is closing.");
        }
        if (Write.Outstanding || !_sendMemory.IsEmpty)
        {
            throw new InvalidOperationException("Only one output segment may be owned by native I/O.");
        }
        _sendMemory = memory;
        _sendOffset = 0;
        try
        {
            _worker.SubmitSend(this, memory);
        }
        catch
        {
            _sendMemory = default;
            throw;
        }
    }

    internal void Sent(int count, int error)
    {
        if (_sendMemory.IsEmpty)
        {
            throw new InvalidOperationException("Send completion has no owned output.");
        }
        if (error != 0 || _closing)
        {
            _sendMemory = default;
            Application.Sent(_error ?? TransportError(new SocketException(error == 0 ? Native.OperationAborted : error)));
            TrySignalClosed();
            return;
        }
        if (count <= 0 || count > _sendMemory.Length - _sendOffset)
        {
            _sendMemory = default;
            Application.Sent(new IOException($"Invalid native send completion: {count}."));
            return;
        }
        _sendOffset += count;
        _worker.Metrics.SentBytes += count;
        if (_sendOffset < _sendMemory.Length)
        {
            _worker.Metrics.PartialSends++;
            try
            {
                _worker.SubmitSend(this, _sendMemory[_sendOffset..]);
            }
            catch (Exception sendError)
            {
                _sendMemory = default;
                Application.Sent(sendError);
            }
            return;
        }
        _sendMemory = default;
        Application.Sent(null);
    }

    internal void Close(Exception? error, bool abortive)
    {
        if (_closing)
        {
            TrySignalClosed();
            return;
        }
        if (!abortive && error is null && !_sendShutdown)
        {
            try
            {
                Native.ShutdownSend(Socket);
                _sendShutdown = true;
                _shutdownDeadline = Environment.TickCount64 + 5000;
            }
            catch (SocketException shutdownError)
            {
                error = shutdownError;
            }
        }
        if (!abortive && error is null && !_readEof)
        {
            // Send completion releases memory, not the peer's receipt of the queued bytes.
            // Keep the RQ/socket alive after FIN until the peer closes its receive exchange.
            return;
        }
        error = TransportError(error);
        _closing = true;
        _error = error;
        if (error is not null)
        {
            _worker.Metrics.ConnectionErrors++;
            _worker.Logger.LogDebug(error, "Windows connection {ConnectionId} closed after an I/O error.", Id);
        }
        try
        {
            if (!_worker.IsRio)
            {
                Native.Cancel(Socket);
            }
        }
        catch (Exception closeError) when (closeError is SocketException or System.ComponentModel.Win32Exception)
        {
            _error ??= closeError;
            _worker.Logger.LogDebug(closeError, "Windows connection {ConnectionId} shutdown failed; closing its socket.", Id);
        }
        finally
        {
            // Closing a RIO socket also closes its RQ. Neither path releases operation memory yet.
            Socket.Dispose();
        }
        TrySignalClosed();
    }

    internal void CheckDeadline(long now)
    {
        if (_sendShutdown && !_closing && now >= _shutdownDeadline)
        {
            _worker.Metrics.GracefulTimeouts++;
            Close(new IOException("Peer did not close within five seconds after the Windows send shutdown."), abortive: true);
        }
    }

    private static Exception? TransportError(Exception? error)
    {
        return error switch
        {
            SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.Shutdown or SocketError.ConnectionAborted } socket
                => new ConnectionResetException(socket.Message, socket),
            SocketException { SocketErrorCode: SocketError.OperationAborted or SocketError.Interrupted } socket
                => new ConnectionAbortedException(socket.Message, socket),
            _ => error
        };
    }

    private void TrySignalClosed()
    {
        if (_closing && !_signalled && !Read.Outstanding && !Write.Outstanding)
        {
            _signalled = true;
            Application.NativeClosed(_error);
            TryForget();
        }
    }

    internal void Forget()
    {
        _forgotten = true;
        TryForget();
    }

    private void TryForget()
    {
        if (CanForget)
        {
            _worker.Forget(this);
        }
    }

    public void Dispose()
    {
        if (!CanForget || Read.Outstanding || Write.Outstanding || _receiveOwner is not null)
        {
            throw new InvalidOperationException("Connection disposal precedes native completion or input release.");
        }
        Read.Dispose();
        Write.Dispose();
        Socket.Dispose();
    }
}
