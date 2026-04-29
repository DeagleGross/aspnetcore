// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0073, IDE0011, IDE0005, CA1822

namespace System.Net.Sockets;

// PROTOTYPE — would live in dotnet/runtime as System.Net.Sockets.EpollOptions

/// <summary>epoll_ctl behaviour modifiers (mirror EPOLL* constants).</summary>
[Flags]
internal enum EpollOptions : uint
{
    None             = 0,
    EdgeTriggered    = 1u << 31, // EPOLLET
    OneShot          = 1u << 30, // EPOLLONESHOT
    ExclusiveWakeup  = 1u << 28, // EPOLLEXCLUSIVE — only one waiter wakes per event
                                 //                  (intended for shared listen sockets)
}
