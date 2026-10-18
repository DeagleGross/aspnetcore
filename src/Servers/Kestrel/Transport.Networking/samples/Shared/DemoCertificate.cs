// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

internal static class DemoCertificate
{
    internal static X509Certificate2 Create(bool exportable = false)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Use the key-provider import required by SChannel, rather than an ephemeral PEM key.
        var flags = exportable ? X509KeyStorageFlags.Exportable : X509KeyStorageFlags.DefaultKeySet;
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, flags);
    }

    internal static SslStream CreateClientStream(Stream network, X509Certificate2 expected)
    {
        return new SslStream(network, leaveInnerStreamOpen: true, (_, certificate, _, _) =>
            certificate is not null && certificate.GetRawCertData().AsSpan().SequenceEqual(expected.RawData));
    }

    internal static SslServerAuthenticationOptions ServerOptions(X509Certificate2 certificate) => new()
    {
        ServerCertificate = certificate,
        EnabledSslProtocols = SslProtocols.Tls13,
        AllowTlsResume = false
    };

    internal static SslClientAuthenticationOptions ClientOptions() => new()
    {
        TargetHost = "localhost",
        EnabledSslProtocols = SslProtocols.Tls13,
        AllowTlsResume = false
    };
}
