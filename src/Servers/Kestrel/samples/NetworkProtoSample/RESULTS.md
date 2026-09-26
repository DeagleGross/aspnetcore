# NetworkProto initial comparison and bounded iterations

## 2026-09-26: sharded owned-buffer and post-TLS prototypes

Four server cores (`0,2,4,6`), `DOTNET_PROCESSOR_COUNT=4`, twelve disjoint physical client cores, wrk2, 1,200 connections, two 15-second runs per cell. Same 1,024-byte response and real Kestrel HTTP/1.1 parser; `--minimal true` suppresses diagnostic response headers. TLS uses RSA-2048, TLS 1.2 and `TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256`; resumption is disabled on both paths. Changes remain uncommitted.

| Transport | TCP close RPS | TCP keep-alive RPS | TLS close RPS | TLS keep-alive RPS |
|---|---:|---:|---:|---:|
| Stock Sockets / SslStream | 54,946-56,967 | 158,035-173,987 | 3,257-3,428 | 107,617-110,669 |
| Existing single-ring io_uring / SslStream | 33,821 (one uncontended run) | 72,351-72,965 | 3,098-3,117 | 63,046-63,193 |
| IoUringTcp owned pages / SslStream | 41,000-42,698 | 199,589-200,958 | 3,518-3,568 | 123,034-128,697 |
| IoUringTls native post-TLS | N/A | N/A | 5,505-5,831 | 132,348-139,806 |
| IoUringTcp with coalesced wakes | 39,236-39,645 | 192,024-195,553 | 3,013-3,107 | 132,665-142,949 |
| IoUringTls with coalesced wakes | N/A | N/A | 5,366-5,493 | 141,629-150,202 |

These are overloaded achieved-throughput runs, not sustainable service rates: offered RPS was 200k TCP-close, 50k TLS-close and 1M keep-alive; corrected latency grew to seconds. Stock TCP-close reported 49/17 timeouts; one stock TLS-close run reported 32. Existing io_uring TCP-close also reported timeouts. New-prototype wrk output reported no socket/HTTP errors in these runs. The first old-io_uring TCP-close run briefly overlapped correctness probes and is excluded.

A final stock control measured 57,435 TCP-close, 183,138 TCP-keep-alive, 3,174 TLS-close and 129,806 TLS-keep-alive RPS. The baseline drift is material; do not quote improvement only against the earliest/lower control.

### Implementation and copy boundaries

Four pinned pump threads own separate rings; managed Kestrel work still uses its normal scheduling. TCP uses multishot accept/receive into native provided-buffer pages. `OwnedPipeReader` exposes the same pages through `MemoryManager<byte>` and `ReadOnlySequence<byte>`, with no adapter payload copy. Native TLS uses fd-bound OpenSSL, read-ahead, and multishot readiness polling. `SSL_read_ex` writes plaintext into native pages exposed by the reader. `IoUringTls` does not use `UseHttps()`; it supplies TLS features and plaintext to Kestrel.

This is **zero additional receive copy into Pipes**, not NIC-to-application zero-copy or proof of zero internal TLS copies. Output pipe storage remains pinned through native completion. TCP sends are one-shot; no ordinary multishot-send facility is claimed.

Every exposed receive page was returned in the measured shutdown counters. In one coalesced TCP keep-alive run, 2,881,291 requests used 1,204 receive submissions and 0.115 pump-step/eventfd-wake P/Invokes per request. The native TLS example used 1,203 readiness polls for 2,254,019 requests and 0.102 such interops per request. These count specific P/Invokes, not all runtime interop or OS syscalls. The old raw field `kernelEntries` counts native submit/wait helper invocations; new builds correctly name it `nativeSubmitWaitCalls`.

Known limitations: approximately 4.0-4.3 KB managed allocation/request in prototype keep-alive versus 1.6-1.7 KB for stock, 32 MiB native page storage per worker, per-page wrappers/task completions, and cross-thread command overhead. This is not zero-allocation or production-hardened. Pool exhaustion, stalled handshakes, and all failure/cancellation races have not been exhaustively validated. TCP close regressed. There is no evidence yet identifying Kestrel's parser or IOQueue as the specific bottleneck.

### Reproduction and checks

```bash
cd ~/code/aspnetcore
src/Servers/Kestrel/samples/NetworkProtoSample/scripts/quick-rps.sh sockets
src/Servers/Kestrel/samples/NetworkProtoSample/scripts/quick-rps.sh IoUringTcp
src/Servers/Kestrel/samples/NetworkProtoSample/scripts/quick-rps.sh IoUringTls
NETWORKPROTO_COALESCE=1 src/Servers/Kestrel/samples/NetworkProtoSample/scripts/quick-rps.sh IoUringTls
```

The runner bounds startup/client/shutdown waits and records metrics/errors. `scripts/check-owned.mjs` passed with coalescing off/on for real Kestrel fragmented requests, a header spanning native page boundaries, 132 requests on one connection, pipelining, backend identity and explicit close. Raw output is in `results/quick-*-20260926-*/`; aggregate data is `results/quick-comparison-20260926.json`.

Initial measurements began on 2026-09-19. **The original TLS experiment did not establish an io_uring performance win over stock Kestrel.** Later plaintext iterations below show workload-specific gains, not universal superiority. Sections describe the implementation at that stage; the final full-ring receive/multishot section describes current defaults. Absolute throughput drifts across this shared WSL session. All source changes remain uncommitted in `/home/deaglegross/code/aspnetcore`, branch `dmkorolev/network-proto`.

## Common setup

Source base: `1fcd7ef305697a1888f3ede076010350ae9f4f8d`. Ubuntu 24.04.3, WSL2 kernel `6.18.33.2-microsoft-standard-WSL2`, AMD Ryzen 9 7950X3D, 32 logical CPUs. Server affinity: `0,2,4,6`; client affinity: `8,10`, avoiding guest-reported SMT siblings. Both backends use `DOTNET_PROCESSOR_COUNT=4`, Release builds, the same endpoint, a 1024-byte response body, HTTP/1.1, TLS 1.2, RSA-2048 test certificate, and `TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256`. Small diagnostic-header length differences remain.

SDK `11.0.100-rc.1.26420.103`; the final sample actually loaded `.NET 11.0.0-rc.1.26453.108`, independently recorded by `/metrics` and `/proc/<pid>/maps`. GCC 13.3.0; liburing 2.5 from Ubuntu `2.5-1build1` packages extracted locally; OpenSSL 3.0.13; curl 8.5.0; wrk `debian/4.1.0-4build2`. Raw environment files include versions, package hashes, assembly/native-library hashes and CPU topology.

io_uring performs real accept/receive/send of ciphertext. **TLS is the same Kestrel SslStream layer on both paths. No DirectTLS fd-bound TLS, kTLS or NIC offload was implemented or measured.** TLS resumption in wrk is unverified, so close-mode rates are not labeled full-handshake rates.

Every accepted run checked all responses, exact bodies, TLS/backend headers and connection reuse. All wrk connect/read/write/status/timeout counters were zero. There were no unexpected server errors in accepted runs. Explicit, classified peer-reset warnings at wrk cutoff remain recorded; these are not a claim of zero thrown exceptions.

## First measured comparison

The first attempted run, `results/wrk-20260919T093135Z`, was **not accepted**: the server-log gate stopped it after io_uring keep-alive because peer resets were classified as unexpected `IOException`s. Preliminary rates were 138,005.62 sockets versus 104,335.05 io_uring requests/s. Custom close had not run. Those outputs were not discarded.

A real TLS client consumed a response and then generated TCP RST using `SO_LINGER`. `results/rst-20260919T093325Z` is the expected-red evidence. After exact errno 104 classification, `rst-20260919T093406Z` passed with the reset still logged and propagated as `ConnectionResetException`. All other errors still fail the scripts.

The first **complete accepted** comparison is `results/wrk-20260919T093415Z`: two wrk threads, 16 connections, separate three-second warmup and ten-second measurement per case.

| Transport | Connection mode | Requests/s | Responses | Mean latency | p99 latency |
|---|---|---:|---:|---:|---:|
| Sockets | Keep-alive | 137,936.36 | 1,393,105 | 119.42 us | 720 us |
| io_uring | Keep-alive | 109,039.44 | 1,090,431 | 158.82 us | 664 us |
| Sockets | Close | 4,191.83 | 41,973 | 667.30 us | 3.00 ms |
| io_uring | Close | 3,496.82 | 35,311 | 668.09 us | 2.39 ms |

io_uring was 21.0% lower throughput on keep-alive and 16.6% lower on close. Higher connection turnover is not reflected fully in wrk's completed-request latency, so close-mode latency must not be interpreted as total TCP-plus-TLS connection establishment time.

The command was:

```bash
SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4 \
    THREADS=2 CONNECTIONS=16 WARMUP=3 DURATION=10 \
    bash src/Servers/Kestrel/samples/NetworkProtoSample/scripts/wrk-load.sh
```

Actual expanded wrk commands are in each run's `commands.txt`; server launch is defined by `scripts/common.sh`. The raw first results and subsequent results remain separate.

## Repetition and one focused optimization

The user then requested a bounded investigation rather than stopping after the first comparison. Warmup was increased to ten seconds, and three ten-second measured repetitions were run, with per-process/thread CPU and allocation accounting.

Before batching (`results/wrk-20260919T093655Z`), keep-alive medians were 142,938.57 sockets versus 111,893.32 io_uring requests/s; coefficients of variation were 1.23% and 0.30%. The candidate allocated about 2.61 KB/request versus 1.85 KB/request, and consumed about 21.4 versus 18.1 CPU microseconds/request. Its one ring pump used about 88% of one core, mostly kernel time. Client usage was about 135% of a 200% budget, so that particular candidate run was not client-CPU saturated.

EventPipe traces in `results/wrk-20260919T094128Z` confirmed the managed/native boundaries: the candidate pump was in `Native.Wait`, workers performed SslStream encryption/decryption, and close-mode stacks included `SslHandshake`, cipher-policy construction and SSL-context allocation. **These are sampled thread-wall-time traces, including blocked threads, not CPU-percentage attribution.** CPU attribution comes from pidstat. Profiling changed throughput substantially, so those rates are excluded from benchmark comparisons. Kernel perf sampling was unavailable without additional kernel tooling/privileges; no machine settings were changed.

The focused optimization batches up to 64 CQEs and coalesces eventfd wakeups. Its first implementation had a lost-wakeup race: the pump could remove the previous last command between a producer's nonempty check and enqueue. Two runs in `results/wrk-20260919T094423Z` stalled while wrk misleadingly reported no errors and low completed-request latency. One averaged only 16,952.66 requests/s despite active samples around 107,120 requests/s. **That entire intermediate series is invalid**, not a performance result.

The verifier was strengthened and rejects that preserved failing output. Dequeue now participates in the same lock as the empty-to-nonempty decision. Three repeated final-code runs passed without that discrepancy. This was a correctness fix, not suppression of errors. Pre-optimization and regressed source snapshots are `results/iteration0-source.tar.gz` and `results/iteration1-source.tar.gz`.

## Final code: measured stability and limit

All these runs used ten-second warmup, ten-second measured intervals and two wrk threads. Final code is identical across these series.

| Mode / concurrency | Repetitions | Sockets median requests/s (range) | io_uring median requests/s (range) |
|---|---:|---:|---:|
| Keep-alive / 16 | 3 | 121,404.10 (119,661.94-122,570.28) | 113,314.95 (112,854.42-113,449.10) |
| Close / 16 | 3 | 3,448.50 (3,438.44-3,542.41) | 3,226.22 (3,163.35-3,270.52) |
| Keep-alive / 64, first probe | 1 | 139,246.71 | 142,300.88 |
| Keep-alive / 128, next probe | 1 | 143,634.88 | 140,676.71 |
| Keep-alive / 64, later confirmation | 2 | 132,586.54 (130,928.96-134,244.11) | 127,217.96 (126,642.60-127,793.33) |

