// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl.Connection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.DirectSsl;

/// <summary>
/// A factory for direct-ssl based connections.
/// </summary>
internal sealed class DirectSslTransportFactory : IConnectionListenerFactory, IConnectionListenerFactorySelector
{
    private TlsContext? _tlsContext;
    private SslEventPumpPool? _pumpPool;

    private readonly DirectSslTransportOptions _options;

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectSslTransportFactory"/> class.
    /// </summary>
    /// <param name="options">The transport options.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public DirectSslTransportFactory(
        IOptions<DirectSslTransportOptions> options,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DirectSslTransportFactory>();
    }

    /// <inheritdoc />
    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        // Initialize TLS context lazily from options
        if (_tlsContext is null)
        {
            if (string.IsNullOrEmpty(_options.CertificatePath) || string.IsNullOrEmpty(_options.PrivateKeyPath))
            {
                throw new InvalidOperationException("CertificatePath and PrivateKeyPath must be configured in DirectSslTransportOptions.");
            }

            // Load PEM cert + key into a single X509Certificate2 (the private key is associated
            // via the underlying OpenSSL EVP_PKEY on Linux, so it can be used by TlsContext).
            var cert = X509Certificate2.CreateFromPemFile(_options.CertificatePath, _options.PrivateKeyPath);

            var serverOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                AllowRenegotiation = false,
                ClientCertificateRequired = false,
                // Let TlsContext-owned SSL_CTX honor session resumption (the runtime PoC wires this through)
                AllowTlsResume = true,
            };

            _tlsContext = TlsContext.Create(serverOptions);
            _logger.LogInformation("TlsContext initialized with certificate: {CertPath}", _options.CertificatePath);
        }

        // Initialize SSL event pump pool lazily
        if (_pumpPool is null)
        {
            _pumpPool = new SslEventPumpPool(_options.WorkerCount, _loggerFactory);
            _logger.LogInformation("SSL event pump pool started with {PumpCount} pumps.", _options.WorkerCount);
        }

        // Using shared memory pool for simplicity
        var memoryPool = MemoryPool<byte>.Shared;
        var transport = new DirectSslConnectionListener(
            _loggerFactory,
            _tlsContext,
            _pumpPool,
            endpoint,
            _options,
            memoryPool);

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
