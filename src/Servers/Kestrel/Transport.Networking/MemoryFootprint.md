# Memory footprint of the prototype transports

This document records what the prototype transports cost in memory, and how that memory behaves after a burst. It compares the Windows providers (`Iocp`, `Rio`) and the Linux providers (`Epoll`, `IoUringTcp`) with Kestrel's built-in Sockets transport. The Sockets transport is measured with the runtime TLS session on top, so the TLS layer is comparable. It is a companion to [ScalingCapabilities.md](ScalingCapabilities.md), which covers adding CPUs and memory after start, and to [PROTOTYPE.md](PROTOTYPE.md), which describes the provider model.

Every number here was measured on one development machine and was not independently reproduced. Most memory arms have three replicates, some have one, and each is listed with its limits below. The raw outputs and the drivers are in a local results folder and are not committed. Treat the numbers as characteristics of the prototype, not as a statement about RIO, io_uring or the Sockets transport in general.

## Summary

| Question | Windows | Linux |
| --- | --- | --- |
| Idle memory right after start | 52-55 MiB for all providers | 67-68 MiB native, 76 MiB for Sockets with the runtime TLS session |
| First request | RIO +14.2 MiB (registered slabs), IOCP +2.1, Sockets +3.7 | +3 to +4 MiB for all |
| Growth per connection, idle (marginal, 1k to 4k connections) | RIO ≈65 KiB, IOCP ≈27, Sockets ≈32 | epoll ≈38 KiB, io_uring ≈53, Sockets ≈51 |
| Steady-state under load, 256 connections, TLS (crank P90) | RIO 168-171 MB, IOCP 96-119, Sockets 99-126 | epoll 140-187 MB, io_uring 161-204, Sockets 166-185 |
| Memory returned after a burst, before pool trimming | RIO and IOCP kept 53-82% of their peak, Sockets 48-50% | epoll and io_uring kept 82-91% of their peak, Sockets 57-58% |
| Memory returned after a burst, with pool trimming (new) | Back to within 11-16 MiB of idle-start | Not implemented, the 32 MiB receive array per engine is fixed |

The performance lead of RIO costs registered memory. RIO registers its buffer slabs with the kernel up front, the registration commits and locks the pages, and so a RIO provider holds more memory than IOCP or Sockets at every load level. Idle-slab trimming now gives most of it back after a burst, at the price of registering again on the next burst.

## How the tests were set up

The memory runs use a loopback `MemClient` against the Networking-Transports benchmark app. The app serves `GET /mem[?gc=1]` when `NETWORKPROTO_MEMPORT` is set, which returns the working set, private bytes, GC counters, native memory counters and, on Linux, `/proc` and `sockstat` values. A forced GC is requested through `?gc=1`.

- **Windows:** rc.1 runtime, TLS 1.3, native Schannel for `Iocp` and `Rio`, and the runtime TLS session for Sockets. Four workers (`w4`, three replicates) and two workers (`w2`, one replicate). Resident memory is the Windows working set.
- **Linux:** WSL2 on kernel 6.18, the server pinned with `taskset` to cores 0-3 and the client to cores 8-15. This is not the lab Linux machine. Resident memory is `VmRSS`.
- **Phases:** idle right after start, 10 seconds later, after one request, then a fresh process holding 256, 1024 and 4096 idle TLS connections (resident memory after a forced GC), then a 64 KiB response burst on those connections, then release of all connections plus a forced GC.
- **Replicates:** three for `w4` warm runs, one for `w2` and for the cold-process runs at 1024 and 4096 connections.
- **Steady state under load** comes from the crank matrix of the layered-TLS work, not from the memory harness. It is the mean of 18 runs per row at 256 connections, with TLS 1.2 on Windows and Linux, and reports crank's initial, maximum, P90 and last working set.
- **Asymmetric request and response sizes** use the same crank profiles with a POST body written by a small Lua script, 256 keep-alive connections, 15 seconds per run and two repetitions per cell.

The measurement instrumentation (the `/mem` endpoint, the Lua POST script and the pool environment variables in `Common/NativeTransport.cs`) lives in the Benchmarks repository and is not committed.

## Idle memory and the first request

