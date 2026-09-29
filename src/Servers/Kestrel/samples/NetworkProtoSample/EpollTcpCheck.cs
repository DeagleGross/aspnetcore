// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

internal static class EpollTcpCheck
{
    internal static async Task RunAsync(IConnectionListenerFactory factory, int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = await factory.BindAsync(new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            var connection = await listener.AcceptAsync(timeout.Token) ?? throw new IOException("Listener closed.");
            try
            {
                if (connection.Features.Get<ITlsConnectionFeature>() is not null
                    || connection.Features.Get<ITlsHandshakeFeature>() is not null)
                {
                    throw new InvalidOperationException("Raw TCP must not publish TLS features.");
                }
                using (var cancelled = new CancellationTokenSource())
                {
                    var read = connection.Transport.Input.ReadAsync(cancelled.Token);
                    cancelled.Cancel();
                    var result = await read;
                    if (!result.IsCanceled)
                    {
                        throw new IOException("Pending input cancellation was not observed.");
                    }
                    connection.Transport.Input.AdvanceTo(result.Buffer.End);
                }

                var data = new byte[256 * 1024];
                for (var i = 0; i < data.Length; i++)
                {
                    data[i] = (byte)(i % 251);
                }
                var stream = client.GetStream();
                var writing = stream.WriteAsync(data, timeout.Token).AsTask();
                // Let the real socket fill the four-page lease limit before consuming input.
                await Task.Delay(200, timeout.Token);
                await writing;
                client.Client.Shutdown(SocketShutdown.Send);
                var received = 0;
                while (true)
                {
                    var read = await connection.Transport.Input.ReadAsync(timeout.Token);
                    foreach (var segment in read.Buffer)
                    {
                        for (var i = 0; i < segment.Length; i++)
                        {
                            if (segment.Span[i] != (byte)((received + i) % 251))
                            {
                                throw new InvalidDataException("TCP input corrupted across a page lease.");
                            }
                        }
                        received += segment.Length;
                    }
                    connection.Transport.Input.AdvanceTo(read.Buffer.End);
                    if (read.IsCompleted)
                    {
                        break;
                    }
                }
                if (received != data.Length)
                {
                    throw new InvalidDataException($"Premature TCP input EOF: {received}.");
                }
                // Input EOF must not close output before the application produces its reply.
                await connection.Transport.Output.WriteAsync(data.AsMemory(0, 1024), timeout.Token);
                await connection.Transport.Output.CompleteAsync();
                var response = new byte[1024];
                await stream.ReadExactlyAsync(response, timeout.Token);
                if (!response.AsSpan().SequenceEqual(data.AsSpan(0, response.Length))
                    || await stream.ReadAsync(new byte[1], timeout.Token) != 0)
                {
                    throw new InvalidDataException("TCP half-close lost the response or its final EOF.");
                }
                await connection.DisposeAsync().AsTask().WaitAsync(timeout.Token);
                Console.WriteLine("PASS epoll TCP: cancelled read recovered, 256 KiB backpressured input, response after peer half-close, no TLS features");
            }
            catch
            {
                connection.Abort(new ConnectionAbortedException("TCP check failed."));
                await connection.DisposeAsync().AsTask().WaitAsync(timeout.Token);
                throw;
            }
        }
        finally
        {
            await listener.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        }
    }
}
