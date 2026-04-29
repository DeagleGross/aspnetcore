// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0073, IDE0011, IDE0005, CA1822

namespace System.Net.Sockets;

// PROTOTYPE — would live in dotnet/runtime as System.Net.Sockets.EpollEvents

/// <summary>Linux epoll event flags (mirror EPOLL* constants).</summary>
[Flags]
internal enum EpollEvents : uint
{
    None      = 0,
    Read      = 1u << 0,   // EPOLLIN
    Write     = 1u << 2,   // EPOLLOUT
    PeerClose = 1u << 13,  // EPOLLRDHUP — peer half-closed write side
    Error     = 1u << 3,   // EPOLLERR  — surfaced even when not requested
    HangUp    = 1u << 4,   // EPOLLHUP  — surfaced even when not requested
}
