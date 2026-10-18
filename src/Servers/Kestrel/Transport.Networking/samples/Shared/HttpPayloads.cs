// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net.Transport;
using System.Text;

internal static class HttpPayloads
{
    internal static readonly byte[] Request = "GET /hello HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"u8.ToArray();
    internal static readonly byte[] Response = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 12\r\nConnection: close\r\n\r\nHello world\n"u8.ToArray();
    internal static readonly byte[] Greeting = "READY FOR TLS\r\n"u8.ToArray();
    internal static readonly byte[] Prefix = "PROTO/1\r\n"u8.ToArray();

    internal static async Task VerifyResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var observed = new byte[Response.Length];
        await stream.ReadExactlyAsync(observed, cancellationToken);
        if (!observed.AsSpan().SequenceEqual(Response))
        {
            throw new InvalidDataException("The hardcoded HTTP response was corrupted.");
        }
        Console.WriteLine("CLIENT received:\n" + Encoding.ASCII.GetString(observed));
        if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
        {
            throw new InvalidDataException("Expected EOF after the HTTP response.");
        }
    }

    internal static async Task ReplyOverStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        var observed = new byte[Request.Length];
        await stream.ReadExactlyAsync(observed, cancellationToken);
        if (!observed.AsSpan().SequenceEqual(Request))
        {
            throw new InvalidDataException("The hardcoded HTTP request was corrupted.");
        }
        Console.WriteLine("SERVER plaintext request:\n" + Encoding.ASCII.GetString(observed));
        await stream.WriteAsync(Response, cancellationToken);
    }

    internal static void CheckRead(NetworkReceiveResult received, CancellationToken cancellationToken)
    {
        if (received.IsCanceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (received.IsCompleted && received.Buffer.IsEmpty)
        {
            throw new EndOfStreamException("The peer closed before sending the expected demo data.");
        }
    }

    internal static void VerifyPattern(in ReadOnlySequence<byte> bytes, int expectedLength)
    {
        if (bytes.Length != expectedLength)
        {
            throw new InvalidDataException("The retained payload length changed.");
        }
        var offset = 0;
        foreach (var segment in bytes)
        {
            for (var index = 0; index < segment.Length; index++)
            {
                if (segment.Span[index] != (byte)(offset++ % 251))
                {
                    throw new InvalidDataException("The retained page data was changed or reused.");
                }
            }
        }
    }
}
