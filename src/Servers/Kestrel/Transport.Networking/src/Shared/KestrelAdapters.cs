// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Security;
using System.Net.Transport;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

/// <summary>
/// The Kestrel view of a <see cref="NetworkConnection"/>. This and <see cref="RawKestrelListenerFactory"/> are the only
/// places the shared path touches a Kestrel connection type: providers and workers publish Kestrel-free connections.
/// </summary>
internal sealed class KestrelConnectionContext : DefaultConnectionContext
{
    private readonly NetworkConnection _network;

    internal KestrelConnectionContext(NetworkConnection network) : base(network.Id)
    {
        _network = network;
        LocalEndPoint = network.LocalEndPoint;
        RemoteEndPoint = network.RemoteEndPoint;
        ConnectionClosed = network.ConnectionClosed;
        Transport = network.AsDuplexPipe();
        if (network.TlsInfo is { } info)
        {
            var feature = new TlsInfoFeature(info);
            Features.Set<ITlsConnectionFeature>(feature);
            Features.Set<ITlsHandshakeFeature>(feature);
        }
    }

    public override void Abort(ConnectionAbortedException abortReason) => _network.Abort(abortReason);

    public override async ValueTask DisposeAsync()
    {
        await _network.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Exposes provider-reported TLS facts as Kestrel's TLS features. Obsolete algorithm properties are derived from the suite name.</summary>
internal sealed class TlsInfoFeature(NetworkTlsInfo info) : ITlsConnectionFeature, ITlsHandshakeFeature
{
    public X509Certificate2? ClientCertificate { get; set; }
    public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken) => Task.FromResult(ClientCertificate);
    public SslProtocols Protocol => info.Protocol;
    public TlsCipherSuite? NegotiatedCipherSuite => info.CipherSuite;
    public string HostName => string.Empty;
#pragma warning disable SYSLIB0058
    public CipherAlgorithmType CipherAlgorithm => Suite switch
    {
        var name when name.Contains("AES_256", StringComparison.Ordinal) => CipherAlgorithmType.Aes256,
        var name when name.Contains("AES_128", StringComparison.Ordinal) => CipherAlgorithmType.Aes128,
        _ => CipherAlgorithmType.None
    };
    public int CipherStrength => Suite switch
    {
        var name when name.Contains("AES_256", StringComparison.Ordinal) || name.Contains("CHACHA20", StringComparison.Ordinal) => 256,
        var name when name.Contains("AES_128", StringComparison.Ordinal) => 128,
        _ => 0
    };
    public HashAlgorithmType HashAlgorithm => Suite switch
    {
        var name when name.EndsWith("SHA384", StringComparison.Ordinal) => HashAlgorithmType.Sha384,
        var name when name.EndsWith("SHA256", StringComparison.Ordinal) => HashAlgorithmType.Sha256,
        var name when name.EndsWith("_SHA", StringComparison.Ordinal) => HashAlgorithmType.Sha1,
        _ => HashAlgorithmType.None
    };
    public int HashStrength => HashAlgorithm switch
    {
        HashAlgorithmType.Sha384 => 384,
        HashAlgorithmType.Sha256 => 256,
        HashAlgorithmType.Sha1 => 160,
        _ => 0
    };
    public ExchangeAlgorithmType KeyExchangeAlgorithm => Suite.Length == 0 ? ExchangeAlgorithmType.None : ExchangeAlgorithmType.DiffieHellman;
    public int KeyExchangeStrength => 0;
#pragma warning restore SYSLIB0058
    private string Suite => info.CipherSuite?.ToString() ?? string.Empty;
}

/// <summary>Adapts a Kestrel-free provider listener factory to Kestrel when the provider publishes legacy raw connections.</summary>
internal sealed class RawKestrelListenerFactory(IProviderListenerFactory factory) : IConnectionListenerFactory
{
    public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        return new RawKestrelListener(await factory.BindAsync(endpoint, cancellationToken).ConfigureAwait(false));
    }

    private sealed class RawKestrelListener(IProviderListener listener) : IConnectionListener
    {
        public EndPoint EndPoint => listener.EndPoint;

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            var connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            return connection switch
            {
                null => null,
                ConnectionContext context => context,
                _ => throw new InvalidOperationException("The raw provider published a connection that is not a Kestrel ConnectionContext.")
            };
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default) => listener.UnbindAsync(cancellationToken);
        public ValueTask DisposeAsync() => listener.DisposeAsync();
    }
}
