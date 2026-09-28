// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll;

internal sealed class TransportFactory(string certificate, string key, int workers, ILoggerFactory logging) : IConnectionListenerFactory
{
    public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (endpoint is not IPEndPoint { AddressFamily: System.Net.Sockets.AddressFamily.InterNetwork, Port: > 0 } ip
            || !IPAddress.IsLoopback(ip.Address))
        {
            throw new NotSupportedException("EpollTls requires IPv4 loopback and a fixed nonzero port.");
        }
        var cpus = Environment.GetEnvironmentVariable("NETWORKPROTO_CPUS") is { } setting
            ? setting.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray()
            : Enumerable.Repeat(-1, workers).ToArray();
        if (workers <= 0 || cpus.Length != workers || cpus.Any(cpu => cpu < -1)
            || (Environment.GetEnvironmentVariable("NETWORKPROTO_CPUS") is not null && cpus.Any(cpu => cpu < 0)))
        {
            throw new ArgumentException("Epoll worker count must be positive and match NETWORKPROTO_CPUS when an explicit CPU list is supplied.");
        }
        if (Environment.GetEnvironmentVariable("NETWORKPROTO_KTLS") is { } ktls && ktls != "0")
        {
            throw new NotSupportedException("EpollTls currently supports userspace TLS only; unset NETWORKPROTO_KTLS.");
        }
        var listener = new Listener(ip, cpus, certificate, key, logging.CreateLogger("NetworkProto.Epoll"));
        await listener.StartAsync().ConfigureAwait(false);
        return listener;
    }
}

internal sealed class Listener : IConnectionListener
{
    private readonly Channel<ConnectionContext> _accepted = Channel.CreateUnbounded<ConnectionContext>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Engine[] _engines;
    private int _unbound;

    internal Listener(IPEndPoint endpoint, int[] cpus, string certificate, string key, ILogger logger)
    {
        EndPoint = endpoint;
        _engines = cpus.Select(cpu => new Engine(endpoint, cpu, certificate, key, _accepted.Writer, logger)).ToArray();
    }

    public EndPoint EndPoint { get; }

    internal async Task StartAsync()
    {
        try
        {
            await Task.WhenAll(_engines.Select(engine => engine.Started)).ConfigureAwait(false);
        }
        catch
        {
            _accepted.Writer.TryComplete();
            foreach (var engine in _engines)
            {
                engine.Stop();
            }
            await Task.WhenAll(_engines.Select(engine => engine.Stopped)).ConfigureAwait(false);
            throw;
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

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _unbound, 1) == 0)
        {
            _accepted.Writer.TryComplete();
            foreach (var engine in _engines)
            {
                engine.Enqueue(new(Engine.CommandKind.Unbind));
            }
        }
        return default;
    }

    public async ValueTask DisposeAsync()
    {
        await UnbindAsync().ConfigureAwait(false);
        while (_accepted.Reader.TryRead(out var connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        foreach (var engine in _engines)
        {
            engine.Stop();
        }
        await Task.WhenAll(_engines.Select(engine => engine.Stopped)).ConfigureAwait(false);
    }
}
