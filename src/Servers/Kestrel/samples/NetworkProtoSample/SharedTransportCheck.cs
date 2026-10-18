// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.Transport;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

internal static class SharedTransportCheck
{
    internal static async Task RunTlsAsync(NetworkEngineOptions options, int port, X509Certificate2 certificate, SslProtocols protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellationToken = deadline.Token;
        await using var engine = NetworkEngineFactory.Create(options);
        await using var listener = await engine.ListenAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken: cancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint, cancellationToken);
        var connection = await listener.AcceptAsync(cancellationToken) ?? throw new IOException("Shared listener closed.");
        try
        {
            var prefix = "PROTO/1\r\n"u8.ToArray();
            var network = client.GetStream();
            await network.WriteAsync(prefix, cancellationToken);
            using var clientTls = new SslStream(network, leaveInnerStreamOpen: true, static (_, _, _, _) => true);
            var clientAuthentication = clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = protocol,
                AllowTlsResume = false
            }, cancellationToken);
            NetworkReceiveResult raw;
            while (true)
            {
                raw = await connection.ReceiveAsync(cancellationToken);
                if (raw.Buffer.Length > prefix.Length)
                {
                    break;
                }
                connection.AdvanceReceiveTo(raw.Buffer.Start, raw.Buffer.End);
            }
            if (!raw.Buffer.Slice(0, prefix.Length).ToArray().AsSpan().SequenceEqual(prefix))
            {
                throw new InvalidDataException("The actual pre-TLS producer prefix was corrupted.");
            }
            // Keep already-received ClientHello bytes while consuming only the protocol prefix.
            connection.AdvanceReceiveTo(raw.Buffer.GetPosition(prefix.Length), raw.Buffer.End);
            using var serverTls = await connection.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = protocol,
                AllowTlsResume = false
            }, cancellationToken);
            await clientAuthentication;
            var message = "shared-layered-message"u8.ToArray();
            await clientTls.WriteAsync(message, cancellationToken);
            var observed = new byte[message.Length];
            await serverTls.ReadExactlyAsync(observed, cancellationToken);
            if (!observed.AsSpan().SequenceEqual(message))
            {
                throw new InvalidDataException("TLS plaintext did not match the real client message.");
            }
            await serverTls.WriteAsync(message, cancellationToken);
            await clientTls.ReadExactlyAsync(observed, cancellationToken);
            if (!observed.AsSpan().SequenceEqual(message))
            {
                throw new InvalidDataException("Layered TLS response was corrupted.");
            }
            await serverTls.ShutdownAsync();
            if (await clientTls.ReadAsync(new byte[1], cancellationToken) != 0)
            {
                throw new InvalidDataException("Expected TLS EOF after close_notify.");
            }
            await clientTls.ShutdownAsync();
            client.Client.Shutdown(SocketShutdown.Send);
            await connection.DisposeAsync().AsTask().WaitAsync(cancellationToken);
            Console.WriteLine($"PASS shared layered {engine.Kind}: actual raw prefix plus pre-read ClientHello, caller-installed TLS, exact plaintext, close_notify and native drain");
        }
        catch
        {
            connection.Abort(new IOException("Layered TLS check failed."));
            await connection.DisposeAsync().AsTask().WaitAsync(cancellationToken);
            throw;
        }
    }

    internal static async Task RunAsync(NetworkEngineOptions options, int port)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = deadline.Token;
        await using var engine = NetworkEngineFactory.Create(options);
        await using var listener = await engine.ListenAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken: cancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint, cancellationToken);
        var connection = await listener.AcceptAsync(cancellationToken) ?? throw new IOException("Shared listener closed.");
        NetworkPayload? payload = null;
        try
        {
            var stream = client.GetStream();
            var message = new byte[24 * 1024];
            for (var i = 0; i < message.Length; i++)
            {
                message[i] = (byte)(i % 251);
            }
            await stream.WriteAsync(message, cancellationToken);
            NetworkReceiveResult received;
            while (true)
            {
                received = await connection.ReceiveAsync(cancellationToken);
                if (received.Buffer.Length >= message.Length)
                {
                    break;
                }
                if (received.IsCompleted)
                {
                    throw new InvalidDataException("EOF before the retained message.");
                }
                connection.AdvanceReceiveTo(received.Buffer.Start, received.Buffer.End);
            }
            payload = received.Retain(8, message.Length - 16);
            if (!received.Buffer.Slice(8).FirstSpan.Overlaps(payload.Sequence.FirstSpan))
            {
                throw new InvalidOperationException("Retention copied the first payload segment instead of retaining storage.");
            }
            Verify(payload.Sequence, 8, message.Length - 16);
            using (var retained = payload.Retain())
            {
                if (!payload.Sequence.FirstSpan.Overlaps(retained.Sequence.FirstSpan))
                {
                    throw new InvalidOperationException("Independent payload retention copied bytes.");
                }
            }
            connection.AdvanceReceiveTo(received.Buffer.End);

            var later = new byte[128 * 1024];
            Array.Fill(later, (byte)0xa5);
            var writing = stream.WriteAsync(later, cancellationToken).AsTask();
            var remaining = later.Length;
            while (remaining != 0)
            {
                var read = await connection.ReceiveAsync(cancellationToken);
                foreach (var segment in read.Buffer)
                {
                    if (segment.Span.ContainsAnyExcept((byte)0xa5))
                    {
                        throw new InvalidDataException("Later received storage was corrupted.");
                    }
                }
                remaining -= checked((int)read.Buffer.Length);
                connection.AdvanceReceiveTo(read.Buffer.End);
            }
            await writing;
            Verify(payload.Sequence, 8, message.Length - 16);
            client.Client.Shutdown(SocketShutdown.Send);
            while (true)
            {
                var eof = await connection.ReceiveAsync(cancellationToken);
                connection.AdvanceReceiveTo(eof.Buffer.End);
                if (eof.IsCompleted)
                {
                    break;
                }
            }

            var borrowed = new byte[1024];
            Array.Fill(borrowed, (byte)0x3c);
            await connection.SendAsync(borrowed, cancellationToken);
            var response = new byte[1024];
            await stream.ReadExactlyAsync(response, cancellationToken);
            if (!response.AsSpan().SequenceEqual(borrowed))
            {
                throw new InvalidDataException("Borrowed send was corrupted.");
            }

            using (var owned = connection.AllocateBuffer(1024))
            {
                owned.Memory.Span[..1024].Fill(0x7e);
                connection.EnqueueSend(owned.Slice.Slice(0, 1024));
            }
            await stream.ReadExactlyAsync(response, cancellationToken);
            if (response.AsSpan().ContainsAnyExcept((byte)0x7e))
            {
                throw new InvalidDataException("Owned enqueue released storage before native completion.");
            }
            var multiPageResponse = new byte[message.Length - 16];
            await connection.SendAsync(payload.Sequence, cancellationToken);
            await stream.ReadExactlyAsync(multiPageResponse, cancellationToken);
            Verify(new ReadOnlySequence<byte>(multiPageResponse), 8, multiPageResponse.Length);
            using (var forwarded = payload.Retain())
            {
                connection.EnqueueSend(forwarded);
            }
            await stream.ReadExactlyAsync(multiPageResponse, cancellationToken);
            Verify(new ReadOnlySequence<byte>(multiPageResponse), 8, multiPageResponse.Length);
            var disposing = connection.DisposeAsync().AsTask();
            if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
            {
                throw new InvalidDataException("Expected FIN after queued sends.");
            }
            Verify(payload.Sequence, 8, message.Length - 16);
            if (disposing.IsCompleted)
            {
                throw new InvalidOperationException("Connection disposal did not wait for the retained native pages.");
            }
            payload.Dispose();
            payload = null;
            await disposing.WaitAsync(cancellationToken);
            Console.WriteLine($"PASS shared {engine.Kind}: actual multi-page retention without copy, cursor independence, later receives, borrowed single/sequence send, owned slice/payload enqueue, half-close and native lease drain");
        }
        catch
        {
            payload?.Dispose();
            connection.Abort(new IOException("Shared transport check failed."));
            await connection.DisposeAsync().AsTask().WaitAsync(cancellationToken);
            throw;
        }
        await CheckSendCancellationAsync(listener, cancellationToken);
    }

    private static async Task CheckSendCancellationAsync(NetworkListener listener, CancellationToken cancellationToken)
    {
        using var client = new TcpClient { ReceiveBufferSize = 4096 };
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint, cancellationToken);
        var connection = await listener.AcceptAsync(cancellationToken) ?? throw new IOException("Shared listener closed.");
        try
        {
            var borrowed = new byte[4 * 1024 * 1024];
            connection.MaxQueuedSendBytes = borrowed.Length;
            var head = new BorrowedSegment(borrowed.AsMemory(0, 16384));
            var tail = head;
            for (var offset = 16384; offset < borrowed.Length; offset += 16384)
            {
                var next = new BorrowedSegment(borrowed.AsMemory(offset, 16384));
                tail.Link(next);
                tail = next;
            }
            var sequence = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
            using var cancelling = new CancellationTokenSource();
            var sending = connection.SendAsync(sequence, cancelling.Token).AsTask();
            await Task.Delay(100, cancellationToken);
            if (sending.IsCompleted)
            {
                await sending;
                throw new InvalidOperationException("The direct send cancellation check did not reach native output backpressure.");
            }
            cancelling.Cancel();
            try
            {
                await sending.WaitAsync(cancellationToken);
                throw new InvalidOperationException("A cancelled direct send reported success.");
            }
            catch (OperationCanceledException error) when (sending.IsCanceled && error.CancellationToken == cancelling.Token)
            {
            }
            borrowed.AsSpan().Fill(0x5a);
            await connection.DisposeAsync().AsTask().WaitAsync(cancellationToken);
            Console.WriteLine("PASS shared direct cancellation: real backpressured native send, original token, terminal ownership before borrowed-memory reuse");
        }
        catch
        {
            connection.Abort(new IOException("Shared direct send cancellation check failed."));
            await connection.DisposeAsync().AsTask().WaitAsync(cancellationToken);
            throw;
        }
    }

    private sealed class BorrowedSegment : ReadOnlySequenceSegment<byte>
    {
        internal BorrowedSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        internal void Link(BorrowedSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }

    private static void Verify(in ReadOnlySequence<byte> sequence, int offset, int expectedLength)
    {
        if (sequence.Length != expectedLength)
        {
            throw new InvalidDataException("Retained payload length changed.");
        }
        foreach (var segment in sequence)
        {
            for (var i = 0; i < segment.Length; i++)
            {
                if (segment.Span[i] != (byte)((offset + i) % 251))
                {
                    throw new InvalidDataException("Retained payload storage was reused early.");
                }
            }
            offset += segment.Length;
        }
    }
}
