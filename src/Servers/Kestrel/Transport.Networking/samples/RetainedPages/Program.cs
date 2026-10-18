// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net.Sockets;
using System.Net.Transport;

const int MessageLength = 24 * 1024;
const int LaterLength = 128 * 1024;
var settings = DemoSettings.Parse(args, 5012);
using var cancellation = new CancellationTokenSource(settings.Timeout);
using var observer = new NativeSendObserver();
await using var engine = NetworkEngineFactory.Create(settings.EngineOptions, observer);
await using var listener = await engine.ListenAsync(settings.EndPoint, cancellationToken: cancellation.Token);
Console.WriteLine($"Retained pages through {engine.Kind}, listening at {listener.LocalEndPoint}");
await OneConnectionDemo.RunAsync(listener, cancellation, RunServerAsync, RunClientAsync);
observer.Verify();
Console.WriteLine("PASS: framed metadata decoded/re-encoded; original payload pages survived advancement/later traffic and owned enqueue.");

async Task RunServerAsync(NetworkConnection connection, CancellationToken cancellationToken)
{
    NetworkPayload message;
    FrameCodec.Header header;
    while (true)
    {
        var received = await connection.ReceiveAsync(cancellationToken);
        HttpPayloads.CheckRead(received, cancellationToken);
        var hasHeader = FrameCodec.TryReadHeader(received.Buffer, out header);
        if (hasHeader && (header.PayloadLength != MessageLength || header.RequestId != 42))
        {
            throw new InvalidDataException("Unexpected request metadata.");
        }
        if (hasHeader && received.Buffer.Length >= FrameCodec.HeaderLength + MessageLength)
        {
            message = received.Retain(FrameCodec.HeaderLength, header.PayloadLength);
            try
            {
                var original = received.Buffer.Slice(FrameCodec.HeaderLength, header.PayloadLength).GetEnumerator();
                foreach (var segment in message.Sequence)
                {
                    if (!original.MoveNext() || original.Current.Length != segment.Length
                        || !original.Current.Span.Overlaps(segment.Span, out var offset) || offset != 0)
                    {
                        throw new InvalidOperationException("Retention copied bytes instead of sharing every receive-page slice.");
                    }
                    observer.Expect(segment);
                }
                if (original.MoveNext())
                {
                    throw new InvalidOperationException("Retention omitted a receive-page slice.");
                }
                connection.AdvanceReceiveTo(received.Buffer.GetPosition(FrameCodec.HeaderLength + header.PayloadLength));
            }
            catch
            {
                message.Dispose();
                throw;
            }
            break;
        }
        if (received.IsCompleted)
        {
            throw new EndOfStreamException("Incomplete multi-page message.");
        }
        var bufferedPages = 0;
        foreach (var segment in received.Buffer)
        {
            bufferedPages++;
        }
        if (bufferedPages >= 4)
        {
            throw new NotSupportedException("The fragmented demo message exhausted the prototype's four-page receive window before its frame completed.");
        }
        connection.AdvanceReceiveTo(received.Buffer.Start, received.Buffer.End);
    }
    using (message)
    {
        var segments = 0;
        foreach (var segment in message.Sequence)
        {
            segments++;
        }
        if (segments >= 4)
        {
            throw new NotSupportedException("This demo must leave a receive-page slot available for the later-traffic retention check.");
        }
        Console.WriteLine($"SERVER retained {message.Sequence.Length} bytes in {segments} segments.");
        var remaining = LaterLength;
        while (remaining != 0)
        {
            var received = await connection.ReceiveAsync(cancellationToken);
            HttpPayloads.CheckRead(received, cancellationToken);
            var count = Math.Min(remaining, checked((int)received.Buffer.Length));
            foreach (var segment in received.Buffer.Slice(0, count))
            {
                if (segment.Span.ContainsAnyExcept((byte)0xa5))
                {
                    throw new InvalidDataException("Later traffic was corrupted.");
                }
            }
            remaining -= count;
            connection.AdvanceReceiveTo(received.Buffer.GetPosition(count));
        }
        HttpPayloads.VerifyPattern(message.Sequence, MessageLength);
        Console.WriteLine("SERVER cursor advanced and later traffic consumed; retained message still exact.");
        using var replyHeader = connection.AllocateBuffer(FrameCodec.HeaderLength);
        FrameCodec.WriteHeader(replyHeader.Memory.Span, header with { RequestId = header.RequestId + 1 });
        connection.EnqueueSend(replyHeader.Slice.Slice(0, FrameCodec.HeaderLength));
        connection.EnqueueSend(message);
        Console.WriteLine($"SERVER serialized reply metadata id={header.RequestId + 1}; enqueued the original payload pages, then released caller ownership.");
    }
    await connection.DisposeAsync();
}

async Task RunClientAsync(CancellationToken cancellationToken)
{
    using var client = new TcpClient();
    await client.ConnectAsync(settings.EndPoint, cancellationToken);
    var stream = client.GetStream();
    var header = new byte[FrameCodec.HeaderLength];
    FrameCodec.WriteHeader(header, new FrameCodec.Header(MessageLength, 42));
    var message = new byte[MessageLength];
    for (var index = 0; index < message.Length; index++)
    {
        message[index] = (byte)(index % 251);
    }
    var later = new byte[LaterLength];
    Array.Fill(later, (byte)0xa5);
    await stream.WriteAsync(header, cancellationToken);
    await stream.WriteAsync(message, cancellationToken);
    await stream.WriteAsync(later, cancellationToken);
    client.Client.Shutdown(SocketShutdown.Send);
    await stream.ReadExactlyAsync(header, cancellationToken);
    if (!FrameCodec.TryReadHeader(new ReadOnlySequence<byte>(header), out var replyHeader)
        || replyHeader != new FrameCodec.Header(MessageLength, 43))
    {
        throw new InvalidDataException("The reply metadata was not serialized correctly.");
    }
    var reply = new byte[MessageLength];
    await stream.ReadExactlyAsync(reply, cancellationToken);
    HttpPayloads.VerifyPattern(new ReadOnlySequence<byte>(reply), MessageLength);
    if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
    {
        throw new InvalidDataException("Expected EOF after the retained message reply.");
    }
    Console.WriteLine($"CLIENT received the exact {reply.Length}-byte message back.");
}
