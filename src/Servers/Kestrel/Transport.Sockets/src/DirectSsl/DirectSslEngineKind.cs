// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl;

/// <summary>
/// Selects which TLS engine the DirectSsl transport uses. Both engines have
/// identical I/O shape (epoll-driven, fd-bound SSL_*); only the call site that
/// drives OpenSSL differs. Lets the same binary A/B-compare the cost of the
/// managed wrapper introduced by <see cref="System.Net.Security.TlsContext"/>
/// and <see cref="System.Net.Security.TlsSession"/> against direct P/Invoke.
/// </summary>
public enum DirectSslEngineKind
{
    /// <summary>
    /// Drive OpenSSL through <c>System.Net.Security.TlsContext</c> / <c>TlsSession</c>.
    /// </summary>
    TlsSession = 0,

    /// <summary>
    /// Drive OpenSSL with direct P/Invoke calls (SSL_do_handshake, SSL_read, SSL_write)
    /// hosted inside this assembly.
    /// </summary>
    OpenSslDirect = 1,

    /// <summary>
    /// Hybrid engine: byte-for-byte clone of OpenSslDirect where ONLY the raw SSL primitives
    /// (SSL_new/SSL_set_fd/SSL_do_handshake/SSL_read/SSL_write/SSL_free) are swapped for
    /// TlsSession / TlsContext calls. Everything else — pump, epoll loop, connection wrapper,
    /// buffering — is identical to OpenSslDirect. Used to isolate whether the primitive swap
    /// alone accounts for the persistent-keepalive perf gap.
    /// </summary>
    Hybrid = 2,
}