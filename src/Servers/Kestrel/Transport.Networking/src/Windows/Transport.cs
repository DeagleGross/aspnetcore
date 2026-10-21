// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Transport;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Rio;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

internal sealed class TransportFactory(bool rio, int workerCount, ILoggerFactory logging, bool shared = false, int[]? processors = null,
    NetworkNativeTlsOptions? nativeTls = null) : IProviderListenerFactory
{
    public async ValueTask<IProviderListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Native.ValidatePlatform();
        if (endpoint is not IPEndPoint { AddressFamily: AddressFamily.InterNetwork } ip)
        {
            throw new NotSupportedException("The Windows prototypes currently bind IPv4 TCP endpoints only.");
        }
        var setting = Environment.GetEnvironmentVariable("NETWORKPROTO_CPUS");
        var cpus = processors ?? (setting is null ? Enumerable.Repeat(-1, workerCount).ToArray()
            : setting.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray());
        if (workerCount <= 0 || cpus.Length != workerCount || cpus.Any(cpu => cpu < -1 || cpu >= 64)
            || (setting is not null && cpus.Any(cpu => cpu < 0)))
        {
            throw new ArgumentException("Worker count must match NETWORKPROTO_CPUS, whose entries are logical CPUs 0..63.");
        }
        var listener = new WindowsListener(ip, rio, cpus, logging.CreateLogger(rio ? "NetworkProto.Rio" : "NetworkProto.Iocp"), shared, nativeTls);
        try
        {
            await listener.StartAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return listener;
        }
        catch
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class WindowsListener : IProviderListener
{
    private readonly WinsockSession _winsock;
    private readonly Channel<IProviderConnection> _accepted = Channel.CreateBounded<IProviderConnection>(
        new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly WindowsWorker[] _workers;
    private readonly Lock _disposeGate = new();
    private Task? _disposing;
    private int _nextWorker, _unbound;

    internal SocketHandle Socket { get; }
    internal bool Shared { get; }
    internal Schannel.Credential? TlsCredential { get; }
    internal HandshakeScheduler? Handshakes { get; }
    public EndPoint EndPoint { get; }

    internal WindowsListener(IPEndPoint endpoint, bool rio, int[] cpus, ILogger logger, bool shared, NetworkNativeTlsOptions? nativeTls)
    {
        Shared = shared;
        _winsock = new WinsockSession();
        try
        {
            TlsCredential = nativeTls is null ? null : new Schannel.Credential(nativeTls);
            Handshakes = TlsCredential is not null && Environment.GetEnvironmentVariable("NETWORKPROTO_TLS_HANDSHAKE_OFFLOAD") != "0"
                ? new HandshakeScheduler(cpus.Length) : null;
            Socket = Native.Listen(endpoint, rio);
            EndPoint = Native.GetEndPoint(Socket, peer: false);
            var api = rio ? new RioApi(Socket) : null;
            _workers = cpus.Select((cpu, index) => new WindowsWorker(this, index, cpu, api, logger)).ToArray();
        }
        catch
        {
            Socket?.Dispose();
            TlsCredential?.Dispose();
            _winsock.Dispose();
            throw;
        }
    }

    internal Task StartAsync()
    {
        foreach (var worker in _workers)
        {
            worker.Start();
        }
        return Task.WhenAll(_workers.Select(worker => worker.Started));
    }

    internal void Dispatch(SocketHandle socket)
    {
        var index = (uint)Interlocked.Increment(ref _nextWorker) % (uint)_workers.Length;
        _workers[index].Enqueue(new WindowsWorker.Command(WindowsWorker.CommandKind.Accepted, Socket: socket));
    }

    internal bool Publish(IProviderConnection connection) => _accepted.Writer.TryWrite(connection);

    internal void Failed(Exception error)
    {
        _accepted.Writer.TryComplete(error);
        foreach (var worker in _workers)
        {
            worker.Enqueue(new WindowsWorker.Command(WindowsWorker.CommandKind.Stop));
        }
    }

    public async ValueTask<IProviderConnection?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        while (await _accepted.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_accepted.Reader.TryRead(out var connection))
            {
                return connection;
            }
        }
        return null;
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _unbound, 1) == 0)
        {
            _accepted.Writer.TryComplete();
            _workers[0].Enqueue(new WindowsWorker.Command(WindowsWorker.CommandKind.Unbind));
        }
        await _workers[0].Unbound.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposing ??= DisposeCore());
        }
    }

    private async Task DisposeCore()
    {
        try
        {
            await UnbindAsync().ConfigureAwait(false);
            foreach (var worker in _workers)
            {
                worker.Enqueue(new WindowsWorker.Command(WindowsWorker.CommandKind.Stop));
            }
            while (_accepted.Reader.TryRead(out var connection))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            await Task.WhenAll(_workers.Select(worker => worker.Stopped)).ConfigureAwait(false);
            if (Handshakes is not null)
            {
                await Handshakes.DrainAsync().ConfigureAwait(false);
                Console.WriteLine("WINDOWS_HANDSHAKES " + JsonSerializer.Serialize(Handshakes.Snapshot()));
            }
        }
        finally
        {
            Socket.Dispose();
            if (Handshakes is not null)
            {
                await Handshakes.DrainAsync().ConfigureAwait(false);
            }
            TlsCredential?.Dispose();
            _winsock.Dispose();
        }
    }
}
