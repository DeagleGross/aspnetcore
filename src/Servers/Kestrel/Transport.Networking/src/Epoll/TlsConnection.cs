// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll;

// Only the owning Engine thread mutates TLS and readiness state.
internal sealed class TlsConnection(Engine engine, ulong id, int fd, nint session)
{
    private int _fd = fd;
    private nint _session = session;
    private bool _registered, _handshake, _closeRequested, _readPaused, _pending, _finalSend;
    private int _readPage = -1, _sendLength, _sendOffset;
    private nint _sendData;
    private uint _interest, _handshakeWant, _readWant, _writeWant, _shutdownWant;
    private long _deadline = Environment.TickCount64 + 10000;
    internal ulong Id { get; } = id;
    internal bool Closed { get; private set; }
    internal int Leased { get; private set; }
    internal Connection? Application { get; private set; }

    internal void Drive(uint ready)
    {
        if (Closed)
        {
            return;
        }
        try
        {
            if (_closeRequested)
            {
                return;
            }
            if (!_handshake)
            {
                if (_handshakeWant != 0 && (ready & _handshakeWant) == 0)
                {
                    return;
                }
                if (!ApplyResult(Call(Native.TlsOperation.Handshake), ref _handshakeWant))
                {
                    return;
                }
                _handshake = true;
                _deadline = 0;
                _readWant = Native.Readable;
                engine.Metrics.Handshakes++;
                Application = engine.Publish(this);
            }
            if (!_readPaused && _readWant == Native.Writable)
            {
                if ((ready & Native.Writable) == 0)
                {
                    return;
                }
                Read();
                if (Closed || _closeRequested || _readWant == Native.Writable)
                {
                    return;
                }
            }
            if (_sendData != 0 && (_writeWant == 0 || (ready & _writeWant) != 0))
            {
                Write();
                if (Closed || _closeRequested)
                {
                    return;
                }
            }
            if (!_readPaused && ((ready & _readWant) != 0 || _pending))
            {
                Read();
            }
        }
        finally
        {
            if (_closeRequested && !Closed)
            {
                Shutdown(ready);
            }
            UpdateInterest();
        }
    }

    internal void Send(nint data, int length, bool final)
    {
        if (Closed || _closeRequested)
        {
            Application!.Sent(-125);
            return;
        }
        if (_sendData != 0 || data == 0 || length <= 0)
        {
            throw new InvalidOperationException("Invalid concurrent epoll send.");
        }
        _sendData = data;
        _sendLength = length;
        _sendOffset = 0;
        _writeWant = 0;
        _finalSend = final;
        if (final && engine.CorkFinal)
        {
            Native.Check(Native.SetSocketOption(_fd, Native.SocketOption.Cork, 1), "TCP_CORK");
            engine.Metrics.Corks++;
        }
        Drive(0);
    }

    private void Write()
    {
        for (var i = 0; i < 16 && _sendData != 0; i++)
        {
            var remaining = Math.Min(_sendLength - _sendOffset, Engine.PageSize);
            engine.Metrics.SslWrites++;
            var result = Call(Native.TlsOperation.Write, _sendData + _sendOffset, remaining);
            if (!ApplyResult(result, ref _writeWant))
            {
                if (result.Status is Native.TlsStatus.WantRead or Native.TlsStatus.WantWrite)
                {
                    engine.Metrics.WriteRetries++;
                }
                return;
            }
            engine.Metrics.WriteSuccess++;
            _sendOffset += result.Length;
            if (_sendOffset == _sendLength)
            {
                _sendData = 0;
                Application!.Sent(_sendLength);
                _closeRequested |= _finalSend;
            }
            else
            {
                _writeWant = Native.Writable;
            }
        }
    }

    private unsafe void Read()
    {
        while (Leased < 4)
        {
            if (_readPage < 0)
            {
                _readPage = engine.RentPage(Id);
                if (_readPage < 0)
                {
                    break;
                }
            }
            engine.Metrics.SslReads++;
            Native.TlsResult result;
            fixed (byte* pointer = engine.Page(_readPage).Span)
            {
                result = Call(Native.TlsOperation.Read, (nint)pointer, Engine.PageSize);
            }
            if (!ApplyResult(result, ref _readWant))
            {
                return;
            }
            engine.Metrics.ReadSuccess++;
            Leased++;
            engine.Received(Application!, _readPage, result.Length);
            _readPage = -1;
            _readWant = Native.Readable;
            if (!_pending && Leased < 4)
            {
                return;
            }
        }
        _readPaused = true;
        engine.Metrics.ReadPauses++;
    }

    private Native.TlsResult Call(Native.TlsOperation operation, nint data = 0, int length = 0)
    {
        Native.Tls(_session, operation, data, length, out var result);
        if (result.Status == Native.TlsStatus.Complete)
        {
            _pending = result.Pending != 0;
        }
        return result;
    }