The final 16-connection keep-alive series has CV 1.21% sockets / 0.28% io_uring; close has CV 1.65% / 1.67%. Those are locally consistent short repetitions. **Absolute stability across the entire session was not achieved:** the unchanged sockets control drifted from about 143k to 121k between series. Do not attribute the smaller final gap solely to batching, or use the single 64-connection candidate lead as proof of superiority. The later 64-connection confirmation reversed that lead.

The candidate's observed limit is approximately **127k-142k keep-alive requests/s on this setup**, not a universal io_uring limit. Increasing concurrency from 64 to 128 did not increase throughput, while candidate mean latency rose from 444.35 us to 890 us. Later 64-connection means were 494.21 and 496.98 us, with p99 1.50 and 1.29 ms.

At 64/128 connections the ring pump used 93-96% of one core, predominantly system CPU. The entire server used roughly 2.8 of its four cores; the client used roughly 1.64-1.71 of its two cores. This supports a **single-pump/kernel-I/O bottleneck for this implementation**, rather than saturation of all server or client cores. The first 64-connection run spent about 19.8 server CPU microseconds/request, of which roughly 6.7 microseconds/request were on the ring pump. Per-operation allocation, pinning, task completions and ThreadPool dispatch remain costs; the extra approximately 0.75 KB/request is measured, but its individual CPU cost was not isolated.

Close mode has a different limit: wrk consumed approximately 200% CPU on its two assigned cores, while the candidate server was below its four-core budget. The load generator and repeated TLS/connection setup therefore constrain this measurement. Without a different client budget or implementation, these results do not establish either server's maximum close-mode capacity. Server traces identify TLS setup work but do not resolve native cryptographic costs into precise CPU shares.

No further tuning was attempted after this plateau and the confirmation runs. A more aggressive design would change the scope: ring sharding, reusable completion sources, reducing dispatch/allocations, or native TLS integration are not implemented here.

## Evidence index and limitations

Paths below are relative to this sample. Raw outputs are deliberately ignored by Git but persist on disk for review.

| Evidence | Location |
|---|---|
| Initial accepted comparison | `results/wrk-20260919T093415Z/` |
| Original implementation, three repeated runs | `results/wrk-20260919T093655Z/` |
| EventPipe traces and reports | `results/wrk-20260919T094128Z/` |
| Invalid intermediate batching series | `results/wrk-20260919T094423Z/` |
| Final keep-alive / 16, three repeats | `results/wrk-20260919T094952Z/` |
| Final close / 16, three repeats | `results/wrk-20260919T095230Z/` |
| Keep-alive / 64 and / 128 probes | `results/wrk-20260919T095434Z/`, `results/wrk-20260919T095516Z/` |
| Later / 64 confirmation | `results/wrk-20260919T095632Z/` |
| Final-code curl response/reuse/close checks | `results/curl-20260919T100141Z/` |
| Final-code intentional reset check | `results/rst-20260919T100143Z/` |
| Final Release build (zero warnings/errors) | `results/final-build.log` |
| Final source snapshot, without certificates or outputs | `results/final-source.tar.gz` |
| Aggregated values | `results/summary.json`, `results/statistics.txt` |

The tests use a loopback client and server on one WSL/Windows host. Affinity prevents guest SMT overlap but does not reserve physical host CPUs or eliminate host activity, frequency changes, virtualization effects or thermal drift. Run order was not randomized. Tiny diagnostics run on the request path for both backends. Lua response validation adds client work. A limited number of short repetitions is not a statistical performance study.

The warnings gate only tolerates explicitly classified `ECONNRESET` and records every occurrence. A 250 ms exit window and a count bounded by concurrency are screening heuristics, not proof that every individual reset was generated by wrk termination. The real-client RST test verifies the actual errno handling; all other warning/error categories remain failures. Graceful curl checks have no such reset warnings.

Build, curl, wrk, cancellation during server shutdown and the deliberate-reset path were exercised. Full production edge cases, native memory fault injection, exhaustive partial-I/O/backpressure tests, HTTP/2/3, full/resumed-handshake attribution, cross-platform support and offload are intentionally unverified. No PR, commit or push was created, and the pre-existing `DirectTlsTransportApp` was left untouched.

The final build and curl/reset rerun followed whitespace-only C formatting and using-order cleanup; performance behavior is unchanged from the final measured series. All 36 accepted measured outputs were subsequently rechecked with the strengthened response/liveness verifier. All servers and observation processes started by the scripts have exited.

## Plaintext follow-up: 2026-09-19 evening

Scope: measure the same application over HTTP/1.1 with TLS disabled on both backends, then try one focused optimization and explain the remaining limits. Acceptance: in-tree build; verified backend and no-TLS selection; correct individual/reused/close responses; real partial-send/backpressure fallback; no unexplained client/server errors; repeated, affinity-controlled before/after measurements with raw evidence. No native TLS, kTLS, pipe replacement, operation pooling, multishot or ring sharding was added. Changes remain uncommitted in `/home/deaglegross/code/aspnetcore`, branch `dmkorolev/network-proto`; `DirectTlsTransportApp` remains untouched.

### Timing prerequisite and methodology

The first installed-wrk attempts (`results/plaintext/baseline-c16` and `baseline-c16-retry`) failed on stock sockets, before the candidate ran. Requested 10-second intervals were reported as 8.23/8.20 seconds with 14/9 timeout counts. A separate realtime-versus-monotonic probe caught a -1.847694-second realtime adjustment (`clock-under-load.json`). wrk 4.1.0 uses `gettimeofday` for rates and request latency, so backward adjustments can also underflow its unsigned latency measurement. These attempts are rejected evidence, not accepted performance or forgiven network errors.

All accepted follow-up measurements use the same local monotonic-clock wrk 4.1.0 build on both backends. `build-wrk.sh`, `wrk-monotonic.patch` and `artifacts/networkproto-wrk/provenance.sha256` record the exact source and modification. The source archive SHA-256 is `49c309c834c484243d1f381505e7723326c5a9b6e328d88adef9ead804c8d83e`. The verifier also independently checks duration using `/proc/uptime`. No host/WSL clock settings were changed.

The rest of the environment is unchanged: WSL Ubuntu on Ryzen 9 7950X3D, kernel `6.18.33.2-microsoft-standard-WSL2`, repository runtime `.NET 11.0.0-rc.1.26453.108`, Release build, IPv4 loopback. Server affinity is `0,2,4,6` with `DOTNET_PROCESSOR_COUNT=4`; client affinity is `8,10`, two wrk threads. Affinity is inherited by wrk worker threads; the launcher execs wrk in the recorded PID. These are disjoint guest-reported physical cores, not reserved host cores.

Each headline run has a separate 10-second warmup and 10-second measured interval, three repetitions, the same 1024-byte body, response validation and diagnostics headers. HTTP is positively checked through scheme, `X-Tls: none` and zero server handshakes. Every accepted measured output has zero wrk connect/read/write/status/timeout errors and zero invalid responses. Explicit peer resets at load termination remain logged under the previously documented strict category/count/timing gate; the gate is not proof of the origin of every individual reset.

### First plaintext baseline and optimization

The initial accepted 16-connection medians were stock 147,863.75 req/s versus ring-only 89,335.42 keep-alive; close was 27,738.29 versus 10,539.62. Thus the candidate's deficit exists without TLS. Do not ascribe it solely to SslStream.

The only data-path optimization is a direct, nonblocking native send for buffers smaller than 16 KiB, with io_uring fallback on EAGAIN and the existing partial-write loop. The same binary can disable it with `NETWORKPROTO_SYNC_SEND=0`. Error classification, input pipes, cancellation and TLS integration are unchanged.

Afterward, the 16-connection keep-alive medians were stock 133,876.26 versus optimized 131,899.35 req/s, effectively near parity for these runs. Stock drifted between the initial and later series, so their difference is not a clean causal estimate of the optimization. The stronger result is the following interleaved same-binary comparison.

### Interleaved 64-connection keep-alive comparison

Round order was stock/ring-only/optimized, optimized/ring-only/stock, then stock/ring-only/optimized. Each cell below summarizes three separately warmed runs. All variants use the same server binary and client build; diagnostics are disabled.

| Variant | Median req/s | Range req/s | CV | Median p50 | Median p99 | Server CPU us/request | Allocated bytes/request |
|---|---:|---:|---:|---:|---:|---:|---:|
| Stock sockets | 162,057.05 | 161,763.76-163,569.62 | 0.60% | 221 us | 1.66 ms | 16.22 | 1,640 |
| io_uring, ring-only send | 113,808.32 | 111,717.81-114,429.18 | 1.25% | 521 us | 1.34 ms | 20.51 | 2,400 |
| io_uring, direct-send fast path | 172,631.58 | 169,853.80-172,972.07 | 1.00% | 280 us | 1.59 ms | 16.62 | 2,016 |

The optimized variant is **51.7% above ring-only** and **6.5% above stock** in median throughput here. This is a small, locally repeatable throughput win, not universal superiority: stock still has better p50 and mean latency (303 us versus 359 us), lower CPU/request and fewer allocations. wrk's latency histogram, including its correction behavior, is not a complete accounting of all client/server queueing.

The ring-only pump used 89-90% of a core. With direct sends it used 58-59%, despite substantially more throughput. Whole-server use changed from about 2.33 cores to 2.85; stock used about 2.63. We removed a serialization point and moved successful send work onto producing threads; we did not reduce all server work below stock. The optimized pump is no longer at its earlier single-core ceiling, so these data do not justify calling it the remaining bottleneck.

Separate diagnostic runs show the mechanism, without including counter overhead in headline rates. Ring-only recorded 738,515 send SQEs; the optimized run completed all 974,772 attempted small sends directly and submitted zero send SQEs. Allocations fell by approximately 384 bytes/request, consistent with avoiding one queued operation/task/closure path. Wakeups did not uniformly decrease: the optimized run processed fewer CQEs per wait and more wakeups per request. This is not a claim that every part of ring scheduling improved.

### Connection-close tradeoff and remaining limits

The matched post-change 16-connection close series remained much worse than stock: **26,499.21 req/s stock versus 9,530.03 optimized**. Unlike the earlier TLS close measurements, this plaintext custom workload did not saturate either CPU budget: about 1.65 server cores and 0.72 client cores were used. The earlier TLS client-saturation explanation does not apply here.

An additional interleaved same-binary close comparison confirmed a tradeoff in the optimization itself: ring-only median **9,884.08** versus direct-send **9,317.71 req/s**, a **5.7% regression** (three repetitions; CV 1.36%/0.58%). Keep the switch when evaluating connection-churn workloads; the default fast path is a keep-alive-oriented prototype choice, not an unconditional improvement.

A separate close profile (`close-profile`) shows work in socket disposal, synchronous send, wakeups, locks and socket-handle initialization, alongside many blocked threads. Its operation counters show roughly one accept and two receives per request, plus cancellation/wakeup/completion activity at teardown. The source has one outstanding accept with an asynchronous continuation before another accept is posted. These observations identify connection setup/teardown and scheduling as the next investigation area, but do not isolate one root cause or prove that multishot accept alone fixes the gap. EventPipe sampled-thread-time percentages include blocked time and are not CPU percentages.

Most justified next experiments, in order:

