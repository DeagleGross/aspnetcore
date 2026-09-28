// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;

// Uses real TLS handshakes. Hold clients' second writes until the server's
// accepted-connection channel closes, forcing completion after UnbindAsync.
internal static class RejectedAcceptCheck
{
    internal static async Task RunAsync(IConnectionListenerFactory factory, int port, X509Certificate2 certificate)
    {
        const int clientCount = 16;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = await factory.BindAsync(new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
        var clients = new List<(TcpClient Socket, HandshakeGate Gate, SslStream Tls, Task Handshake)>();
        try
        {
            for (var i = 0; i < clientCount; i++)
            {
                var socket = new TcpClient();
                await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                var gate = new HandshakeGate(socket.GetStream());
                var tls = new SslStream(gate, leaveInnerStreamOpen: false, (_, remote, _, _) =>
                    remote is not null && remote.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData));
                var handshake = tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12,
                    AllowTlsResume = false
                }, timeout.Token);
                clients.Add((socket, gate, tls, handshake));
            }

            await Task.WhenAll(clients.Select(c => c.Gate.SecondWrite)).WaitAsync(timeout.Token);
            await listener.UnbindAsync(timeout.Token);
            foreach (var c in clients)
            {
                c.Gate.Release();
            }
            foreach (var c in clients)
            {
                try
                {
                    await c.Handshake.WaitAsync(timeout.Token);
                }
                catch (IOException)
                {
                    // Rejection may close TLS before the peer sees Finished.
                }
                catch (AuthenticationException)
                {
                    // The native rejectedAccepts counter establishes reachability.
                }
                c.Tls.Dispose();
                c.Socket.Dispose();
            }
            await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine($"PASS rejected-accept: {clientCount} gated handshakes, unbind before Finished, listener disposed");
        }
        finally
        {
            foreach (var c in clients)
            {
                c.Gate.Release();
                c.Tls.Dispose();
                c.Socket.Dispose();
            }
        }
    }

    private sealed class HandshakeGate(Stream inner) : Stream
    {
        private readonly TaskCompletionSource _secondWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        internal Task SecondWrite => _secondWrite.Task;
        internal void Release() => _release.TrySetResult();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_writes > 1)
            {
                _secondWrite.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            await inner.WriteAsync(buffer, cancellationToken);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Expected asynchronous TLS writes.");
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
