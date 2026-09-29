// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Microsoft.Extensions.Logging;
using BioNative = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringBio.Native;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp;

// Pump-owned state. Multishot terminal CQEs and cancellation acknowledgments own separate references.
internal sealed class RingConnection(Engine engine, ulong id, int fd)
{
    internal enum Operation
    {
        Read = 1, Write, CancelRead, CancelWrite, FinalShutdown
    }
    internal ulong Id { get; } = id;
    internal Connection? Application
    {
        get; private set;
    }
    private int _fd = fd, _operations, _leased, _readPage = -1;
    private bool _closing, _done, _handshake, _fatal, _closeRequested, _readBackpressured;
    private bool _readActive, _writeActive, _cancelRead, _cancelWrite, _finActive, _finComplete;
    private bool _finalSend, _tlsWriteDone, _shutdownSent, _peerEof, _pending;
    private nint _session, _bio, _sendData;
    private int _sendLength, _sendOffset;
    private readonly Queue<(int Page, int Length)> _cipher = new();
    private int _bioInputPage = -1, _bioInputLength, _cipherBytes;
    private BioNative.Status _bioStatus;
    private ulong Token(Operation operation) => (Id << 4) | (uint)operation;

    internal void Start()
    {
        Native.Check(Native.Option(_fd, 0, 1), "TCP_NODELAY");
        if (engine.SendBufferSize > 0)
        {
            Native.Check(Native.Option(_fd, 2, engine.SendBufferSize), "SO_SNDBUF");
        }

        if (engine.Tls)
        {
            _session = engine.BioTls
                ? BioNative.Session(engine.Context, engine.BioMethod, out _bio)
                : Native.Session(engine.Context, _fd);
            if (_session == 0)
            {
                Fail(new IOException("OpenSSL session creation failed."));
                return;
            }
            DriveTls();
        }
        else
        {
            Application = engine.Publish(this);
            StartRead();
        }
    }

    internal void Send(nint data, int length, bool final)
    {
        if (_closing || _closeRequested)
        {
            Application!.Sent(Native.Canceled);
            return;
        }
        if (_sendData != 0 || data == 0 || length <= 0)
        {
            throw new InvalidOperationException("Invalid concurrent send.");
        }

        _sendData = data;
        _sendLength = length;
        _sendOffset = 0;
        _finalSend = final;
        if (final && engine.FinalMode == 3 && (engine.Tls || engine.Layered))
        {
            Native.Check(Native.Option(_fd, 1, 1), "TCP_CORK");
            engine.Metrics.Corks++;
        }
        if (engine.Tls)
        {
            DriveTls();
        }
        else
        {
            StartWrite();
        }
    }

    private void StartRead()
    {
        if (engine.Tls && _leased >= 4)
        {
            _readBackpressured = true;
        }

        if (_closing || _readActive || _cancelRead || _leased >= 4
            || (engine.BioTls && (_closeRequested || _peerEof || _cipherBytes >= 65536)))
        {
            return;
        }

        engine.Prepare(engine.Tls && !engine.Layered ? Native.Operation.ReadPoll : Native.Operation.Receive,
            _fd, 0, 0, Token(Operation.Read));
        _readActive = true;
        _operations++;
    }

    private void StartWrite()
    {
        if (_closing || _writeActive)
        {
            return;
        }

        if (engine.BioTls)
        {
            if (_bioStatus.OutputRemaining == 0)
            {
                throw new InvalidOperationException("Missing BIO output.");
            }

            engine.Prepare(Native.Operation.Send, _fd, _bioStatus.Output, _bioStatus.OutputRemaining, Token(Operation.Write));
        }
        else if (engine.Tls)
        {
            engine.Prepare(Native.Operation.WritePoll, _fd, 0, 0, Token(Operation.Write));
        }
        else
        {
            var linked = _finalSend && !engine.Layered && engine.FinalMode >= 2;
            engine.Prepare(linked ? Native.Operation.LinkedSend : Native.Operation.Send,
                _fd, _sendData + _sendOffset, _sendLength - _sendOffset, Token(Operation.Write),
                linked ? Token(Operation.FinalShutdown) : 0, engine.FinalMode == 3 ? 1 : 0);
            if (linked)
            {
                _finActive = true;
                _operations++;
                engine.Metrics.Links++;
            }
        }
        _writeActive = true;
        _operations++;
    }

