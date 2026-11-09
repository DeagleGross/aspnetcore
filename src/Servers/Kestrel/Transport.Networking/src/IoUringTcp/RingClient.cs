// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp;

// Outbound side of the io_uring provider. It owns client-mode engines (a ring with no listener) and hands them sockets connected with a blocking connect()
// off the ring thread; the ring then owns the receive/send operations exactly as it does for accepted sockets.
internal sealed class RingClient : IProviderClient
{
    private readonly Engine[] _engines;
    private int _next, _disposed;

    private RingClient(Engine[] engines) => _engines = engines;

    internal static async ValueTask<RingClient> CreateAsync(int engineCount, ILoggerFactory logging, int[]? processors, CancellationToken cancellationToken)
    {
        var cpus = processors ?? Enumerable.Repeat(-1, engineCount).ToArray();
        // Client engines never accept, so they share a channel that nothing reads or completes.
        var unused = Channel.CreateUnbounded<IProviderConnection>();
        var logger = logging.CreateLogger("NetworkProto.Owned.Client");
        var engines = cpus.Select(cpu => new Engine(null, cpu, tls: false, string.Empty, string.Empty, logger, unused.Writer, layered: false, shared: true)).ToArray();
        try
        {
            await Task.WhenAll(engines.Select(engine => engine.Started)).WaitAsync(cancellationToken).ConfigureAwait(false);
            return new RingClient(engines);
        }
        catch
        {
            await StopAsync(engines).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<IProviderConnection> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (endpoint is not IPEndPoint { AddressFamily: AddressFamily.InterNetwork } ip)
        {
            throw new NotSupportedException("The io_uring prototype currently connects to IPv4 TCP endpoints only.");
        }
        var address = BinaryPrimitives.ReadUInt32BigEndian(ip.Address.GetAddressBytes());
        // The prototype connects with a blocking connect() on the thread pool: connection setup is not the measured path.
        var connect = Task.Run(() => Native.Connect(address, ip.Port), CancellationToken.None);
        int fd;
        try
        {
            fd = await connect.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = connect.ContinueWith(static task =>
            {
                if (task.Status == TaskStatus.RanToCompletion && task.Result >= 0)
                {
                    Native.Close(task.Result);
                }
            }, TaskScheduler.Default);
            throw;
        }
        if (fd < 0)
        {
            throw ConnectFailure.FromErrno(fd, "io_uring");
        }
        var completion = new TaskCompletionSource<IProviderConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = _engines[(int)((uint)Interlocked.Increment(ref _next) % (uint)_engines.Length)];
        engine.Connected(fd, ip, completion);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        return Interlocked.Exchange(ref _disposed, 1) == 0 ? new(StopAsync(_engines)) : default;
    }

    private static async Task StopAsync(Engine[] engines)
    {
        foreach (var engine in engines)
        {
            engine.Stop();
        }
        await Task.WhenAll(engines.Select(engine => engine.Stopped)).ConfigureAwait(false);
    }
}
