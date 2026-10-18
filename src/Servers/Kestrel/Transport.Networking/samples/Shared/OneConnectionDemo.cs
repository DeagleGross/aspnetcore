// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Transport;

internal static class OneConnectionDemo
{
    internal static async Task RunAsync(NetworkListener listener, CancellationTokenSource cancellation,
        Func<NetworkConnection, CancellationToken, Task> server, Func<CancellationToken, Task> client)
    {
        NetworkConnection? connection = null;
        var peer = RunClientAsync();
        try
        {
            connection = await listener.AcceptAsync(cancellation.Token)
                ?? throw new IOException("The listener closed before accepting the demo client.");
            Console.WriteLine($"SERVER accepted {connection.Id}");
            await server(connection, cancellation.Token);
            await peer;
        }
        catch (Exception error)
        {
            connection?.Abort(error);
            cancellation.Cancel();
            try
            {
                await peer;
            }
            catch (Exception peerError)
            {
                Console.Error.WriteLine($"CLIENT cleanup after demo failure: {peerError}");
            }
            throw;
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }

        async Task RunClientAsync()
        {
            try
            {
                await client(cancellation.Token);
            }
            catch
            {
                cancellation.Cancel();
                throw;
            }
        }
    }
}
