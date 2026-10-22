// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;

namespace System.Net.Transport;

/// <summary>A shared owned-memory connection with provider-owned native execution.</summary>
public abstract class NetworkConnection : IAsyncDisposable
{
    private readonly NetworkInputReader _input = new();
    private readonly Pipe _output;
    private readonly MemoryPool<byte> _outputPool;
    private readonly NetworkMemoryKind _outputKind;
    private readonly Action _outputReady;
    private ConcurrentQueue<SendRequest>? _requests;
    private readonly TaskCompletionSource _nativeClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _outputStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _receiveReleased;
    private readonly CancellationTokenSource _closed = new();
    private readonly Lock _disposeGate = new();
    private readonly Lock _admissionGate = new();
    private Task? _disposing;
    private ValueTask<ReadResult> _pendingRead;
    private ReadResult _read;
    private ReadOnlySequence<byte> _remaining;
    private SendRequest? _request;
    private MemoryHandle _pin;
    private int _sendLength, _sendMode, _receivePages, _disposingStarted, _outputScheduled;
    private long _queuedBytes;
    private bool _reading, _hasRead, _sending, _stopped, _transportClosed, _pumping;
    private Exception? _error;
    private NetworkTlsInfo? _tlsInfo;

    /// <summary>Initializes provider-independent receive ownership and output progression.</summary>
    protected NetworkConnection(string id, EndPoint? localEndPoint, EndPoint? remoteEndPoint, MemoryPool<byte>? outputPool = null,
        NetworkMemoryKind outputKind = NetworkMemoryKind.Managed, bool isTls = false)
    {
        Id = id;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        _outputPool = outputPool ?? MemoryPool<byte>.Shared;
        _outputKind = outputKind;
        _output = new Pipe(new PipeOptions(pool: _outputPool, readerScheduler: PipeScheduler.Inline,
            writerScheduler: PipeScheduler.ThreadPool, useSynchronizationContext: false));
        _outputReady = QueueOutput;
        IsTls = isTls;
    }

    /// <summary>Gets a provider-stable connection identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the local endpoint.</summary>
    public EndPoint? LocalEndPoint { get; }

    /// <summary>Gets the peer endpoint.</summary>
    public EndPoint? RemoteEndPoint { get; }

    /// <summary>Gets whether this connection presents authenticated TLS plaintext.</summary>
    public bool IsTls { get; }

    /// <summary>Gets the TLS session the provider established before publishing this connection, or null for plain TCP.</summary>
    public NetworkTlsInfo? TlsInfo => Volatile.Read(ref _tlsInfo);

    /// <summary>Gets the native-close notification token.</summary>
    public CancellationToken ConnectionClosed => _closed.Token;

    /// <summary>Gets or sets the admission budget for direct queued sends.</summary>
    public long MaxQueuedSendBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets the pipe output mode. Do not mix pipe output and direct SendAsync/EnqueueSend on one connection.
    /// </summary>
    public PipeWriter Output
    {
        get
        {
            SelectSendMode(1);
            QueueOutput();
            return _output.Writer;
        }
    }

    /// <summary>Returns a copy-free input adapter and the worker-drained output pipe.</summary>
    public IDuplexPipe AsDuplexPipe()
    {
        return new DuplexPipe(_input, Output);
    }

    /// <summary>
    /// Creates a non-owning stream view and hands unconsumed pre-read bytes to its consumer.
    /// Dispose the connection separately; pipe/direct-send consumption modes remain exclusive.
    /// </summary>
    public Stream AsStream()
    {
        _input.HandoffConsumer();
        return new ConnectionStream(_input.AsStream(leaveOpen: true), Output.AsStream(leaveOpen: true));
    }

