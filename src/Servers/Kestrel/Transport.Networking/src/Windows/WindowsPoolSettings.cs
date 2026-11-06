// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Transport;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

/// <summary>The per-worker native pool limits and the idle-slab trim policy.</summary>
internal sealed record WindowsPoolSettings(int InputMaximumBytes, int OutputMaximumBytes, TimeSpan IdleTrimDelay)
{
    internal static WindowsPoolSettings Default { get; } = From(new IocpEngineOptions());

    internal static WindowsPoolSettings From(WindowsEngineOptions options)
        => new(options.InputPoolMaximumBytes, options.OutputPoolMaximumBytes, options.PoolIdleTrimDelay);
}
