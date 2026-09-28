// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;

internal static class FinalSendCheck
{
    internal static async Task RunAsync(IConnectionListenerFactory factory, int port, X509Certificate2? certificate = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = await factory.BindAsync(new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
        try
        {
            // Pipe completion is the real producer of the final-send hint.
            foreach (var size in new[] { 1024, 4 * 1024 * 1024 })
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                using var stream = await GetStreamAsync(client, certificate, timeout.Token);
                var connection = await listener.AcceptAsync(timeout.Token) ?? throw new IOException("Listener closed.");
                try
                {
                    Fill(connection, size);
                    await connection.Transport.Output.CompleteAsync();
                    // Allow send backpressure before the client begins consuming.
                    await Task.Delay(150, timeout.Token);
                    var received = 0;
                    var buffer = new byte[8192];
                    while (true)
                    {
                        var count = await stream.ReadAsync(buffer, timeout.Token);
                        if (count == 0)
                        {
                            break;
                        }
                        for (var i = 0; i < count; i++)
                        {
                            if (buffer[i] != (byte)((received + i) % 251))
                            {
                                throw new InvalidDataException("Final-send payload corrupted.");
                            }
                        }
                        received += count;
                    }
                    if (received != size)
                    {
                        throw new InvalidDataException($"Premature FIN: expected {size}, received {received}.");
                    }
                    await connection.DisposeAsync().AsTask().WaitAsync(timeout.Token);
                    Console.WriteLine($"PASS final-send: {size} bytes followed by EOF, native lifetime drained");
                }
                catch
                {
                    connection.Abort(new ConnectionAbortedException("Final-send check failed."));
                    throw;
                }
            }

            using (var client = new TcpClient { ReceiveBufferSize = 4096 })
            {
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                using var stream = await GetStreamAsync(client, certificate, timeout.Token);
                var connection = await listener.AcceptAsync(timeout.Token) ?? throw new IOException("Listener closed.");
                Fill(connection, 16 * 1024 * 1024);
                await connection.Transport.Output.CompleteAsync();
                await Task.Delay(150, timeout.Token);
                client.Client.LingerState = new LingerOption(true, 0);
                client.Close();
                await connection.DisposeAsync().AsTask().WaitAsync(timeout.Token);
                Console.WriteLine("PASS final-send: reset during backpressured final output drained without hanging");
            }
        }
        finally
        {
            await listener.UnbindAsync(timeout.Token);
            await listener.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        }
    }

    private static async Task<Stream> GetStreamAsync(TcpClient client, X509Certificate2? certificate, CancellationToken token)
    {
        if (certificate is null)
        {
            return client.GetStream();
        }
        var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: true, (_, remote, _, _) =>
            remote is not null && remote.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData));
        try
        {
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12,
                AllowTlsResume = false
            }, token);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void Fill(ConnectionContext connection, int size)
    {
        var memory = connection.Transport.Output.GetMemory(size);
        for (var i = 0; i < size; i++)
        {
            memory.Span[i] = (byte)(i % 251);
        }
        connection.Transport.Output.Advance(size);
    }
}
