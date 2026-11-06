# Fitting the prototype under the Connections abstractions (Project Bedrock)

This document records an experiment on branch `dmkorolev/project-bedrock`. It asks two questions. First, can the prototype providers (`Iocp`, `Rio`, and the Linux providers) be reached through the existing `Microsoft.AspNetCore.Connections` interfaces instead of through a dedicated engine, factory and bind API? Second, can the same providers act as the connect side of an `HttpClient`, in the spirit of [dotnet/aspnetcore#4772](https://github.com/dotnet/aspnetcore/issues/4772) (Project Bedrock), so that a transport other than `Socket` carries `SocketsHttpHandler` traffic?

It is an experiment, not an API proposal. The new public types are non-shipping and are listed in `src\PublicAPI.Unshipped.txt` only because the project requires it. Every number was measured on one development machine and was not independently reproduced. It is a companion to [PROTOTYPE.md](PROTOTYPE.md), which describes the provider model, and to [MemoryFootprint.md](MemoryFootprint.md).

## Summary

| Question | Result |
| --- | --- |
| Can the server side hide behind `IConnectionListenerFactory`? | Yes. One class, `NetworkTransportFactory`, implements it together with `IConnectionListenerFactorySelector`. The selector is the only thing that had to be added, and the options type (`IocpEngineOptions`, `RioEngineOptions`, ...) is what chooses the provider. Routing through the interfaces costs nothing measurable against the earlier private factory. |
| Can the client side hide behind `IConnectionFactory`? | Yes, for IPv4 on Windows. The same class implements `IConnectionFactory`, and `UseConnectionFactory` plugs it into `SocketsHttpHandler.ConnectCallback`. Epoll and io_uring have no client path yet. |
| Does the Windows server beat Sockets through the abstractions? | Yes, as before. At 1 KB RIO is about 21% and IOCP about 11% ahead of the Sockets transport. |
| Does the Windows client beat `SocketsHttpHandler`? | Only marginally, and only for small bodies. RIO is about 5% ahead at 1 KB and 8 KB, within run-to-run noise. At 64 KB both providers are slower than `SocketsHttpHandler`. |
| Is anything lost by going through the interfaces? | The zero-copy read lease. A `ConnectionContext` exposes `PipeReader` and `PipeWriter`, so the native page lease is hidden unless the consumer asks for `INetworkConnectionFeature`. On the client side a `Stream` is needed, which copies. |

## What had to be added

The goal was to introduce as little as possible. The final surface is four public items. Three are in `System.Net.Transport`, next to the prototype types, and `AddNetworkTransport` is in `Microsoft.Extensions.DependencyInjection`:

- `NetworkTransportFactory` implements `IConnectionListenerFactory`, `IConnectionListenerFactorySelector`, `IConnectionFactory` and `IAsyncDisposable`. It is constructed from the engine options, so the options type is the transport selector. There is no separate "transport kind" setting.
- `INetworkConnectionFeature` is a connection feature that returns the underlying `NetworkConnection`. A consumer that wants the zero-copy read lease or the explicit send queue gets it from `connection.Features`.
- `AddNetworkTransport(this IServiceCollection, NetworkEngineOptions, NetworkNativeTlsOptions?)` registers one singleton as both `IConnectionListenerFactory` and `IConnectionFactory`.
- `UseConnectionFactory(this SocketsHttpHandler, IConnectionFactory[, int receiveBufferBytes])` sets `ConnectCallback`.

The server wiring is only this:

```csharp
builder.Services.AddNetworkTransport(new RioEngineOptions { WorkerCount = 4 });
```

Kestrel then selects the factory per endpoint. `KestrelServerImpl` reverses the registered `IConnectionListenerFactory` list, so the last registration is tried first, and `TransportManager.BindAsync` takes the first factory whose `CanBind(endpoint)` is true. `NetworkTransportFactory.CanBind` accepts an `IPEndPoint` (IPv4 only on Windows), so an IPv6, DNS or Unix endpoint falls through to the default sockets factory in the same process. The check run covers this: an IPv6 listener fell through to the default factory.

What could not be hidden:

- **The options types.** Worker count, processor affinity, pool caps and trimming are provider specific and have to be passed somewhere. They stay public.
- **`NetworkConnection`.** The native read lease and the send queue are the reason a native transport can be faster. They are reachable only through `INetworkConnectionFeature`.
- **Native TLS options.** `NetworkNativeTlsOptions` is passed to the factory, because the abstractions have no place for transport-level TLS.

What could become internal if this direction were taken: the private `KestrelFactory` in `src\Shared\Registration.cs`. `UseNetworkTransport` could register `NetworkTransportFactory` instead. The server plan below shows the two perform the same.

## The client side

`SocketsHttpHandler.ConnectCallback` returns a `Stream`, and TLS and HTTP framing stay in the handler. The adapter is a `ConnectionContextStream` that reads and writes through the connection's duplex pipe:

```csharp
var handler = new SocketsHttpHandler().UseConnectionFactory(factory);
using var client = new HttpClient(handler);
```

`ConnectAsync` resolves a `DnsEndPoint` to the first IPv4 address and connects through the provider. On Windows the new `WindowsClient` runs a blocking `connect` on the thread pool and then hands the socket to a worker, which registers it with the completion port or the request queue. The Epoll and IoUring providers throw `NotSupportedException` for a client connection.

### A receive-side stall, and the read-ahead

The first Kestrel plus `HttpClient` run stalled on a 1 MiB POST echo. The cause is the provider's receive backpressure. `ConnectionState.StartRead` (`src\Windows\ConnectionState.cs`) stops receiving once four 16 KB pages are leased and not yet consumed. Kestrel never hits this because it reads continuously. HTTP/1.1 in `SocketsHttpHandler` writes the whole request body before it reads the response, and the server echoes while it reads, so both sides fill their four pages and neither makes progress. The thresholds were about 512 KB for IOCP and 256 KB for RIO. A socket does not stall here because the kernel receive buffer absorbs the data.

The remedy is a read-ahead pump in `ConnectionContextStream`. It drains the leased pages into a local `Pipe` bounded by `receiveBufferBytes` (default 1 MiB), which plays the part of the kernel buffer. The cost is one extra copy and up to `receiveBufferBytes` of memory per connection. `receiveBufferBytes: 0` keeps the stream zero-copy but reintroduces the stall for large uploads, and the check run reports the stall as an expected result.

A real runtime integration would not need the extra copy if the handler wrote the body and read the response concurrently, or if the connection decoupled its receive window from the consumer. The relevant ordering in the runtime is in `HttpConnection.SendAsync` in `src\libraries\System.Net.Http\src\System\Net\Http\SocketsHttpHandler\HttpConnection.cs`. The runtime was not changed for this experiment.

## Correctness checks

`samples\Bedrock` has a `check` mode. Its last run passed all of these, for IOCP and RIO:

- The `CanBind` contract: IPv4 yes, DNS and Unix (and IPv6 on Windows) fall through.
- A 262,161-byte echo round trip through `IConnectionListenerFactory` and `IConnectionFactory`.
- A connect to a closed port fails fast with a `SocketException`.
- Kestrel plus `HttpClient`: 55 requests (GET keep-alive and POST echoes of 1 KiB to 1 MiB) and the IPv6 fall-through.

## Benchmarks

All runs use the `samples\Bedrock` app (`serve` and `bench` modes) driven by `samples\Bedrock\run-bench.ps1`. The machine is a Ryzen 9 7950X3D (16 cores, 32 logical) on Windows 11, with client and server on loopback and pinned to disjoint logical CPUs. Each cell is 64 connections, 2 s warmup and 8 s measured. HTTP/1.1, the request body is echoed, and the body size is the number in the first column. `registration` `bedrock` means `AddNetworkTransport`, `legacy` means `UseNetworkTransport` with the private factory. CPU is the process CPU time in ms per 1,000 requests. Raw CSV files are in a local results folder and are not committed.

### Server: the Kestrel transport through the abstractions

Plain `SocketsHttpHandler` client on CPUs 16-31, Kestrel on CPUs 0-3 with 4 workers. Two repetitions per cell, rows are the average. Requests per second, then p99 latency in µs:

| Body | Sockets | IOCP (Bedrock) | IOCP (legacy) | RIO (Bedrock) | RIO (legacy) |
| --- | --- | --- | --- | --- | --- |
| 1 KB | 82.5k, 1569 | 91.3k, 2670 | 92.4k, 2812 | 99.8k, 1312 | 100.1k, 1324 |
| 8 KB | 75.3k, 1658 | 77.7k, 2876 | 80.1k, 2590 | 85.1k, 1514 | 83.9k, 1540 |
| 64 KB | 43.6k, 15994 | 53.7k, 2922 | 54.9k, 2736 | 59.9k, 2198 | 61.0k, 2068 |

- The Bedrock and legacy registrations differ by 1-3%, which is inside the repetition noise. Going through `IConnectionListenerFactory` added no measurable cost.
- Over Sockets, RIO is about +21% at 1 KB, +13% at 8 KB and +37% at 64 KB. IOCP is about +11%, +3-6% and +23-26%.
- Sockets has a 16 ms p99 at 64 KB against 2-3 ms for the native providers. This is one two-repetition measurement and the cause was not investigated.
- Server working set: 61-77 MB for Sockets, 64-72 MB for IOCP, 96-112 MB for RIO. The RIO figure is registered slabs ([MemoryFootprint.md](MemoryFootprint.md)).

### Client: `SocketsHttpHandler` against the Bedrock connection factory

A Kestrel server on the Sockets transport (CPUs 16-31, 4 workers). The client is on CPUs 0-7, with 8 client workers for the Bedrock providers. Two repetitions. `ra` is the read-ahead size: 1 MiB (the default) or 0 (zero-copy, which can stall on large uploads). Requests per second, p99 in µs:

| Body | SocketsHttpHandler | IOCP ra=1 MiB | IOCP ra=0 | RIO ra=1 MiB | RIO ra=0 |
| --- | --- | --- | --- | --- | --- |
| 1 KB | 127.0k, 2947 | 122.4k, 4839 | 126.0k, 4895 | 133.4k, 2122 | 139.5k, 1966 |
| 8 KB | 122.9k, 2337 | 118.5k, 5334 | 118.0k, 5026 | 129.1k, 2214 | 134.3k, 2051 |
| 64 KB | 98.5k, 3020 | 67.6k, 8802 | 72.9k, 8298 | 56.1k, 10151 | 58.4k, 9270 |

- At 1-8 KB RIO is about 5% ahead and IOCP is within about 4% either way. These differences are close to the repetition spread: the `SocketsHttpHandler` runs alone ranged 122-132k.
- RIO has the lower p99 at 1-8 KB. IOCP has the higher p99.
- At 64 KB the Bedrock clients are clearly slower, at about 69-74% (IOCP) and 57-59% (RIO) of `SocketsHttpHandler`. CPU per 1,000 requests is 89-121 against 67.
- Client working set: about 66-69 MB for Sockets, 70-74 MB for IOCP and 130-143 MB for RIO.

### Client: is the 64 KB gap CPU?

The client above uses all 8 of its logical CPUs, so the 64 KB result might be a CPU limit. A follow-up gave the client 16 logical CPUs (0-15) and moved the server to 16-31, with 2 repetitions at 64 KB:

| Client | 8 workers | 16 workers | CPU ms per 1k requests (8 / 16 workers) |
| --- | --- | --- | --- |
| SocketsHttpHandler | 113.1k | 107.0k | 94.3 / 93.5 |
| IOCP ra=1 MiB | 86.6k | 89.1k | 119.2 / 118.7 |
| IOCP ra=0 | 93.3k | 101.8k | 116.9 / 108.8 |
| RIO ra=1 MiB | 83.4k | 86.1k | 140.7 / 120.2 |
| RIO ra=0 | 86.4k | 93.2k | 126.8 / 117.4 |

Extra CPUs and workers narrow the gap: IOCP ra=0 goes from about 74% of `SocketsHttpHandler` to 82% with 8 workers and 95% with 16, and RIO ra=0 from about 59% to 76% and 87%. A gap remains, and the providers still use about 16-50% more CPU per request. So the earlier result was partly a CPU limit, but also a real per-request cost at this size. The causes were not isolated. The extra copy of the read-ahead pump accounts for some of it, since `ra=0` is consistently faster than `ra=1 MiB`, but `ra=0` also stays behind. The server side was not varied here, and the Sockets numbers (107-113k) may be limited by the server, so the remaining gap should not be read more precisely than this.

### Client: worker count

A Bedrock client does not use the thread pool for I/O. It has a fixed number of engine workers, each owning its connections. Throughput at 1 KB and 64 connections, from one run per cell:

| Workers | IOCP (ra=1 MiB) | RIO (ra=1 MiB) | SocketsHttpHandler |
| --- | --- | --- | --- |
| 1 | 27.4k | 31.7k | 128.2k |
| 2 | 51.4k | 58.8k | 121.7k |
| 4 | 93.8k | 108.9k | 129.9k |
| 6 | 135.7k | 141.2k | 142.6k |
| 8 | 137.6k | 139.6k | 142.5k |

Throughput scales about linearly with workers, at about 27k requests per second per IOCP worker and about 30k per RIO worker. The client reaches parity with `SocketsHttpHandler` at 6-8 workers, where the machine and the server are probably the limit. The consequence is that a Bedrock client has to size its workers, and place them on distinct cores, while `SocketsHttpHandler` scales with the thread pool. RIO client memory also grows with the worker count, because each worker registers its own buffer slab (about 68 MB at 1 worker to about 140 MB at 8).

## What this shows

- **The server side fits.** One factory class and a selector are enough, no new engine API is needed to bind a listener, and the abstractions cost nothing measurable.
- **The client side fits mechanically.** `IConnectionFactory` plus `ConnectCallback` carries real HTTP/1.1 traffic, including 1 MiB uploads, with the read-ahead.
- **A native client is not yet faster.** The best case is RIO at about +5% for small bodies, within noise, at about twice the memory and with a tuned worker count. For 64 KB bodies it is slower. The claim "RIO on the client beats `System.Net.Sockets`" is supported only for 1-8 KB, and only weakly.
- **`ConnectionContext` hides the part that makes the native providers fast.** The zero-copy lease is reachable only through the feature, and any consumer that needs a `Stream` pays a copy.

## What a real runtime integration would still need

- A connection that does not stall when the request body is written before the response is read, without the extra copy. The runtime change is in `HttpConnection`, which was not touched.
- `ConnectEx` instead of a blocking `connect` on the thread pool.
- IPv6, and a DNS-aware connect.
- A client path for the Linux providers.
- A decision about worker ownership. A process-wide client engine with a few workers would be needed, and its sizing is an application concern.
- Measurements on more than one machine, with more than two repetitions, against the runtime's own `SocketsHttpHandler` on the same runtime build. A runtime fork was not used for this experiment.

## Limits of these measurements

- One machine, loopback, two repetitions per cell (one for the worker sweep), so differences of a few percent are noise.
- The server in the client plan is the Sockets transport. The Bedrock client was not measured against a Bedrock server (`run-bench.ps1 -Plan both` was not run).
- The Linux providers were not measured because they have no client path.
- No TLS runs: TLS stays in `SocketsHttpHandler` for the client and in Kestrel or the native providers for the server, and the numbers above are plain HTTP.
