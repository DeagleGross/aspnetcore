// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Rio;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

internal sealed class TransportFactory(bool rio, int workerCount, ILoggerFactory logging) : IConnectionListenerFactory
{
    public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Native.ValidatePlatform();
        if (endpoint is not IPEndPoint { AddressFamily: AddressFamily.InterNetwork } ip)
        {
            throw new NotSupportedException("The Windows prototypes currently bind IPv4 TCP endpoints only.");
        }
        var setting = Environment.GetEnvironmentVariable("NETWORKPROTO_CPUS");
        var cpus = setting is null ? Enumerable.Repeat(-1, workerCount).ToArray()
            : setting.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (workerCount <= 0 || cpus.Length != workerCount || cpus.Any(cpu => cpu < -1 || cpu >= 64)
            || (setting is not null && cpus.Any(cpu => cpu < 0)))
        {
            throw new ArgumentException("Worker count must match NETWORKPROTO_CPUS, whose entries are logical CPUs 0..63.");
        }
        var listener = new WindowsListener(ip, rio, cpus, logging.CreateLogger(rio ? "NetworkProto.Rio" : "NetworkProto.Iocp"));
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

internal sealed class WindowsListener : IConnectionListener
{
    private readonly WinsockSession _winsock;
    private readonly Channel<ConnectionContext> _accepted = Channel.CreateBounded<ConnectionContext>(
        new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly WindowsWorker[] _workers;
    private readonly Lock _disposeGate = new();
    private Task? _disposing;
    private int _nextWorker, _unbound;

    internal SocketHandle Socket { get; }
    public EndPoint EndPoint { get; }

    internal WindowsListener(IPEndPoint endpoint, bool rio, int[] cpus, ILogger logger)
    {
        _winsock = new WinsockSession();
        try
        {
            Socket = Native.Listen(endpoint, rio);
            EndPoint = Native.GetEndPoint(Socket, peer: false);
            var api = rio ? new RioApi(Socket) : null;
            _workers = cpus.Select((cpu, index) => new WindowsWorker(this, index, cpu, api, logger)).ToArray();
        }
        catch
        {
            Socket?.Dispose();
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

    internal bool Publish(Connection connection) => _accepted.Writer.TryWrite(connection);

    internal void Failed(Exception error)
    {
        _accepted.Writer.TryComplete(error);
        foreach (var worker in _workers)
        {
            worker.Enqueue(new WindowsWorker.Command(WindowsWorker.CommandKind.Stop));
        }
    }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
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
        }
        finally
        {
            Socket.Dispose();
            _winsock.Dispose();
        }
    }
}