Right after start all providers are close to each other. A registration cost shows up on the first request only for RIO.

| Windows arm | Idle at start | Delta after one request |
| --- | --- | --- |
| RIO, native Schannel | 55 MiB | +14.2 MiB |
| IOCP, native Schannel | 54-55 MiB | +2.1 MiB |
| Sockets, runtime TLS session | 52.7 MiB | +3.7 MiB |

The RIO delta is the same in every run. It is consistent with the first 4 MiB input and output slabs being allocated and registered on first use, but it was not isolated further.

| Linux arm | Idle at start | +10 s | After one request |
| --- | --- | --- | --- |
| epoll | 67 MiB | 70 | 74 (+4) |
| io_uring | 68 MiB | 71 | 75 (+4) |
| Sockets, runtime TLS session | 76 MiB | 79 | 82 (+3) |

The Sockets arm starts about 8 MiB higher on Linux and not on Windows. The reason was not investigated.

## Growth per connection

This measures a fresh process holding N idle TLS connections, after a forced GC. The first 256 connections overstate the per-connection cost of RIO, because the fixed slab cost is spread over only 256 connections. The marginal slope between 1024 and 4096 connections is the better number.

| Windows arm | 256 conns | 1024 conns | 4096 conns | Marginal slope, 1k to 4k | Fixed overhead above idle-start |
| --- | --- | --- | --- | --- | --- |
| RIO, `w4` | 118 MiB | 167.6 MiB (97.5 KiB per conn) | 368 MiB (74 KiB per conn) | ≈65 KiB per conn | ≈30 MiB |
| IOCP, `w4` | 70 MiB | 90 MiB (32.5 KiB) | 172 MiB (28.7 KiB) | ≈27 KiB | ≈5 MiB |
| Sockets with TLS session, `w4` | 73 MiB | 97 MiB (39 KiB) | 193 MiB (33.7 KiB) | ≈32 KiB | ≈7 MiB |

| Linux arm | 256 conns | 1024 conns | 4096 conns | Marginal slope, 1k to 4k | Fixed overhead above idle-start |
| --- | --- | --- | --- | --- | --- |
| epoll | 89-91 MiB | 118.9 MiB (41.8 KiB per conn) | 233.7 MiB (39.1 KiB) | ≈38 KiB | ≈7 MiB |
| io_uring | 95.6 MiB | 140.7 MiB (62.1 KiB) | 300.3 MiB (55.4 KiB) | ≈53 KiB | ≈9 MiB |
| Sockets with TLS session | 100 MiB | 140.5 MiB (55 KiB) | 293.6 MiB (51.9 KiB) | ≈51 KiB | ≈5 MiB |

Warm idle connections are higher. These runs step through the 256, 1024 and 4096 levels in one process, so the bursts of the earlier levels have already grown the pools. Windows, resident MiB, three replicates with spread under ±3 MiB:

| Windows arm | 256 | 1024 | 4096 |
| --- | --- | --- | --- |
| RIO `w4` | 118 | 237 | 591 |
| RIO `w2` | 101 | 220 | 430 |
| IOCP `w4` | 70 | 146 | 280-293 |
| IOCP `w2` | 70 | 147 | 233 |
| Sockets `w4` | 73 | 126 | 278-281 |

On Linux at 4096 warm idle connections the arms are epoll 372-380 MiB, io_uring 448-453 MiB and Sockets 394-395 MiB.

## Steady-state memory under load

The memory harness above measures idle connections. The crank matrix also records memory while 256 connections run the benchmark, so it shows the steady-state working set under load. Values are MB, the mean of 18 runs per row.

| Windows (TLS 1.2, AMD relay) | Idle | Max | Steady P90 | Last |
| --- | --- | --- | --- | --- |
| Sockets, plain TCP | 45 | 71 | 68 | 68 |
| Sockets, SslStream | 51 | 100 | 100 | 96 |
| Sockets, TLS session | 51 | 103 | 99 | 98 |
| Sockets, Schannel | 49 | 129 | 126 | 121 |
| IOCP, plain TCP | 47 | 94 | 85 | 79 |
| IOCP, native Schannel | 52 | 107 | 96 | 89 |
| IOCP, TLS session on top | 54 | 124 | 119 | 115 |
| RIO, plain TCP | 48 | 156 | 150 | 146 |
| RIO, native Schannel | 53 | 178 | 168 | 162 |
| RIO, TLS session on top | 54 | 176 | 171 | 160 |

