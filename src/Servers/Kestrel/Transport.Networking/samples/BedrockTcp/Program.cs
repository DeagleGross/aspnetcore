// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Net.Transport;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

return await TcpBenchmark.RunAsync(Arguments.Parse(args));

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
            var from = int.Parse(range[0], CultureInfo.InvariantCulture);
            var to = range.Length > 1 ? int.Parse(range[1], CultureInfo.InvariantCulture) : from;
            for (var cpu = from; cpu <= to; cpu++)
            {
                mask |= 1L << cpu;
            }
        }
        Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)mask;
        var pinned = System.Numerics.BitOperations.PopCount((ulong)mask);
        if (Environment.ProcessorCount != pinned)
        {
            Console.WriteLine($"WARNING: pinned to {pinned} CPUs but Environment.ProcessorCount is {Environment.ProcessorCount}; start the process with DOTNET_PROCESSOR_COUNT={pinned}, otherwise the ThreadPool is sized for the whole machine.");
        }
    }
}

// One client connection that sends a request and reads exactly the response length back. Every arm reads and
// writes through the layer being measured and does nothing else with the bytes.
internal abstract class Peer : IAsyncDisposable
{
    public abstract ValueTask ExchangeAsync(ReadOnlyMemory<byte> request, int responseBytes);

    public abstract ValueTask DisposeAsync();
}

internal sealed class SocketPeer(Socket socket, int readBufferBytes) : Peer
{
    private readonly byte[] _buffer = new byte[readBufferBytes];

    public static async Task<Peer> ConnectAsync(IPEndPoint endpoint, int readBufferBytes)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(endpoint).ConfigureAwait(false);
        return new SocketPeer(socket, readBufferBytes);
    }

    public override async ValueTask ExchangeAsync(ReadOnlyMemory<byte> request, int responseBytes)
    {
        var sent = 0;
        while (sent < request.Length)
        {
            sent += await socket.SendAsync(request[sent..], SocketFlags.None).ConfigureAwait(false);
        }
        var received = 0;
        while (received < responseBytes)
        {
            var read = await socket.ReceiveAsync(_buffer.AsMemory(0, Math.Min(_buffer.Length, responseBytes - received)), SocketFlags.None).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("the server closed the connection");
            }
            received += read;
        }
    }

    public override ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

// The IDuplexPipe a ConnectionContext exposes. This is the Bedrock server-style API used from the client side.
internal sealed class PipePeer(ConnectionContext connection) : Peer
{
    public override async ValueTask ExchangeAsync(ReadOnlyMemory<byte> request, int responseBytes)
    {
        var result = await connection.Transport.Output.WriteAsync(request).ConfigureAwait(false);
        if (result.IsCompleted)
        {
            throw new EndOfStreamException("the connection is closed for writing");
        }
        var input = connection.Transport.Input;
        var remaining = responseBytes;
        while (remaining > 0)
        {
            var read = await input.ReadAsync().ConfigureAwait(false);
            var buffer = read.Buffer;
            if (buffer.IsEmpty && read.IsCompleted)
            {
                throw new EndOfStreamException("the server closed the connection");
            }
            var take = (int)Math.Min(buffer.Length, remaining);
            remaining -= take;
            input.AdvanceTo(buffer.GetPosition(take));
        }
    }

    public override ValueTask DisposeAsync() => connection.DisposeAsync();
}

// The provider-native connection behind INetworkConnectionFeature: queued sends and leased reads with no pipe or Stream in between.
internal sealed class NativePeer(NetworkConnection connection) : Peer
{
    public override async ValueTask ExchangeAsync(ReadOnlyMemory<byte> request, int responseBytes)
    {
        await connection.SendAsync(request).ConfigureAwait(false);
        var remaining = responseBytes;
        while (remaining > 0)
        {
            var result = await connection.ReceiveAsync().ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.IsEmpty && result.IsCompleted)
            {
                throw new EndOfStreamException("the server closed the connection");
            }
            var take = (int)Math.Min(buffer.Length, remaining);
            remaining -= take;
            connection.AdvanceReceiveTo(buffer.GetPosition(take));
        }
    }

    public override ValueTask DisposeAsync() => connection.DisposeAsync();
}

