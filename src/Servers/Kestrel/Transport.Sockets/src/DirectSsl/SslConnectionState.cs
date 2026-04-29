// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Uncomment the following line to enable debug counters for SSL diagnostics
// #define DIRECTSSL_DEBUG_COUNTERS

using System.Net.Security;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl;

// ─────────────────────────────────────────────────────────────────────────────
// Per-connection state machine driving SafeOpenSslHandle.
//
// Compared to the pre-runtime-API version, this file shrinks substantially:
//   * No DoSslRead / DoSslWrite helpers — Ssl.Read / Ssl.Write live on the
//     SafeOpenSslHandle now.
//   * No SSL_get_error switch with 6 OpenSSL-ABI error codes per call site —
//     we get a 4-value SslOperationStatus instead.
//   * No errno capture, no SYSCALL/EAGAIN/ECONNRESET disambiguation, no
//     ERR_clear_error / ERR_peek_error plumbing — the runtime does all of it.
//   * No manual SSL_shutdown / SSL_free / close(fd) in Dispose — disposing
//     the SafeOpenSslHandle does SSL_shutdown + SSL_free + DangerousRelease
//     on the SafeSocketHandle; disposing the SafeSocketHandle does close(fd).
// ─────────────────────────────────────────────────────────────────────────────
internal sealed class SslConnectionState : IDisposable
{
    private readonly ILogger? _logger;

    public readonly int Fd;
    public readonly SafeOpenSslHandle Ssl;
    public readonly SafeSocketHandle Socket;

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
    private bool _readWantsWrite;  // SSL_read returned WantWrite (renegotiation)

    // Write - reusable awaitable to avoid TCS allocations
    private readonly SslAwaitable<int> _writeAwaitable = new();
    private ReadOnlyMemory<byte> _writeBuffer;
    private bool _writeWantsRead;  // SSL_write returned WantRead (renegotiation)

    public SslConnectionState(int fd, SafeOpenSslHandle ssl, SafeSocketHandle socket, ILogger? logger = null)
    {
        _logger = logger;

        Fd = fd;
        Ssl = ssl;
        Socket = socket;
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
        SslOperationStatus status;
        try
        {
            status = Ssl.Handshake();
        }
        catch (OpenSslException ex)
        {
            return ValueTask.FromException(ex);
        }

        switch (status)
        {
            case SslOperationStatus.Complete:
                IsHandshaked = true;
                return ValueTask.CompletedTask;

            case SslOperationStatus.WantRead:
            case SslOperationStatus.WantWrite:
                var valueTask = _handshakeAwaitable.Reset();
                return new ValueTask(valueTask.AsTask());

            case SslOperationStatus.Closed:
            default:
                return ValueTask.FromException(new SslException("Handshake closed by peer"));
        }
    }

