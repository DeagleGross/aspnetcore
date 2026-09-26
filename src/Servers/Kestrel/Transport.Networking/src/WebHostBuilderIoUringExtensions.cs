// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.AspNetCore.Hosting;

/// <summary>Registers the Linux-only, non-shipping io_uring transport prototype.</summary>
public static class WebHostBuilderIoUringExtensions
{
    /// <summary>
    /// Replaces stream listener factories with real io_uring TCP I/O. Configure
    /// HTTPS using Kestrel's normal HTTPS middleware; this transport moves ciphertext.
    /// </summary>
    /// <param name="builder">The web host builder.</param>
    /// <returns>The same builder.</returns>
    public static IWebHostBuilder UseIoUring(this IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The io_uring prototype requires Linux.");
        }
        return builder.ConfigureServices(services =>
        {
            services.RemoveAll<IConnectionListenerFactory>();
            services.AddSingleton<IConnectionListenerFactory, IoUringTransportFactory>();
        });
    }
}
