// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll;

// Outbound side of the epoll provider. It owns client-mode workers (no listener) and hands them sockets connected with a blocking connect() off the worker thread.
internal sealed class EpollClient : IProviderClient
{
    private readonly EpollWorker[] _workers;
    private int _next, _disposed;

    private EpollClient(EpollWorker[] workers) => _workers = workers;

    internal static async ValueTask<EpollClient> CreateAsync(int workerCount, ILoggerFactory logging, int[]? processors, CancellationToken cancellationToken)
    {
        var cpus = processors ?? Enumerable.Repeat(-1, workerCount).ToArray();
        // Client workers never accept, so they share a channel that nothing reads or completes.
        var unused = Channel.CreateUnbounded<IProviderConnection>();
        var logger = logging.CreateLogger("NetworkProto.Epoll.Client");
        var workers = cpus.Select(cpu => new EpollWorker(null, cpu, tls: false, string.Empty, string.Empty, unused.Writer, logger, shared: true)).ToArray();
        try
        {
            await Task.WhenAll(workers.Select(worker => worker.Started)).WaitAsync(cancellationToken).ConfigureAwait(false);
            return new EpollClient(workers);
        }
        catch
        {
            await StopAsync(workers).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<IProviderConnection> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (endpoint is not IPEndPoint { AddressFamily: AddressFamily.InterNetwork } ip)
        {
            throw new NotSupportedException("The epoll prototype currently connects to IPv4 TCP endpoints only.");
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
            throw ConnectFailure.FromErrno(fd, "Epoll");
        }
        var completion = new TaskCompletionSource<IProviderConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = _workers[(int)((uint)Interlocked.Increment(ref _next) % (uint)_workers.Length)];
        worker.Enqueue(new(EpollWorker.CommandKind.Connected, Fd: fd, Remote: ip, Connect: completion));
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        return Interlocked.Exchange(ref _disposed, 1) == 0 ? new(StopAsync(_workers)) : default;
    }

    private static async Task StopAsync(EpollWorker[] workers)
    {
        foreach (var worker in workers)
        {
            worker.Stop();
        }
        await Task.WhenAll(workers.Select(worker => worker.Stopped)).ConfigureAwait(false);
    }
}
