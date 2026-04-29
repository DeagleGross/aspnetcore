// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0073, IDE0011, IDE0005, CA1822

namespace System.Net.Security;

// PROTOTYPE — would live in dotnet/runtime as System.Net.Security.SslSessionCacheMode

[Flags]
internal enum SslSessionCacheMode
{
    Off = 0,
    Client = 1 << 0,
    Server = 1 << 1,
    Both = Client | Server,
}
