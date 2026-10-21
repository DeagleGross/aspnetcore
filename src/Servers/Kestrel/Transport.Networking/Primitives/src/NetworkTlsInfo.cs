// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Security;
using System.Security.Authentication;

namespace System.Net.Transport;

/// <summary>Describes the TLS session a provider already established before publishing a connection.</summary>
/// <param name="protocol">The negotiated protocol version.</param>
/// <param name="cipherSuite">The negotiated cipher suite, or null when the provider cannot report it.</param>
public sealed class NetworkTlsInfo(SslProtocols protocol, TlsCipherSuite? cipherSuite)
{
    /// <summary>Gets the negotiated protocol version.</summary>
    public SslProtocols Protocol { get; } = protocol;

    /// <summary>Gets the negotiated cipher suite, or null when the provider cannot report it.</summary>
    public TlsCipherSuite? CipherSuite { get; } = cipherSuite;
}