    /// <summary>Receives a borrowed snapshot of all currently unconsumed bytes.</summary>
    public async ValueTask<NetworkReceiveResult> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var result = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new NetworkReceiveResult(_input, result);
    }

    /// <summary>Advances consumed and examined cursors, releasing reader references to completed pages.</summary>
    public void AdvanceReceiveTo(SequencePosition consumed, SequencePosition examined)
    {
        _input.AdvanceTo(consumed, examined);
    }

    /// <summary>Advances both cursors to the same position.</summary>
    public void AdvanceReceiveTo(SequencePosition consumed)
    {
        _input.AdvanceTo(consumed);
    }

    /// <summary>Rents provider-appropriate writable storage which can be sliced and retained.</summary>
    public NetworkBuffer AllocateBuffer(int minimumSize = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumSize);
        return NetworkBuffer.TakeOwnership(_outputPool.Rent(minimumSize), _outputKind);
    }

    /// <summary>
    /// Queues borrowed bytes and completes only after native send ownership ends.
    /// Cancellation aborts the connection so a partially sent framed message cannot be skipped.
    /// </summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        return SendAsync(new ReadOnlySequence<byte>(data), cancellationToken);
    }

    /// <summary>Queues borrowed bytes until terminal native completion.</summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> data) => SendAsync(data, default);

    /// <summary>Queues a borrowed sequence as one request until terminal native completion.</summary>
    public ValueTask SendAsync(ReadOnlySequence<byte> data) => SendAsync(data, default);

    /// <summary>Queues borrowed segments as one request, retaining native ownership until terminal completion.</summary>
    public ValueTask SendAsync(ReadOnlySequence<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SelectSendMode(2);
        var request = new SendRequest(data, null, cancellationToken, observed: true);
        Admit(request);
        return new ValueTask(request.Completion!.Task);
    }

    /// <summary>
    /// Adds independent ownership and queues a slice. The caller can release its reference immediately.
    /// A later send error is reported through connection closure.
    /// </summary>
    public void EnqueueSend(NetworkBufferSlice data)
    {
        SelectSendMode(2);
        var owner = data.Retain();
        Admit(new SendRequest(new ReadOnlySequence<byte>(owner.Memory), owner, default, observed: false));
    }

    /// <summary>Retains a multi-page payload before enqueue; the caller can dispose its payload immediately.</summary>
    public void EnqueueSend(NetworkPayload data)
    {
        ArgumentNullException.ThrowIfNull(data);
        SelectSendMode(2);
        var owner = data.Retain();
        Admit(new SendRequest(owner.Sequence, owner, default, observed: false));
    }

    private void SelectSendMode(int mode)
    {
        var previous = Interlocked.CompareExchange(ref _sendMode, mode, 0);
        if (previous != 0 && previous != mode)
        {
            throw new InvalidOperationException("Pipe output and direct queued sends are separate consumption modes.");
        }
    }

    private void QueueOutput()
    {
        if (!Volatile.Read(ref _stopped) && Interlocked.Exchange(ref _outputScheduled, 1) == 0)
        {
            ScheduleOutputCore();
        }
    }

    private void Admit(SendRequest request)
    {
        lock (_admissionGate)
        {
            if (_stopped || _transportClosed || Volatile.Read(ref _disposingStarted) != 0)
            {
                request.Owner?.Dispose();
                throw new IOException("The network connection is closing.");
            }
            var total = Interlocked.Add(ref _queuedBytes, request.Buffer.Length);
            if (MaxQueuedSendBytes <= 0 || total > MaxQueuedSendBytes)
            {
                Interlocked.Add(ref _queuedBytes, -request.Buffer.Length);
                request.Owner?.Dispose();
                throw new InvalidOperationException("The direct-send admission budget was exceeded.");
            }
            if (request.CancellationToken.CanBeCanceled)
            {
                request.Registration = request.CancellationToken.UnsafeRegister(static state =>
                    ((NetworkConnection)state!).Abort(new OperationCanceledException("A queued or active send was cancelled.")), this);
            }
            (_requests ??= new ConcurrentQueue<SendRequest>()).Enqueue(request);
        }
        QueueOutput();
    }

    /// <summary>Requests cancellation on the owning provider, without freeing live native storage.</summary>
    public void Abort(Exception? reason = null)
    {
        var error = reason ?? new IOException("The network connection was aborted.");
        _input.End(error);
        _output.Reader.CancelPendingRead();
        AbortCore(error);
    }

    /// <summary>Completes output and waits for native operations and retained receive pages to drain.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposing ??= DisposeCore());
        }
    }

    private async Task DisposeCore()
    {
        Volatile.Write(ref _disposingStarted, 1);
        _input.Complete();
        _output.Writer.Complete();
        QueueOutput();
        await _outputStopped.Task.ConfigureAwait(false);
        await _nativeClosed.Task.ConfigureAwait(false);
        if (Volatile.Read(ref _receivePages) != 0)
        {
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _receiveReleased, released);
            if (Volatile.Read(ref _receivePages) == 0)
            {
                released.TrySetResult();
            }
            await released.Task.ConfigureAwait(false);
        }
        await ForgetCoreAsync().ConfigureAwait(false);
        _closed.Dispose();
    }

    /// <summary>Schedules output progression on the owning provider thread.</summary>
    protected abstract void ScheduleOutputCore();

    /// <summary>
    /// Submits one borrowed segment using the core's live pin. Do not pin again or release the address.
    /// Call CompleteSendCore only after all native ownership of that address ends.
    /// </summary>
    protected abstract void SubmitSendCore(ReadOnlyMemory<byte> data, nint pinnedAddress, bool final);

    /// <summary>Requests a graceful provider close after output completion.</summary>
    protected abstract void CloseCore();

    /// <summary>Marshals an abort request to the owning provider.</summary>
    protected abstract void AbortCore(Exception error);

    /// <summary>Releases provider connection state after receive leases and native operations drain.</summary>
    protected abstract ValueTask ForgetCoreAsync();

    /// <summary>Publishes an already-filled provider lease without copying its bytes.</summary>
    protected void PublishReceiveCore(IMemoryOwner<byte> owner, int count, NetworkMemoryKind kind)
    {
        Interlocked.Increment(ref _receivePages);
        _input.Append(new NetworkBuffer.Storage(owner, kind, this), count);
    }

    /// <summary>Records the established TLS session. Call before the provider publishes the connection to consumers.</summary>
    protected void SetTlsInfoCore(NetworkTlsInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Volatile.Write(ref _tlsInfo, info);
    }

    /// <summary>Reports normal receive EOF or a provider receive failure.</summary>
    protected void EndReceiveCore(Exception? error = null) => _input.End(error);

    /// <summary>Reports terminal completion of a submitted segment on the owning provider.</summary>
    protected void CompleteSendCore(int count, Exception? error = null)
    {
        if (!_sending)
        {
            throw new InvalidOperationException("Unexpected shared send completion.");
        }
        _sending = false;
        _pin.Dispose();
        _error ??= error;
        if (count != _sendLength)
        {
            _error ??= new IOException($"Native send failed or ended short: {count}, expected {_sendLength}.");
        }
        if (!_pumping)
        {
            QueueOutput();
        }
    }

    /// <summary>Reports final native close only after the provider no longer references send/receive storage.</summary>
    protected void CompleteCloseCore(Exception? error = null)
    {
        _transportClosed = true;
        _error ??= error;
        if (_sending)
        {
            CompleteSendCore(-1);
        }
        _input.End(_error);
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

    /// <summary>Drains pipe output or direct sends on the owning thread with bounded synchronous progress.</summary>
    protected void PumpOutputCore()
    {
        Volatile.Write(ref _outputScheduled, 0);
        if (_stopped || _sending || Volatile.Read(ref _sendMode) == 0 && Volatile.Read(ref _disposingStarted) == 0)
        {
            return;
        }
        _pumping = true;
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
            for (var burst = 0; burst < 16; burst++)
            {
                if (_error is not null || _transportClosed)
                {
                    FinishOutput(_error ?? new IOException("The network connection closed."), gracefulTransportClose: _error is null);
                    return;
                }
                if (Volatile.Read(ref _sendMode) == 2)
                {
                    if (_request is not null && _remaining.IsEmpty)
                    {
                        FinishRequest(_request, null);
                        _request = null;
                    }
                    if (_request is null)
                    {
                        if (_requests is null || !_requests.TryDequeue(out _request))
                        {
                            if (Volatile.Read(ref _disposingStarted) != 0)
                            {
                                FinishOutput(null);
                            }
                            return;
                        }
                        _remaining = _request.Buffer;
                    }
                    if (_remaining.IsEmpty)
                    {
                        continue;
                    }
                }
                else
                {
                    if (_hasRead && _remaining.IsEmpty)
                    {
                        _output.Reader.AdvanceTo(_read.Buffer.End);
                        _hasRead = false;
                        if (_read.IsCompleted || _read.IsCanceled)
                        {
                            FinishOutput(null);
                            return;
                        }
                    }
                    if (!_hasRead)
                    {
#pragma warning disable CA2012 // The owner state machine consumes each stored read once after notification.
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
                    _sendLength = segment.Length;
                    _sending = true;
                    unsafe
                    {
                        SubmitSendCore(segment, (nint)_pin.Pointer, _hasRead && _read.IsCompleted && !_read.IsCanceled && _remaining.IsEmpty);
                    }
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
            _input.End(error);
            FinishOutput(error);
        }
        finally
        {
            _pumping = false;
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

    private void FinishRequest(SendRequest request, Exception? error)
    {
        request.Registration.Dispose();
        request.Owner?.Dispose();
        Interlocked.Add(ref _queuedBytes, -request.Buffer.Length);
        if (request.CancellationToken.IsCancellationRequested)
        {
            request.Completion?.TrySetCanceled(request.CancellationToken);
        }
        else if (error is null)
        {
            request.Completion?.TrySetResult();
        }
        else
        {
            request.Completion?.TrySetException(error);
        }
    }

    private void FinishOutput(Exception? error, bool gracefulTransportClose = false)
    {
        lock (_admissionGate)
        {
            if (_stopped)
            {
                return;
            }
            Volatile.Write(ref _stopped, true);
        }
        if (_hasRead)
        {
            _output.Reader.AdvanceTo(_read.Buffer.End);
            _hasRead = false;
        }
        if (_request is not null)
        {
            FinishRequest(_request, error ?? new IOException("Queued output closed."));
            _request = null;
        }
        while (_requests is { } requests && requests.TryDequeue(out var queued))
        {
            FinishRequest(queued, error ?? new IOException("Queued output closed."));
        }
        // A graceful transport close is not a writer fault: like stock Sockets, the writer observes FlushResult.IsCompleted instead of an exception.
        _output.Reader.Complete(gracefulTransportClose ? null : error);
        _outputStopped.TrySetResult();
        if (error is null)
        {
            CloseCore();
        }
        else
        {
            AbortCore(error);
        }
    }

    internal void ReceivePageReleased()
    {
        if (Interlocked.Decrement(ref _receivePages) == 0 && Volatile.Read(ref _disposingStarted) != 0)
        {
            Volatile.Read(ref _receiveReleased)?.TrySetResult();
        }
    }

    private sealed class SendRequest(ReadOnlySequence<byte> buffer, IDisposable? owner, CancellationToken cancellationToken, bool observed)
    {
        internal ReadOnlySequence<byte> Buffer { get; } = buffer;
        internal IDisposable? Owner { get; } = owner;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal TaskCompletionSource? Completion { get; } = observed ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        internal CancellationTokenRegistration Registration;
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }

    private sealed class ConnectionStream(Stream input, Stream output) : Stream
    {
        public override bool CanRead => input.CanRead;
        public override bool CanWrite => output.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => input.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => input.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => output.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => output.WriteAsync(buffer, offset, count, cancellationToken);
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                input.Dispose();
                output.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await input.DisposeAsync().ConfigureAwait(false);
            await output.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
