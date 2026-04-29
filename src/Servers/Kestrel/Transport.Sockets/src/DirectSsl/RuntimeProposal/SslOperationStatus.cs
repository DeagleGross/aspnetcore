// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0073, IDE0011, IDE0005, CA1822

namespace System.Net.Security;

// PROTOTYPE — would live in dotnet/runtime as System.Net.Security.SslOperationStatus

/// <summary>
/// Status returned from non-blocking OpenSSL operations on
/// <see cref="SafeOpenSslHandle"/>. Mirrors the OpenSSL <c>SSL_get_error()</c>
/// classification but only exposes the values the caller needs to drive the loop.
/// Anything else (TLS protocol errors, certificate failures, system errors) is
/// surfaced via <see cref="OpenSslException"/>.
/// </summary>
internal enum SslOperationStatus
{
    /// <summary>
    /// Operation completed. For Read: <c>bytesRead</c> bytes were produced, or
    /// <c>bytesRead == 0</c> means the peer sent <c>close_notify</c> (clean EOF).
    /// </summary>
    Complete = 0,

    /// <summary>
    /// OpenSSL needs to read from the underlying socket before it can make
    /// progress. Wait for socket-readable, then call the same method again.
    /// </summary>
    WantRead = 1,

    /// <summary>
    /// OpenSSL needs to write to the underlying socket before it can make
    /// progress. Wait for socket-writable, then call the same method again.
    /// </summary>
    WantWrite = 2,

    /// <summary>
    /// Underlying socket is gone (RST, unexpected EOF before close_notify).
    /// Caller should dispose the <see cref="SafeOpenSslHandle"/>.
    /// </summary>
    Closed = 3,
}