    private void Cancel(Operation target, Operation acknowledgment)
    {
        engine.Prepare(Native.Operation.Cancel, 0, 0, 0, Token(acknowledgment), Token(target));
        _operations++;
    }

    internal void Complete(Operation operation, int result, uint flags)
    {
        var terminal = (flags & Native.More) == 0;
        switch (operation)
        {
            case Operation.CancelRead:
                _operations--;
                _cancelRead = false;
                if (!_closing)
                {
                    StartRead();
                }

                break;
            case Operation.CancelWrite:
                _operations--;
                _cancelWrite = false;
                break;
            case Operation.Read:
                if (terminal)
                {
                    _readActive = false;
                    _operations--;
                }
                if ((flags & Native.Buffer) != 0)
                {
                    var page = (int)(flags >> 16);
                    engine.ClaimPage(page, engine.BioTls);
                    if (result > Engine.PageSize)
                    {
                        throw new InvalidOperationException("Invalid receive length.");
                    }

                    if (result > 0 && !_closing && !(engine.BioTls && _closeRequested))
                    {
                        if (engine.BioTls)
                        {
                            _cipher.Enqueue((page, result));
                            _cipherBytes += result;
                        }
                        else
                        {
                            _leased++;
                            engine.Received(Application!, page, result);
                        }
                    }
                    else
                    {
                        engine.OfferPage(page, engine.BioTls);
                    }
                }
                if (!_closing)
                {
                    if (result < 0 && result is not (Native.Canceled or Native.NoBuffers))
                    {
                        Fail(new IOException($"io_uring receive failed: {result}."));
                    }
                    else if (result == 0 && !(engine.Tls && !engine.Layered))
                    {
                        _peerEof = true;
                        if (engine.BioTls)
                        {
                            DriveTls();
                        }
                        else
                        {
                            Application!.Input.End(null);
                        }
                    }
                    else if (engine.Tls && result >= 0)
                    {
                        DriveTls();
                    }
                    else
                    {
                        StartRead();
                    }

                    if ((!engine.Tls || engine.BioTls) && (_leased >= 4 || _cipherBytes >= 65536) && _readActive && !_cancelRead)
                    {
                        _cancelRead = true;
                        Cancel(Operation.Read, Operation.CancelRead);
                    }
                }
                break;
            case Operation.Write:
                _writeActive = false;
                _operations--;
                if (engine.Tls && !engine.Layered)
                {
                    if (!_closing)
                    {
                        if (result < 0)
                        {
                            Fail(new IOException($"TLS write readiness failed: {result}."));
                        }
                        else
                        {
                            DriveTls();
                        }
                    }
                }
                else if (engine.BioTls)
                {
                    if (!_closing)
                    {
                        if (result <= 0)
                        {
                            Fail(new IOException($"Ciphertext send failed: {result}."));
                        }
                        else
                        {
                            BioNative.Advance(_bio, result);
                            RefreshBio();
                            if (_bioStatus.OutputRemaining > 0)
                            {
                                engine.Metrics.PartialCipherSends++;
                                StartWrite();
                            }
                            else
                            {
                                DriveTls();
                            }
                        }
                    }
                }
                else if (_sendData != 0)
                {
                    if (result > 0 && !_closing)
                    {
                        _sendOffset += result;
                        if (_sendOffset < _sendLength)
                        {
                            engine.Metrics.PartialSends++;
                            if (!_finActive)
                            {
                                StartWrite();
                            }
                        }
                        else
                        {
                            _sendData = 0;
                            Application!.Sent(_sendLength);
                            if (_finalSend && (!_finActive || _finComplete))
                            {
                                if (engine.Layered || engine.FinalMode == 1)
                                {
                                    engine.Metrics.FinalShutdowns++;
                                }

                                RequestClose();
                            }
                        }
                    }
                    else
                    {
                        _sendData = 0;
                        Application!.Sent(result < 0 ? result : -32);
                    }
                }
                break;
            case Operation.FinalShutdown:
                _finActive = false;
                _operations--;
                if (result == 0)
                {
                    engine.Metrics.FinalShutdowns++;
                    _finComplete = true;
                    if (!_closing && !_writeActive && _sendData == 0)
                    {
                        RequestClose();
                    }
                }
                else if (result == Native.Canceled)
                {
                    engine.Metrics.CanceledLinks++;
                    if (!_closing && _sendData != 0 && !_writeActive)
                    {
                        StartWrite();
                    }
                }
                else if (_closing)
                {
                    engine.Metrics.ShutdownAfterClose++;
                }
                else
                {
                    if (result == Native.NotConnected)
                    {
                        engine.Metrics.NotConnected++;
                    }
                    else
                    {
                        engine.Metrics.ShutdownErrors++;
                    }

                    Fail(new IOException($"Linked shutdown failed: {result}."));
                }
                break;
            default:
                throw new InvalidOperationException("Unknown completion operation.");
        }
        TryCompleteClose();
    }