1. Reuse receive-operation/completion state to attack the remaining approximately 376-byte/request allocation gap, without changing TLS or pipe semantics.
2. Investigate accept/connection-close scheduling separately: measure acceptance/rearm delay and teardown/cancellation work; then test accept batching or preposting. The close-mode regression warrants its own explanation rather than adding more ring threads indiscriminately.
3. Measure queue-to-pump and completion-to-consumer delay in diagnostic runs before changing schedulers or inlining continuations. Avoid running arbitrary HTTP/application work on the pump.
4. Evaluate scatter sends, provided buffers/multishot and native TLS only as separately controlled architectural experiments. This study did not quantify intrinsic pipe overhead or implement TLS changes.

### Correctness boundaries and reproduction

The Release builds passed. Both backends passed plaintext and preserved HTTPS individual/reuse/close curl checks. The HTTPS real-peer RST check still passed. `send-pressure.sh` exercised native partial writes, EAGAIN, recovery and explicit EPIPE, then verified all 8192 pipelined responses on each real Kestrel backend after withholding client reads. Candidate diagnostics confirmed one partial direct send and one EAGAIN-to-io_uring fallback. No corruption or unexpected server errors occurred in that test. Exhaustive cancellation/fault injection and production lifecycle coverage remain out of scope.

Reproduce a matrix using the commands in README. For the exact 64-connection ordering:

```bash
cd ~/code/aspnetcore
sample=src/Servers/Kestrel/samples/NetworkProtoSample
export SCHEME=http WRK="$PWD/artifacts/networkproto-wrk/wrk"
export SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4
export THREADS=2 CONNECTIONS=64 WARMUP=10 DURATION=10 REPETITIONS=1 MODES=keepalive
export NETWORKPROTO_SYNC_ACCEPT=0 NETWORKPROTO_SYNC_RECEIVE=0
export NETWORKPROTO_SHUTDOWN_CLOSE=0
export NETWORKPROTO_DIRECT_SQ=0 NETWORKPROTO_REUSE_OPERATIONS=0 NETWORKPROTO_INLINE_RECEIVE=0 NETWORKPROTO_COMPACT_CONNECTIONS=0
for round in 1 2 3; do
    variants="sockets ring-only sync-send"
    if [[ "$round" == 2 ]]; then variants="sync-send ring-only sockets"; fi
    for variant in $variants; do
        export BACKENDS=io_uring NETWORKPROTO_SYNC_SEND=1
        if [[ "$variant" == sockets ]]; then
            export BACKENDS=sockets
        elif [[ "$variant" == ring-only ]]; then
            export NETWORKPROTO_SYNC_SEND=0
        fi
        bash "$sample/scripts/wrk-load.sh" "$sample/results/repeat-c64-r$round-$variant"
    done
done
```

Evidence is under `results/plaintext/`: `baseline-monotonic-c16`, `optimized-c16`, `c64-r{1,2,3}-{sockets,ring-only,sync-send}`, `close-r{1,2,3}-sync-{0,1}`, `diagnostics-sync-{0,1}`, `close-profile`, `send-pressure`, and the curl/RST directories. Each measurement directory contains raw output, commands, metadata, hashes, metrics, CPU/affinity samples and verification. `summary.json` and `grouped-statistics.txt` contain the combined data. `pre-optimization-source.tar.gz` preserves the original ring-only implementation and HTTP harness; rejected wall-clock evidence remains separate.

These are bounded same-host WSL experiments, not dedicated-host results or an established maximum. Absolute stock throughput drifted during the session; the interleaved series limits but does not remove that confound. Client Lua validation and diagnostic headers consume work on both variants. No further optimization was attempted after the fast-path result, same-binary close tradeoff and bounded profile.

## Short-lived follow-up: two additional ideas

The user subsequently requested a bounded attempt to improve short-lived connections. The full Kestrel HTTP/1.1 parser, endpoint and output pipes remain in every variant. This is not a raw TCP or canned-response benchmark.

Two independently selectable changes were evaluated: direct nonblocking accept before ring fallback, and direct nonblocking receive before ring fallback. The listener must explicitly be nonblocking for `accept4`: `SOCK_NONBLOCK` on an accepted-fd flag would not make the accept call itself nonblocking. Only the listening socket's mode changes for this experiment; accepted-fd flags remain `SOCK_CLOEXEC`. Receive uses per-call `MSG_DONTWAIT` without changing socket mode. Cancellation is checked before either attempt, and fd/buffer lifetime is preserved. There are no simultaneous raw receives for one connection: each operation finishes before its receive loop submits the next.

### Repeated close-mode comparison

Same monotonic-clock wrk, loopback, 1024-byte body, four server cores (`0,2,4,6`), two client cores (`8,10`), two wrk threads and 16 connections. Each run has 10-second warmup plus 10-second measurement. All variants keep the earlier direct-send optimization enabled. Three repetitions were interleaved: stock/previous/accept/receive/both, the reverse order, then the original order. No diagnostic counters or traces were enabled in these headline runs.

| Variant | Median req/s | Range req/s | Median p50 | Server CPU us/request | Allocated bytes/request |
|---|---:|---:|---:|---:|---:|
| Stock sockets | 24,512.69 | 20,118.49-24,833.47 | 300 us | 85.85 | 13,887 |
| Previous custom: direct send only | 9,215.16 | 9,066.29-10,713.86 | 1,480 us | 177.47 | 15,448 |
| Add direct accept | 17,873.54 | 16,727.44-19,104.32 | 457 us | 112.85 | 14,955 |
| Add direct receive only | 10,017.99 | 9,900.23-10,756.24 | 1,290 us | 166.63 | 14,382 |
| Add direct accept and receive | 18,786.99 | 17,483.54-20,560.40 | 434 us | 109.09 | 14,483 |

Direct accept improved median close throughput by approximately **94%** over the previous custom implementation. Both changes together improved it by approximately **104%**, but still trailed stock by **23.4%**. The original deficit in this series was **62.4%**. This narrows the gap substantially; it does not meet a claim of beating stock on short-lived connections. Host drift remains visible, so the individual ranges and matched controls matter.

### Why accept helped

The old accept always allocated/enqueued an operation, waited for an SQE/CQE, resumed an asynchronous continuation, constructed the connection and then allowed the next accept. Queued connections could not take a synchronous path. The new path drains available accepts without that forced ring/thread handoff, similar in purpose to synchronous completion in the stock Socket API.

Separate diagnostic runs confirm the path was reached: direct accept completed **122,626 of 134,403 attempts (91.2%)** immediately; only 11,777 accepts were submitted to the ring. Pump waits per delivered response fell from approximately 3.14 to 1.08, and eventfd wakes from approximately 2.23 to 0.77. These count changes, together with the controlled switch comparison and CPU/request reduction, support accept dispatch overhead as a major contributor. The pump was not CPU-saturated in close mode; distributing it across more cores would not directly address the forced handoffs.

With both changes enabled, direct receive completed 81,701 of 272,693 attempts (30.0%) in its diagnostic run. The rest still required ring operations, and cancellation/teardown remained. Both-side server CPU stayed around 2.05 of four cores versus approximately 2.11 for stock; custom client use was below two cores. The remaining deficit is not an exhausted server CPU budget. Precise attribution among socket wrapping, cancellation, connection/pipe disposal and scheduling is still unresolved; no claim is made that pipes themselves are the cause.

### Keep-alive tradeoff and retained defaults

Two reversed-order keep-alive checks at 64 connections used the same 10+10-second intervals. These are a regression screen, not another extensive study:

| Variant | Requests/s, run 1 | Requests/s, run 2 |
|---|---:|---:|
| Stock sockets | 172,937.60 | 174,077.93 |
| Previous custom | 190,029.07 | 183,870.10 |
| Direct accept | 195,165.01 | 196,736.64 |
| Direct accept and receive | 181,746.21 | 182,856.32 |

Direct accept is retained by default (`NETWORKPROTO_SYNC_ACCEPT=1`): it improves close mode without an observed keep-alive penalty. Direct receive remains opt-in (`NETWORKPROTO_SYNC_RECEIVE=0` by default): both runs were about 7% below accept-only keep-alive throughput, despite its additional close-mode benefit. The new defaults therefore choose approximately 17.9k close throughput in the measured series, not the fastest 18.8k close-only configuration.

These remain hybrid transports. When immediate syscalls succeed, those operations do not enter io_uring; would-block operations still use actual io_uring SQEs/CQEs. No endpoint, HTTP parsing, body generation or response validation was bypassed.

### Reproduction and evidence

Use the README close command, or reproduce the exact variant order:

```bash
cd ~/code/aspnetcore
sample=src/Servers/Kestrel/samples/NetworkProtoSample
export SCHEME=http WRK="$PWD/artifacts/networkproto-wrk/wrk"
export SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4
export THREADS=2 CONNECTIONS=16 WARMUP=10 DURATION=10 REPETITIONS=1 MODES=close
export NETWORKPROTO_SYNC_SEND=1
export NETWORKPROTO_SHUTDOWN_CLOSE=0
export NETWORKPROTO_DIRECT_SQ=0 NETWORKPROTO_REUSE_OPERATIONS=0 NETWORKPROTO_INLINE_RECEIVE=0 NETWORKPROTO_COMPACT_CONNECTIONS=0
for round in 1 2 3; do
    variants="sockets previous accept receive both"
    if [[ "$round" == 2 ]]; then variants="both receive accept previous sockets"; fi
    for variant in $variants; do
        export BACKENDS=io_uring NETWORKPROTO_SYNC_ACCEPT=0 NETWORKPROTO_SYNC_RECEIVE=0
        case "$variant" in
            sockets) export BACKENDS=sockets ;;
            accept) export NETWORKPROTO_SYNC_ACCEPT=1 ;;
            receive) export NETWORKPROTO_SYNC_RECEIVE=1 ;;
            both) export NETWORKPROTO_SYNC_ACCEPT=1 NETWORKPROTO_SYNC_RECEIVE=1 ;;
        esac
        bash "$sample/scripts/wrk-load.sh" "$sample/results/repeat-close-r$round-$variant"
    done
done
```

All 15 close and eight keep-alive measured runs passed response, actual-connection-close/reuse, zero-TLS-handshake, timing and strict client/server error checks. Explicit peer-reset warnings remain subject to the previously documented category/count/timing gate; they are not suppressed or classified solely by time.

Final Release build, HTTP and preserved HTTPS curl lifecycles, real-client HTTPS RST, and 8192-response slow-reader tests passed. Native checks exercise queued/empty accepts, CLOEXEC, available/empty receive, EOF, partial send, EAGAIN fallback and explicit EPIPE. Both retained-default and opt-in receive configurations were exercised. The diagnostic counters prove real ring fallback, not just successful direct syscalls.

Evidence is under `results/short-lived/`: `r{1,2,3}-{sockets,previous,accept,receive,both}`, `keepalive-r{1,2}-{sockets,previous,accept,both}`, `diagnostics-{previous,accept,both}`, final curl/pressure/RST directories, `summary.json`, and build/source artifacts. Final changes remain uncommitted in the designated WSL branch; the pre-existing `DirectTlsTransportApp` is untouched. No further optimization was attempted after these two ideas and their tradeoff checks.

## Graceful teardown: closing the short-lived gap

On the user's next continuation, one additional policy change was evaluated: finish normal output with `Socket.Shutdown(Both)` instead of routing normal closure through cancellation of the pending receive. Kestrel processing, application payload, backpressure and fd/pin completion ownership remain unchanged. Abort/error paths retain cancellation. Receive optimization stays independently selectable.

The old sequence was output drained, cancel token, enqueue cancel SQE, wait for receive terminal CQE and task cancellation, finish both loops, then dispose the socket. The new sequence initiates TCP shutdown immediately after output drains, lets the receive complete normally, then disposes only after both loops finish. In both cases the original receive CQE owns the pin and fd reference until completion. The change removes unnecessary cancellation work and advances when the client can observe TCP closure; the experiment does not separately attribute the gains between these two effects.

