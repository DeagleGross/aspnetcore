// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

internal enum SessionTlsOperation { Handshake, Read, Write, Shutdown }

// Same order as the native TlsStatus enums so providers can cast.
internal enum SessionTlsStatus { Complete, WantRead, WantWrite, Closed, PeerAbort, Failed }

internal readonly record struct SessionTlsResult(SessionTlsStatus Status, int Length = 0, bool Pending = false);

/// <summary>
/// The shared server-side <see cref="TlsContext"/> used when NETWORKPROTO_LINUX_TLS_IMPL=session selects the runtime's
/// fd-bound <see cref="TlsSocketSession"/> instead of the provider's native OpenSSL shim.
/// </summary>
internal sealed class SessionTlsContext : IDisposable
{
    internal const string SelectorVariable = "NETWORKPROTO_LINUX_TLS_IMPL";
    private readonly X509Certificate2 _certificate;
    private readonly TlsContext _context;

    internal static bool IsSelected => Environment.GetEnvironmentVariable(SelectorVariable) == "session";

    internal SessionTlsContext(string certificatePath, string keyPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The fd-bound TlsSession providers are Linux-only.");
        }
        _certificate = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        try
        {
            // Matches the native provider configuration: TLS 1.2, ECDHE-RSA-AES128-GCM-SHA256, no tickets or session cache.
            _context = TlsContext.CreateServer(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                EnabledSslProtocols = SslProtocols.Tls12,
                CipherSuitesPolicy = new CipherSuitesPolicy([TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256]),
                ClientCertificateRequired = false,
                AllowTlsResume = false
            });
        }
        catch
        {
            _certificate.Dispose();
            throw;
        }
    }

    // The provider keeps ownership of the descriptor: the session never closes it.
    internal SessionTls CreateSession(int fd) => new(_context, fd);

    public void Dispose()
    {
        _context.Dispose();
        _certificate.Dispose();
    }
}

/// <summary>
/// Adapts one non-blocking socket descriptor to the provider-level <c>Complete/WantRead/WantWrite/Closed/PeerAbort/Failed</c>
/// contract that the native OpenSSL shims already expose, so the provider state machines stay unchanged.
/// </summary>
internal sealed class SessionTls : IDisposable
{
    private readonly SafeSocketHandle _handle;
    private readonly TlsSocketSession _session;

    internal Exception? LastError { get; private set; }

    internal SessionTls(TlsContext context, int fd)
    {
        _handle = new SafeSocketHandle(fd, ownsHandle: false);
        _session = new TlsSocketSession(_handle);
        try
        {
            _session.SetContext(context);
        }
        catch
        {
            _session.Dispose();
            _handle.Dispose();
            throw;
        }
    }

    internal unsafe SessionTlsResult Call(SessionTlsOperation operation, nint data, int length)
    {
        try
        {
            switch (operation)
            {
                case SessionTlsOperation.Handshake:
                    return Map(Handshake());
                case SessionTlsOperation.Read:
                    var read = _session.Read(new Span<byte>((void*)data, length), out var bytesRead);
                    return Map(read, bytesRead, pending: bytesRead == length);
                case SessionTlsOperation.Write:
                    var write = _session.Write(new ReadOnlySpan<byte>((void*)data, length), out var bytesWritten);
                    return Map(write, bytesWritten);
                default:
                    // The fd-bound SSL object writes close_notify straight to the socket, then Shutdown() reports Closed.
                    var shutdown = _session.Shutdown();
                    return Map(shutdown == TlsOperationStatus.Closed ? TlsOperationStatus.Complete : shutdown);
            }
        }
        catch (Exception error)
        {
            LastError = error;
            return new SessionTlsResult(IsPeerAbort(error) ? SessionTlsStatus.PeerAbort : SessionTlsStatus.Failed);
        }
    }

    private TlsOperationStatus Handshake()
    {
        var status = _session.Handshake();
        if (status == TlsOperationStatus.NeedsCertificateValidation)
        {
            // Kestrel is configured without client certificates, so there is nothing to validate.
            _session.SetRemoteCertificateValidationResult(SslPolicyErrors.None);
            status = _session.Handshake();
        }
        return status;
    }

    private static SessionTlsResult Map(TlsOperationStatus status, int length = 0, bool pending = false) => status switch
    {
        TlsOperationStatus.Complete => new SessionTlsResult(SessionTlsStatus.Complete, length, pending),
        TlsOperationStatus.NeedMoreData => new SessionTlsResult(SessionTlsStatus.WantRead),
        TlsOperationStatus.DestinationTooSmall => new SessionTlsResult(SessionTlsStatus.WantWrite),
        TlsOperationStatus.Closed => new SessionTlsResult(SessionTlsStatus.Closed),
        _ => throw new InvalidOperationException($"Unexpected TLS operation status {status}.")
    };

    // The runtime surfaces OpenSSL failures as AuthenticationException naming the SSL error code; SYSCALL covers reset/EOF.
    private static bool IsPeerAbort(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is SocketException or IOException || current.Message.Contains("SSL_ERROR_SYSCALL", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        _session.Dispose();
        _handle.Dispose();
    }
}
