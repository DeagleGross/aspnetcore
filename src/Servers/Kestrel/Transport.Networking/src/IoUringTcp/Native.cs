// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringTcp;

internal static unsafe partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Command
    {
        public int Kind, Length;
        public ulong Connection;
        public void* Data;
        public int Page, Unused;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Event
    {
        public int Kind, Result;
        public ulong Connection;
        public void* Data;
        public int Page, Length;
    }

    [LibraryImport("networkproto2", EntryPoint = "np2_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint Create(int port, int tls, string cert, string key, int cpu, int flags, out int error);
    [LibraryImport("networkproto2", EntryPoint = "np2_step")]
    internal static partial int Step(nint engine, Command* commands, int count, out Event* events);
    [LibraryImport("networkproto2", EntryPoint = "np2_wake")]
    internal static partial int Wake(nint engine);
    [LibraryImport("networkproto2", EntryPoint = "np2_stats")]
    internal static partial void Stats(nint engine, ulong* counters);
    [LibraryImport("networkproto2", EntryPoint = "np2_destroy")]
    internal static partial void Destroy(nint engine);
}
