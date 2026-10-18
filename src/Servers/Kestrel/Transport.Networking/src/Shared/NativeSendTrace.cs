// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

internal static class NativeSendTrace
{
    internal static void Write(ILogger logger, nint address, int length)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace(new EventId(1010, "NativeTcpSend"), "Native TCP send address={Address} length={Length}", (long)address, length);
        }
    }
}
