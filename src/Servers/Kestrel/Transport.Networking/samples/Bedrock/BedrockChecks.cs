// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal static class BedrockChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static int _failures;

    public static async Task<int> RunAsync(Arguments arguments)
    {
        var requested = arguments.Get("backend", "all");
        var backends = requested == "all" ? Backends.ForCurrentOs : [requested];
        var workers = arguments.Get("workers", 2);

        await RunAsync("CanBind selector contract", () => CanBindContractAsync());
        foreach (var backend in backends)
        {
            await RunAsync($"{backend}: echo through IConnectionListenerFactory + IConnectionFactory", () => EchoAsync(backend, workers));
            await RunAsync($"{backend}: connect to a closed port fails fast", () => ConnectRefusedAsync(backend, workers));
            await RunAsync($"{backend}: Kestrel + HttpClient (GET keep-alive, POST echo, IPv6 fall-through)", () => KestrelAndHttpClientAsync(backend, workers));
        }

        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task RunAsync(string name, Func<Task<string>> check)
    {
        try
        {
            var detail = await check().WaitAsync(TimeSpan.FromSeconds(60));
            Console.WriteLine($"PASS {name}{(detail.Length == 0 ? string.Empty : " - " + detail)}");
        }
        catch (Exception error)
        {
            _failures++;
            Console.WriteLine($"FAIL {name}: {error}");
        }
    }

    private static Task<string> CanBindContractAsync()
    {
        var options = Backends.Create(Backends.ForCurrentOs[0], 1);
        var factory = new NetworkTransportFactory(options);
        Require(factory.CanBind(new IPEndPoint(IPAddress.Loopback, 1)), "IPv4 endpoint must be accepted");
        Require(!factory.CanBind(new DnsEndPoint("localhost", 1)), "DnsEndPoint must fall through");
        Require(!factory.CanBind(new UnixDomainSocketEndPoint("/tmp/x.sock")), "Unix endpoint must fall through");
        Require(!factory.CanBind(new IPEndPoint(IPAddress.IPv6Loopback, 1)), "The providers are IPv4 only, so IPv6 must fall through");
        if (!OperatingSystem.IsWindows())
        {
            Require(!factory.CanBind(new IPEndPoint(IPAddress.Loopback, 0)), "The Linux providers need a fixed port, so port 0 must fall through");
        }
        return Task.FromResult("IPv4 yes; Dns/Unix/IPv6 (and port 0 on Linux) fall through");
    }

    private static async Task<string> EchoAsync(string backend, int workers)
    {
        await using var factory = new NetworkTransportFactory(Backends.Create(backend, workers));
        var endpoint = new IPEndPoint(IPAddress.Loopback, FreePort());
        await using var listener = await factory.BindAsync(endpoint);
        var server = Task.Run(async () =>
        {
            var accepted = await listener.AcceptAsync();
            Require(accepted is not null, "listener returned no connection");
            Require(accepted!.Features.Get<INetworkConnectionFeature>() is not null, "server side must expose INetworkConnectionFeature");
            var input = accepted.Transport.Input;
            var output = accepted.Transport.Output;
            while (true)
            {
                var result = await input.ReadAsync();
                foreach (var segment in result.Buffer)
                {
                    await output.WriteAsync(segment);
                }
                input.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
            await accepted.DisposeAsync();
        });

        ConnectionContext client;
        try
        {
            client = await factory.ConnectAsync(listener.EndPoint).AsTask().WaitAsync(Timeout);
        }
        catch (NotSupportedException error)
        {
            await listener.UnbindAsync();
            return $"SKIP connect ({error.Message})";
        }

        var feature = client.Features.Get<INetworkConnectionFeature>();
        Require(feature is not null, "client side must expose INetworkConnectionFeature");
        Require(Equals(feature!.Connection.RemoteEndPoint, listener.EndPoint), $"remote endpoint {feature.Connection.RemoteEndPoint} != {listener.EndPoint}");

        var payload = new byte[256 * 1024 + 17];
        Random.Shared.NextBytes(payload);
        await client.Transport.Output.WriteAsync(payload);
        var received = new byte[payload.Length];
        var offset = 0;
        while (offset < payload.Length)
        {
            var result = await client.Transport.Input.ReadAsync().AsTask().WaitAsync(Timeout);
            var buffer = result.Buffer;
            var take = (int)Math.Min(buffer.Length, payload.Length - offset);
            buffer.Slice(0, take).CopyTo(received.AsSpan(offset));
            offset += take;
            client.Transport.Input.AdvanceTo(buffer.GetPosition(take));
            Require(!(result.IsCompleted && offset < payload.Length), "echo stream ended early");
        }
        Require(received.AsSpan().SequenceEqual(payload), "echoed bytes differ");
        await client.DisposeAsync();
        await server.WaitAsync(Timeout);
        await listener.UnbindAsync();
        return $"{payload.Length} bytes round-tripped, kind {factory.Kind}";
    }

    private static async Task<string> ConnectRefusedAsync(string backend, int workers)
    {
        await using var factory = new NetworkTransportFactory(Backends.Create(backend, workers));
        try
        {
            using var cancel = new CancellationTokenSource(Timeout);
            await factory.ConnectAsync(new IPEndPoint(IPAddress.Loopback, FreePort()), cancel.Token).AsTask().WaitAsync(Timeout);
        }
        catch (NotSupportedException error)
        {
            return $"SKIP ({error.Message})";
        }
        catch (Exception error) when (error is SocketException or IOException or InvalidOperationException)
        {
            return error.GetType().Name;
        }
        throw new InvalidOperationException("Connect to a closed port unexpectedly succeeded.");
    }

    private static async Task<string> KestrelAndHttpClientAsync(string backend, int workers)
    {
        var port = FreePort();
        var port6 = Socket.OSSupportsIPv6 ? FreePort() : 0;
        var viaNetwork = 0;
        var viaOther = 0;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddNetworkTransport(Backends.Create(backend, workers));
        var body = new string('x', 1023) + "\n";
        builder.WebHost.ConfigureKestrel(options =>
        {
            void Configure(ListenOptions listen)
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.Use(next => async connection =>
                {
                    if (connection.Features.Get<INetworkConnectionFeature>() is not null)
                    {
                        Interlocked.Increment(ref viaNetwork);
                    }
                    else
                    {
                        Interlocked.Increment(ref viaOther);
                    }
                    await next(connection);
                });
            }
            options.Listen(IPAddress.Loopback, port, Configure);
            if (port6 != 0)
            {
                options.Listen(IPAddress.IPv6Loopback, port6, Configure);
            }
        });
        await using var app = builder.Build();
        app.MapGet("/", () => body);
        app.MapPost("/echo", async (HttpContext context) =>
        {
            context.Response.ContentType = "application/octet-stream";
            await context.Request.Body.CopyToAsync(context.Response.Body);
        });
        await app.StartAsync();
        try
        {
            var factory = app.Services.GetRequiredService<IConnectionFactory>();
            Require(ReferenceEquals(factory, app.Services.GetRequiredService<NetworkTransportFactory>()), "IConnectionFactory must be the same instance as the listener factory");
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }.UseConnectionFactory(factory));
            for (var i = 0; i < 50; i++)
            {
                var text = await client.GetStringAsync($"http://127.0.0.1:{port}/").WaitAsync(Timeout);
                Require(text.Length == body.Length, $"GET returned {text.Length} chars");
            }
            foreach (var size in new[] { 1024, 64 * 1024, 256 * 1024, 512 * 1024, 1024 * 1024 + 3 })
            {
                var upload = new byte[size];
                Random.Shared.NextBytes(upload);
                try
                {
                    using var response = await client.PostAsync($"http://127.0.0.1:{port}/echo", new ByteArrayContent(upload)).WaitAsync(Timeout);
                    var echoed = await response.Content.ReadAsByteArrayAsync().WaitAsync(Timeout);
                    Require(echoed.AsSpan().SequenceEqual(upload), $"POST echo of {size} bytes differs");
                }
                catch (TimeoutException ex)
                {
                    throw new TimeoutException($"POST echo of {size} bytes timed out", ex);
                }
            }
            Require(Volatile.Read(ref viaNetwork) == 1, $"expected one pooled keep-alive connection via the network transport, saw {viaNetwork}");

            var detail = $"55 requests over {viaNetwork} connection(s), uploads of 1 KiB..1 MiB echoed";
            using (var readAhead = new HttpClient(new SocketsHttpHandler { UseProxy = false }.UseConnectionFactory(factory, receiveBufferBytes: 1024 * 1024)))
            {
                var upload = new byte[1024 * 1024 + 3];
                Random.Shared.NextBytes(upload);
                using var response = await readAhead.PostAsync($"http://127.0.0.1:{port}/echo", new ByteArrayContent(upload)).WaitAsync(Timeout);
                var echoed = await response.Content.ReadAsByteArrayAsync().WaitAsync(Timeout);
                Require(echoed.AsSpan().SequenceEqual(upload), "POST echo through the read-ahead adapter differs");
                detail += "; the explicit 1 MiB read-ahead overload echoed too";
            }
            if (port6 != 0)
            {
                using var plain = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                var text = await plain.GetStringAsync($"http://[::1]:{port6}/").WaitAsync(Timeout);
                Require(text.Length == body.Length, "IPv6 GET failed");
                Require(Volatile.Read(ref viaOther) >= 1, "IPv6 listener should have been served by the fall-through factory");
                detail += $"; IPv6 listener fell through to the default factory ({viaOther} connection)";
            }
            return detail;
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
