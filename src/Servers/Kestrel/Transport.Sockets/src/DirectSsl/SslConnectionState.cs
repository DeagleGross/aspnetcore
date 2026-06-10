// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Uncomment the following line to enable debug counters for SSL diagnostics
// #define DIRECTSSL_DEBUG_COUNTERS

using System.Net.Security;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl;

internal sealed class SslConnectionState : IDisposable
{
    private readonly ILogger? _logger;

    public readonly int Fd;

    // Socket-bound TlsSession from System.Net.Security. Drives ciphertext I/O via
    // SSL_set_fd on Linux/OpenSSL; owns the SafeSocketHandle and disposes it.
    public readonly TlsSession Session;

    // Reference to pump for dynamic event modification
    internal SslEventPump? Pump { get; set; }

    // Callback for fatal errors (e.g., peer disconnect) - allows owner to trigger disposal
    internal Action<Exception>? OnFatalError { get; set; }

    // Handshake - reusable awaitable to avoid TCS allocations
    private readonly SslAwaitable<bool> _handshakeAwaitable = new();
    public bool IsHandshaked { get; private set; }

    // Read - reusable awaitable to avoid TCS allocations
    private readonly SslAwaitable<int> _readAwaitable = new();
    private Memory<byte> _readBuffer;
    private bool _readWantsWrite;  // TlsSession.Read returned WantWrite (renegotiation)

    // Write - reusable awaitable to avoid TCS allocations
    private readonly SslAwaitable<int> _writeAwaitable = new();
    private ReadOnlyMemory<byte> _writeBuffer;
    private bool _writeWantsRead;  // TlsSession.Write returned WantRead (renegotiation)

    public SslConnectionState(int fd, TlsSession session, ILogger? logger = null)
    {
        _logger = logger;

        Fd = fd;
        Session = session;
    }

    /// <summary>
    /// Mark handshake as complete (used when handshake was done externally by pump).
    /// </summary>
    internal void SetHandshakeComplete()
    {
        IsHandshaked = true;
    }

    // ═══════════════════════════════════════════════════════════════
    // HANDSHAKE
    // ═══════════════════════════════════════════════════════════════

    public ValueTask HandshakeAsync()
    {
        TlsOperationStatus status;
        try
        {
            status = Session.Handshake();
        }
        catch (Exception ex)
        {
            return ValueTask.FromException(new SslException($"Handshake failed: {ex.Message}"));
        }

        switch (status)
        {
            case TlsOperationStatus.Complete:
                IsHandshaked = true;
                return ValueTask.CompletedTask;

            case TlsOperationStatus.WantRead:
            case TlsOperationStatus.WantWrite:
                // Use pooled awaitable instead of allocating new TCS
                var valueTask = _handshakeAwaitable.Reset();
                return new ValueTask(valueTask.AsTask());

            default:
                return ValueTask.FromException(new SslException($"Handshake failed: {status}"));
        }
    }

