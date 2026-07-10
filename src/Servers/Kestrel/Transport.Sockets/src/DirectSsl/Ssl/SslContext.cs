// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Ssl;

/// <summary>
/// Owns the server-side <see cref="TlsContext"/> shared by every connection on the transport.
/// The context is built once from the configured certificate and reused across handshakes,
/// so certificate loading and OpenSSL context setup happen a single time at startup.
/// Thread-safe once constructed - the underlying context is created for concurrent sessions.
/// </summary>
internal sealed class SslContext : IDisposable
{
    private readonly X509Certificate2 _certificate;
    private readonly TlsContext _context;
    private bool _disposed;

    /// <summary>
    /// The shared server <see cref="TlsContext"/> used to create per-connection sessions.
    /// </summary>
    public TlsContext Context => _context;

    public SslContext(string certPath, string keyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(certPath);
        ArgumentException.ThrowIfNullOrEmpty(keyPath);

        // Force the pure SSL_set_fd handshake fast path: with server options supplied up
        // front (ServerCertificate below), disabling ClientHello capture makes the runtime
        // bind the socket to OpenSSL immediately instead of running the managed ClientHello
        // peek (TryPeekClientHello). The peek path reads the fd eagerly and throws on a
        // not-yet-arrived or reset connection under load; fd-mode returns NeedMoreData
        // (WANT_READ) cleanly and drives handshake/read/write entirely through the fd.
        // Must be set before the first session is created (i.e. before any connection is
        // accepted), which holds because the context is built once at transport bind time.
        AppContext.SetSwitch("System.Net.Security.CaptureClientHello", false);

        // Load the PEM certificate + private key into a single X509Certificate2 that the
        // runtime TLS stack can consume. On Linux this key is usable directly by OpenSSL.
        _certificate = X509Certificate2.CreateFromPemFile(certPath, keyPath);

        var options = new SslServerAuthenticationOptions
        {
            ServerCertificate = _certificate,
        };

        _context = TlsContext.CreateServer(options);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _context.Dispose();
            _certificate.Dispose();
            _disposed = true;
        }
    }
}
