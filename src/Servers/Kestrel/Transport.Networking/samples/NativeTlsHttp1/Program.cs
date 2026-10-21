// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net.Sockets;
using System.Net.Transport;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

var settings = DemoSettings.Parse(args, 5015);
using var cancellation = new CancellationTokenSource(settings.Timeout);
using var certificate = DemoCertificate.Create(exportable: true);
var folder = Directory.CreateTempSubdirectory("networkproto-native-tls-").FullName;
var certificatePath = Path.Combine(folder, "certificate.pem");
var keyPath = Path.Combine(folder, "key.pem");
try
{
    using var key = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("The fixture requires an RSA key.");
    await File.WriteAllTextAsync(certificatePath, certificate.ExportCertificatePem(), cancellation.Token);
    await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), cancellation.Token);
    await using var engine = NetworkEngineFactory.Create(settings.EngineOptions);
    await using var listener = await engine.ListenAsync(settings.EndPoint, new NetworkListenOptions
    {
        NativeTls = new NetworkNativeTlsOptions { CertificatePath = certificatePath, PrivateKeyPath = keyPath }
    }, cancellation.Token);
    Console.WriteLine($"Transport-owned TLS through {engine.Kind}, listening at {listener.LocalEndPoint}");
    await OneConnectionDemo.RunAsync(listener, cancellation, RunServerAsync, RunClientAsync);
    Console.WriteLine("PASS: transport authenticates TLS and exposes plaintext; no server SslStream.");
}
finally
{
    File.Delete(certificatePath);
    File.Delete(keyPath);
    Directory.Delete(folder);
}

static async Task RunServerAsync(NetworkConnection connection, CancellationToken cancellationToken)
{
    if (!connection.IsTls)
    {
        throw new InvalidOperationException("The transport published a connection without completing native TLS.");
    }
    var tlsInfo = connection.TlsInfo ?? throw new InvalidOperationException("The provider did not report TLS facts for a native TLS connection.");
    Console.WriteLine($"SERVER sees provider-reported TLS: {tlsInfo.Protocol}, {tlsInfo.CipherSuite}");
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
            Console.WriteLine("SERVER receives plaintext directly:\n" + Encoding.ASCII.GetString(request));
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
    await tls.WriteAsync(HttpPayloads.Request, cancellationToken);
    await HttpPayloads.VerifyResponseAsync(tls, cancellationToken);
    await tls.ShutdownAsync();
    client.Client.Shutdown(SocketShutdown.Send);
}
