// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

if (args.Contains("--topology", StringComparer.Ordinal))
{
    Console.WriteLine(JsonSerializer.Serialize(Topology.Read()));
    return;
}
if (args.Contains("--tls-probe", StringComparer.Ordinal))
{
    await TlsProbe.RunAsync(args[1], args[3]);
    return;
}

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length; i += 2)
{
    options.Add(args[i].TrimStart('-'), args[i + 1]);
}
var uri = new Uri(options.GetValueOrDefault("url", "http://127.0.0.1:5800/"));
var connections = int.Parse(options.GetValueOrDefault("connections", "256"), CultureInfo.InvariantCulture);
var seconds = int.Parse(options.GetValueOrDefault("seconds", "15"), CultureInfo.InvariantCulture);
var warmup = int.Parse(options.GetValueOrDefault("warmup", "5"), CultureInfo.InvariantCulture);
var shortConnections = options.GetValueOrDefault("mode", "long") == "short";
var sourceSetting = options.GetValueOrDefault("source-network", "auto");
var sourceNetwork = sourceSetting == "auto" ? null : IPAddress.Parse(sourceSetting);
if (sourceNetwork is not null && (sourceNetwork.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(sourceNetwork)))
{
    throw new ArgumentException("The local benchmark requires an IPv4 loopback source network.");
}
var tlsProtocol = options.GetValueOrDefault("tls-protocol", nameof(SslProtocols.Tls13)) switch
{
    nameof(SslProtocols.Tls12) => SslProtocols.Tls12,
    nameof(SslProtocols.Tls13) => SslProtocols.Tls13,
    _ => throw new ArgumentException("Use --tls-protocol Tls12 or Tls13.")
};
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(connections);
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
ArgumentOutOfRangeException.ThrowIfNegative(warmup);
ThreadPool.SetMinThreads(Math.Max(32, Environment.ProcessorCount * 8), Math.Max(32, Environment.ProcessorCount * 8));

using var cancellation = new CancellationTokenSource();
var phase = new Phase();
var request = Encoding.ASCII.GetBytes($"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: localhost\r\nConnection: {(shortConnections ? "close" : "keep-alive")}\r\n\r\n");
var peers = Enumerable.Range(0, connections).Select(index => new Peer(index, uri, shortConnections, request, phase, tlsProtocol, sourceNetwork)).ToArray();
var workers = peers.Select(peer => Task.Run(() => peer.RunAsync(cancellation.Token))).ToArray();
await Task.Delay(TimeSpan.FromSeconds(warmup));
var readyDeadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
while (peers.Any(peer => !Volatile.Read(ref peer.Ready)))
{
    if (Stopwatch.GetTimestamp() >= readyDeadline)
    {
        cancellation.Cancel();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15));
        throw new InvalidOperationException("Not every load lane completed a verified response before the measurement window.");
    }
    await Task.Delay(10);
}
using var process = Process.GetCurrentProcess();
var cpuStart = process.TotalProcessorTime;
var start = Stopwatch.GetTimestamp();
phase.Start = start;
Volatile.Write(ref phase.Measuring, true);
Console.WriteLine("MEASURE_START");
await Task.Delay(TimeSpan.FromSeconds(seconds));
Volatile.Write(ref phase.Measuring, false);
var end = Stopwatch.GetTimestamp();
phase.End = end;
var cpuEnd = process.TotalProcessorTime;
cancellation.Cancel();
await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15));
var elapsed = Stopwatch.GetElapsedTime(start, end).TotalSeconds;
var successful = peers.Sum(peer => peer.Requests);
var histogram = new long[Histogram.Size];
foreach (var peer in peers)
{
    for (var i = 0; i < histogram.Length; i++)
    {
        histogram[i] += peer.Latencies[i];
    }
}
Console.WriteLine("LOAD_RESULT " + JsonSerializer.Serialize(new
{
    url = uri.ToString(),
    mode = shortConnections ? "short" : "long",
    connections,
    seconds = elapsed,
    requests = successful,
    rps = successful / elapsed,
    errors = peers.Sum(peer => peer.Errors),
    warmupErrors = peers.Sum(peer => peer.WarmupErrors),
    startupResourceErrors = peers.Sum(peer => peer.StartupResourceErrors),
    readyLanes = peers.Count(peer => peer.Ready),
    firstError = peers.Select(peer => peer.FirstError).FirstOrDefault(error => error is not null),
    clientCpuMilliseconds = (cpuEnd - cpuStart).TotalMilliseconds,
    clientCpuCores = (cpuEnd - cpuStart).TotalSeconds / elapsed,
    p50Microseconds = Histogram.Percentile(histogram, successful, 0.50),
    p99Microseconds = Histogram.Percentile(histogram, successful, 0.99),
    tlsCipher = peers.Select(peer => peer.Cipher).FirstOrDefault(cipher => cipher is not null),
    tlsProtocol = tlsProtocol.ToString(),
    runtime = RuntimeInformation.FrameworkDescription,
    cpuAffinity = $"0x{process.ProcessorAffinity.ToInt64():x}",
    sourceAddresses = sourceNetwork is null ? 1 : 64,
    sourceNetwork = sourceSetting,
    connectMode = sourceNetwork is null ? "implicit bind" : "SO_PORT_SCALABILITY bind",
    pipeline = 1,
    bodyBytes = 1024,
    waitForEofOnShort = true
}));

