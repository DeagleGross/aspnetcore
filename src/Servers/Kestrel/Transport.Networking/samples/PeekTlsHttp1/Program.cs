// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net.Sockets;
using System.Net.Transport;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

var settings = DemoSettings.Parse(args, 5013);
using var cancellation = new CancellationTokenSource(settings.Timeout);
using var certificate = DemoCertificate.Create(exportable: true);
var folder = Directory.CreateTempSubdirectory("networkproto-peek-tls-").FullName;
var certificatePath = Path.Combine(folder, "certificate.pem");
var keyPath = Path.Combine(folder, "key.pem");
var observed = 0;
var clientAuthenticated = 0;
try
{
    using var key = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("The fixture requires an RSA key.");
    await File.WriteAllTextAsync(certificatePath, certificate.ExportCertificatePem(), cancellation.Token);
    await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), cancellation.Token);
    await using var engine = NetworkEngineFactory.Create(settings.EngineOptions);
    await using var listener = await engine.ListenAsync(settings.EndPoint, new NetworkListenOptions
    {
        NativeTls = new NetworkNativeTlsOptions
        {
            CertificatePath = certificatePath,
            PrivateKeyPath = keyPath,
            HandshakePrefixCallback = prefix =>
            {
                if (prefix.IsEmpty || prefix[0] != 22 || Volatile.Read(ref clientAuthenticated) != 0)
                {
                    throw new InvalidDataException("Expected raw TLS handshake bytes before the client authenticated.");
                }
                if (Interlocked.Increment(ref observed) != 1)
                {
                    throw new InvalidOperationException("The prefix observer must run exactly once for this connection.");
                }
                Console.WriteLine($"SERVER raw TLS prefix before native handshake: {Convert.ToHexString(prefix[..Math.Min(prefix.Length, 16)])}");
            }
        }
    }, cancellation.Token);
    Console.WriteLine($"Native TLS prefix observation through {engine.Kind}, listening at {listener.LocalEndPoint}");
    await OneConnectionDemo.RunAsync(listener, cancellation, RunServerAsync, RunClientAsync);
    Console.WriteLine("PASS: raw ClientHello prefix observed once before authentication; native TLS HTTP and close completed without a server SslStream.");
}
finally
{
    File.Delete(certificatePath);
    File.Delete(keyPath);
    Directory.Delete(folder);
}

async Task RunServerAsync(NetworkConnection connection, CancellationToken cancellationToken)
{
    if (!connection.IsTls || Volatile.Read(ref observed) != 1)
    {
        throw new InvalidOperationException("The native TLS connection was published before prefix observation/authentication.");
    }
    while (true)
    {
        var received = await connection.ReceiveAsync(cancellationToken);
        HttpPayloads.CheckRead(received, cancellationToken);
        if (received.Buffer.Length >= HttpPayloads.Request.Length)
        {
            var request = received.Buffer.Slice(0, HttpPayloads.Request.Length).ToArray();
            if (!request.AsSpan().SequenceEqual(HttpPayloads.Request))
            {
                throw new InvalidDataException("The native TLS plaintext request was corrupted.");
            }
            Console.WriteLine("SERVER receives authenticated plaintext:\n" + Encoding.ASCII.GetString(request));
            connection.AdvanceReceiveTo(received.Buffer.GetPosition(HttpPayloads.Request.Length));
            break;
        }
        if (received.IsCompleted)
        {
            throw new EndOfStreamException("Incomplete native TLS HTTP request.");
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
    using var tls = DemoCertificate.CreateClientStream(client.GetStream(), certificate);
    var options = DemoCertificate.ClientOptions();
    options.EnabledSslProtocols = OperatingSystem.IsWindows() ? SslProtocols.Tls13 : SslProtocols.Tls12;
    await tls.AuthenticateAsClientAsync(options, cancellationToken);
    Volatile.Write(ref clientAuthenticated, 1);
    await tls.WriteAsync(HttpPayloads.Request, cancellationToken);
    await HttpPayloads.VerifyResponseAsync(tls, cancellationToken);
    await tls.ShutdownAsync();
    client.Client.Shutdown(SocketShutdown.Send);
}