// The owned-buffer send path: the request is copied into provider storage and queued without waiting for send completion.
internal sealed class NativeQueuedPeer(NetworkConnection connection) : Peer
{
    public override async ValueTask ExchangeAsync(ReadOnlyMemory<byte> request, int responseBytes)
    {
        using (var buffer = connection.AllocateBuffer(request.Length))
        {
            request.CopyTo(buffer.Memory);
            connection.EnqueueSend(buffer.Slice.Slice(0, request.Length));
        }
        var remaining = responseBytes;
        while (remaining > 0)
        {
            var result = await connection.ReceiveAsync().ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.IsEmpty && result.IsCompleted)
            {
                throw new EndOfStreamException("the server closed the connection");
            }
            var take = (int)Math.Min(buffer.Length, remaining);
            remaining -= take;
            connection.AdvanceReceiveTo(buffer.GetPosition(take));
        }
    }

    public override ValueTask DisposeAsync() => connection.DisposeAsync();
}

// The Stream that UseConnectionFactory hands to SocketsHttpHandler: reads copy out of the pipe into the caller's buffer.
internal sealed class StreamPeer(ConnectionContextStream stream, int readBufferBytes) : Peer
{
    private readonly byte[] _buffer = new byte[readBufferBytes];

    public override async ValueTask ExchangeAsync(ReadOnlyMemory<byte> request, int responseBytes)
    {
        await stream.WriteAsync(request).ConfigureAwait(false);
        var received = 0;
        while (received < responseBytes)
        {
            var read = await stream.ReadAsync(_buffer.AsMemory(0, Math.Min(_buffer.Length, responseBytes - received))).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("the server closed the connection");
            }
            received += read;
        }
    }

    public override ValueTask DisposeAsync() => stream.DisposeAsync();
}

internal static class TcpBenchmark
{
    private static NetworkConnection InlineSends(NetworkConnection connection)
    {
        connection.CompleteSendsInline = true;
        return connection;
    }
    public static async Task<int> RunAsync(Arguments arguments)
    {
        Affinity.Apply(arguments.Get("cpus", string.Empty));
        var arm = arguments.Get("arm", "sockets");
        var port = arguments.Get("port", 5601);
        var connections = arguments.Get("connections", 64);
        var duration = TimeSpan.FromSeconds(arguments.Get("duration", 10));
        var warmup = TimeSpan.FromSeconds(arguments.Get("warmup", 3));
        var requestBytes = arguments.Get("request-bytes", 64);
        var responseBytes = arguments.Get("response-bytes", 1024);
        var workers = arguments.Get("workers", 8);
        var readBufferBytes = arguments.Get("read-buffer", 64 * 1024);
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);

        var separator = arm.IndexOf('-', StringComparison.Ordinal);
        var provider = separator < 0 ? arm : arm[..separator];
        var layer = separator < 0 ? string.Empty : arm[(separator + 1)..];
        if (provider != "sockets" && layer is not ("pipe" or "stream" or "native" or "native-inline" or "queued"))
        {
            Console.WriteLine("Usage: --arm sockets | <epoll|iouring|iocp|rio>-<pipe|stream|native|native-inline|queued> [--port 5601] [--connections 64] [--duration 10] [--warmup 3] [--request-bytes 64] [--response-bytes 1024] [--workers 8] [--cpus 0-7]");
            return 2;
        }

        NetworkTransportFactory? factory = provider == "sockets" ? null : new NetworkTransportFactory(Backends.Create(provider, workers));
        var peers = new Peer[connections];
        try
        {
            await Task.WhenAll(Enumerable.Range(0, connections).Select(async i =>
            {
                peers[i] = factory is null
                    ? await SocketPeer.ConnectAsync(endpoint, readBufferBytes).ConfigureAwait(false)
                    : layer switch
                    {
                        "pipe" => new PipePeer(await factory.ConnectAsync(endpoint).ConfigureAwait(false)),
                        "native" => new NativePeer((await factory.ConnectAsync(endpoint).ConfigureAwait(false)).Features.Get<INetworkConnectionFeature>()!.Connection),
                        "native-inline" => new NativePeer(InlineSends((await factory.ConnectAsync(endpoint).ConfigureAwait(false)).Features.Get<INetworkConnectionFeature>()!.Connection)),
                        "queued" => new NativeQueuedPeer((await factory.ConnectAsync(endpoint).ConfigureAwait(false)).Features.Get<INetworkConnectionFeature>()!.Connection),
                        _ => new StreamPeer(new ConnectionContextStream(await factory.ConnectAsync(endpoint).ConfigureAwait(false), 0), readBufferBytes),
                    };
            }));
        }
        catch (Exception error)
        {
            Console.WriteLine($"connect failed: {error.Message}");
            return 1;
        }