    private void RefreshBio()
    {
        BioNative.GetStatus(_bio, out var status);
        engine.Metrics.InputCopied += status.InputCopied - _bioStatus.InputCopied;
        engine.Metrics.OutputCopied += status.OutputCopied - _bioStatus.OutputCopied;
        _bioStatus = status;
        if (_bioInputPage >= 0 && status.InputRemaining == 0)
        {
            BioNative.Feed(_bio, 0, 0, _peerEof ? 1 : 0);
            engine.OfferPage(_bioInputPage, true);
            _cipherBytes -= _bioInputLength;
            _bioInputPage = -1;
        }
    }
    private void FeedBio()
    {
        if (_bioInputPage < 0 && _cipher.TryDequeue(out var input))
        {
            _bioInputPage = input.Page;
            _bioInputLength = input.Length;
            BioNative.Feed(_bio, engine.Buffer(input.Page, true), input.Length, _peerEof && _cipher.Count == 0 ? 1 : 0);
        }
        else if (_bioInputPage < 0)
        {
            BioNative.Feed(_bio, 0, 0, _peerEof ? 1 : 0);
        }
    }
    private Native.TlsResult CallTls(Native.TlsOperation operation, nint data = 0, int length = 0)
    {
        if (engine.BioTls)
        {
            FeedBio();
        }

        Native.Tls(_session, operation, data, length, out var result);
        if (operation == Native.TlsOperation.Write && result.Status is Native.TlsStatus.WantRead or Native.TlsStatus.WantWrite)
        {
            engine.Metrics.TlsWriteRetries++;
        }
        if (engine.BioTls)
        {
            RefreshBio();
        }

        _pending = result.Pending != 0;
        return result;
    }
    private bool TlsResult(Native.TlsResult result)
    {
        switch (result.Status)
        {
            case Native.TlsStatus.Complete:
                return true;
            case Native.TlsStatus.WantRead:
                engine.Metrics.WantRead++;
                StartRead();
                if (engine.BioTls && _bioStatus.OutputRemaining > 0)
                {
                    StartWrite();
                }

                return false;
            case Native.TlsStatus.WantWrite:
                if (!engine.BioTls || _bioStatus.OutputRemaining > 0)
                {
                    StartWrite();
                }

                return false;
            case Native.TlsStatus.Closed:
                Application?.Input.End(null);
                RequestClose();
                return false;
            default:
                if (result.Status == Native.TlsStatus.PeerAbort)
                {
                    engine.Metrics.PeerAborts++;
                }
                else
                {
                    engine.Metrics.TlsErrors++;
                    engine.Logger.LogError("OpenSSL failure: errno={Error} reason={Reason}", result.Error, result.Reason);
                }

                Fail(new IOException($"TLS operation failed: {result.Status}, errno={result.Error}, reason=0x{result.Reason:x}."));
                return false;
        }
    }