| Linux (TLS 1.2, Intel relay) | Idle | Max | Steady P90 | Last |
| --- | --- | --- | --- | --- |
| Sockets, plain TCP | 66 | 106 | 104 | 101 |
| Sockets, SslStream | 74 | 186 | 185 | 183 |
| Sockets, TLS session | 75 | 169 | 166 | 163 |
| epoll, plain TCP | 68 | 118 | 116 | 108 |
| epoll, native TLS | 69 | 145 | 140 | 133 |
| epoll, TLS session on top | 77 | 190 | 187 | 183 |
| io_uring, plain TCP | 69 | 137 | 132 | 123 |
| io_uring, native TLS | 70 | 162 | 161 | 154 |
| io_uring, TLS session on top | 77 | 206 | 204 | 199 |

Two things stand out. The steady state is close to the maximum for every row, so the peak is not a short spike. And putting the runtime TLS session on top of a native provider costs more memory than the provider's own native TLS (for example epoll 187 against 140 MB P90, io_uring 204 against 161), which is consistent with the layered design holding separate ciphertext and plaintext buffers, but that was not profiled.

## Giving memory back after a burst

The burst phase sends 64 KiB responses on every connection, then closes all connections and forces a GC.

### Before pool trimming

The earliest builds never released their native pools. Resident memory after all connections closed and a forced GC, against the peak during the burst:

| Windows arm | Peak at 4096 conns | Retained after close and GC | Share of peak |
| --- | --- | --- | --- |
| RIO `w4` | 861-883 MiB | 715-717 MiB (+660 over idle-start) | 80-82% |
| RIO `w2` | 473 MiB | 391 MiB | 80% |
| IOCP `w4` | 419-464 MiB | 272-288 MiB | 53-60% |
| IOCP `w2` | 346 MiB | 211 MiB | 53% |
| Sockets `w4` | 731-741 MiB | 380-385 MiB | 48-50% |

| Linux arm (4096 conns) | Peak | Retained after close and GC | Share of peak |
| --- | --- | --- | --- |
| epoll | 726-789 MiB | 656-681 MiB | 82-91% |
| io_uring | 905-930 MiB | 767-779 MiB | 82-83% |
| Sockets with TLS session | 873-887 MiB | 542-544 MiB | 57-58% |

Release was flat from 1 to 60 seconds after the connections closed. Only a forced GC lowered it, by about 40-140 MB. In the Sockets arm on Windows about 287 MB stayed as a pinned object heap after close and GC. It is probably Kestrel's pinned block pool, but that was not profiled.

The peaks of the Windows native providers in that table are too low. The pools were capped at 32 MiB input and 128 MiB output per worker, and at 4096 connections the caps aborted connections instead of using more memory (see the closure section below).

### With configurable caps and idle-slab trimming

The Windows providers now trim idle pool slabs; the design is in [ScalingCapabilities.md](ScalingCapabilities.md) and the options are listed at the end of this document. The trim A/B below uses 1024 connections and a 64 KiB burst, two rounds per row. "Trim off" is the new build with `PoolIdleTrimDelay = Timeout.InfiniteTimeSpan`. The idle-start for these runs is 55.5-57.7 MiB. "Retained" is resident memory 5 seconds after the connections closed and a forced GC.

| Arm | Burst resident at 1024 conns | Retained, trim off | Retained, 3 s delay | Retained, default 10 s delay |
| --- | --- | --- | --- | --- |
| RIO | 418-472 MiB | 390-391 MiB | 71-74 MiB | 284-379 MiB |
| IOCP | 253-295 MiB | 219 MiB | 68 MiB | 133-141 MiB |

The 10 second default had only partly acted by the time of the sample, because trimming starts once a slab has been idle for 10 seconds and frees at most 8 slabs per pass and pool. The sample was taken a few seconds after the release, so it shows work in progress, not the end state. With the 3 second delay the working set returned to within 11-16 MiB of idle-start.

