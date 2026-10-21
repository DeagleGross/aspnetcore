// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Transport;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

/// <summary>
/// The only surface provider workers use to drive a connection. It names no Kestrel type, so a provider publishes
/// either the shared <see cref="NetworkConnection"/> or a legacy raw connection through the same worker code.
/// </summary>
internal interface IProviderConnection : IAsyncDisposable
{
    void Sent(int count, Exception? error = null);
    void Closed(Exception? error);
    void EndReceive(Exception? error);
    void PumpOutput();
    void Abort(Exception reason);

    /// <summary>Called once, after the provider finished its TLS handshake and before the connection is published.</summary>
    void TlsEstablished(NetworkTlsInfo info) => throw new InvalidOperationException("This provider establishes TLS before connection construction.");
}

/// <summary>Accepts provider connections without committing to the consumer's connection type.</summary>
internal interface IProviderListener : IAsyncDisposable
{
    EndPoint EndPoint { get; }
    ValueTask<IProviderConnection?> AcceptAsync(CancellationToken cancellationToken = default);
    ValueTask UnbindAsync(CancellationToken cancellationToken = default);
}

internal interface IProviderListenerFactory
{
    ValueTask<IProviderListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default);
}
