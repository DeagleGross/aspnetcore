// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Security;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Engines.Hybrid.Ssl;

/// <summary>
/// Hybrid SslContext — STEP 1 of the incremental TlsSession migration.
///
/// This engine's context is a THIN WRAPPER around a runtime-owned
/// <see cref="TlsContext"/>. We use reflection to force materialization of the
/// TlsContext's internal <c>_sslContext</c> (SafeSslContextHandle) and expose
/// the raw <c>SSL_CTX*</c> via <see cref="Handle"/> so that the rest of the
/// engine (pump, connection state) continues to call raw <c>SSL_new</c>,
/// <c>SSL_set_fd</c>, <c>SSL_read</c>, <c>SSL_write</c> on that pointer
/// exactly as OpenSslDirect does.
///
/// This is the SMALLEST possible delta from OSD: only the origin of the
/// SSL_CTX changes (TlsContext-owned vs. our own <c>SSL_CTX_new</c>).
/// If RPS/fairness change here, the culprit is on the TlsContext SSL_CTX
/// configuration side (ciphers/session-cache/protocol mask), not the pump.
/// </summary>
internal sealed class SslContext : IDisposable
{
    private TlsContext? _tlsContext;
    private IntPtr _sslCtxHandle;
    private bool _disposed;

    /// <summary>Raw <c>SSL_CTX*</c> extracted from the TlsContext via reflection.</summary>
    public IntPtr Handle => _sslCtxHandle;

    /// <summary>Underlying runtime TlsContext — exposed for later migration steps.</summary>
    public TlsContext TlsContext => _tlsContext ?? throw new ObjectDisposedException(nameof(SslContext));

    public SslContext(string certPath, string keyPath)
    {
        if (string.IsNullOrEmpty(certPath))
        {
            throw new ArgumentNullException(nameof(certPath));
        }
        if (string.IsNullOrEmpty(keyPath))
        {
            throw new ArgumentNullException(nameof(keyPath));
        }

        var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);

        var serverOptions = new SslServerAuthenticationOptions
        {
            ServerCertificate = cert,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            AllowTlsResume = true,
        };

        _tlsContext = TlsContext.Create(serverOptions);

        _sslCtxHandle = ExtractRawSslCtx(_tlsContext);
        if (_sslCtxHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to extract SSL_CTX* from TlsContext._sslContext");
        }

        Console.WriteLine($"[SslContext] Hybrid Step 1: wrapped TlsContext, extracted SSL_CTX* = 0x{_sslCtxHandle.ToInt64():X}");
    }

    /// <summary>
    /// Force materialization of <c>TlsContext._sslContext</c> (a lazy
    /// SafeSslContextHandle) by invoking the internal
    /// <c>CreateSessionOptions()</c> method — which calls the OpenSSL-partial
    /// <c>AttachSharedNativeContext</c> that allocates the SSL_CTX. Then
    /// read the private <c>_sslContext</c> field and return its raw handle.
    /// </summary>
    private static IntPtr ExtractRawSslCtx(TlsContext tlsCtx)
    {
        var t = typeof(TlsContext);

        var createSessionOptions = t.GetMethod(
            "CreateSessionOptions",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TlsContext.CreateSessionOptions() not found");
        _ = createSessionOptions.Invoke(tlsCtx, null);

        var sslContextField = t.GetField(
            "_sslContext",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TlsContext._sslContext not found");

        var handle = sslContextField.GetValue(tlsCtx) as SafeHandle;
        return handle?.DangerousGetHandle() ?? IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _tlsContext?.Dispose();
            _tlsContext = null;
            _sslCtxHandle = IntPtr.Zero;
            _disposed = true;
        }
    }

    ~SslContext()
    {
        Dispose();
    }
}
