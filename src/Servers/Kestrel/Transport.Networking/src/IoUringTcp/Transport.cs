// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp;

internal sealed class TransportFactory(bool tls, string cert, string key, ILoggerFactory logging) : IConnectionListenerFactory
{
    public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is not IPEndPoint { AddressFamily: System.Net.Sockets.AddressFamily.InterNetwork } ip || !IPAddress.IsLoopback(ip.Address))
        {
            throw new NotSupportedException("Prototype supports IPv4 loopback only.");
        }
        var listener = new Listener(ip, tls, cert, key, logging.CreateLogger("NetworkProto.Owned"));
        await listener.StartAsync().ConfigureAwait(false);
        return listener;
    }
}

internal sealed class Listener : IConnectionListener
{
    private readonly Channel<ConnectionContext> _accepted = Channel.CreateUnbounded<ConnectionContext>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Engine[] _engines;
    internal Listener(IPEndPoint endpoint, bool tls, string cert, string key, ILogger logger)
    {
        EndPoint = endpoint;
        var cpus = (Environment.GetEnvironmentVariable("NETWORKPROTO_CPUS") ?? "0,2,4,6").Split(',').Select(int.Parse).ToArray();
        _engines = cpus.Select(cpu => new Engine(endpoint, cpu, tls, cert, key, logger, _accepted.Writer)).ToArray();
    }
    public EndPoint EndPoint { get; }
    internal Task StartAsync() => Task.WhenAll(_engines.Select(engine => engine.Started));
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
        foreach (var engine in _engines)
        {
            engine.Enqueue(new Native.Command { Kind = 5 });
        }
        _accepted.Writer.TryComplete();
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

internal sealed class Engine
{
    private readonly ConcurrentQueue<Native.Command> _commands = new();
    private readonly Dictionary<ulong, Connection> _connections = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ChannelWriter<ConnectionContext> _accepted;
    private readonly ILogger _logger;
    private readonly bool _tls;
    private readonly IPEndPoint _endpoint;
    private nint _handle;
    private volatile bool _stop;
    private readonly bool _coalesce = Environment.GetEnvironmentVariable("NETWORKPROTO_COALESCE") == "1";
    private int _wakeScheduled, _pumpThread;
    private long _steps, _wakes, _commandsProcessed, _pages, _bytes;
    internal Task Started => _started.Task;
    internal Task Stopped => _stopped.Task;

    internal Engine(IPEndPoint endpoint, int cpu, bool tls, string cert, string key, ILogger logger, ChannelWriter<ConnectionContext> accepted)
    {
        _endpoint = endpoint;
        _tls = tls;
        _logger = logger;
        _accepted = accepted;
        using (ExecutionContext.SuppressFlow())
        {
            new Thread(() => Run(cpu, cert, key)) { IsBackground = true, Name = $"Owned io_uring CPU {cpu}" }.Start();
        }
    }

    internal void Enqueue(Native.Command command)
    {
        _commands.Enqueue(command);
        if (_coalesce && (Environment.CurrentManagedThreadId == _pumpThread || Interlocked.Exchange(ref _wakeScheduled, 1) != 0))
        {
            return;
        }
        Interlocked.Increment(ref _wakes);
        var error = Native.Wake(_handle);
        if (error < 0)
        {
            throw new IOException($"io_uring eventfd wake failed: {error}");
        }
    }

    internal void Stop()
    {
        _stop = true;
        Native.Wake(_handle);
    }

    private unsafe void Run(int cpu, string cert, string key)
    {
        try
        {
            _pumpThread = Environment.CurrentManagedThreadId;
            _handle = Native.Create(_endpoint.Port, _tls ? 1 : 0, cert, key, cpu,
                Environment.GetEnvironmentVariable("NETWORKPROTO_DEFER") == "0" ? 0 : 1, out var error);
            if (_handle == 0)
            {
                throw new Win32Exception(error, "Owned transport initialization failed.");
            }
            _started.SetResult();
            var commands = new Native.Command[1024];
            while (!_stop || _connections.Count != 0 || !_commands.IsEmpty)
            {
                // Clear before draining: a concurrent producer either joins this
                // batch or leaves a wake for the upcoming native wait.
                Volatile.Write(ref _wakeScheduled, 0);
                var count = 0;
                while (count < commands.Length && _commands.TryDequeue(out commands[count]))
                {
                    if (commands[count].Kind == 4)
                    {
                        _connections.Remove(commands[count].Connection);
                    }
                    count++;
                }
                _commandsProcessed += count;
                fixed (Native.Command* pointer = commands)
                {
                    var length = Native.Step(_handle, pointer, count, out var events);
                    _steps++;
                    if (length < 0)
                    {
                        throw new IOException($"Native pump failed: {length}");
                    }
                    for (var i = 0; i < length; i++)
                    {
                        ref var item = ref events[i];
                        if (item.Kind == 1)
                        {
                            var c = new Connection(this, item.Connection, _endpoint, _tls);
                            _connections.Add(item.Connection, c);
                            if (!_accepted.TryWrite(c))
                            {
                                c.Abort(new ConnectionAbortedException("Listener stopped."));
                            }
                        }
                        else if (_connections.TryGetValue(item.Connection, out var c))
                        {
                            switch (item.Kind)
                            {
                                case 2:
                                    _pages++;
                                    _bytes += item.Length;
                                    c.Input.Append(item.Data, item.Length, item.Page);
                                    break;
                                case 3:
                                    c.Input.End(item.Result < 0 ? new IOException($"Transport read ended: {item.Result}") : null);
                                    break;
                                case 4:
                                    c.Sent(item.Result);
                                    break;
                                case 5:
                                    c.NativeClosed();
                                    break;
                            }
                        }
                        else if (item.Kind == 5)
                        {
                            _commands.Enqueue(new Native.Command { Kind = 4, Connection = item.Connection });
                        }
                        else if (item.Kind == 6)
                        {
                            throw new IOException($"Native accept failed: {item.Result}");
                        }
                    }
                }
                if (_stop)
                {
                    foreach (var c in _connections.Values.ToArray())
                    {
                        c.Abort(new ConnectionAbortedException("Engine stopped."));
                    }
                }
            }
            var counters = new ulong[8];
            fixed (ulong* p = counters)
            {
                Native.Stats(_handle, p);
            }
            Console.WriteLine("OWNED_METRICS " + JsonSerializer.Serialize(new {
                cpu, tls = _tls, coalesced = _coalesce, stepInterop = _steps, wakeInterop = _wakes, commands = _commandsProcessed,
                pages = _pages, bytes = _bytes, acceptSqes = counters[0], receiveSqes = counters[1],
                sendSqes = counters[2], pollSqes = counters[3], cqes = counters[4],
                nativeSubmitWaitCalls = counters[5], returnedPages = counters[6], sslReads = counters[7],
                receiveAdapterCopyBytes = 0
            }));
            Native.Destroy(_handle);
            _stopped.SetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Owned transport pump failed.");
            _started.TrySetException(ex);
            _accepted.TryComplete(ex);
            _stopped.TrySetException(ex);
        }
    }

