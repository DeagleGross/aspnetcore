// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
var backend = builder.Configuration["backend"] ?? "sockets";
if (backend is not ("sockets" or "io_uring" or "IoUringTcp" or "IoUringTls" or "IoUringBio" or "epollTls" or "epollTcp" or "iocp" or "rio"))
{
    throw new ArgumentException("Use --backend sockets, io_uring, IoUringTcp, IoUringTls, IoUringBio, epollTls, epollTcp, iocp or rio.");
}
var port = builder.Configuration.GetValue("port", 5443);
var scheme = builder.Configuration["scheme"] ?? (backend is "epollTcp" or "iocp" or "rio" ? "http" : "https");
if (scheme is not ("http" or "https"))
{
    throw new ArgumentException("Use --scheme http or --scheme https.");
}
if (backend == "epollTcp" && scheme != "http")
{
    throw new ArgumentException("The epollTcp sample is HTTP-only; use epollTls for native TLS.");
}
using var certificate = scheme == "https"
    ? LoadCertificate(builder.Configuration)
    : null;
var tlsProtocols = builder.Configuration["tls-protocol"] switch
{
    null or nameof(SslProtocols.Tls12) => SslProtocols.Tls12,
    nameof(SslProtocols.Tls13) => SslProtocols.Tls13,
    _ => throw new ArgumentException("Use --tls-protocol Tls12 or Tls13.")
};
if (tlsProtocols != SslProtocols.Tls12 && backend is "IoUringTls" or "IoUringBio" or "epollTls")
{
    throw new ArgumentException("The native Linux TLS prototypes are fixed to TLS 1.2.");
}
var counters = new Counters();
var payload = new string('x', 1023) + "\n";
if (backend == "io_uring")
{
    builder.WebHost.UseIoUring();
}
if (backend == "IoUringTcp")
{
    builder.WebHost.UseIoUringTcp();
}
if (backend == "IoUringTls")
{
    if (scheme != "https")
    {
        throw new ArgumentException("IoUringTls exposes HTTPS only.");
    }
    builder.WebHost.UseIoUringTls(builder.Configuration["cert"]!, builder.Configuration["key"]!);
}
if (backend == "epollTls")
{
    if (scheme != "https")
    {
        throw new ArgumentException("epollTls exposes HTTPS only.");
    }
    builder.WebHost.UseEpollTls(builder.Configuration["cert"]!, builder.Configuration["key"]!, builder.Configuration.GetValue("workers", 4));
}
if (backend == "epollTcp")
{
    builder.WebHost.UseEpollTcp(builder.Configuration.GetValue("workers", 4));
}
if (backend == "iocp")
{
    builder.WebHost.UseIocp(builder.Configuration.GetValue("workers", 4));
}
if (backend == "rio")
{
    builder.WebHost.UseRio(builder.Configuration.GetValue("workers", 4));
}
var quiet = builder.Configuration.GetValue("minimal", false);
if (backend == "IoUringBio")
{
    builder.WebHost.UseIoUringBio(scheme == "https", builder.Configuration["cert"] ?? string.Empty, builder.Configuration["key"] ?? string.Empty);
}
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port, listen =>
    {
        listen.Protocols = HttpProtocols.Http1;
        listen.Use(next => async connection =>
        {
            if ((backend == "io_uring" && !connection.ConnectionId.StartsWith("io-uring-", StringComparison.Ordinal))
                || ((backend.StartsWith("IoUring", StringComparison.Ordinal) || backend is "epollTls" or "epollTcp") && !connection.ConnectionId.StartsWith("owned-", StringComparison.Ordinal))
                || (backend is "iocp" or "rio" && !connection.ConnectionId.StartsWith($"owned-{backend}-", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Configured backend does not match the actual connection.");
            }
            var state = new ConnectionState();
            connection.Features.Set(state);
            Interlocked.Increment(ref counters.Accepted);
            try
            {
                await next(connection);
            }
            finally
            {
                Interlocked.Increment(ref counters.Closed);
            }
        });

        if (scheme == "https" && backend is not ("IoUringTls" or "IoUringBio" or "epollTls"))
        {
            listen.UseHttps(https =>
            {
                https.ServerCertificate = certificate;
                https.SslProtocols = tlsProtocols;
                https.OnAuthenticate = (_, ssl) =>
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        ssl.CipherSuitesPolicy = new CipherSuitesPolicy([TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256]);
                    }
                    ssl.AllowTlsResume = false;
                };
            });
            listen.Use(next => async connection =>
            {
                Interlocked.Increment(ref counters.Handshakes);
                await next(connection);
            });
        }
        else if (scheme == "https")
        {
            listen.Use(next => async connection =>
            {
                Interlocked.Increment(ref counters.Handshakes);
                await next(connection);
            });
        }
    });
});
var app = builder.Build();
if (builder.Configuration.GetValue("check-windows", false))
{
    if (backend is not ("iocp" or "rio") || scheme != "http")
    {
        throw new ArgumentException("The Windows transport check requires iocp or rio with --scheme http.");
    }
    try
    {
        await WindowsTransportCheck.RunAsync(app.Services.GetRequiredService<IConnectionListenerFactory>(), port);
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL Windows transport: {error}");
        Environment.ExitCode = 1;
    }
    return;
}
if (builder.Configuration.GetValue("check-epoll-tcp", false))
{
    if (backend != "epollTcp")
    {
        throw new ArgumentException("Raw epoll TCP check requires epollTcp.");
    }
    try
    {
        await EpollTcpCheck.RunAsync(app.Services.GetRequiredService<IConnectionListenerFactory>(), port);
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL epoll TCP: {error}");
        Environment.ExitCode = 1;
    }
    return;
}
if (builder.Configuration.GetValue("check-uring-lifecycle", false))
{
    if (backend is not ("IoUringTcp" or "IoUringTls" or "IoUringBio"))
    {
        throw new ArgumentException("Ring lifecycle check requires a managed io_uring backend.");
    }
    try
    {
        await RingLifecycleCheck.RunAsync(app.Services.GetRequiredService<IConnectionListenerFactory>(), port);
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL ring lifecycle: {error}");
        Environment.ExitCode = 1;
    }
    return;
}
if (builder.Configuration.GetValue("check-epoll-lifecycle", false))
{
    if (backend is not ("epollTls" or "epollTcp"))
    {
        throw new ArgumentException("Epoll lifecycle check requires epollTls or epollTcp.");
    }
    try
    {
        await EpollLifecycleCheck.RunAsync(app.Services.GetRequiredService<IConnectionListenerFactory>(), port, backend == "epollTls");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL epoll lifecycle: {error}");
        Environment.ExitCode = 1;
    }
    return;
}
if (builder.Configuration.GetValue("check-final-send", false))
{
    if (!((scheme == "http" && backend is "IoUringTcp" or "IoUringBio" or "epollTcp")
        || (scheme == "https" && backend is "IoUringTls" or "IoUringBio" or "epollTls")))
    {
        throw new ArgumentException("Final-send check requires a native TCP or native post-TLS transport.");
    }
    try
    {
        await FinalSendCheck.RunAsync(app.Services.GetRequiredService<IConnectionListenerFactory>(), port, certificate);
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL final-send: {error}");
        Environment.ExitCode = 1;
    }
    return;
}
if (builder.Configuration.GetValue("check-rejected-accept", false))
{
    if (scheme != "https" || backend is not ("IoUringTls" or "IoUringBio" or "epollTls"))
    {
        throw new ArgumentException("Rejected-accept check requires IoUringTls, IoUringBio or epollTls with HTTPS.");
    }
    try
    {
        await RejectedAcceptCheck.RunAsync(app.Services.GetRequiredService<IConnectionListenerFactory>(), port, certificate!);
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"FAIL rejected-accept: {error}");
        Environment.ExitCode = 1;
    }
    return;
}
app.MapGet("/", async context =>
{
    var state = context.Features.Get<ConnectionState>() ?? throw new InvalidOperationException("Missing connection state.");
    var tls = context.Features.Get<ITlsHandshakeFeature>();
    if ((tls is not null) != (scheme == "https"))
    {
        throw new InvalidOperationException("Actual TLS state does not match the configured scheme.");
    }
    var request = Interlocked.Increment(ref state.Requests);
    Interlocked.Increment(ref counters.Requests);
    if (request > 1)
    {
        Interlocked.Increment(ref counters.ReusedRequests);
    }
    if (!quiet)
    {
        context.Response.Headers["X-Backend"] = backend;
        context.Response.Headers["X-Connection-Id"] = context.Connection.Id;
        context.Response.Headers["X-Connection-Request"] = request.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers["X-Tls"] = tls is null ? "none" : $"{tls.Protocol}:{tls.NegotiatedCipherSuite}";
    }
    context.Response.ContentType = "text/plain";
    context.Response.ContentLength = 1024;
    await context.Response.WriteAsync(payload);
});
app.MapGet("/metrics", (HttpContext context) =>
{
    using var process = Process.GetCurrentProcess();
    return context.Response.WriteAsJsonAsync(new
    {
        backend,
        scheme,
        accepted = Interlocked.Read(ref counters.Accepted),
        closed = Interlocked.Read(ref counters.Closed),
        handshakes = Interlocked.Read(ref counters.Handshakes),
        requests = Interlocked.Read(ref counters.Requests),
        reusedRequests = Interlocked.Read(ref counters.ReusedRequests),
        cpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
        allocatedBytes = GC.GetTotalAllocatedBytes(),
        gen0 = GC.CollectionCount(0),
        gen1 = GC.CollectionCount(1),
        gen2 = GC.CollectionCount(2),
        workingSet = process.WorkingSet64,
        runtime = RuntimeInformation.FrameworkDescription,
        tlsProtocolRequested = tlsProtocols.ToString(),
        tlsResumption = scheme == "http" ? "not applicable" : "disabled in SslStream options / native SSL_CTX"
    });
});
if (builder.Configuration.GetValue("benchmark", false))
{
    app.MapPost("/shutdown", (HttpContext context) =>
    {
        context.Response.OnCompleted(() =>
        {
            app.Lifetime.StopApplication();
            return Task.CompletedTask;
        });
        return context.Response.WriteAsync("stopping\n");
    });
}
await app.RunAsync();

static X509Certificate2 LoadCertificate(IConfiguration configuration)
{
    var certificate = X509Certificate2.CreateFromPemFile(
        configuration["cert"] ?? throw new ArgumentException("Pass --cert <PEM certificate>."),
        configuration["key"] ?? throw new ArgumentException("Pass --key <PEM private key>."));
    if (!OperatingSystem.IsWindows())
    {
        return certificate;
    }
    using (certificate)
    {
        // SChannel cannot use the ephemeral key produced by this PEM import.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.DefaultKeySet);
    }
}

internal sealed class ConnectionState
{
    public long Requests;
}

internal sealed class Counters
{
    public long Accepted;
    public long Closed;
    public long Handshakes;
    public long Requests;
    public long ReusedRequests;
}