    private void ContinueHandshake()
    {
        TlsOperationStatus status;
        try
        {
            status = Session.Handshake();
        }
        catch (Exception ex)
        {
            _handshakeAwaitable.TrySetException(new SslException($"Handshake failed: {ex.Message}"));
            return;
        }

        switch (status)
        {
            case TlsOperationStatus.Complete:
                IsHandshaked = true;
                _handshakeAwaitable.TrySetResult(true);
                return;

            case TlsOperationStatus.WantRead:
            case TlsOperationStatus.WantWrite:
                return;

            default:
                _handshakeAwaitable.TrySetException(new SslException($"Handshake failed: {status}"));
                return;
        }
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

        TlsOperationStatus status;
        int bytesRead;
        try
        {
            status = Session.Read(buffer.Span, out bytesRead);
        }
        catch (AuthenticationException)
        {
            // OpenSSL SSL_ERROR_SYSCALL (e.g. ECONNRESET, abrupt peer close without close_notify)
            // surfaces as AuthenticationException via TlsSession. Treat as EOF — the connection
            // is gone and our caller will tear down the pipeline.
            return new ValueTask<int>(0);
        }

        switch (status)
        {
            case TlsOperationStatus.Complete:
                return new ValueTask<int>(bytesRead);

            case TlsOperationStatus.Closed:
                // Peer sent close_notify or transport is gone — treat as EOF
#if DIRECTSSL_DEBUG_COUNTERS
                Interlocked.Increment(ref SslEventPump.TotalSslErrorZeroReturn);
#endif
                return new ValueTask<int>(0);

            case TlsOperationStatus.WantRead:
                _readBuffer = buffer;
                _readWantsWrite = false;
                return _readAwaitable.Reset();

            case TlsOperationStatus.WantWrite:
                // TlsSession.Read needs to write (TLS renegotiation or post-handshake auth).
                // Register for EPOLLOUT - OnWritable will call TryCompleteRead.
                _readBuffer = buffer;
                _readWantsWrite = true;
                Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN | NativeSsl.EPOLLOUT);
                return _readAwaitable.Reset();

            default:
#if DIRECTSSL_DEBUG_COUNTERS
                Interlocked.Increment(ref SslEventPump.TotalSslErrorOther);
#endif
                return ValueTask.FromException<int>(new SslException($"TlsSession.Read failed: {status}"));
        }
    }

    private void TryCompleteRead()
    {
        if (!_readAwaitable.IsActive)
        {
            _logger?.LogDebug("TryCompleteRead called but no read is pending");
            return; // Race: cancelled or completed between check and call
        }

        TlsOperationStatus status;
        int bytesRead;
        try
        {
            status = Session.Read(_readBuffer.Span, out bytesRead);
        }
        catch (AuthenticationException)
        {
            var wasWaitingForWrite = _readWantsWrite;
            _readBuffer = default;
            _readWantsWrite = false;
            if (wasWaitingForWrite)
            {
                Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
            }
            _readAwaitable.TrySetResult(0); // Treat as EOF
            return;
        }

        switch (status)
        {
            case TlsOperationStatus.Complete:
            {
                var wasWaitingForWrite = _readWantsWrite;
                _readBuffer = default;
                _readWantsWrite = false;

                // If we were waiting for write, remove EPOLLOUT now that read completed
                if (wasWaitingForWrite)
                {
                    Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
                }

                _readAwaitable.TrySetResult(bytesRead);
                return;
            }

            case TlsOperationStatus.Closed:
            {
#if DIRECTSSL_DEBUG_COUNTERS
                Interlocked.Increment(ref SslEventPump.TotalSslErrorZeroReturn);
#endif
                var wasWaitingForWrite = _readWantsWrite;
                _readBuffer = default;
                _readWantsWrite = false;

                if (wasWaitingForWrite)
                {
                    Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
                }

                _readAwaitable.TrySetResult(0);
                return;
            }

            case TlsOperationStatus.WantRead:
                // Need to wait for more data - if we were waiting for write, switch back to read
                if (_readWantsWrite)
                {
                    _readWantsWrite = false;
                    Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
                }
                return;

            case TlsOperationStatus.WantWrite:
                // Need to write - register for EPOLLOUT if not already
                if (!_readWantsWrite)
                {
                    _readWantsWrite = true;
                    Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN | NativeSsl.EPOLLOUT);
                }
                return;

            default:
#if DIRECTSSL_DEBUG_COUNTERS
                Interlocked.Increment(ref SslEventPump.TotalSslErrorOther);
#endif
                _readBuffer = default;
                _readWantsWrite = false;
                _readAwaitable.TrySetException(new SslException($"TlsSession.Read failed: {status}"));
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

        TlsOperationStatus status;
        int bytesWritten;
        try
        {
            status = Session.Write(buffer.Span, out bytesWritten);
        }
        catch (AuthenticationException)
        {
            return new ValueTask<int>(0); // Treat as broken connection → caller will tear down
        }

        switch (status)
        {
            case TlsOperationStatus.Complete:
#if DIRECTSSL_DEBUG_COUNTERS
                Interlocked.Increment(ref SslEventPump.TotalWriteImmediate);
#endif
                return new ValueTask<int>(bytesWritten);

            case TlsOperationStatus.WantWrite:
#if DIRECTSSL_DEBUG_COUNTERS
                Interlocked.Increment(ref SslEventPump.TotalWriteWouldBlock);
#endif
                _writeBuffer = buffer;
                _writeWantsRead = false;

                // Dynamically add EPOLLOUT since the write would block
                Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN | NativeSsl.EPOLLOUT);
                return _writeAwaitable.Reset();

            case TlsOperationStatus.WantRead:
                // TlsSession.Write needs to read (TLS renegotiation or post-handshake auth)
                // Stay registered for EPOLLIN - OnReadable will call TryCompleteWrite
                _writeBuffer = buffer;
                _writeWantsRead = true;
                return _writeAwaitable.Reset();

            case TlsOperationStatus.Closed:
                return new ValueTask<int>(0);

            default:
                return ValueTask.FromException<int>(new SslException($"TlsSession.Write failed: {status}"));
        }
    }

    private void TryCompleteWrite()
    {
        if (!_writeAwaitable.IsActive)
        {
            // Spurious EPOLLOUT - remove it to avoid future wakeups
            Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
            return;
        }

        TlsOperationStatus status;
        int bytesWritten;
        try
        {
            status = Session.Write(_writeBuffer.Span, out bytesWritten);
        }
        catch (AuthenticationException)
        {
            _writeBuffer = default;
            _writeWantsRead = false;
            Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
            _writeAwaitable.TrySetResult(0);
            return;
        }

        switch (status)
        {
            case TlsOperationStatus.Complete:
            {
                var wasWaitingForRead = _writeWantsRead;
                _writeBuffer = default;
                _writeWantsRead = false;

                // Write completed - remove EPOLLOUT if we had it registered
                if (!wasWaitingForRead)
                {
                    Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
                }

                _writeAwaitable.TrySetResult(bytesWritten);
                return;
            }

            case TlsOperationStatus.WantWrite:
                // Need to wait for write - if we were waiting for read, switch to write
                if (_writeWantsRead)
                {
                    _writeWantsRead = false;
                    Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN | NativeSsl.EPOLLOUT);
                }
                return;

            case TlsOperationStatus.WantRead:
                // Need to read - remove EPOLLOUT if we had it, stay on EPOLLIN
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
                _writeAwaitable.TrySetResult(0);
                return;

            default:
                _writeBuffer = default;
                _writeWantsRead = false;
                Pump?.ModifyEvents(Fd, NativeSsl.EPOLLIN);
                _writeAwaitable.TrySetException(new SslException($"TlsSession.Write failed: {status}"));
                return;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // EVENT HANDLERS (called by pump)
    // ═══════════════════════════════════════════════════════════════

    internal void OnReadable()
    {
        if (_handshakeAwaitable.IsActive)
        {
            ContinueHandshake();
            return;
        }

        // Check if a pending write was waiting for read (renegotiation)
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
        if (_handshakeAwaitable.IsActive)
        {
            ContinueHandshake();
            return;
        }

        // Check if a pending read was waiting for write (renegotiation)
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
        _handshakeAwaitable.TrySetException(ex);
        _readAwaitable.TrySetException(ex);
        _writeAwaitable.TrySetException(ex);

        // Notify owner about fatal error so it can trigger disposal
        OnFatalError?.Invoke(ex);
    }

    /// <summary>
    /// Cancel any pending async operations (read/write awaitables).
    /// Called during connection disposal to unblock waiting tasks.
    /// </summary>
    internal void Cancel()
    {
        _handshakeAwaitable.TrySetCanceled();
        _readAwaitable.TrySetCanceled();
        _writeAwaitable.TrySetCanceled();
    }

    // ═══════════════════════════════════════════════════════════════
    // DISPOSAL
    // ═══════════════════════════════════════════════════════════════

    public void Dispose()
    {
        // TlsSession owns the SafeSocketHandle when created via
        // TlsSession.Create(ctx, socketHandle), so Dispose() will:
        //   1. Issue SSL_shutdown (clean close_notify)
        //   2. SSL_free the underlying handle
        //   3. Close the file descriptor
        // We do NOT need to call NativeSsl.close(Fd) — that would be a double-close.
        Session.Dispose();
    }
}