### Repeated close results

Same monotonic wrk, Release build, plaintext HTTP/1.1, 1024-byte payload, 16 connections, two client threads, four server cores and two client cores. Each run has 10-second warmup plus 10-second measurement. Three repetitions used stock/previous/shutdown/shutdown-plus-receive, reverse order, then original order. Direct accept and small-send optimization are enabled in all custom variants. Diagnostics are off.

| Variant | Median req/s | Range req/s | Median p50 | Median p99 | Server CPU us/request | Allocated bytes/request |
|---|---:|---:|---:|---:|---:|---:|
| Stock sockets | 28,894.20 | 28,868.87-29,081.92 | 233 us | 1.14 ms | 76.48 | 13,917 |
| Previous custom, cancellation-first close | 19,484.22 | 19,479.45-19,618.20 | 389 us | 2.29 ms | 98.02 | 14,955 |
| Graceful shutdown, receive fast path off | 29,691.21 | 29,173.95-30,034.16 | 255 us | 1.27 ms | 82.80 | 14,580 |
| Graceful shutdown plus receive fast path | 30,869.05 | 30,825.64-31,000.74 | 245 us | 1.20 ms | 79.73 | 14,251 |

The retained default is **52.4% above the previous custom implementation and 2.8% above stock** in median close throughput. Enabling optional direct receive gives **6.8% above stock** here. All three rounds show a throughput lead for both shutdown variants, but this is still a small same-host result, not proof of general superiority. Stock retains lower CPU/request, lower allocation and better reported median/tail latency. The throughput lead does not imply an efficiency or latency win.

### Mechanism evidence

Separate close diagnostic runs observed 120,681 native cancellation requests for approximately 133,480 responses with cancellation-first close, versus just one cancellation request for approximately 191,439 responses with graceful shutdown. The remaining cancellation is the listener's pending accept at stop.

A smaller controlled `close-eof.sh` run removed ambiguity from load-generator termination: 256 distinct requests per backend, exact body/header checks, then wait for server EOF while keeping the client write side open. Both policies produced correct EOF, but the custom control issued **257** cancellation requests versus **one** with the new policy. Thus this is an optimization of valid normal closure, not a claim that the previous transport could never close correctly. No warnings or errors occurred in that check.

This supports two major findings across the investigation: queued accepts should not require an unconditional asynchronous handoff, and normal output completion should not be forced through the abort/cancellation path. The earlier large short-lived loss was not an intrinsic io_uring or TLS limitation. The remaining CPU/allocation difference versus stock was not separately optimized.

### Keep-alive, correctness and retained configuration

Two reversed-order 64-connection keep-alive checks showed no observed regression: previous custom 203,732.52/202,859.37 req/s, graceful shutdown 203,160.22/204,983.23, stock 178,879.03/180,569.01. These are a limited regression screen; normal keep-alive traffic does not invoke the changed closure path until connection termination.

`NETWORKPROTO_SHUTDOWN_CLOSE=1` is retained by default. `NETWORKPROTO_SYNC_RECEIVE=0` stays the default because its prior keep-alive tradeoff remains; use `1` explicitly for the highest short-lived result above. `NETWORKPROTO_SYNC_ACCEPT=1` and `NETWORKPROTO_SYNC_SEND=1` remain enabled. Disabling the shutdown switch reproduces the previous behavior with the same binary.

Release build, HTTP/HTTPS curl reuse and close, real-peer HTTPS RST, partial native send, empty/queued accept, available/empty receive and EOF, plus 8192-response pressured-send checks passed. Both default and optional direct-receive paths exercised real io_uring fallback. Error categories were not broadened or suppressed: socket shutdown failures are errors, peer resets remain explicit, and cancelled/error output retains the original cancellation path. Production fault injection and exhaustive shutdown races remain unverified.

Reproduce the exact close ordering:

```bash
cd ~/code/aspnetcore
sample=src/Servers/Kestrel/samples/NetworkProtoSample
export SCHEME=http WRK="$PWD/artifacts/networkproto-wrk/wrk"
export SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4
export THREADS=2 CONNECTIONS=16 WARMUP=10 DURATION=10 REPETITIONS=1 MODES=close
export NETWORKPROTO_SYNC_SEND=1 NETWORKPROTO_SYNC_ACCEPT=1
export NETWORKPROTO_DIRECT_SQ=0 NETWORKPROTO_REUSE_OPERATIONS=0 NETWORKPROTO_INLINE_RECEIVE=0 NETWORKPROTO_COMPACT_CONNECTIONS=0
for round in 1 2 3; do
    variants="sockets previous shutdown shutdown-receive"
    if [[ "$round" == 2 ]]; then variants="shutdown-receive shutdown previous sockets"; fi
    for variant in $variants; do
        export BACKENDS=io_uring NETWORKPROTO_SHUTDOWN_CLOSE=0 NETWORKPROTO_SYNC_RECEIVE=0
        case "$variant" in
            sockets) export BACKENDS=sockets ;;
            shutdown) export NETWORKPROTO_SHUTDOWN_CLOSE=1 ;;
            shutdown-receive) export NETWORKPROTO_SHUTDOWN_CLOSE=1 NETWORKPROTO_SYNC_RECEIVE=1 ;;
        esac
        bash "$sample/scripts/wrk-load.sh" "$sample/results/repeat-teardown-r$round-$variant"
    done
done
```

Raw evidence is in `results/teardown/`: `r{1,2,3}-{sockets,previous,shutdown,shutdown-receive}`, `keepalive-r{1,2}-{sockets,previous,shutdown}`, `diagnostics-{0,1}`, `eof-control`, `eof-final`, curl/pressure/RST directories, build logs and source snapshots. This phase contains 12 accepted close measurements and six accepted keep-alive measurements, each with a separately verified warmup. Full commands, environment, hashes, CPU/affinity, counters and error checks remain with each run. No commit, push, new worktree, native TLS or kTLS change was made.

## Allocation and latency: reusable completions and bounded receive inlining

The user requested fewer allocations and better latency. Acceptance was defined before these changes: retain the complete Kestrel HTTP request/response path, reduce measured allocation and latency relative to the previous prototype, retain explicit errors and terminal-CQE ownership, exercise actual partial-I/O/reset/close paths, and repeat controlled comparisons rather than selecting one favorable result. No TLS architecture, HTTP parser, payload, native operation type or buffer registration strategy changed.

Three changes are retained, each with a same-binary control. `NETWORKPROTO_REUSE_OPERATIONS=1` replaces per-I/O operation/task/closure allocation with lazily created, owner-local `ManualResetValueTaskSourceCore<int>` state. `NETWORKPROTO_INLINE_RECEIVE=1` lets small positive receive completions publish into the input pipe and rearm receive without an extra ThreadPool handoff; application readers remain ThreadPool-scheduled. EOF, errors, large receives, accepts and sends still resume asynchronously. `NETWORKPROTO_COMPACT_CONNECTIONS=1` shares immutable pipe options and avoids linked accept cancellation sources when there is no cancellable caller token. Reuse/inline/compact now default to one; all three zero restore the preceding transport for comparison. Inline requires reuse.

The reusable operation retains versioned consumption and a unique engine ID on every submission. It disposes the old cancellation registration and releases only terminal-completion-owned pins/fd references before signaling. Cancellation commands capture IDs by value, so an old queued cancellation cannot cancel the next use of the same managed object. There is no cross-connection object pooling or unsafe early buffer return.

### Repeated comparison

The main matrix used three alternating-order rounds: sockets/previous/optimized, optimized/previous/sockets, sockets/previous/optimized. Each round exercised keep-alive at 64 connections and close at 16. Each run had a separate 10-second warmup and 10-second measurement. A subsequent single-connection latency matrix used the same alternating order, one wrk thread, a five-second warmup and ten-second measurement. The client still had CPUs `8,10` available; the server remained on `0,2,4,6` with four processors. All timed runs had diagnostic counters/tracing disabled.

The same Release binary, monotonic-clock wrk, runtime, HTTP/1.1 application, 1024-byte body and verification were used throughout the matrix. Every request traversed Kestrel HTTP and the sample endpoint. Neither TLS backend was used for these performance measurements; both explicitly reported no TLS. The optimized variant means all three new switches enabled; previous means all three disabled. Both custom variants retain direct send/accept and graceful shutdown, with direct receive disabled.

Each value below is a median of three runs, not a percentile pooled across runs. Allocations are process-wide allocated-byte deltas divided by server requests, not retained heap size.

| Workload | Variant | Requests/s | Bytes/request | Mean latency us | p50 us | p99 us | Server CPU us/request |
|---|---|---:|---:|---:|---:|---:|---:|
| Keep-alive, 64 connections | Stock sockets | 165,748.83 | 1,640.18 | 295.10 | 204 | 1610 | 15.44 |
| Keep-alive, 64 connections | Previous custom | 181,841.65 | 2,016.24 | 333.69 | 270 | 1420 | 15.91 |
| Keep-alive, 64 connections | Optimized custom | 177,547.40 | 1,632.24 | 327.26 | 267 | 1010 | 14.97 |
| Close, 16 connections | Stock sockets | 27,791.53 | 13,909.25 | 314.25 | 247 | 1270 | 78.43 |
| Close, 16 connections | Previous custom | 28,610.28 | 14,590.52 | 365.59 | 271 | 1360 | 86.42 |
| Close, 16 connections | Optimized custom | 29,546.35 | 13,628.87 | 326.90 | 243 | 1300 | 82.73 |
| Keep-alive, 1 connection | Stock sockets | 10,685.51 | 1,641.19 | 91.56 | 79 | 194 | 161.42 |
| Keep-alive, 1 connection | Previous custom | 9,131.64 | 2,016.81 | 106.46 | 95 | 221 | 193.60 |
| Keep-alive, 1 connection | Optimized custom | 10,926.02 | 1,633.42 | 90.49 | 77 | 193 | 158.37 |

Relative to the preceding custom transport, keep-alive allocation fell approximately **384 bytes/request (19%)**, close allocation approximately **962 bytes/request (6.6%)**, close p50 **10.3%**, and single-connection p50 **18.9%**. Keep-alive/64 p99 improved **28.9%**, but its throughput median decreased **2.4%**. Its p50 improved only 1.1%; this is not a substantial high-concurrency median-latency improvement.

Compared with stock, the optimized median throughput is 7.1% higher at keep-alive/64 and 6.3% higher at close/16, but these are not maximum-capacity claims. Stock still has distinctly better keep-alive/64 p50 (204 versus 267 us), and better close mean/p99 (314/1270 versus 327/1300 us) and CPU/request. The approximately 8-byte keep-alive allocation lead is near parity, not an important advantage; close allocation is about 280 bytes/request below stock. Single-connection stock versus optimized latency is also near parity: the 2 us p50 and 1 us p99 differences do not establish a meaningful win on this host.

### Stability, attribution and remaining limit

All three matched rounds reduced allocation, close p50, single-connection p50 and keep-alive/64 p99 relative to the preceding custom transport. Throughput is less consistent. Optimized keep-alive/64 ranged **145,521.69-183,419.92** requests/s; previous ranged **172,916.42-187,105.77**, and stock **156,691.28-168,656.71**. The first optimized keep-alive run was notably slower; it passed every correctness/liveness gate and is retained, not discarded as an unexplained error. Close optimized ranged **29,467.60-30,475.39**, previous **26,591.06-28,873.22**, stock **26,186.67-27,883.35**. Single-connection optimized ranged **10,638.10-10,972.84**, previous **8,528.71-9,917.12**, stock **9,363.49-10,698.71**.

