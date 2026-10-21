// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using EpollFactory = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll.TransportFactory;
using RingFactory = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp.TransportFactory;
using WindowsFactory = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows.TransportFactory;

namespace System.Net.Transport;

/// <summary>Creates an explicitly selected prototype provider for the shared primitives.</summary>
public static class NetworkEngineFactory
{
    /// <summary>Creates owning native workers when listeners are bound; unsupported providers fail explicitly.</summary>
    /// <param name="options">Typed provider and worker options.</param>
    /// <param name="loggerFactory">Optional provider logging.</param>
    /// <returns>An engine exposing the shared owned-memory contract.</returns>
    public static NetworkEngine Create(NetworkEngineOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.WorkerCount);
        var kind = options switch
        {
            EpollEngineOptions => NetworkEngineKind.Epoll,
            IoUringEngineOptions => NetworkEngineKind.IoUring,
            IocpEngineOptions => NetworkEngineKind.Iocp,
            RioEngineOptions => NetworkEngineKind.Rio,
            _ => throw new NotSupportedException("Unknown network engine options.")
        };
        if (kind is NetworkEngineKind.Epoll or NetworkEngineKind.IoUring && !OperatingSystem.IsLinux()
            || kind is NetworkEngineKind.Iocp or NetworkEngineKind.Rio && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException($"The explicitly selected {kind} engine is not available on this OS.");
        }
        var setting = Environment.GetEnvironmentVariable("NETWORKPROTO_CPUS");
        var processors = options.WorkerProcessors?.ToArray()
            ?? (setting?.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray());
        if (processors is not null && (processors.Length != options.WorkerCount || processors.Any(cpu => cpu < 0)))
        {
            throw new ArgumentException("Worker processor IDs must be nonnegative and match WorkerCount.", nameof(options));
        }
        if (processors is null && kind == NetworkEngineKind.IoUring)
        {
            processors = Enumerable.Range(0, options.WorkerCount).Select(cpu => 2 * cpu).ToArray();
        }
        return new ProviderEngine(kind, options.WorkerCount, processors, loggerFactory ?? NullLoggerFactory.Instance);
    }

    private sealed class ProviderEngine(NetworkEngineKind kind, int workers, int[]? processors, ILoggerFactory logging) : NetworkEngine
    {
        private readonly List<NetworkListener> _listeners = new();
        private readonly SemaphoreSlim _binding = new(1, 1);
        private readonly Lock _disposeGate = new();
        private Task? _disposing;
        private int _disposed;
        public override NetworkEngineKind Kind => kind;

        // Default: the ring receives/sends ciphertext and OpenSSL only sees it through a custom BIO. NETWORKPROTO_URING_TLS=fd selects the fd-bound OpenSSL path,
        // which is also used when NETWORKPROTO_KTLS=1 because a custom BIO cannot activate kernel TLS.
        private static bool UseRingOwnedCiphertext() => Environment.GetEnvironmentVariable("NETWORKPROTO_URING_TLS") switch
        {
            "fd" => false,
            "ring" => true,
            _ => Environment.GetEnvironmentVariable("NETWORKPROTO_KTLS") != "1"
        };

        public override async ValueTask<NetworkListener> ListenAsync(EndPoint endpoint, NetworkListenOptions? options = null, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ArgumentNullException.ThrowIfNull(endpoint);
            await _binding.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var tls = options?.NativeTls;
                if (tls is not null)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(tls.CertificatePath);
                    ArgumentException.ThrowIfNullOrWhiteSpace(tls.PrivateKeyPath);
                }
                IConnectionListenerFactory factory = kind switch
                {
                    NetworkEngineKind.Epoll => new EpollFactory(tls?.CertificatePath ?? string.Empty, tls?.PrivateKeyPath ?? string.Empty,
                        workers, logging, tls is not null, shared: true, processors, tls?.HandshakePrefixCallback),
                    NetworkEngineKind.IoUring => new RingFactory(tls is not null, tls?.CertificatePath ?? string.Empty, tls?.PrivateKeyPath ?? string.Empty,
                        logging, layered: tls is not null && UseRingOwnedCiphertext(),
                        shared: true, processors: processors, handshakePrefixCallback: tls?.HandshakePrefixCallback),
                    NetworkEngineKind.Iocp or NetworkEngineKind.Rio => new WindowsFactory(kind == NetworkEngineKind.Rio, workers, logging,
                        shared: true, processors, tls),
                    _ => throw new NotSupportedException("Unknown network engine.")
                };
                var listener = new ProviderListener(await factory.BindAsync(endpoint, cancellationToken).ConfigureAwait(false));
                _listeners.Add(listener);
                return listener;
            }
            finally
            {
                _binding.Release();
            }
        }

        public override ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                return new ValueTask(_disposing ??= DisposeCore());
            }
        }

        private async Task DisposeCore()
        {
            Volatile.Write(ref _disposed, 1);
            await _binding.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var listener in _listeners)
                {
                    await listener.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _binding.Release();
            }
        }
    }

    private sealed class ProviderListener(IConnectionListener listener) : NetworkListener
    {
        private readonly Lock _gate = new();
        private Task? _disposing;
        public override EndPoint LocalEndPoint => listener.EndPoint;
        public override async ValueTask<NetworkConnection?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            var connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            if (connection is null)
            {
                return null;
            }
            if (connection is not SharedConnectionContext shared)
            {
                throw new InvalidOperationException("The provider published a raw connection through the shared engine.");
            }
            return shared.Network;
        }
        public override ValueTask UnbindAsync(CancellationToken cancellationToken = default) => listener.UnbindAsync(cancellationToken);
        public override ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                return new ValueTask(_disposing ??= listener.DisposeAsync().AsTask());
            }
        }
    }
}
