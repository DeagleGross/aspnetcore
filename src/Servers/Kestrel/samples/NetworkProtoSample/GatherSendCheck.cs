// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;

// Produces genuinely multi-segment pipe output so the provider gather-send path (sendmsg / IORING_OP_SENDMSG) is the producer under test.
internal static class GatherSendCheck
{
    internal static async Task RunAsync(IConnectionListenerFactory factory, int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var listener = await factory.BindAsync(new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
        try
        {
            await WithheldReaderAsync(listener, port, 3 * 1024 * 1024 + 12345, 4000, timeout.Token);
            Console.WriteLine("PASS gather-send: odd-size multi-segment final output with a withheld reader, exact bytes and EOF");
            await ConcurrentFlushAsync(listener, port, 20000, 700, timeout.Token);
            Console.WriteLine("PASS gather-send: many small flushed messages with a concurrent reader, exact bytes and EOF");
            await ResetAsync(listener, port, timeout.Token);
            Console.WriteLine("PASS gather-send: reset during backpressured multi-segment output drained without hanging");
            await PingPongAsync(listener, port, 300, 24000, timeout.Token);
            Console.WriteLine("PASS gather-send: 300 keep-alive request/response round trips with multi-segment non-final responses stayed prompt (no send corking)");
        }
        finally
        {
            await listener.UnbindAsync(timeout.Token);
            await listener.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        }
    }

    private static async Task WithheldReaderAsync(IConnectionListener listener, int port, int size, int chunk, CancellationToken token)
    {
        using var client = new TcpClient { ReceiveBufferSize = 8192 };
        await client.ConnectAsync(IPAddress.Loopback, port, token);
        var connection = await listener.AcceptAsync(token) ?? throw new IOException("Listener closed.");
        try
        {
            WritePattern(connection, 0, size, chunk);
            await connection.Transport.Output.CompleteAsync();
            await Task.Delay(150, token);
            await ReadAllAsync(client.GetStream(), size, token);
            await connection.DisposeAsync().AsTask().WaitAsync(token);
        }
        catch
        {
            connection.Abort(new ConnectionAbortedException("Gather-send check failed."));
            throw;
        }
    }

    private static async Task ConcurrentFlushAsync(IConnectionListener listener, int port, int messages, int messageSize, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, token);
        var connection = await listener.AcceptAsync(token) ?? throw new IOException("Listener closed.");
        try
        {
            var total = messages * messageSize;
            var reader = ReadAllAsync(client.GetStream(), total, token);
            var output = connection.Transport.Output;
            for (var i = 0; i < messages; i++)
            {
                WritePattern(connection, i * messageSize, messageSize, messageSize);
                if (i % 7 == 0)
                {
                    await output.FlushAsync(token);
                }
            }
            await output.CompleteAsync();
            await reader;
            await connection.DisposeAsync().AsTask().WaitAsync(token);
        }
        catch
        {
            connection.Abort(new ConnectionAbortedException("Gather-send check failed."));
            throw;
        }
    }

    private static async Task ResetAsync(IConnectionListener listener, int port, CancellationToken token)
    {
        using var client = new TcpClient { ReceiveBufferSize = 4096 };
        await client.ConnectAsync(IPAddress.Loopback, port, token);
        var connection = await listener.AcceptAsync(token) ?? throw new IOException("Listener closed.");
        WritePattern(connection, 0, 16 * 1024 * 1024, 4000);
        await connection.Transport.Output.CompleteAsync();
        await Task.Delay(150, token);
        client.Client.LingerState = new LingerOption(true, 0);
        client.Close();
        await connection.DisposeAsync().AsTask().WaitAsync(token);
    }

    private static async Task PingPongAsync(IConnectionListener listener, int port, int rounds, int size, CancellationToken outerToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        var token = budget.Token;
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port, token);
        var connection = await listener.AcceptAsync(token) ?? throw new IOException("Listener closed.");
        try
        {
            var input = connection.Transport.Input;
            var output = connection.Transport.Output;
            var server = Task.Run(async () =>
            {
                for (var i = 0; i < rounds; i++)
                {
                    var result = await input.ReadAsync(token);
                    if (result.Buffer.IsEmpty)
                    {
                        throw new IOException("Client closed before the last request.");
                    }
                    input.AdvanceTo(result.Buffer.End);
                    WritePattern(connection, 0, size, 4000);
                    await output.FlushAsync(token);
                }
            }, token);
            var stream = client.GetStream();
            var request = new byte[1];
            var response = new byte[size];
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < rounds; i++)
            {
                await stream.WriteAsync(request, token);
                await stream.ReadExactlyAsync(response, token);
                for (var j = 0; j < size; j++)
                {
                    if (response[j] != (byte)(j % 251))
                    {
                        throw new InvalidDataException($"Gather payload corrupted in round {i} at offset {j}.");
                    }
                }
            }
            watch.Stop();
            await server;
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new InvalidOperationException($"{rounds} keep-alive round trips took {watch.Elapsed.TotalMilliseconds:F0} ms; a non-final gather send was held back.");
            }
            await output.CompleteAsync();
            await connection.DisposeAsync().AsTask().WaitAsync(token);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !outerToken.IsCancellationRequested)
        {
            connection.Abort(new ConnectionAbortedException("Gather-send check failed."));
            throw new InvalidOperationException($"{rounds} keep-alive round trips did not finish within 10 s; a non-final gather send was held back.");
        }
        catch
        {
            connection.Abort(new ConnectionAbortedException("Gather-send check failed."));
            throw;
        }
    }

    private static void WritePattern(ConnectionContext connection, long offset, int size, int chunk)
    {
        var output = connection.Transport.Output;
        var buffer = new byte[chunk];
        for (var written = 0; written < size;)
        {
            var count = Math.Min(chunk, size - written);
            for (var i = 0; i < count; i++)
            {
                buffer[i] = (byte)((offset + written + i) % 251);
            }
            output.Write(buffer.AsSpan(0, count));
            written += count;
        }
    }

    private static async Task ReadAllAsync(NetworkStream stream, int size, CancellationToken token)
    {
        var received = 0;
        var buffer = new byte[16384];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0)
            {
                break;
            }
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != (byte)((received + i) % 251))
                {
                    throw new InvalidDataException($"Gather payload corrupted at offset {received + i}.");
                }
            }
            received += count;
        }
        if (received != size)
        {
            throw new InvalidDataException($"Premature FIN: expected {size}, received {received}.");
        }
    }
}
