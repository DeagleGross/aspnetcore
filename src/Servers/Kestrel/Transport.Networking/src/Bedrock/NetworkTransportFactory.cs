// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;
using Microsoft.Extensions.Logging;

namespace System.Net.Transport;

/// <summary>
/// Experimental: hides a <see cref="NetworkEngine"/> behind the existing <c>Microsoft.AspNetCore.Connections</c> contracts.
/// Servers see an <see cref="IConnectionListenerFactory"/> (with <see cref="IConnectionListenerFactorySelector"/> deciding which endpoints it takes),
/// clients see an <see cref="IConnectionFactory"/>, and the native read-lease path stays reachable through <see cref="INetworkConnectionFeature"/>.
/// </summary>
public sealed class NetworkTransportFactory : IConnectionListenerFactory, IConnectionListenerFactorySelector, IConnectionFactory, IAsyncDisposable
{
    private readonly NetworkEngine _engine;
    private readonly NetworkNativeTlsOptions? _nativeTls;

    /// <summary>Creates the engine selected by the options type. Listeners and outbound connections are created lazily.</summary>
    /// <param name="options">Provider and worker options. The options type is the transport selector.</param>
    /// <param name="loggerFactory">Optional provider logging.</param>
    /// <param name="nativeTls">Optional Linux native TLS for bound listeners.</param>
    public NetworkTransportFactory(NetworkEngineOptions options, ILoggerFactory? loggerFactory = null, NetworkNativeTlsOptions? nativeTls = null)
    {
        _engine = NetworkEngineFactory.Create(options, loggerFactory);
        _nativeTls = nativeTls;
    }

    /// <summary>Gets the selected provider.</summary>
    public NetworkEngineKind Kind => _engine.Kind;

    /// <summary>Returns whether the prototype provider handles the endpoint; other endpoints fall through to the next registered factory.</summary>
    /// <param name="endpoint">The endpoint Kestrel asks to bind.</param>
    /// <returns><see langword="true"/> for IPv4 endpoints; the Linux providers also need a fixed nonzero port.</returns>
    public bool CanBind(EndPoint endpoint) => endpoint is IPEndPoint { AddressFamily: AddressFamily.InterNetwork } ip && (OperatingSystem.IsWindows() || ip.Port > 0);

    /// <inheritdoc />
    public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        var listener = await _engine.ListenAsync(endpoint, new NetworkListenOptions { NativeTls = _nativeTls }, cancellationToken).ConfigureAwait(false);
        return new Listener(listener);
    }

    /// <inheritdoc />
    public async ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (_engine is not IConnectableEngine connectable)
        {
            throw new NotSupportedException("The selected engine does not support outbound connections.");
        }
        if (endpoint is DnsEndPoint dns)
        {
            endpoint = await ResolveAsync(dns, cancellationToken).ConfigureAwait(false);
        }
        var connection = await connectable.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return new KestrelConnectionContext(connection);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _engine.DisposeAsync();

    private static async ValueTask<EndPoint> ResolveAsync(DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        foreach (var address in addresses)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return new IPEndPoint(address, endpoint.Port);
            }
        }
        throw new SocketException((int)SocketError.HostNotFound);
    }

    private sealed class Listener(NetworkListener listener) : IConnectionListener
    {
        public EndPoint EndPoint => listener.LocalEndPoint;

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            var connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            return connection is null ? null : new KestrelConnectionContext(connection);
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default) => listener.UnbindAsync(cancellationToken);

        public ValueTask DisposeAsync() => listener.DisposeAsync();
    }
}

/// <summary>Exposes the native connection behind a <see cref="ConnectionContext"/> so a consumer can use retained read leases instead of the pipe adapter.</summary>
public interface INetworkConnectionFeature
{
    /// <summary>Gets the provider connection. Using it directly is exclusive with using <see cref="ConnectionContext.Transport"/>.</summary>
    NetworkConnection Connection { get; }
}
