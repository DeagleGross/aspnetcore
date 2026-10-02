// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll;

internal sealed class TcpConnection(EpollWorker engine, ulong id, int fd) : ConnectionState(id)
{
    private const int WriteBudget = 256 * 1024;
    private int _fd = fd, _sendLength, _sendOffset;
    private nint _sendData;
    private uint _interest;
    private bool _registered, _readPaused, _readEnded, _finalSend;

    internal override void Drive(uint ready)
    {
        if (Closed)
        {
            return;
        }
        Application ??= engine.Publish(this);
        if (_sendData != 0 && (ready & Native.Writable) != 0)
        {
            Write();
        }
        if (!Closed && !_readPaused && !_readEnded && (ready & Native.Readable) != 0)
        {
            Read();
        }
        UpdateInterest();
    }

    internal override void Send(nint data, int length, bool final)
    {
        if (Closed)
        {
            Application!.Sent(-125);
            return;
        }
        if (_sendData != 0 || data == 0 || length <= 0)
        {
            throw new InvalidOperationException("Invalid concurrent epoll TCP send.");
        }
        _sendData = data;
        _sendLength = length;
        _sendOffset = 0;
        _finalSend = final;
        if (final && engine.CorkFinal)
        {
            Native.Check(Native.SetSocketOption(_fd, Native.SocketOption.Cork, 1), "TCP_CORK");
            engine.Metrics.Corks++;
        }
        Write();
        UpdateInterest();
    }

    private void Write()
    {
        var budget = WriteBudget;
        while (_sendData != 0 && budget > 0)
        {
            var length = Math.Min(_sendLength - _sendOffset, budget);
            engine.Metrics.SendCalls++;
            var count = Native.Send(_fd, _sendData + _sendOffset, length);
            if (count == Native.Again)
            {
                engine.Metrics.SendWouldBlock++;
                return;
            }
            if (count <= 0)
            {
                SocketError(count == 0 ? Native.BrokenPipe : count, "send");
                return;
            }
            if (count < length)
            {
                engine.Metrics.PartialSends++;
            }
            engine.Metrics.SendBytes += count;
            _sendOffset += count;
            budget -= count;
            if (_sendOffset == _sendLength)
            {
                _sendData = 0;
                Application!.Sent(_sendLength);
                if (_finalSend)
                {
                    Finish(null);
                }
            }
        }
    }

    private unsafe void Read()
    {
        while (Leased < 4)
        {
            var page = engine.RentPage(Id);
            if (page < 0)
            {
                break;
            }
            int count;
            engine.Metrics.RecvCalls++;
            fixed (byte* pointer = engine.Page(page).Span)
            {
                count = Native.Receive(_fd, (nint)pointer, EpollWorker.PageSize);
            }
            if (count <= 0)
            {
                engine.OfferPage(Id, page);
                if (count == Native.Again)
                {
                    engine.Metrics.ReceiveWouldBlock++;
                }
                else if (count == 0)
                {
                    // A TCP half-close ends input, not the response still being produced.
                    _readEnded = true;
                    engine.Metrics.ReadEofs++;
                    Application!.Input.End(null);
                }
                else
                {
                    SocketError(count, "recv");
                }
                return;
            }
            engine.Metrics.RecvBytes += count;
            Leased++;
            engine.Received(Application!, page, count);
            // Level-triggered epoll will report any remaining input. Do not make
            // another usually-empty recv after the common small request.
            if (count < EpollWorker.PageSize && Leased < 4)
            {
                return;
            }
        }
        _readPaused = true;
        engine.Metrics.ReadPauses++;
    }

    private void UpdateInterest()
    {
        if (Closed)
        {
            return;
        }
        var mask = (_readPaused || _readEnded ? 0 : Native.Readable) | (_sendData != 0 ? Native.Writable : 0);
        if (mask == 0)
        {
            if (_registered)
            {
                engine.Control(Native.Delete, _fd, 0, Id);
                _registered = false;
            }
        }
        else if (!_registered || mask != _interest)
        {
            engine.Control(_registered ? Native.Modify : Native.Add, _fd, mask, Id);
            _registered = true;
            _interest = mask;
        }
    }

    internal override void ReturnPage(int page)
    {
        if (Leased <= 0)
        {
            throw new InvalidOperationException("Epoll TCP page lease returned twice.");
        }
        engine.OfferPage(Id, page);
        Leased--;
        engine.Metrics.ReturnedPages++;
        ResumeReader();
    }

    internal override void ResumeForPool()
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
            Drive(Native.Readable);
        }
    }

    private void SocketError(int result, string operation)
    {
        Exception error;
        if (result is Native.ConnectionReset or Native.BrokenPipe)
        {
            engine.Metrics.PeerAborts++;
            error = new ConnectionResetException($"Epoll TCP peer disconnected during {operation}: {result}.");
        }
        else
        {
            engine.Metrics.SocketErrors++;
            error = new IOException($"Epoll TCP {operation} failed.", new Win32Exception(-result));
            engine.Logger.LogError(error, "Epoll TCP operation failed.");
        }
        Finish(error);
    }

    internal override void RequestClose()
    {
        Finish(_sendData == 0 ? null : new ConnectionAbortedException("Epoll TCP closed with pending output."));
    }

    internal override void Finish(Exception? error)
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
                engine.Logger.LogError("Epoll TCP shutdown failed: {Error}", result);
            }
            engine.CloseDescriptor(ref _fd);
            _sendData = 0;
            engine.Metrics.Closed++;
            Application!.Closed(error);
        }
    }
}
