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
using HybridListener         = Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Engines.Hybrid.Connection.DirectSslConnectionListener;
using HybridPumpPool         = Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Engines.Hybrid.SslEventPumpPool;
using HybridContext          = Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Engines.Hybrid.Ssl.SslContext;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl;

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

    // Hybrid engine state (OSD clone with TlsSession primitives)
    private HybridContext? _hybridContext;
    private HybridPumpPool? _hybridPumpPool;

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
            DirectSslEngineKind.Hybrid        => BindHybrid(endpoint),
            _ => throw new InvalidOperationException($"Unknown engine kind: {_engine}"),
        };
    }

    private ValueTask<IConnectionListener> BindTlsSession(EndPoint endpoint)
    {
        if (_tlsContext is null)
        {
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

    private ValueTask<IConnectionListener> BindHybrid(EndPoint endpoint)
    {
        if (_hybridContext is null)
        {
            _hybridContext = new HybridContext(_options.CertificatePath!, _options.PrivateKeyPath!);
            _logger.LogInformation("[Hybrid] SslContext initialized with certificate: {CertPath}", _options.CertificatePath);
        }

        if (_hybridPumpPool is null)
        {
            _hybridPumpPool = new HybridPumpPool(_options.WorkerCount, _loggerFactory);
            _logger.LogInformation("[Hybrid] event pump pool started with {PumpCount} pumps.", _options.WorkerCount);
        }

        var transport = new HybridListener(
            _loggerFactory,
            _hybridContext,
            _hybridPumpPool,
            endpoint,
            _options,
            MemoryPool<byte>.Shared);

        transport.Bind();
        return new ValueTask<IConnectionListener>(transport);
    }

    public bool CanBind(EndPoint endpoint) => endpoint switch
    {
        IPEndPoint _ => true,
        UnixDomainSocketEndPoint _ => true,
        FileHandleEndPoint _ => true,
        _ => false
    };
}