    private bool MoreBioInput(Native.TlsResult result) => engine.BioTls && !_writeActive
        && result.Status == Native.TlsStatus.WantRead && _bioInputPage < 0 && _cipher.Count > 0;

    private void DriveTls()
    {
        if (_closing || _writeActive)
        {
            return;
        }

        if (engine.BioTls && _bioStatus.OutputRemaining > 0)
        {
            StartWrite();
            return;
        }
        if (_closeRequested)
        {
            RequestClose();
            return;
        }
        if (!_handshake)
        {
            Native.TlsResult result;
            do
            {
                result = CallTls(Native.TlsOperation.Handshake);
            } while (MoreBioInput(result));
            if (!TlsResult(result))
            {
                return;
            }

            _handshake = true;
            var ktls = Native.Ktls(_session);
            engine.Metrics.Handshakes++;
            if ((ktls & 1) != 0)
            {
                engine.Metrics.KtlsRx++;
            }

            if ((ktls & 2) != 0)
            {
                engine.Metrics.KtlsTx++;
            }

            if (engine.RequireKtls && ktls != 3)
            {
                engine.Metrics.KtlsRejected++;
                engine.Logger.LogError("kTLS required but not active: RX={Rx} TX={Tx}", ktls & 1, (ktls >> 1) & 1);
                Fail(new IOException("Required RX+TX kTLS was not activated."));
                return;
            }
            Application = engine.Publish(this);
            if (engine.BioTls && _bioStatus.OutputRemaining > 0)
            {
                StartWrite();
                StartRead();
                return;
            }
        }
        if (_sendData != 0 && !_tlsWriteDone)
        {
            Native.TlsResult result;
            do
            {
                result = CallTls(Native.TlsOperation.Write, _sendData + _sendOffset, _sendLength - _sendOffset);
            } while (MoreBioInput(result));
            if (!TlsResult(result))
            {
                return;
            }

            _sendOffset += result.Length;
            if (_sendOffset != _sendLength)
            {
                DriveTls();
                return;
            }
            _tlsWriteDone = true;
            if (engine.BioTls && _bioStatus.OutputRemaining > 0)
            {
                StartWrite();
                StartRead();
                return;
            }
        }
        if (_sendData != 0 && _tlsWriteDone)
        {
            _sendData = 0;
            _tlsWriteDone = false;
            Application!.Sent(_sendLength);
            if (_finalSend)
            {
                RequestClose();
                return;
            }
            if (!_pending && (!engine.BioTls || _cipherBytes == 0))
            {
                StartRead();
                return;
            }
        }
        _readBackpressured = false;
        while (!_closing && _leased < 4)
        {
            if (engine.BioTls && _cipherBytes == 0 && !_pending && !_peerEof)
            {
                StartRead();
                return;
            }
            if (_readPage < 0)
            {
                _readPage = engine.RentPage();
                if (_readPage < 0)
                {
                    _readBackpressured = true;
                    return;
                }
            }
            Native.TlsResult result;
            do
            {
                engine.Metrics.SslReads++;
                result = CallTls(Native.TlsOperation.Read, engine.Buffer(_readPage), Engine.PageSize);
            } while (MoreBioInput(result));
            if (!TlsResult(result))
            {
                return;
            }

            engine.Metrics.ReadSuccess++;
            _leased++;
            engine.Received(Application!, _readPage, result.Length);
            _readPage = -1;
            if (engine.BioTls && _bioStatus.OutputRemaining > 0)
            {
                StartWrite();
                StartRead();
                return;
            }
            if (!_pending && (!engine.BioTls || _cipherBytes == 0))
            {
                StartRead();
                return;
            }
        }
        if (!_closing && _leased >= 4)
        {
            _readBackpressured = true;
        }
    }

