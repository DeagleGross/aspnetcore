// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Hosting;

/// <summary>Registers TCP with an optional custom-BIO TLS layer for the experiment.</summary>
public static class LayeredTransportExtensions
{
    /// <summary>Uses io_uring data operations with optional transport-driven OpenSSL TLS.</summary>
    /// <param name="builder">The web host builder.</param>
    /// <param name="tls">Whether the transport terminates TLS before exposing data to Kestrel.</param>
    /// <param name="certificatePath">PEM certificate path, or an empty string for plaintext.</param>
    /// <param name="keyPath">PEM private key path, or an empty string for plaintext.</param>
    /// <returns>The web host builder.</returns>
    public static IWebHostBuilder UseIoUringBio(this IWebHostBuilder builder, bool tls, string certificatePath, string keyPath)
        => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IConnectionListenerFactory>();
            services.AddSingleton<IConnectionListenerFactory>(provider =>
                new TransportFactory(tls, certificatePath, keyPath, provider.GetRequiredService<ILoggerFactory>(), layered: true));
        });
}
