// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Security;
using Microsoft.Extensions.Logging;

#pragma warning disable SYSLIB5007 // TlsSocketSession/TlsOperationStatus are experimental.

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl;

/// <summary>
/// Per-connection state for an established (post-handshake) DirectSsl connection.
/// Drives non-blocking application reads/writes over a <see cref="TlsSocketSession"/>,
/// mapping <see cref="TlsOperationStatus"/> to epoll readiness the pump can wait on.
/// The handshake is completed by <see cref="SslEventPump"/> before this state is created.
/// </summary>
internal sealed class SslConnectionState : IDisposable
{
    private readonly ILogger? _logger;

    public readonly int Fd;
    public readonly TlsSocketSession Session;

    // Reference to pump for dynamic event modification
    internal SslEventPump? Pump { get; set; }

    // Callback for fatal errors (e.g., peer disconnect) - allows owner to trigger disposal
    internal Action<Exception>? OnFatalError { get; set; }

    public bool IsHandshaked { get; private set; }

    // Read - reusable awaitable to avoid TCS allocations
    private readonly SslAwaitable<int> _readAwaitable = new();
    private Memory<byte> _readBuffer;
    private bool _readWantsWrite;  // Read needs the socket to become writable (renegotiation)

    // Write - reusable awaitable to avoid TCS allocations
    private readonly SslAwaitable<int> _writeAwaitable = new();
    private ReadOnlyMemory<byte> _writeBuffer;  // Remaining (unwritten) application bytes
    private int _writeTotal;                     // Original request length to report on completion
    private bool _writeWantsRead;                // Write needs the socket to become readable (renegotiation)

    public SslConnectionState(int fd, TlsSocketSession session, ILogger? logger = null)
    {
        _logger = logger;

        Fd = fd;
        Session = session;
    }

    /// <summary>
    /// Mark handshake as complete (the handshake is performed by the pump before this state exists).
    /// </summary>
    internal void SetHandshakeComplete()
    {
        IsHandshaked = true;
    }

    // ═══════════════════════════════════════════════════════════════
    // READ
    // ═══════════════════════════════════════════════════════════════

