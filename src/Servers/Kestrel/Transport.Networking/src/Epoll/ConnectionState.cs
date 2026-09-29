// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Epoll;

// The epoll worker owns either a raw TCP or an fd-bound TLS state machine.
internal abstract class ConnectionState(ulong id)
{
    internal ulong Id { get; } = id;
    internal bool Closed { get; private protected set; }
    internal int Leased { get; private protected set; }
    internal Connection? Application { get; private protected set; }

    internal abstract void Drive(uint ready);
    internal abstract void Send(nint data, int length, bool final);
    internal abstract void ReturnPage(int page);
    internal abstract void RequestClose();
    internal abstract void ResumeForPool();
    internal abstract void Finish(Exception? error);
    internal virtual void CheckDeadline(long now) { }
}
