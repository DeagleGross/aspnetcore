// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Buffers;
using System.Net;
using System.Net.Transport;
using System.Text;

// Answers each HTTP request with ONE SendAsync(ReadOnlySequence<byte>) whose segments are caller-owned arrays, so no byte is copied
// into a pipe. This is the "many buffers, one call" workload that provider gather sends are meant for.
internal static class DirectSequenceServer
{
    internal static async Task RunAsync(NetworkEngineOptions options, int port, int segments, int bodyBytes)
    {
        using var stop = new CancellationTokenSource();
        var response = BuildResponse(segments, bodyBytes);
        var metrics = BuildResponse(1, 3, "{}\n"u8.ToArray());
        await using var engine = NetworkEngineFactory.Create(options);
        await using var listener = await engine.ListenAsync(new IPEndPoint(IPAddress.Loopback, port));
        var connections = new List<Task>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var connection = await listener.AcceptAsync(stop.Token);
                if (connection is null)
                {
                    break;
                }
                lock (connections)
                {
                    connections.RemoveAll(static task => task.IsCompleted);
                    connections.Add(ServeAsync(connection, response, metrics, stop));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        Task[] pending;
        lock (connections)
        {
            pending = [.. connections];
        }
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task ServeAsync(NetworkConnection connection, ReadOnlySequence<byte> response, ReadOnlySequence<byte> metrics, CancellationTokenSource stop)
    {
        try
        {
            while (true)
            {
                var result = await connection.ReceiveAsync();
                var buffer = result.Buffer;
                var reader = new SequenceReader<byte>(buffer);
                if (!reader.TryReadTo(out ReadOnlySequence<byte> _, "\r\n\r\n"u8))
                {
                    connection.AdvanceReceiveTo(buffer.Start, buffer.End);
                    if (result.IsCompleted)
                    {
                        break;
                    }
                    continue;
                }
                var first = buffer.FirstSpan;
                var shutdown = first.StartsWith("POST /shutdown"u8);
                var probe = shutdown || first.StartsWith("GET /metrics"u8);
                connection.AdvanceReceiveTo(reader.Position);
                await connection.SendAsync(probe ? metrics : response);
                if (shutdown)
                {
                    stop.Cancel();
                    break;
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    private static ReadOnlySequence<byte> BuildResponse(int segments, int bodyBytes, byte[]? fixedBody = null)
    {
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {bodyBytes}\r\n\r\n");
        var first = new Chain(header, null);
        var last = first;
        if (fixedBody is not null)
        {
            last = last.Append(fixedBody);
        }
        else
        {
            var body = Encoding.ASCII.GetBytes(new string('x', bodyBytes - 1) + "\n");
            var size = Math.Max(1, bodyBytes / segments);
            for (var offset = 0; offset < bodyBytes; offset += size)
            {
                var length = bodyBytes - offset - size < size ? bodyBytes - offset : size;
                last = last.Append(body.AsMemory(offset, length));
                if (length != size)
                {
                    break;
                }
            }
        }
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Chain : ReadOnlySequenceSegment<byte>
    {
        internal Chain(ReadOnlyMemory<byte> memory, Chain? previous)
        {
            Memory = memory;
            RunningIndex = previous is null ? 0 : previous.RunningIndex + previous.Memory.Length;
        }

        internal Chain Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Chain(memory, this);
            Next = next;
            return next;
        }
    }
}
