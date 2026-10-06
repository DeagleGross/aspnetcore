// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

internal sealed class Connection : DefaultConnectionContext
{
    private readonly WindowsWorker _worker;
    private readonly ConnectionState _state;
    private readonly Pipe _output;
    private readonly TaskCompletionSource _nativeClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _outputCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closed = new();
    private readonly Lock _disposeGate = new();
    private readonly Action _outputReady;
    private Task? _disposing;
    private ValueTask<ReadResult> _pendingRead;
    private ReadResult _read;
    private ReadOnlySequence<byte> _remaining;
    private MemoryHandle _pin;
    private bool _reading, _hasRead, _sending, _outputStopped, _transportClosed;
    private int _outputScheduled;
    private Exception? _sendError;

    internal LeasedPipeReader Input { get; }

    internal Connection(WindowsWorker worker, ConnectionState state) : base($"owned-{worker.Backend}-{worker.Index}-{state.Id:x}")
    {
        _worker = worker;
        _state = state;
        _outputReady = QueueOutput;
        _output = new Pipe(new PipeOptions(pool: worker.OutputPool, readerScheduler: PipeScheduler.Inline,
            writerScheduler: PipeScheduler.ThreadPool, useSynchronizationContext: false));
        LocalEndPoint = state.LocalEndPoint;
        RemoteEndPoint = state.RemoteEndPoint;
        Input = new LeasedPipeReader(owner => worker.Enqueue(new(WindowsWorker.CommandKind.ReturnPage, state, owner)));
        Transport = new DuplexPipe(Input, _output.Writer);
        ConnectionClosed = _closed.Token;
        QueueOutput();
    }

    private void QueueOutput()
    {
        if (Interlocked.Exchange(ref _outputScheduled, 1) == 0)
        {
            _worker.Enqueue(new(WindowsWorker.CommandKind.OutputReady, _state));
        }
    }

    internal void PumpOutput()
    {
        Volatile.Write(ref _outputScheduled, 0);
        if (_outputStopped || _sending)
        {
            return;
        }
        try
        {
            if (_reading)
            {
                if (!_pendingRead.IsCompleted)
                {
                    return;
                }
                TakeRead();
            }
            if (_sendError is not null)
            {
                throw _sendError;
            }
            for (var i = 0; i < 16; i++)
            {
                if (_transportClosed)
                {
                    CompleteOutput();
                    return;
                }
                if (_hasRead && _remaining.IsEmpty)
                {
                    _output.Reader.AdvanceTo(_read.Buffer.End);
                    _hasRead = false;
                    if (_read.IsCompleted || _read.IsCanceled)
                    {
                        CompleteOutput();
                        return;
                    }
                }
                if (!_hasRead)
                {
#pragma warning disable CA2012 // The worker consumes each stored read exactly once after notification.
                    _pendingRead = _output.Reader.ReadAsync();
#pragma warning restore CA2012
                    _reading = true;
                    if (!_pendingRead.IsCompleted)
                    {
                        _pendingRead.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_outputReady);
                        return;
                    }
                    TakeRead();
                    if (_remaining.IsEmpty)
                    {
                        continue;
                    }
                }
                var position = _remaining.Start;
                while (_remaining.TryGet(ref position, out var segment))
                {
                    if (segment.IsEmpty)
                    {
                        continue;
                    }
                    _remaining = _remaining.Slice(segment.Length);
                    _pin = segment.Pin();
                    _sending = true;
                    _state.Send(segment);
                    break;
                }
                if (_sending)
                {
                    return;
                }
            }
            QueueOutput();
        }
        catch (Exception error)
        {
            if (_sending)
            {
                _sending = false;
                _pin.Dispose();
            }
            Input.End(error);
            CompleteOutput(error);
        }
    }

    private void TakeRead()
    {
        _reading = false;
        _read = _pendingRead.GetAwaiter().GetResult();
        _pendingRead = default;
        _remaining = _read.Buffer;
        _hasRead = true;
    }

    private void CompleteOutput(Exception? error = null)
    {
        if (_outputStopped)
        {
            return;
        }
        if (_sending)
        {
            throw new InvalidOperationException("The native send still owns its output memory.");
        }
        _outputStopped = true;
        if (_hasRead)
        {
            _output.Reader.AdvanceTo(_read.Buffer.End);
            _hasRead = false;
        }
        _output.Reader.Complete(error);
        _outputCompleted.TrySetResult();
        _state.Close(error, abortive: error is not null);
    }

    internal void Sent(Exception? error)
    {
        if (!_sending)
        {
            throw new InvalidOperationException("Unexpected terminal send completion.");
        }
        _sending = false;
        _pin.Dispose();
        _sendError = error;
        PumpOutput();
    }

    internal void NativeClosed(Exception? error)
    {
        _transportClosed = true;
        Input.End(error);
        _output.Reader.CancelPendingRead();
        QueueOutput();
        ThreadPool.UnsafeQueueUserWorkItem(static connection => connection.SignalClosed(), this, preferLocal: false);
    }

    private void SignalClosed()
    {
        try
        {
            _closed.Cancel();
        }
        finally
        {
            _nativeClosed.TrySetResult();
        }
    }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        Input.End(abortReason);
        _output.Reader.CancelPendingRead();
        _worker.Enqueue(new(WindowsWorker.CommandKind.Close, _state, Error: abortReason));
    }

    public override ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposing ??= DisposeCore());
        }
    }

    private async Task DisposeCore()
    {
        Input.Complete();
        _output.Writer.Complete();
        QueueOutput();
        await _outputCompleted.Task.ConfigureAwait(false);
        await _nativeClosed.Task.ConfigureAwait(false);
        _worker.Enqueue(new(WindowsWorker.CommandKind.Forget, _state));
        _closed.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
