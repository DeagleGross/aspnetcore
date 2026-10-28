// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
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
    private int _firstWorkerShare = 100;
    private readonly bool _adaptiveBalance;
    private readonly bool _rio;
    private readonly ILogger _logger;
    private PoolAcceptor? _acceptor;
    private long _balanceStamp;
    private long[]? _balanceWait;

    internal SocketHandle Socket { get; }
    internal bool PoolAccept { get; }
    internal bool Shared { get; }
    internal Schannel.Credential? TlsCredential { get; }
    internal TlsSessionServer? TlsSession { get; }
    internal HandshakeScheduler? Handshakes { get; }
    public EndPoint EndPoint { get; }

    internal WindowsListener(IPEndPoint endpoint, bool rio, int[] cpus, ILogger logger, bool shared, NetworkNativeTlsOptions? nativeTls)
    {
        Shared = shared;
        _rio = rio;
        _logger = logger;
        PoolAccept = Environment.GetEnvironmentVariable("NETWORKPROTO_WINDOWS_ACCEPT") == "pool";
        var balance = Environment.GetEnvironmentVariable("NETWORKPROTO_WINDOWS_BALANCE");
        _adaptiveBalance = balance == "adaptive" && !PoolAccept;
        if (int.TryParse(balance, NumberStyles.None, CultureInfo.InvariantCulture, out var share))
        {
            _firstWorkerShare = Math.Clamp(share, 0, 100);
        }
        _winsock = new WinsockSession();
        try
        {
            var useSession = nativeTls is not null && TlsSessionServer.IsSelected;
            TlsSession = useSession ? new TlsSessionServer(nativeTls!) : null;
            TlsCredential = nativeTls is null || useSession ? null : new Schannel.Credential(nativeTls);
            Handshakes = (TlsCredential is not null || TlsSession is not null) && Environment.GetEnvironmentVariable("NETWORKPROTO_TLS_HANDSHAKE_OFFLOAD") != "0"
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
            TlsSession?.Dispose();
            _winsock.Dispose();
            throw;
        }
    }

    internal async Task StartAsync()
    {
        foreach (var worker in _workers)
        {
            worker.Start();
        }
        await Task.WhenAll(_workers.Select(worker => worker.Started)).ConfigureAwait(false);
        if (PoolAccept)
        {
            // Workers are running, so every accepted socket has a live adopter.
            _acceptor = new PoolAcceptor(Socket, _rio, Dispatch, Failed, _logger);
            _acceptor.Start();
        }
    }

    internal int FirstWorkerShare => Volatile.Read(ref _firstWorkerShare);

    internal void Dispatch(SocketHandle socket)
    {
        var round = (uint)Interlocked.Increment(ref _nextWorker);
        var count = (uint)_workers.Length;
        var index = round % count;
        if (count > 1)
        {
            if (_adaptiveBalance)
            {
                Rebalance();
            }
            if (index == 0 && _firstWorkerShare < 100 && (round / count) % 100 >= _firstWorkerShare)
            {
                index = 1 + (round / count) % (count - 1);
            }
        }
        _workers[index].Enqueue(new WindowsWorker.Command(WindowsWorker.CommandKind.Accepted, Socket: socket));
    }

    // Worker 0 also owns every AcceptEx completion, so under connection churn it is busier than its peers.
    // Steer a fraction of its round-robin turns to the other workers until measured busy time evens out.
    // Runs only on worker 0's thread (the sole caller of Dispatch), so the sampling state needs no locking.
    private void Rebalance()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = now - _balanceStamp;
        if (_balanceWait is not null && elapsed < Stopwatch.Frequency / 50)
        {
            return;
        }
        var wait = new long[_workers.Length];
        for (var i = 0; i < wait.Length; i++)
        {
            wait[i] = _workers[i].WaitTicks;
        }
        if (_balanceWait is not null)
        {
            var first = 1.0 - (double)(wait[0] - _balanceWait[0]) / elapsed;
            var others = 0.0;
            for (var i = 1; i < wait.Length; i++)
            {
                others += 1.0 - (double)(wait[i] - _balanceWait[i]) / elapsed;
            }
            others /= wait.Length - 1;
            if (first > others + 0.05)
            {
                _firstWorkerShare = Math.Max(0, _firstWorkerShare - 10);
            }
            else if (first < others - 0.05)
            {
                _firstWorkerShare = Math.Min(100, _firstWorkerShare + 10);
            }
        }
        _balanceWait = wait;
        _balanceStamp = now;
    }

    internal bool Publish(IProviderConnection connection) => _accepted.Writer.TryWrite(connection);

    internal void Failed(Exception error)
    {
        _accepted.Writer.TryComplete(error);
        _acceptor?.Stop();
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
            if (_acceptor is not null)
            {
                _acceptor.Stop();
            }
            else
            {
                _workers[0].Enqueue(new WindowsWorker.Command(WindowsWorker.CommandKind.Unbind));
            }
        }
        await (_acceptor?.Drained ?? _workers[0].Unbound).WaitAsync(cancellationToken).ConfigureAwait(false);
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
            if (_acceptor is { Drained.IsCompleted: true } acceptor)
            {
                Console.WriteLine("WINDOWS_ACCEPTOR " + JsonSerializer.Serialize(new { accepted = acceptor.Accepted, rejected = acceptor.Rejected }));
                acceptor.Dispose();
            }
            Socket.Dispose();
            if (Handshakes is not null)
            {
                await Handshakes.DrainAsync().ConfigureAwait(false);
            }
            TlsCredential?.Dispose();
            TlsSession?.Dispose();
            _winsock.Dispose();
        }
    }
}
