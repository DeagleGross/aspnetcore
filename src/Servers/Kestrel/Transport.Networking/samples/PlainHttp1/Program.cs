// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net.Sockets;
using System.Net.Transport;
using System.Text;

var settings = DemoSettings.Parse(args, 5011);
using var cancellation = new CancellationTokenSource(settings.Timeout);
await using var engine = NetworkEngineFactory.Create(settings.EngineOptions);
await using var listener = await engine.ListenAsync(settings.EndPoint, cancellationToken: cancellation.Token);
Console.WriteLine($"Plain HTTP/1 through {engine.Kind}, listening at {listener.LocalEndPoint}");
await OneConnectionDemo.RunAsync(listener, cancellation, RunServerAsync, RunClientAsync);
Console.WriteLine("PASS: exact request, direct borrowed send, response after client half-close and EOF.");

static async Task RunServerAsync(NetworkConnection connection, CancellationToken cancellationToken)
{
    while (true)
    {
        var received = await connection.ReceiveAsync(cancellationToken);
        HttpPayloads.CheckRead(received, cancellationToken);
        Console.WriteLine($"SERVER borrowed snapshot: {received.Buffer.Length} bytes, completed={received.IsCompleted}");
        if (received.Buffer.Length >= HttpPayloads.Request.Length)
        {
            var request = received.Buffer.Slice(0, HttpPayloads.Request.Length).ToArray();
            if (!request.AsSpan().SequenceEqual(HttpPayloads.Request))
            {
                throw new InvalidDataException("The hardcoded HTTP request was corrupted.");
            }
            Console.WriteLine("SERVER request:\n" + Encoding.ASCII.GetString(request));
            connection.AdvanceReceiveTo(received.Buffer.GetPosition(HttpPayloads.Request.Length));
            break;
        }
        if (received.IsCompleted)
        {
            throw new EndOfStreamException("Incomplete HTTP request.");
        }
        connection.AdvanceReceiveTo(received.Buffer.Start, received.Buffer.End);
    }
    await connection.SendAsync(HttpPayloads.Response.AsMemory(), cancellationToken);
    await connection.DisposeAsync();
}

async Task RunClientAsync(CancellationToken cancellationToken)
{
    using var client = new TcpClient();
    await client.ConnectAsync(settings.EndPoint, cancellationToken);
    var stream = client.GetStream();
    await stream.WriteAsync(HttpPayloads.Request, cancellationToken);
    client.Client.Shutdown(SocketShutdown.Send);
    await HttpPayloads.VerifyResponseAsync(stream, cancellationToken);
}
