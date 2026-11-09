# Fitting the prototype under the Connections abstractions (Project Bedrock)

This document records an experiment on branch `dmkorolev/project-bedrock`. It asks two questions. First, can the prototype providers (`Iocp`, `Rio`, and the Linux providers) be reached through the existing `Microsoft.AspNetCore.Connections` interfaces instead of through a dedicated engine, factory and bind API? Second, can the same providers act as the connect side of an `HttpClient`, in the spirit of [dotnet/aspnetcore#4772](https://github.com/dotnet/aspnetcore/issues/4772) (Project Bedrock), so that a transport other than `Socket` carries `SocketsHttpHandler` traffic?

It is an experiment, not an API proposal. The new public types are non-shipping and are listed in `src\PublicAPI.Unshipped.txt` only because the project requires it. Every number was measured on one development machine and was not independently reproduced. It is a companion to [PROTOTYPE.md](PROTOTYPE.md), which describes the provider model, and to [MemoryFootprint.md](MemoryFootprint.md).

## Summary

| Question | Result |
| --- | --- |
| Can the server side hide behind `IConnectionListenerFactory`? | Yes. One class, `NetworkTransportFactory`, implements it together with the existing `IConnectionListenerFactorySelector`. The options type (`IocpEngineOptions`, `RioEngineOptions`, ...) chooses the provider. |
| Is the Bedrock server as fast as the former private-factory prototype? | Yes, within noise. Over three repetitions the two registrations differ by 0-4% in either direction, and the order flips between repetitions. Both stay ahead of the Sockets transport: RIO by about 19-44% and IOCP by about 9-24%. |
| Can the client side hide behind `IConnectionFactory`? | Yes, for IPv4, on all four providers. The same class implements `IConnectionFactory`, and `UseConnectionFactory` plugs it into `SocketsHttpHandler.ConnectCallback`. Epoll and io_uring got a client path in the Linux follow-up below. |
| Is the Bedrock client faster than `SocketsHttpHandler`? | At 8 KB and 64 KB, yes: RIO is about +13% and +8% (ahead in 6 of 6 repetitions), IOCP about +6% and +3.5%. At 1 KB it is at parity (IOCP -2%, RIO 0%), which is noise-dominated. Before the changes below the client was at parity at 1-8 KB and 31-43% slower at 64 KB. |
| Does the Windows advantage carry over to Linux? | **On the server, yes for io_uring, and for epoll at a small core count. On the client, no.** With matched CPUs and workers and a native `wrk` load generator (1 KB, 1,200 connections, 4 repetitions), the Bedrock io_uring server is about 18% ahead of the Sockets server at both 4 and 8 logical CPUs and has a lower p99. Epoll is about 6-8% ahead at 4 CPUs and at parity at 8. An earlier "epoll about 82%, io_uring about 73% of Sockets" result was a harness artifact (Sockets had 8 CPUs, the native providers 4 workers, and the load generator was a .NET `HttpClient`); it is kept in the Linux section for the record. The Bedrock adapter itself is within about 4% of the former private-factory server in five of six cells, and io_uring at 1 KB is about 8% lower in both repetitions of the old harness. The Linux clients are near parity with `SocketsHttpHandler` at 1-8 KB (epoll 99-106%, io_uring 89-105% per repetition) and slower at 64 KB (epoll about 85-95%, io_uring about 75-80%), with higher CPU per request and a higher p99. See [Linux: epoll and io_uring under Bedrock](#linux-epoll-and-io_uring-under-bedrock). |
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
- Environment variables read by the Linux providers (`EpollWorker.cs`, `TcpConnection.cs`, `Engine.cs`, `RingConnection.cs`), client mode only unless noted: `NETWORKPROTO_CLIENT_PAGE_KB` (receive page size, default 64), `NETWORKPROTO_CLIENT_PAGES` (io_uring only, pages pinned per client engine, default 128), `NETWORKPROTO_CLIENT_LEASE_BYTES` (receive window, default 1 MiB, the same name the Windows worker reads) and `NETWORKPROTO_COALESCE` (`1` or `0`, joins pending worker wakes; defaults to on for clients and off for servers).
- The private `KestrelFactory` in `src\Shared\Registration.cs` could be removed: `UseNetworkTransport` could register `NetworkTransportFactory` instead. The server benchmark below shows the two perform the same.

### What the existing abstractions cannot express

These are the places where something new would be needed for more than an experiment. None is implemented here.

1. **A receive window.** The client needs the transport to keep some bytes in flight for a consumer that writes before it reads. Nothing in `ConnectionContext` carries that. Here it is a transport-internal byte budget configured by an environment variable. If it had to be public, it belongs on the factory or engine options, not on `ConnectionContext`.
2. **A zero-copy `Stream`.** `ConnectCallback` returns a `Stream`, so the native lease is converted to a pipe and then to a `Stream`. Removing that copy needs a runtime change: a handler hook that accepts an `IConnectionFactory` or a `ConnectionContext`. That is a `System.Net.Http` API proposal and is outside this experiment.
3. **Client-side selection.** There is a selector for binding but no equivalent for connecting. A factory whose providers connect only over IPv4 cannot say so before `ConnectAsync`, and there is no fallback to a sockets factory. A composite factory would be needed, or a `CanConnect` counterpart.
4. **Engine lifetime and worker sizing for clients.** A server owns its workers for the life of the host. A client factory is created ad hoc, and its worker count has no default that fits every machine (see the worker sweep). This is configuration and ownership policy, not a missing interface.

Implementation work that is not API: `ConnectEx` in place of a blocking `connect`, and IPv6 and DNS-aware connect. The epoll and io_uring client paths are implemented in the Linux follow-up.

## The server side

`NetworkTransportFactory` is the whole server integration. Kestrel selects the factory per endpoint. `CanBind` accepts an IPv4 `IPEndPoint` (on Linux also only with a non-zero port), so an IPv6, DNS or Unix endpoint falls through to the default sockets factory in the same process. The check run covers this: an IPv6 listener fell through to the default factory.

The Linux follow-up found that `CanBind` had been accepting any `IPEndPoint` on Linux. Kestrel's IPv6 loopback listener then went to the epoll or io_uring factory, which threw `NotSupportedException`. The rule is now the same on both platforms, apart from the port-0 case.

## The client side

`SocketsHttpHandler.ConnectCallback` returns a `Stream`, and TLS and HTTP framing stay in the handler. `ConnectAsync` resolves a `DnsEndPoint` to the first IPv4 address and connects through the provider. On Windows the `WindowsClient` runs a blocking `connect` on the thread pool and then hands the socket to a worker, which registers it with the completion port or the request queue. The connection is wrapped in a `KestrelConnectionContext`, and the `ConnectionContextStream` adapts its pipe to a `Stream`. The Linux providers have their own client paths, described in the Linux section.

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

The Windows runs passed all of these for IOCP and RIO, and the Linux runs (WSL2) passed the same list for epoll and io_uring, after the Linux client work:

- The `CanBind` contract: IPv4 yes, DNS, Unix and IPv6 (and port 0 on Linux) fall through.
- A 262,161-byte echo round trip through `IConnectionListenerFactory` and `IConnectionFactory`.
- A connect to a closed port fails fast with a `SocketException`.
- Kestrel plus `HttpClient`: GET keep-alive and POST echoes of 1 KiB to 1 MiB, through the default overload, which has no read-ahead for native connections.
- A 1 MiB+3 byte POST echo through the explicit `UseConnectionFactory(factory, receiveBufferBytes: 1024 * 1024)` overload.
- The IPv6 fall-through.

## Benchmarks

The first three subsections are Windows (IOCP and RIO). The Linux results follow in [Linux: epoll and io_uring under Bedrock](#linux-epoll-and-io_uring-under-bedrock).

All Windows runs use the `samples\Bedrock` app (`serve` and `bench` modes) driven by `samples\Bedrock\run-bench.ps1`. The machine is a Ryzen 9 7950X3D (16 cores, 32 logical) on Windows 11, with client and server on loopback and pinned to disjoint logical CPUs. Each cell is 64 connections, 2 s warmup and 8 s measured. HTTP/1.1, the request body is echoed, and the body size is the number in the first column. All cells had 0 errors. `registration` `bedrock` means `AddNetworkTransport`, `legacy` means `UseNetworkTransport` with the private factory. CPU is the process CPU time in ms per 1,000 requests. Raw CSV files are in a local results folder and are not committed.

### Windows server: the Kestrel transport through the abstractions

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

### Windows client: `SocketsHttpHandler` against the Bedrock connection factory

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

### Windows client: worker count

A Bedrock client does not use the thread pool for I/O. It has a fixed number of engine workers, each owning its connections. Throughput at 1 KB and 64 connections, from one run per cell. This sweep was run with the earlier client code and was not repeated:

| Workers | IOCP | RIO | SocketsHttpHandler |
| --- | --- | --- | --- |
| 1 | 27.4k | 31.7k | 128.2k |
| 2 | 51.4k | 58.8k | 121.7k |
| 4 | 93.8k | 108.9k | 129.9k |
| 6 | 135.7k | 141.2k | 142.6k |
| 8 | 137.6k | 139.6k | 142.5k |

Throughput scales about linearly with workers, at about 27k requests per second per IOCP worker and about 30k per RIO worker, and reaches parity with `SocketsHttpHandler` at 6-8 workers. A Bedrock client has to size its workers (about the CPU count that the client may use) and place them on distinct cores, while `SocketsHttpHandler` scales with the thread pool. RIO client memory also grows with the worker count, because each worker registers its own slab (about 68 MB at 1 worker to about 140 MB at 8). A client that runs below the worker count it needs is far slower than Sockets, so the default matters.

## Linux: epoll and io_uring under Bedrock

The first pass covered only the Windows providers. The Linux providers could serve `IConnectionListenerFactory` already, but they had no client path, so `IConnectionFactory` threw `NotSupportedException`. This section adds the client paths and measures both providers as a Kestrel transport and as an `HttpClient` connection factory. **The client result is less favorable than on Windows. The server result was first reported too unfavorably because of a harness mismatch and was corrected with a matched run, see [Linux server: matched CPUs and workers, native wrk](#linux-server-matched-cpus-and-workers-native-wrk).**

### What was added

- **Epoll client** (`src\Epoll\EpollClient.cs`, `ep_connect` in `native.c`). `ConnectAsync` runs a blocking `connect` on the thread pool, like the Windows client, then hands the socket to a worker in client mode.
- **io_uring client** (`src\IoUringTcp\RingClient.cs`, `ur_connect` in `native.c`). The same shape: blocking `connect` on the thread pool, then the socket joins the engine's ring. IPv4 only.
- **Connect errors** (`src\Shared\ConnectFailure.cs`). `ECONNREFUSED`, `ETIMEDOUT`, `ENETUNREACH` and `EHOSTUNREACH` become a `SocketException` with the matching `SocketError`, so callers see what `Socket.ConnectAsync` would throw. Other errors become an `IOException`.
- **Client-specific receive sizing**, gated on client mode so the server path is unchanged. Client pages are 64 KB (`NETWORKPROTO_CLIENT_PAGE_KB`). The receive window is a 1 MiB byte budget (`NETWORKPROTO_CLIENT_LEASE_BYTES`), which is what prevents the HTTP/1.1 echo stall described above. Coalescing of writes is on by default for clients and off for servers (`NETWORKPROTO_COALESCE`). An io_uring client engine pins 128 x 64 KB = 8 MB (`NETWORKPROTO_CLIENT_PAGES`) instead of the 2048 x 16 KB = 32 MB of a server engine.
- **A `CanBind` fix** (see [The server side](#the-server-side)).
- **`samples\Bedrock\run-bench.sh`**, the bash counterpart of `run-bench.ps1`, for the same `client`, `server` and `both` plans.

### Environment

WSL2 (kernel 6.18, Ubuntu) on the same Ryzen 9 7950X3D, 32 logical CPUs, loopback. In the client plan the Sockets Kestrel server runs on CPUs 16-31 and the client on CPUs 0-7. In the server plan Kestrel runs on CPUs 0-7 and the client (`SocketsHttpHandler`) on CPUs 16-31. **The server plan has a flaw that was found afterwards:** the server was given 8 logical CPUs, but the native providers ran only 4 workers, so the Sockets transport (which scales over every CPU it gets) had twice the CPUs of the native providers, and the load generator was a .NET `HttpClient`. The server tables below are kept as measured; the matched run in [Linux server: matched CPUs and workers, native wrk](#linux-server-matched-cpus-and-workers-native-wrk) supersedes their comparison with Sockets. 64 connections, 2 s warmup, 8 s measured, 2 repetitions per cell, HTTP/1.1 echo, no TLS. All cells had 0 errors. Rows are the average of two repetitions. The Linux runs are noisier than the Windows runs: the Sockets baseline moved by 10-20% between batches, and WSL2 adds virtualization overhead on top. Results are from one machine and were not independently reproduced. Raw CSVs are in the local results folder and are not committed.

### Linux client: `SocketsHttpHandler` against the Bedrock connection factory

The two providers were measured in separate batches, each with its own Sockets rows from the same batch. 8 client workers, through the default `UseConnectionFactory` overload.

| Body | Client | Requests per second | vs Sockets | p50 µs | p99 µs | CPU ms/1k | Working set |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 KB | SocketsHttpHandler | 320.9k | 100% | 162 | 768 | 19.8 | 100 MB |
| 1 KB | epoll | 322.0k | 100% | 114 | 1801 | 22.9 | 114 MB |
| 8 KB | SocketsHttpHandler | 274.3k | 100% | 187 | 916 | 22.4 | 94 MB |
| 8 KB | epoll | 280.2k | 102% | 124 | 2223 | 26.7 | 114 MB |
| 64 KB | SocketsHttpHandler | 126.4k | 100% | 429 | 1654 | 47.3 | 78 MB |
| 64 KB | epoll | 108.2k | 86% | 380 | 3776 | 66.2 | 96 MB |
| 1 KB | SocketsHttpHandler | 327.1k | 100% | 159 | 738 | 19.4 | 96 MB |
| 1 KB | io_uring | 311.7k | 95% | 136 | 1526 | 23.0 | 128 MB |
| 8 KB | SocketsHttpHandler | 270.3k | 100% | 190 | 929 | 22.7 | 90 MB |
| 8 KB | io_uring | 260.8k | 96% | 164 | 1675 | 27.1 | 120 MB |
| 64 KB | SocketsHttpHandler | 124.1k | 100% | 438 | 1673 | 48.1 | 78 MB |
| 64 KB | io_uring | 94.8k | 76% | 530 | 2646 | 70.9 | 152 MB |

- **There is no throughput win on Linux.** Epoll is at parity at 1-8 KB (per repetition 99-106% of Sockets) and about 14% behind at 64 KB. io_uring is 90-100% at 1 KB, 89-105% at 8 KB (noisy), and about 24% behind at 64 KB.
- **The native clients use more CPU per request**: about 16-20% more at 1-8 KB and 40-47% more at 64 KB. p50 is lower at 1-8 KB, but p99 is higher at every size (about 2.3-2.4 times for epoll, 1.6-2.1 times for io_uring).
- **The 64 KB gap has a structural cause that was not removed.** Each receive is a worker `recv` into a page, a publish to the pipe, a copy by the consumer into the `Stream` buffer, and a command that returns the page to the worker. That is an extra copy and a cross-thread handoff per page that the kernel socket path of `SocketsHttpHandler` does not have. Removing it needs the zero-copy hook listed under [What a real runtime integration would still need](#what-a-real-runtime-integration-would-still-need). This is the reasoning from reading the receive path, and it was not confirmed with a profile.
- **Knobs that did not close the io_uring 64 KB gap**, two repetitions each (requests per second at 64 KB, against about 94k for the default): 4 client workers 80k and 91k, a 2 MiB receive window 93k and 92k, 128 KB client pages 96k and 99k (working set 164-171 MB). Epoll was swept the same way (64 KB body, two repetitions each): 32 KB pages 108.1k, 64 KB pages 108.2k, 128 KB pages 111.3k, 4 workers 106.4k and 16 workers 105.2k. None of these is distinguishable from the default. Epoll at 64 KB measured between 83% and 95% of Sockets in different batches (the Sockets baseline moved from 119k to 127k), so the 86% in the table is one point in a noisy range. The remainder was accepted and is documented here rather than hidden.

### Linux server: the Kestrel transport through the abstractions

A plain `SocketsHttpHandler` client on CPUs 16-31, Kestrel on CPUs 0-7, 4 workers for the native providers (so the Sockets server had 8 CPUs against 4 workers, see the note in the environment section). Requests per second, then p99 in µs:

| Body | Sockets | epoll (Bedrock) | epoll (legacy) | io_uring (Bedrock) | io_uring (legacy) |
| --- | --- | --- | --- | --- | --- |
| 1 KB | 348.2k, 764 | 287.1k, 958 | 293.6k, 954 | 254.8k, 962 | 278.3k, 847 |
| 8 KB | 261.9k, 1003 | 245.5k, 954 | 244.3k, 970 | 212.1k, 1038 | 212.6k, 1110 |
| 64 KB | 110.5k, 1649 | 111.7k, 1735 | 107.8k, 1796 | 102.1k, 1648 | 106.3k, 1622 |

- **Bedrock against the former private-factory registration.** Epoll: -2%, +0.5% and +3.7% at 1, 8 and 64 KB. io_uring: -8.4%, -0.2% and -4.0%. Five of the six cells are inside the noise of two repetitions. **The exception is io_uring at 1 KB, where Bedrock was lower in both repetitions** (254.5k and 255.0k against 280.7k and 275.8k). It was not investigated, so "on par" holds for epoll and for io_uring at 8-64 KB, and not for io_uring at 1 KB.
- **Against Sockets (mismatched CPUs and workers, superseded).** Epoll is at 82% of the Sockets rows at 1 KB, 94% at 8 KB and 101% at 64 KB. io_uring is at 73%, 81% and 92%. Those ratios compare 4 native workers with a Sockets server that had 8 CPUs, so they measure the harness, not the transports. The matched run below gives the opposite picture at 1 KB.
- CPU per 1,000 requests is about equal across servers: 28-32 at 1 KB, 33-38 at 8 KB, 58-64 at 64 KB. p50 is higher for the native servers (epoll 177 µs, io_uring 202 µs, Sockets 150 µs at 1 KB).
- Server resident memory (`VmRSS`): Sockets 71-82 MB, epoll 78-90 MB, io_uring 110-123 MB.

### Linux server: worker count

A native Linux server owns its workers, and the worker count decides the result. Sockets uses the thread pool, so it does not depend on this setting (its numbers differ between batches because of the baseline noise, not because of the setting). Requests per second, two repetitions, Bedrock registration, server on 8 CPUs:

| Workers | Body | Sockets | epoll | io_uring | io_uring RSS |
| --- | --- | --- | --- | --- | --- |
| 2 | 1 KB | 229.2k | 161.0k | 165.0k | 94 MB |
| 2 | 8 KB | 205.8k | 130.8k | 106.8k | 96 MB |
| 4 | 1 KB | 348.2k | 287.1k | 254.8k | 112 MB |
| 4 | 8 KB | 261.9k | 245.5k | 212.1k | 114 MB |
| 8 | 1 KB | 338.3k | 298.8k | 268.1k | 148 MB |
| 8 | 8 KB | 246.3k | 232.6k | 268.9k | 149 MB |

- With 2 workers the native servers are clearly behind. The io_uring 8 KB cell includes one outlier repetition (72.8k, p99 6.5 ms), so treat that cell as unreliable.
- With 8 workers on 8 CPUs, io_uring is ahead of Sockets at 8 KB in both repetitions (264.0k and 273.7k against 259.2k and 233.5k, so the first is within noise), and Sockets still leads at 1 KB. Epoll at 8 KB is at 89% and 100% of Sockets in the two repetitions.
- io_uring server memory grows with the worker count (94, 112 and 148 MB at 2, 4 and 8 workers, against about 70 MB for Sockets), about 9 MB per worker in these runs.
- This sweep used the .NET `HttpClient` load generator at 64 connections. With 8 workers on 8 CPUs it is the closest of these cells to a matched plan, and it still shows Sockets ahead at 1 KB, which the native `wrk` run below does not reproduce. The load generator is the main suspect for the difference (it was not isolated further).

### Linux server: matched CPUs and workers, native wrk

This run was added after the results above looked wrong: the earlier prototype measurements in this repository (see [`RPS-RESULTS.md`](src/IoUring/RPS-RESULTS.md)) showed the Linux providers ahead of Sockets at 4 cores. The causes found in the harness above were a Sockets server with more CPUs than the native providers had workers, and a managed load generator that was itself a possible bottleneck. The matched run changes both: the native providers get one worker per server CPU and the load is generated by native `wrk` (`-t12 -c1200`, 3 s warmup, 8 s measured, client on CPUs 16-27), driven by `samples\Bedrock\run-wrk-server.sh`. Bedrock registration for all three servers, 1 KB body, plain HTTP, 4 repetitions, 0 errors except where noted. The "4 CPUs" plan is CPUs `0,2,4,6` (one logical CPU on each of four physical cores, as WSL2 presents the topology) with 4 workers, and the "8 CPUs" plan is CPUs `0-7` (four physical cores with both SMT threads) with 8 workers.

| Server CPUs | Sockets | epoll | io_uring |
| --- | --- | --- | --- |
| 4 CPUs, 4 workers | 213.1k | 230.3k (+8%) | 250.9k (+18%) |
| 8 CPUs, 8 workers | 405.1k | 397.3k (-2%) | 477.8k (+18%) |

Requests per second, mean of 4 repetitions. Per repetition:

| Server CPUs | Sockets | epoll | io_uring |
| --- | --- | --- | --- |
| 4 CPUs, 4 workers | 214.3k, 209.0k, 217.8k, 211.0k | 218.4k, 224.9k, 243.5k, 234.2k | 250.8k, 255.1k, 249.5k, 248.1k |
| 8 CPUs, 8 workers | 408.5k, 404.4k, 399.7k, 407.9k | 402.4k, 389.7k, 403.9k, 393.2k | 479.9k, 481.0k, 486.7k, 463.6k |

Latency (mean, p99):

| Server CPUs | Sockets | epoll | io_uring |
| --- | --- | --- | --- |
| 4 CPUs, 4 workers | 5.6 ms, 12.4 ms | 5.3 ms, 12-14 ms | 4.8 ms, 9.4-9.9 ms |
| 8 CPUs, 8 workers | 3.0 ms, 7.3-8.5 ms | 3.0-3.1 ms, 7.5-8.4 ms | 2.5 ms, 4.8-5.5 ms |

- **io_uring is ahead in every repetition at both sizes**, by about 18%, with a p99 about 20-45% lower. The four repetitions are tight (Sockets 209-218k, io_uring 248-255k at 4 CPUs), so this is not noise.
- **Epoll is ahead at 4 CPUs and at parity at 8.** In the third 4-CPU epoll repetition (243.5k) `wrk` reported 1,200 socket timeouts, so that rep is suspect. Without it the epoll mean is 225.8k (+6%). The 4-CPU epoll gain is therefore about +6-8%, which is within the spread of its own repetitions (218-244k).
- **This reproduces the earlier prototype shape**: epoll about +13.5% at 4 cores and about -4% at 8 cores in the October 1 `wrk2` batch, and io_uring ahead of epoll for raw TCP in the September 29 batch. It does not reproduce any individual number, because the load generator, the server cores and the body path differ.
- **A first run with 4 workers on every plan** (so that 8 CPUs had 4 workers) showed the same artifact as the old harness: at 1,200 connections Sockets reached 415-422k on 8 CPUs, while epoll stayed at about 295k and io_uring at 280-298k. Giving a native provider fewer workers than CPUs caps it, because it does not use the CPUs that it has no worker on.
- **Not measured under this matched setup:** 8 KB and 64 KB bodies, 64 connections (a first, unmatched 64-connection run had the providers roughly tied with Sockets at 4 CPUs), HTTPS, and the Linux client side. The Linux client conclusions above still come from the older harness.
- Measured on one machine under WSL2 and not independently reproduced. Raw CSVs (`wrk-server-matched-workers.csv` and the unmatched first matrix) are in the local results folder and are not committed.

### What the Linux results say

- **The Bedrock adapter costs about nothing for epoll, and for io_uring at 8-64 KB.** The one open item is io_uring at 1 KB in the old harness (about 8% below the legacy registration); the matched `wrk` run was Bedrock-only and did not re-measure the legacy registration.
- **The comparison with Sockets depends on matching CPUs and workers.** With one native worker per server CPU and `wrk`, the Linux io_uring server is about 18% ahead of Sockets at 1 KB and epoll is about +6-8% at 4 CPUs and at parity at 8. The earlier "native is slower at 1 KB" reading came from giving Sockets twice the CPUs and from a managed load generator. Native providers need the worker count set to the CPU count; Sockets does not.
- **The Linux client is the part that does not beat `SocketsHttpHandler`.** The clients have lower p50 but higher CPU and p99, and are slower at 64 KB. The client plan did give the native clients 8 workers on 8 CPUs (0-7) against a Sockets server on 16 CPUs, so the CPU/worker mismatch of the server plan does not apply to it. It was still not re-measured with a different load method or an SMT-aware CPU placement, and the 8 workers share four physical cores.
- **WSL2 is not a production Linux.** Loopback inside a virtual machine, with 10-20% batch noise and few repetitions. A bare-metal run, more repetitions, 8 KB/64 KB and 64-connection matched runs, and a profile of the Linux 64 KB client would be needed before drawing a conclusion about the providers themselves.

## What this shows

- **The server side fits, at no measurable cost.** One factory class that implements the existing interfaces is enough, and it matches the private factory within noise on Windows, and for epoll and for io_uring at 8-64 KB on Linux. With matched CPUs and workers the Linux servers are at or ahead of the Sockets transport at 1 KB (io_uring about +18%, epoll +6-8% at 4 CPUs).
- **The client side fits mechanically and can be faster on Windows.** `IConnectionFactory` plus `ConnectCallback` carries real HTTP/1.1 traffic including 1 MiB uploads, on all four providers. RIO beats `SocketsHttpHandler` at 8 KB and 64 KB, and IOCP at 8 KB, on this machine. It does not beat it at 1 KB. On Linux the clients reach parity at 1-8 KB and are slower at 64 KB.
- **The price is memory and tuning.** RIO uses about two to three times the memory of `SocketsHttpHandler` on the client, IOCP has the worse p99, and the Linux clients have the worse p99 and CPU. All of them need the worker count chosen by the application.
- **The zero-copy path still needs a new contract.** The Windows gains came from the transport's own receive window and block sizing, not from removing the `Stream` copy. The Linux 64 KB gap is consistent with that copy being the remaining cost. A runtime hook that passes a `ConnectionContext` would remove it, and it would require a `System.Net.Http` API proposal.

## What a real runtime integration would still need

- A handler hook that accepts an `IConnectionFactory` or `ConnectionContext`, instead of a `ConnectCallback` that returns a `Stream`. The runtime was not changed for this experiment.
- `ConnectEx` on Windows and a non-blocking connect on Linux instead of a blocking `connect` on the thread pool, which also keeps connect off the thread pool under load.
- IPv6, and a DNS-aware connect, with a fallback to the sockets factory.
- A decision about worker ownership: a process-wide client engine with a few workers, with the sizing treated as an application concern.
- Measurements on more than one machine, on bare-metal Linux, with more than two or three repetitions, against the runtime's own `SocketsHttpHandler` on the same runtime build. A runtime fork was not used for this experiment.

## Limits of these measurements

- One machine, loopback, three repetitions per Windows cell and two per Linux cell (one for the Windows worker sweep). Differences of a few percent are noise, and the Sockets baseline varies by 10-20% between batches.
- The server in the client plan is the Sockets transport. The Bedrock client was not measured against a Bedrock server (the `both` plan of `run-bench.ps1` and `run-bench.sh` was not run).
- The clients are IPv4-only. The Linux numbers are from WSL2, not bare metal. The Linux server tables in the first Linux section use a mismatched CPU/worker plan and a managed load generator (see [Linux server: matched CPUs and workers, native wrk](#linux-server-matched-cpus-and-workers-native-wrk)); the matched run covers only 1 KB, 1,200 connections and plain HTTP. The Linux 64 KB client gap and the old-harness io_uring 1 KB server gap were not profiled.
- No TLS runs: TLS stays in `SocketsHttpHandler` for the client and in Kestrel or the native providers for the server, and the numbers above are plain HTTP.
- The client wins depend on the receive window (1 MiB) and, on Windows, the 256 KB receive block cap. The RIO block cap experiment showed that 64 KB blocks gave no consistent win and 256 KB blocks gave RIO +8% at 64 KB, at about +30 MB of working set.
- Latency percentiles are from one load generator at a fixed 64 connections. They are not a latency-focused benchmark.
