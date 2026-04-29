// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0073, IDE0011, IDE0005, CA1822

using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace System.Net.Security;

// PROTOTYPE — would live in dotnet/runtime as System.Net.Security.SafeOpenSslHandle
//
// Wraps OpenSSL SSL* bound to a socket fd via SSL_set_fd.
// Created by SafeOpenSslHandle.CreateForSocket(ctx, socket, isServer).
// Holds the SafeSocketHandle via DangerousAddRef so the fd cannot be invalidated under OpenSSL.

internal sealed class SafeOpenSslHandle : SafeHandle
{
    public static bool IsSupported => OperatingSystem.IsLinux();

    private SafeSocketHandle? _socket;
    private bool _socketRefAdded;
    private int _lastErrno;

    public SafeOpenSslHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public SafeOpenSslHandle(IntPtr handle, bool ownsHandle) : base(IntPtr.Zero, ownsHandle)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
        {
            try
            {
                Native.SSL_set_quiet_shutdown(handle, 1);
                Native.SSL_shutdown(handle);
            }
            catch
            {
                // best-effort
            }

            Native.SSL_free(handle);
            SetHandle(IntPtr.Zero);
        }

        if (_socketRefAdded && _socket is not null)
        {
            _socket.DangerousRelease();
            _socketRefAdded = false;
        }

