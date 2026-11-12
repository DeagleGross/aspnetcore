# Project Bedrock: the history of the experiment

> **This is the historical record, not the current state.** It is the Bedrock write-up as it stood at commit `9eab55c648` (before the Linux scaling investigation of 2026-11-12), kept unchanged except for this note. It includes intermediate findings that were later superseded or explained: harness artefacts, the pre-allocation and GC-count findings, dropped experiments, the first Windows tables that were biased by the thread-pool sizing artefact, and the local WSL2 results.
>
> Read [Bedrock.md](Bedrock.md) for the current architecture and the figures to rely on, and [Bedrock-API.md](Bedrock-API.md) for the public API. Where this file and `Bedrock.md` disagree, `Bedrock.md` wins.

# Fitting the prototype under the Connections abstractions (Project Bedrock)

This document records an experiment on branch `dmkorolev/project-bedrock`. It asks two questions. First, can the prototype providers (`Iocp`, `Rio`, and the Linux providers) be reached through the existing `Microsoft.AspNetCore.Connections` interfaces instead of through a dedicated engine, factory and bind API? Second, can the same providers act as the connect side of an `HttpClient`, in the spirit of [dotnet/aspnetcore#4772](https://github.com/dotnet/aspnetcore/issues/4772) (Project Bedrock), so that a transport other than `Socket` carries `SocketsHttpHandler` traffic?

It is an experiment, not an API proposal. The new public types are non-shipping and are listed in `src\PublicAPI.Unshipped.txt` only because the project requires it. Every number was measured on one development machine and was not independently reproduced. It is a companion to [PROTOTYPE.md](PROTOTYPE.md), which describes the provider model, and to [MemoryFootprint.md](MemoryFootprint.md).

## Summary

| Question | Result |
| --- | --- |
| Can the server side hide behind `IConnectionListenerFactory`? | Yes. One class, `NetworkTransportFactory`, implements it together with the existing `IConnectionListenerFactorySelector`. The options type (`IocpEngineOptions`, `RioEngineOptions`, ...) chooses the provider. |
| Is the Bedrock server as fast as the former private-factory prototype? | Yes, within noise. Over three repetitions the two registrations differ by 0-4% in either direction, and the order flips between repetitions. Both stay ahead of the Sockets transport: RIO by about 19-44% and IOCP by about 9-24% in the first Windows run, and by +5% to +14% (RIO) and +1% to +7% (IOCP) after the Windows harness was fixed ([Windows HTTP re-run](#windows-http-re-run-with-the-cpu-count-fixed)). |
| Can the client side hide behind `IConnectionFactory`? | Yes, for IPv4, on all four providers. The same class implements `IConnectionFactory`, and `UseConnectionFactory` plugs it into `SocketsHttpHandler.ConnectCallback`. Epoll and io_uring got a client path in the Linux follow-up below. |
| Is the Bedrock client faster than `SocketsHttpHandler`? | **On Windows, modestly. On Linux, no.** The first 3-repetition Windows run gave RIO +13% at 8 KB and +8% at 64 KB. The rotated 4-repetition re-run ([Rotated re-run](#rotated-re-run-the-figures-to-rely-on)) gave RIO +10% at 8 KB, +3% at 64 KB and +2% at 1 KB, and IOCP +6%, +2% and -2%. Those Windows runs had a harness artefact that favoured Bedrock ([below](#a-harness-artefact-that-flattered-bedrock-on-windows)); with it fixed ([Windows HTTP re-run](#windows-http-re-run-with-the-cpu-count-fixed)) RIO is -4% at 1 KB, +10% at 8 KB and +6% at 64 KB, and IOCP -2%, +3% and +7%, so the Windows client is parity at 1 KB and a small win at 8-64 KB. Before the receive-path changes below the client was at parity at 1-8 KB and 31-43% slower at 64 KB. On Linux the rotated re-run has epoll at 98%, 101% and 86% and io_uring at 87%, 99% and 73% of `SocketsHttpHandler` at 1, 8 and 64 KB. |
| Do the servers beat Sockets by a margin, and does Bedrock keep the prototype's numbers? | Bedrock matches the former private-factory prototype within about 5% in every cell on both operating systems (rotated re-run, below). Against Sockets the margin on Windows is RIO +5% to +14% and IOCP +1% to +7% after the harness fix (the rotated re-run before the fix said RIO +18% to +28%, IOCP +12% to +22%), and for io_uring on Linux in 5 of 6 cells (+14% to +50%). Linux epoll is +11% to +12% on TCP at 1 and 8 KB and about +30% on TLS at 8 and 64 KB, but it does **not** beat Sockets at TCP 64 KB (-5%), and io_uring does not at TLS 64 KB (-10%). The crank-sized margins (+22% TCP, +40% TLS for epoll) were not reproduced on this machine for every cell. |
| Is the Bedrock client faster than a plain `Socket` once HTTP and Kestrel are removed? | **io_uring yes (about +7% to +16% at 64 B to 8 KB), epoll only at parity, as long as the consumer does not `await NetworkConnection.SendAsync` on a CPU-limited client.** Raw TCP against a C server, WSL2, 2 client CPUs, one machine ([Raw TCP client](#raw-tcp-client-removing-http-and-kestrel-from-the-picture)). The default awaited send cost epoll 30% of its throughput because its continuation hops to the thread pool. `EnqueueSend`, or the opt-in `CompleteSendsInline`, brings it back to parity. The `Stream` adapter costs another 4-9 points at 8-64 KB. **On Windows (harness fixed), RIO is ahead by about 6-17% at 64 B and 1 KB, IOCP is at parity, and nothing beats `Socket` at 64 KB.** |
| Does the Windows advantage carry over to Linux? | **On the server, yes for io_uring, and for epoll at a small core count. On the client, no.** With matched CPUs and workers and a native `wrk` load generator (1 KB, 1,200 connections, 4 repetitions), the Bedrock io_uring server is about 18% ahead of the Sockets server at both 4 and 8 logical CPUs and has a lower p99. Epoll is about 6-8% ahead at 4 CPUs and at parity at 8. An earlier "epoll about 82%, io_uring about 73% of Sockets" result was a harness artifact (Sockets had 8 CPUs, the native providers 4 workers, and the load generator was a .NET `HttpClient`); it is kept in the Linux section for the record. The Bedrock adapter itself is within noise of the former private-factory server: an interleaved 8-repetition A/B of epoll at 1 KB and 256 connections gave Bedrock +1.6% (TCP) and +3.8% (TLS) over the legacy registration, with each beating Sockets by about 15-18%. At 256 connections with TLS the Bedrock servers are ahead of Sockets (`SslStream`) at 1-8 KB (epoll +24-40%, io_uring +47-59%, one machine, 3 repetitions); 64 KB is inconclusive. The figures of the earlier crank run (`LINUX-RESULTS.md`) are the pre-Bedrock reference and are compared in [Linux server: TCP and TLS at 256 connections](#linux-server-tcp-and-tls-at-256-connections-and-how-it-compares-with-the-pre-bedrock-numbers). The Linux clients are near parity with `SocketsHttpHandler` at 1-8 KB (epoll 99-106%, io_uring 89-105% per repetition) and slower at 64 KB (epoll about 85-95%, io_uring about 75-80%), with higher CPU per request and a higher p99. See [Linux: epoll and io_uring under Bedrock](#linux-epoll-and-io_uring-under-bedrock). |
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
| `System.Net.Transport.NetworkConnection.CompleteSendsInline { get; set; }` (default `false`) | No. Opt-in, only for a consumer that awaits `SendAsync` and whose continuation never blocks. | Found by the [raw TCP client](#raw-tcp-client-removing-http-and-kestrel-from-the-picture) study. It decides which thread runs the continuation of an awaited `SendAsync`. The default hops to the thread pool, which cost the epoll provider 30% of its throughput on a CPU-limited client. See that section for the design tension. |

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

The first three subsections are Windows (IOCP and RIO). **They were measured before the Windows harness was fixed and overstate the Bedrock margin over `System.Net.Sockets`; the corrected figures are in the [Windows HTTP re-run](#windows-http-re-run-with-the-cpu-count-fixed).** The Linux results follow in [Linux: epoll and io_uring under Bedrock](#linux-epoll-and-io_uring-under-bedrock).

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
- **Not measured under this matched 1,200-connection setup:** 8 KB and 64 KB bodies and HTTPS (both are in the 256-connection section below), 64 connections (a first, unmatched 64-connection run had the providers roughly tied with Sockets at 4 CPUs), and the Linux client side. The Linux client conclusions above still come from the older harness.
- Measured on one machine under WSL2 and not independently reproduced. Raw CSVs (`wrk-server-matched-workers.csv` and the unmatched first matrix) are in the local results folder and are not committed.

### Linux server: TCP and TLS at 256 connections, and how it compares with the pre-Bedrock numbers

The numbers above are plain HTTP at 1,200 connections. The earlier prototype results that the Linux providers were measured with (`LINUX-RESULTS.md` on the `dmkorolev/networking-proto` branch of `aspnet/Benchmarks`, crank, 2026-10-28) used 256 connections, TCP and TLS, and 1, 8 and 64 KB bodies. This section repeats that shape under Bedrock. **Those crank numbers are the pre-Bedrock prototype, not Bedrock**, and they are reported, not reproduced here.

What "epoll" is in the Bedrock server: the epoll provider with the transport-owned TLS (`NetworkNativeTlsOptions`, an fd-bound OpenSSL connection, the same design as the "direct TLS" columns of the earlier document) in the TLS cells, and plain TCP in the TCP cells. The io_uring provider owns its ciphertext through a custom BIO by default. The Sockets baseline is stock Kestrel `UseHttps` (`SslStream`) in the TLS cells. The earlier Bedrock tables in this document are TCP only, which is why the epoll rows there do not show the TLS gain.

Setup of the new runs: WSL2, `samples\Bedrock\run-wrk-matrix.sh` and `run-wrk-ab.sh`, server on CPUs `0,2` with 2 workers, native `wrk -t12 -c256` on CPUs 16-27 (3 s warmup, 8 s measured), TLS 1.2 `ECDHE_RSA_AES_128_GCM_SHA256` with a self-signed certificate, 0 errors apart from a one-per-connection start-up timeout in some cells. The crank runs used a separate load-generator machine (32 `wrk` threads), 15 s warmup and 15 s measured, a different server app (`app.Run`, no Server header, `BodyWriter.WriteAsync`; the Bedrock sample uses `MapGet`) and a CPU about half as fast, so absolute numbers are not comparable (the WSL2 Sockets baseline is about twice the crank baseline).

Bedrock registration for all three servers, mean of 3 repetitions (requests per second, percent over the Sockets mean in the same table):

| Mode | Body | Sockets | epoll | io_uring |
| --- | --- | --- | --- | --- |
| TCP | 1 KB | 145.9k | 153.6k (+5%) | 172.3k (+18%) |
| TCP | 8 KB | 99.6k | 129.6k (+30%) | 134.4k (+35%) |
| TCP | 64 KB | 34.9k | 38.9k (+11%) | 46.2k (+32%) |
| TLS | 1 KB | 84.9k | 105.1k (+24%) | 125.0k (+47%) |
| TLS | 8 KB | 67.4k | 94.1k (+40%) | 107.5k (+59%) |
| TLS | 64 KB | 20.1k | 20.9k (+4%) | 14.9k (-26%) |

The 64 KB cells were repeated five times and swing too much to conclude anything (TCP: Sockets 49.7k mean, epoll 42.4k, io_uring 57.0k, which is -15% and +15%; TLS: Sockets 18.2k, epoll 22.3k, io_uring 16.5k, with a spread of 9-28k inside each server). The Sockets TCP 64 KB baseline itself moved from 34.9k to 49.7k between the two batches. Treat 64 KB as inconclusive.

Pre-Bedrock crank numbers for the same cells (from `LINUX-RESULTS.md`, mean of 3 runs, percent over its own Sockets baseline, reported):

| Mode | Body | Sockets | epoll | io_uring |
| --- | --- | --- | --- | --- |
| TCP | 1 KB | 73.0k | 79.5k (+8.9%) | 90.0k (+23.2%) |
| TCP | 8 KB | 50.5k | 59.3k (+17.4%) | 65.5k (+29.7%) |
| TCP | 64 KB | 14.5k | 17.7k (+22.3%) | 17.9k (+23.8%) |
| TLS | 1 KB | 37.2k | 52.8k (+41.7%) | 57.5k (+54.3%) |
| TLS | 8 KB | 24.9k | 35.6k (+42.6%) | 37.2k (+49.2%) |
| TLS | 64 KB | 6.6k | 9.2k (+39.7%) | 8.7k (+31.9%) |

Two points about reading that table. The TCP rows involve no `SslStream`: epoll is +8.9%, +17.4% and +22.3% over plain Sockets at 1, 8 and 64 KB, and io_uring is +23.2%, +29.7% and +23.8%. Only the TLS rows (epoll +40% to +43%, io_uring +32% to +54%) compare against `SslStream`. Against those, the Bedrock server on this machine agrees at TCP 8 KB (epoll +30%, io_uring +35%), TLS 8 KB (+40% and +59%) and TLS 1 KB for io_uring (+47%). It is lower only for epoll at TCP 1 KB and TLS 1 KB in the 3-repetition batch, and that is addressed next. The TCP 64 KB cell does not reproduce crank's +22.3% for epoll here (+11% in the 3-repetition batch, -15% in the 5-repetition batch), and io_uring at TLS 64 KB is below Sockets here and above it in the crank run; 64 KB is the noisy cell.

#### Is the gap to the earlier numbers caused by Bedrock?

To test that, the former private registration (`UseNetworkTransport`, `--registration legacy`) and the Bedrock registration (`AddNetworkTransport`, `--registration bedrock`) were run on the same harness, interleaved repetition by repetition so that host drift hits both (`samples\Bedrock\run-wrk-ab.sh`). Sockets runs in the Bedrock cells as the common baseline. The same server app and flags are used for both. The first A/B run (3 repetitions, epoll and io_uring, TCP and TLS, 1 KB and 8 KB) was too noisy for epoll at 1 KB (the Sockets baseline alone varied between 48k and 128k in one cell, and single repetitions of 46-52k appeared for Bedrock epoll), so epoll at 1 KB was repeated 8 times.

Epoll at 1 KB, 256 connections, 8 interleaved repetitions (requests per second):

| Mode | Sockets mean | Bedrock epoll mean | Legacy epoll mean | Bedrock epoll over Sockets (paired median) | Legacy epoll over Sockets (paired median) | Bedrock over legacy (paired median) |
| --- | --- | --- | --- | --- | --- | --- |
| TCP | 138.2k | 158.8k | 156.9k | +17.7% | +15.8% | +1.6% |
| TLS | 111.7k | 131.1k | 129.2k | +17.1% | +14.6% | +3.8% |

Per repetition, Bedrock epoll over legacy epoll ranged 0.94-1.08 (TCP) and 0.91-1.11 (TLS) with no consistent sign, and the run-to-run coefficient of variation of each server was 4-7%. **The Bedrock adapter is within noise of the former private registration.**

First A/B run, means of 3 repetitions (percent over the Sockets mean of the same cell; noisy, shown for completeness):

| Cell | Sockets | epoll Bedrock | epoll legacy | io_uring Bedrock | io_uring legacy |
| --- | --- | --- | --- | --- | --- |
| TCP 1 KB | 88.0k | 112.0k (+27%) | 128.5k (+46%) | 138.2k (+57%) | 129.4k (+47%) |
| TCP 8 KB | 84.5k | 121.2k (+43%) | 117.4k (+39%) | 132.8k (+57%) | 130.9k (+55%) |
| TLS 1 KB | 84.4k | 88.5k (+5%) | 106.4k (+26%) | 128.1k (+52%) | 136.1k (+61%) |
| TLS 8 KB | 66.3k | 91.4k (+38%) | 93.2k (+41%) | 106.3k (+60%) | 97.5k (+47%) |

The two registrations differ by -17% to +9% in the means, in both directions, and the large deviations are single repetitions (Bedrock epoll TCP 1 KB had one repetition at 52k against 135k and 148k in the other two; Bedrock epoll TLS 1 KB had one at 46k against 96k for legacy). No cell shows a consistent Bedrock penalty. Io_uring at 1 KB TLS and TCP, which was about 8% lower than legacy in the old harness, was +7% and -6% here, so that earlier gap is also within noise.

**Reading.** The server numbers in `LINUX-RESULTS.md` are the reference for "the transport against Sockets" on a dedicated server with a separate load generator. The Bedrock registration reproduces them within the noise of this machine. The remaining difference from crank is the environment (WSL2 loopback on a shared desktop with `wrk` on the same host, 12 `wrk` threads instead of 32 on another machine, shorter runs, an absolute Sockets baseline about twice as high, which suggests a faster CPU and a different kernel-versus-user balance; this was not isolated) and a different server app. In this environment the Sockets baseline moves 10-20% between batches and by more than 2x between single repetitions, so a repetition-level percentage below about 10-15% is not a finding. The epoll TCP 1 KB gain is the smallest in both crank (+8.9%) and here (+5% in the 3-repetition matrix, +15-18% in the 8-repetition A/B).

What would still be needed to claim the crank figures for Bedrock: running the Bedrock sample through crank with a separate load generator (the `aspnet/Benchmarks` application uses the private factory), 64 connections, bare-metal Linux, and the plain-TCP-plus-`TlsBufferSession` variant (group D, [dotnet/aspnetcore#69565](https://github.com/dotnet/aspnetcore/pull/69565)), which was not measured in either run. Raw CSVs and logs: `D:\code\NetworkingTransports-Results\2026-11-09\linux\wrk\ab-legacy-vs-bedrock` and `c256-tcp-vs-tls` (local, not committed).

### What the Linux results say

- **The Bedrock adapter costs about nothing for epoll, and for io_uring at 8-64 KB.** The io_uring 1 KB gap of about 8% in the old harness did not reproduce in the interleaved 256-connection A/B (+7% TCP, -6% TLS, both within noise). See the previous section.
- **The comparison with Sockets depends on matching CPUs and workers.** With one native worker per server CPU and `wrk`, the Linux io_uring server is about 18% ahead of Sockets at 1 KB and epoll is about +6-8% at 4 CPUs and at parity at 8. The earlier "native is slower at 1 KB" reading came from giving Sockets twice the CPUs and from a managed load generator. Native providers need the worker count set to the CPU count; Sockets does not.
- **The Linux client is the part that does not beat `SocketsHttpHandler`.** The clients have lower p50 but higher CPU and p99, and are slower at 64 KB. The client plan did give the native clients 8 workers on 8 CPUs (0-7) against a Sockets server on 16 CPUs, so the CPU/worker mismatch of the server plan does not apply to it. It was still not re-measured with a different load method or an SMT-aware CPU placement, and the 8 workers share four physical cores.
- **WSL2 is not a production Linux.** Loopback inside a virtual machine, with 10-20% batch noise and few repetitions. A bare-metal run, more repetitions, 8 KB/64 KB and 64-connection matched runs, and a profile of the Linux 64 KB client would be needed before drawing a conclusion about the providers themselves.

## Rotated re-run: the figures to rely on

The tables above were measured in several batches, some with a fixed arm order, which lets drift and warm-up favor whichever arm runs first. This section repeats the four comparisons that matter (Linux server, Linux client, Windows server, Windows client) with the **arm order rotated per repetition**, longer runs (3 s warmup, 10 s measured) and more repetitions. Where it disagrees with an earlier table, use this section.

**Correction for the Windows parts of this section.** The Windows runs here, like every earlier Windows run in this document, pinned the benchmark processes after the runtime had started, so .NET sized its thread pool for 32 CPUs ([the harness artefact](#a-harness-artefact-that-flattered-bedrock-on-windows)). That favoured the native providers over `System.Net.Sockets`. For the Windows server and client figures, rely on the [Windows HTTP re-run](#windows-http-re-run-with-the-cpu-count-fixed) instead. The Linux parts are unaffected.

Method:

- Rotation: `samples\Bedrock\run-wrk-rotate.sh` (Linux server), `run-bench.sh` and `run-bench.ps1` (the `client` and `server` plans) start each repetition with a different arm, so every arm is first, middle and last equally often.
- "Paired median" is the median over repetitions of arm / Sockets in the same repetition. "b/l" is the same for Bedrock / legacy registration. Percentages above 100 mean faster than the baseline.
- Every cell had 0 transport errors. The `errors` column of the Linux server CSV counts `wrk` socket timeouts, which appeared only in repetition 1 of five cells (256 connections starting at once).
- All numbers are from one machine and were not independently reproduced. Raw CSVs and logs: `D:\code\NetworkingTransports-Results\2026-11-09\linux\wrk\rotate-5reps`, `...\linux\client-rotate-4reps`, `...\windows\server-rotate-4reps` and `...\windows\client-rotate-4reps` (local, not committed).
- **The load generators differ by operating system.** Linux server runs use native `wrk` (256 connections, 12 threads, CPUs 16-27). There is no `wrk` for Windows here, so the Windows server runs use the sample's .NET `SocketsHttpClient` generator (64 connections, CPUs 16-31). Windows and Linux absolute numbers are therefore not comparable, and Windows server gaps may be understated if that generator is the limit (the Sockets arm reaches 85k requests per second at 1 KB with 4 server workers).

### Linux server (WSL2): Bedrock against the former prototype against Sockets

Server on CPUs `0,2` with 2 workers, `wrk -t12 -c256`, 5 repetitions, 150 cells (2 modes x 3 bodies x 5 arms x 5 repetitions). TLS is `UseHttps` (`SslStream`, TLS 1.2) for Sockets and transport-owned TLS (TLS 1.2) for epoll and io_uring. Gap over Sockets is the paired median:

| Mode | Body | Sockets mean (CV) | epoll Bedrock | epoll legacy | epoll b/l | io_uring Bedrock | io_uring legacy | io_uring b/l |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| TCP | 1 KB | 138.4k (4.9%) | +12.0% | +16.4% | -2.2% | +24.1% | +26.1% | -4.0% |
| TCP | 8 KB | 126.0k (5.0%) | +11.2% | +11.3% | -0.6% | +14.3% | +16.1% | +0.1% |
| TCP | 64 KB | 51.8k (5.1%) | -4.6% | +0.6% | -5.2% | +14.7% | +28.1% | -3.4% |
| TLS | 1 KB | 106.7k (18.2%) | +5.7% | +13.2% | +0.5% | +48.0% | +47.0% | -0.3% |
| TLS | 8 KB | 83.6k (6.6%) | +30.5% | +25.5% | +5.6% | +49.6% | +43.4% | +7.2% |
| TLS | 64 KB | 22.1k (9.7%) | +30.7% | +25.3% | +4.3% | -10.0% | -4.3% | -5.3% |

- **Bedrock is the prototype, within noise.** The b/l column ranges from -5.3% to +7.2% with no consistent sign: TCP is slightly behind (-0.6% to -5.2% for epoll, -4.0% to +0.1% for io_uring), TLS 8 and 64 KB slightly ahead for epoll. The largest TCP gap (epoll 64 KB, -5.2%) is in the cell whose own Sockets baseline has the same spread as the gap.
- **io_uring beats Sockets by a margin in 5 of 6 cells** (+14% to +50%), and its TLS gains (+48% and +50% at 1 and 8 KB) are the largest and agree with the crank figures (+54% and +49%). It does not beat Sockets at TLS 64 KB (-10%, legacy -4%).
- **Epoll beats Sockets by about 11-12% on TCP at 1 and 8 KB and by about 30% on TLS at 8 and 64 KB.** It is +6% at TLS 1 KB (the Sockets baseline there has an 18% CV, the legacy arm is +13%), and it does not beat Sockets at TCP 64 KB (-5%, legacy +0.6%), where the path is dominated by copying 64 KB bodies and the Sockets baseline is wide.
- **Against crank (`LINUX-RESULTS.md`, pre-Bedrock, reported).** Crank epoll TCP was +8.9%, +17.4% and +22.3%; here it is +12%, +11% and -5%. Crank io_uring TCP was +23%, +30% and +24%; here +24%, +14% and +15%. Crank TLS epoll was +42%, +43%, +40% and io_uring +54%, +49%, +32%; here epoll +6%, +31%, +31% and io_uring +48%, +50%, -10%. The legacy arm on this machine is also below crank in the same cells, so the gap to crank is the environment and the app, not the registration. Reasons that were not isolated: a faster CPU (the absolute Sockets baseline is about twice crank's), `wrk` sharing the host with 12 threads instead of 32 on another machine, 10 s intervals instead of 15 s, and a `MapGet` app with a Server header instead of `app.Run`.

### Linux client (WSL2): `SocketsHttpHandler` against Bedrock

Kestrel on the Sockets transport on CPUs 16-31, the client on CPUs 0-7 with 8 workers, 64 connections, 4 repetitions, 36 cells. Share of `SocketsHttpHandler`, as the mean ratio and the paired median:

| Body | Sockets rps (CV) | epoll | io_uring | CPU ms/1k (Sockets / epoll / io_uring) | p99 µs (Sockets / epoll / io_uring) | Working set MB (Sockets / epoll / io_uring) |
| --- | --- | --- | --- | --- | --- | --- |
| 1 KB | 321.7k (4.9%) | 98% / 98% | 87% / 92% | 18.8 / 22.1 / 24.0 | 806 / 1812 / 1618 | 103 / 117 / 120 |
| 8 KB | 263.1k (11.9%) | 101% / 101% | 99% / 99% | 22.6 / 26.8 / 26.1 | 982 / 2212 / 1663 | 97 / 115 / 132 |
| 64 KB | 123.8k (2.0%) | 86% / 86% | 73% / 74% | 46.4 / 64.5 / 71.4 | 1672 / 3884 / 2696 | 79 / 101 / 156 |

**The Linux Bedrock client does not beat `SocketsHttpHandler`.** It is at parity at 1-8 KB for epoll and slower at 64 KB for both providers, with 15-55% more CPU per request and a higher p99. This confirms the earlier 2-repetition result (epoll 100%, 102%, 86%; io_uring 95%, 96%, 76%). The suspected cause is the extra copy and cross-thread page return per receive described under [Linux client](#linux-client-socketshttphandler-against-the-bedrock-connection-factory); it was not profiled.

### Windows server: Bedrock against the former prototype against Sockets

SocketsHttpHandler load generator on CPUs 16-31, Kestrel on CPUs 0-3 with 4 workers, 64 connections, 4 repetitions, 60 cells. Requests per second (mean, CV) and the paired median over Sockets:

| Body | Sockets | IOCP Bedrock | IOCP legacy | RIO Bedrock | RIO legacy |
| --- | --- | --- | --- | --- | --- |
| 1 KB | 84.8k (5.2%) | 94.8k (+12.1%) | 94.3k (+11.4%) | 100.0k (+17.7%) | 101.6k (+19.6%) |
| 8 KB | 75.4k (5.6%) | 84.9k (+12.8%) | 85.3k (+13.0%) | 91.5k (+21.8%) | 90.8k (+20.6%) |
| 64 KB | 49.5k (4.0%) | 59.4k (+21.7%) | 59.9k (+23.4%) | 63.3k (+28.3%) | 62.6k (+23.9%) |

| Body | IOCP b/l | RIO b/l | CPU ms/1k (Sockets / IOCP / RIO) | p99 µs (Sockets / IOCP / RIO) | Server working set MB (Sockets / IOCP / RIO) |
| --- | --- | --- | --- | --- | --- |
| 1 KB | +0.7% | -1.1% | 74.1 / 67.9 / 67.8 | 1447 / 1973 / 1340 | 60 / 64 / 96 |
| 8 KB | -0.4% | +0.8% | 80.7 / 75.8 / 72.1 | 1601 / 2172 / 1365 | 61 / 64 / 96 |
| 64 KB | -0.5% | +0.6% | 95.8 / 86.9 / 79.8 | 7884 / 2260 / 1777 | 73 / 71 / 112 |

- **Bedrock equals the prototype on Windows**: the b/l paired median is within ±1.1% in all six cells.
- **Both beat Sockets by a margin in every cell**: RIO +18% to +28%, IOCP +11% to +22%, with lower CPU per request (RIO 8-17% lower). This agrees with the first 3-repetition run (RIO +20%, +19%, +44%; IOCP +9%, +14%, +24%) at 1 and 8 KB. The 64 KB gain is smaller here (+28% against +44%) because this run's Sockets baseline is higher (49.5k against 45.4k) and RIO's is similar.
- RIO has the better p99 at every size, IOCP the worse p99 at 1 and 8 KB, and Sockets has a 7.9 ms p99 at 64 KB against about 2 ms for the native providers (not investigated).
- RIO costs about 35-40 MB of working set more than Sockets (registered slabs), IOCP is within 4 MB.

### Windows client: `SocketsHttpHandler` against Bedrock

Kestrel on the Sockets transport on CPUs 16-31, the client on CPUs 0-7 with 8 workers, 64 connections, 4 repetitions, 36 cells. Mean requests per second, the paired median share of `SocketsHttpHandler`, and the number of repetitions the provider beat Sockets in:

| Body | Sockets | IOCP | RIO | CPU ms/1k (Sockets / IOCP / RIO) | p99 µs (Sockets / IOCP / RIO) | Working set MB (Sockets / IOCP / RIO) |
| --- | --- | --- | --- | --- | --- | --- |
| 1 KB | 153.6k (3.7%) | 150.2k, 98%, 1/4 | 156.2k, 103%, 3/4 | 44.6 / 48.6 / 45.5 | 2541 / 4308 / 1624 | 77 / 83 / 144 |
| 8 KB | 137.4k (4.0%) | 146.0k, 108%, 4/4 | 150.6k, 110%, 4/4 | 51.5 / 48.2 / 46.7 | 2624 / 4516 / 1697 | 76 / 80 / 148 |
| 64 KB | 116.4k (5.3%) | 119.4k, 103%, 3/4 | 119.9k, 103%, 4/4 | 61.6 / 58.4 / 57.4 | 2643 / 5442 / 2356 | 74 / 91 / 173 |

- **The Windows client win is real but small**: RIO is ahead in 11 of 12 cells-by-repetition (+10% at 8 KB, +3% elsewhere) and IOCP is ahead at 8 KB in every repetition and at 64 KB in three of four. At 1 KB IOCP is behind and RIO is at parity within a 3.7% CV, so there is no 1 KB win.
- The first 3-repetition run overstated the margin at 8 KB and 64 KB for RIO (+13% and +8%), most likely because its Sockets baseline was lower in that batch (140.7k, 122.4k, 103.0k against 153.6k, 137.4k, 116.4k here).
- RIO again has the lower p99 and uses a little less CPU per request, and costs 65-100 MB more working set than `SocketsHttpHandler`. IOCP has 1.7-2.1 times the p99 of Sockets.

### Verdict

| Question | Answer |
| --- | --- |
| Does Bedrock match the former prototype? | Yes. Linux server within about -5% to +7% (no consistent sign), Windows server within ±1.1%. |
| Do the servers beat Sockets by a margin? | Windows: yes for RIO in every cell and for IOCP at 8 and 64 KB, but by much less than the rotated re-run said once the harness is fixed (RIO +5% to +14%, IOCP +1% to +7%, [Windows HTTP re-run](#windows-http-re-run-with-the-cpu-count-fixed); the rotated re-run before the fix said RIO +18% to +28%, IOCP +11% to +22%). Linux io_uring: yes in 5 of 6 cells (+14% to +50%), except TLS 64 KB (-10%). Linux epoll: +11-12% (TCP 1 and 8 KB) and about +30% (TLS 8 and 64 KB), parity or slightly below at TCP 64 KB (-5%) and weak at TLS 1 KB (+6%, noisy baseline). |
| Do the clients beat `SocketsHttpHandler`? | Windows, harness fixed: parity at 1 KB (RIO -4%, IOCP -2%, inside a 10% CV), a small win at 8 KB (RIO +10%, IOCP +3%) and 64 KB (+6% and +7%), at 63-163 MB more memory for RIO and a worse p99 for IOCP. The pre-fix rotated re-run said RIO +10% at 8 KB and +3% elsewhere, IOCP +8% at 8 KB. Linux: no (epoll 98%, 101%, 86%; io_uring 87%, 99%, 73%). |
| What would it take to say more? | A crank run of the Bedrock sample with a separate load generator, bare-metal Linux, a `wrk`-class generator on Windows, and a profile of the Linux 64 KB client path. |

## Raw TCP client: removing HTTP and Kestrel from the picture

The HTTP client benchmarks above compare `SocketsHttpHandler` over a Kestrel Sockets server. They put the HTTP layer, the `Stream` adapter and a managed server between the transport and the number. This section removes all three to see whether the transport itself is faster than `System.Net.Sockets` on the client side.

### Setup

- **Server.** `samples\BedrockTcp\tcpserver\tcp_server.c`, one C file for Linux and Windows. It reads the request bytes, writes the response bytes and repeats, on loopback IPv4 with `TCP_NODELAY` and non-blocking sockets. On Linux it uses 16 level-triggered epoll threads on CPUs 8-31. On Windows it uses `WSAPoll` with a 1 ms timeout, which quantises latency, so Windows numbers are only comparable between arms of the same table.
- **Client.** `samples\BedrockTcp\Program.cs`. 64 connections, each a closed loop: write the request, read the whole response, repeat. No HTTP, no TLS. The client process is pinned to CPUs 0-1 and the providers run 2 workers, so the client is the CPU-limited side.
- **Arms.** `sockets` is a plain `Socket`. For a provider, `<provider>-pipe` uses `ConnectionContext.Transport` (what Kestrel and any `IConnectionFactory` consumer sees). `-stream` uses the `ConnectionContextStream` that `UseConnectionFactory` hands to `SocketsHttpHandler`. `-native` uses `INetworkConnectionFeature` with an awaited `NetworkConnection.SendAsync` and `ReceiveAsync`. `-native-inline` is `-native` with `CompleteSendsInline = true`. `-queued` uses `AllocateBuffer` and `EnqueueSend` and never awaits the send.
- **Method.** Linux (WSL2) on a Ryzen 9 7950X3D, 5 repetitions of 6 s after a 2 s warm-up, arm order rotated per repetition, all errors 0. Measured on one machine, not independently reproduced. Each repetition's ratio is taken against the Sockets arm of the same repetition and the median is shown, with the number of repetitions the arm beat Sockets in.

### Linux results: share of Sockets (100% is parity), paired median, repetitions won

| Arm | 64 B / 64 B | 64 B / 1 KB | 64 B / 8 KB | 64 B / 64 KB |
| --- | --- | --- | --- | --- |
| Sockets (mean requests/s) | 125.8k | 122.0k | 119.1k | 84.7k |
| Sockets (client CPU ms per 1k requests) | 14.62 | 14.70 | 15.42 | 21.98 |
| epoll-pipe | 100.0%, 3/5 | 99.6%, 2/5 | 103.5%, 5/5 | 102.4%, 4/5 |
| epoll-stream | 101.2%, 3/5 | 104.6%, 4/5 | 97.1%, 1/5 | 98.1%, 1/5 |
| **epoll-native (default `SendAsync`)** | **70.3%, 0/5** | **70.2%, 0/5** | **67.0%, 0/5** | **66.3%, 0/5** |
| epoll-native-inline | 97.3%, 2/5 | 107.1%, 4/5 | 100.1%, 3/5 | 103.4%, 4/5 |
| epoll-queued | 98.0%, 1/5 | 106.4%, 4/5 | 101.1%, 3/5 | 100.9%, 3/5 |
| io_uring-pipe | 109.7%, 5/5 | 115.7%, 5/5 | 110.2%, 5/5 | 106.0%, 3/5 |
| io_uring-stream | 108.1%, 5/5 | 115.5%, 5/5 | 105.1%, 4/5 | 97.9%, 1/5 |
| io_uring-native (default `SendAsync`) | 102.5%, 3/5 | 109.8%, 4/5 | 106.3%, 4/5 | 97.2%, 2/5 |
| io_uring-native-inline | 109.8%, 5/5 | 115.3%, 5/5 | 106.7%, 5/5 | 101.7%, 3/5 |
| io_uring-queued | 110.6%, 5/5 | 109.1%, 5/5 | 107.6%, 4/5 | 100.0%, 3/5 |

Client CPU per request tracks throughput (the client is CPU-limited), so a share above 100% is also a lower cost per request. io_uring uses 89-95% of the Sockets CPU per request at 64 B to 8 KB.

### What the numbers say

- **io_uring beats Sockets on the raw client**, by about 7-16% for the pipe and `-native-inline` layers at 64 B to 8 KB, and is within 2-6% of it at 64 KB. Both layers win in 5 of 5 repetitions at 64 B and 1 KB.
- **epoll is at parity, not ahead.** Its pipe, `-native-inline` and `-queued` layers have paired medians of 97-107%, and individual repetitions range from 89% to 114%. This is a plausible ceiling rather than a defect: `System.Net.Sockets` is also an epoll engine, so per request both do a `send`, a `recv` and a share of an `epoll_wait`. io_uring wins because it batches submissions and completions. That explanation is a hypothesis; the syscall counts were not measured.
- **The server-side margin does not carry over.** The Kestrel margin comes from the whole request path (scheduling, pipe hand-offs, parsing), where the native workers remove thread-pool hops. A raw client has almost none of that, so there is little left for the transport to win on epoll.

### The epoll `SendAsync` finding

The first matrix had `epoll-native` at 67-70% of Sockets with +50% client CPU and a p99 of 6-8 ms, in every cell, while its p50 was the lowest of all arms (340 µs against 460 µs). io_uring was unaffected. The cause is the thread-pool hop on the awaited send:

- `NetworkConnection.SendAsync` returns a task from a `TaskCompletionSource` created with `RunContinuationsAsynchronously`.
- The task completes on the provider worker, but only on the next pump iteration (`PumpOutputCore` calls `FinishRequest` after the data is fully consumed).
- The continuation is the benchmark's read loop, so every request queued a thread-pool work item. On a 2-CPU client the epoll worker and the thread-pool threads compete for the same two cores. The result is a bimodal latency (a low p50 and a 6 ms tail) and a third more CPU per request.

Evidence, each measured on the same machine, 64 B request and 1 KB response:

| Experiment | epoll-native share of Sockets |
| --- | --- |
| Default (`RunContinuationsAsynchronously`) | 69.8% and 68.5% in two runs |
| `EnqueueSend`, no await (`epoll-queued`) | 106.8% |
| Hard-coded synchronous continuation (throwaway change) | 101.6% |
| `CompleteSendsInline = true` | 99.7% |

The cause is CPU contention. A separate diagnostic with no Sockets arm gave epoll-native 68% of epoll-pipe on 2 client CPUs (94.8k against 139.7k requests per second), and 98% of epoll-pipe on 4 client CPUs (176.3k against 179.8k, 2 repetitions). With spare cores the hop is cheap and the arms converge, because a closed loop of 64 connections is then bound by latency rather than CPU. The same diagnostic with the thread pool spin limit set to 0 raised epoll-native from 94.8k to 114.7k, which also points at thread-pool wake-ups as the cost.

### Design consequences

1. **Send-completion scheduling is part of the contract.** An awaited send that resumes on the thread pool is the safe default (user code never stalls the provider worker that serves other connections), but it can cost 30% on a CPU-limited client. Any real `NetworkConnection` would need to say where the continuation runs.
2. **The fire-and-forget send is the fast path for request/response clients.** `AllocateBuffer` plus `EnqueueSend` took epoll from 70% to 106% with no new API, and it also removes the per-send `TaskCompletionSource`. Errors surface through connection closure instead of the call, which is the price.
3. **`CompleteSendsInline` is a small opt-in.** It is the only API this study added (see [New public API](#new-public-api-non-shipping)). It runs the continuation synchronously on the worker, so enabling it is only safe if the continuation never blocks. Alternatives not built: a pooled `IValueTaskSource` for the send (removes the allocation and lets the scheduler be chosen), or completing synchronously when the provider sent immediately. Both change a public return type or contract, which is why only the property was added.
4. **Lazy `Output` in `AsDuplexPipe`.** `NetworkTransportFactory.ConnectAsync` returns a `KestrelConnectionContext`, and its constructor read `Transport.Output`, which claimed the pipe send mode before any native consumer could choose the direct mode. Every native arm failed with `InvalidOperationException: Pipe output and direct queued sends are separate consumption modes`. The pipe is now created on first use. This is a bug fix, not an API change, but it shows that a single connection object exposing two send modes needs a defined choice point.

### The `Stream` adapter at 8 KB and 64 KB

`-stream` is within noise of its `-pipe` sibling at 64 B and 1 KB, and 4-9 points below it at 8 KB and 64 KB (paired medians: epoll 97% and 98%, io_uring 105% and 98%, against 104% and 102%, 110% and 106% for the pipe arms), with 6-16% more client CPU per request. That is what one extra `memcpy` per byte looks like: the pipe arm only advances over the leased page, while `ConnectionContextStream.ReadAsync` copies from the page into the caller's buffer, and `Socket` copies once from the kernel straight into the caller's buffer. The size dependence fits, but the copy was not isolated with a profile. A direct `Stream` that skipped the pipe was already tried on the Windows HTTP client and was not better (see the dropped experiment above), so the pipe hop does not look like the main cost and the remaining candidate is the lease-then-copy model itself. Removing it needs a receive that fills a caller-supplied buffer, which is a different contract from the leased `ReadOnlySequence<byte>`.

### Windows results: RIO and IOCP against a plain `Socket`

Same client, same arms, same C server (Windows build, 16 `WSAPoll` threads on CPUs 8-31, 1 ms poll timeout). The client is pinned to CPUs 0-1 with 2 provider workers and `DOTNET_PROCESSOR_COUNT=2` (see [the harness artefact](#a-harness-artefact-that-flattered-bedrock-on-windows) below, which is why this table replaces an earlier one). 4 repetitions of 6 s after a 2 s warm-up, arm order rotated, all errors 0, 64 connections. Measured on one machine, not independently reproduced. Paired median share of Sockets (100% is parity), repetitions won out of 4, and the median client CPU per request as a share of Sockets.

| Arm | 64 B / 64 B | 64 B / 1 KB | 64 B / 64 KB |
| --- | --- | --- | --- |
| Sockets (mean requests/s) | 58.6k | 55.7k | 52.9k |
| Sockets (client CPU ms per 1k requests) | 31.21 | 33.51 | 34.70 |
| iocp-pipe | 102.5%, 4/4, cpu 99% | 100.1%, 2/4, cpu 101% | 99.0%, 2/4, cpu 104% |
| iocp-stream | 98.0%, 2/4 | 102.5%, 4/4 | 90.1%, 0/4, cpu 113% |
| iocp-native | 93.0%, 2/4 | 99.8%, 2/4 | 95.1%, 0/4 |
| iocp-native-inline | 93.4%, 1/4 | 100.4%, 2/4 | 98.2%, 2/4 |
| iocp-queued | 89.5%, 1/4 | 102.0%, 3/4 | 97.6%, 2/4 |
| rio-pipe | 105.6%, 3/4, cpu 96% | **117.1%, 4/4, cpu 88%** | 93.1%, 1/4, cpu 108% |
| rio-stream | 109.9%, 3/4 | 110.8%, 4/4 | 79.9%, 1/4, cpu 126% |
| rio-native | 111.9%, 3/4, cpu 92% | 111.0%, 4/4, cpu 91% | 96.9%, 1/4 |
| rio-native-inline | 108.9%, 3/4 | 113.8%, 4/4 | 98.0%, 2/4 |
| rio-queued | 108.8%, 3/4 | 114.1%, 4/4, cpu 89% | 96.7%, 1/4 |

- **RIO is ahead of Sockets at small and medium messages**, by about 6-12% at 64 B and 11-17% at 1 KB, at 4-12% less client CPU per request, and with a lower p50 (about 920-950 µs against about 1,120 µs at 1 KB). The 1 KB cells won 4 of 4 repetitions for every RIO arm. At 64 B the repetitions range from 90% to 130%, so 64 B is a lean win, not a firm one.
- **IOCP is at parity at 1 KB (100-103%) and for its `-pipe` and `-stream` arms at 64 B (98-103%), and loses at 64 KB.** Its `-native` and `-queued` arms are 5-13 points lower at 64 B (89-93% paired median, ranges 80-118%), which is suggestive rather than established with 4 repetitions. IOCP costs about the same CPU per request as Sockets. This is the expected outcome for a provider that, like `System.Net.Sockets` on Windows, drives overlapped I/O through a completion port.
- **At 64 KB nothing beats Sockets.** The best arms (`rio-native-inline`, `iocp-native-inline`) are at 98%, and the `Stream` adapter is the worst, at 80% for RIO and 90% for IOCP with 13-26% more CPU per request. That is the extra copy again, and it is larger on Windows than on Linux.
- **There is no epoll-style awaited-send penalty on Windows.** For RIO, `-native`, `-native-inline` and `-queued` are within noise of `-pipe` (the IOCP 64 B gap above is much smaller than epoll's 30%). The Windows driver sets `CanCompleteInline` (it is true when the call is already on the provider worker), so the send completion does not hop to the thread pool the way the epoll one does.
- **Sockets still has the lowest p99** (about 1.7-2.1 ms against 1.9-3.0 ms for the Bedrock arms), even where it loses on throughput. Some cells also show a single 50-110 ms pause that hits half of the connections at once (one of the two workers), in 8 of 120 Bedrock cells and not in a consistent arm. That is under 1% of a 6 s window and its cause was not found. Sockets showed an isolated single-connection stall in 2 of 12 cells.

### Allocations: Bedrock allocates 4-7 times as much per request as `Socket`

Measured once (one repetition, 64 B request and 1 KB response, `GC.GetTotalAllocatedBytes` over the measured window), bytes allocated per request:

| Arm | Bytes per request | gen0 GCs in 6 s | GC pause in 6 s |
| --- | --- | --- | --- |
| Sockets | 132 | 2 | 0.9 ms |
| iocp-pipe / rio-pipe | 528 / 528 | 57 / 78 | 21.6 / 24.5 ms |
| iocp-stream | 664 | 46 | 16.1 ms |
| iocp-native / rio-native | 619 / 665 | 82 / 108 | 37.1 / 42.3 ms |
| iocp-queued | 920 | 116 | 50.6 ms |

That is a real cost and an easy target (candidates: the per-send `SendRequest` and `TaskCompletionSource`, the `NetworkReceiveResult` and `NetworkBuffer` wrappers, pipe segments). It is not what limits throughput in these runs: the total pause is 16-51 ms of a 6,000 ms window (under 1%), and `-queued` allocates the most while running faster than `-pipe` at 1 KB. It does matter for the claim that the transport is cheaper than `Socket` per request, which these numbers do not support for IOCP.

### A harness artefact that flattered Bedrock on Windows

The first Windows raw-TCP matrix had Sockets at about 49k requests/s with 600-700 individual stalls of 190-600 ms per 6 s window, and the Bedrock arms 10-29% ahead. The stalls were not a transport property. The benchmark clients pin themselves with `Process.ProcessorAffinity` **after** the runtime has started, so `Environment.ProcessorCount` still reported 32 and the thread pool started with a minimum of 32 threads and grew to 30-48 threads, all contending for 2 CPUs. `System.Net.Sockets` on Windows routes its completions through the thread pool, so it stalled. The Bedrock IOCP and RIO providers complete on their own workers and never touch that path, so the artefact favoured them.

Setting `DOTNET_PROCESSOR_COUNT` to the pinned count (2) made the stalls disappear (685 to 0 per window, p99.9 from 282 ms to 2.5 ms for Sockets) and raised Sockets to 55-65k requests/s. That is the harness fixed here. On Linux the runs pin with `taskset` before the process starts, so .NET sees the right CPU count there and the Linux figures were not affected.

Every earlier Windows table in this document that used `Affinity.Apply` (the Windows server, client and worker-count sections and the Windows rows of the rotated re-run) has the same bias, of unknown size, in Bedrock's favour. They are kept for the record. The re-run is in [Windows HTTP re-run with the CPU count fixed](#windows-http-re-run-with-the-cpu-count-fixed).

### Windows HTTP re-run with the CPU count fixed

Same plans as the Windows server and client sections above (`run-bench.ps1`, 64 connections, 2 s warm-up, 8 s measured, 4 repetitions, arm order rotated, all 96 cells 0 errors), but `run-bench.ps1` now sets `DOTNET_PROCESSOR_COUNT` to the size of each process's pinned CPU set before it starts the process: 4 for the Kestrel server on CPUs 0-3 and 16 for the load generator on CPUs 16-31 in the server plan, and 8 for the client on CPUs 0-7 and 16 for the Sockets server on CPUs 16-31 in the client plan. Measured on one machine, not independently reproduced. The HTTP sample does not print the processor count it saw, so the effect is inferred from the script and from the Sockets 64 KB p99 falling from 7.9-14 ms to 3.0 ms, not observed directly. Paired median share of Sockets (100% is parity), repetitions won out of 4:

**Server plan** (Kestrel on CPUs 0-3, 4 workers; the baseline is the Sockets transport):

| Body | Sockets (mean, CV) | IOCP Bedrock | IOCP legacy | RIO Bedrock | RIO legacy |
| --- | --- | --- | --- | --- | --- |
| 1 KB | 87.8k (7.2%) | 100.7%, 2/4 | 98.3%, 1/4 | 105.1%, 3/4 | 108.5%, 4/4 |
| 8 KB | 75.6k (4.9%) | 103.0%, 4/4 | 102.0%, 3/4 | 108.1%, 3/4 | 107.5%, 3/4 |
| 64 KB | 48.7k (2.9%) | 106.8%, 4/4 | 105.5%, 4/4 | 113.9%, 4/4 | 113.1%, 4/4 |

| Body | IOCP Bedrock / legacy | RIO Bedrock / legacy | CPU ms/1k (Sockets / IOCP / RIO) | p99 µs (Sockets / IOCP / RIO) | Server working set MB (Sockets / IOCP / RIO) |
| --- | --- | --- | --- | --- | --- |
| 1 KB | 100.0% | 97.4% (88-100%) | 73.6 / 69.7 / 65.8 | 1680 / 2460 / 1539 | 55 / 60 / 92 |
| 8 KB | 102.5% | 101.2% | 79.0 / 77.5 / 76.1 | 1930 / 2365 / 1544 | 56 / 60 / 92 |
| 64 KB | 100.8% | 101.4% | 96.1 / 92.3 / 89.2 | 2992 / 2912 / 2186 | 65 / 68 / 108 |

**Client plan** (`SocketsHttpHandler` against Bedrock on CPUs 0-7 with 8 workers, Kestrel on the Sockets transport):

| Body | Sockets (mean, CV) | IOCP | RIO | CPU ms/1k (Sockets / IOCP / RIO) | p99 µs (Sockets / IOCP / RIO) | Client working set MB (Sockets / IOCP / RIO) |
| --- | --- | --- | --- | --- | --- | --- |
| 1 KB | 130.2k (10.3%) | 98.5%, 2/4 | 95.5%, 1/4 | 46.6 / 47.9 / 47.8 | 2084 / 3196 / 2148 | 71 / 70 / 134 |
| 8 KB | 115.5k (8.9%) | 103.4%, 3/4 | 109.9%, 4/4 | 54.4 / 50.4 / 47.6 | 2200 / 2979 / 2253 | 66 / 68 / 134 |
| 64 KB | 93.7k (5.0%) | 106.5%, 3/4 | 106.4%, 3/4 | 66.0 / 62.0 / 60.7 | 2692 / 3083 / 2642 | 64 / 88 / 227 |

- **The Windows server margin over Sockets is about half of what the earlier tables said.** RIO is now +5%, +8% and +14% at 1, 8 and 64 KB (earlier: +18%, +22%, +28%) and IOCP +1%, +3% and +7% (earlier: +12%, +13%, +22%). The ordering is the same and RIO is ahead in every cell, but IOCP at 1 KB is at parity, and RIO at 1 KB is a 3-of-4 lean rather than a firm win. The Sockets means barely moved (87.8k, 75.6k and 48.7k against 84.8k, 75.4k and 49.5k); the Bedrock servers got slower (RIO 1 KB 100.0k to 90.6k, IOCP 1 KB 94.8k to 87.6k). So the artefact favoured the native providers in this plan, but the mechanism on the server side was not investigated.
- **Bedrock still equals the former prototype on Windows.** The Bedrock-to-legacy paired medians are 97-103%, with one repetition range of 88-100% for RIO at 1 KB. That is the same conclusion as before.
- **The client margin is a small win at 8 KB and 64 KB and nothing at 1 KB.** RIO is +10% at 8 KB (4 of 4) and IOCP +3%; both are +6-7% at 64 KB. At 1 KB the per-repetition ratios run 92-112% for RIO and 92-107% for IOCP against a Sockets CV of 10%, which is not distinguishable from parity. The 64 KB gain is larger than in the earlier run (+3% for both), and a 4-repetition run cannot say whether that is a real effect.
- **CPU per request:** on the server RIO uses 4-11% less than Sockets and IOCP 2-5% less. On the client both are about 3% higher at 1 KB, then RIO is 12% and 8% lower and IOCP 7% and 6% lower at 8 and 64 KB. **p99:** RIO is the lowest on the server at every size; on the client Sockets is lowest at 1 and 8 KB and RIO at 64 KB, and IOCP is worse than Sockets on the client at every size, as before.
- **Memory is unchanged by the fix**: RIO costs 36-43 MB more than Sockets on the server and 63-163 MB more on the client, IOCP 3-5 MB more on the server and up to 24 MB more on the client at 64 KB.

## What this shows

- **The server side fits, at no measurable cost.** One factory class that implements the existing interfaces is enough, and in the rotated re-run it matches the private factory within ±1.1% on Windows (97-103% after the harness fix) and within about -5% to +7% (no consistent sign) on Linux. It beats Sockets in every Windows cell with RIO (+5% to +14% after the fix) and in most with IOCP (+1% to +7%), and, on Linux, in most cells (io_uring 5 of 6, epoll 5 of 6, with epoll TLS 1 KB weak); the exceptions are listed in the [verdict](#verdict).
- **The client side fits mechanically and is slightly faster only on Windows.** `IConnectionFactory` plus `ConnectCallback` carries real HTTP/1.1 traffic including 1 MiB uploads, on all four providers. With the Windows harness fixed, RIO is +10% at 8 KB and +6% at 64 KB over `SocketsHttpHandler` (parity at 1 KB) and IOCP is +3% and +7%. On Linux the clients reach parity at 1-8 KB (epoll) and are slower at 64 KB (86% epoll, 73% io_uring).
- **The price is memory and tuning.** RIO uses about two to three times the memory of `SocketsHttpHandler` on the client, IOCP has the worse p99, and the Linux clients have the worse p99 and CPU. All of them need the worker count chosen by the application.
- **The zero-copy path still needs a new contract.** The Windows gains came from the transport's own receive window and block sizing, not from removing the `Stream` copy. The Linux 64 KB gap is consistent with that copy being the remaining cost. A runtime hook that passes a `ConnectionContext` would remove it, and it would require a `System.Net.Http` API proposal.
- **Below HTTP, io_uring is ahead and epoll is level with Sockets.** The raw TCP client puts the margin where it can be attributed to the transport: io_uring +7% to +16% at 64 B to 8 KB, epoll within about ±5%. The HTTP client numbers are lower because the HTTP layer, the `Stream` copy and the Kestrel Sockets server add cost that the transport does not control.
- **How the consumer sends matters as much as the provider.** An awaited `SendAsync` on a CPU-limited client cost epoll 30% through a thread-pool hop. Any contract for a public native connection would need to define where send continuations run, and offer a fire-and-forget path.

## What a real runtime integration would still need

- A handler hook that accepts an `IConnectionFactory` or `ConnectionContext`, instead of a `ConnectCallback` that returns a `Stream`. The runtime was not changed for this experiment.
- `ConnectEx` on Windows and a non-blocking connect on Linux instead of a blocking `connect` on the thread pool, which also keeps connect off the thread pool under load.
- IPv6, and a DNS-aware connect, with a fallback to the sockets factory.
- A decision about worker ownership: a process-wide client engine with a few workers, with the sizing treated as an application concern.
- Measurements on more than one machine, on bare-metal Linux, with more than two or three repetitions, against the runtime's own `SocketsHttpHandler` on the same runtime build. A runtime fork was not used for this experiment.

## Limits of these measurements

- One machine, loopback. The [rotated re-run](#rotated-re-run-the-figures-to-rely-on) used 4 repetitions per Windows and Linux client cell and 5 per Linux server cell with the arm order rotated; the earlier sections used two or three repetitions in a fixed order (one for the Windows worker sweep). Differences of a few percent are noise, and the Sockets baseline varies by 10-20% between batches.
- The Windows server runs use a .NET `SocketsHttpClient` load generator, the Linux server runs use native `wrk`. The two operating systems are not directly comparable, and no Windows `wrk`-class generator was available.
- **Windows methodology changed during the study.** Until the [harness artefact](#a-harness-artefact-that-flattered-bedrock-on-windows) was found, the Windows benchmark processes were pinned with `Process.ProcessorAffinity` after start-up and .NET saw all 32 CPUs. The Windows tables that precede the [Windows HTTP re-run](#windows-http-re-run-with-the-cpu-count-fixed) are biased in Bedrock's favour by an unknown amount and are kept only for the record; the raw-TCP Windows table and the HTTP re-run run with `DOTNET_PROCESSOR_COUNT` set. The HTTP re-run's fix is inferred from the script and from the Sockets p99, because that sample does not print the processor count.
- The Windows TLS path (Schannel, TLS 1.3 by default) was not measured.
- The server in the client plan is the Sockets transport. The Bedrock client was not measured against a Bedrock server (the `both` plan of `run-bench.ps1` and `run-bench.sh` was not run).
- The clients are IPv4-only. The Linux numbers are from WSL2, not bare metal. The Linux server tables in the first Linux section use a mismatched CPU/worker plan and a managed load generator (see [Linux server: matched CPUs and workers, native wrk](#linux-server-matched-cpus-and-workers-native-wrk)); the matched run covers only 1 KB, 1,200 connections and plain HTTP. The Linux 64 KB client gap and the old-harness io_uring 1 KB server gap were not profiled.
- TLS was measured only on the Linux server (256 connections, `UseHttps` for Sockets and transport-owned TLS for epoll and io_uring, see [Linux server: TCP and TLS at 256 connections](#linux-server-tcp-and-tls-at-256-connections-and-how-it-compares-with-the-pre-bedrock-numbers)). The clients (`SocketsHttpHandler` TLS stays in `SslStream`) and the Windows server were measured as plain HTTP only.
- The Linux 256-connection runs used native `wrk` on the same host as the server, 2 server CPUs, 8 s measured intervals, 3 to 8 repetitions, and single repetitions varied by more than 2x. Percentages below 10-15% in a single cell are not findings, and the 64 KB cells are inconclusive.
- The client wins depend on the receive window (1 MiB) and, on Windows, the 256 KB receive block cap. The RIO block cap experiment showed that 64 KB blocks gave no consistent win and 256 KB blocks gave RIO +8% at 64 KB, at about +30 MB of working set.
- Latency percentiles are from one load generator at a fixed 64 connections. They are not a latency-focused benchmark.
- The raw TCP client runs used a C echo-style server on 16 threads (CPUs 8-31), a client pinned to 2 CPUs with 2 provider workers, 64 connections and 5 repetitions of 6 s on Linux (4 on Windows, with `DOTNET_PROCESSOR_COUNT=2`). The 2-CPU client is deliberate, to make the client the limiting side, and with 4 CPUs the arms converge. Per-arm repetition counts are small (n=5), so differences under about 5% are noise. The Linux run is on WSL2. The Windows C server polls with `WSAPoll` and a 1 ms timeout, so its latency is quantised and its absolute throughput is far below Linux.
