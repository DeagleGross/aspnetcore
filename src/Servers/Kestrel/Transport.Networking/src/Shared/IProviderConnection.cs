// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Connections;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Shared;

internal interface IProviderConnection : IAsyncDisposable
{
    ConnectionContext Context { get; }
    void Sent(int count, Exception? error = null);
    void Closed(Exception? error);
    void EndReceive(Exception? error);
    void PumpOutput();
    void Abort(ConnectionAbortedException reason);
}