        return true;
    }

    /// <summary>SSL_new(ctx) + SSL_set_fd(ssl, socket) + SSL_set_accept_state / SSL_set_connect_state.</summary>
    public static SafeOpenSslHandle CreateForSocket(
        SafeOpenSslContextHandle context,
        SafeSocketHandle socket,
        bool isServer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(socket);

        IntPtr ssl = Native.SSL_new(context.DangerousHandle);
        if (ssl == IntPtr.Zero)
        {
            throw new OpenSslException("SSL_new failed", Native.GetErrorString());
        }

        var safe = new SafeOpenSslHandle(ssl, ownsHandle: true);

        bool addedRef = false;
        try
        {
            socket.DangerousAddRef(ref addedRef);
            int fd = (int)socket.DangerousGetHandle();
            if (Native.SSL_set_fd(ssl, fd) != 1)
            {
                throw new OpenSslException("SSL_set_fd failed", Native.GetErrorString());
            }
        }
        catch
        {
            if (addedRef) socket.DangerousRelease();
            safe.Dispose();
            throw;
        }

        safe._socket = socket;
        safe._socketRefAdded = addedRef;

        if (isServer)
        {
            Native.SSL_set_accept_state(ssl);
        }
        else
        {
            throw new NotImplementedException("PROTOTYPE: client mode not wired.");
        }

        return safe;
    }

    /// <summary>SSL_set_tlsext_host_name (client SNI). Set on a client handle before the first Handshake().</summary>
    public void SetTargetHostName(string targetHost)
    {
        _ = targetHost;
        _ = handle;
        throw new NotImplementedException("PROTOTYPE: client SNI not wired.");
    }

    /// <summary>SSL_set_quiet_shutdown(1) — skip waiting for peer close_notify.</summary>
    public void SetQuietShutdown(bool enabled) => Native.SSL_set_quiet_shutdown(handle, enabled ? 1 : 0);

    /// <summary>SSL_do_handshake + SSL_get_error.</summary>
    public SslOperationStatus Handshake()
    {
        Native.ERR_clear_error();
        int n = Native.SSL_do_handshake(handle);
        if (n == 1) return SslOperationStatus.Complete;

        int err = Native.SSL_get_error(handle, n);
        switch (err)
        {
            case Native.SSL_ERROR_WANT_READ:    return SslOperationStatus.WantRead;
            case Native.SSL_ERROR_WANT_WRITE:   return SslOperationStatus.WantWrite;
            case Native.SSL_ERROR_ZERO_RETURN:  return SslOperationStatus.Closed;
            case Native.SSL_ERROR_SYSCALL:      return SslOperationStatus.Closed;
            default:
                throw new OpenSslException($"SSL_do_handshake failed (err={err})", Native.GetErrorString());
        }
    }

    /// <summary>SSL_read into the supplied span.</summary>
    public unsafe SslOperationStatus Read(Span<byte> buffer, out int bytesRead)
    {
        Native.ERR_clear_error();
        int n;
        fixed (byte* ptr = buffer)
        {
            n = Native.SSL_read(handle, ptr, buffer.Length);
            _lastErrno = Marshal.GetLastWin32Error();
        }

        if (n > 0)
        {
            bytesRead = n;
            return SslOperationStatus.Complete;
        }

        bytesRead = 0;

        int err = Native.SSL_get_error(handle, n);
        switch (err)
        {
            case Native.SSL_ERROR_WANT_READ:    return SslOperationStatus.WantRead;
            case Native.SSL_ERROR_WANT_WRITE:   return SslOperationStatus.WantWrite;
            case Native.SSL_ERROR_ZERO_RETURN:  return SslOperationStatus.Complete;
            case Native.SSL_ERROR_SYSCALL:
                if (n == 0 || _lastErrno == 0 || _lastErrno == 104)
                {
                    return SslOperationStatus.Complete;
                }
                if (_lastErrno == 11 || _lastErrno == 115)
                {
                    return SslOperationStatus.WantRead;
                }
                throw new OpenSslException($"SSL_read syscall error (errno={_lastErrno})", Native.GetErrorString());
            default:
                throw new OpenSslException($"SSL_read failed (err={err})", Native.GetErrorString());
        }
    }

    /// <summary>SSL_write from the supplied span.</summary>
    public unsafe SslOperationStatus Write(ReadOnlySpan<byte> buffer, out int bytesWritten)
    {
        Native.ERR_clear_error();
        int n;
        fixed (byte* ptr = buffer)
        {
            n = Native.SSL_write(handle, (byte*)ptr, buffer.Length);
        }

        if (n > 0)
        {
            bytesWritten = n;
            return SslOperationStatus.Complete;
        }

        bytesWritten = 0;

        int err = Native.SSL_get_error(handle, n);
        switch (err)
        {
            case Native.SSL_ERROR_WANT_WRITE:   return SslOperationStatus.WantWrite;
            case Native.SSL_ERROR_WANT_READ:    return SslOperationStatus.WantRead;
            case Native.SSL_ERROR_ZERO_RETURN:  return SslOperationStatus.Closed;
            case Native.SSL_ERROR_SYSCALL:
                if (Native.ERR_peek_error() == 0)
                {
                    return SslOperationStatus.Closed;
                }
                throw new OpenSslException("SSL_write syscall error", Native.GetErrorString());
            default:
                throw new OpenSslException($"SSL_write failed (err={err})", Native.GetErrorString());
        }
    }

    /// <summary>SSL_shutdown.</summary>
    public SslOperationStatus Shutdown()
    {
        int n = Native.SSL_shutdown(handle);
        if (n >= 1) return SslOperationStatus.Complete;
        if (n == 0) return SslOperationStatus.WantRead;

        int err = Native.SSL_get_error(handle, n);
        return err switch
        {
            Native.SSL_ERROR_WANT_READ  => SslOperationStatus.WantRead,
            Native.SSL_ERROR_WANT_WRITE => SslOperationStatus.WantWrite,
            _                           => SslOperationStatus.Closed,
        };
    }

    // PROTOTYPE: negotiated info getters not wired (not used by the prototype consumer).
    public SslProtocols NegotiatedSslProtocol { get { _ = handle; return SslProtocols.None; } }
    public TlsCipherSuite NegotiatedCipherSuite { get { _ = handle; return default; } }
    public SslApplicationProtocol NegotiatedApplicationProtocol { get { _ = handle; return default; } }
    public string? TargetHostName { get { _ = handle; return null; } }
    public bool SessionResumed { get { _ = handle; return false; } }
    public X509Certificate2? GetRemoteCertificate() { _ = handle; return null; }
}
