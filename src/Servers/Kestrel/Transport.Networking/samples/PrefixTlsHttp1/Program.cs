// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net.Sockets;
using System.Net.Transport;
using System.Text;

var settings = DemoSettings.Parse(args, 5014);
using var cancellation = new CancellationTokenSource(settings.Timeout);
using var certificate = DemoCertificate.Create();
await using var engine = NetworkEngineFactory.Create(settings.EngineOptions);
await using var listener = await engine.ListenAsync(settings.EndPoint, cancellationToken: cancellation.Token);
Console.WriteLine($"Raw prefix then TLS through {engine.Kind}, listening at {listener.LocalEndPoint}");
await OneConnectionDemo.RunAsync(listener, cancellation, RunServerAsync, RunClientAsync);
Console.WriteLine("PASS: pipe greeting, consumed prefix, preserved prefetched ClientHello and TLS HTTP/1.");

async Task RunServerAsync(NetworkConnection connection, CancellationToken cancellationToken)
{
    await connection.Output.WriteAsync(HttpPayloads.Greeting, cancellationToken);
    Console.WriteLine("SERVER sent a plaintext greeting through pipe output.");
    while (true)
    {
        var received = await connection.ReceiveAsync(cancellationToken);
        HttpPayloads.CheckRead(received, cancellationToken);
        if (received.Buffer.Length > HttpPayloads.Prefix.Length)
        {
            if (!received.Buffer.Slice(0, HttpPayloads.Prefix.Length).ToArray().AsSpan().SequenceEqual(HttpPayloads.Prefix))
            {
                throw new InvalidDataException("The raw protocol prefix was corrupted.");
            }
            Console.WriteLine($"SERVER consumes {HttpPayloads.Prefix.Length} prefix bytes, leaving {received.Buffer.Length - HttpPayloads.Prefix.Length} TLS bytes.");
            connection.AdvanceReceiveTo(received.Buffer.GetPosition(HttpPayloads.Prefix.Length), received.Buffer.End);
            break;
        }
        if (received.IsCompleted)
        {
            throw new EndOfStreamException("The peer closed before prefix plus ClientHello.");
        }
        connection.AdvanceReceiveTo(received.Buffer.Start, received.Buffer.End);
    }
    using (var tls = await connection.AuthenticateAsServerAsync(DemoCertificate.ServerOptions(certificate), cancellationToken))
    {
        await HttpPayloads.ReplyOverStreamAsync(tls, cancellationToken);
        await tls.ShutdownAsync();
    }
}

async Task RunClientAsync(CancellationToken cancellationToken)
{
    using var client = new TcpClient();
    await client.ConnectAsync(settings.EndPoint, cancellationToken);
    var network = client.GetStream();
    var greeting = new byte[HttpPayloads.Greeting.Length];
    await network.ReadExactlyAsync(greeting, cancellationToken);
    if (!greeting.AsSpan().SequenceEqual(HttpPayloads.Greeting))
    {
        throw new InvalidDataException("The plaintext greeting was corrupted.");
    }
    Console.WriteLine("CLIENT greeting: " + Encoding.ASCII.GetString(greeting));
    await network.WriteAsync(HttpPayloads.Prefix, cancellationToken);
    using var tls = DemoCertificate.CreateClientStream(network, certificate);
    await tls.AuthenticateAsClientAsync(DemoCertificate.ClientOptions(), cancellationToken);
    await tls.WriteAsync(HttpPayloads.Request, cancellationToken);
    await HttpPayloads.VerifyResponseAsync(tls, cancellationToken);
    await tls.ShutdownAsync();
    client.Client.Shutdown(SocketShutdown.Send);
}