At 4096 connections with the same burst and a 3 second delay, one run per arm:

| Arm | Peak burst | Retained after close and GC | Retained before trimming (above) |
| --- | --- | --- | --- |
| RIO | 1581 MiB | 74 MiB | 715-717 MiB |
| IOCP | 407 MiB | 69 MiB | 272-288 MiB |

The RIO peak is higher than the 861-883 MiB of the older build because the older build aborted connections at its caps. With 256 MiB input and 512 MiB output per worker the 4096 connections all run, and they hold the memory they need.

Trimming had no consistent effect on throughput in a two-round A/B at 256 and 1024 connections with the trim off, a 3 second delay and the 10 second default. The run-to-run noise on the development machine was up to about 20%, which is larger than any effect trimming could have, so this is not proof of zero cost. The next burst after a full trim has to allocate, and for RIO register, slabs again. A 256-connection burst after the release peaked at 144-213 MiB with the 3 second delay and at 251-437 MiB with trimming off, which shows the memory was given back, but the extra latency of re-registering was not measured separately.

A stress run of the trim path passed before the harness was removed: about 2.7k-3.2k slabs trimmed, no failed rents and no corrupted payloads.

Linux does not trim. The epoll and io_uring pools are a fixed 32 MiB per engine, are not configurable yet, and keep what the table above shows.

## Native, managed and kernel memory

The split is a residual, not an instrumented measurement. The provider pools are not exposed as counters, so native memory is private bytes (Windows) or `RssAnon` (Linux) minus the GC heap.

| Windows, 4096 idle connections | Native | GC heap |
| --- | --- | --- |
| RIO `w4` | 521 MiB (`w2`: 381) | 42 MiB |
| IOCP `w4` | 308-326 MiB (`w2`: 239) | 42 MiB |
| Sockets, TLS session | 88-91 MiB | 162 MiB |

After the connections closed and a GC ran, the old build still held about 685 MiB native for RIO `w4` (361 for `w2`) and 428 MiB for IOCP `w4` (292 for `w2`), and Sockets held 54-61 MiB native with about 300 MB managed. With trimming at a 3 second delay the native part fell to 36-41 MiB after the release.

| Linux, 4096 idle connections | GC committed | Native residual |
| --- | --- | --- |
| epoll | 166 MiB | ≈138-145 MiB |
| io_uring | 295 MiB | ≈85-90 MiB |
| Sockets, TLS session | 160 MiB | ≈160 MiB |

On Linux the residual is only a lower bound. The io_uring provider's receive pages are a pinned managed array, so `gc.committed` is about 129 MB at idle against a `RssAnon` of about 9 MB: the 32 MiB array per engine is committed but untouched, so `RssAnon − gc.committed` understates native memory.

**Kernel-side memory.** On Windows RIO registered buffers are locked pages that count in the working set, so they are already in the numbers above. The system-level kernel and commit counters were too noisy to report and are left out. On Linux the delta of `sockstat` TCP memory plus `SUnreclaim` against idle-start was 18-23 MiB at 4096 idle connections for all three arms, 72 MiB (epoll), 48-50 MiB (io_uring) and 25-32 MiB (Sockets) during the 64 KiB burst, and 2-30 MiB after release. It is system-wide, includes the loopback client's own sockets, and the WSL VM is shared with Windows, so treat it as indicative only.

## Different request and response sizes

The earlier crank matrix sent GET requests with no body and varied only the response size. This section sends a POST body and varies the request and response sizes independently. Cells show requests per second, p99 latency in ms, and server working set in MB. All runs completed with no bad responses and no socket errors.

Linux (`workers=2`, two cores, TLS 1.2). The server CPU was about 91-99% of its budget in every cell, so these runs are CPU-bound:

