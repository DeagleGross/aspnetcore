// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;

internal static class EpollLifecycleCheck
{
    internal static async Task RunAsync(IConnectionListenerFactory factory, int port, bool tls)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = await factory.BindAsync(new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
        var clients = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 16; i++)
            {
                var client = new TcpClient();
                clients.Add(client);
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            }
            // Stop with real idle peers, including unfinished handshakes in TLS mode.
            await Task.Delay(200, timeout.Token);
            await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var client in clients)
            {
                if (await client.GetStream().ReadAsync(new byte[1], timeout.Token) != 0)
                {
                    throw new IOException("Expected EOF after listener disposal.");
                }
            }
            Console.WriteLine(tls
                ? "PASS epoll lifecycle: listener stopped with 16 uncompleted TLS handshakes"
                : "PASS epoll lifecycle: 16 idle TCP peers observed EOF after listener disposal");
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }
}
