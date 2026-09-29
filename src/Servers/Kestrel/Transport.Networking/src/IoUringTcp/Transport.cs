// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp;

internal sealed class TransportFactory(bool tls, string cert, string key, ILoggerFactory logging, bool layered = false) : IConnectionListenerFactory
{
    public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (endpoint is not IPEndPoint { AddressFamily: System.Net.Sockets.AddressFamily.InterNetwork, Port: > 0 } ip || !IPAddress.IsLoopback(ip.Address))
        {
            throw new NotSupportedException("Prototype supports IPv4 loopback and a fixed port only.");
        }
        var listener = new Listener(ip, tls, cert, key, logging.CreateLogger("NetworkProto.Owned"), layered);
        await listener.StartAsync().ConfigureAwait(false);
        return listener;
    }
}

internal sealed class Listener : IConnectionListener
{
    private readonly Channel<ConnectionContext> _accepted = Channel.CreateUnbounded<ConnectionContext>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Engine[] _engines;
    private int _unbound;

    internal Listener(IPEndPoint endpoint, bool tls, string cert, string key, ILogger logger, bool layered)
    {
        EndPoint = endpoint;
        var cpus = (Environment.GetEnvironmentVariable("NETWORKPROTO_CPUS") ?? "0,2,4,6")
            .Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        _engines = cpus.Select(cpu => new Engine(endpoint, cpu, tls, cert, key, logger, _accepted.Writer, layered)).ToArray();
    }
    public EndPoint EndPoint
    {
        get;
    }
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
                engine.Enqueue(new Native.Command { Kind = 5 });
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
