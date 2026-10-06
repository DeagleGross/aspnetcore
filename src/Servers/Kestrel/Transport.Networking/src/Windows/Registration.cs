// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Hosting;

/// <summary>Registers the non-shipping Windows TCP transport experiments.</summary>
public static class WindowsTransportExtensions
{
    /// <summary>Uses dedicated IOCP workers for TCP. Add UseHttps separately for SChannel TLS.</summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="workerCount">The number of owning completion workers.</param>
    /// <returns>The host builder.</returns>
    public static IWebHostBuilder UseIocp(this IWebHostBuilder builder, int workerCount = 4)
    {
        return Register(builder, workerCount, rio: false);
    }

    /// <summary>Uses registered Winsock I/O on dedicated workers. Add UseHttps separately for SChannel TLS.</summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="workerCount">The number of owning completion workers.</param>
    /// <returns>The host builder.</returns>
    public static IWebHostBuilder UseRio(this IWebHostBuilder builder, int workerCount = 4)
    {
        return Register(builder, workerCount, rio: true);
    }

    private static IWebHostBuilder Register(IWebHostBuilder builder, int workerCount, bool rio)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        Native.ValidatePlatform();
        return builder.ConfigureServices(services =>
        {
            services.RemoveAll<IConnectionListenerFactory>();
            services.AddSingleton<IConnectionListenerFactory>(provider => new TransportFactory(rio, workerCount,
                provider.GetRequiredService<ILoggerFactory>()));
        });
    }
}
