// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;

internal static class RingLifecycleCheck
{
    internal static async Task RunAsync(IConnectionListenerFactory factory, int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
            await Task.Delay(200, timeout.Token);
            await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var client in clients)
            {
                var read = await client.GetStream().ReadAsync(new byte[1], timeout.Token);
                if (read != 0)
                {
                    throw new IOException("Expected EOF after listener disposal.");
                }
            }
            Console.WriteLine("PASS ring lifecycle: 16 idle peers observed EOF, pending receives/handshakes and listener drained");
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
