// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;

internal static class EpollLifecycleCheck
{
    internal static async Task RunAsync(IConnectionListenerFactory factory, int port)
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
            // Leave real accepted sockets waiting for ClientHello while stopping the listener.
            await Task.Delay(200, timeout.Token);
            await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine("PASS epoll lifecycle: listener stopped with 16 uncompleted TLS handshakes");
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
