// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Runtime.InteropServices;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUringBio;

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Status
    {
        public nint Output;
        public int InputRemaining, OutputRemaining;
        public ulong InputCopied, OutputCopied;
    }
    [LibraryImport("networkprotobio", EntryPoint = "ub_method")]
    internal static partial nint Method();
    [LibraryImport("networkprotobio", EntryPoint = "ub_session")]
    internal static partial nint Session(nint context, nint method, out nint state);
    [LibraryImport("networkprotobio", EntryPoint = "ub_feed")]
    internal static partial void Feed(nint state, nint data, int length, int eof);
    [LibraryImport("networkprotobio", EntryPoint = "ub_status")]
    internal static partial void GetStatus(nint state, out Status result);
    [LibraryImport("networkprotobio", EntryPoint = "ub_advance")]
    internal static partial void Advance(nint state, int count);
    [LibraryImport("networkprotobio", EntryPoint = "ub_free")]
    internal static partial void Free(nint state);
    [LibraryImport("networkprotobio", EntryPoint = "ub_free_method")]
    internal static partial void FreeMethod(nint method);
}