    internal void ReturnPage(int page)
    {
        if (_leased <= 0)
        {
            throw new InvalidOperationException("Page returned without a lease.");
        }

        engine.OfferPage(page);
        engine.Metrics.ReturnedPages++;
        _leased--;
        if (!_closing)
        {
            if (!engine.Tls)
            {
                StartRead();
            }
            else if (engine.BioTls || !engine.GuardPageReads || !_handshake || _closeRequested || _sendData != 0 || _readBackpressured || _pending)
            {
                engine.Metrics.PageDrives++;
                DriveTls();
            }
            else
            {
                engine.Metrics.PageSkips++;
                StartRead();
            }
        }
    }
    internal void ResumeReader()
    {
        if (!_closing && _readBackpressured && _leased < 4)
        {
            DriveTls();
        }
        else if (!_closing)
        {
            StartRead();
        }
    }

    internal void RequestClose()
    {
        if (!_closing)
        {
            _closeRequested = true;
            if (_finActive && _sendData == 0)
            {
                return;
            }

            if (_session != 0 && _handshake && !_fatal && _sendData == 0)
            {
                if (_writeActive)
                {
                    return;
                }

                if (!_shutdownSent)
                {
                    var result = CallTls(Native.TlsOperation.Shutdown);
                    if (result.Status == Native.TlsStatus.Complete)
                    {
                        _shutdownSent = true;
                        if (_finalSend)
                        {
                            engine.Metrics.CloseNotify++;
                        }
                    }
                    else if (result.Status == Native.TlsStatus.WantWrite
                        || (engine.BioTls && result.Status == Native.TlsStatus.WantRead && _bioStatus.OutputRemaining > 0))
                    {
                        StartWrite();
                        return;
                    }
                    else
                    {
                        _fatal = true;
                        if (_finalSend)
                        {
                            engine.Metrics.TlsShutdownFailures++;
                        }
                    }
                }
                if (engine.BioTls && !_fatal && _bioStatus.OutputRemaining > 0)
                {
                    StartWrite();
                    return;
                }
            }
            _closing = true;
            var resultCode = Native.Shutdown(_fd);
            if (_finalSend && engine.Tls)
            {
                if (resultCode == 0)
                {
                    if (engine.Layered)
                    {
                        engine.Metrics.FinalShutdowns++;
                    }
                    else
                    {
                        engine.Metrics.TlsFinalShutdowns++;
                    }
                }
                else if (resultCode == Native.NotConnected)
                {
                    engine.Metrics.NotConnected++;
                }
                else
                {
                    engine.Metrics.ShutdownErrors++;
                    engine.Logger.LogError("TLS shutdown failed: {Error}", resultCode);
                }
            }
            if (_readActive && !_cancelRead)
            {
                _cancelRead = true;
                Cancel(Operation.Read, Operation.CancelRead);
            }
            if (_writeActive && !_cancelWrite)
            {
                _cancelWrite = true;
                Cancel(Operation.Write, Operation.CancelWrite);
            }
        }
        TryCompleteClose();
    }
    internal void Fail(Exception error)
    {
        _fatal = true;
        Application?.Input.End(error);
        RequestClose();
    }
    private void TryCompleteClose()
    {
        if (_operations < 0)
        {
            throw new InvalidOperationException("Completion ownership underflow.");
        }

        if (!_closing || _operations != 0 || _done)
        {
            return;
        }

        _done = true;
        if (_readPage >= 0)
        {
            engine.OfferPage(_readPage);
            _readPage = -1;
        }
        if (engine.BioTls)
        {
            RefreshBio();
            if (_bioInputPage >= 0)
            {
                engine.OfferPage(_bioInputPage, true);
                _bioInputPage = -1;
            }
            while (_cipher.TryDequeue(out var input))
            {
                engine.OfferPage(input.Page, true);
            }

            _cipherBytes = 0;
        }
        if (Application is { } application)
        {
            if (_sendData != 0)
            {
                _sendData = 0;
                application.Sent(Native.Canceled);
            }
            application.NativeClosed();
        }
        else
        {
            engine.ForgetUnpublished(Id);
        }
    }
    internal void Dispose()
    {
        if (!_done || _leased != 0 || _operations != 0)
        {
            throw new InvalidOperationException("Connection freed before its terminal completions or leases.");
        }

        Native.FreeSession(_session);
        if (_bio != 0)
        {
            BioNative.Free(_bio);
        }

        engine.CloseFd(ref _fd);
        _session = _bio = 0;
    }
}
