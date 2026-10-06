// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

internal static class WindowsTransportCheck
{
    internal static async Task RunAsync(IConnectionListenerFactory factory, int port)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cancellationToken = deadline.Token;
        var listener = await factory.BindAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.EndPoint, cancellationToken);
            var connection = await listener.AcceptAsync(cancellationToken) ?? throw new IOException("Listener closed.");
            try
            {
                if (connection.Features.Get<ITlsConnectionFeature>() is not null
                    || connection.Features.Get<ITlsHandshakeFeature>() is not null)
                {
                    throw new InvalidOperationException("Windows TCP drivers must not claim to perform TLS.");
                }
                using (var cancelled = new CancellationTokenSource())
                {
                    var pending = connection.Transport.Input.ReadAsync(cancelled.Token);
                    cancelled.Cancel();
                    var result = await pending;
                    if (!result.IsCanceled)
                    {
                        throw new InvalidOperationException("Pipe read cancellation was lost.");
                    }
                    connection.Transport.Input.AdvanceTo(result.Buffer.End);
                }
                var stream = client.GetStream();
                await stream.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
                var prefix = await connection.Transport.Input.ReadAsync(cancellationToken);
                if (!prefix.Buffer.ToArray().AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }))
                {
                    throw new InvalidDataException("Prefix was corrupted.");
                }
                connection.Transport.Input.AdvanceTo(prefix.Buffer.Start, prefix.Buffer.End);
                var awaitingMore = connection.Transport.Input.ReadAsync(cancellationToken);
                await Task.Delay(50, cancellationToken);
                if (awaitingMore.IsCompleted)
                {
                    throw new InvalidOperationException("Examined but unconsumed bytes caused a busy read loop.");
                }
                await stream.WriteAsync(new byte[] { 4, 5, 6, 7 }, cancellationToken);
                var combined = await awaitingMore;
                if (!combined.Buffer.ToArray().AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7 }))
                {
                    throw new InvalidDataException("Unconsumed prefix did not survive the next receive.");
                }
                connection.Transport.Input.AdvanceTo(combined.Buffer.End);

                var data = new byte[256 * 1024];
                for (var i = 0; i < data.Length; i++)
                {
                    data[i] = (byte)(i % 251);
                }
                var writing = stream.WriteAsync(data, cancellationToken).AsTask();
                await Task.Delay(200, cancellationToken);
                await writing;
                client.Client.Shutdown(SocketShutdown.Send);
                var received = 0;
                while (true)
                {
                    var read = await connection.Transport.Input.ReadAsync(cancellationToken);
                    Verify(read.Buffer, received);
                    received += checked((int)read.Buffer.Length);
                    connection.Transport.Input.AdvanceTo(read.Buffer.End);
                    if (read.IsCompleted)
                    {
                        break;
                    }
                }
                if (received != data.Length)
                {
                    throw new InvalidDataException($"Premature input EOF at {received} bytes.");
                }

                var response = new byte[4 * 1024 * 1024];
                for (var i = 0; i < response.Length; i++)
                {
                    response[i] = (byte)(i % 251);
                }
                var flushing = connection.Transport.Output.WriteAsync(response, cancellationToken).AsTask();
                await Task.Delay(200, cancellationToken);
                if (flushing.IsCompleted)
                {
                    throw new InvalidOperationException("The slow-reader check did not reach output backpressure.");
                }
                var observed = new byte[response.Length];
                var receiving = stream.ReadExactlyAsync(observed, cancellationToken).AsTask();
                await flushing;
                await connection.Transport.Output.CompleteAsync();
                await receiving;
                if (!observed.AsSpan().SequenceEqual(response)
                    || await stream.ReadAsync(new byte[1], cancellationToken) != 0)
                {
                    throw new InvalidDataException("Response after peer half-close was corrupted or missing EOF.");
                }
                await connection.DisposeAsync().AsTask().WaitAsync(cancellationToken);
                Console.WriteLine("PASS Windows transfer: cancellation recovery, examined cursor, 256 KiB bounded input, 4 MiB backpressured output, peer half-close and EOF");
            }
            catch
            {
                connection.Abort(new ConnectionAbortedException("Windows transport check failed."));
                await connection.DisposeAsync().AsTask().WaitAsync(cancellationToken);
                throw;
            }

            using var resetClient = new TcpClient();
            await resetClient.ConnectAsync((IPEndPoint)listener.EndPoint, cancellationToken);
            var resetConnection = await listener.AcceptAsync(cancellationToken) ?? throw new IOException("Listener closed.");
            var blockedOutput = resetConnection.Transport.Output.WriteAsync(new byte[4 * 1024 * 1024], cancellationToken).AsTask();
            await Task.Delay(100, cancellationToken);
            if (blockedOutput.IsCompleted)
            {
                throw new InvalidOperationException("Reset check did not reach a blocked send.");
            }
            resetClient.Client.LingerState = new LingerOption(true, 0);
            resetClient.Dispose();
            try
            {
                await blockedOutput;
            }
            catch (IOException)
            {
                // A real peer reset may fail the pending producer flush.
            }
            catch (SocketException error) when (error.NativeErrorCode is 64 or 995 or 10053 or 10054)
            {
                Console.WriteLine($"Observed expected peer-reset send failure: {error.NativeErrorCode}");
            }
            await resetConnection.ConnectionClosed.WaitHandle.WaitOneAsync(cancellationToken);
            await resetConnection.DisposeAsync().AsTask().WaitAsync(cancellationToken);
            Console.WriteLine("PASS Windows reset: blocked native output and receive drained after peer RST");

            var idleClients = new List<TcpClient>();
            try
            {
                for (var i = 0; i < 32; i++)
                {
                    var idle = new TcpClient();
                    idleClients.Add(idle);
                    await idle.ConnectAsync((IPEndPoint)listener.EndPoint, cancellationToken);
                }
                await Task.Delay(100, cancellationToken);
                await listener.DisposeAsync().AsTask().WaitAsync(cancellationToken);
                foreach (var idle in idleClients)
                {
                    if (await idle.GetStream().ReadAsync(new byte[1], cancellationToken) != 0)
                    {
                        throw new IOException("Idle connection survived listener disposal.");
                    }
                }
                Console.WriteLine("PASS Windows lifecycle: queued idle peers and preposted accepts drained at listener disposal");
            }
            finally
            {
                foreach (var idle in idleClients)
                {
                    idle.Dispose();
                }
            }
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(cancellationToken);
        }
    }

    private static void Verify(in ReadOnlySequence<byte> bytes, int offset)
    {
        foreach (var segment in bytes)
        {
            for (var i = 0; i < segment.Length; i++)
            {
                if (segment.Span[i] != (byte)((offset + i) % 251))
                {
                    throw new InvalidDataException("Input page was reused before consumption.");
                }
            }
            offset += segment.Length;
        }
    }

    private static async Task WaitOneAsync(this WaitHandle handle, CancellationToken cancellationToken)
    {
        while (!handle.WaitOne(0))
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