| Shape (request x response) | epoll | io_uring | Sockets with TLS session |
| --- | --- | --- | --- |
| 0 x 1 KiB | 49.8k / 12 / 136 | 56.7k / 10 / 155 | 37.0k / 14 / 128 |
| 1 KiB x 1 KiB | 47.7k / 12 / 134 | 51.5k / 10 / 157 | 35.9k / 14 / 163 |
| 1 KiB x 8 KiB | 34.7k / 16 / 131 | 32.3k / 15 / 152 | 24.3k / 18 / 160 |
| 8 KiB x 1 KiB | 29.7k / 22 / 142 | 30.8k / 17 / 196 | 20.4k / 21 / 156 |
| 1 KiB x 64 KiB | 8.6k / 58 / 174 | 8.6k / 61 / 175 | 7.9k / 63 / 200 |
| 64 KiB x 1 KiB | 7.1k / 133 / 136 | 7.1k / 152 / 214 | 6.8k / 62 / 169 |
| 64 KiB x 64 KiB | 4.8k / 91 / 178 | 4.9k / 91 / 222 | 3.8k / 109 / 236 |

Windows (TLS 1.2, Windows accept pool). Server CPU was 40-65%:

| Shape (request x response) | IOCP native | RIO native | Sockets with TLS session |
| --- | --- | --- | --- |
| 0 x 1 KiB | 71.8k / 41 / 92 | 91.8k / 22 / 216 | 70.8k / 40 / 74 |
| 1 KiB x 1 KiB | 63.7k / 41 / 96 | 78.9k / 31 / 228 | 66.6k / 34 / 118 |
| 1 KiB x 8 KiB | 45.3k / 47 / 105 | 51.9k / 29 / 164 | 43.9k / 41 / 114 |
| 1 KiB x 64 KiB | 7.1k / 41 / 124 | 7.1k / 77 / 200 | 7.1k / 47 / 142 |
| 8 KiB x 1 KiB | 40.6k / 52 / 92 | 60.4k / 34 / 221 | 43.9k / 40 / 113 |
| 64 KiB x 1 KiB | 15.4k / 76 / 129 | 13.5k / 99 / 162 | 13.6k / 46 / 136 |
| 64 KiB x 64 KiB | 7.0k / 68 / 156 | 6.9k / 83 / 218 | 6.9k / 75 / 164 |

What the matrix shows:

- Large requests cost much more than large responses. On Linux epoll a 64 KiB request with a 1 KiB response gives 7.1k requests per second, against 47.7k for 1 KiB both ways and 8.6k for a 64 KiB response. The server drains the request body before it responds. The prototype did not profile where the time goes, so the cause is open.
- On Linux the native providers lead Sockets with the TLS session by about 20-40% at small and medium shapes, and the lead mostly closes at 64 KiB responses.
- On Windows RIO leads at small and medium shapes by about 20-30% over the other two, and everything converges at 64 KiB responses. The 64 KiB runs are probably bandwidth-bound, but that was not confirmed.
- RIO has the highest working set in every Windows shape, 164-228 MB against 74-142 MB for Sockets. This is the registered-memory cost described above.
- io_uring's working set is consistently above epoll's on Linux, for example 196 against 142 MB at 8 KiB x 1 KiB.
- The Linux p99 at 64 KiB x 1 KiB is much higher for epoll and io_uring (133 and 152 ms) than for Sockets (62 ms) at similar throughput. This was not investigated.
- With two repetitions and CPU-saturated Linux runs, differences under about 10% are not meaningful. The IOCP 8 KiB x 1 KiB cell had one noisy repetition (about 39.5% CPU), so its mean is depressed.

## Pool limits and connection closures

Before the caps were configurable, the Windows providers hard-coded 32 MiB of input and 128 MiB of output pool per worker. In the 4096-connection runs that limit showed up as aborted connections. This is a limit of the prototype's bounded pool, not of RIO or IOCP.

- Plain TCP uses `TryRent` for input and waits for a free slot, so it slows down instead of failing.
- The Schannel and TLS session paths use `Rent`. When the pool is exhausted it throws "The transport's bounded native memory pool is exhausted." and the connection is aborted.
- RIO at 4096 connections with a 64 KiB burst had 1413-2346 closures, with both pools at their caps and the first error reporting pool exhaustion on three of four workers. At 1024 connections it had none.
- RIO with an 8 KiB burst still had 470-490 closures with the input pool at its cap. The likely cause is that 1024 connections per worker times 32 KiB is exactly the 32 MiB cap, and any second transient buffer overruns it. That is inferred, not measured per buffer.
- IOCP at 4096 connections had 603 closures in one run and none in another, because it sits at the edge of the output cap.
- Epoll, io_uring and Sockets had no closures.

