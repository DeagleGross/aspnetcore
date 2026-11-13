// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Transport;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

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
            await RunAsync($"{backend}: accepting survives file descriptor exhaustion", () => DescriptorExhaustionAsync(backend, workers));
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
        var serverView = new TaskCompletionSource<(EndPoint? Local, EndPoint? Remote)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var accepted = await listener.AcceptAsync();
            Require(accepted is not null, "listener returned no connection");
            serverView.TrySetResult((accepted!.LocalEndPoint, accepted.RemoteEndPoint));
            Require(accepted.Features.Get<INetworkConnectionFeature>() is not null, "server side must expose INetworkConnectionFeature");
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
        var clientLocal = feature.Connection.LocalEndPoint;

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
        var (serverLocal, serverRemote) = await serverView.Task.WaitAsync(Timeout);
        Require(clientLocal is IPEndPoint { Port: > 0 }, $"the client's local endpoint {clientLocal} must have a real port");
        Require(Equals(serverRemote, clientLocal), $"the server saw peer {serverRemote} but the client is {clientLocal}");
        Require(Equals(serverLocal, listener.EndPoint), $"the server saw local {serverLocal} but the listener is {listener.EndPoint}");
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

    private static async Task<string> DescriptorExhaustionAsync(string backend, int workers)
    {
        if (!OperatingSystem.IsLinux())
        {
            return "SKIP (the descriptor limit only applies to the Linux accept path)";
        }
        // io_uring reads the descriptor limit when it prepares the accept request, so the limit is lowered before the listener binds. Descriptor use is bounded for the whole check.
        Require(getrlimit(RlimitNoFile, out var original) == 0, "getrlimit failed");
        var lowered = new RLimit { Current = Math.Min(original.Maximum, (ulong)Directory.GetFileSystemEntries("/proc/self/fd").Length + 80), Maximum = original.Maximum };
        Require(setrlimit(RlimitNoFile, ref lowered) == 0, "setrlimit failed");
        try
        {
            return await ExhaustAsync(backend, workers);
        }
        finally
        {
            Require(setrlimit(RlimitNoFile, ref original) == 0, "restoring the descriptor limit failed");
        }
    }

    private static async Task<string> ExhaustAsync(string backend, int workers)
    {
        await using var factory = new NetworkTransportFactory(Backends.Create(backend, workers));
        var endpoint = new IPEndPoint(IPAddress.Loopback, FreePort());
        await using var listener = await factory.BindAsync(endpoint);
        var accepted = 0;
        var server = Task.Run(async () =>
        {
            var echoes = new List<Task>();
            while (await listener.AcceptAsync() is { } connection)
            {
                Interlocked.Increment(ref accepted);
                echoes.Add(EchoUntilClosedAsync(connection));
            }
            await Task.WhenAll(echoes);
        });

        // With no descriptor left even the runtime cannot open an assembly, so everything the exhausted phase needs is exercised before it starts.
        await EchoProbeAsync(endpoint);
        File.OpenHandle("/dev/null").Dispose();
        try
        {
            throw new IOException();
        }
        catch (IOException)
        {
        }
        var accepted0 = Volatile.Read(ref accepted);
        const int Pending = 10;
        var clients = new List<Socket>();
        for (var i = 0; i < Pending; i++)
        {
            clients.Add(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp));
        }
        var filler = new List<SafeFileHandle>();
        try
        {
            while (true)
            {
                try
                {
                    filler.Add(File.OpenHandle("/dev/null"));
                }
                catch (IOException)
                {
                    break;
                }
            }
            // No descriptor is left. Connecting needs none (the sockets exist already), so the kernel completes the handshakes and the listener cannot accept them.
            foreach (var client in clients)
            {
                await client.ConnectAsync(endpoint).WaitAsync(Timeout);
            }
            await Task.Delay(500);
            Require(Volatile.Read(ref accepted) == accepted0, $"{accepted - accepted0} connections were accepted although no descriptor was free");
        }
        finally
        {
            foreach (var handle in filler)
            {
                handle.Dispose();
            }
        }

        var deadline = DateTime.UtcNow + Timeout;
        while (Volatile.Read(ref accepted) < accepted0 + Pending && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        Require(Volatile.Read(ref accepted) == accepted0 + Pending, $"only {accepted - accepted0} of {Pending} pending connections were accepted after descriptors became free again");

        await EchoProbeAsync(endpoint);
        foreach (var client in clients)
        {
            client.Dispose();
        }
        await listener.UnbindAsync();
        await server.WaitAsync(Timeout);
        return $"{Pending} connections arrived while no descriptor was free, all were accepted once descriptors were free again, and a new connection was served";
    }

    private static async Task EchoProbeAsync(IPEndPoint endpoint)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await probe.ConnectAsync(endpoint).WaitAsync(Timeout);
        var message = new byte[100];
        Random.Shared.NextBytes(message);
        await probe.SendAsync(message.AsMemory()).AsTask().WaitAsync(Timeout);
        var echo = new byte[message.Length];
        var read = 0;
        while (read < echo.Length)
        {
            var count = await probe.ReceiveAsync(echo.AsMemory(read)).AsTask().WaitAsync(Timeout);
            Require(count > 0, "the probe connection was closed instead of echoed");
            read += count;
        }
        Require(echo.AsSpan().SequenceEqual(message), "the probe echo differs");
        probe.Shutdown(SocketShutdown.Send);
    }

    private static async Task EchoUntilClosedAsync(ConnectionContext connection)
    {
        try
        {
            while (true)
            {
                var result = await connection.Transport.Input.ReadAsync();
                foreach (var segment in result.Buffer)
                {
                    await connection.Transport.Output.WriteAsync(segment);
                }
                connection.Transport.Input.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ConnectionAbortedException)
        {
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    private const int RlimitNoFile = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong Current;
        public ulong Maximum;
    }

    // A sample check calls the C library directly; the source-generated marshaller would need unsafe code in this project.
#pragma warning disable SYSLIB1054
    [DllImport("libc", SetLastError = true)]
    private static extern int getrlimit(int resource, out RLimit limit);

    [DllImport("libc", SetLastError = true)]
    private static extern int setrlimit(int resource, ref RLimit limit);
#pragma warning restore SYSLIB1054

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
        app.MapGet("/who", (HttpContext context) => $"{context.Connection.RemoteIpAddress}|{context.Connection.RemotePort}|{context.Connection.LocalIpAddress}|{context.Connection.LocalPort}");
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
            var who = (await client.GetStringAsync($"http://127.0.0.1:{port}/who").WaitAsync(Timeout)).Split('|');
            Require(who[0] == "127.0.0.1" && int.Parse(who[1], CultureInfo.InvariantCulture) > 0, $"HttpContext.Connection must report the real client endpoint, saw {who[0]}:{who[1]}");
            Require(who[2] == "127.0.0.1" && int.Parse(who[3], CultureInfo.InvariantCulture) == port, $"HttpContext.Connection must report the real server endpoint, saw {who[2]}:{who[3]}");
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
