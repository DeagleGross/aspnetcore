// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Abi = Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp.Native;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringBio;

internal static unsafe partial class Native
{
    [LibraryImport("networkprotobio", EntryPoint = "np2_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint Create(int port, int tls, string cert, string key, int cpu, int flags, out int error);
    [LibraryImport("networkprotobio", EntryPoint = "np2_step")]
    internal static partial int Step(nint engine, Abi.Command* commands, int count, out Abi.Event* events);
    [LibraryImport("networkprotobio", EntryPoint = "np2_wake")]
    internal static partial int Wake(nint engine);
    [LibraryImport("networkprotobio", EntryPoint = "np2_stats")]
    internal static partial void Stats(nint engine, ulong* counters);
    [LibraryImport("networkprotobio", EntryPoint = "np2_bio_stats")]
    internal static partial void BioStats(nint engine, ulong* counters);
    [LibraryImport("networkprotobio", EntryPoint = "np2_destroy")]
    internal static partial void Destroy(nint engine);
}
