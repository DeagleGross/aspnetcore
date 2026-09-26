// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Hosting;

/// <summary>Registers the owned-buffer native transport experiment.</summary>
public static class OwnedTransportExtensions
{
    /// <summary>Uses multishot TCP and a provider-owned input reader.</summary>
    public static IWebHostBuilder UseIoUringTcp(this IWebHostBuilder builder)
        => Register(builder, false, string.Empty, string.Empty);

    /// <summary>Terminates TLS in the native transport, exposing plaintext to Kestrel without UseHttps.</summary>
    public static IWebHostBuilder UseIoUringTls(this IWebHostBuilder builder, string certificatePath, string keyPath)
        => Register(builder, true, certificatePath, keyPath);

    private static IWebHostBuilder Register(IWebHostBuilder builder, bool tls, string cert, string key)
        => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IConnectionListenerFactory>();
            services.AddSingleton<IConnectionListenerFactory>(provider => new TransportFactory(tls, cert, key, provider.GetRequiredService<ILoggerFactory>()));
        });
}