These ranges preclude claiming stable absolute throughput or broad statistical significance. Same-host WSL activity, frequency/scheduling variation, wrk response validation and a small closed-loop workload remain confounds. The low-concurrency check reduces client CPU saturation but is not a fixed-offered-rate latency study. In close mode wrk's request histogram does not account for the entire TCP establishment lifecycle; connection turnover throughput and the independent EOF checks provide separate evidence.

The allocation mechanism has direct evidence: the first reuse-only screen reduced keep-alive allocation from about 2016 to 1632 bytes/request. The separate all-ring diagnostic run subsequently completed **1,369,260 reusable submissions with only 77 reusable objects created** across its short keep-alive warmup/measurement. This included 684,629 receive and 684,591 send submissions, so reuse was not merely the direct-send fast path bypassing state allocation. That diagnostic run is not a headline performance comparison.

Small-receive inlining removes a transport scheduling hop, but moves pipe publication/rearm work onto the single pump. In the main optimized keep-alive runs, that pump used about 60-64% of a core versus 58-59% before; the whole optimized server used about 2.65-2.68 cores of four. It is not currently shown to saturate one pump or all four cores. The measurement establishes less allocation and some lower latency, not that pipes, the client, or one kernel call uniquely determines the remaining limit. No new CPU-only profile or heap dump was collected in this iteration, and earlier EventPipe wall-time percentages must not be repurposed as precise attribution.

The retained choice prioritizes the requested allocation/tail/low-concurrency latency gains over the previous transport's slightly higher keep-alive throughput. Keep `NETWORKPROTO_INLINE_RECEIVE=0` available for throughput-oriented follow-up; the one-run reuse-only screen is not enough to establish its stable optimum. Further scheduler/ring-sharding/multishot or native-TLS changes would be separate experiments, not conclusions justified by these data.

### Correctness evidence and reproduction

The in-tree Release build succeeded. HTTP and HTTPS curl checked single requests, genuine reuse and actual fresh close connections on both backends. The native pressure probe verified partial writes, EAGAIN, recovery and EPIPE; the real Kestrel slow-reader probe delivered 8192 intact pipelined responses on each backend and observed actual ring-send fallback. The EOF probe verified 256 distinct complete responses followed by server-initiated EOF per backend, with one listener cancellation on the custom path. The real TLS-client RST remained explicitly classified and logged, not suppressed.

With direct send/accept disabled, HTTPS curl and intentional RST passed, followed by separate keep-alive/close ring-only load checks. These exercise repeated reusable sends as well as receives, and terminal cancellation at listener shutdown. Single-connection measured requests exceeded the 16-bit completion version range without a consumption failure. None of this substitutes for exhaustive cancellation-race/fault injection, native memory analysis, mixed-request fairness, HTTP/2/3 or production hardening.

All 27 main/low-concurrency measurements and their warmups passed response, clock, connection-mode, socket-error and server-error gates. The two ring-only stress measurements passed the same gates. Explicit native peer-reset warnings remained at fixed-duration client shutdown; the errno path is independently reproduced, while timing/count remains only a screening heuristic. No other error category was relaxed. A zero-exception claim is intentionally not made.

Reproduce the exact main and subsequent latency ordering with the following bounded matrix. Build first using `scripts/build.sh`; do not overlap it with another measurement. The recorded source and binaries used explicit switch settings; a later rebuild only enabled those tested settings as defaults and added the transport-assembly hash to future environment records.

```bash
cd ~/code/aspnetcore
sample=src/Servers/Kestrel/samples/NetworkProtoSample
export SCHEME=http WRK="$PWD/artifacts/networkproto-wrk/wrk"
export SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4
export DURATION=10 REPETITIONS=1 NETWORKPROTO_DIAGNOSTICS=0
export NETWORKPROTO_SYNC_SEND=1 NETWORKPROTO_SYNC_ACCEPT=1
export NETWORKPROTO_SYNC_RECEIVE=0 NETWORKPROTO_SHUTDOWN_CLOSE=1
export NETWORKPROTO_DIRECT_SQ=0
for phase in load latency; do
    for round in 1 2 3; do
        modes="keepalive close"
        if [[ "$phase" == latency ]]; then modes=keepalive; fi
        for mode in $modes; do
            export MODES="$mode" THREADS=2 WARMUP=10 CONNECTIONS=64
            if [[ "$mode" == close ]]; then export CONNECTIONS=16; fi
            if [[ "$phase" == latency ]]; then export THREADS=1 WARMUP=5 CONNECTIONS=1; fi
            variants="sockets previous optimized"
            if [[ "$round" == 2 ]]; then variants="optimized previous sockets"; fi
            for variant in $variants; do
                export BACKENDS=io_uring NETWORKPROTO_REUSE_OPERATIONS=0
                export NETWORKPROTO_INLINE_RECEIVE=0 NETWORKPROTO_COMPACT_CONNECTIONS=0
                case "$variant" in
                    sockets) export BACKENDS=sockets ;;
                    optimized)
                        export NETWORKPROTO_REUSE_OPERATIONS=1 NETWORKPROTO_INLINE_RECEIVE=1
                        export NETWORKPROTO_COMPACT_CONNECTIONS=1 ;;
                esac
                bash "$sample/scripts/wrk-load.sh" "$sample/results/repeat-allocation-$phase-r$round-$mode-$variant"
            done
        done
    done
done
```

Evidence is in `results/allocations/`: `final-r{1,2,3}-{keepalive,close}-{sockets,previous,optimized}`, `latency-r{1,2,3}-{sockets,previous,optimized}`, `summary.json`, `latency-summary.json`, preliminary `r1-*` screens, curl/pressure/EOF/RST checks, `ring-only-load`, build logs, `before-source.tar.gz`, `measured-source.tar.gz` and `measured-binaries.sha256`. The last file additionally records the exact transport assembly hash missing from older per-run environment files. All implementation remains in the designated WSL branch, uncommitted; the pre-existing `DirectTlsTransportApp` is untouched.

### Supplementary HTTPS load check: rejected, unresolved

After enabling the tested defaults, the Release build and HTTP/HTTPS curl, pressure, EOF and deliberate RST checks passed again (`build-defaults.log`, `defaults-curl-*`, `defaults-pressure`, `defaults-eof`, `defaults-rst`). An additional HTTPS wrk check used two threads, eight connections, three-second warmup and five-second measurement on both transports/modes. Response/body/backend/TLS and client socket-error checks passed, but the **custom close-mode server-warning gate failed**. Unlike accepted plaintext measurements, many native `ECONNRESET` warnings occurred during traffic, not only at the process deadline. `defaults-https-load` is therefore rejected, not a new accepted comparison.

Three same-binary HTTPS close probes then explicitly selected previous completion/setup behavior, reuse with asynchronous completion, and reuse with small-receive inlining. All failed the same strict gate: 11,227 / 11,591 / 8,124 peer-reset warnings respectively across their warmup and measured intervals. They reported zero client HTTP/socket errors, but that does not override the server gate. Evidence is retained in `tls-close-{previous,reuse,inline}` and their `.log` files. This establishes that the warning pattern also exists with the new allocation/inline/compact changes disabled; it does not prove every reset is harmless or identify its precise cause.

Inspection of the exact local wrk source found that `response_complete` immediately calls `reconnect_socket` for a close response, which calls `ssl_close` and then `close(fd)`. `ssl_close` calls `SSL_shutdown` once, does not handle its nonblocking result or wait for reciprocal TLS shutdown, and calls `SSL_clear`. That is a plausible client-close interaction, not verified packet-level attribution. No capture or controlled reproduction of that specific one-shot shutdown path was completed. The existing deliberate `SO_LINGER` reset test establishes errno classification, not this separate producer behavior.

The bounded allocation/latency results above are accepted **plaintext** results. Basic HTTPS response/reuse/close is verified by curl, but strict HTTPS wrk-close validation remains unresolved and is not claimed complete. No logging, warning category, error gate or client behavior was changed to make these failed runs pass. There are no accepted new TLS performance numbers from this supplementary check.

## Full-ring submission experiment: direct SQ versus typed pump queue

The next user request was to eliminate generic command/dictionary overhead, try preparing SQEs immediately with queueing only when full, and stop obtaining performance gains by bypassing io_uring. Acceptance was recorded before implementation: in-tree build, genuine ring accept/receive/send, safe terminal ownership/cancellation, real SQ overflow/retry and partial-I/O checks, and a repeated matched comparison including unsuccessful outcomes. No multishot or TLS-layer change was included.

All measurements in this section explicitly set `NETWORKPROTO_SYNC_SEND=0`, `NETWORKPROTO_SYNC_ACCEPT=0`, and `NETWORKPROTO_SYNC_RECEIVE=0`. All custom accept/receive/send operations use SQEs/CQEs; direct syscall methods remain only as disabled historical experiments. Graceful `Socket.Shutdown` still closes connections after writes drain; ring-only data movement does not imply that binding, socket options or shutdown stop using normal socket APIs.

The client is **wrk 4.1.0, not wrk2**, using the previously documented monotonic-clock build. These are same-host closed-loop measurements, not equal offered-rate latency measurements. Setup remains plaintext HTTP/1.1, 1024-byte response through the actual Kestrel endpoint, two client threads on CPUs `8,10`, server CPUs `0,2,4,6`, four .NET processors. Each measurement has a separately verified ten-second warmup and ten-second measured interval. Three rounds interleave variants and reverse their order in round two. All values below are medians of three individual runs. Source/runtime/kernel/package metadata, command lines, binary hashes, metrics and pidstat outputs are retained with each run.

### First direct-SQ result: a large regression, not a win

The first version replaced the dictionary with generation-tagged operation slots and prepared SQEs directly from callers under a common lock. Only SQ-full overflow was queued. The pump held the same lock during submission, consumed CQEs outside it, and acquired it again for each completion-slot transition. The initial full matrix passed the response/lifecycle/error gates, but was substantially slower:

| Mode | Stock requests/s | Previous full-ring requests/s | First direct-SQ requests/s | Direct-SQ versus previous |
|---|---:|---:|---:|---:|
| Keep-alive, 64 connections | 187,908.53 | 149,816.57 | 52,912.53 | -64.7% |
| Close, 16 connections | 30,434.37 | 16,580.03 | 11,107.24 | -33.0% |

Direct-SQ keep-alive p50/p99 were 1180/2020 us versus 386/940 us for the previous full-ring path. Close p50/p99 were 1330/2180 us versus 841/1810 us. Server CPU/request increased from 16.40 to 36.65 us for keep-alive and from 154.49 to 239.68 us for close. The direct keep-alive range was 51,145.06-53,038.44 requests/s; this was not one isolated bad sample.

Separate diagnostics showed about 2.9 CQEs per collection call on the direct path versus 31.5 on the old queue path, and approximately 0.35 versus 0.06 eventfd wakes per send. Those counters are not from timed headline runs. Removing the per-CQE lock, while preserving generation/ownership rules, only reached 56,767.41 keep-alive and 10,782.76 close requests/s in one screen. It did not solve the regression.

A bounded alternative retains the slots but gives SQ ownership back to the pump: cross-thread callers enqueue typed reusable operations, pump-thread callers prepare directly or queue SQ overflow, and kernel submission no longer holds the producer lock. A preliminary screen returned to about 148k keep-alive and 16.8k close requests/s. Legacy delegate creation was then made lazy so this path does not allocate unused submission delegates. This alternative is the retained default; fully caller-thread SQ preparation remains opt-in for instruction and reproduction.

This comparison implicates the shared-ring scheduling/synchronization design, but does not isolate a single cause. Changing to pump ownership changes lock contention, submission batching, wake timing and which threads touch SQ cache lines together. No CPU-only profile establishes their separate contributions. It is not evidence that direct submission is intrinsically slow in a thread-local runtime such as Tokio, or that io_uring generally needs multishot to be competitive.

