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

var builder = WebApplication.CreateBuilder(args);
var backend = builder.Configuration["backend"] ?? "sockets";
if (backend is not ("sockets" or "io_uring" or "IoUringTcp" or "IoUringTls"))
{
    throw new ArgumentException("Use --backend sockets, io_uring, IoUringTcp or IoUringTls.");
}
var port = builder.Configuration.GetValue("port", 5443);
var scheme = builder.Configuration["scheme"] ?? "https";
if (scheme is not ("http" or "https"))
{
    throw new ArgumentException("Use --scheme http or --scheme https.");
}
using var certificate = scheme == "https"
    ? X509Certificate2.CreateFromPemFile(
        builder.Configuration["cert"] ?? throw new ArgumentException("Pass --cert <PEM certificate>."),
        builder.Configuration["key"] ?? throw new ArgumentException("Pass --key <PEM private key>."))
    : null;
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
var quiet = builder.Configuration.GetValue("minimal", false);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port, listen =>
    {
        listen.Protocols = HttpProtocols.Http1;
        listen.Use(next => async connection =>
        {
            if ((backend == "io_uring" && !connection.ConnectionId.StartsWith("io-uring-", StringComparison.Ordinal))
                || (backend.StartsWith("IoUring", StringComparison.Ordinal) && !connection.ConnectionId.StartsWith("owned-", StringComparison.Ordinal)))
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

        if (scheme == "https" && backend != "IoUringTls")
        {
            listen.UseHttps(https =>
            {
                https.ServerCertificate = certificate;
                https.SslProtocols = SslProtocols.Tls12;
                https.OnAuthenticate = (_, ssl) =>
                {
                    ssl.CipherSuitesPolicy = new CipherSuitesPolicy([TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256]);
                    ssl.AllowTlsResume = false;
                };
            });
            listen.Use(next => async connection =>
            {
                Interlocked.Increment(ref counters.Handshakes);
                await next(connection);
            });
        }
        else if (backend == "IoUringTls")
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
        tlsResumption = scheme == "http" ? "not applicable" : "disabled in SslStream options / native SSL_CTX"
    });
});
await app.RunAsync();

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
