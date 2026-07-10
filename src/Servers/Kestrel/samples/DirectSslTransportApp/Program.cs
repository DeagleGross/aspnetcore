// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPNETCORE_DIRECTSSL_001 // Experimental DirectSsl API

using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Set USE_STANDARD_TLS=1 to use standard Kestrel TLS (SslStream) for comparison.
var useStandardTls = Environment.GetEnvironmentVariable("USE_STANDARD_TLS") == "1";

var hostBuilder = new HostBuilder()
    .ConfigureLogging((_, factory) =>
    {
        factory.AddSimpleConsole();
        factory.SetMinimumLevel(LogLevel.Warning);
    })
    .ConfigureServices(services =>
    {
        services.AddRouting();
    })
    .ConfigureWebHost(webHost =>
    {
        if (!useStandardTls)
        {
            Console.WriteLine("Using DirectSsl transport (TlsSocketSession)");

            webHost.UseKestrelDirectSslTransport();

            webHost.UseDirectSslSockets(options =>
            {
                options.CertificatePath = "server-p256.crt";
                options.PrivateKeyPath = "server-p256.key";
                options.WorkerCount = 4;
            });

            webHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(5001, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http1;
                });
            });
        }
        else
        {
            Console.WriteLine("Using standard Kestrel TLS (SslStream)");

            webHost.UseKestrel(options =>
            {
                options.ListenAnyIP(5001, listenOptions =>
                {
                    listenOptions.UseHttps(X509CertificateLoader.LoadPkcs12FromFile("server-p256.pfx", "testpassword"));
                    listenOptions.Protocols = HttpProtocols.Http1;
                });
            });
        }

        webHost.Configure(app =>
        {
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/", async context =>
                {
                    await context.Response.WriteAsync("Hello world");
                });
            });
        });
    });

await hostBuilder.Build().RunAsync();