### Final same-binary comparison

The final binary keeps all three variants selectable. `previous` means `NETWORKPROTO_DIRECT_SQ=0`; `typed` means `NETWORKPROTO_DIRECT_SQ=1 NETWORKPROTO_STAGE_ON_PUMP=1`. Reuse, receive inlining, compact connections and graceful shutdown are enabled identically on both custom variants.

| Mode | Variant | Requests/s | Bytes/request | Mean us | p50 us | p99 us | Server CPU us/request |
|---|---|---:|---:|---:|---:|---:|---:|
| Keep-alive, 64 | Stock sockets | 189,706.88 | 1,640.15 | 238.44 | 170 | 1190 | 14.19 |
| Keep-alive, 64 | Previous full-ring | 148,354.02 | 1,632.30 | 425.64 | 391 | 900 | 16.60 |
| Keep-alive, 64 | Typed queue/slots | 148,576.05 | 1,632.28 | 430.10 | 396 | 880 | 16.62 |
| Close, 16 | Stock sockets | 30,608.01 | 13,918.43 | 277.64 | 220 | 1110 | 76.25 |
| Close, 16 | Previous full-ring | 16,632.63 | 14,040.82 | 880.00 | 843 | 1720 | 155.25 |
| Close, 16 | Typed queue/slots | 16,511.25 | 13,937.23 | 900.00 | 850 | 1760 | 152.47 |

**The submission optimization did not establish a throughput or latency win.** Typed-slot throughput is effectively unchanged: +0.15% keep-alive and -0.73% close versus the previous ring-only median. Keep-alive allocation is unchanged. Close allocation fell about **104 bytes/request (0.74%)**, and measured CPU/request fell about 1.8%. Close p50/p99 and keep-alive p50 were slightly worse, not better; the 20 us keep-alive p99 reduction alone is not a convincing latency improvement.

All three matched rounds had lower close allocation and CPU/request for typed slots, but throughput changes versus the old ring path were small and mixed. Keep-alive ranges were 186,697-189,865 stock, 147,456-150,317 previous and 147,002-150,284 typed. Close ranges were 29,990-30,750 stock, 16,001-16,689 previous and 16,002-16,718 typed. The first close round was lower for all three, illustrating shared-host drift.

Compared with stock, retained full-ring throughput is approximately **21.7% lower on keep-alive and 46.1% lower on close**. Stock also has substantially lower mean/p50 latency and CPU/request. The custom keep-alive p99 happens to be lower in this closed-loop workload; that does not offset its lower throughput or establish better latency at the same offered load. Earlier hybrid wins are not evidence for full-ring superiority.

### Implementation and faithful checks

CQE `user_data` is `(generation << 32) | (slotIndex + 2)`; 0 identifies wake completion and 1 cancellation acknowledgment. Slots retain their operation while active, reject stale generations, and are released by connection/listener disposal only after result consumption. The table grows by publishing a larger array containing the same slot objects. Completion dispatch does not acquire the producer lock, and signaling happens only after marking the old operation inactive and releasing terminal-owned resources.

The retained pump-owned path uses a typed queue for cross-thread transfer, not `_commands` delegates or `_pending` dictionary lookups. Pump-local submissions try `np_stage` immediately and use a memory queue on SQ-full. Fully direct mode tries `np_stage` from every caller under the SQ lock; it also preserves queued overflow ordering. Both keep separate cancellation IDs and delay cancellation submission until the target's queued SQE has been staged. Neither dispatch path treats cancellation acknowledgment as permission to unpin an outstanding original operation.

The final build passed, followed by HTTP/HTTPS individual, reused and fresh-close curl checks on both backends; 256 server-initiated EOF checks/backend; and an intentional TLS-client RST with explicit logging/classification. `ring-pressure.sh` passed in both SQ strategies with a two-entry ring: native SQ-full/retry, buffer identity, cancellation acknowledgment plus original `-ECANCELED`, and a real partial ring send. Real Kestrel delivered 8192 intact pipelined responses after withholding reads, followed by 512 synchronized close connections with exact body/EOF and bounded fd count. Both runs recorded nonzero managed overflow, zero active operations/occupied slots on shutdown, and zero direct accept/receive/send attempt counters.

Additional 256-connection keep-alive checks forced both slot engines to grow from the initial 256-entry array to **513 allocated slot indices**, later returning active/occupied counts to zero. The typed run completed 2,165,734 original operations through 1,037 reusable objects. These diagnostic rates are excluded from the performance tables. The slot count represents allocated/reused indices, not an exact live-connection peak.

The 18 initial and 18 final timed runs and their warmups passed response/body/backend, connection-mode, monotonic-duration, liveness and error checks. Explicit end-of-client-run peer-reset warnings remain subject to the existing strict count/time screen; they were not relabeled as zero exceptions. The unresolved supplementary HTTPS wrk-close warning pattern was not retested or declared fixed; only plaintext performance and HTTPS curl functionality are claimed here. Exhaustive cancellation races, mixed-request fairness, multishot, registered/provided buffers and production recovery remain outside this iteration.

### Reproduction and persisted evidence

Run the following after `scripts/build.sh`, without overlapping other measurements:

```bash
cd ~/code/aspnetcore
sample=src/Servers/Kestrel/samples/NetworkProtoSample
export SCHEME=http WRK="$PWD/artifacts/networkproto-wrk/wrk"
export SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4
export THREADS=2 WARMUP=10 DURATION=10 REPETITIONS=1
export NETWORKPROTO_SYNC_SEND=0 NETWORKPROTO_SYNC_ACCEPT=0 NETWORKPROTO_SYNC_RECEIVE=0
export NETWORKPROTO_REUSE_OPERATIONS=1 NETWORKPROTO_INLINE_RECEIVE=1
export NETWORKPROTO_COMPACT_CONNECTIONS=1 NETWORKPROTO_SHUTDOWN_CLOSE=1
export NETWORKPROTO_STAGE_ON_PUMP=1 NETWORKPROTO_DIAGNOSTICS=0
for round in 1 2 3; do
    variants="sockets previous typed"
    if [[ "$round" == 2 ]]; then variants="typed previous sockets"; fi
    for mode in keepalive close; do
        export MODES="$mode" CONNECTIONS=64
        if [[ "$mode" == close ]]; then export CONNECTIONS=16; fi
        for variant in $variants; do
            export BACKENDS=io_uring NETWORKPROTO_DIRECT_SQ=1
            case "$variant" in
                sockets) export BACKENDS=sockets ;;
                previous) export NETWORKPROTO_DIRECT_SQ=0 ;;
            esac
            bash "$sample/scripts/wrk-load.sh" "$sample/results/repeat-submission-r$round-$mode-$variant"
        done
    done
done
```

Use `NETWORKPROTO_DIRECT_SQ=1 NETWORKPROTO_STAGE_ON_PUMP=0` to exercise the retained caller-thread SQ experiment. That final version includes the later lock removal and lazy delegates; use the archived initial source, not the current binary, for a byte-for-byte reproduction of the first regressed implementation.

Evidence is under `results/submission/`: `r{1,2,3}-{keepalive,close}-{sockets,previous,direct}` plus `summary.json` for the first comparison; `no-cq-lock-*` and `pump-stage-*` for bounded screens; `final-r{1,2,3}-{keepalive,close}-{sockets,previous,typed}` and `final-summary.json` for the final comparison; `diagnostic-initial-*`, `pressure-final-{0,1}`, `slot-growth-{0,1}`, `curl-final-{http,https}`, `eof-final`, `rst-final`; build logs; and `before-source.tar.gz`, `iteration1-direct-source.tar.gz`, `final-measured-source.tar.gz`. All scripts and instructional probes remain beside the sample. The unrelated `.vscode/settings.json` change and `DirectTlsTransportApp` were preserved. No commit, push, PR, new worktree, additional child session or factory was created.

## Pump alternatives: batching and combined submit/wait

The user asked whether the small allocation saving justified the extra machinery and requested alternatives based on the researched libraries. Acceptance was defined before implementation: preserve the complete Kestrel/full-ring I/O path and terminal ownership, exercise tiny-ring/native and real-server lifecycles, compare independent same-binary alternatives in repeated matched runs, and prefer a simpler default when no repeatable benefit emerges. This iteration did not implement multishot or change TLS.

Two opt-in alternatives were added:

- **Batched handoff:** `NETWORKPROTO_BATCH_SUBMISSIONS=1` swaps the shared typed submission queue with an empty pump-local queue once per iteration. The pump stages that finite snapshot without per-item locking. Queue storage is reused; operations arriving during staging wait for the next iteration, without intentionally sleeping to form batches.
- **Combined submission/wait:** `NETWORKPROTO_COMBINED_WAIT=1` uses `np_submit_and_collect` and liburing's `io_uring_submit_and_wait`, rather than always doing separate flush and completion collection. It requests one completion only when no backlog or existing CQE is available, outside the producer lock. Wake polling is rearmed before entering the kernel, and interrupted calls retry without releasing I/O owners.

The combined primitive was source-verified in the previously inspected Tokio driver (`5fb1a4f65b8c471ba6fab8d12e42e129231d865f`), libxev loop (`9ce8e8e6ff89e583258a7f8e7adeeeaeae8611bf`) and Reuben's Orleans engine (`9b00d64b3fdd0d7a3b51612f7800313ed1a9ef42`). Queue swapping is our bounded handoff experiment, not a claim that those projects use this exact C# queue arrangement. Their event-loop/runtime ownership differs from Kestrel's ThreadPool-plus-pipes design.

Both alternatives require the slot engine with pump-owned SQ preparation. They were tested independently and together, not assumed to be additive. No direct `accept`/`recv`/`send` syscall shortcut was enabled. A blocking completion wait on the dedicated pump is not a busy loop or a blocked Kestrel application worker.

### Three-round five-variant matrix

Same monotonic-clock **wrk 4.1.0, not wrk2**, plaintext HTTP/1.1, 1024-byte Kestrel response, server CPUs `0,2,4,6`, four .NET processors, client CPUs `8,10`, two client threads. Keep-alive uses 64 connections; close uses 16. Each of the 30 measured runs has a separately validated ten-second warmup and ten-second measurement. Order is forward, reverse, then rotated, recorded in `scripts/compare-pumps.sh`. Tracing and diagnostic counters are disabled for timed runs.

In this matrix `previous` means the typed-queue/slot engine with both new switches disabled, not the older dictionary engine. The table shows medians of three run-level metrics, not pooled request percentiles:

| Variant | Keep-alive requests/s | Keep-alive p50/p99 us | Keep-alive CPU us/request | Close requests/s | Close p50/p99 us | Close CPU us/request |
|---|---:|---:|---:|---:|---:|---:|
| Stock sockets | 168,818.66 | 200 / 1320 | 15.90 | 26,099.39 | 273 / 1340 | 87.17 |
| Previous typed queue | 128,582.64 | 456 / 980 | 19.01 | 13,656.01 | 1040 / 2010 | 177.90 |
| Batched handoff | 129,188.77 | 456 / 940 | 19.14 | 13,404.72 | 1060 / 2030 | 181.71 |
| Combined submit/wait | 126,876.50 | 463 / 970 | 19.17 | 13,631.50 | 1040 / 1980 | 178.06 |
| Both changes | 132,957.72 | 442 / 960 | 18.55 | 13,609.33 | 1040 / 1980 | 178.05 |

**Do not interpret the aggregate median for "both" as a demonstrated speedup.** Absolute rates drifted substantially: the unchanged previous typed queue fell from 151,914 to 128,583 to 124,467 keep-alive requests/s; stock fell from 180,077 to 168,819 to 166,794. Neither affinity nor the three-round design eliminates shared-host variation. Matched-round throughput changes relative to the previous typed queue were:

