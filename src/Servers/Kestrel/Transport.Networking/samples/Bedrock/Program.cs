// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var mode = args.Length > 0 ? args[0] : "check";
var arguments = Arguments.Parse(args);
return mode switch
{
    "check" => await BedrockChecks.RunAsync(arguments),
    "serve" => await BedrockServer.RunAsync(arguments),
    "bench" => await BedrockBenchmark.RunAsync(arguments),
    _ => Usage()
};

static int Usage()
{
    Console.WriteLine("""
        Bedrock sample
          check [--backend all|iocp|rio|epoll|iouring]
          serve --backend sockets|iocp|rio|epoll|iouring [--registration bedrock|legacy] [--port 5443] [--body-bytes 1024] [--workers 4] [--cpus 0-3]
          bench --handler sockets|iocp|rio|epoll|iouring [--url http://127.0.0.1:5443/] [--connections 64] [--duration 10] [--warmup 3] [--body-bytes 1024] [--workers 2] [--cpus 8-15] [--read-ahead 1048576]
        """);
    return 2;
}

internal static class Affinity
{
    public static void Apply(string? cpus)
    {
        if (string.IsNullOrEmpty(cpus))
        {
            return;
        }
        long mask = 0;
        foreach (var part in cpus.Split(','))
        {
            var range = part.Split('-');
            var from = int.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture);
            var to = range.Length > 1 ? int.Parse(range[1], System.Globalization.CultureInfo.InvariantCulture) : from;
            for (var cpu = from; cpu <= to; cpu++)
            {
                mask |= 1L << cpu;
            }
        }
        Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)mask;
    }
}

internal static class BedrockServer
{
    public static async Task<int> RunAsync(Arguments arguments)
    {
        Affinity.Apply(arguments.Get("cpus", string.Empty));
        var backend = arguments.Get("backend", "sockets");
        var port = arguments.Get("port", 5443);
        var bodyBytes = arguments.Get("body-bytes", 1024);
        var workers = arguments.Get("workers", 4);
        var payload = new byte[bodyBytes];
        Array.Fill(payload, (byte)'x');

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (backend != "sockets")
        {
            if (arguments.Get("registration", "bedrock") == "legacy")
            {
                builder.WebHost.UseNetworkTransport(Backends.Create(backend, workers));
            }
            else
            {
                builder.Services.AddNetworkTransport(Backends.Create(backend, workers));
            }
        }
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.MapGet("/", (HttpContext context) =>
        {
            context.Response.ContentLength = payload.Length;
            return context.Response.Body.WriteAsync(payload).AsTask();
        });
        app.MapGet("/metrics", () =>
        {
            var process = Process.GetCurrentProcess();
            return $"{{\"cpuMilliseconds\":{process.TotalProcessorTime.TotalMilliseconds:F0},\"workingSet\":{process.WorkingSet64}}}";
        });
        app.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return "ok";
        });
        Console.WriteLine($"serving backend={backend} port={port} body={bodyBytes}");
        await app.RunAsync();
        return 0;
    }
}

internal static class BedrockBenchmark
{
    public static async Task<int> RunAsync(Arguments arguments)
    {
        Affinity.Apply(arguments.Get("cpus", string.Empty));
        var handlerName = arguments.Get("handler", "sockets");
        var url = arguments.Get("url", "http://127.0.0.1:5443/");
        var connections = arguments.Get("connections", 64);
        var duration = TimeSpan.FromSeconds(arguments.Get("duration", 10));
        var warmup = TimeSpan.FromSeconds(arguments.Get("warmup", 3));
        var expected = arguments.Get("body-bytes", 1024);
        var workers = arguments.Get("workers", 2);
        var readAhead = arguments.Get("read-ahead", -1);

        NetworkTransportFactory? factory = null;
        var handler = new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = int.MaxValue, PooledConnectionLifetime = System.Threading.Timeout.InfiniteTimeSpan };
        if (handlerName != "sockets")
        {
            factory = new NetworkTransportFactory(Backends.Create(handlerName, workers));
            if (readAhead < 0)
            {
                handler.UseConnectionFactory(factory);
            }
            else
            {
                handler.UseConnectionFactory(factory, readAhead);
            }
        }
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        var measuring = false;
        var stop = false;
        var errors = 0;
        var latencies = new List<int>[connections];
        var tasks = new Task[connections];
        for (var i = 0; i < connections; i++)
        {
            var samples = latencies[i] = new List<int>(1 << 16);
            tasks[i] = Task.Run(async () =>
            {
                var buffer = new byte[64 * 1024];
                while (!Volatile.Read(ref stop))
                {
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                        await using var body = await response.Content.ReadAsStreamAsync();
                        var total = 0;
                        int read;
                        while ((read = await body.ReadAsync(buffer)) > 0)
                        {
                            total += read;
                        }
                        if (total != expected)
                        {
                            throw new InvalidOperationException($"expected {expected} bytes, got {total}");
                        }
                        if (Volatile.Read(ref measuring))
                        {
                            samples.Add((int)(Stopwatch.GetElapsedTime(started).TotalMicroseconds));
                        }
                    }
                    catch (Exception error)
                    {
                        if (Interlocked.Increment(ref errors) == 1)
                        {
                            Console.WriteLine($"first error: {error}");
                        }
                        await Task.Delay(10);
                    }
                }
            });
        }

        await Task.Delay(warmup);
        var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        Volatile.Write(ref measuring, true);
        await Task.Delay(duration);
        Volatile.Write(ref measuring, false);
        clock.Stop();
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuStart;
        Volatile.Write(ref stop, true);
        await Task.WhenAll(tasks);

        var all = new List<int>();
        foreach (var samples in latencies)
        {
            all.AddRange(samples);
        }
        all.Sort();
        var requests = all.Count;
        var rps = requests / clock.Elapsed.TotalSeconds;
        var p50 = requests == 0 ? 0 : all[requests / 2];
        var p99 = requests == 0 ? 0 : all[(int)(requests * 0.99)];
        var cpuPerKiloRequest = requests == 0 ? 0 : cpu.TotalMilliseconds * 1000 / requests;
        Console.WriteLine($"RESULT handler={handlerName} readAhead={(handlerName == "sockets" ? "n/a" : readAhead < 0 ? "default" : readAhead.ToString(System.Globalization.CultureInfo.InvariantCulture))} connections={connections} body={expected} requests={requests} rps={rps:F0} cpuMsPer1kReq={cpuPerKiloRequest:F2} p50us={p50} p99us={p99} errors={errors} workingSetMB={process.WorkingSet64 / (1024 * 1024)}");

        if (factory is not null)
        {
            await factory.DisposeAsync();
        }
        return errors == 0 ? 0 : 1;
    }
}
