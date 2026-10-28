// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

internal sealed class ConnectionState : IDisposable
{
    private readonly WindowsWorker _worker;
    private readonly IWindowsTlsConnection? _tls;
    private IMemoryOwner<byte>? _receiveOwner;
    private MemoryHandle _receivePin;
    private ReadOnlyMemory<byte> _sendMemory;
    private nint _sendAddress;
    private int _sendOffset, _leased;
    private bool _closing, _signalled, _readEof, _ready, _forgotten, _sendShutdown;
    private long _shutdownDeadline;
    private Exception? _error;
    private bool _tlsShutdownFinished, _published, _disposingUnpublished;
    private readonly long _handshakeDeadline = Environment.TickCount64 + 10000;
    private long _outputQueuedAt;

    internal nuint Id { get; }
    internal SocketHandle Socket { get; }
    internal nint RequestQueue { get; set; }
    internal IoOperation Read { get; }
    internal IoOperation Write { get; }
    internal IProviderConnection Application { get; }
    internal IPEndPoint LocalEndPoint { get; }
    internal IPEndPoint RemoteEndPoint { get; }
    internal bool CanForget => _forgotten && _signalled && _leased == 0;
    internal bool HasTls => _tls is not null;
    internal bool Closing => _closing;
    internal int Leased => _leased;
    internal bool Published { get => _published; set => _published = value; }

    internal string Describe(long now)
    {
        var age = now - (_handshakeDeadline - 10000);
        return $"tls={(_tls is null ? "-" : _tls.Authenticated ? "done" : _tls.HandshakePending ? "offload" : "pending")} pub={(_published ? 1 : 0)} rd={(Read.Outstanding ? 1 : 0)} wr={(Write.Outstanding ? 1 : 0)} lease={_leased} buf={(_tls is { BufferedCiphertext: > 0 } ? "Y" : "0")} closing={(_closing ? 1 : 0)} eof={(_readEof ? 1 : 0)} age={(age < 1000 ? "<1s" : age < 5000 ? "<5s" : ">=5s")}";
    }

    internal void OutputQueued()
    {
        if (HasTls && _worker.TlsTiming)
        {
            Interlocked.CompareExchange(ref _outputQueuedAt, Stopwatch.GetTimestamp(), 0);
        }
    }

    internal void OutputDequeued()
    {
        if (HasTls && _worker.TlsTiming && Interlocked.Exchange(ref _outputQueuedAt, 0) is var queued && queued != 0)
        {
            var delay = Stopwatch.GetTimestamp() - queued;
            _worker.Metrics.TlsOutputQueueTicks += delay;
            _worker.Metrics.TlsOutputQueueRuns++;
            _worker.Metrics.TlsOutputQueueMaxTicks = Math.Max(_worker.Metrics.TlsOutputQueueMaxTicks, delay);
        }
    }

