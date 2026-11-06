// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

namespace System.Net.Transport;

/// <summary>Lets <see cref="HttpClient"/> dial through any <see cref="IConnectionFactory"/>.</summary>
public static class NetworkTransportHttpExtensions
{
    /// <summary>
    /// Replaces the socket layer of <see cref="SocketsHttpHandler"/> with a connection factory. HTTP framing, pooling and TLS stay in the handler.
    /// A connection that exposes <see cref="INetworkConnectionFeature"/> already bounds its own receive window (the transport lets a client lease
    /// received bytes up to a byte budget, which plays the part of a kernel receive buffer), so the handler reads straight from the transport pipe.
    /// Any other <see cref="IConnectionFactory"/> gets a 1 MiB read-ahead copy so a request body written before the response is read cannot deadlock.
    /// </summary>
    /// <param name="handler">The handler to configure.</param>
    /// <param name="factory">The factory that opens the transport connections.</param>
    /// <returns>The same handler.</returns>
    public static SocketsHttpHandler UseConnectionFactory(this SocketsHttpHandler handler, IConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(factory);
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var connection = await factory.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            return new ConnectionContextStream(connection, connection.Features.Get<INetworkConnectionFeature>() is null ? DefaultReceiveBufferBytes : 0);
        };
        return handler;
    }

    /// <summary>
    /// Replaces the socket layer of <see cref="SocketsHttpHandler"/> with a connection factory and always adapts the connection's
    /// <see cref="ConnectionContext.Transport"/> pipe, which works with any <see cref="IConnectionFactory"/>.
    /// </summary>
    /// <param name="handler">The handler to configure.</param>
    /// <param name="factory">The factory that opens the transport connections.</param>
    /// <param name="receiveBufferBytes">
    /// How many received bytes a connection can buffer ahead of the handler. The transport itself leases only a few pages, so without
    /// read-ahead a request body that is written fully before the response is read can deadlock against an echoing peer. Zero reads
    /// straight from the transport pipe without an extra copy, which is right for consumers that read and write concurrently.
    /// </param>
    /// <returns>The same handler.</returns>
    public static SocketsHttpHandler UseConnectionFactory(this SocketsHttpHandler handler, IConnectionFactory factory, int receiveBufferBytes)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentOutOfRangeException.ThrowIfNegative(receiveBufferBytes);
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var connection = await factory.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            return new ConnectionContextStream(connection, receiveBufferBytes);
        };
        return handler;
    }

    private const int DefaultReceiveBufferBytes = 1024 * 1024;
}
