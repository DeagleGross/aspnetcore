// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0073, IDE0011, IDE0005, CA1822, IDE0044

using System.Runtime.InteropServices;

namespace System.Net.Security;

// PROTOTYPE — represents the runtime-internal interop layer.
// In the real ship, these would live as `internal static partial class Interop.Ssl`
// in `Common/src/Interop/Unix/System.Security.Cryptography.Native/Interop.Ssl.cs`
// (the existing file used by SslStream — most of these P/Invokes are already there).
//
// The public SafeOpenSslContextHandle / SafeOpenSslHandle types in this folder
// call into Native.* the same way SslStream's internals call Interop.Ssl.*.
//
// The ONLY new P/Invoke this proposal needs internally is SSL_set_fd —
// every other call here already exists in the runtime as `Interop.Ssl.Ssl*`.

internal static unsafe partial class Native
{
    private const string LibSsl = "libssl.so.3";
    private const string LibCrypto = "libcrypto.so.3";

    // ── SSL_CTX lifetime ───────────────────────────────────────────────────
    [LibraryImport(LibSsl)] public static partial IntPtr TLS_server_method();
    [LibraryImport(LibSsl)] public static partial IntPtr SSL_CTX_new(IntPtr method);
    [LibraryImport(LibSsl)] public static partial void SSL_CTX_free(IntPtr ctx);

    // ── Cert / key (file-loading variant — runtime impl uses in-memory variant) ──
    [LibraryImport(LibSsl, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SSL_CTX_use_certificate_file(IntPtr ctx, string file, int type);
    [LibraryImport(LibSsl, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SSL_CTX_use_PrivateKey_file(IntPtr ctx, string file, int type);
    [LibraryImport(LibSsl)] public static partial int SSL_CTX_check_private_key(IntPtr ctx);

    public const int SSL_FILETYPE_PEM = 1;

    // ── SSL_CTX session cache (via SSL_CTX_ctrl macro pattern) ─────────────
    [LibraryImport(LibSsl)] public static partial long SSL_CTX_set_timeout(IntPtr ctx, long t);
    [LibraryImport(LibSsl)] private static partial long SSL_CTX_ctrl(IntPtr ctx, int cmd, long larg, IntPtr parg);

    private const int SSL_CTRL_SET_SESS_CACHE_SIZE = 42;
    private const int SSL_CTRL_SET_SESS_CACHE_MODE = 44;

    public const int SSL_SESS_CACHE_OFF    = 0x0000;
    public const int SSL_SESS_CACHE_CLIENT = 0x0001;
    public const int SSL_SESS_CACHE_SERVER = 0x0002;
    public const int SSL_SESS_CACHE_BOTH   = SSL_SESS_CACHE_CLIENT | SSL_SESS_CACHE_SERVER;

    public static long SSL_CTX_set_session_cache_mode(IntPtr ctx, int mode)
        => SSL_CTX_ctrl(ctx, SSL_CTRL_SET_SESS_CACHE_MODE, mode, IntPtr.Zero);
    public static long SSL_CTX_sess_set_cache_size(IntPtr ctx, long size)
        => SSL_CTX_ctrl(ctx, SSL_CTRL_SET_SESS_CACHE_SIZE, size, IntPtr.Zero);

    // ── SSL per-connection lifetime ────────────────────────────────────────
    [LibraryImport(LibSsl)] public static partial IntPtr SSL_new(IntPtr ctx);
    [LibraryImport(LibSsl)] public static partial void SSL_free(IntPtr ssl);

    // ⚠ THE ONE NEW P/INVOKE this proposal needs internally — does not exist
    // in dotnet/runtime today because SslStream uses memory BIOs, not SSL_set_fd.
    // Adding this is a one-line addition to Common/src/Interop/Unix/System.Security.Cryptography.Native/Interop.Ssl.cs.
    [LibraryImport(LibSsl)] public static partial int SSL_set_fd(IntPtr ssl, int fd);

    [LibraryImport(LibSsl)] public static partial void SSL_set_accept_state(IntPtr ssl);
    [LibraryImport(LibSsl)] public static partial void SSL_set_quiet_shutdown(IntPtr ssl, int mode);

    // ── SSL non-blocking operations ────────────────────────────────────────
    [LibraryImport(LibSsl)] public static partial int SSL_do_handshake(IntPtr ssl);
    [LibraryImport(LibSsl)] public static partial int SSL_get_error(IntPtr ssl, int ret);
    [LibraryImport(LibSsl, SetLastError = true)] public static partial int SSL_read(IntPtr ssl, byte* buf, int num);
    [LibraryImport(LibSsl, SetLastError = true)] public static partial int SSL_write(IntPtr ssl, byte* buf, int num);
    [LibraryImport(LibSsl)] public static partial int SSL_shutdown(IntPtr ssl);

    // SSL_get_error return values — mirrors the internal Interop.Ssl.SslErrorCode enum
    public const int SSL_ERROR_NONE        = 0;
    public const int SSL_ERROR_SSL         = 1;
    public const int SSL_ERROR_WANT_READ   = 2;
    public const int SSL_ERROR_WANT_WRITE  = 3;
    public const int SSL_ERROR_SYSCALL     = 5;
    public const int SSL_ERROR_ZERO_RETURN = 6;

    // ── OpenSSL error queue ────────────────────────────────────────────────
    [LibraryImport(LibCrypto)] public static partial void ERR_clear_error();
    [LibraryImport(LibCrypto)] public static partial ulong ERR_peek_error();
    [LibraryImport(LibCrypto)] public static partial ulong ERR_get_error();
    [LibraryImport(LibCrypto)] public static partial void ERR_error_string_n(ulong e, byte* buf, nuint len);

    public static string GetErrorString()
    {
        ulong err = ERR_get_error();
        if (err == 0) return "No error";
        byte* buf = stackalloc byte[256];
        ERR_error_string_n(err, buf, 256);
        return Marshal.PtrToStringUTF8((IntPtr)buf) ?? "Unknown error";
    }

    // ── OpenSSL one-time initialization ────────────────────────────────────
    [LibraryImport(LibSsl)] private static partial int OPENSSL_init_ssl(ulong opts, IntPtr settings);
    [LibraryImport(LibCrypto)] private static partial int OPENSSL_init_crypto(ulong opts, IntPtr settings);

    public static void Initialize()
    {
        OPENSSL_init_ssl(0, IntPtr.Zero);
        OPENSSL_init_crypto(0, IntPtr.Zero);
    }
}
