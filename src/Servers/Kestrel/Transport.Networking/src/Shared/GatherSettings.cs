// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

// NETWORKPROTO_VECTORED=0 restores one native send per output segment so the same binary can A/B the gather path.
internal static class GatherSettings
{
    internal const int Segments = 64;
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("NETWORKPROTO_VECTORED") != "0";
}
