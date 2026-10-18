// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

internal static class HttpProbe
{
    internal static async Task RunAsync(Uri address, string method, string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var client = new TcpClient();
        await client.ConnectAsync(address.Host, address.Port, token);
        var network = client.GetStream();
        using var tls = address.Scheme == "https" ? new SslStream(network, leaveInnerStreamOpen: true, static (_, _, _, _) => true) : null;
        if (tls is not null)
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = Enum.Parse<SslProtocols>(protocol),
                AllowTlsResume = false
            }, token);
        }
        Stream stream = tls is null ? network : tls;
        var request = Encoding.ASCII.GetBytes($"{method} {address.PathAndQuery} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
        await stream.WriteAsync(request, token);
        using var response = new MemoryStream();
        await stream.CopyToAsync(response, token);
        if (tls is not null)
        {
            await tls.ShutdownAsync();
        }
        client.Client.Shutdown(SocketShutdown.Send);
        var bytes = response.ToArray();
        var headerEnd = bytes.AsSpan().IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0 || !bytes.AsSpan().StartsWith("HTTP/1.1 200"u8))
        {
            throw new IOException("Control probe did not receive an HTTP 200 response.");
        }
        var headers = Encoding.ASCII.GetString(bytes, 0, headerEnd);
        var body = bytes.AsMemory(headerEnd + 4);
        if (headers.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
        {
            using var decoded = new MemoryStream();
            while (true)
            {
                var end = body.Span.IndexOf("\r\n"u8);
                if (end < 0)
                {
                    throw new IOException("Incomplete chunked control response.");
                }
                var length = int.Parse(Encoding.ASCII.GetString(body.Span[..end]), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                body = body[(end + 2)..];
                if (length == 0)
                {
                    break;
                }
                decoded.Write(body.Span[..length]);
                body = body[(length + 2)..];
            }
            Console.Write(Encoding.UTF8.GetString(decoded.ToArray()));
        }
        else
        {
            Console.Write(Encoding.UTF8.GetString(body.Span));
        }
    }
}
