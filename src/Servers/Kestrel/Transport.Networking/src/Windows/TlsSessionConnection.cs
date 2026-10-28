// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net.Security;
using System.Net.Transport;
using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

// The same owner-serialized contract as SchannelConnection, but record protection goes through the runtime's sans-I/O TlsBufferSession (dotnet/runtime#128871).
// The session never decrypts inside the receive page, so every decrypted record lands in its own pooled page that is published to the application.
internal sealed class TlsSessionConnection : IWindowsTlsConnection
{
    private const int MaximumPlaintext = 16384, InitialInput = 32768, MaximumBuffer = 256 * 1024, RecordSlack = 1024, ShutdownSlack = 512;
    private readonly WindowsWorker _worker;
    private readonly ConnectionState _state;
    private readonly TlsSessionServer _server;
    private readonly TlsBufferSession _session = new();
    private readonly Queue<Packet> _tokens = new();
    private IMemoryOwner<byte>? _input, _plainSpare, _wireOwner, _sendStorage;
    private ReadOnlyMemory<byte> _plain;
    private int _start, _end, _plainOffset, _plainLength;
    private bool _handshakeStarted, _handshakeDone, _published, _peerClosed, _closing, _shutdownSent, _driving;
    private bool _plainFinal, _wireFinal, _prefixObserved;
    private PacketKind _wireKind;
    private HandshakeWork? _handshakeWork;

