// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Transport;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

// The owner serializes all state/record work. An initial handshake job has exclusive context/input ownership.
internal sealed class SchannelConnection(WindowsWorker worker, ConnectionState state, Schannel.Credential credential) : IWindowsTlsConnection
{
    private readonly Schannel.SecurityHandle _context = new(credential: false);
    private readonly Queue<Packet> _tokens = new();
    private ReceiveStorage? _input;
    private IMemoryOwner<byte>? _wireOwner;
    private IMemoryOwner<byte>? _sendStorage;
    private ReadOnlyMemory<byte> _plain;
    private int _start, _end, _plainOffset, _plainLength;
    private bool _handshakeDone, _published, _peerClosed, _closing, _shutdownSent, _driving;
    private bool _plainFinal, _wireFinal;
    private PacketKind _wireKind;
    private Schannel.StreamSizes _sizes;
    private Schannel.ConnectionInfo _info;
    private uint _suite;
    private long _authenticatedAt, _firstPlaintextAt, _firstSubmissionAt, _firstSentAt;
    private long _finAt;
    private long _lastHandshakeInputAt, _handshakeOutputSentAt;
    private HandshakeScheduler.Work? _handshakeWork;
    private bool _prefixObserved;

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
            _input = new ReceiveStorage(worker.InputPool.Rent(32768));
            _start = _end = 0;
        }
        if (_end == _input.Memory.Length)
        {
            var remaining = _end - _start;
            if (_start != 0 && _input.References == 1)
            {
                _input.Memory.Span.Slice(_start, remaining).CopyTo(_input.Memory.Span);
                worker.Metrics.TlsReceiveCopyBytes += remaining;
            }
            else
            {
                var size = remaining == _input.Memory.Length ? checked(remaining * 2) : _input.Memory.Length;
                if (size > 256 * 1024)
                {
                    throw new AuthenticationException("The bounded native TLS input record/handshake buffer was exceeded.");
                }
                var next = new ReceiveStorage(worker.InputPool.Rent(size));
                _input.Memory.Span.Slice(_start, remaining).CopyTo(next.Memory.Span);
                worker.Metrics.TlsReceiveCopyBytes += remaining;
                _input.Release();
                _input = next;
            }
            _start = 0;
            _end = remaining;
        }
        var free = _input.Memory.Length - _end;
        if (_handshakeDone && _end == _start)
        {
            free = Math.Min(free, checked((int)(_sizes.MaximumMessage + _sizes.Header + _sizes.Trailer)));
        }
        if (free <= 0)
        {
            throw new IOException("No writable native TLS ciphertext storage remains.");
        }
        return _input.Memory.Slice(_end, free);
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
        if (!state.Read.Outstanding)
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
        while (_input is not null && _end > _start && state.Leased < 4)
        {
            var available = _end - _start;
            if (!_handshakeDone)
            {
                if (!_prefixObserved && credential.HandshakePrefixCallback is { } callback)
                {
                    TlsHandshakePrefix.Observe(callback, _input.Memory.Span.Slice(_start, available));
                    _prefixObserved = true;
                }
                if (!_context.Acquired && worker.Handshakes is { } scheduler)
                {
                    var work = new HandshakeScheduler.Work(scheduler, worker, state, credential, _context,
                        _input.Memory.Slice(_start, available));
                    _handshakeWork = work;
                    if (!scheduler.TryQueue(work))
                    {
                        _handshakeWork = null;
                        throw new IOException("The bounded native TLS handshake queue is full.");
                    }
                    worker.HandshakeQueued();
                    return;
                }
                var handshakeAt = worker.TlsTiming ? Stopwatch.GetTimestamp() : 0;
                _lastHandshakeInputAt = handshakeAt;
                var result = Schannel.Handshake(credential, _context, _input.Memory.Slice(_start, available),
                    out var extra, out var token, out var length);
                if (worker.TlsTiming)
                {
                    worker.Metrics.TlsHandshakeTicks += Stopwatch.GetTimestamp() - handshakeAt;
                    worker.Metrics.TlsHandshakeCalls++;
                }
                if (!ApplyHandshake(result, available, extra, token, length))
                {
                    return;
                }
                continue;
            }
            if (!_published)
            {
                return;
            }
            worker.Metrics.TlsDecryptCalls++;
            var status = Schannel.Decrypt(_context, _input.Memory.Slice(_start, available),
                out var offset, out var plaintext, out var remaining);
            if (status == Schannel.Incomplete)
            {
                worker.Metrics.TlsIncompleteRecords++;
                return;
            }
            if (status == Schannel.Renegotiate)
            {
                throw new AuthenticationException("Post-handshake TLS renegotiation/key-update is not supported by this prototype.");
            }
            if (status is not (0 or Schannel.Expired))
            {
                Schannel.Check(status, "DecryptMessage");
            }
            if (plaintext != 0)
            {
                if (worker.TlsTiming && _firstPlaintextAt == 0)
                {
                    _firstPlaintextAt = Stopwatch.GetTimestamp();
                    worker.Metrics.TlsHandshakeToPlainTicks += _firstPlaintextAt - _authenticatedAt;
                    worker.Metrics.TlsTimedPlaintext++;
                }
                var page = _input.Retain(_start + offset, plaintext);
                worker.Metrics.TlsPlaintextBytes += plaintext;
                state.PublishPlaintext(page, plaintext);
            }
            ConsumeCiphertext(available, remaining);
            if (status == Schannel.Expired)
            {
                _peerClosed = true;
                state.Application.EndReceive(null);
                return;
            }
        }
    }

    private bool ApplyHandshake(int result, int available, int extra, nint token, int length)
    {
        try
        {
            if (result == Schannel.Incomplete)
            {
                worker.Metrics.TlsIncompleteRecords++;
                return false;
            }
            if (result is not (0 or Schannel.Continue))
            {
                Schannel.Check(result, "AcceptSecurityContext");
            }
            QueueToken(token, length, PacketKind.Handshake);
            ConsumeCiphertext(available, extra);
            if (result == 0)
            {
                var infoAt = worker.TlsTiming ? Stopwatch.GetTimestamp() : 0;
                Schannel.GetInfo(_context, out _sizes, out _info, out _suite);
                if (worker.TlsTiming)
                {
                    worker.Metrics.TlsInfoTicks += Stopwatch.GetTimestamp() - infoAt;
                    _authenticatedAt = Stopwatch.GetTimestamp();
                }
                _handshakeDone = true;
            }
        }
        finally
        {
            Schannel.FreeToken(token);
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
            if (worker.TlsTiming && work.Called)
            {
                worker.Metrics.TlsHandshakeTicks += work.CallTicks;
                worker.Metrics.TlsHandshakeCalls++;
                worker.Metrics.TlsHandshakeQueueTicks += work.QueueTicks;
            }
            if (state.Closing || work.Error is not null)
            {
                Schannel.FreeToken(work.Output);
                if (!state.Closing)
                {
                    throw new IOException("The native TLS handshake job failed.", work.Error);
                }
                return;
            }
            if (ApplyHandshake(work.Status, work.InputLength, work.Extra, work.Output, work.OutputLength))
            {
                ProcessInput();
            }
        }
        finally
        {
            worker.HandshakeFinished();
        }
    }

    public void CancelHandshake()
    {
        if (_handshakeWork is { } work)
        {
            worker.Metrics.CancelledHandshakeWork++;
            var running = work.Cancel();
            if (running)
            {
                worker.Metrics.CancelledRunningHandshake++;
            }
            if (worker.Logger.IsEnabled(LogLevel.Debug))
            {
                worker.Logger.LogDebug(new EventId(running ? 1003 : 1002, "NativeHandshakeCancellation"),
                    "Native TLS cancellation retains pending handshake ownership for {ConnectionId}; running={Running}.", state.Id, running);
            }
        }
    }

    private void ConsumeCiphertext(int available, int extra)
    {
        if (extra < 0 || extra >= available)
        {
            throw new IOException("SChannel made no progress or returned invalid extra ciphertext.");
        }
        _start = _end - extra;
        if (_start == _end)
        {
            _input!.Release();
            _input = null;
            _start = _end = 0;
        }
    }

    private void PublishIfReady()
    {
        if (_handshakeDone && !_published && _wireOwner is null && _tokens.Count == 0)
        {
            _published = true;
            state.Application.TlsEstablished(new NetworkTlsInfo(Schannel.Protocol, (TlsCipherSuite)_suite));
            worker.Metrics.TlsHandshakes++;
            worker.Publish(state);
        }
    }

    private unsafe void QueueToken(nint token, int length, PacketKind kind)
    {
        if (length == 0)
        {
            return;
        }
        var owner = worker.OutputPool.Rent(length);
        new ReadOnlySpan<byte>((void*)token, length).CopyTo(owner.Memory.Span);
        worker.Metrics.TlsHandshakeCopyBytes += length;
        _tokens.Enqueue(new Packet(owner, length, kind));
    }

    public void Send(ReadOnlyMemory<byte> plaintext, bool final)
    {
        if (!_published || _closing || !_plain.IsEmpty || plaintext.IsEmpty)
        {
            throw new IOException("Invalid or concurrent native TLS plaintext send.");
        }
        _plain = plaintext;
        if (worker.TlsTiming && _firstSubmissionAt == 0)
        {
            _firstSubmissionAt = Stopwatch.GetTimestamp();
            if (_firstPlaintextAt != 0)
            {
                worker.Metrics.TlsApplicationTicks += _firstSubmissionAt - _firstPlaintextAt;
                worker.Metrics.TlsTimedApplication++;
            }
        }
        _plainLength = plaintext.Length;
        _plainOffset = 0;
        _plainFinal = final;
        if (final)
        {
            worker.Metrics.TlsFinalSendHints++;
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
        if (_driving || state.Closing)
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
                var count = Math.Min(_plain.Length - _plainOffset, checked((int)_sizes.MaximumMessage));
                var required = checked((int)(_sizes.Header + _sizes.Trailer) + count + (_plainFinal ? 512 : 0));
                if (_sendStorage is null || _sendStorage.Memory.Length < required)
                {
                    _sendStorage?.Dispose();
                    _sendStorage = worker.OutputPool.Rent(required);
                    worker.Metrics.TlsSendBufferRentals++;
                }
                var owner = _sendStorage;
                var encryptAt = worker.TlsTiming ? Stopwatch.GetTimestamp() : 0;
                var bytes = Schannel.Encrypt(_context, _sizes, _plain.Span.Slice(_plainOffset, count), owner.Memory);
                if (worker.TlsTiming)
                {
                    worker.Metrics.TlsEncryptTicks += Stopwatch.GetTimestamp() - encryptAt;
                }
                worker.Metrics.TlsEncryptCalls++;
                worker.Metrics.TlsSendCopyBytes += count;
                _plainOffset += count;
                if (_plainFinal && _plainOffset == _plain.Length)
                {
                    bytes += AppendShutdown(owner.Memory[bytes..]);
                    _wireFinal = true;
                }
                SendPacket(owner, bytes, PacketKind.Data);
            }
            else if (_closing && !_shutdownSent)
            {
                Schannel.BeginShutdown(_context);
                var status = Schannel.Handshake(credential, _context, default, out _, out var data, out var length);
                try
                {
                    if (status is not (0 or Schannel.Expired))
                    {
                        Schannel.Check(status, "AcceptSecurityContext shutdown");
                    }
                    _shutdownSent = true;
                    QueueToken(data, length, PacketKind.Shutdown);
                }
                finally
                {
                    Schannel.FreeToken(data);
                }
                if (_tokens.TryDequeue(out var shutdown))
                {
                    SendPacket(shutdown.Owner, shutdown.Length, shutdown.Kind);
                }
                else
                {
                    state.FinishTlsShutdown();
                }
            }
        }
        finally
        {
            _driving = false;
        }
    }

    private unsafe int AppendShutdown(Memory<byte> output)
    {
        Schannel.BeginShutdown(_context);
        var status = Schannel.Handshake(credential, _context, default, out _, out var token, out var length);
        try
        {
            if (status is not (0 or Schannel.Expired))
            {
                Schannel.Check(status, "AcceptSecurityContext final shutdown");
            }
            if (length > output.Length)
            {
                throw new IOException("Native TLS close_notify exceeded the reserved final-record tailroom.");
            }
            new ReadOnlySpan<byte>((void*)token, length).CopyTo(output.Span);
            _closing = _shutdownSent = true;
            worker.Metrics.TlsHandshakeCopyBytes += length;
            return length;
        }
        finally
        {
            Schannel.FreeToken(token);
        }
    }

    private void SendPacket(IMemoryOwner<byte> owner, int length, PacketKind kind)
    {
        _wireOwner = owner;
        _wireKind = kind;
        try
        {
            state.SendCiphertext(owner.Memory[..length]);
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
        if (worker.TlsTiming && kind == PacketKind.Handshake)
        {
            _handshakeOutputSentAt = Stopwatch.GetTimestamp();
        }
        var final = _wireFinal;
        _wireFinal = false;
        if (!ReferenceEquals(_wireOwner, _sendStorage))
        {
            _wireOwner?.Dispose();
        }
        _wireOwner = null;
        if (error is not null || state.Closing)
        {
            CompletePlaintext(error ?? new IOException("Native TLS closed during a send."));
            return;
        }
        if (kind == PacketKind.Data && _plainOffset == _plain.Length)
        {
            if (worker.TlsTiming && _firstSentAt == 0)
            {
                _firstSentAt = Stopwatch.GetTimestamp();
                worker.Metrics.TlsSendTicks += _firstSentAt - _firstSubmissionAt;
                worker.Metrics.TlsTimedSend++;
            }
            CompletePlaintext(null);
        }
        if (kind == PacketKind.Shutdown || final)
        {
            if (worker.TlsTiming)
            {
                _finAt = Stopwatch.GetTimestamp();
            }
            worker.Metrics.TlsCloseNotify++;
            if (worker.TlsTiming && _firstSentAt != 0)
            {
                worker.Metrics.TlsCloseTicks += Stopwatch.GetTimestamp() - _firstSentAt;
                worker.Metrics.TlsTimedClose++;
            }
            state.FinishTlsShutdown();
            return;
        }
        DriveOutput();
        if (!state.Read.Outstanding)
        {
            ProcessInput();
        }
        state.StartRead();
    }

    private void CompletePlaintext(Exception? error)
    {
        if (!_plain.IsEmpty)
        {
            var length = _plainLength;
            _plain = default;
            _plainOffset = _plainLength = 0;
            state.Application.Sent(error is null ? length : -1, error);
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
        state.Application.EndReceive(null);
    }

    public void NativeClosed(Exception? error)
    {
        _input?.Release();
        _input = null;
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
            throw new InvalidOperationException("SChannel disposed while a native send or handshake owns its storage/context.");
        }
        _input?.Release();
        _sendStorage?.Dispose();
        _context.Dispose();
        if (worker.TlsTiming && state.Id % 32 == 0)
        {
            Console.WriteLine("TLS_TIMELINE " + JsonSerializer.Serialize(new
            {
                backend = worker.Backend, sourcePort = state.RemoteEndPoint.Port,
                authenticated = _authenticatedAt, plaintext = _firstPlaintextAt,
                submitted = _firstSubmissionAt, sent = _firstSentAt, fin = _finAt,
                lastHandshakeInput = _lastHandshakeInputAt, handshakeOutputSent = _handshakeOutputSentAt
            }));
        }
    }

    private enum PacketKind { Handshake, Data, Shutdown }
    private readonly record struct Packet(IMemoryOwner<byte> Owner, int Length, PacketKind Kind);

    private sealed class ReceiveStorage(IMemoryOwner<byte> owner)
    {
        private int _references = 1;
        internal int References => Volatile.Read(ref _references);
        internal Memory<byte> Memory => owner.Memory;
        internal IMemoryOwner<byte> Retain(int offset, int length)
        {
            Interlocked.Increment(ref _references);
            return new PlaintextLease(this, owner.Memory.Slice(offset, length));
        }
        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
            {
                owner.Dispose();
            }
        }
    }

    private sealed class PlaintextLease(ReceiveStorage storage, Memory<byte> memory) : IMemoryOwner<byte>
    {
        private int _disposed;
        public Memory<byte> Memory => memory;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                storage.Release();
            }
        }
    }
}
