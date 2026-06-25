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
    /// This is the "this run" / runtime-PoC path.
    /// </summary>
    TlsSession = 0,

    /// <summary>
    /// Drive OpenSSL with direct P/Invoke calls (<c>SSL_do_handshake</c>,
    /// <c>SSL_read</c>, <c>SSL_write</c>) hosted inside this assembly.
    /// This is the "Net10 Private" path resurrected from
    /// <c>dmkorolev/internal/native-tls-transport@82e1b108</c>.
    /// </summary>
    OpenSslDirect = 1,
}
