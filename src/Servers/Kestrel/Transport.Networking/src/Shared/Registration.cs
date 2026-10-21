// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Hosting;

/// <summary>Registers Kestrel above the experimental shared engine and connection API.</summary>
public static class SharedNetworkTransportExtensions
{
    /// <summary>Uses the shared owned-memory API without changing Kestrel's HTTP implementation.</summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="options">Typed engine and worker options.</param>
    /// <param name="nativeTls">Optional explicitly selected Linux native TLS fast path.</param>
    /// <returns>The host builder.</returns>
    public static IWebHostBuilder UseNetworkTransport(this IWebHostBuilder builder, NetworkEngineOptions options, NetworkNativeTlsOptions? nativeTls = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.ConfigureServices(services =>
        {
            services.RemoveAll<IConnectionListenerFactory>();
            services.AddSingleton<IConnectionListenerFactory>(provider => new KestrelFactory(
                NetworkEngineFactory.Create(options, provider.GetRequiredService<ILoggerFactory>()), nativeTls));
        });
    }

    private sealed class KestrelFactory(NetworkEngine engine, NetworkNativeTlsOptions? tls) : IConnectionListenerFactory, IAsyncDisposable
    {
        public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            return new KestrelListener(await engine.ListenAsync(endpoint, new NetworkListenOptions { NativeTls = tls }, cancellationToken).ConfigureAwait(false));
        }
        public ValueTask DisposeAsync() => engine.DisposeAsync();
    }

    private sealed class KestrelListener(NetworkListener listener) : IConnectionListener
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
