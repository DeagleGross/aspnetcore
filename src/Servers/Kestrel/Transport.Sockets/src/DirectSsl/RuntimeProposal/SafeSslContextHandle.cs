// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0073, IDE0011, IDE0005, CA1822

using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace System.Net.Security;

// PROTOTYPE — would live in dotnet/runtime as System.Net.Security.SafeSslContextHandle
//
// Wraps OpenSSL SSL_CTX*. Created via SSL_CTX_new(TLS_server_method()),
// released via SSL_CTX_free. Configuration (cert, protocols, ALPN, session
// cache) is via instance methods.

internal sealed class SafeSslContextHandle : SafeHandle
{
    public static bool IsSupported => OperatingSystem.IsLinux();

    public SafeSslContextHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public SafeSslContextHandle(IntPtr handle, bool ownsHandle) : base(IntPtr.Zero, ownsHandle)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
        {
            Native.SSL_CTX_free(handle);
            SetHandle(IntPtr.Zero);
        }
        return true;
    }

    internal IntPtr DangerousHandle => handle;

    /// <summary>SSL_CTX_new(TLS_server_method())</summary>
    public static SafeSslContextHandle CreateServer()
    {
        Native.Initialize();
        IntPtr method = Native.TLS_server_method();
        if (method == IntPtr.Zero)
        {
            throw new SslException("TLS_server_method failed", Native.GetErrorString());
        }

        IntPtr ctx = Native.SSL_CTX_new(method);
        if (ctx == IntPtr.Zero)
        {
            throw new SslException("SSL_CTX_new failed", Native.GetErrorString());
        }

        return new SafeSslContextHandle(ctx, ownsHandle: true);
    }

    /// <summary>SSL_CTX_new(TLS_client_method())</summary>
    public static SafeSslContextHandle CreateClient()
        => throw new NotImplementedException("PROTOTYPE: client not wired");

    /// <summary>SSL_CTX_use_certificate + SSL_CTX_use_PrivateKey from a managed cert.</summary>
    public void UseCertificate(X509Certificate2 certificate)
    {
        _ = certificate;
        _ = handle;
        throw new NotImplementedException(
            "PROTOTYPE: in-memory cert loading not wired. Use UseCertificateFile instead.");
    }

    /// <summary>Pre-built cert context — the type SslStream already accepts.</summary>
    public void UseCertificateContext(SslStreamCertificateContext certificateContext)
    {
        _ = certificateContext;
        _ = handle;
        throw new NotImplementedException(
            "PROTOTYPE: SslStreamCertificateContext not wired. Use UseCertificateFile instead.");
    }

    /// <summary>PROTOTYPE-ONLY: load PEM cert + key from file paths.</summary>
    public void UseCertificateFile(string certPath, string keyPath)
    {
        if (Native.SSL_CTX_use_certificate_file(handle, certPath, Native.SSL_FILETYPE_PEM) <= 0)
        {
            throw new SslException(
                $"SSL_CTX_use_certificate_file failed for {certPath}",
                Native.GetErrorString());
        }
        if (Native.SSL_CTX_use_PrivateKey_file(handle, keyPath, Native.SSL_FILETYPE_PEM) <= 0)
        {
            throw new SslException(
                $"SSL_CTX_use_PrivateKey_file failed for {keyPath}",
                Native.GetErrorString());
        }
    }

    /// <summary>SSL_CTX_check_private_key</summary>
    public void CheckPrivateKey()
    {
        if (Native.SSL_CTX_check_private_key(handle) <= 0)
        {
            throw new SslException(
                "SSL_CTX_check_private_key failed: private key does not match certificate",
                Native.GetErrorString());
        }
    }

    /// <summary>SSL_CTX_set_min_proto_version / SSL_CTX_set_max_proto_version</summary>
    public void SetProtocols(SslProtocols protocols)
    {
        _ = protocols;
        _ = handle;
        // PROTOTYPE: TLS_server_method already enables TLS 1.2 + 1.3 by default; no-op.
    }

    /// <summary>SSL_CTX_set_alpn_protos</summary>
    public void SetApplicationProtocols(IList<SslApplicationProtocol> protocols)
    {
        _ = protocols;
        _ = handle;
        // PROTOTYPE: ALPN selection callback not wired.
    }

    /// <summary>SSL_CTX_set_session_cache_mode</summary>
    public void SetSessionCacheMode(SslSessionCacheMode mode)
    {
        int openSslMode = mode switch
        {
            SslSessionCacheMode.Off    => Native.SSL_SESS_CACHE_OFF,
            SslSessionCacheMode.Client => Native.SSL_SESS_CACHE_CLIENT,
            SslSessionCacheMode.Server => Native.SSL_SESS_CACHE_SERVER,
            SslSessionCacheMode.Both   => Native.SSL_SESS_CACHE_BOTH,
            _                          => Native.SSL_SESS_CACHE_OFF,
        };
        Native.SSL_CTX_set_session_cache_mode(handle, openSslMode);
    }

    /// <summary>SSL_CTX_sess_set_cache_size</summary>
    public void SetSessionCacheSize(int size) => Native.SSL_CTX_sess_set_cache_size(handle, size);

    /// <summary>SSL_CTX_set_timeout (in seconds)</summary>
    public void SetSessionTimeout(TimeSpan timeout) => Native.SSL_CTX_set_timeout(handle, (long)timeout.TotalSeconds);
}