    private void ContinueHandshake()
    {
        SslOperationStatus status;
        try
        {
            status = Ssl.Handshake();
        }
        catch (OpenSslException ex)
        {
            _handshakeAwaitable.TrySetException(ex);
            return;
        }

        switch (status)
        {
            case SslOperationStatus.Complete:
                IsHandshaked = true;
                _handshakeAwaitable.TrySetResult(true);
                return;

            case SslOperationStatus.WantRead:
            case SslOperationStatus.WantWrite:
                // Keep waiting
                return;

            case SslOperationStatus.Closed:
            default:
                _handshakeAwaitable.TrySetException(new SslException("Handshake closed by peer"));
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

        SslOperationStatus status;
        int n;
        try
        {
            status = Ssl.Read(buffer.Span, out n);
        }
        catch (OpenSslException ex)
        {
            return ValueTask.FromException<int>(ex);
        }

        switch (status)
        {
            case SslOperationStatus.Complete:
                // n > 0: data; n == 0: clean EOF
                return new ValueTask<int>(n);

            case SslOperationStatus.WantRead:
                _readBuffer = buffer;
                _readWantsWrite = false;
                return _readAwaitable.Reset();

            case SslOperationStatus.WantWrite:
                // SSL_read needs to write (TLS renegotiation or post-handshake auth)
                _readBuffer = buffer;
                _readWantsWrite = true;
                Pump?.ModifyEvents(Socket, Fd, EpollEvents.Read | EpollEvents.Write);
                return _readAwaitable.Reset();

            case SslOperationStatus.Closed:
            default:
                return new ValueTask<int>(0); // EOF
        }
    }

    private void TryCompleteRead()
    {
        if (!_readAwaitable.IsActive)
        {
            _logger?.LogDebug("TryCompleteRead called but no read is pending");
            return;
        }

        SslOperationStatus status;
        int n;
        try
        {
            status = Ssl.Read(_readBuffer.Span, out n);
        }
        catch (OpenSslException ex)
        {
            _readBuffer = default;
            _readWantsWrite = false;
            _readAwaitable.TrySetException(ex);
            return;
        }

        switch (status)
        {
            case SslOperationStatus.Complete:
                {
                    var wasWaitingForWrite = _readWantsWrite;
                    _readBuffer = default;
                    _readWantsWrite = false;

                    if (wasWaitingForWrite)
                    {
                        Pump?.ModifyEvents(Socket, Fd, EpollEvents.Read);
                    }

                    _readAwaitable.TrySetResult(n);
                    return;
                }

            case SslOperationStatus.WantRead:
                if (_readWantsWrite)
                {
                    _readWantsWrite = false;
                    Pump?.ModifyEvents(Socket, Fd, EpollEvents.Read);
                }
                return;

            case SslOperationStatus.WantWrite:
                if (!_readWantsWrite)
                {
                    _readWantsWrite = true;
                    Pump?.ModifyEvents(Socket, Fd, EpollEvents.Read | EpollEvents.Write);
                }
                return;

            case SslOperationStatus.Closed:
            default:
                _readBuffer = default;
                _readWantsWrite = false;
                _readAwaitable.TrySetResult(0);
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

        SslOperationStatus status;
        int n;
        try
        {
            status = Ssl.Write(buffer.Span, out n);
        }
        catch (OpenSslException ex)
        {
            return ValueTask.FromException<int>(ex);
        }

        switch (status)
        {
            case SslOperationStatus.Complete:
                return new ValueTask<int>(n);

            case SslOperationStatus.WantWrite:
                _writeBuffer = buffer;
                _writeWantsRead = false;
                Pump?.ModifyEvents(Socket, Fd, EpollEvents.Read | EpollEvents.Write);
                return _writeAwaitable.Reset();

            case SslOperationStatus.WantRead:
                // SSL_write needs to read (TLS renegotiation or post-handshake auth)
                _writeBuffer = buffer;
                _writeWantsRead = true;
                // EPOLLIN already registered
                return _writeAwaitable.Reset();

            case SslOperationStatus.Closed:
            default:
                return new ValueTask<int>(0);
        }
    }

    private void TryCompleteWrite()
    {
        if (!_writeAwaitable.IsActive)
        {
            // Spurious EPOLLOUT - remove it to avoid future wakeups
            Pump?.ModifyEvents(Socket, Fd, EpollEvents.Read);
            return;
        }

        SslOperationStatus status;
        int n;
        try
        {
            status = Ssl.Write(_writeBuffer.Span, out n);
        }
        catch (OpenSslException ex)
        {
            _writeBuffer = default;
            _writeWantsRead = false;
            Pump?.ModifyEvents(Socket, Fd, EpollEvents.Read);
            _writeAwaitable.TrySetException(ex);
            return;
        }

        switch (status)
        {
            case SslOperationStatus.Complete:
                {
                    var wasWaitingForRead = _writeWantsRead;
                    _writeBuffer = default;
                    _writeWantsRead = false;

                    if (!wasWaitingForRead)
                    {
                        Pump?.ModifyEvents(Socket, Fd, EpollEvents.Read);
                    }

                    _writeAwaitable.TrySetResult(n);
                    return;
                }

            case SslOperationStatus.WantWrite:
                if (_writeWantsRead)
                {
                    _writeWantsRead = false;
                    Pump?.ModifyEvents(Socket, Fd, EpollEvents.Read | EpollEvents.Write);
                }
                return;

            case SslOperationStatus.WantRead:
                if (!_writeWantsRead)
                {
                    _writeWantsRead = true;
                    Pump?.ModifyEvents(Socket, Fd, EpollEvents.Read);
                }
                return;

            case SslOperationStatus.Closed:
            default:
                _writeBuffer = default;
                _writeWantsRead = false;
                Pump?.ModifyEvents(Socket, Fd, EpollEvents.Read);
                _writeAwaitable.TrySetResult(0);
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
    // DISPOSE
    // ═══════════════════════════════════════════════════════════════
    //
    // BEFORE: ERR_clear_error + SSL_set_quiet_shutdown(1) + SSL_shutdown +
    //         SSL_free + manual close(fd), all open-coded with try/catch wrappers.
    // AFTER:  Dispose the SafeOpenSslHandle (does quiet-shutdown + SSL_free +
    //         DangerousRelease on the socket ref) and the SafeSocketHandle
    //         (does close(fd)). Both are SafeHandles — exceptions during one
    //         don't leak the other.
    public void Dispose()
    {
        Ssl.SetQuietShutdown(true);
        Ssl.Dispose();
    }
}