The defaults are now 256 MiB input and 512 MiB output per worker. Only the first error text and a count are recorded per connection, and `connectionErrors` also counts the client's own abrupt closes, so closure counts from older runs are an upper bound on pool aborts.

## The RIO registration tradeoff

RIO needs the memory registered before a receive is posted, and registration commits and locks the pages. This is where its performance comes from, and also why its footprint differs.

- The first request costs about 14 MiB because the first slabs are registered then.
- The per-connection slope is about 65 KiB against 27 KiB for IOCP at the same connection counts, and the fixed overhead is about 30 MiB against 5 MiB.
- Registered pages stay in the working set while they are registered. Only deregistering and freeing returns them, which is why the trim deregisters first and frees second.
- A trim has a cost. The next burst allocates and registers slabs again, and one long-lived lease keeps a whole 4 MiB slab alive, so a pool with scattered long-lived leases can hold much more than it needs.
- Fewer, larger registrations and a minimum number of warm slabs would change this balance. Neither exists in the prototype.

## What ASP.NET Core users have to do

- Plan the memory for the peak, not the idle. On Windows the process can reach the per-worker caps times the worker count, plus the managed heap.
- Set `InputPoolMaximumBytes`, `OutputPoolMaximumBytes` and `PoolIdleTrimDelay` on `RioEngineOptions` or `IocpEngineOptions` ([Primitives/src/NetworkEngine.cs](Primitives/src/NetworkEngine.cs)). The defaults are 256 MiB, 512 MiB and 10 seconds. `Timeout.InfiniteTimeSpan` disables trimming. The caps are per worker. A shorter delay returns memory sooner and costs more re-registration on the next burst.
- Size the input cap for the connections per worker. As a rule from the closure runs, the cap must hold the connections per worker times the pages each one has in flight, with room for transient extra buffers. The tests used 1024 connections per worker.
- The Kestrel `UseIocp` and `UseRio` registration in the prototype takes the defaults and does not expose these options yet. The benchmark app sets them through the environment variables `NETWORKPROTO_WINDOWS_POOL_IN_MB`, `NETWORKPROTO_WINDOWS_POOL_OUT_MB` and `NETWORKPROTO_WINDOWS_TRIM_SECONDS`, read by its own `Common/NativeTransport.cs`.
- On Linux the receive memory is fixed at 32 MiB per engine (and another 32 MiB with the OpenSSL BIO mode). There is nothing to set.
- Do not compare the burst-phase throughput of the native providers against Sockets. The native arms were limited to four workers and Sockets was not (Windows `w4`, 64 KiB burst: RIO about 22k down to 17-18k requests per second from 256 to 4096 connections, IOCP about 23k to 19k, Sockets 95k to 63k). The burst phase is a memory stress.

## Limitations

- One development machine, 32 logical CPUs, loopback. The Linux runs are in WSL2 and are not the lab Linux machine.
- Three replicates for `w4` warm runs, one for `w2` and the cold runs, one for the 4096-connection trim run, two for the trim A/B and the asymmetric runs.
- Provider native pools are not instrumented, so the native and managed split is a residual. On Linux the residual is a lower bound.
- Kernel-side memory on Linux is system-wide and shared with the Windows host's VM. On Windows it is reasoned from the registration behaviour and not measured.
- Windows ephemeral port limits bound the connection counts that can be tested.
- The asymmetric runs are CPU-saturated on Linux and have two repetitions per cell.
- TLS 1.3 with RIO native on the AMD crank profile produced zero requests in one 0 x 1 KiB run. It was not root-caused, and TLS 1.2 was used for the asymmetric runs.
- The throughput effect of trimming is inside the noise of the machine, not proven to be zero.

## Not done

- Configurable caps and trimming for the Linux epoll and io_uring pools.
- Sizing the pools from the connection fan-out instead of a fixed cap.
- Releasing input pages sooner after the plaintext has been consumed.
- Finding what holds the Kestrel Sockets pinned heap after close.
- The TLS 1.3 and RIO native failure on the AMD profile, and the Linux p99 at 64 KiB request size.
