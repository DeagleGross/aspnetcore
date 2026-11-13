// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

internal static class TransportDiagnostics
{
    /// <summary>Gets whether the providers print their per-worker counters to the console (<c>NETWORKPROTO_DIAG=1</c>). Off by default so a host's stdout stays its own.</summary>
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("NETWORKPROTO_DIAG") == "1";
}