internal sealed class Phase
{
    internal bool Measuring;
    internal long Start, End;
}

internal sealed class Peer
{
    private readonly Uri _uri;
    private readonly bool _shortConnections;
    private readonly byte[] _request;
    private readonly Phase _phase;
    private readonly SslProtocols _tlsProtocol;
    private readonly byte[] _buffer = new byte[32768];
    private static readonly byte[] ExpectedBody = Encoding.ASCII.GetBytes(new string('x', 1023) + "\n");
    private const SocketOptionName PortScalability = (SocketOptionName)0x3006;
    private readonly IPEndPoint? _source;
    internal long Requests, Errors, WarmupErrors, StartupResourceErrors;
    internal string? FirstError, Cipher;
    internal bool Ready;
    internal long[] Latencies { get; } = new long[Histogram.Size];

    internal Peer(int index, Uri uri, bool shortConnections, byte[] request, Phase phase, SslProtocols tlsProtocol, IPAddress? sourceNetwork)
    {
        _uri = uri;
        _shortConnections = shortConnections;
        _request = request;
        _phase = phase;
        _tlsProtocol = tlsProtocol;
        if (sourceNetwork is not null)
        {
            var address = sourceNetwork.GetAddressBytes();
            address[3] = checked((byte)(2 + index % 64));
            _source = new IPEndPoint(new IPAddress(address), 0);
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var connectionStarted = Stopwatch.GetTimestamp();
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                using var connectCancellation = cancellationToken.UnsafeRegister(static value => ((Socket)value!).Dispose(), socket);
                if (_source is not null)
                {
                    socket.SetSocketOption(SocketOptionLevel.Socket, PortScalability, true);
                    socket.Bind(_source);
                }
                socket.Connect(new IPEndPoint(IPAddress.Parse(_uri.Host), _uri.Port));
                using var network = new NetworkStream(socket, ownsSocket: false);
                using var tls = _uri.Scheme == "https" ? new SslStream(network, leaveInnerStreamOpen: true, static (_, _, _, _) => true) : null;
                if (tls is not null)
                {
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        EnabledSslProtocols = _tlsProtocol,
                        AllowTlsResume = false
                    }, cancellationToken);
                    Cipher = tls.NegotiatedCipherSuite.ToString();
                }
                Stream stream = tls is null ? network : tls;
                do
                {
                    var started = _shortConnections ? connectionStarted : Stopwatch.GetTimestamp();
                    await stream.WriteAsync(_request, cancellationToken);
                    await ReadResponseAsync(stream, cancellationToken);
                    if (_shortConnections && await stream.ReadAsync(_buffer.AsMemory(0, 1), cancellationToken) != 0)
                    {
                        throw new InvalidDataException("Unexpected data after a Connection: close response.");
                    }
                    if (_shortConnections && tls is not null && await network.ReadAsync(_buffer.AsMemory(0, 1), cancellationToken) != 0)
                    {
                        throw new InvalidDataException("Unexpected ciphertext after TLS EOF.");
                    }
                    Volatile.Write(ref Ready, true);
                    if (Volatile.Read(ref _phase.Measuring))
                    {
                        Requests++;
                        var microseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
                        Latencies[Histogram.Bucket(microseconds)]++;
                    }
                }
                while (!_shortConnections && !cancellationToken.IsCancellationRequested);
            }
            catch (Exception error) when (error is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                if (Volatile.Read(ref _phase.Measuring))
                {
                    Errors++;
                }
                else
                {
                    WarmupErrors++;
                    if (error.GetBaseException() is SocketException { NativeErrorCode: 10055 })
                    {
                        StartupResourceErrors++;
                    }
                }
                FirstError ??= error.ToString();
                await Task.Delay(10);
            }
        }
    }

    private async Task ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = 0;
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(_buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                throw new IOException("EOF before the HTTP response header.");
            }
            length += read;
            headerEnd = _buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (length == _buffer.Length && headerEnd < 0)
            {
                throw new InvalidDataException("Oversized HTTP header.");
            }
        }
        headerEnd += 4;
        if (!_buffer.AsSpan(0, length).StartsWith("HTTP/1.1 200"u8))
        {
            throw new InvalidDataException("Unexpected HTTP response: " + Encoding.ASCII.GetString(_buffer, 0, headerEnd));
        }
        var contentLength = FindLength(_buffer.AsSpan(0, headerEnd));
        if (contentLength != ExpectedBody.Length)
        {
            throw new InvalidDataException($"Expected a 1024-byte response, got Content-Length={contentLength}.");
        }
        var bodyRead = length - headerEnd;
        if (bodyRead > contentLength || !_buffer.AsSpan(headerEnd, bodyRead).SequenceEqual(ExpectedBody.AsSpan(0, bodyRead)))
        {
            throw new InvalidDataException("Corrupted initial response body.");
        }
        while (bodyRead < contentLength)
        {
            var read = await stream.ReadAsync(_buffer.AsMemory(0, contentLength - bodyRead), cancellationToken);
            if (read == 0 || !_buffer.AsSpan(0, read).SequenceEqual(ExpectedBody.AsSpan(bodyRead, read)))
            {
                throw new InvalidDataException("Truncated or corrupted response body.");
            }
            bodyRead += read;
        }
    }

    private static int FindLength(ReadOnlySpan<byte> header)
    {
        var index = header.IndexOf("Content-Length:"u8);
        if (index < 0)
        {
            throw new InvalidDataException("Missing Content-Length.");
        }
        index += "Content-Length:"u8.Length;
        while (header[index] == (byte)' ')
        {
            index++;
        }
        var length = 0;
        while (header[index] is >= (byte)'0' and <= (byte)'9')
        {
            length = checked(length * 10 + header[index++] - (byte)'0');
        }
        return length;
    }
}

