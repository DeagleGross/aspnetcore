// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

/// <summary>
/// A stream that owns a connection and reads and writes through its duplex pipe.
/// A consumer that writes a large message before it reads (HTTP/1.1 request bodies) can deadlock against a peer that echoes
/// if the receive side stops once a few pages are leased and unconsumed. A native client connection bounds its own receive
/// window by bytes, so it needs no read-ahead and reads straight from the pipe. For any other connection, an optional
/// read-ahead buffer drains the pipe into a bounded local pipe and plays the role the kernel receive buffer plays for a socket.
/// </summary>
internal sealed class ConnectionContextStream : Stream
{
    private readonly ConnectionContext _connection;
    private readonly PipeReader _input;
    private readonly PipeWriter _output;
    private readonly Pipe? _readAhead;
    private readonly Task _pump = Task.CompletedTask;
    private int _disposed;

    internal ConnectionContextStream(ConnectionContext connection, int receiveBufferBytes = 0)
    {
        _connection = connection;
        _output = connection.Transport.Output;
        if (receiveBufferBytes <= 0)
        {
            _input = connection.Transport.Input;
            return;
        }
        _readAhead = new Pipe(new PipeOptions(pauseWriterThreshold: receiveBufferBytes, resumeWriterThreshold: receiveBufferBytes / 2, useSynchronizationContext: false));
        _input = _readAhead.Reader;
        _pump = PumpAsync(connection.Transport.Input, _readAhead.Writer);
    }

    private static async Task PumpAsync(PipeReader source, PipeWriter destination)
    {
        Exception? error = null;
        try
        {
            while (true)
            {
                var result = await source.ReadAsync().ConfigureAwait(false);
                var sequence = result.Buffer;
                foreach (var segment in sequence)
                {
                    destination.Write(segment.Span);
                }
                source.AdvanceTo(sequence.End);
                var flush = await destination.FlushAsync().ConfigureAwait(false);
                if (result.IsCompleted || result.IsCanceled || flush.IsCompleted || flush.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        await destination.CompleteAsync(error).ConfigureAwait(false);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var sequence = result.Buffer;
            if (!sequence.IsEmpty && !buffer.IsEmpty)
            {
                var count = (int)Math.Min(sequence.Length, buffer.Length);
                sequence.Slice(0, count).CopyTo(buffer.Span);
                _input.AdvanceTo(sequence.GetPosition(count));
                return count;
            }
            if (buffer.IsEmpty)
            {
                // A zero-length read only waits for data. Leave the bytes unexamined so the read that follows returns them instead of waiting for more.
                _input.AdvanceTo(sequence.Start);
                return 0;
            }
            _input.AdvanceTo(sequence.Start, sequence.End);
            if (result.IsCompleted || result.IsCanceled)
            {
                return 0;
            }
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var result = await _output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (result.IsCanceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await StopReadAheadAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            StopReadAheadAsync().GetAwaiter().GetResult();
            _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    private async Task StopReadAheadAsync()
    {
        if (_readAhead is null)
        {
            return;
        }
        await _readAhead.Reader.CompleteAsync().ConfigureAwait(false);
        _connection.Transport.Input.CancelPendingRead();
        await _pump.ConfigureAwait(false);
    }
}