    private bool ApplyResult(Native.TlsResult result, ref uint want)
    {
        switch (result.Status)
        {
            case Native.TlsStatus.Complete:
                want = 0;
                return true;
            case Native.TlsStatus.WantRead:
                want = Native.Readable;
                engine.Metrics.WantRead++;
                return false;
            case Native.TlsStatus.WantWrite:
                want = Native.Writable;
                engine.Metrics.WantWrite++;
                return false;
            case Native.TlsStatus.Closed:
                Application?.Input.End(null);
                _closeRequested = true;
                return false;
            case Native.TlsStatus.PeerAbort:
                engine.Metrics.PeerAborts++;
                Finish(new ConnectionResetException("Epoll TLS peer disconnected."));
                return false;
            default:
                engine.Metrics.TlsErrors++;
                var error = new IOException($"OpenSSL operation failed: status={result.Status}, errno={result.Error}, reason=0x{result.Reason:x}.");
                engine.Logger.LogError(error, "Epoll TLS connection failed.");
                Finish(error);
                return false;
        }
    }

    internal void RequestClose()
    {
        _closeRequested = true;
        Drive(Native.Readable | Native.Writable);
    }

    private void Shutdown(uint ready)
    {
        if (Closed)
        {
            return;
        }
        if (!_handshake || _sendData != 0)
        {
            Finish(new ConnectionAbortedException("Epoll closed with incomplete TLS output."));
            return;
        }
        if (_deadline == 0)
        {
            _deadline = Environment.TickCount64 + 5000;
        }
        if (_shutdownWant != 0 && (ready & _shutdownWant) == 0)
        {
            return;
        }
        var result = Call(Native.TlsOperation.Shutdown);
        if (result.Status == Native.TlsStatus.Complete)
        {
            engine.Metrics.CloseNotify++;
            Finish(null);
        }
        else if (!ApplyResult(result, ref _shutdownWant) && result.Status == Native.TlsStatus.Closed)
        {
            engine.Metrics.ShutdownFailures++;
            Finish(new IOException("TLS shutdown ended before close_notify was written."));
        }
    }

    private void UpdateInterest()
    {
        if (Closed)
        {
            return;
        }
        var mask = !_handshake ? _handshakeWant
            : _closeRequested ? _shutdownWant
            : (_readPaused ? 0 : _readWant) | (_sendData != 0 ? _writeWant : 0);
        if (mask == 0)
        {
            if (_registered)
            {
                engine.Control(Native.Delete, _fd, 0, Id);
                _registered = false;
            }
        }
        else if (!_registered || _interest != mask)
        {
            engine.Control(_registered ? Native.Modify : Native.Add, _fd, mask, Id);
            _registered = true;
            _interest = mask;
        }
    }

    internal void ReturnPage(int page)
    {
        if (Leased <= 0)
        {
            throw new InvalidOperationException("Epoll page lease returned twice.");
        }
        engine.OfferPage(Id, page);
        Leased--;
        engine.Metrics.ReturnedPages++;
        ResumeReader();
    }

    internal void ResumeForPool()
    {
        if (!Closed && _readPaused && Leased < 4)
        {
            engine.Metrics.PageResumes++;
            ResumeReader();
        }
    }

    private void ResumeReader()
    {
        if (!Closed && _readPaused && Leased < 4)
        {
            _readPaused = false;
            _readWant = Native.Readable;
            Drive(Native.Readable);
        }
    }

    internal void CheckDeadline(long now)
    {
        if (!Closed && _deadline != 0 && now >= _deadline)
        {
            if (_handshake)
            {
                engine.Metrics.ShutdownTimeouts++;
            }
            else
            {
                engine.Metrics.HandshakeTimeouts++;
            }
            Finish(new TimeoutException("Epoll TLS handshake or shutdown timed out."));
        }
    }

    internal void Finish(Exception? error)
    {
        if (Closed)
        {
            return;
        }
        Closed = true;
        try
        {
            if (_registered)
            {
                engine.Control(Native.Delete, _fd, 0, Id);
            }
        }
        finally
        {
            _registered = false;
            if (_readPage >= 0)
            {
                engine.OfferPage(Id, _readPage);
                _readPage = -1;
            }
            var result = Native.Shutdown(_fd);
            if (result == 0 && _finalSend && error is null)
            {
                engine.Metrics.FinalShutdowns++;
            }
            else if (result == Native.NotConnected)
            {
                engine.Metrics.NotConnected++;
            }
            else if (result < 0)
            {
                engine.Metrics.ShutdownFailures++;
                engine.Logger.LogError("Epoll socket shutdown failed: {Error}", result);
            }
            engine.CloseDescriptor(ref _fd);
            Native.FreeSession(_session);
            _session = 0;
            _sendData = 0;
            engine.Metrics.Closed++;
            if (Application is { } application)
            {
                application.Closed(error);
            }
            else
            {
                engine.RemoveUnpublished(Id);
            }
        }
    }
}
