// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.IoUring;

internal static partial class Native
{
    private const string Library = "networkproto";
    internal const uint More = 2;

    [LibraryImport(Library, EntryPoint = "np_create")]
    internal static partial RingHandle Create(out int error);

    [LibraryImport(Library, EntryPoint = "np_create_size")]
    internal static partial RingHandle CreateSize(uint entries, out int error);

    [LibraryImport(Library, EntryPoint = "np_create_configured")]
    internal static partial RingHandle CreateConfigured(uint entries, int deferred, out int error);

    [LibraryImport(Library, EntryPoint = "np_enable")]
    internal static partial int Enable(RingHandle ring);

    [LibraryImport(Library, EntryPoint = "np_stage")]
    internal static unsafe partial int Stage(RingHandle ring, int operation, int fd, void* buffer, uint length, ulong id);

    [LibraryImport(Library, EntryPoint = "np_flush")]
    internal static partial int Flush(RingHandle ring);

    [LibraryImport(Library, EntryPoint = "np_collect")]
    internal static unsafe partial int Collect(RingHandle ring, ulong* ids, int* results, uint capacity, int wait);

    [LibraryImport(Library, EntryPoint = "np_submit_and_collect")]
    internal static unsafe partial int SubmitAndCollect(RingHandle ring, ulong* ids, int* results, uint capacity, int wait);

    [LibraryImport(Library, EntryPoint = "np_wake")]
    internal static partial int Wake(RingHandle ring);

    [LibraryImport(Library, EntryPoint = "np_enqueue")]
    internal static unsafe partial int Enqueue(RingHandle ring, int operation, int fd, void* buffer, uint length, ulong id);

    [LibraryImport(Library, EntryPoint = "np_try_send")]
    internal static unsafe partial int TrySend(SafeSocketHandle socket, void* buffer, uint length);

    [LibraryImport(Library, EntryPoint = "np_try_accept")]
    internal static partial int TryAccept(SafeSocketHandle socket);

    [LibraryImport(Library, EntryPoint = "np_cancel")]
    internal static partial int Cancel(RingHandle ring, ulong id);

    [LibraryImport(Library, EntryPoint = "np_wait")]
    internal static unsafe partial int Wait(RingHandle ring, ulong* ids, int* results, uint capacity, uint* flags);

    [LibraryImport(Library, EntryPoint = "np_destroy")]
    internal static partial void Destroy(nint ring);
}

internal sealed class RingHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public RingHandle() : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        Native.Destroy(handle);
        return true;
    }
}