| Alternative | Keep-alive change, rounds 1 / 2 / 3 | Close change, rounds 1 / 2 / 3 |
|---|---|---|
| Batched handoff | -6.09% / +0.47% / -1.50% | -1.05% / -1.84% / -1.09% |
| Combined submit/wait | -1.20% / -1.33% / +0.08% | +2.84% / -0.18% / -1.22% |
| Both changes | -10.17% / +3.40% / -2.20% | +1.51% / -0.34% / -1.38% |

Batching increased CPU/request in every matched keep-alive and close round. Combined waiting did not show a consistent CPU or latency benefit. Both changes together were mixed. Allocation stayed effectively unchanged: approximately 1632 bytes/request on keep-alive and 13928-13932 bytes/request on close. Therefore none is promoted to default. These data do not establish that batching or combined waiting is generally ineffective; they establish no repeatable benefit from these implementations in this workload. Kernel syscall counts and contention costs were not independently profiled in this iteration.

### Simpler default, with confirmation

`NETWORKPROTO_DIRECT_SQ` now defaults to **0**, selecting the original pump-owned action queue and pending-operation dictionary. The reusable `IValueTaskSource` operation state remains enabled, so this does not restore per-I/O task/operation/delegate allocation. Bounded receive inlining, compact connection setup and graceful shutdown also remain. Direct accept/send/receive remain **off**. Slots, caller-thread SQ preparation, batched handoff and combined waiting are preserved as opt-in instructional variants; this simplifies the active default path, not the entire experimental source tree.

A separate final-binary confirmation used three alternating rounds of `simple` (unset `NETWORKPROTO_DIRECT_SQ`, exercising its actual default) versus `slots` (`NETWORKPROTO_DIRECT_SQ=1`). Both new alternatives were explicitly disabled. The same warmup, duration, affinity, client and modes were used. Medians:

| Mode | Engine | Requests/s | Bytes/request | Mean / p50 / p99 us | CPU us/request |
|---|---|---:|---:|---:|---:|
| Keep-alive, 64 | Simpler default | 120,519.40 | 1,632.36 | 523.57 / 484 / 1080 | 20.02 |
| Keep-alive, 64 | Slots | 119,714.00 | 1,632.36 | 527.79 / 488 / 1070 | 20.23 |
| Close, 16 | Simpler default | 13,199.09 | 14,034.92 | 1130 / 1080 / 2090 | 189.07 |
| Close, 16 | Slots | 13,166.16 | 13,928.19 | 1120 / 1080 / 2080 | 185.03 |

The slots save approximately **107 bytes per close request (0.76%)**, with unchanged keep-alive allocation. Close CPU/request is about 2.1% lower in the median and lower in all three matched rounds; that is a small real measured tradeoff, not zero benefit. Throughput is effectively equal and latency differences are small. The simpler default is chosen for fewer ownership/synchronization mechanisms, accepting that small allocation/CPU cost. The substantially larger prior reusable-operation allocation reduction is retained. Slot allocation machinery alone is not shown to be the dominant bottleneck.

The lower absolute rates in this later confirmation cannot be compared directly with the earlier matrix to claim a regression caused by selecting the simpler engine: paired runs of both engines were at the same lower level. No thermal, host-scheduling or CPU-frequency cause was established.

### Correctness, reproduction and evidence

Each of the three new configurations passed `ring-pressure.sh` with a real two-entry SQ: native overflow/retry, buffer identity, canceled receive plus separate cancel acknowledgment, and a partial ring send. The combined variant's native probe submits via the new boundary without an earlier flush. Real Kestrel checks delivered 8192 intact responses after withholding reads and 512 synchronized close connections with exact body/EOF and bounded fd count. Diagnostics recorded actual batches larger than one, managed overflow, complete terminal ownership accounting and zero direct-I/O attempts. All three configurations also passed HTTPS single/reuse/close curl.

After restoring the simpler default, the Release build, pressure probe, HTTP/HTTPS curl, server-initiated EOF and intentional TLS-client reset checks passed again. The final binary also passed the combined-plus-batched pressure check. The default's dictionary, action queue and outstanding cancel acknowledgments were empty at shutdown. All 30 matrix measurements plus 12 confirmation measurements and their warmups passed the existing strict HTTP/body/backend/clock/liveness/error gates. No logging or reset gate was relaxed. The earlier strict HTTPS wrk-close warning issue remains unresolved and was not claimed fixed; all new load measurements are plaintext. Production-scale fairness/cancellation fault injection, multishot, registered/provided buffers and fixed-offered-rate latency remain unverified.

Reproduce the matrix with the checked-in wrapper:

```bash
cd ~/code/aspnetcore
sample=src/Servers/Kestrel/samples/NetworkProtoSample
bash "$sample/scripts/build.sh"
SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4 \
    WRK="$PWD/artifacts/networkproto-wrk/wrk" \
    bash "$sample/scripts/compare-pumps.sh" "$sample/results/repeat-pumps"
```

For the simpler-default confirmation:

```bash
export SCHEME=http WRK="$PWD/artifacts/networkproto-wrk/wrk"
export SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4
export THREADS=2 WARMUP=10 DURATION=10 REPETITIONS=1 BACKENDS=io_uring
export NETWORKPROTO_SYNC_SEND=0 NETWORKPROTO_SYNC_ACCEPT=0 NETWORKPROTO_SYNC_RECEIVE=0
export NETWORKPROTO_BATCH_SUBMISSIONS=0 NETWORKPROTO_COMBINED_WAIT=0
export NETWORKPROTO_STAGE_ON_PUMP=1 NETWORKPROTO_DIAGNOSTICS=0
export NETWORKPROTO_REUSE_OPERATIONS=1 NETWORKPROTO_INLINE_RECEIVE=1
export NETWORKPROTO_COMPACT_CONNECTIONS=1 NETWORKPROTO_SHUTDOWN_CLOSE=1
for round in 1 2 3; do
    variants="simple slots"
    if [[ "$round" == 2 ]]; then variants="slots simple"; fi
    for mode in keepalive close; do
        export MODES="$mode" CONNECTIONS=64
        if [[ "$mode" == close ]]; then export CONNECTIONS=16; fi
        for variant in $variants; do
            unset NETWORKPROTO_DIRECT_SQ
            if [[ "$variant" == slots ]]; then export NETWORKPROTO_DIRECT_SQ=1; fi
            bash "$sample/scripts/wrk-load.sh" "$sample/results/repeat-simple-r$round-$mode-$variant"
        done
    done
done
```

Evidence is in `results/pump-alternatives/`: `matrix/r{1,2,3}-{keepalive,close}-{sockets,previous,batch,combined,both}`, matrix summaries, `confirmation-r{1,2,3}-{keepalive,close}-{simple,slots}` and confirmation summaries, `pressure-{batch,combined,both}`, `pressure-simple-default`, `pressure-both-final`, `curl-*`, `eof-simple`, `rst-simple`, build logs and source archives. The script records expanded client commands, native/managed binary hashes, runtime/kernel/software metadata, process metrics and pidstat alongside each run. Implementation remains in the designated WSL local branch, uncommitted and unpushed; unrelated settings and the pre-existing sample are preserved.

## Full-ring receive, eager accept and multishot

The user requested removal of synchronous receive, a fresh full-ring baseline, then multishot experimentation until an improvement or an evidence-backed limit emerged. Acceptance was defined before implementation: every measured accept/receive/send must go through io_uring, every request must traverse Kestrel HTTP, multishot must demonstrate multiple real CQEs per SQE and terminal ownership, backpressure/cancellation must be exercised, and comparisons must retain stock and appropriate one-shot controls. No agents, new sessions, commits or pushes were used.

### Direct receive removed, then baseline rerun

The prior measurements already had direct receive disabled. This iteration removed its managed branch and P/Invoke, so `IoUringEngine.ReceiveAsync` only submits receive operations. `NETWORKPROTO_SYNC_RECEIVE=1` is now explicitly rejected, not ignored. The old `np_try_recv` native helper remains solely for the historical standalone probe. Small receive continuation inlining is still enabled **after CQE delivery**; it does not bypass io_uring.

Before adding accept changes, the Release build, real ring-pressure probe and HTTPS curl passed, then 12 measured runs compared stock against the unchanged full-ring path. Three reordered rounds used two wrk threads, 64 keep-alive or 16 close connections, ten-second warmup and ten-second measurement, server CPUs `0,2,4,6`, client CPUs `8,10`, four .NET processors, plaintext HTTP/1.1 and a 1024-byte body:

| Workload | Stock median requests/s | Full-ring median requests/s |
|---|---:|---:|
| Keep-alive, 64 | 187,240.91 | 157,689.02 |
| Close, 16 | 30,438.74 | 17,457.37 |

This confirms the baseline; it is not a gain caused by removing an already-disabled branch. All performance measurements in this section use the monotonic-clock **wrk 4.1.0, not wrk2**. They measure real Kestrel HTTP over TCP, not a raw TCP echo loop or TLS performance.

### Accept implementation and controls

`IoUringEngine.AcceptStream.cs` implements a bounded accepted-handle channel shared by two new modes. `queued` rearms one-shot accept from the pump without waiting for Kestrel to construct a connection and call accept again. `multishot` uses the same queue/ownership but submits `io_uring_prep_multishot_accept`. The old `oneshot` demand-driven path remains as a control.

The native completion batch now includes flags. A multishot result with `CQE_F_MORE` retains its pending dictionary entry and listening socket reference. A CQE without `MORE` is terminal, releasing that reference; cancel acknowledgment is separate and never substitutes for original completion. Accepted fds become owned `SafeSocketHandle`s, transferred to the listener or closed if shutdown has begun. Rearm uses a fresh operation ID.

The channel has hard capacity 1024, default pause threshold 256 and resume threshold 128. The pump pauses one-shot rearm or cancels multishot at the high threshold, drains terminal completion, then resumes after consumption. Threshold can be reduced to force this path. Cancellation has already-produced CQEs to account for; remaining capacity is headroom, not a guarantee against arbitrary overload. Exhausting it explicitly logs/fails the listener and closes unclaimed descriptors rather than silently treating dropped traffic as success. No queue overflow occurred in accepted runs.

The native probe produced **eight real accepted sockets from one SQE**, all with `MORE`, verified socket data and close-on-exec flags, canceled the request, observed original `-ECANCELED` plus acknowledgment, then repeated with a new generation. The Kestrel pressure probe used a pause threshold of one, forcing hundreds of pause/resume cycles with terminal accounting and zero outstanding queue/native ownership at shutdown.

The first pressure wrapper failed only because its new assertion expected 515 accepted sockets. Actual traffic creates 514 sockets (readiness, slow-reader, 512 burst connections); the 515th one-shot submission is the final canceled pending accept, not an accepted socket. The assertion was corrected, not a transport error hidden. Both initial outputs and the passing rerun are retained.

### First repeated accept comparison: a large close-mode improvement

This matrix uses the same settings as the baseline, with all four variants in each of three reordered rounds. Deferred task work is disabled here. Medians of individual run metrics:

| Workload | Variant | Requests/s | Mean us | p50 us | p99 us | CPU us/request |
|---|---|---:|---:|---:|---:|---:|
| Keep-alive, 64 | Stock sockets | 178,762.75 | 259.94 | 182 | 1210 | 14.94 |
| Keep-alive, 64 | Original one-shot | 145,462.26 | 434.91 | 401 | 890 | 17.04 |
| Keep-alive, 64 | Eager one-shot | 139,427.51 | 450.12 | 417 | 900 | 17.66 |
| Keep-alive, 64 | Multishot | 159,817.91 | 397.00 | 374 | 761 | 15.85 |
| Close, 16 | Stock sockets | 27,723.42 | 316.32 | 248 | 1210 | 80.98 |
| Close, 16 | Original one-shot | 15,963.75 | 920.00 | 870 | 1850 | 159.87 |
| Close, 16 | Eager one-shot | 30,785.35 | 338.33 | 243 | 1250 | 90.31 |
| Close, 16 | Multishot | 31,049.64 | 301.05 | 246 | 1150 | 90.10 |

