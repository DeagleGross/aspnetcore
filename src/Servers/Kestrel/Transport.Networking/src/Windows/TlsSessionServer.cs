// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Security;
using System.Net.Transport;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

/// <summary>
/// The shared server-side <see cref="TlsContext"/> used when NETWORKPROTO_WINDOWS_TLS_IMPL=session selects the runtime's low-level TLS state machine instead of direct SSPI calls.
/// </summary>
internal sealed class TlsSessionServer : IDisposable
{
    internal const string SelectorVariable = "NETWORKPROTO_WINDOWS_TLS_IMPL";
    private readonly X509Certificate2 _certificate;

    internal TlsContext Context { get; }
    internal SslProtocols Protocol { get; } = Schannel.Protocol;
    internal Action<ReadOnlySpan<byte>>? HandshakePrefixCallback { get; }

    internal static bool IsSelected => Environment.GetEnvironmentVariable(SelectorVariable) == "session";

    internal TlsSessionServer(NetworkNativeTlsOptions options)
    {
        HandshakePrefixCallback = options.HandshakePrefixCallback;
        using var pem = X509Certificate2.CreateFromPemFile(options.CertificatePath, options.PrivateKeyPath);
        _certificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.DefaultKeySet);
        try
        {
            Context = TlsContext.CreateServer(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                EnabledSslProtocols = Protocol,
                ClientCertificateRequired = false,
                AllowTlsResume = false
            });
        }
        catch
        {
            _certificate.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Context.Dispose();
        _certificate.Dispose();
    }
}