internal static class Histogram
{
    internal const int Size = 4096;
    internal static int Bucket(double microseconds)
    {
        if (microseconds <= 0)
        {
            return 0;
        }
        return Math.Min(Size - 1, checked((int)(Math.Log2(1 + microseconds) * 128)));
    }
    internal static double Percentile(long[] histogram, long total, double quantile)
    {
        if (total == 0)
        {
            return 0;
        }
        var remaining = (long)Math.Ceiling(total * quantile);
        for (var i = 0; i < histogram.Length; i++)
        {
            remaining -= histogram[i];
            if (remaining <= 0)
            {
                return Math.Pow(2, (i + 1) / 128.0) - 1;
            }
        }
        return Math.Pow(2, Size / 128.0) - 1;
    }
}

internal static unsafe partial class Topology
{
    internal static object Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        uint length = 0;
        GetLogicalProcessorInformationEx(0, null, &length);
        var bytes = new byte[length];
        var cores = new List<object>();
        fixed (byte* buffer = bytes)
        {
            if (GetLogicalProcessorInformationEx(0, buffer, &length) == 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }
            for (uint offset = 0; offset < length;)
            {
                var entry = buffer + offset;
                var size = *(uint*)(entry + 4);
                var groups = *(ushort*)(entry + 30);
                if (size < 48 || groups != 1 || *(ushort*)(entry + 40) != 0)
                {
                    throw new NotSupportedException("The benchmark currently supports a single processor group.");
                }
                var mask = *(ulong*)(entry + 32);
                cores.Add(new
                {
                    index = cores.Count,
                    logicalCpus = Enumerable.Range(0, 64).Where(cpu => (mask & (1UL << cpu)) != 0).ToArray(),
                    efficiencyClass = entry[9]
                });
                offset += size;
            }
        }
        return new { cores, runtime = RuntimeInformation.FrameworkDescription };
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetLogicalProcessorInformationEx(int relationship, byte* buffer, uint* length);
}
