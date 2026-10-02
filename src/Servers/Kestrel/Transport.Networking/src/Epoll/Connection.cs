// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll;

internal sealed class Connection : DefaultConnectionContext
{
    private readonly EpollWorker _worker;
    private readonly ConnectionState _state;
    // The inline reader callback only enqueues worker work. Application flush
    // continuations still use the ThreadPool writer scheduler.
    private readonly Pipe _output = new(new PipeOptions(readerScheduler: PipeScheduler.Inline, writerScheduler: PipeScheduler.ThreadPool, useSynchronizationContext: false));
    private readonly TaskCompletionSource _closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource _outputCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action _outputReady;
    private readonly Lock _disposeGate = new();
    private Task? _disposing;
    private ValueTask<ReadResult> _pendingRead;
    private ReadResult _read;
    private ReadOnlySequence<byte> _remaining;
    private MemoryHandle _pin;
    private bool _reading, _hasRead, _sending, _outputStopped, _transportClosed, _pumping;
    private int _outputScheduled, _sendLength;
    private Exception? _sendError;
    internal OwnedPipeReader Input { get; }

    internal Connection(EpollWorker worker, ConnectionState state, IPEndPoint endpoint) : base($"owned-epoll-{state.Id:x}")
    {
        _worker = worker;
        _state = state;
        _outputReady = QueueOutput;
        LocalEndPoint = endpoint;
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        Input = new OwnedPipeReader(page => worker.Enqueue(new(EpollWorker.CommandKind.ReturnPage, state, Page: page)));
        Transport = new DuplexPipe(Input, _output.Writer);
        ConnectionClosed = _closed.Token;
        if (worker.Tls)
        {
            var tls = new TlsFeature();
            Features.Set<ITlsConnectionFeature>(tls);
            Features.Set<ITlsHandshakeFeature>(tls);
        }
        QueueOutput();
    }

    private void QueueOutput()
    {
        if (Interlocked.Exchange(ref _outputScheduled, 1) == 0)
        {
            _worker.Enqueue(new(EpollWorker.CommandKind.OutputReady, _state));
        }
    }

    // Called only by the epoll worker. Pipe completion merely queues this work;
    // it never executes socket I/O or the application on a ThreadPool producer.
    internal void PumpOutput()
    {
        Volatile.Write(ref _outputScheduled, 0);
        if (_outputStopped || _sending)
        {
            return;
        }
        _pumping = true;
        try
        {
            if (_reading)
            {
                if (!_pendingRead.IsCompleted)
                {
                    return;
                }
                _reading = false;
                _read = _pendingRead.GetAwaiter().GetResult();
                _pendingRead = default;
                _remaining = _read.Buffer;
                _hasRead = true;
            }
            if (_sendError is not null)
            {
                throw _sendError;
            }
            if (_transportClosed)
            {
                CompleteOutput();
                return;
            }
            // Bound synchronous pipe/socket progress to avoid monopolizing the worker.
            for (var i = 0; i < 16; i++)
            {
                if (_transportClosed || _sendError is not null)
                {
                    if (_sendError is not null)
                    {
                        Input.End(_sendError);
                    }
                    CompleteOutput();
                    return;
                }
                if (_hasRead && _remaining.IsEmpty)
                {
                    _output.Reader.AdvanceTo(_read.Buffer.End);
                    _hasRead = false;
                    if (_read.IsCompleted || _read.IsCanceled)
                    {
                        CompleteOutput();
                        return;
                    }
                }
                if (!_hasRead)
                {
#pragma warning disable CA2012 // Pump-owned state machine consumes each read exactly once after its readiness notification.
                    _pendingRead = _output.Reader.ReadAsync();
#pragma warning restore CA2012
                    _reading = true;
                    if (!_pendingRead.IsCompleted)
                    {
                        _pendingRead.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_outputReady);
                        return;
                    }
                    _reading = false;
                    _read = _pendingRead.GetAwaiter().GetResult();
                    _pendingRead = default;
                    _remaining = _read.Buffer;
                    _hasRead = true;
                    if (_remaining.IsEmpty)
                    {
                        continue;
                    }
                }
                var position = _remaining.Start;
                while (_remaining.TryGet(ref position, out var segment))
                {
                    if (segment.IsEmpty)
                    {
                        continue;
                    }
                    _remaining = _remaining.Slice(segment.Length);
                    _pin = segment.Pin();
                    _sendLength = segment.Length;
                    _sending = true;
                    var final = _worker.FinalSendEnabled && _read.IsCompleted && !_read.IsCanceled && _remaining.IsEmpty;
                    unsafe
                    {
                        _worker.Send(_state, (nint)_pin.Pointer, _sendLength, final);
                    }
                    break;
                }
                if (_sending)
                {
                    return;
                }
            }
            QueueOutput();
        }
        catch (Exception error)
        {
            Input.End(error);
            // Native calls are synchronous; a thrown submission cannot retain the pin.
            if (_sending)
            {
                _sending = false;
                _pin.Dispose();
            }
            CompleteOutput();
        }
        finally
        {
            _pumping = false;
        }
    }

    private void CompleteOutput()
    {
        if (_outputStopped)
        {
            return;
        }
        _outputStopped = true;
        if (_sending)
        {
            throw new InvalidOperationException("Output completed while a socket still owns its buffer.");
        }
        if (_hasRead)
        {
            _output.Reader.AdvanceTo(_read.Buffer.End);
            _hasRead = false;
        }
        _output.Reader.Complete();
        _outputCompleted.TrySetResult();
        _state.RequestClose();
    }

    internal void Sent(int count)
    {
        if (!_sending)
        {
            return;
        }
        _sending = false;
        _pin.Dispose();
        if (count != _sendLength)
        {
            _sendError = new IOException($"Epoll send failed: {count}.");
        }
        if (!_pumping)
        {
            QueueOutput();
        }
    }

    internal void Closed(Exception? error)
    {
        _transportClosed = true;
        Sent(-125);
        Input.End(error);
        _output.Reader.CancelPendingRead();
        QueueOutput();
        try
        {
            _closed.Cancel();
        }
        finally
        {
            _closedSignal.TrySetResult();
        }
    }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        Input.End(abortReason);
        _output.Reader.CancelPendingRead();
        _worker.Enqueue(new(EpollWorker.CommandKind.Close, _state));
    }

    public override ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new(_disposing ??= DisposeCore());
        }
    }

    private async Task DisposeCore()
    {
        Input.Complete();
        _output.Writer.Complete();
        await _outputCompleted.Task.ConfigureAwait(false);
        await _closedSignal.Task.ConfigureAwait(false);
        _worker.Enqueue(new(EpollWorker.CommandKind.Forget, _state));
        _closed.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class TlsFeature : ITlsConnectionFeature, ITlsHandshakeFeature
    {
        public X509Certificate2? ClientCertificate { get; set; }
        public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken) => Task.FromResult(ClientCertificate);
        public SslProtocols Protocol => SslProtocols.Tls12;
        public TlsCipherSuite? NegotiatedCipherSuite => TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256;
#pragma warning disable SYSLIB0058 // Required by the existing ITlsHandshakeFeature contract.
        public CipherAlgorithmType CipherAlgorithm => CipherAlgorithmType.Aes128;
        public int CipherStrength => 128;
        public HashAlgorithmType HashAlgorithm => HashAlgorithmType.Sha256;
        public int HashStrength => 256;
        public ExchangeAlgorithmType KeyExchangeAlgorithm => ExchangeAlgorithmType.DiffieHellman;
        public int KeyExchangeStrength => 0;
#pragma warning restore SYSLIB0058
        public string HostName => string.Empty;
    }
}
