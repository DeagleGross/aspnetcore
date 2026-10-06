// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

internal static class TlsProbe
{
    internal static async Task RunAsync(string certificatePath, string keyPath)
    {
        using var pem = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        var pfx = pem.Export(X509ContentType.Pkcs12);
        foreach (var protocol in new[] { SslProtocols.Tls13, SslProtocols.Tls12 })
        {
            for (var mode = 0; mode < 3; mode++)
            {
                using var certificate = mode == 0
                    ? X509Certificate2.CreateFromPemFile(certificatePath, keyPath)
                    : X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                using var client = new TcpClient();
                await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
                using var server = await listener.AcceptTcpClientAsync(deadline.Token);
                using var serverTls = new SslStream(server.GetStream(), false);
                using var clientTls = new SslStream(client.GetStream(), false, static (_, _, _, _) => true);
                try
                {
                    var serverAuth = serverTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        EnabledSslProtocols = protocol,
                        AllowTlsResume = mode == 2
                    }, deadline.Token);
                    var clientAuth = clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        EnabledSslProtocols = protocol,
                        AllowTlsResume = false
                    }, deadline.Token);
                    await Task.WhenAll(serverAuth, clientAuth).WaitAsync(deadline.Token);
                    Console.WriteLine($"TLS_PROBE PASS protocol={protocol} mode={mode} cipher={clientTls.NegotiatedCipherSuite}");
                }
                catch (Exception error) when (error is AuthenticationException or OperationCanceledException or IOException)
                {
                    Console.WriteLine($"TLS_PROBE FAIL protocol={protocol} mode={mode} error={error.GetBaseException().Message}");
                }
            }
        }
    }
}
