// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.Transport;

/// <summary>Identifies an experimental native networking provider.</summary>
public enum NetworkEngineKind
{
    /// <summary>Linux readiness I/O.</summary>
    Epoll,
    /// <summary>Linux ring submission and completion I/O.</summary>
    IoUring,
    /// <summary>Windows overlapped I/O completion ports.</summary>
    Iocp,
    /// <summary>Windows registered Winsock I/O.</summary>
    Rio
}

/// <summary>Configures owning workers without exposing a kernel submission format.</summary>
public abstract class NetworkEngineOptions
{
    /// <summary>Gets or sets the number of owning worker threads.</summary>
    public int WorkerCount { get; set; } = 4;

    /// <summary>Gets or sets optional logical processor IDs, one per worker.</summary>
    public IReadOnlyList<int>? WorkerProcessors { get; set; }
}

/// <summary>Configures the epoll provider.</summary>
public sealed class EpollEngineOptions : NetworkEngineOptions
{
}

/// <summary>Configures the current ring-native io_uring provider.</summary>
public sealed class IoUringEngineOptions : NetworkEngineOptions
{
}

/// <summary>Configures the IOCP provider.</summary>
public sealed class IocpEngineOptions : NetworkEngineOptions
{
}

/// <summary>Configures the explicitly selected registered-I/O provider.</summary>
public sealed class RioEngineOptions : NetworkEngineOptions
{
}

/// <summary>Configures a listener independently of provider selection.</summary>
public sealed class NetworkListenOptions
{
    /// <summary>
    /// Gets or sets transport-owned TLS: Linux fd-bound OpenSSL or Windows SChannel.
    /// The connection is published after authentication. Raw initial TLS bytes can be
    /// observed through <see cref="NetworkNativeTlsOptions.HandshakePrefixCallback"/>;
    /// arbitrary pre-TLS protocols instead use a plain listener and a stream TLS wrapper.
    /// </summary>
    public NetworkNativeTlsOptions? NativeTls { get; set; }
}

/// <summary>Configures fixed TLS 1.2 OpenSSL on Linux or TLS 1.3 SChannel on Windows, without a server SslStream.</summary>
public sealed class NetworkNativeTlsOptions
{
    /// <summary>Gets the PEM certificate-chain path.</summary>
    public required string CertificatePath { get; init; }

    /// <summary>Gets the PEM private-key path.</summary>
    public required string PrivateKeyPath { get; init; }

    /// <summary>
    /// Gets an optional synchronous observer of a nonempty prefix of the first inbound TLS bytes,
    /// before native handshake processing. The prefix may be fragmented and is not a parsed or complete
    /// ClientHello. It is borrowed only for the callback and cannot be consumed or retained.
    /// The callback runs once on the owning I/O worker, must not block, and aborts the connection if it throws.
    /// </summary>
    public Action<ReadOnlySpan<byte>>? HandshakePrefixCallback { get; init; }
}

/// <summary>A framework-neutral, non-shipping networking engine contract.</summary>
public abstract class NetworkEngine : IAsyncDisposable
{
    /// <summary>Gets the actual selected provider.</summary>
    public abstract NetworkEngineKind Kind { get; }

    /// <summary>Binds a server listener using this engine.</summary>
    public abstract ValueTask<NetworkListener> ListenAsync(EndPoint endpoint, NetworkListenOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Stops listeners and drains their native ownership.</summary>
    public abstract ValueTask DisposeAsync();
}

/// <summary>Accepts connections while keeping the native listener private.</summary>
public abstract class NetworkListener : IAsyncDisposable
{
    /// <summary>Gets the bound local endpoint.</summary>
    public abstract EndPoint LocalEndPoint { get; }

    /// <summary>Accepts the next connection, or returns null after unbinding.</summary>
    public abstract ValueTask<NetworkConnection?> AcceptAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops new accepts without prematurely releasing live connections.</summary>
    public abstract ValueTask UnbindAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the listener and drains outstanding ownership.</summary>
    public abstract ValueTask DisposeAsync();
}
