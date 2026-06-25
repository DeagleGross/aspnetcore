// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Aliases: both engines define classes that share simple names. Disambiguate at the using-level
// so the factory body stays readable.
using TlsSessionListener     = Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Connection.DirectSslConnectionListener;
using TlsSessionPumpPool     = Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.SslEventPumpPool;
using OpenSslDirectListener  = Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Engines.OpenSslDirect.Connection.DirectSslConnectionListener;
using OpenSslDirectPumpPool  = Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Engines.OpenSslDirect.SslEventPumpPool;
using OpenSslDirectContext   = Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Engines.OpenSslDirect.Ssl.SslContext;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl;

/// <summary>
/// A factory for direct-ssl based connections. Selects between two TLS engines
/// at startup based on <see cref="DirectSslTransportOptions.Engine"/> or the
/// <c>KESTREL_DIRECTSSL_ENGINE</c> environment variable. Both engines share the
/// listener-factory entry point but each carries its own pump pool, listener,
/// and per-connection state — there is no per-call virtual dispatch between
/// them, which keeps the hot path monomorphic and the A/B comparison clean.
/// </summary>
internal sealed class DirectSslTransportFactory : IConnectionListenerFactory, IConnectionListenerFactorySelector
{
    private readonly DirectSslTransportOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    private readonly DirectSslEngineKind _engine;

    // TlsSession (runtime PoC) state
    private TlsContext? _tlsContext;
    private TlsSessionPumpPool? _tlsSessionPumpPool;

    // OpenSslDirect (resurrected Net10-Private) state
    private OpenSslDirectContext? _opensslDirectContext;
    private OpenSslDirectPumpPool? _opensslDirectPumpPool;

    public DirectSslTransportFactory(
        IOptions<DirectSslTransportOptions> options,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DirectSslTransportFactory>();

        _engine = ResolveEngine(_options, _logger);
        _logger.LogInformation("DirectSsl engine selected: {Engine}", _engine);
    }

    /// <summary>
    /// Returns the engine kind to use. Precedence:
    /// 1. <c>KESTREL_DIRECTSSL_ENGINE</c> env var (case-insensitive) — wins over options so that
    ///    benchmark scripts can flip the engine without touching code.
    /// 2. <see cref="DirectSslTransportOptions.Engine"/>.
    /// </summary>
    private static DirectSslEngineKind ResolveEngine(DirectSslTransportOptions options, ILogger logger)
    {
        string? envValue = Environment.GetEnvironmentVariable("KESTREL_DIRECTSSL_ENGINE");
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            if (Enum.TryParse<DirectSslEngineKind>(envValue, ignoreCase: true, out var parsed))
            {
                return parsed;
            }
            logger.LogWarning(
                "KESTREL_DIRECTSSL_ENGINE='{Value}' is not a recognized engine kind. Falling back to options ({Fallback}).",
                envValue, options.Engine);
        }
        return options.Engine;
    }

    /// <inheritdoc />
    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_options.CertificatePath) || string.IsNullOrEmpty(_options.PrivateKeyPath))
        {
            throw new InvalidOperationException("CertificatePath and PrivateKeyPath must be configured in DirectSslTransportOptions.");
        }

        return _engine switch
        {
            DirectSslEngineKind.TlsSession    => BindTlsSession(endpoint),
            DirectSslEngineKind.OpenSslDirect => BindOpenSslDirect(endpoint),
            _ => throw new InvalidOperationException($"Unknown engine kind: {_engine}"),
        };
    }

    private ValueTask<IConnectionListener> BindTlsSession(EndPoint endpoint)
    {
        if (_tlsContext is null)
        {
            // Load PEM cert + key into a single X509Certificate2 (the private key is associated
            // via the underlying OpenSSL EVP_PKEY on Linux, so it can be used by TlsContext).
            var cert = X509Certificate2.CreateFromPemFile(_options.CertificatePath!, _options.PrivateKeyPath!);

            var serverOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                AllowRenegotiation = false,
                ClientCertificateRequired = false,
                AllowTlsResume = true,
            };

            _tlsContext = TlsContext.Create(serverOptions);
            _logger.LogInformation("[TlsSession] TlsContext initialized with certificate: {CertPath}", _options.CertificatePath);
        }

        if (_tlsSessionPumpPool is null)
        {
            _tlsSessionPumpPool = new TlsSessionPumpPool(_options.WorkerCount, _loggerFactory);
            _logger.LogInformation("[TlsSession] event pump pool started with {PumpCount} pumps.", _options.WorkerCount);
        }

        var transport = new TlsSessionListener(
            _loggerFactory,
            _tlsContext,
            _tlsSessionPumpPool,
            endpoint,
            _options,
            MemoryPool<byte>.Shared);

        transport.Bind();
        return new ValueTask<IConnectionListener>(transport);
    }

    private ValueTask<IConnectionListener> BindOpenSslDirect(EndPoint endpoint)
    {
        if (_opensslDirectContext is null)
        {
            _opensslDirectContext = new OpenSslDirectContext(_options.CertificatePath!, _options.PrivateKeyPath!);
            _logger.LogInformation("[OpenSslDirect] SslContext initialized with certificate: {CertPath}", _options.CertificatePath);
        }

        if (_opensslDirectPumpPool is null)
        {
            _opensslDirectPumpPool = new OpenSslDirectPumpPool(_options.WorkerCount, _loggerFactory);
            _logger.LogInformation("[OpenSslDirect] event pump pool started with {PumpCount} pumps.", _options.WorkerCount);
        }

        var transport = new OpenSslDirectListener(
            _loggerFactory,
            _opensslDirectContext,
            _opensslDirectPumpPool,
            endpoint,
            _options,
            MemoryPool<byte>.Shared);

        transport.Bind();
        return new ValueTask<IConnectionListener>(transport);
    }

    /// <inheritdoc />
    public bool CanBind(EndPoint endpoint) => endpoint switch
    {
        IPEndPoint _ => true,
        UnixDomainSocketEndPoint _ => true,
        FileHandleEndPoint _ => true,
        _ => false
    };
}
