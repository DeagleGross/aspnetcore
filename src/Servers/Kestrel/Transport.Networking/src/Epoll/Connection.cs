// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    private readonly Engine _engine;
    private readonly ulong _id;
    private readonly Pipe _output = new(new PipeOptions(useSynchronizationContext: false));
    private readonly TaskCompletionSource _closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closed = new();
    private readonly Task _sending;
    private readonly Lock _disposeGate = new();
    private Task? _disposing;
    private TaskCompletionSource<int>? _send;
    private int _closing;
    internal OwnedPipeReader Input { get; }

    internal Connection(Engine engine, ulong id, IPEndPoint endpoint) : base($"owned-epoll-{id:x}")
    {
        _engine = engine;
        _id = id;
        LocalEndPoint = endpoint;
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        Input = new OwnedPipeReader(page => engine.Enqueue(new(Engine.CommandKind.ReturnPage, id, Page: page)));
        Transport = new DuplexPipe(Input, _output.Writer);
        ConnectionClosed = _closed.Token;
        var tls = new TlsFeature();
        Features.Set<ITlsConnectionFeature>(tls);
        Features.Set<ITlsHandshakeFeature>(tls);
        _sending = SendLoop();
    }

    private async Task SendLoop()
    {
        try
        {
            while (true)
            {
                var read = await _output.Reader.ReadAsync().ConfigureAwait(false);
                try
                {
                    var remaining = read.Buffer.Length;
                    foreach (var segment in read.Buffer)
                    {
                        remaining -= segment.Length;
                        if (segment.IsEmpty)
                        {
                            continue;
                        }
                        using var pin = segment.Pin();
                        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _send = completion;
                        unsafe
                        {
                            _engine.Enqueue(new(Engine.CommandKind.Send, _id, (nint)pin.Pointer, segment.Length,
                                Final: _engine.FinalSendEnabled && read.IsCompleted && !read.IsCanceled && remaining == 0));
                        }
                        var count = await completion.Task.ConfigureAwait(false);
                        if (count != segment.Length)
                        {
                            throw new IOException($"Epoll send failed: {count}.");
                        }
                    }
                }
                finally
                {
                    _output.Reader.AdvanceTo(read.Buffer.End);
                }
                if (read.IsCompleted || read.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (Exception error)
        {
            Input.End(error);
        }
        finally
        {
            _output.Reader.Complete();
            Close();
        }
    }

    internal void Sent(int count) => _send?.TrySetResult(count);

    internal void Closed(Exception? error)
    {
        _send?.TrySetResult(-125);
        Input.End(error);
        try
        {
            _closed.Cancel();
        }
        finally
        {
            _closedSignal.TrySetResult();
        }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closing, 1) == 0)
        {
            _engine.Enqueue(new(Engine.CommandKind.Close, _id));
        }
    }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        Input.End(abortReason);
        _output.Reader.CancelPendingRead();
        Close();
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
        await _sending.ConfigureAwait(false);
        await _closedSignal.Task.ConfigureAwait(false);
        _engine.Enqueue(new(Engine.CommandKind.Forget, _id));
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
