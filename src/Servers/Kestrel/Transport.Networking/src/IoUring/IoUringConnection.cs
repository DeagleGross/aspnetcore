// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUring;

internal sealed class IoUringConnection : DefaultConnectionContext
{
    private readonly Socket _socket;
    private readonly IoUringEngine _engine;
    private readonly ILogger _logger;
    private readonly PipelineApplication _application;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly Task _receiving;
    private readonly Task _sending;
    private readonly IDuplexPipe _originalTransport;
    private static long _nextId;
    private IoUringEngine.ReusableOperation? _receiveOperation;
    private IoUringEngine.ReusableOperation? _sendOperation;

    public IoUringConnection(Socket socket, IoUringEngine engine, ILogger logger)
        : base($"io-uring-{Interlocked.Increment(ref _nextId)}")
    {
        _socket = socket;
        _engine = engine;
        _logger = logger;
        _application = new PipelineApplication(engine.CompactConnections);
        LocalEndPoint = socket.LocalEndPoint;
        RemoteEndPoint = socket.RemoteEndPoint;
        Transport = _originalTransport = _application.Transport;
        ConnectionClosed = _closed.Token;
        _receiving = ReceiveAsync(_application);
        _sending = SendAsync(_application);
    }

    private async Task ReceiveAsync(TransportApplication application)
    {
        Exception? error = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var memory = application.GetReceiveMemory();
                var count = await _engine.ReceiveAsync(_socket, memory, _stop.Token, ref _receiveOperation).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                var result = await application.OnReceiveAsync(count).ConfigureAwait(false);
                if (result.IsCompleted || result.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (ConnectionResetException ex)
        {
            error = ex;
            LogPeerReset();
            Stop();
        }
        catch (Exception ex)
        {
            error = ex;
            _logger.LogError(ex, "io_uring receive failed for {ConnectionId}", ConnectionId);
            Stop();
        }
        finally
        {
            application.OnClosed(error);
            await _closed.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task SendAsync(TransportApplication application)
    {
        Exception? error = null;
        var completed = false;
        try
        {
            while (true)
            {
                var result = await application.Output.ReadAsync(_stop.Token).ConfigureAwait(false);
                var buffer = result.Buffer;
                try
                {
                    foreach (var segment in buffer)
                    {
                        var remaining = MemoryMarshal.AsMemory(segment);
                        while (!remaining.IsEmpty)
                        {
                            var count = await _engine.SendAsync(_socket, remaining, _stop.Token, ref _sendOperation).ConfigureAwait(false);
                            if (count == 0)
                            {
                                throw new IOException("io_uring send made no progress.");
                            }
                            remaining = remaining[count..];
                        }
                    }
                }
                finally
                {
                    application.Output.AdvanceTo(buffer.End);
                }
                if (result.IsCompleted || result.IsCanceled)
                {
                    completed = result.IsCompleted && !result.IsCanceled;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (ConnectionResetException ex)
        {
            error = ex;
            LogPeerReset();
        }
        catch (Exception ex)
        {
            error = ex;
            _logger.LogError(ex, "io_uring send failed for {ConnectionId}", ConnectionId);
        }
        finally
        {
            application.Output.Complete(error);
            if (_engine.ShutdownOnClose && completed && error is null && !_stop.IsCancellationRequested)
            {
                try
                {
                    // Output has drained. Shutdown lets the pending receive finish
                    // with EOF; its terminal CQE still owns the pin and fd reference.
                    _socket.Shutdown(SocketShutdown.Both);
                    _application.Cancel();
                }
                catch (SocketException ex)
                {
                    _logger.LogError(ex, "io_uring graceful shutdown failed for {ConnectionId}", ConnectionId);
                    Stop();
                }
            }
            else
            {
                Stop();
            }
        }
    }

    private void Stop()
    {
        _stop.Cancel();
        _application.Cancel();
    }

    private void LogPeerReset()
    {
        _logger.LogWarning("io_uring peer reset {ConnectionId} errno=104 time_ms={Time}", ConnectionId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        _logger.LogDebug(abortReason, "Aborting {ConnectionId}", ConnectionId);
        Stop();
    }

    public override async ValueTask DisposeAsync()
    {
        _originalTransport.Input.Complete();
        _originalTransport.Output.Complete();
        await Task.WhenAll(_receiving, _sending).ConfigureAwait(false);
        _receiveOperation?.ReleaseSlot();
        _sendOperation?.ReleaseSlot();
        _socket.Dispose();
        _stop.Dispose();
        _closed.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
