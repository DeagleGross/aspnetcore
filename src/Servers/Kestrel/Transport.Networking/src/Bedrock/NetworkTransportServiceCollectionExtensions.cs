// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the experimental network transport behind the existing connection abstractions.</summary>
public static class NetworkTransportServiceCollectionExtensions
{
    /// <summary>
    /// Registers one <see cref="NetworkTransportFactory"/> as both <see cref="IConnectionListenerFactory"/> and <see cref="IConnectionFactory"/>.
    /// Kestrel keeps the previously registered factory for endpoints this one declines through <see cref="IConnectionListenerFactorySelector"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Provider and worker options. The options type selects the provider.</param>
    /// <param name="nativeTls">Optional Linux native TLS for bound listeners.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddNetworkTransport(this IServiceCollection services, NetworkEngineOptions options, NetworkNativeTlsOptions? nativeTls = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(provider => new NetworkTransportFactory(options, provider.GetService<ILoggerFactory>(), nativeTls));
        services.AddSingleton<IConnectionListenerFactory>(provider => provider.GetRequiredService<NetworkTransportFactory>());
        services.AddSingleton<IConnectionFactory>(provider => provider.GetRequiredService<NetworkTransportFactory>());
        return services;
    }
}