    public ValueTask<int> ReadAsync(Memory<byte> buffer)
    {
        if (!IsHandshaked)
        {
            throw new InvalidOperationException("Handshake not complete");
        }

        if (_readAwaitable.IsActive)
        {
            throw new InvalidOperationException("Read already pending");
        }

        TlsOperationStatus status = Session.Read(buffer.Span, out int read);

        switch (status)
        {
            case TlsOperationStatus.Complete:
                return new ValueTask<int>(read);

            case TlsOperationStatus.NeedMoreData:
                _readBuffer = buffer;
                _readWantsWrite = false;
                return _readAwaitable.Reset();

            case TlsOperationStatus.DestinationTooSmall:
                // Renegotiation: the read needs to send handshake output. Wait for writable.
                _readBuffer = buffer;
                _readWantsWrite = true;
                Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN | NativeSsl.EPOLLOUT);
                return _readAwaitable.Reset();

            case TlsOperationStatus.Closed:
                return new ValueTask<int>(0); // EOF

            default:
                return ValueTask.FromException<int>(new SslException($"TLS read failed: {status}"));
        }
    }

    private void TryCompleteRead()
    {
        if (!_readAwaitable.IsActive)
        {
            _logger?.LogDebug("TryCompleteRead called but no read is pending");
            return; // Race: cancelled or completed between check and call
        }

        TlsOperationStatus status = Session.Read(_readBuffer.Span, out int read);

        switch (status)
        {
            case TlsOperationStatus.Complete:
            {
                var wasWaitingForWrite = _readWantsWrite;
                _readBuffer = default;
                _readWantsWrite = false;

                if (wasWaitingForWrite)
                {
                    Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
                }

                _readAwaitable.TrySetResult(read);
                return;
            }

            case TlsOperationStatus.NeedMoreData:
                // Still need more ciphertext - if we were waiting for write, switch back to read.
                if (_readWantsWrite)
                {
                    _readWantsWrite = false;
                    Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
                }
                return;

            case TlsOperationStatus.DestinationTooSmall:
                // Renegotiation: need to write - register for EPOLLOUT if not already.
                if (!_readWantsWrite)
                {
                    _readWantsWrite = true;
                    Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN | NativeSsl.EPOLLOUT);
                }
                return;

            case TlsOperationStatus.Closed:
                _readBuffer = default;
                _readWantsWrite = false;
                _readAwaitable.TrySetResult(0); // EOF
                return;

            default:
                _readBuffer = default;
                _readWantsWrite = false;
                _readAwaitable.TrySetException(new SslException($"TLS read failed: {status}"));
                return;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // WRITE
    // ═══════════════════════════════════════════════════════════════

    public ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer)
    {
        if (!IsHandshaked)
        {
            throw new InvalidOperationException("Handshake not complete");
        }

        if (_writeAwaitable.IsActive)
        {
            throw new InvalidOperationException("Write already pending");
        }

        _writeBuffer = buffer;
        _writeTotal = buffer.Length;
        _writeWantsRead = false;

        TlsOperationStatus status = Session.Write(_writeBuffer.Span, out int written);

        switch (status)
        {
            case TlsOperationStatus.Complete:
                _writeBuffer = default;
                return new ValueTask<int>(_writeTotal);

            case TlsOperationStatus.DestinationTooSmall:
                // Socket WouldBlock mid-write. 'written' plaintext bytes were consumed;
                // retry the remainder once the socket is writable.
                _writeBuffer = _writeBuffer.Slice(written);
                Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN | NativeSsl.EPOLLOUT);
                return _writeAwaitable.Reset();

            case TlsOperationStatus.NeedMoreData:
                // Renegotiation: the write needs to read peer ciphertext first.
                _writeBuffer = _writeBuffer.Slice(written);
                _writeWantsRead = true;
                // EPOLLIN is already registered.
                return _writeAwaitable.Reset();

            case TlsOperationStatus.Closed:
                _writeBuffer = default;
                return new ValueTask<int>(0); // EOF

            default:
                _writeBuffer = default;
                return ValueTask.FromException<int>(new SslException($"TLS write failed: {status}"));
        }
    }

    private void TryCompleteWrite()
    {
        if (!_writeAwaitable.IsActive)
        {
            // Spurious EPOLLOUT - remove it to avoid future wakeups.
            Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
            return;
        }

        TlsOperationStatus status = Session.Write(_writeBuffer.Span, out int written);

        switch (status)
        {
            case TlsOperationStatus.Complete:
                _writeBuffer = default;
                _writeWantsRead = false;
                Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
                _writeAwaitable.TrySetResult(_writeTotal);
                return;

            case TlsOperationStatus.DestinationTooSmall:
                // Still WouldBlock. Advance past what was written and keep waiting for writable.
                _writeBuffer = _writeBuffer.Slice(written);
                if (_writeWantsRead)
                {
                    _writeWantsRead = false;
                    Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN | NativeSsl.EPOLLOUT);
                }
                return;

            case TlsOperationStatus.NeedMoreData:
                // Renegotiation: need to read - drop EPOLLOUT, stay on EPOLLIN.
                _writeBuffer = _writeBuffer.Slice(written);
                if (!_writeWantsRead)
                {
                    _writeWantsRead = true;
                    Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
                }
                return;

            case TlsOperationStatus.Closed:
                _writeBuffer = default;
                _writeWantsRead = false;
                Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
                _writeAwaitable.TrySetResult(0); // EOF
                return;

            default:
                _writeBuffer = default;
                _writeWantsRead = false;
                Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
                _writeAwaitable.TrySetException(new SslException($"TLS write failed: {status}"));
                return;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // EVENT HANDLERS (called by pump)
    // ═══════════════════════════════════════════════════════════════

    internal void OnReadable()
    {
        // A pending write waiting for read (renegotiation) takes priority.
        if (_writeWantsRead && _writeAwaitable.IsActive)
        {
            TryCompleteWrite();
            return;
        }

        if (_readAwaitable.IsActive)
        {
            TryCompleteRead();
        }
    }

    internal void OnWritable()
    {
        // A pending read waiting for write (renegotiation) takes priority.
        if (_readWantsWrite && _readAwaitable.IsActive)
        {
            TryCompleteRead();
            return;
        }

        if (_writeAwaitable.IsActive)
        {
            TryCompleteWrite();
        }
    }

    internal void OnError(Exception ex)
    {
        _readAwaitable.TrySetException(ex);
        _writeAwaitable.TrySetException(ex);

        // Notify owner about fatal error so it can trigger disposal.
        OnFatalError?.Invoke(ex);
    }

    /// <summary>
    /// Cancel any pending async operations (read/write awaitables).
    /// Called during connection disposal to unblock waiting tasks.
    /// </summary>
    internal void Cancel()
    {
        _readAwaitable.TrySetCanceled();
        _writeAwaitable.TrySetCanceled();
    }

    public void Dispose()
    {
        // Send close_notify (best-effort) then dispose the session, which closes the socket fd.
        Session.Shutdown();
        Session.Dispose();
    }
}