        var request = new byte[requestBytes];
        Array.Fill(request, (byte)'q');
        var measuring = false;
        var stop = false;
        var errors = 0;
        var latencies = new List<int>[connections];
        var stalls = new List<(int Connection, long EndTimestamp, int Microseconds)>[connections];
        var tasks = new Task[connections];
        for (var i = 0; i < connections; i++)
        {
            var peer = peers[i];
            var connectionIndex = i;
            var samples = latencies[i] = new List<int>(1 << 16);
            var slow = stalls[i] = new List<(int, long, int)>();
            tasks[i] = Task.Run(async () =>
            {
                while (!Volatile.Read(ref stop))
                {
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        await peer.ExchangeAsync(request, responseBytes).ConfigureAwait(false);
                        if (Volatile.Read(ref measuring))
                        {
                            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMicroseconds;
                            samples.Add(elapsed);
                            if (elapsed > 50_000)
                            {
                                slow.Add((connectionIndex, Stopwatch.GetTimestamp(), elapsed));
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        if (Interlocked.Increment(ref errors) == 1)
                        {
                            Console.WriteLine($"first error: {error}");
                        }
                        return;
                    }
                }
            });
        }

        await Task.Delay(warmup);
        var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        var gc0Start = GC.CollectionCount(0);
        var gc2Start = GC.CollectionCount(2);
        var allocStart = GC.GetTotalAllocatedBytes(false);
        var gcPauseStart = GC.GetTotalPauseDuration();
        var workItemsStart = ThreadPool.CompletedWorkItemCount;
        var threadsStart = ThreadPool.ThreadCount;
        var measureStart = Stopwatch.GetTimestamp();
        var clock = Stopwatch.StartNew();
        Volatile.Write(ref measuring, true);
        await Task.Delay(duration);
        Volatile.Write(ref measuring, false);
        clock.Stop();
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuStart;
        var allocBytes = GC.GetTotalAllocatedBytes(false) - allocStart;
        var gcSummary = $"allocBytes={allocBytes} gen0={GC.CollectionCount(0) - gc0Start} gen2={GC.CollectionCount(2) - gc2Start} gcPauseMs={(GC.GetTotalPauseDuration() - gcPauseStart).TotalMilliseconds:F1} processorCount={Environment.ProcessorCount} minThreads={minWorkers}/{minIo} poolThreads={threadsStart}->{ThreadPool.ThreadCount} poolWorkItems={ThreadPool.CompletedWorkItemCount - workItemsStart} pendingWorkItems={ThreadPool.PendingWorkItemCount}";
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
        var p999 = requests == 0 ? 0 : all[(int)(requests * 0.999)];
        var max = requests == 0 ? 0 : all[^1];
        var mean = requests == 0 ? 0 : all.Average();
        var cpuPerKiloRequest = requests == 0 ? 0 : cpu.TotalMilliseconds * 1000 / requests;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"RESULT arm={arm} connections={connections} request={requestBytes} response={responseBytes} requests={requests} rps={rps:F0} cpuMsPer1kReq={cpuPerKiloRequest:F2} p50us={p50} p99us={p99} errors={errors} workingSetMB={process.WorkingSet64 / (1024 * 1024)} meanus={mean:F0} p999us={p999} maxus={max}"));

        var slowSamples = stalls.SelectMany(static list => list).OrderBy(static s => s.EndTimestamp).ToList();
        Console.WriteLine($"STALLS over50ms={slowSamples.Count} connectionsAffected={slowSamples.Select(static s => s.Connection).Distinct().Count()}");
        Console.WriteLine($"RUNTIME {gcSummary}");
        foreach (var bucket in slowSamples.GroupBy(s => (int)Stopwatch.GetElapsedTime(measureStart, s.EndTimestamp).TotalMilliseconds / 100).Take(30))
        {
            Console.WriteLine($"STALLBUCKET t={bucket.Key * 100}ms samples={bucket.Count()} connections={bucket.Select(static s => s.Connection).Distinct().Count()} maxMs={bucket.Max(static s => s.Microseconds) / 1000}");
        }

        foreach (var peer in peers)
        {
            await peer.DisposeAsync();
        }
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }
        return errors == 0 ? 0 : 1;
    }
}
