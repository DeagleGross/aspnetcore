// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUring;

internal sealed class IoUringTransportFactory(ILoggerFactory loggerFactory) : IConnectionListenerFactory
{
    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IConnectionListener>(new Listener(endpoint, loggerFactory.CreateLogger("NetworkProto.IoUring")));
    }

    private sealed class Listener : IConnectionListener
    {
        private readonly Socket _socket;
        private readonly IoUringEngine _engine;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _stop = new();
        private IoUringEngine.ReusableOperation? _acceptOperation;
        private readonly IoUringEngine.AcceptStream? _acceptStream;

        public Listener(EndPoint endpoint, ILogger logger)
        {
            if (endpoint is not IPEndPoint { AddressFamily: AddressFamily.InterNetwork } ip)
            {
                throw new NotSupportedException("This prototype supports IPv4 TCP endpoints only.");
            }
            _logger = logger;
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _socket.Bind(ip);
                _socket.Listen(512);
                EndPoint = _socket.LocalEndPoint!;
                if (IoUringEngine.SynchronousAcceptEnabled)
                {
                    // accept4's flags affect the accepted fd, not whether the listener blocks.
                    _socket.Blocking = false;
                }
                _engine = new IoUringEngine();
            }
            catch
            {
                _socket.Dispose();
                throw;
            }
            _logger.LogInformation("io_uring backend bound to {EndPoint}; accept/receive/send use ring operations unless diagnostic fast paths are explicitly enabled", EndPoint);
            if (_engine.AcceptMode != "oneshot")
            {
                _acceptStream = new IoUringEngine.AcceptStream(_engine, _socket, _stop.Token, _logger);
            }
        }

        public EndPoint EndPoint { get; }

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            using var linked = _engine.CompactConnections && !cancellationToken.CanBeCanceled
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            try
            {
                var token = linked?.Token ?? _stop.Token;
                var handle = _acceptStream is not null
                    ? await _acceptStream.ReadAsync(token).ConfigureAwait(false)
                    : new SafeSocketHandle(await _engine.AcceptAsync(_socket, token, ref _acceptOperation).ConfigureAwait(false), ownsHandle: true);
                Socket socket;
                try
                {
                    socket = new Socket(handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
                try
                {
                    socket.NoDelay = true;
                    return new IoUringConnection(socket, _engine, _logger);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return null;
            }
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            _stop.Cancel();
            return default;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _engine.DisposeAsync().ConfigureAwait(false);
            _acceptStream?.Dispose();
            _acceptOperation?.ReleaseSlot();
            if (_engine.DiagnosticsEnabled)
            {
                _logger.LogInformation("io_uring diagnostics {Counters}", _engine.GetDiagnostics());
            }
            _socket.Dispose();
            _stop.Dispose();
        }
    }
}
