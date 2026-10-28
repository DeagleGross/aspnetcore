// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.Windows;

/// <summary>
/// The per-connection TLS contract <see cref="ConnectionState"/> drives. The worker thread owns every call except an initial handshake job.
/// </summary>
internal interface IWindowsTlsConnection : IDisposable
{
    bool Authenticated { get; }
    bool HandshakePending { get; }
    int BufferedCiphertext { get; }
    Memory<byte> GetReceiveMemory();
    void Received(int count);
    void ResumeInput();
    void HandshakeCompleted();
    void CancelHandshake();
    void Send(ReadOnlyMemory<byte> plaintext, bool final);
    void Sent(Exception? error);
    void Shutdown();
    void PeerEof();
    void NativeClosed(Exception? error);
}