    internal TlsSessionConnection(WindowsWorker worker, ConnectionState state, TlsSessionServer server)
    {
        _worker = worker;
        _state = state;
        _server = server;
        try
        {
            _session.SetContext(server.Context);
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    public bool Authenticated => _handshakeDone;
    public bool HandshakePending => _handshakeWork is not null;
    public int BufferedCiphertext => _input is null ? 0 : _end - _start;

    public Memory<byte> GetReceiveMemory()
    {
        if (HandshakePending || _handshakeDone && !_published || !_handshakeDone && (_wireOwner is not null || _tokens.Count != 0))
        {
            return default;
        }
        if (_input is null)
        {
            _input = _worker.InputPool.Rent(InitialInput);
            _start = _end = 0;
        }
        if (_end == _input.Memory.Length)
        {
            var remaining = _end - _start;
            if (_start != 0)
            {
                _input.Memory.Span.Slice(_start, remaining).CopyTo(_input.Memory.Span);
            }
            else
            {
                var size = checked(remaining * 2);
                if (size > MaximumBuffer)
                {
                    throw new AuthenticationException("The bounded native TLS input record/handshake buffer was exceeded.");
                }
                var next = _worker.InputPool.Rent(size);
                _input.Memory.Span.Slice(_start, remaining).CopyTo(next.Memory.Span);
                _input.Dispose();
                _input = next;
            }
            _worker.Metrics.TlsReceiveCopyBytes += remaining;
            _start = 0;
            _end = remaining;
        }
        return _input.Memory[_end..];
    }

    public void Received(int count)
    {
        if (_input is null || count <= 0 || count > _input.Memory.Length - _end)
        {
            throw new IOException("Native TLS receive completed outside its owned ciphertext storage.");
        }
        if (_peerClosed)
        {
            throw new IOException("Ciphertext arrived after TLS close_notify.");
        }
        _end += count;
        ProcessInput();
    }

    public void ResumeInput()
    {
        if (!_state.Read.Outstanding)
        {
            ProcessInput();
        }
    }

    private void ProcessInput()
    {
        if (HandshakePending)
        {
            return;
        }
        while (_input is not null && _end > _start && _state.Leased < 4)
        {
            var available = _end - _start;
            if (!_handshakeDone)
            {
                if (!_prefixObserved && _server.HandshakePrefixCallback is { } callback)
                {
                    TlsHandshakePrefix.Observe(callback, _input.Memory.Span.Slice(_start, available));
                    _prefixObserved = true;
                }
                if (!_handshakeStarted && _worker.Handshakes is { } scheduler)
                {
                    var work = new HandshakeWork(scheduler, _worker, _state, _session, _input.Memory.Slice(_start, available));
                    _handshakeWork = work;
                    if (!scheduler.TryQueue(work))
                    {
                        _handshakeWork = null;
                        throw new IOException("The bounded native TLS handshake queue is full.");
                    }
                    _worker.HandshakeQueued();
                    return;
                }
                if (!ApplyHandshake(RunHandshake(_session, _input.Memory.Span.Slice(_start, available))))
                {
                    return;
                }
                continue;
            }
            if (!_published || !DecryptRecord(available))
            {
                return;
            }
        }
    }

    private bool DecryptRecord(int available)
    {
        _plainSpare ??= _worker.InputPool.Rent(MaximumPlaintext);
        _worker.Metrics.TlsDecryptCalls++;
        var status = _session.Read(_input!.Memory.Span.Slice(_start, available), _plainSpare.Memory.Span, out var consumed, out var written);
        switch (status)
        {
            case TlsOperationStatus.NeedMoreData:
                _worker.Metrics.TlsIncompleteRecords++;
                return false;
            case TlsOperationStatus.Complete:
                if (consumed <= 0)
                {
                    throw new IOException("The TLS session made no progress decrypting buffered ciphertext.");
                }
                ConsumeCiphertext(consumed);
                if (written > 0)
                {
                    var page = _plainSpare;
                    _plainSpare = null;
                    _worker.Metrics.TlsPlaintextBytes += written;
                    _state.PublishPlaintext(page, written);
                }
                return true;
            case TlsOperationStatus.Closed:
                ConsumeCiphertext(consumed);
                _peerClosed = true;
                _state.Application.EndReceive(null);
                return false;
            default:
                throw new AuthenticationException($"Post-handshake TLS output ({status}) is not supported by this prototype.");
        }
    }

    // Drives the session until it needs more ciphertext or the handshake completes. Output is returned in a rented array the caller must release.
    private static HandshakeStep RunHandshake(TlsBufferSession session, ReadOnlySpan<byte> input)
    {
        var output = ArrayPool<byte>.Shared.Rent(16384);
        var consumed = 0;
        var written = 0;
        var succeeded = false;
        try
        {
            while (true)
            {
                if (output.Length - written < 4096)
                {
                    output = Grow(output, written);
                }
                var status = session.Handshake(input[consumed..], output.AsSpan(written), out var usedInput, out var usedOutput);
                consumed += usedInput;
                written += usedOutput;
                switch (status)
                {
                    case TlsOperationStatus.Complete when session.IsHandshakeComplete:
                        succeeded = true;
                        return new HandshakeStep(consumed, written, true, output);
                    case TlsOperationStatus.Complete when usedInput != 0 || usedOutput != 0:
                        continue;
                    case TlsOperationStatus.Complete:
                    case TlsOperationStatus.NeedMoreData:
                        succeeded = true;
                        return new HandshakeStep(consumed, written, false, output);
                    case TlsOperationStatus.DestinationTooSmall:
                        output = Grow(output, written);
                        continue;
                    case TlsOperationStatus.NeedsCertificateValidation:
                        // The server session always suspends once the handshake completes; no client certificate is requested, so there is nothing to reject.
                        session.SetRemoteCertificateValidationResult(SslPolicyErrors.None);
                        while (true)
                        {
                            if (output.Length - written < 4096)
                            {
                                output = Grow(output, written);
                            }
                            var drain = session.DrainPendingOutput(output.AsSpan(written), out var drained);
                            written += drained;
                            if (drain == TlsOperationStatus.Complete)
                            {
                                break;
                            }
                            if (drain != TlsOperationStatus.DestinationTooSmall)
                            {
                                throw new AuthenticationException($"Unsupported TLS drain status {drain}.");
                            }
                        }
                        succeeded = true;
                        return new HandshakeStep(consumed, written, true, output);
                    default:
                        throw new AuthenticationException($"Unsupported TLS handshake status {status}.");
                }
            }
        }
        finally
        {
            if (!succeeded)
            {
                ArrayPool<byte>.Shared.Return(output);
            }
        }
    }

    private static byte[] Grow(byte[] output, int written)
    {
        if (output.Length >= MaximumBuffer)
        {
            throw new AuthenticationException("The bounded native TLS handshake output buffer was exceeded.");
        }
        var next = ArrayPool<byte>.Shared.Rent(output.Length * 2);
        output.AsSpan(0, written).CopyTo(next);
        ArrayPool<byte>.Shared.Return(output);
        return next;
    }

    private bool ApplyHandshake(HandshakeStep step)
    {
        try
        {
            if (step.Consumed == 0 && step.Written == 0 && !step.Complete)
            {
                _worker.Metrics.TlsIncompleteRecords++;
                return false;
            }
            _handshakeStarted = true;
            QueueToken(step.Output, step.Written);
            ConsumeCiphertext(step.Consumed);
            _handshakeDone |= step.Complete;
        }
        finally
        {
            if (step.Output is not null)
            {
                ArrayPool<byte>.Shared.Return(step.Output);
            }
        }
        DriveOutput();
        PublishIfReady();
        return _published;
    }

    public void HandshakeCompleted()
    {
        var work = _handshakeWork ?? throw new InvalidOperationException("A native handshake result arrived without an ownership hold.");
        _handshakeWork = null;
        try
        {
            if (_state.Closing || work.Error is not null)
            {
                if (work.Step.Output is not null)
                {
                    ArrayPool<byte>.Shared.Return(work.Step.Output);
                }
                if (!_state.Closing)
                {
                    throw new IOException("The native TLS handshake job failed.", work.Error);
                }
                return;
            }
            if (ApplyHandshake(work.Step))
            {
                ProcessInput();
            }
        }
        finally
        {
            _worker.HandshakeFinished();
        }
    }

    public void CancelHandshake()
    {
        if (_handshakeWork is { } work)
        {
            _worker.Metrics.CancelledHandshakeWork++;
            if (work.Cancel())
            {
                _worker.Metrics.CancelledRunningHandshake++;
            }
        }
    }

    private void ConsumeCiphertext(int consumed)
    {
        if (consumed < 0 || consumed > _end - _start)
        {
            throw new IOException("The TLS session reported an invalid consumed ciphertext count.");
        }
        _start += consumed;
        if (_start == _end)
        {
            _input?.Dispose();
            _input = null;
            _start = _end = 0;
        }
    }

    private void PublishIfReady()
    {
        if (_handshakeDone && !_published && _wireOwner is null && _tokens.Count == 0)
        {
            _published = true;
            _state.Application.TlsEstablished(new NetworkTlsInfo(_session.NegotiatedProtocol, _session.NegotiatedCipherSuite));
            _worker.Metrics.TlsHandshakes++;
            _worker.Publish(_state);
        }
    }

    private void QueueToken(byte[]? token, int length)
    {
        if (length == 0)
        {
            return;
        }
        var owner = _worker.OutputPool.Rent(length);
        token.AsSpan(0, length).CopyTo(owner.Memory.Span);
        _worker.Metrics.TlsHandshakeCopyBytes += length;
        _tokens.Enqueue(new Packet(owner, length, PacketKind.Handshake));
    }

    public void Send(ReadOnlyMemory<byte> plaintext, bool final)
    {
        if (!_published || _closing || !_plain.IsEmpty || plaintext.IsEmpty)
        {
            throw new IOException("Invalid or concurrent native TLS plaintext send.");
        }
        _plain = plaintext;
        _plainLength = plaintext.Length;
        _plainOffset = 0;
        _plainFinal = final;
        if (final)
        {
            _worker.Metrics.TlsFinalSendHints++;
        }
        try
        {
            DriveOutput();
        }
        catch
        {
            _plain = default;
            _plainOffset = _plainLength = 0;
            throw;
        }
    }

    private void DriveOutput()
    {
        if (_driving || _state.Closing)
        {
            return;
        }
        _driving = true;
        try
        {
            if (_wireOwner is not null)
            {
                return;
            }
            if (_tokens.TryDequeue(out var token))
            {
                SendPacket(token.Owner, token.Length, token.Kind);
                return;
            }
            PublishIfReady();
            if (!_plain.IsEmpty)
            {
                var count = Math.Min(_plain.Length - _plainOffset, MaximumPlaintext);
                var required = count + RecordSlack + (_plainFinal ? ShutdownSlack : 0);
                if (_sendStorage is null || _sendStorage.Memory.Length < required)
                {
                    _sendStorage?.Dispose();
                    _sendStorage = _worker.OutputPool.Rent(required);
                    _worker.Metrics.TlsSendBufferRentals++;
                }
                var owner = _sendStorage;
                var status = _session.Write(_plain.Span.Slice(_plainOffset, count), owner.Memory.Span, out var consumed, out var bytes);
                if (status != TlsOperationStatus.Complete || consumed <= 0)
                {
                    throw new IOException($"The TLS session returned {status} after consuming {consumed} of {count} plaintext bytes.");
                }
                _worker.Metrics.TlsEncryptCalls++;
                _worker.Metrics.TlsSendCopyBytes += consumed;
                _plainOffset += consumed;
                if (_plainFinal && _plainOffset == _plain.Length)
                {
                    bytes += AppendShutdown(owner.Memory.Span[bytes..]);
                    _wireFinal = true;
                }
                SendPacket(owner, bytes, PacketKind.Data);
            }
            else if (_closing && !_shutdownSent)
            {
                var owner = _worker.OutputPool.Rent(ShutdownSlack);
                int length;
                try
                {
                    var status = _session.Shutdown(owner.Memory.Span, out length);
                    if (status != TlsOperationStatus.Closed)
                    {
                        throw new IOException($"The TLS session returned {status} for close_notify.");
                    }
                }
                catch
                {
                    owner.Dispose();
                    throw;
                }
                _shutdownSent = true;
                if (length == 0)
                {
                    owner.Dispose();
                    _state.FinishTlsShutdown();
                }
                else
                {
                    SendPacket(owner, length, PacketKind.Shutdown);
                }
            }
        }
        finally
        {
            _driving = false;
        }
    }

    private int AppendShutdown(Span<byte> output)
    {
        var status = _session.Shutdown(output, out var length);
        if (status != TlsOperationStatus.Closed)
        {
            throw new IOException($"Native TLS close_notify exceeded the reserved final-record tailroom ({status}).");
        }
        _closing = _shutdownSent = true;
        _worker.Metrics.TlsHandshakeCopyBytes += length;
        return length;
    }

    private void SendPacket(IMemoryOwner<byte> owner, int length, PacketKind kind)
    {
        _wireOwner = owner;
        _wireKind = kind;
        try
        {
            _state.SendCiphertext(owner.Memory[..length]);
        }
        catch
        {
            _wireOwner = null;
            if (!ReferenceEquals(owner, _sendStorage))
            {
                owner.Dispose();
            }
            throw;
        }
    }

    public void Sent(Exception? error)
    {
        var kind = _wireKind;
        var final = _wireFinal;
        _wireFinal = false;
        if (!ReferenceEquals(_wireOwner, _sendStorage))
        {
            _wireOwner?.Dispose();
        }
        _wireOwner = null;
        if (error is not null || _state.Closing)
        {
            CompletePlaintext(error ?? new IOException("Native TLS closed during a send."));
            return;
        }
        if (kind == PacketKind.Data && _plainOffset == _plain.Length)
        {
            CompletePlaintext(null);
        }
        if (kind == PacketKind.Shutdown || final)
        {
            _worker.Metrics.TlsCloseNotify++;
            _state.FinishTlsShutdown();
            return;
        }
        DriveOutput();
        if (!_state.Read.Outstanding)
        {
            ProcessInput();
        }
        _state.StartRead();
    }

    private void CompletePlaintext(Exception? error)
    {
        if (!_plain.IsEmpty)
        {
            var length = _plainLength;
            _plain = default;
            _plainOffset = _plainLength = 0;
            _state.Application.Sent(error is null ? length : -1, error);
        }
    }

    public void Shutdown()
    {
        _closing = true;
        DriveOutput();
    }

    public void PeerEof()
    {
        if (!_handshakeDone || _end != _start || (!_peerClosed && !_shutdownSent))
        {
            throw new IOException("TCP EOF interrupted native TLS before a complete record/close_notify.");
        }
        _state.Application.EndReceive(null);
    }

    public void NativeClosed(Exception? error)
    {
        _input?.Dispose();
        _input = null;
        _plainSpare?.Dispose();
        _plainSpare = null;
        while (_tokens.TryDequeue(out var token))
        {
            token.Owner.Dispose();
        }
        _sendStorage?.Dispose();
        _sendStorage = null;
        CompletePlaintext(error ?? new IOException("Native TLS connection closed."));
    }

    public void Dispose()
    {
        if (_wireOwner is not null || HandshakePending)
        {
            throw new InvalidOperationException("The TLS session was disposed while a native send or handshake owns its storage/context.");
        }
        _input?.Dispose();
        _plainSpare?.Dispose();
        _sendStorage?.Dispose();
        _session.Dispose();
    }

    private enum PacketKind { Handshake, Data, Shutdown }
    private readonly record struct Packet(IMemoryOwner<byte> Owner, int Length, PacketKind Kind);
    private readonly record struct HandshakeStep(int Consumed, int Written, bool Complete, byte[]? Output);

    private sealed class HandshakeWork(HandshakeScheduler scheduler, WindowsWorker worker, ConnectionState state, TlsBufferSession session, Memory<byte> input) : IThreadPoolWorkItem
    {
        private int _phase;
        internal HandshakeStep Step;
        internal Exception? Error;

        internal bool Cancel()
        {
            return Interlocked.CompareExchange(ref _phase, 3, 0) == 1;
        }

        void IThreadPoolWorkItem.Execute()
        {
            try
            {
                if (Interlocked.CompareExchange(ref _phase, 1, 0) != 0)
                {
                    return;
                }
                Step = RunHandshake(session, input.Span);
            }
            catch (Exception error)
            {
                Error = error;
            }
            finally
            {
                Volatile.Write(ref _phase, 2);
                var next = scheduler.ReleaseSlot();
                try
                {
                    // The owner still holds the session/input. Only this command may release the work hold.
                    worker.Enqueue(new(WindowsWorker.CommandKind.HandshakeCompleted, state));
                }
                finally
                {
                    if (next is not null)
                    {
                        ThreadPool.UnsafeQueueUserWorkItem(next, preferLocal: false);
                    }
                }
            }
        }
    }
}
