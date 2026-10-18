// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

internal static unsafe partial class TlsHandshakePrefix
{
    internal const int MaximumLength = 256;

    internal static bool TryPeek(int socket, Action<ReadOnlySpan<byte>> callback)
    {
        Span<byte> prefix = stackalloc byte[MaximumLength];
        nint count;
        fixed (byte* pointer = prefix)
        {
            count = Receive(socket, pointer, (nuint)prefix.Length, 0x02);
        }
        if (count < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 4 or 11)
            {
                return false;
            }
            throw new IOException("Peeking the native TLS handshake prefix failed.", new Win32Exception(error));
        }
        if (count == 0)
        {
            throw new EndOfStreamException("The peer closed before sending a TLS handshake prefix.");
        }
        Observe(callback, prefix[..checked((int)count)]);
        return true;
    }

    internal static void Observe(Action<ReadOnlySpan<byte>> callback, ReadOnlySpan<byte> prefix)
    {
        try
        {
            callback(prefix[..Math.Min(prefix.Length, MaximumLength)]);
        }
        catch (Exception error)
        {
            throw new IOException("The native TLS handshake-prefix callback failed.", error);
        }
    }

    [LibraryImport("libc", EntryPoint = "recv", SetLastError = true)]
    private static partial nint Receive(int socket, byte* buffer, nuint length, int flags);
}
