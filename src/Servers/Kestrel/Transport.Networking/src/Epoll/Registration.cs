// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Hosting;

/// <summary>Registers the non-shipping native epoll TLS experiment.</summary>
public static class EpollTransportExtensions
{
    /// <summary>Uses native OpenSSL on dedicated managed epoll workers and exposes post-TLS plaintext to Kestrel.</summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="certificatePath">The PEM certificate chain file.</param>
    /// <param name="keyPath">The PEM private key file.</param>
    /// <param name="workerCount">The number of dedicated TLS workers.</param>
    /// <returns>The host builder.</returns>
    public static IWebHostBuilder UseEpollTls(this IWebHostBuilder builder, string certificatePath, string keyPath, int workerCount = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        return builder.ConfigureServices(services =>
        {
            services.RemoveAll<IConnectionListenerFactory>();
            services.AddSingleton<IConnectionListenerFactory>(provider => new TransportFactory(
                certificatePath, keyPath, workerCount, provider.GetRequiredService<ILoggerFactory>()));
        });
    }
}