**The new full-ring accept pipeline substantially improves close-mode throughput and latency.** Eager one-shot gains approximately 93% median throughput over the old demand-driven path; multishot gains about 95%. Their close p50 falls from 870 us to roughly 245 us, while CPU/request falls from about 160 to 90 us.

However, most of the gain is not uniquely multishot: both new modes remove the wait for Kestrel/application-side accept rearming. Compared directly with eager one-shot, multishot close throughput changes were +10.3%, +0.9% and -3.5% across the three rounds, not a consistent extra win. It beat stock in two close rounds and trailed it in one. No universal superiority claim is justified.

Do not read the keep-alive medians as an isolated multishot gain. Rates drifted during the matrix: original one-shot keep-alive fell from 156,980 to 145,462 to 135,822 requests/s; stock fell from 188,618 to 178,763 to 171,558. Accept is rarely exercised after persistent connections are established. The roughly 95-98% pump-core usage in keep-alive, with whole-server usage around 2.4-2.6 cores of four, points to the single pump/kernel-I/O path as a current limit, not a universal io_uring limit. No CPU-only profile isolates the exact costs.

Close-mode allocation was approximately 14,067 bytes/request original, 14,087 eager and 14,076 multishot. This improvement is not an allocation reduction. Keep-alive remained about 1632 bytes/request for the custom variants.

Separate diagnostics establish actual mechanism rather than relying on flags alone:

| Diagnostic mode | Successful accepts | Accept SQEs | MORE CQEs | Terminal accept CQEs |
|---|---:|---:|---:|---:|
| Eager one-shot | 216,871 | 216,872 | 0 | 216,872 |
| Multishot | 207,358 | 1 | 207,358 | 1 |

The extra one-shot SQE is the final canceled accept. Both diagnostics ended with zero pending operations, queued accepted handles, queue overflows and direct-I/O attempts. These short diagnostic rates are excluded from headline comparisons.

### Higher close concurrency: multishot does not automatically win

A further three-round matrix used close/64, four wrk threads on CPUs `8,10,12,14`, and the same four server CPUs. Each run retained ten-second warmup and measurement:

| Variant | Median requests/s | Range requests/s | p50 / p99 us | CPU us/request |
|---|---:|---:|---:|---:|
| Stock sockets | 43,015.59 | 41,584-51,103 | 1150 / 3670 | 67.02 |
| Original one-shot | 13,729.20 | 13,575-14,979 | 4540 / 6280 | 184.14 |
| Eager one-shot | 43,325.02 | 42,961-46,407 | 890 / 3070 | 69.27 |
| Multishot | 37,722.71 | 37,454-40,146 | 848 / 3880 | 73.40 |

Both new paths improve markedly over the old one, but multishot is approximately 12-14% slower than eager one-shot in each matched round here. Its lower accept-SQE count does not imply lower total workload cost. The multishot client used roughly 3.5-3.6 of four cores and the server around 2.7-2.8 cores; eager one-shot server usage was about three cores. Completion delivery/connection timing and client-side TCP costs may contribute, but were not separately isolated. This is why simply selecting multishot everywhere is not the retained recommendation.

### Deferred task work: another repeatable improvement

One bounded follow-up tested kernel task-work placement. The ring is created disabled with `SINGLE_ISSUER | DEFER_TASKRUN | TASKRUN_FLAG`, then enabled on its managed pump thread. That thread becomes the kernel issuer and drives deferred work through completion collection/waiting. This follows the documented [io_uring_setup requirements](https://man7.org/linux/man-pages/man2/io_uring_setup.2.html); it is not SQPOLL or busy spinning. The default-off screening version, source snapshot and failed/accepted evidence are retained. There is no silent fallback on unsupported kernels.

After correctness checks and a one-pass screen, a 30-run matrix independently compared eager/multishot with deferred work off/on plus stock. Keep-alive/64 used two client threads/cores; close/64 used four. Other settings remained unchanged. Median results:

| Workload | Variant | Requests/s | p50 / p99 us | Bytes/request | CPU us/request |
|---|---|---:|---:|---:|---:|
| Keep-alive, 64 | Stock | 169,356.42 | 192 / 1190 | 1640.15 | 15.71 |
| Keep-alive, 64 | Eager, normal task work | 135,788.96 | 420 / 920 | 1632.31 | 18.11 |
| Keep-alive, 64 | Eager, deferred | 142,299.08 | 403 / 900 | 1632.29 | 17.05 |
| Keep-alive, 64 | Multishot, normal | 134,427.33 | 425 / 950 | 1632.31 | 17.99 |
| Keep-alive, 64 | Multishot, deferred | 144,426.27 | 396 / 880 | 1632.28 | 17.23 |
| Close, 64 | Stock | 42,036.00 | 1190 / 3740 | 13520.09 | 68.86 |
| Close, 64 | Eager, normal | 44,528.12 | 860 / 3080 | 14246.66 | 67.24 |
| Close, 64 | Eager, deferred | 47,978.79 | 747 / 2970 | 14319.77 | 61.75 |
| Close, 64 | Multishot, normal | 39,973.70 | 797 / 3490 | 14102.35 | 69.69 |
| Close, 64 | Multishot, deferred | 42,466.04 | 753 / 3300 | 14231.79 | 63.99 |

Deferred work improved throughput and CPU/request in **every matched round** for both accept modes. Eager throughput improvements were 3.5%, 4.8%, 4.2% for keep-alive and 9.9%, 6.2%, 8.4% for close. Multishot improvements were 7.8%, 5.3%, 3.8% and 7.2%, 3.4%, 8.9% respectively. Eager close CPU/request fell 8-10%; multishot close CPU/request fell 7-9%. p50 improved in every matched case; eager p99 remained mixed, so no across-the-board tail-latency claim is made.

Deferred close allocation increased modestly (about 73 bytes/request eager and 129 bytes/request multishot in the medians), while keep-alive allocation was unchanged. Measured scheduling/connection-lifetime changes can affect process allocation; this experiment does not attribute the difference to a specific object type.

Stock close throughput ranged from 41,079 to 51,777 requests/s. Eager/deferred ranged 47,727-48,943; multishot/deferred 41,641-42,861. Eager/deferred outperformed stock in two rounds but not the first. It is a repeatable improvement over the corresponding custom control, not proof of stable superiority over stock on arbitrary hosts/workloads. Keep-alive remains slower than stock in all final matched rounds. Absolute rates from different matrices must not be compared as if host conditions were constant.

### Retained defaults, checks and boundaries

The retained configuration is `NETWORKPROTO_ACCEPT_MODE=queued NETWORKPROTO_DEFER_TASKRUN=1 NETWORKPROTO_DIRECT_SQ=0`: eager one-shot accept with a bounded queue, deferred work on one pump, and the simpler pending dictionary/action queue for ordinary I/O. This was chosen because it gives essentially the same keep-alive performance as multishot/deferred and consistently better high-concurrency close performance. **Multishot is implemented, retained and selectable with `NETWORKPROTO_ACCEPT_MODE=multishot`; it is not silently mapped to one-shot.** The original `oneshot` and normal task work (`NETWORKPROTO_DEFER_TASKRUN=0`) remain controls.

All direct accept/receive/send attempts were zero in the diagnostic/pressure runs. Direct send/accept are still disabled historical options; direct receive is removed. An explicit `NETWORKPROTO_SYNC_RECEIVE=1` startup check failed with the documented configuration error (exit 134 with core dumps disabled), proving that setting cannot silently select the old path.

The final build passed, followed by default tiny-ring read/write pressure, eager/multishot pause/resume pressure, HTTP/HTTPS curl, multishot/deferred HTTPS curl, server-initiated EOF and intentional TLS-client reset checks. A final unqualified-default load check, not another headline repeated matrix, passed on both backends: keep-alive default 144,525 versus stock 171,571 requests/s; close/16 default 32,606 versus stock 28,093. Existing HTTP/body/backend/clock/liveness/error gates were preserved throughout. End-of-client-run reset warnings retain the prior strict screening policy; no zero-exception claim is made.

This is **accept multishot**, not receive multishot. Receive/send stay one-shot with pinned pipe buffers until terminal CQEs. Receive multishot would require provided-buffer ownership and revised backpressure/copying, and ordinary sends still need explicit application-supplied buffers; neither was added just to claim more features. Sharding the roughly saturated keep-alive pump, multishot receive, production overload policy, exhaustive cancellation races and equal-offered-rate latency are not verified by these results. Unconsumed-accept shutdown cleanup is implemented, but the current workload checks do not force every possible queued-fd/shutdown interleaving. The prior strict HTTPS wrk-close warning issue remains unresolved; only HTTPS curl functionality and plaintext load performance are claimed.

### Reproduction and evidence

Build from the designated WSL checkout, then run the two checked-in matrices:

```bash
cd ~/code/aspnetcore
sample=src/Servers/Kestrel/samples/NetworkProtoSample
bash "$sample/scripts/build.sh"
SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4 \
    WRK="$PWD/artifacts/networkproto-wrk/wrk" \
    bash "$sample/scripts/compare-accept.sh" "$sample/results/repeat-accept"
SERVER_CPUS=0,2,4,6 KEEPALIVE_CLIENT_CPUS=8,10 CLOSE_CLIENT_CPUS=8,10,12,14 \
    SERVER_PROCESSORS=4 WRK="$PWD/artifacts/networkproto-wrk/wrk" \
    bash "$sample/scripts/compare-taskrun.sh" "$sample/results/repeat-taskrun"
```

`compare-accept.sh` disables deferred task work unless explicitly overridden and fixes all full-ring/reuse/continuation settings. `compare-taskrun.sh` sets normal/deferred per variant. Both reorder variants over three rounds, preserve per-run metadata/commands/hashes and refuse to overwrite run directories. For the larger normal-task-work close comparison, run `wrk-load.sh` with `THREADS=4 CONNECTIONS=64 MODES=close CLIENT_CPUS=8,10,12,14 NETWORKPROTO_DEFER_TASKRUN=0`, varying `BACKENDS` and `NETWORKPROTO_ACCEPT_MODE` through stock/oneshot/queued/multishot in the documented forward/reverse/rotated order. To reproduce the fresh original baseline, use `NETWORKPROTO_ACCEPT_MODE=oneshot NETWORKPROTO_DEFER_TASKRUN=0` and the 64-keep-alive/16-close settings.

All evidence persists in `results/multishot/`: `baseline-r*` plus `baseline-summary.json`; `matrix/` and its summaries; `capacity-r*`/`capacity-summary.json`; `diagnostic-{oneshot,queued,multishot}`; `deferred-screen-*`; `taskrun-matrix/`; `defaults-{keepalive,close}`; native/pressure/curl/EOF/RST outputs; configuration-rejection output; build logs; and `before-source.tar.gz`, `ring-only-source.tar.gz`, `accept-measured-source.tar.gz`, `taskrun-measured-source.tar.gz`, `final-source.tar.gz` and `final-binary-sha256.txt`. Final revalidation reran the existing gates over all 93 measured outputs, 93 warmups and 93 server logs, including the three diagnostic runs; all passed, as recorded in `final-evidence-verification.log`. Machine/software/runtime/source metadata remain with every measured run. The unrelated settings change and pre-existing sample were preserved; all implementation is uncommitted and unpushed.