    internal void Forget(ulong id)
    {
        // The caller queues this marker; dictionary mutation stays on the pump.
        Enqueue(new Native.Command { Kind = 4, Connection = id });
    }
}

internal sealed class Connection : DefaultConnectionContext
{
    private readonly Engine _engine;
    private readonly ulong _id;
    private readonly Pipe _output = new(new PipeOptions(useSynchronizationContext: false));
    private readonly TaskCompletionSource _nativeClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closed = new();
    private readonly Task _sending;
    private TaskCompletionSource<int>? _send;
    private int _closing;
    internal OwnedPipeReader Input { get; }

    internal Connection(Engine engine, ulong id, IPEndPoint endpoint, bool tls) : base($"owned-{id:x}")
    {
        _engine = engine;
        _id = id;
        LocalEndPoint = endpoint;
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        Input = new OwnedPipeReader(page => engine.Enqueue(new Native.Command { Kind = 2, Connection = id, Page = page }));
        Transport = new DuplexPipe(Input, _output.Writer);
        ConnectionClosed = _closed.Token;
        if (tls)
        {
            Features.Set<ITlsConnectionFeature>(new TlsFeature());
            Features.Set<ITlsHandshakeFeature>(new TlsFeature());
        }
        _sending = SendLoop();
    }

    private async Task SendLoop()
    {
        try
        {
            while (true)
            {
                var read = await _output.Reader.ReadAsync().ConfigureAwait(false);
                try
                {
                    foreach (var segment in read.Buffer)
                    {
                        using var pin = segment.Pin();
                        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _send = source;
                        unsafe
                        {
                            _engine.Enqueue(new Native.Command { Kind = 1, Connection = _id, Data = pin.Pointer, Length = segment.Length });
                        }
                        var count = await source.Task.ConfigureAwait(false);
                        if (count < 0)
                        {
                            throw new IOException($"Native send failed: {count}");
                        }
                    }
                }
                finally
                {
                    _output.Reader.AdvanceTo(read.Buffer.End);
                }
                if (read.IsCompleted || read.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Input.End(ex);
        }
        finally
        {
            _output.Reader.Complete();
            Close();
        }
    }

    internal void Sent(int count) => _send?.TrySetResult(count);
    internal void NativeClosed()
    {
        _send?.TrySetResult(-125);
        Input.End(null);
        _closed.Cancel();
        _nativeClosed.TrySetResult();
    }
    private void Close()
    {
        if (Interlocked.Exchange(ref _closing, 1) == 0)
        {
            _engine.Enqueue(new Native.Command { Kind = 3, Connection = _id });
        }
    }
    public override void Abort(ConnectionAbortedException abortReason)
    {
        Input.End(abortReason);
        _output.Reader.CancelPendingRead();
        Close();
    }
    public override async ValueTask DisposeAsync()
    {
        Input.Complete();
        _output.Writer.Complete();
        await _sending.ConfigureAwait(false);
        await _nativeClosed.Task.ConfigureAwait(false);
        _engine.Forget(_id);
        _closed.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class TlsFeature : ITlsConnectionFeature, ITlsHandshakeFeature
    {
        public X509Certificate2? ClientCertificate { get; set; }
        public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken) => Task.FromResult(ClientCertificate);
        public SslProtocols Protocol => SslProtocols.Tls12;
        public TlsCipherSuite? NegotiatedCipherSuite => TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256;
#pragma warning disable SYSLIB0058 // Required by the existing ITlsHandshakeFeature contract.
        public CipherAlgorithmType CipherAlgorithm => CipherAlgorithmType.Aes128;
        public int CipherStrength => 128;
        public HashAlgorithmType HashAlgorithm => HashAlgorithmType.Sha256;
        public int HashStrength => 256;
        public ExchangeAlgorithmType KeyExchangeAlgorithm => ExchangeAlgorithmType.DiffieHellman;
        public int KeyExchangeStrength => 0;
#pragma warning restore SYSLIB0058
        public string HostName => string.Empty;
    }
}
