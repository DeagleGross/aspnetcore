// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.Transport;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

internal static class WindowsNativeTlsCheck
{
    internal static async Task RunAsync(NetworkEngineOptions options, NetworkNativeTlsOptions tlsOptions, X509Certificate2 certificate, int port)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var token = deadline.Token;
        await using var engine = NetworkEngineFactory.Create(options);
        await using var listener = await engine.ListenAsync(new IPEndPoint(IPAddress.Loopback, port),
            new NetworkListenOptions { NativeTls = tlsOptions }, token);
        await RetainedPagesAsync(listener, certificate, token);
        await PeerCloseAsync(listener, certificate, token);
        await TruncatedInputAsync(listener, certificate, token);
        await BackpressureAsync(listener, certificate, reset: false, token);
        await BackpressureAsync(listener, certificate, reset: true, token);
        var idle = new List<TcpClient>();
        try
        {
            for (var index = 0; index < 8; index++)
            {
                var client = new TcpClient();
                idle.Add(client);
                await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint, token);
                await client.GetStream().WriteAsync(new byte[] { 22, 3 }, token);
            }
            using var cancelAccept = CancellationTokenSource.CreateLinkedTokenSource(token);
            var accepting = listener.AcceptAsync(cancelAccept.Token).AsTask();
            cancelAccept.Cancel();
            try
            {
                await accepting;
                throw new InvalidOperationException("An unauthenticated TLS connection was exposed to the application.");
            }
            catch (OperationCanceledException) when (cancelAccept.IsCancellationRequested)
            {
            }
            await listener.DisposeAsync().AsTask().WaitAsync(token);
            foreach (var peer in idle)
            {
                if (await peer.GetStream().ReadAsync(new byte[1], token) != 0)
                {
                    throw new IOException("An incomplete handshake survived listener shutdown.");
                }
            }
            Console.WriteLine("PASS native TLS lifecycle: unauthenticated peers not published, incomplete records/handshakes and native accepts drained.");
        }
        finally
        {
            foreach (var client in idle)
            {
                client.Dispose();
            }
        }
        if (Environment.GetEnvironmentVariable("NETWORKPROTO_TLS_HANDSHAKE_OFFLOAD") != "0")
        {
            await HandshakeCancellationAsync(options, tlsOptions, certificate, token);
        }
    }

    private static async Task HandshakeCancellationAsync(NetworkEngineOptions options, NetworkNativeTlsOptions tlsOptions,
        X509Certificate2 certificate, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var observer = new HandshakeObserver();
            using var logging = LoggerFactory.Create(builder => builder.AddProvider(observer).SetMinimumLevel(LogLevel.Debug));
            await using var engine = NetworkEngineFactory.Create(options, logging);
            await using var listener = await engine.ListenAsync(new IPEndPoint(IPAddress.Loopback, 0),
                new NetworkListenOptions { NativeTls = tlsOptions }, token);
            var stopping = 0;
            var peers = Enumerable.Range(0, 64).Select(_ => RunPeerAsync()).ToArray();
            try
            {
                await observer.Started.Task.WaitAsync(token);
                Volatile.Write(ref stopping, 1);
                await listener.DisposeAsync().AsTask().WaitAsync(token);
                await Task.WhenAll(peers).WaitAsync(token);
                if (observer.RunningCancellations != 0)
                {
                    Console.WriteLine($"PASS native TLS offload cancellation: actual queued/running ClientHello work retained until completion; running cancellations={observer.RunningCancellations}.");
                    return;
                }
            }
            finally
            {
                Volatile.Write(ref stopping, 1);
                await listener.DisposeAsync().AsTask().WaitAsync(token);
                await Task.WhenAll(peers).WaitAsync(token);
            }

            async Task RunPeerAsync()
            {
                using var client = new TcpClient();
                try
                {
                    await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint, token);
                    using var tls = ClientTls(client, certificate);
                    await tls.AuthenticateAsClientAsync(ClientOptions(), token);
                    if (await tls.ReadAsync(new byte[1], token) != 0)
                    {
                        throw new InvalidDataException("The cancellation peer received unexpected application bytes.");
                    }
                }
                catch (Exception error) when (Volatile.Read(ref stopping) != 0
                    && error is IOException or SocketException or AuthenticationException)
                {
                    // The real listener shutdown interrupts this client handshake/read.
                }
            }
        }
        throw new InvalidOperationException("The native offload test did not observe cancellation while an actual handshake job was running.");
    }

    private sealed class HandshakeObserver : ILoggerProvider, ILogger
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runningCancellations;
        internal int RunningCancellations => Volatile.Read(ref _runningCancellations);
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Dispose() { }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 1001)
            {
                Started.TrySetResult();
            }
            else if (eventId.Id == 1003)
            {
                Interlocked.Increment(ref _runningCancellations);
            }
            if (logLevel >= LogLevel.Error)
            {
                Console.Error.WriteLine(formatter(state, exception));
            }
        }
    }

    private static async Task TruncatedInputAsync(NetworkListener listener, X509Certificate2 certificate, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint, token);
        using var tls = ClientTls(client, certificate);
        var handshake = tls.AuthenticateAsClientAsync(ClientOptions(), token);
        var connection = await listener.AcceptAsync(token) ?? throw new IOException("Native TLS listener closed.");
        try
        {
            await handshake;
            client.Client.Shutdown(SocketShutdown.Send);
            try
            {
                await connection.ReceiveAsync(token);
                throw new InvalidDataException("A peer EOF without TLS close_notify reported clean plaintext EOF.");
            }
            catch (IOException error) when (error.Message.Contains("TCP EOF interrupted native TLS", StringComparison.Ordinal))
            {
            }
            Console.WriteLine("PASS native TLS truncated input: peer TCP EOF without close_notify reports an I/O error.");
        }
        finally
        {
            await connection.DisposeAsync().AsTask().WaitAsync(token);
        }
    }

    private static async Task PeerCloseAsync(NetworkListener listener, X509Certificate2 certificate, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint, token);
        using var tls = ClientTls(client, certificate);
        var handshake = tls.AuthenticateAsClientAsync(ClientOptions(), token);
        var connection = await listener.AcceptAsync(token) ?? throw new IOException("Native TLS listener closed.");
        try
        {
            await handshake;
            var request = "request"u8.ToArray();
            await tls.WriteAsync(request, token);
            await tls.ShutdownAsync();
            client.Client.Shutdown(SocketShutdown.Send);
            var observed = new List<byte>();
            while (true)
            {
                var received = await connection.ReceiveAsync(token);
                observed.AddRange(received.Buffer.ToArray());
                connection.AdvanceReceiveTo(received.Buffer.End);
                if (received.IsCompleted)
                {
                    break;
                }
            }
            if (!observed.ToArray().AsSpan().SequenceEqual(request))
            {
                throw new InvalidDataException("TLS input contained bytes beyond the known request: close_notify leaked as plaintext.");
            }
            var response = "reply"u8.ToArray();
            await connection.SendAsync(response.AsMemory(), token);
            var disposing = connection.DisposeAsync().AsTask();
            var receivedReply = new byte[response.Length];
            await tls.ReadExactlyAsync(receivedReply, token);
            if (!receivedReply.AsSpan().SequenceEqual(response) || await tls.ReadAsync(new byte[1], token) != 0)
            {
                throw new InvalidDataException("Native TLS lost its response after the peer send-half-close.");
            }
            await disposing.WaitAsync(token);
            Console.WriteLine("PASS native TLS peer close: close_notify is EOF, never plaintext; reply after peer TLS/TCP half-close.");
        }
        catch
        {
            connection.Abort(new IOException("Native TLS peer close check failed."));
            await connection.DisposeAsync().AsTask().WaitAsync(token);
            throw;
        }
    }

    private static async Task RetainedPagesAsync(NetworkListener listener, X509Certificate2 certificate, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint, token);
        using var tls = ClientTls(client, certificate);
        var handshake = tls.AuthenticateAsClientAsync(ClientOptions(), token);
        var connection = await listener.AcceptAsync(token) ?? throw new IOException("Native TLS listener closed.");
        NetworkPayload? payload = null;
        try
        {
            await handshake;
            if (!connection.IsTls)
            {
                throw new InvalidOperationException("Native TLS was not attached to the shared connection.");
            }
            var message = new byte[24 * 1024];
            for (var index = 0; index < message.Length; index++)
            {
                message[index] = (byte)(index % 251);
            }
            await tls.WriteAsync(message, token);
            while (true)
            {
                var received = await connection.ReceiveAsync(token);
                if (received.Buffer.Length >= message.Length)
                {
                    payload = received.Retain(0, message.Length);
                    if (!received.Buffer.FirstSpan.Overlaps(payload.Sequence.FirstSpan))
                    {
                        throw new InvalidOperationException("Native TLS payload retention copied its plaintext.");
                    }
                    connection.AdvanceReceiveTo(received.Buffer.GetPosition(message.Length));
                    break;
                }
                connection.AdvanceReceiveTo(received.Buffer.Start, received.Buffer.End);
            }
            var later = new byte[256 * 1024];
            Array.Fill(later, (byte)0xa5);
            var writing = tls.WriteAsync(later, token).AsTask();
            var count = 0L;
            while (count < later.Length)
            {
                var received = await connection.ReceiveAsync(token);
                foreach (var segment in received.Buffer)
                {
                    if (segment.Span.ContainsAnyExcept((byte)0xa5))
                    {
                        throw new InvalidDataException("Native TLS plaintext record was corrupted.");
                    }
                }
                count += received.Buffer.Length;
                connection.AdvanceReceiveTo(received.Buffer.End);
            }
            await writing;
            if (!payload.Sequence.ToArray().AsSpan().SequenceEqual(message))
            {
                throw new InvalidDataException("Later native TLS records overwrote retained plaintext pages.");
            }
            connection.EnqueueSend(payload);
            payload.Dispose();
            payload = null;
            var echoed = new byte[message.Length];
            await tls.ReadExactlyAsync(echoed, token);
            if (!echoed.AsSpan().SequenceEqual(message))
            {
                throw new InvalidDataException("Native TLS encrypted payload send was corrupted.");
            }
            using (var allocated = connection.AllocateBuffer(1024))
            {
                allocated.Memory.Span[..1024].Fill(0x7e);
                connection.EnqueueSend(allocated.Slice.Slice(0, 1024));
            }
            var response = new byte[1024];
            await tls.ReadExactlyAsync(response, token);
            if (response.AsSpan().ContainsAnyExcept((byte)0x7e))
            {
                throw new InvalidDataException("Native TLS owned-buffer send was released or mutated early.");
            }
            var disposing = connection.DisposeAsync().AsTask();
            if (await tls.ReadAsync(new byte[1], token) != 0)
            {
                throw new InvalidDataException("Ciphertext or close_notify was exposed as application plaintext.");
            }
            await tls.ShutdownAsync();
            client.Client.Shutdown(SocketShutdown.Send);
            await disposing.WaitAsync(token);
            Console.WriteLine("PASS native TLS ownership: TLS1.3, retained plaintext, later fragmented records, owned payload/buffer sends, close_notify and FIN.");
        }
        catch
        {
            payload?.Dispose();
            connection.Abort(new IOException("Native TLS ownership check failed."));
            await connection.DisposeAsync().AsTask().WaitAsync(token);
            throw;
        }
    }

    private static async Task BackpressureAsync(NetworkListener listener, X509Certificate2 certificate, bool reset, CancellationToken token)
    {
        using var client = new TcpClient { ReceiveBufferSize = 4096 };
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint, token);
        using var tls = ClientTls(client, certificate);
        var handshake = tls.AuthenticateAsClientAsync(ClientOptions(), token);
        var connection = await listener.AcceptAsync(token) ?? throw new IOException("Native TLS listener closed.");
        try
        {
            await handshake;
            var data = new byte[4 * 1024 * 1024];
            for (var index = 0; index < data.Length; index++)
            {
                data[index] = (byte)(index % 251);
            }
            var flushing = connection.Output.WriteAsync(data, token).AsTask();
            await Task.Delay(150, token);
            if (flushing.IsCompleted)
            {
                throw new InvalidOperationException("The native TLS test did not reach actual output backpressure.");
            }
            if (reset)
            {
                client.Client.LingerState = new LingerOption(true, 0);
                client.Dispose();
                try
                {
                    await flushing;
                }
                catch (Exception error) when (error is IOException or SocketException)
                {
                    Console.WriteLine($"Observed native TLS peer reset: {error.GetType().Name}");
                }
                await connection.DisposeAsync().AsTask().WaitAsync(token);
                Console.WriteLine("PASS native TLS reset: real blocked encrypted output and pending receives drained after peer RST.");
            }
            else
            {
                var observed = new byte[data.Length];
                var reading = tls.ReadExactlyAsync(observed, token).AsTask();
                await flushing;
                var disposing = connection.DisposeAsync().AsTask();
                await reading;
                if (!observed.AsSpan().SequenceEqual(data) || await tls.ReadAsync(new byte[1], token) != 0)
                {
                    throw new InvalidDataException("Backpressured native TLS response was corrupted or ended early.");
                }
                await tls.ShutdownAsync();
                client.Client.Shutdown(SocketShutdown.Send);
                await disposing.WaitAsync(token);
                Console.WriteLine("PASS native TLS output: exact 4 MiB across records, withheld reader, close_notify and native drain.");
            }
        }
        catch
        {
            connection.Abort(new IOException("Native TLS output check failed."));
            await connection.DisposeAsync().AsTask().WaitAsync(token);
            throw;
        }
    }

    private static SslStream ClientTls(TcpClient client, X509Certificate2 expected)
    {
        return new SslStream(client.GetStream(), leaveInnerStreamOpen: true, (_, remote, _, _) =>
            remote is not null && remote.GetRawCertData().AsSpan().SequenceEqual(expected.RawData));
    }

    private static SslClientAuthenticationOptions ClientOptions() => new()
    {
        TargetHost = "localhost",
        EnabledSslProtocols = SslProtocols.Tls13,
        AllowTlsResume = false
    };
}