    internal ConnectionState(WindowsWorker worker, nuint id, SocketHandle socket)
    {
        _worker = worker;
        Id = id;
        Socket = socket;
        LocalEndPoint = Native.GetEndPoint(socket, peer: false);
        RemoteEndPoint = Native.GetEndPoint(socket, peer: true);
        Read = new IoOperation(this, IoKind.Receive);
        Write = new IoOperation(this, IoKind.Send);
        _tls = worker.TlsSession is { } session ? new TlsSessionConnection(worker, this, session)
            : worker.TlsCredential is { } credential ? new SchannelConnection(worker, this, credential) : null;
        Application = worker.Shared ? ProviderNetworkConnection.ForWindows(worker, this) : new Connection(worker, this);
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
            Memory<byte> memory;
            if (_tls is not null)
            {
                memory = _tls.GetReceiveMemory();
                if (memory.IsEmpty)
                {
                    return;
                }
            }
            else
            {
                _receiveOwner = _worker.InputPool.TryRent();
                if (_receiveOwner is null)
                {
                    _worker.WaitForPool(this);
                    return;
                }
                memory = _receiveOwner.Memory;
            }
            _receivePin = memory.Pin();
            unsafe
            {
                _worker.SubmitRead(this, _receivePin.Pointer, memory.Length, probe: false, memory);
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
        if (_tls is not null)
        {
            try
            {
                if (count == 0)
                {
                    _readEof = true;
                    _tls.PeerEof();
                    if (_sendShutdown)
                    {
                        Close(null, abortive: false);
                    }
                }
                else
                {
                    _worker.Metrics.ReceivedBytes += count;
                    _tls.Received(count);
                    StartRead();
                }
            }
            catch (Exception tlsError)
            {
                Close(tlsError, abortive: true);
            }
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
            Application.EndReceive(null);
            if (_sendShutdown)
            {
                Close(null, abortive: false);
            }
            return;
        }
        _worker.Metrics.ReceivedBytes += count;
        PublishPlaintext(owner, count);
        StartRead();
    }

    internal void PublishPlaintext(IMemoryOwner<byte> owner, int count)
    {
        _leased++;
        _worker.Metrics.MaxLeasedPages = Math.Max(_worker.Metrics.MaxLeasedPages, _leased);
        if (_leased == 4)
        {
            _worker.Metrics.ReceiveBackpressure++;
        }
        if (Application is ProviderNetworkConnection shared)
        {
            shared.Receive(owner, count, _worker.IsRio ? NetworkMemoryKind.Registered : NetworkMemoryKind.Native);
        }
        else
        {
            ((Connection)Application).Input.Append(owner, count);
        }
    }

    internal void ReturnPage(IMemoryOwner<byte> owner)
    {
        if (_leased <= 0)
        {
            throw new InvalidOperationException("Input page ownership was returned twice.");
        }
        owner.Dispose();
        _leased--;
        try
        {
            _tls?.ResumeInput();
        }
        catch (Exception error)
        {
            Close(error, abortive: true);
        }
        StartRead();
        _worker.ResumePoolWaiters();
        TryForget();
    }

    internal void Send(ReadOnlyMemory<byte> memory, nint pinnedAddress = 0, bool final = false)
    {
        if (_tls is not null)
        {
            _tls.Send(memory, final);
        }
        else
        {
            SendCiphertext(memory, pinnedAddress);
        }
    }

    internal void SendCiphertext(ReadOnlyMemory<byte> memory, nint pinnedAddress = 0)
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
        _sendAddress = pinnedAddress;
        _sendOffset = 0;
        try
        {
            _worker.SubmitSend(this, memory, _sendAddress);
        }
        catch
        {
            _sendMemory = default;
            _sendAddress = 0;
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
            _sendAddress = 0;
            CompleteSend(-1, _error ?? TransportError(new SocketException(error == 0 ? Native.OperationAborted : error)));
            TrySignalClosed();
            return;
        }
        if (count <= 0 || count > _sendMemory.Length - _sendOffset)
        {
            _sendMemory = default;
            _sendAddress = 0;
            CompleteSend(-1, new IOException($"Invalid native send completion: {count}."));
            return;
        }
        _sendOffset += count;
        _worker.Metrics.SentBytes += count;
        if (_sendOffset < _sendMemory.Length)
        {
            _worker.Metrics.PartialSends++;
            try
            {
                _worker.SubmitSend(this, _sendMemory[_sendOffset..], _sendAddress == 0 ? 0 : _sendAddress + _sendOffset);
            }
            catch (Exception sendError)
            {
                _sendMemory = default;
                _sendAddress = 0;
                CompleteSend(-1, sendError);
            }
            return;
        }
        var completed = _sendMemory.Length;
        _sendMemory = default;
        _sendAddress = 0;
        CompleteSend(completed, null);
    }

    private void CompleteSend(int count, Exception? error)
    {
        if (_tls is not null)
        {
            try
            {
                _tls.Sent(error);
                if (error is not null)
                {
                    Close(error, abortive: true);
                }
            }
            catch (Exception tlsError)
            {
                Close(tlsError, abortive: true);
            }
        }
        else
        {
            Application.Sent(count, error);
        }
    }

    internal void FinishTlsShutdown()
    {
        _tlsShutdownFinished = true;
        Close(null, abortive: false);
    }

    internal void HandshakeCompleted()
    {
        try
        {
            _tls!.HandshakeCompleted();
            StartRead();
        }
        catch (Exception error)
        {
            Close(error, abortive: true);
        }
        finally
        {
            TrySignalClosed();
        }
    }

    internal void Close(Exception? error, bool abortive)
    {
        if (_closing)
        {
            TrySignalClosed();
            return;
        }
        if (_tls is { Authenticated: false } && !abortive && error is null)
        {
            error = new ConnectionAbortedException("The TLS connection closed before authentication completed.");
            abortive = true;
        }
        if (_tls is not null && !_tlsShutdownFinished && !abortive && error is null)
        {
            try
            {
                _shutdownDeadline = Environment.TickCount64 + 5000;
                _tls.Shutdown();
            }
            catch (Exception tlsError)
            {
                Close(tlsError, abortive: true);
            }
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
        _tls?.CancelHandshake();
        if (error is not null)
        {
            _worker.Metrics.ConnectionErrors++;
            _worker.Metrics.FirstConnectionError ??= error.GetBaseException().Message;
            _worker.Logger.LogDebug(error, "Windows connection {ConnectionId} closed after an I/O error.", Id);
        }
        try
        {
            if (_worker.IsRio && abortive && Write.Outstanding)
            {
                // A graceful closesocket can keep a window-blocked RIO send alive.
                Native.SetAbortiveLinger(Socket);
            }
            else if (!_worker.IsRio)
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
        if (!_published && !_disposingUnpublished)
        {
            _disposingUnpublished = true;
            _worker.DisposeUnpublished(Application);
        }
    }

    internal void CheckDeadline(long now)
    {
        if (!_closing && _tls is { Authenticated: false } && now >= _handshakeDeadline)
        {
            Close(new IOException("Native TLS handshake did not finish within ten seconds."), abortive: true);
        }
        else if (_shutdownDeadline != 0 && !_closing && now >= _shutdownDeadline)
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
        if (_closing && !_signalled && !Read.Outstanding && !Write.Outstanding && _tls is not { HandshakePending: true })
        {
            _signalled = true;
            _tls?.NativeClosed(_error);
            Application.Closed(_error);
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
        _tls?.Dispose();
        Socket.Dispose();
    }
}
