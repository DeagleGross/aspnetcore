// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Security;

namespace System.Net.Transport;

/// <summary>Installs caller-selected TLS after optional raw connection processing.</summary>
public static class NetworkTlsExtensions
{
    /// <summary>
    /// Authenticates a non-owning TLS stream over a plain connection, preserving pre-read ciphertext.
    /// Native fd-bound TLS is selected explicitly at listen time instead.
    /// </summary>
    /// <param name="connection">The raw TCP connection.</param>
    /// <param name="options">All ordinary SslStream server authentication options and callbacks.</param>
    /// <param name="cancellationToken">Handshake cancellation.</param>
    /// <returns>The authenticated plaintext stream; dispose it before disposing the connection.</returns>
    public static async ValueTask<SslStream> AuthenticateAsServerAsync(this NetworkConnection connection,
        SslServerAuthenticationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        if (connection.IsTls)
        {
            throw new InvalidOperationException("The connection already presents native TLS plaintext.");
        }
        var stream = new SslStream(connection.AsStream(), leaveInnerStreamOpen: false);
        try
        {
            await stream.AuthenticateAsServerAsync(options, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
