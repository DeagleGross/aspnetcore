# Bedrock: the public API it needs

This file lists only the **public API surface** that the Bedrock fit adds or depends on, in the same style as [PUBLIC-API.md](PUBLIC-API.md). It does not explain behaviour or results: see [Bedrock.md](Bedrock.md) for how Bedrock works and what it measured, and [Bedrock-History.md](Bedrock-History.md) for how it got there.

Everything here is **experimental and non-shipping**. The types are public only because the projects require a `PublicAPI.Unshipped.txt`. Nothing is proposed for the BCL by this file; it answers the narrower question "what would have to exist for a native transport to sit behind the existing `Microsoft.AspNetCore.Connections` abstractions?".

## What is needed, in one table

| Need | API | Status |
| --- | --- | --- |
| **Required:** an object that Kestrel and an `HttpClient` can use as a transport | `System.Net.Transport.NetworkTransportFactory` | New. The whole integration. |
| **Required:** a way to choose a provider and size it | `NetworkEngineOptions`, `EpollEngineOptions`, `IoUringEngineOptions`, `IocpEngineOptions`, `RioEngineOptions` (+ `WindowsEngineOptions` pool settings) | Prototype types, reused. |
| Optional: the native read lease and send queue behind a `ConnectionContext` | `System.Net.Transport.INetworkConnectionFeature` | New. A consumer that ignores it still works through the pipes. |
| Optional: one-line registration | `AddNetworkTransport(this IServiceCollection, ...)` | New. Convenience. |
| Optional: use the transport for `HttpClient` | `SocketsHttpHandler.UseConnectionFactory(...)` (two overloads) | New. Convenience around the only runtime hook, `ConnectCallback`. |
| Optional: transport-owned TLS on Linux/Windows | `NetworkNativeTlsOptions` | Prototype type, reused. |
| Optional, **measured to matter**: where receive and send continuations run | `NetworkEngineOptions.CompleteReceivesInline`, `NetworkConnection.CompleteReceivesInline`, `NetworkConnection.CompleteSendsInline` | New / prototype. See [Bedrock.md](Bedrock.md#scheduling-is-the-performance-contract). |

Nothing in `Microsoft.AspNetCore.Connections.Abstractions`, Kestrel Core or `System.Net.Http` was changed.

## Existing API used unchanged

```csharp
namespace Microsoft.AspNetCore.Connections
{
    public interface IConnectionListenerFactory { ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default); }
    public interface IConnectionListenerFactorySelector { bool CanBind(EndPoint endpoint); }   // per-endpoint choice between side-by-side transports
    public interface IConnectionFactory { ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default); }
    public abstract class ConnectionContext { /* Transport (IDuplexPipe), Features, ConnectionClosed, Abort */ }
}

namespace System.Net.Http
{
    public sealed class SocketsHttpHandler
    {
        // Returns a Stream: the reason the client side needs an internal pipe-to-Stream adapter.
        public Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? ConnectCallback { get; set; }
    }
}
```

## New API

```csharp
namespace System.Net.Transport
{
    // The whole server and client integration. The options type selects the provider; listeners and outbound connections are created lazily.
    public sealed class NetworkTransportFactory : IConnectionListenerFactory, IConnectionListenerFactorySelector, IConnectionFactory, IAsyncDisposable
    {
        public NetworkTransportFactory(NetworkEngineOptions options, ILoggerFactory? loggerFactory = null, NetworkNativeTlsOptions? nativeTls = null);
        public NetworkEngineKind Kind { get; }
        public bool CanBind(EndPoint endpoint);   // IPv4; on Linux also a fixed non-zero port. Everything else falls through to the next registered factory.
        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default);
        public ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default);   // IPv4 or a DnsEndPoint resolved to IPv4
        public ValueTask DisposeAsync();
    }

    // Optional. Exposes the native connection of a ConnectionContext so a consumer can use retained read leases and the explicit send queue
    // instead of the pipe adapter. Using it directly is exclusive with using ConnectionContext.Transport.
    public interface INetworkConnectionFeature
    {
        NetworkConnection Connection { get; }
    }

    // Optional. Sets SocketsHttpHandler.ConnectCallback. HTTP framing, pooling and TLS stay in the handler.
    public static class NetworkTransportHttpExtensions
    {
        // A connection that exposes INetworkConnectionFeature is read straight from the transport pipe; any other factory gets a 1 MiB read-ahead copy.
        public static SocketsHttpHandler UseConnectionFactory(this SocketsHttpHandler handler, IConnectionFactory factory);
        // Always adapts ConnectionContext.Transport, with the given read-ahead (0 = none).
        public static SocketsHttpHandler UseConnectionFactory(this SocketsHttpHandler handler, IConnectionFactory factory, int receiveBufferBytes);
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    public static class NetworkTransportServiceCollectionExtensions
    {
        // Registers one NetworkTransportFactory as both IConnectionListenerFactory and IConnectionFactory.
        public static IServiceCollection AddNetworkTransport(this IServiceCollection services, NetworkEngineOptions options, NetworkNativeTlsOptions? nativeTls = null);
    }
}
```

Registering the factory is enough for a server:

```csharp
builder.Services.AddNetworkTransport(new EpollEngineOptions { WorkerCount = 8, CompleteReceivesInline = true });
```

and for a client:

```csharp
await using var factory = new NetworkTransportFactory(new RioEngineOptions { WorkerCount = 8 });
using var client = new HttpClient(new SocketsHttpHandler().UseConnectionFactory(factory));
```

Not part of Bedrock: the prototype also ships `IWebHostBuilder` conveniences in the same assembly (`UseNetworkTransport`, `UseEpollTcp`, `UseEpollTls`, `UseIocp`, `UseRio`). They register the private `KestrelFactory` of the earlier prototype and are listed in `src\PublicAPI.Unshipped.txt`; `AddNetworkTransport` above would replace them.

## Prototype API that Bedrock depends on

These already existed in [PUBLIC-API.md](PUBLIC-API.md). Only the members Bedrock uses are repeated, plus the ones this investigation added (marked **new**).

```csharp
namespace System.Net.Transport
{
    public abstract class NetworkEngineOptions
    {
        public int WorkerCount { get; set; }                       // owning worker threads
        public IReadOnlyList<int>? WorkerProcessors { get; set; }  // optional logical processor per worker
        public bool CompleteReceivesInline { get; set; }           // NEW. Default false. See below.
    }

    public sealed class EpollEngineOptions : NetworkEngineOptions { }
    public sealed class IoUringEngineOptions : NetworkEngineOptions { }
    public abstract class WindowsEngineOptions : NetworkEngineOptions
    {
        public int InputPoolMaximumBytes { get; set; }             // per worker, default 256 MiB, 4 MiB..1 GiB
        public int OutputPoolMaximumBytes { get; set; }            // per worker, default 512 MiB, 4 MiB..1 GiB
        public TimeSpan PoolIdleTrimDelay { get; set; }            // default 10 s, Timeout.InfiniteTimeSpan disables trimming
    }
    public sealed class IocpEngineOptions : WindowsEngineOptions { }
    public sealed class RioEngineOptions : WindowsEngineOptions { }

    public sealed class NetworkNativeTlsOptions                    // optional, bound listeners only
    {
        public required string CertificatePath { get; init; }
        public required string PrivateKeyPath { get; init; }
        public Action<ReadOnlySpan<byte>>? HandshakePrefixCallback { get; init; }
    }

    public abstract class NetworkConnection                        // reached through INetworkConnectionFeature
    {
        public bool CompleteSendsInline { get; set; }              // default false: an awaited SendAsync resumes on the thread pool
        public bool CompleteReceivesInline { get; set; }           // NEW. Default false: a pending receive resumes on the thread pool
        // plus the members listed in PUBLIC-API.md: ReceiveAsync, AdvanceReceiveTo, SendAsync, EnqueueSend, AllocateBuffer, AsDuplexPipe, AsStream, Abort, ...
    }
}
```

### `CompleteReceivesInline` (new)

| | |
| --- | --- |
| Meaning | `true`: a pending receive (a `ReceiveAsync`, or a read of the `Transport.Input` pipe that Kestrel uses) is completed on the provider worker that received the bytes, so the consumer's continuation runs on that worker. `false` (default): the continuation is queued to the thread pool. |
| Where | `NetworkEngineOptions.CompleteReceivesInline` is the default for every connection an engine publishes (accepted and connected). `NetworkConnection.CompleteReceivesInline` overrides it per connection. |
| Why it exists | On a 56-core server at 28 workers it moved epoll from 104% to 136% and io_uring from 102% to 146% of the Sockets transport on Linux, and RIO from 121% to 154% and IOCP from 100% to 122% on Windows (against a Sockets transport that is also run with inline scheduling: 122% and 131% on Linux, 123% and 156% on Windows). See [Bedrock.md](Bedrock.md#scheduling-is-the-performance-contract) and [Windows p99](Bedrock.md#windows-server-56-cores) for the latency trade-off. |
| Cost | The continuation shares the worker with every other connection assigned to it. Blocking in it (synchronous I/O, `.Result`) stalls those connections. It is the analogue of `SocketTransportOptions.UnsafePreferInlineScheduling` for the Sockets transport, and it is off by default for the same reason. |
| Closest existing API | `SocketTransportOptions.UnsafePreferInlineScheduling`, and the `CompleteSendsInline` property that already exists on `NetworkConnection`. |

## Configuration that is deliberately not API

Environment variables of the experiment. Each is a switch for a measurement, not a proposal. A real design would turn at most the first three into options.

| Variable | Provider | Meaning |
| --- | --- | --- |
| `NETWORKPROTO_CLIENT_LEASE_BYTES` | all | Client receive window in bytes (default 1 MiB). Plays the part of a kernel receive buffer. |
| `NETWORKPROTO_CLIENT_PAGE_KB`, `NETWORKPROTO_CLIENT_PAGES` | Linux client | Receive page size (default 64 KB) and, for io_uring, pinned pages per client engine (default 128). |
| `NETWORKPROTO_CLIENT_MAX_RECEIVE_BLOCK`, `NETWORKPROTO_IOCP_ZERO_BYTE` | Windows client | Receive block cap (default 256 KB) and an override of the IOCP zero-byte probe. |
| `NETWORKPROTO_COALESCE` | Linux | `0` restores one worker wake-up per command. The default is now to join a pending wake (it was off for servers before 2026-11-12). |
| `NETWORKPROTO_CPUS` | Linux | Explicit worker processors. Equivalent to `WorkerProcessors`. |
| `NETWORKPROTO_DIAG=1` | Linux | Prints per-second counters (benchmark apps and `EpollDiagnostics`). Diagnostics only. |

## Gaps in the existing abstractions

These are the places where the existing contracts cannot express something Bedrock needed. None is solved by API in this experiment.

1. **A receive window.** A client that writes a whole request before it reads needs the transport to keep bytes in flight. Nothing on `ConnectionContext` carries that, so it is a transport-internal byte budget. If it had to be public, it belongs on the factory or engine options, not on `ConnectionContext`.
2. **A zero-copy `Stream`.** `ConnectCallback` returns a `Stream`, so a leased page is copied once into the handler's buffer (see [why `ConnectionContextStream` exists](Bedrock.md#why-connectioncontextstream-exists)). Removing the copy needs a `System.Net.Http` hook that accepts an `IConnectionFactory` or a `ConnectionContext`. That is a runtime API proposal and is out of scope here.
3. **Client-side selection.** There is a `CanBind` for servers and no `CanConnect`. A factory that connects only over IPv4 cannot say so before `ConnectAsync`, and there is no fallback to a sockets factory.
4. **Scheduling policy.** Where a receive or send continuation runs is a first-class performance decision (see `CompleteReceivesInline`, `CompleteSendsInline`). `IConnectionListenerFactory` has no place for it, so it lives on the engine options.
