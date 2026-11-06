# Fitting the prototype under the Connections abstractions (Project Bedrock)

This document records an experiment on branch `dmkorolev/project-bedrock`. It asks two questions. First, can the prototype providers (`Iocp`, `Rio`, and the Linux providers) be reached through the existing `Microsoft.AspNetCore.Connections` interfaces instead of through a dedicated engine, factory and bind API? Second, can the same providers act as the connect side of an `HttpClient`, in the spirit of [dotnet/aspnetcore#4772](https://github.com/dotnet/aspnetcore/issues/4772) (Project Bedrock), so that a transport other than `Socket` carries `SocketsHttpHandler` traffic?

It is an experiment, not an API proposal. The new public types are non-shipping and are listed in `src\PublicAPI.Unshipped.txt` only because the project requires it. Every number was measured on one development machine and was not independently reproduced. It is a companion to [PROTOTYPE.md](PROTOTYPE.md), which describes the provider model, and to [MemoryFootprint.md](MemoryFootprint.md).

## Summary

| Question | Result |
| --- | --- |
| Can the server side hide behind `IConnectionListenerFactory`? | Yes. One class, `NetworkTransportFactory`, implements it together with the existing `IConnectionListenerFactorySelector`. The options type (`IocpEngineOptions`, `RioEngineOptions`, ...) chooses the provider. |
| Is the Bedrock server as fast as the former private-factory prototype? | Yes, within noise. Over three repetitions the two registrations differ by 0-4% in either direction, and the order flips between repetitions. Both stay ahead of the Sockets transport: RIO by about 19-44% and IOCP by about 9-24%. |
| Can the client side hide behind `IConnectionFactory`? | Yes, for IPv4 on Windows. The same class implements `IConnectionFactory`, and `UseConnectionFactory` plugs it into `SocketsHttpHandler.ConnectCallback`. Epoll and io_uring have no client path yet. |
| Is the Bedrock client faster than `SocketsHttpHandler`? | At 8 KB and 64 KB, yes: RIO is about +13% and +8% (ahead in 6 of 6 repetitions), IOCP about +6% and +3.5%. At 1 KB it is at parity (IOCP -2%, RIO 0%), which is noise-dominated. Before the changes below the client was at parity at 1-8 KB and 31-43% slower at 64 KB. |
| What does the client cost? | Memory. RIO registers its slabs, so the client working set is about 140 MB at 1-8 KB and 230 MB at 64 KB against about 65-70 MB for `SocketsHttpHandler`. IOCP is close to Sockets (75-95 MB), but its p99 latency is worse. |
| Is anything lost by going through the interfaces? | The zero-copy read lease. A `ConnectionContext` exposes `PipeReader` and `PipeWriter`, so the native page lease is hidden unless the consumer asks for `INetworkConnectionFeature`. A `Stream` for `ConnectCallback` is a pipe adapter and copies once. |

## Necessary new API over the existing one

The smallest thing that makes Bedrock happen is one class. Nothing in `Microsoft.AspNetCore.Connections.Abstractions` or in Kestrel Core was changed (`git diff --stat origin/main...HEAD` over `src\Servers\Connections.Abstractions` and `src\Servers\Kestrel\Core` is empty), and no change to `System.Net.Http` was made.

### Existing API that is used unchanged

| API | Role in Bedrock |
| --- | --- |
| `IConnectionListenerFactory.BindAsync` | Server bind. The native transport is a drop-in implementation. |
| `IConnectionListenerFactorySelector.CanBind(EndPoint)` | Per-endpoint selection between side-by-side transports. It already exists, added with side-by-side transports in [dotnet/aspnetcore#44657](https://github.com/dotnet/aspnetcore/pull/44657). The transport only implements it. An earlier draft of this document said the selector had to be added, which was wrong. |
| `IConnectionFactory.ConnectAsync(EndPoint)` | Client connect. The same class implements it. |
| `ConnectionContext`, `IDuplexPipe Transport`, `IFeatureCollection Features` | The connection that both sides use. The native details are exposed as a feature. |
| `SocketsHttpHandler.ConnectCallback` | The only hook in `System.Net.Http` that supplies a connection. It returns a `Stream`. |
| Kestrel factory selection | `KestrelServerImpl` reverses the registered factories, so the last registration is tried first. `TransportManager.BindAsync` takes the first whose `CanBind` is true. |

### New public API (non-shipping)

| API | Required? | Why it exists |
| --- | --- | --- |
| `System.Net.Transport.NetworkTransportFactory` implements `IConnectionListenerFactory`, `IConnectionListenerFactorySelector`, `IConnectionFactory`, `IAsyncDisposable`. Constructor `(NetworkEngineOptions, ILoggerFactory? = null, NetworkNativeTlsOptions? = null)` and a `Kind` property. | **Yes.** This is the whole server integration. | It adapts a prototype engine to the existing interfaces. The options type is the transport selector, so there is no separate "transport kind" setting. |
| `System.Net.Transport.INetworkConnectionFeature` with `NetworkConnection Connection { get; }` | No. Optional optimization. | The zero-copy read lease and the explicit send queue are the reason a native transport can be faster, and a `ConnectionContext` pipe hides them. A consumer that does not know the feature still works through the pipes. |
| `Microsoft.Extensions.DependencyInjection.NetworkTransportServiceCollectionExtensions.AddNetworkTransport(IServiceCollection, NetworkEngineOptions, NetworkNativeTlsOptions? = null)` | No. Convenience. | Registers one singleton as both `IConnectionListenerFactory` and `IConnectionFactory`. Without it, a consumer registers the factory itself. |
| `System.Net.Transport.NetworkTransportHttpExtensions.UseConnectionFactory(SocketsHttpHandler, IConnectionFactory)` and `(..., int receiveBufferBytes)` | No. Convenience for the experiment. | Sets `ConnectCallback`. The real work is the internal `ConnectionContextStream` that adapts a duplex pipe to a `Stream`. |

The prototype options types (`NetworkEngineOptions` and its derived types) and `NetworkNativeTlsOptions` already existed and are reused, so they are not Bedrock additions. Worker count, affinity, pool caps and trimming are provider specific and have to be passed somewhere, so they stay public.

Server usage with the new API:

```csharp
builder.Services.AddNetworkTransport(new RioEngineOptions { WorkerCount = 4 });
```

Client usage:

```csharp
await using var factory = new NetworkTransportFactory(new RioEngineOptions { WorkerCount = 8 });
using var client = new HttpClient(new SocketsHttpHandler().UseConnectionFactory(factory));
```

### Internal or configuration only, not API

- `ConnectionContextStream` (the pipe-to-`Stream` adapter) is internal.
- Environment variables read by the Windows worker (`WindowsWorker.cs`): `NETWORKPROTO_CLIENT_LEASE_BYTES` (client receive window, default 1 MiB), `NETWORKPROTO_CLIENT_MAX_RECEIVE_BLOCK` (client receive block cap, default 256 KB) and `NETWORKPROTO_IOCP_ZERO_BYTE` (override of the IOCP zero-byte probe). They are tuning knobs for the experiment. A real design would turn at most one of them into an option.
- The private `KestrelFactory` in `src\Shared\Registration.cs` could be removed: `UseNetworkTransport` could register `NetworkTransportFactory` instead. The server benchmark below shows the two perform the same.

### What the existing abstractions cannot express

These are the places where something new would be needed for more than an experiment. None is implemented here.

1. **A receive window.** The client needs the transport to keep some bytes in flight for a consumer that writes before it reads. Nothing in `ConnectionContext` carries that. Here it is a transport-internal byte budget configured by an environment variable. If it had to be public, it belongs on the factory or engine options, not on `ConnectionContext`.
2. **A zero-copy `Stream`.** `ConnectCallback` returns a `Stream`, so the native lease is converted to a pipe and then to a `Stream`. Removing that copy needs a runtime change: a handler hook that accepts an `IConnectionFactory` or a `ConnectionContext`. That is a `System.Net.Http` API proposal and is outside this experiment.
3. **Client-side selection.** There is a selector for binding but no equivalent for connecting. A factory that supports only IPv4 on Windows cannot say so before `ConnectAsync`, and there is no fallback to a sockets factory. A composite factory would be needed, or a `CanConnect` counterpart.
4. **Engine lifetime and worker sizing for clients.** A server owns its workers for the life of the host. A client factory is created ad hoc, and its worker count has no default that fits every machine (see the worker sweep). This is configuration and ownership policy, not a missing interface.

Implementation work that is not API: `ConnectEx` in place of a blocking `connect`, IPv6 and DNS-aware connect, and an epoll or io_uring client path.

## The server side

`NetworkTransportFactory` is the whole server integration. Kestrel selects the factory per endpoint. `CanBind` accepts an `IPEndPoint` (IPv4 only on Windows), so an IPv6, DNS or Unix endpoint falls through to the default sockets factory in the same process. The check run covers this: an IPv6 listener fell through to the default factory.

## The client side

`SocketsHttpHandler.ConnectCallback` returns a `Stream`, and TLS and HTTP framing stay in the handler. `ConnectAsync` resolves a `DnsEndPoint` to the first IPv4 address and connects through the provider. On Windows the `WindowsClient` runs a blocking `connect` on the thread pool and then hands the socket to a worker, which registers it with the completion port or the request queue. The connection is wrapped in a `KestrelConnectionContext`, and the `ConnectionContextStream` adapts its pipe to a `Stream`. The Epoll and IoUring providers throw `NotSupportedException` for a client connection.

### Why the first client was slow at 64 KB

The first client was on par with `SocketsHttpHandler` at 1-8 KB and clearly slower at 64 KB (IOCP 69%, RIO 57% of Sockets). Reading the receive path (`ConnectionState.cs`, `WindowsWorker.cs`, `NetworkInputReader`) points to per-page costs that a server hides, because a server request is small:

- A 64 KB response arrived as four 16 KB completions, and every completion woke the consumer through a thread-pool hop (`RunContinuationsAsynchronously`).
- Each returned page was a cross-thread command to the worker, followed by a re-arm of the receive.
- IOCP submitted a zero-byte probe plus a real receive for every page, so each page cost two completions.
- A 1 MiB read-ahead pipe copied every byte once more, to work around the stall described below.

### The receive stall, and the byte budget

HTTP/1.1 in `SocketsHttpHandler` writes the whole request body before it reads the response (`HttpConnection.SendAsync` in `src\libraries\System.Net.Http\src\System\Net\Http\SocketsHttpHandler\HttpConnection.cs`). An echoing server blocks on its send if the client stops receiving, so the client needs a receive window of about the request size minus the server's own buffering. A socket gets that from the kernel receive buffer. The providers instead stop receiving once four 16 KB pages are leased and unconsumed (about 512 KB for IOCP and 256 KB for RIO at the stall). Kestrel never hits this because it reads continuously.

The first fix was a read-ahead copy in the stream adapter, which cost the extra copy above. The current fix is in the transport, for client connections only (everything below is gated on `_listener.Client`, so the server path is unchanged):

- **A byte budget instead of a page count.** `ConnectionState.AtLeaseLimit` for a client is `_leasedBytes >= ClientLeaseBytes` (1 MiB of bytes actually received). A FIFO `Queue<int> _leasedCounts` tracks the bytes of each leased page, so pages that arrive partly filled do not exhaust the budget. An earlier version counted block sizes instead of received bytes, and a RIO 512 KB echo stalled until that was fixed.
- **Adaptive receive blocks.** The block rented for the next receive starts at 16 KB and doubles, up to a 256 KB cap, each time the previous receive filled its block. A 64 KB response arrives in one or two completions instead of four.
- **No IOCP zero-byte probe on the client.** The probe exists to avoid pinning pool memory for idle connections, which matters for a server with many idle connections. A client connection is nearly always waiting for a response, so the probe was pure overhead. It stays on for the server. `NETWORKPROTO_IOCP_ZERO_BYTE` overrides it.
- **No read-ahead for native connections.** The 2-argument `UseConnectionFactory` reads straight from the pipe when the connection exposes `INetworkConnectionFeature`, and keeps the 1 MiB read-ahead for any other `IConnectionFactory`. The 3-argument overload still forces a read-ahead.

Dropped experiment: a direct `NetworkConnectionStream` that skipped the pipe adapter. In a three-repetition A/B (`bedrock-client-final`) it was slightly worse than the pipe adapter without read-ahead in nearly every cell. As a share of Sockets at 1, 8 and 64 KB, the direct stream gave IOCP 89%, 106% and 102% and RIO 93%, 111% and 100%, and the pipe adapter gave IOCP 91%, 109% and 103% and RIO 97%, 120% and 104%. The direct stream was removed.

The runtime was not changed. A runtime change (concurrent write and read in `HttpConnection`, or a handler hook that takes a `ConnectionContext`) would remove both the stall and the adapter.

## Correctness checks

`samples\Bedrock` has a `check` mode. Its last run passed all of these, for IOCP and RIO:

- The `CanBind` contract: IPv4 yes, DNS and Unix (and IPv6 on Windows) fall through.
- A 262,161-byte echo round trip through `IConnectionListenerFactory` and `IConnectionFactory`.
- A connect to a closed port fails fast with a `SocketException`.
- Kestrel plus `HttpClient`: GET keep-alive and POST echoes of 1 KiB to 1 MiB, through the default overload, which has no read-ahead for native connections.
- A 1 MiB+3 byte POST echo through the explicit `UseConnectionFactory(factory, receiveBufferBytes: 1024 * 1024)` overload.
- The IPv6 fall-through.

## Benchmarks

All runs use the `samples\Bedrock` app (`serve` and `bench` modes) driven by `samples\Bedrock\run-bench.ps1`. The machine is a Ryzen 9 7950X3D (16 cores, 32 logical) on Windows 11, with client and server on loopback and pinned to disjoint logical CPUs. Each cell is 64 connections, 2 s warmup and 8 s measured. HTTP/1.1, the request body is echoed, and the body size is the number in the first column. All cells had 0 errors. `registration` `bedrock` means `AddNetworkTransport`, `legacy` means `UseNetworkTransport` with the private factory. CPU is the process CPU time in ms per 1,000 requests. Raw CSV files are in a local results folder and are not committed.

### Server: the Kestrel transport through the abstractions

A plain `SocketsHttpHandler` client on CPUs 16-31, Kestrel on CPUs 0-3 with 4 workers. Three repetitions per cell, rows are the average. Requests per second, then p99 latency in µs:

| Body | Sockets | IOCP (Bedrock) | IOCP (legacy) | RIO (Bedrock) | RIO (legacy) |
| --- | --- | --- | --- | --- | --- |
| 1 KB | 84.4k, 1484 | 91.6k, 2651 | 92.9k, 2781 | 101.2k, 1374 | 105.1k, 1256 |
| 8 KB | 76.3k, 1653 | 87.0k, 1985 | 87.2k, 2198 | 90.8k, 1387 | 90.5k, 1480 |
| 64 KB | 45.4k, 14055 | 56.4k, 2566 | 57.5k, 2366 | 65.4k, 1766 | 64.9k, 1830 |

- **Verdict: the Bedrock server is on par with the former prototype.** The Bedrock-to-legacy difference is -1.3%, -0.2% and -1.9% for IOCP and -3.7%, +0.3% and +0.8% for RIO at 1, 8 and 64 KB. Within each cell the order flips between repetitions (RIO at 8 KB: Bedrock ahead by 11% in repetition 2 and behind by 7% in repetition 3). The RIO 1 KB gap is the largest and comes mostly from one repetition (95.8k against 107.3k). Three repetitions cannot separate a 1-4% cost from noise.
- Over Sockets, RIO is +20%, +19% and +44% at 1, 8 and 64 KB, and IOCP is +9%, +14% and +24%.
- CPU per 1,000 requests: Sockets 75-98, IOCP 69-89, RIO 65-81.
- Sockets has a 14 ms p99 at 64 KB against 2-3 ms for the native providers. The cause was not investigated.
- Server working set: 61-73 MB for Sockets, 64-72 MB for IOCP and 96-113 MB for RIO. The RIO figure is registered slabs ([MemoryFootprint.md](MemoryFootprint.md)).

### Client: `SocketsHttpHandler` against the Bedrock connection factory

A Kestrel server on the Sockets transport (CPUs 16-31). The client is on CPUs 0-7, with 8 client workers for the Bedrock providers, through the default `UseConnectionFactory` overload. Three repetitions, rows are the average, "wins" is the number of repetitions in which the provider beat Sockets in the same repetition:

| Body | Client | Requests per second | vs Sockets | wins | p50 µs | p99 µs | CPU ms/1k | Working set |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 KB | SocketsHttpHandler | 140.7k | 100% | | 237 | 2971 | 47.0 | 71 MB |
| 1 KB | IOCP | 137.5k | 98% | 1/3 | 305 | 4277 | 49.7 | 79 MB |
| 1 KB | RIO | 140.6k | 100% | 2/3 | 328 | 1910 | 46.5 | 141 MB |
| 8 KB | SocketsHttpHandler | 122.4k | 100% | | 301 | 2677 | 54.0 | 67 MB |
| 8 KB | IOCP | 130.2k | 106% | 3/3 | 292 | 4867 | 50.0 | 75 MB |
| 8 KB | RIO | 138.2k | 113% | 3/3 | 327 | 2038 | 47.3 | 141 MB |
| 64 KB | SocketsHttpHandler | 103.0k | 100% | | 451 | 3326 | 63.6 | 64 MB |
| 64 KB | IOCP | 106.6k | 103% | 2/3 | 355 | 5525 | 61.5 | 95 MB |
| 64 KB | RIO | 111.1k | 108% | 3/3 | 411 | 2648 | 60.1 | 231 MB |

- RIO is ahead at 8 KB and 64 KB in every repetition, and uses slightly less CPU per request than `SocketsHttpHandler`. IOCP is ahead at 8 KB in every repetition and at 64 KB in two of three.
- At 1 KB there is no win. The Sockets repetitions alone range from 131k to 146k, which is as large as the gap, and p50 is higher for both providers.
- RIO has the lower p99 at every size. IOCP has the higher p99 at every size (4-6 ms against 2.7-3.3 ms). The cause was not investigated.
- The earlier three-repetition A/B (`bedrock-client-final`, pipe adapter without read-ahead) gave IOCP 91%, 109% and 103% and RIO 97%, 120% and 104% of Sockets at 1, 8 and 64 KB. It agrees in direction, so the 8 KB and 64 KB wins do not come from one lucky batch. The magnitudes differ because Sockets alone varies by 10-20% between batches.

Before and after, as a share of Sockets measured in the same run (the before numbers are from two repetitions of the earlier code, with the 1 MiB read-ahead and 16 KB blocks):

| Body | IOCP before | IOCP now | RIO before | RIO now |
| --- | --- | --- | --- | --- |
| 1 KB | 96% | 98% | 105% | 100% |
| 8 KB | 96% | 106% | 105% | 113% |
| 64 KB | 69% | 103% | 57% | 108% |

The 1 KB row did not improve and RIO's slightly lower value is within noise. The 64 KB row is where the receive-path work paid off.

### Client: worker count

A Bedrock client does not use the thread pool for I/O. It has a fixed number of engine workers, each owning its connections. Throughput at 1 KB and 64 connections, from one run per cell. This sweep was run with the earlier client code and was not repeated:

| Workers | IOCP | RIO | SocketsHttpHandler |
| --- | --- | --- | --- |
| 1 | 27.4k | 31.7k | 128.2k |
| 2 | 51.4k | 58.8k | 121.7k |
| 4 | 93.8k | 108.9k | 129.9k |
| 6 | 135.7k | 141.2k | 142.6k |
| 8 | 137.6k | 139.6k | 142.5k |

Throughput scales about linearly with workers, at about 27k requests per second per IOCP worker and about 30k per RIO worker, and reaches parity with `SocketsHttpHandler` at 6-8 workers. A Bedrock client has to size its workers (about the CPU count that the client may use) and place them on distinct cores, while `SocketsHttpHandler` scales with the thread pool. RIO client memory also grows with the worker count, because each worker registers its own slab (about 68 MB at 1 worker to about 140 MB at 8). A client that runs below the worker count it needs is far slower than Sockets, so the default matters.

## What this shows

- **The server side fits, at no measurable cost.** One factory class that implements the existing interfaces is enough, and it matches the private factory within noise.
- **The client side fits mechanically and can be faster.** `IConnectionFactory` plus `ConnectCallback` carries real HTTP/1.1 traffic including 1 MiB uploads. RIO beats `SocketsHttpHandler` at 8 KB and 64 KB, and IOCP at 8 KB, on this machine. It does not beat it at 1 KB.
- **The price is memory and tuning.** RIO uses about two to three times the memory of `SocketsHttpHandler` on the client, IOCP has the worse p99, and both need the worker count chosen by the application.
- **The zero-copy path still needs a new contract.** The gains above came from the transport's own receive window and block sizing, not from removing the `Stream` copy. A runtime hook that passes a `ConnectionContext` would remove that copy, and it would require a `System.Net.Http` API proposal.

## What a real runtime integration would still need

- A handler hook that accepts an `IConnectionFactory` or `ConnectionContext`, instead of a `ConnectCallback` that returns a `Stream`. The runtime was not changed for this experiment.
- `ConnectEx` instead of a blocking `connect` on the thread pool, which also keeps connect off the thread pool under load.
- IPv6, and a DNS-aware connect, with a fallback to the sockets factory.
- A client path for the Linux providers.
- A decision about worker ownership: a process-wide client engine with a few workers, with the sizing treated as an application concern.
- Measurements on more than one machine, with more than three repetitions, against the runtime's own `SocketsHttpHandler` on the same runtime build. A runtime fork was not used for this experiment.

## Limits of these measurements

- One machine, loopback, three repetitions per cell (one for the worker sweep). Differences of a few percent are noise, and the Sockets baseline varies by 10-20% between batches.
- The server in the client plan is the Sockets transport. The Bedrock client was not measured against a Bedrock server (`run-bench.ps1 -Plan both` was not run).
- The client is Windows-only and IPv4-only. The Linux providers were not measured, and a WSL `check` run was not done.
- No TLS runs: TLS stays in `SocketsHttpHandler` for the client and in Kestrel or the native providers for the server, and the numbers above are plain HTTP.
- The client wins depend on the receive window (1 MiB) and the 256 KB receive block cap. The RIO block cap experiment showed that 64 KB blocks gave no consistent win and 256 KB blocks gave RIO +8% at 64 KB, at about +30 MB of working set.
- Latency percentiles are from one load generator at a fixed 64 connections. They are not a latency-focused benchmark.
