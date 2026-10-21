```csharp
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace System.Net.Transport
{
    public enum NetworkEngineKind
    {
        Epoll = 0,
        IoUring = 1,
        Iocp = 2,
        Rio = 3
    }

    public abstract class NetworkEngineOptions
    {
        public int WorkerCount { get; set; }
        public IReadOnlyList<int>? WorkerProcessors { get; set; }
    }

    public sealed class EpollEngineOptions : NetworkEngineOptions
    {
        public EpollEngineOptions();
    }

    public sealed class IoUringEngineOptions : NetworkEngineOptions
    {
        public IoUringEngineOptions();
    }

    public sealed class IocpEngineOptions : NetworkEngineOptions
    {
        public IocpEngineOptions();
    }

    public sealed class RioEngineOptions : NetworkEngineOptions
    {
        public RioEngineOptions();
    }

    public sealed class NetworkListenOptions
    {
        public NetworkListenOptions();
        public NetworkNativeTlsOptions? NativeTls { get; set; }
    }

    public sealed class NetworkNativeTlsOptions
    {
        public NetworkNativeTlsOptions();
        public required string CertificatePath { get; init; }
        public required string PrivateKeyPath { get; init; }
        public Action<ReadOnlySpan<byte>>? HandshakePrefixCallback { get; init; }
    }

    public static class NetworkEngineFactory
    {
        public static NetworkEngine Create(NetworkEngineOptions options, ILoggerFactory? loggerFactory = null);
    }

    public abstract class NetworkEngine : IAsyncDisposable
    {
        public abstract NetworkEngineKind Kind { get; }
        public abstract ValueTask<NetworkListener> ListenAsync(EndPoint endpoint, NetworkListenOptions? options = null, CancellationToken cancellationToken = default);
        public abstract ValueTask DisposeAsync();
    }

    public abstract class NetworkListener : IAsyncDisposable
    {
        public abstract EndPoint LocalEndPoint { get; }
        public abstract ValueTask<NetworkConnection?> AcceptAsync(CancellationToken cancellationToken = default);
        public abstract ValueTask UnbindAsync(CancellationToken cancellationToken = default);
        public abstract ValueTask DisposeAsync();
    }

    public abstract class NetworkConnection : IAsyncDisposable
    {
        public string Id { get; }
        public EndPoint? LocalEndPoint { get; }
        public EndPoint? RemoteEndPoint { get; }
        public bool IsTls { get; }
        public NetworkTlsInfo? TlsInfo { get; }
        public CancellationToken ConnectionClosed { get; }
        public long MaxQueuedSendBytes { get; set; }
        public PipeWriter Output { get; }

        public ValueTask<NetworkReceiveResult> ReceiveAsync(CancellationToken cancellationToken = default);
        public void AdvanceReceiveTo(SequencePosition consumed);
        public void AdvanceReceiveTo(SequencePosition consumed, SequencePosition examined);
        public NetworkBuffer AllocateBuffer(int minimumSize = 0);
        public ValueTask SendAsync(ReadOnlyMemory<byte> data);
        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
        public ValueTask SendAsync(ReadOnlySequence<byte> data);
        public ValueTask SendAsync(ReadOnlySequence<byte> data, CancellationToken cancellationToken);
        public void EnqueueSend(NetworkBufferSlice data);
        public void EnqueueSend(NetworkPayload data);
        public IDuplexPipe AsDuplexPipe();
        public Stream AsStream();
        public void Abort(Exception? reason = null);
        public ValueTask DisposeAsync();
    }

    public sealed class NetworkTlsInfo
    {
        public NetworkTlsInfo(System.Security.Authentication.SslProtocols protocol, TlsCipherSuite? cipherSuite);
        public System.Security.Authentication.SslProtocols Protocol { get; }
        public TlsCipherSuite? CipherSuite { get; }
    }

    public readonly struct NetworkReceiveResult
    {
        public NetworkReceiveResult();
        public ReadOnlySequence<byte> Buffer { get; }
        public bool IsCompleted { get; }
        public bool IsCanceled { get; }
        public NetworkPayload Retain(long start, long length);
    }

    public enum NetworkMemoryKind
    {
        Managed = 0,
        Pinned = 1,
        Native = 2,
        Registered = 3
    }

    public sealed class NetworkBuffer : IDisposable
    {
        public Memory<byte> Memory { get; }
        public NetworkBufferSlice Slice { get; }
        public NetworkMemoryKind Kind { get; }
        public static NetworkBuffer TakeOwnership(IMemoryOwner<byte> owner, NetworkMemoryKind kind);
        public void Dispose();
    }

    public readonly struct NetworkBufferSlice
    {
        public NetworkBufferSlice();
        public int Length { get; }
        public ReadOnlyMemory<byte> Memory { get; }
        public NetworkMemoryKind Kind { get; }
        public NetworkBufferSlice Slice(int start, int length);
        public NetworkBuffer Retain();
    }

    public sealed class NetworkPayload : IDisposable
    {
        public ReadOnlySequence<byte> Sequence { get; }
        public NetworkPayload Retain();
        public void Dispose();
    }

    public static class NetworkTlsExtensions
    {
        public static ValueTask<SslStream> AuthenticateAsServerAsync(this NetworkConnection connection, SslServerAuthenticationOptions options, CancellationToken cancellationToken = default);
    }
}

namespace Microsoft.AspNetCore.Hosting
{
    public static class SharedNetworkTransportExtensions
    {
        public static IWebHostBuilder UseNetworkTransport(this IWebHostBuilder builder, System.Net.Transport.NetworkEngineOptions options, System.Net.Transport.NetworkNativeTlsOptions? nativeTls = null);
    }

    public static class EpollTransportExtensions
    {
        public static IWebHostBuilder UseEpollTcp(this IWebHostBuilder builder, int workerCount = 4);
        public static IWebHostBuilder UseEpollTls(this IWebHostBuilder builder, string certificatePath, string keyPath, int workerCount = 4);
    }

    public static class OwnedTransportExtensions
    {
        public static IWebHostBuilder UseIoUringTcp(this IWebHostBuilder builder);
        public static IWebHostBuilder UseIoUringTls(this IWebHostBuilder builder, string certificatePath, string keyPath);
    }

    public static class LayeredTransportExtensions
    {
        public static IWebHostBuilder UseIoUringBio(this IWebHostBuilder builder, bool tls, string certificatePath, string keyPath);
    }

    public static class WebHostBuilderIoUringExtensions
    {
        public static IWebHostBuilder UseIoUring(this IWebHostBuilder builder);
    }

    public static class WindowsTransportExtensions
    {
        public static IWebHostBuilder UseIocp(this IWebHostBuilder builder, int workerCount = 4);
        public static IWebHostBuilder UseRio(this IWebHostBuilder builder, int workerCount = 4);
    }
}
```
