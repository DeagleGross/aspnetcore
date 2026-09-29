# Kestrel transport RPS comparison

For the day-by-day story, including wake coalescing, unsuccessful experiments, and final-send/shutdown batching, start with the [development history](history/README.md). This report retains the detailed measurement series.

## C# io_uring engines (2026-09-29)

The newer TCP, fd-TLS and custom-BIO transports now dispatch CQEs and own connection/TLS/page state in C#. Their combined C files decreased from 1,178 to 284 lines; C retains liburing/OpenSSL wrappers and BIO callbacks. The original single-ring experiment and epoll are unchanged. [Architecture, lifetime rules, verification limits, and raw evidence](../IoUringTcp/README.md).

Fresh controls and preserved pre-refactor binaries, same four server/twelve client cores, 1,200 connections, 1 KiB responses and two reversed-order 15-second runs per cell. Coalescing, final-send batching and the fd read guard match between old/new runs.

| Rewritten transport | TCP short RPS | TCP long RPS | TLS short RPS | TLS long RPS |
|---|---:|---:|---:|---:|
| Stock Sockets / SslStream | 59,443 | 190,437 | 4,519 | 119,829 |
| IoUringTcp / SslStream | 76,120 (+28.1%) | 209,512 (+10.0%) | 4,466 (-1.2%) | 130,683 (+9.1%) |
| Fd IoUringTls | N/A | N/A | 7,781 (+72.2%) | 141,072 (+17.7%) |
| Layered IoUringBio | 76,496 (+28.7%) | 201,080 (+5.6%) | 8,119 (+79.6%) | 153,437 (+28.0%) |

Against their own preserved C engines, persistent means were 1-4% lower and fd TLS short about 4.9% lower; raw TCP short was +1.6% and BIO TCP short +16%. The rewrite preserves useful throughput but does not establish zero regression. All 48 principal runs had complete worker/page accounting; raw TCP native shutdown still recorded classified `ENOTCONN` results and stock TCP short had timeout counts. These remain overloaded achieved rates, not sustainable capacity.

Required-kTLS managed runs separately averaged 7,285 short / 134,602 long with complete RX+TX activation. The old short-kTLS baseline aborted during cleanup twice, leaving only one valid old short run; no precise paired short-kTLS delta is claimed. Full results, failed attempts, and the unresolved diagnostic 4 KiB send-buffer kTLS boundary are retained in the linked report.

## Native epoll post-TLS comparison (2026-09-28 evening)

The separate [`Epoll` prototype](../Epoll/README.md) provides `epollTls`: configurable dedicated workers, batched level-triggered epoll readiness, and native fd-bound OpenSSL, without runtime `TlsContext`/`TlsSocketSession`. The table below records the original C engine with a reused managed adapter. The later refactor moved event/TLS state into epoll-local C# code and reduced C to a 176-line shim; it no longer depends on the io_uring engine. It has no custom-BIO or plaintext mode.

Fresh final-binary controls, four server workers/cores, twelve client cores, 1,200 connections, 1,024-byte response, two reversed-order 15-second runs per cell. Native rows enable wake coalescing and final-send batching; fd io_uring also enables the read guard.

| Transport | TLS short mean RPS | TLS long mean RPS |
|---|---:|---:|
| Stock Sockets / SslStream | 3,160 | 95,058 |
| Fd-bound IoUringTls | 5,871 (+85.8%) | 117,357 (+23.5%) |
| Layered IoUringBio | 5,957 (+88.5%) | 130,442 (+37.2%) |
| epollTls | 5,744 (+81.8%) | 127,039 (+33.6%) |

Epoll was -2.2% short / +8.3% long against fd io_uring in this batch, not proof of a general backend ranking. These are overloaded achieved rates; one stock short run had 65 timeouts. Native runs had complete worker/page accounting and no wrk error summary, while native peer-abort/teardown counters remained recorded. Do not compare the lower evening stock rates directly with afternoon results as an implementation regression. Final evidence is `NetworkProtoSample/results/epoll-final-matrix-20260928-225808/validated-summary.json`.

The subsequent same-session native-engine versus C#-engine screen averaged 6,848 versus 6,735 TLS-short RPS and 132,853 versus 130,509 TLS-long RPS (about -1.7% / -1.8%). Fresh stock was 3,922 / 102,685. These are separate batches; the old binary was preserved, not reconstructed from remembered results. The [epoll report](../Epoll/README.md#moving-the-state-machine-to-c-same-session-comparison) describes the boundary, controls and limitations. Evidence: `epoll-managed-matrix-20260928-234344/validated-summary.json`.

## Final-send batching extended to fd TLS and layered BIO (2026-09-28, 17:20 onward)

`NETWORKPROTO_FINAL_SEND=3` now applies to `IoUringTls` and both modes of `IoUringBio`, as well as the existing `IoUringTcp` path. Eligibility is still produced by real output-pipe completion, not by parsing HTTP. The default remains off.

The implementation differs by backend:

| Backend | What mode 3 does after output completion is known |
|---|---|
| Raw IoUringTcp | Existing linked `SEND(MSG_WAITALL | MSG_MORE)` -> `SHUTDOWN(SHUT_WR)` |
| Fd-bound IoUringTls | Set `TCP_CORK` before the final `SSL_write_ex`; after successful output, send `close_notify` with `SSL_shutdown`, then shut down the socket natively |
| IoUringBio, HTTPS | Set `TCP_CORK`; send the final application ciphertext through io_uring, generate and drain `close_notify` through the same BIO/send machinery, then shut down the socket |
| IoUringBio, HTTP | Set `TCP_CORK`; shut down natively after the final send has fully completed |

For fd TLS and layered BIO, modes 1 and 2 both initiate native close after the final send, without corking. Mode 2 does not invent an SQE for `SSL_write_ex` or add a linked shutdown to BIO ciphertext. Mode 3 avoids an extra managed close-command round trip and lets TCP defer the final tail until shutdown; it does not guarantee that the response, TLS alert, and FIN fit in one packet. Larger output still streams, partial sends and TLS retries retain their buffers, and connection resources are freed only after outstanding operations and leases drain.

### Fresh matched results

Original single-source-address wrk2 client, four server cores, twelve separate client cores, 1,200 connections, 1,024-byte responses, 15-second measurements. Native configurations have coalesced wakes enabled; fd TLS also has the page-return read guard enabled. TLS settings and libraries are held fixed. Each cell is the mean of two runs in reversed configuration order, except BIO TLS keep-alive, which has four runs per setting after an additional regression screen. The stock controls were rerun, not carried forward.

Percentages in parentheses are against the fresh stock mean in the same column. These measurements use the quick runner without the separate TCP investigation runner's explicit warmup. Do not directly compare their absolute RPS to the earlier 84k-85k IoUringTcp investigation runs.

| Transport | TCP short RPS | TCP long RPS | TLS short RPS | TLS long RPS |
|---|---:|---:|---:|---:|
| Stock Sockets / SslStream | 51,082 | 180,554 | 4,330 | 114,123 |
| Fd IoUringTls, final batching off | N/A | N/A | 7,402 (+70.9%) | 146,528 (+28.4%) |
| Fd IoUringTls, final batching on | N/A | N/A | 7,526 (+73.8%) | 146,725 (+28.6%) |
| Layered IoUringBio, final batching off | 36,760 (-28.0%) | 206,938 (+14.6%) | 7,758 (+79.1%) | 146,703 (+28.5%) |
| Layered IoUringBio, final batching on | 57,079 (+11.7%) | 213,982 (+18.5%) | 7,723 (+78.3%) | 143,618 (+25.8%) |

The **on/off change**, rather than the total difference from stock, is:

| Backend | TCP short | TCP long | TLS short | TLS long |
|---|---:|---:|---:|---:|
| Fd IoUringTls | N/A | N/A | +1.7% | +0.1% |
| Layered IoUringBio | **+55.3%** | +3.4% | -0.5% | -2.1% |

The large improvement is again in plaintext connection churn, now for the BIO engine's TCP mode: off runs were 35,078 / 38,443 RPS, on runs 56,461 / 57,696. The TLS short runs do not establish a material improvement: fd off/on were 7,442 / 7,363 versus 7,506 / 7,547; BIO off/on were 7,770 / 7,746 versus 7,869 / 7,577. TLS still performs a fresh handshake for each short connection; this change removes neither handshake work nor that exchange. There was no new TLS CPU profile establishing the remaining limiting component.

Keep-alive reported **zero final-send commands and zero cork operations** in every run. Consequently the observed changes there are not evidence of a final-response/FIN batching benefit or delay. BIO TLS keep-alive was 2.1% lower across four off/on runs; that observation remains reported, not dismissed as proven noise or declared a verified regression in the close path. Its initial two-run difference was -3.0%, and the second two-run screen was -1.2%. Stock TLS keep-alive itself ranged from 107,867 to 120,379 RPS. These small-sample measurements on a shared machine do not establish stable percentage gains for persistent connections.

### Reachability, lifetime, and protocol evidence

The optimized TLS short runs reached 111,041 / 112,031 fd final sends and 116,813 / 112,664 BIO final sends, roughly 98% of application receives. Each run had matching final-send, `close_notify`, cork, and native shutdown counts, with no final-shutdown errors. The BIO TCP runs reached 847,374 / 865,877 final sends and successful native shutdowns. Both plaintext and ciphertext page accounting balanced, and all four workers drained in every native benchmark run.

The real output-pipe check now supports fd TLS, BIO TLS, and BIO TCP. Modes 0-3 passed for raw TCP, fd TLS, BIO TCP, BIO TLS, and fd TLS with required RX+TX kTLS. Each mode verified 1 KiB and 4 MiB payloads, byte-for-byte delivery before EOF, and cleanup after a peer reset. The reset case was strengthened to a 16 MiB send with a 4 KiB client receive buffer: 4 MiB with default socket buffers could finish queueing before the intended reset, so it was not a dependable blocked-send case.

Mode 3 also passed isolated native AddressSanitizer/UndefinedBehaviorSanitizer checks for fd TLS, BIO TLS, and required kTLS, including the final-output cases and real Kestrel fragmented-input, 132-request reuse, 40 KiB body, and 3,001-response slow-reader/pipeline checks. An OpenSSL client observed the actual `close_notify` on the real HTTP close path in each configuration. These checks preserve graceful TLS close rather than substituting reset for FIN. Leak detection was disabled in the mixed managed/native sanitizer process; untested cancellation races and TLS versions outside the prototype's TLS 1.2 configuration remain out of scope. kTLS was exercised for correctness here, not benchmarked again.

No wrk errors were reported by the fd/BIO runs in this batch. Stock TCP short reported 15 / 104 timeout counts and one read error in the second run. BIO separately recorded peer aborts at load cutoff but no `tlsErrors`. Offered rates remain deliberately excessive: 200k TCP short, 50k TLS short, 1M keep-alive. These are achieved RPS under overload, not sustainable latency-SLO capacity.

```bash
cd ~/code/aspnetcore
scripts=src/Servers/Kestrel/samples/NetworkProtoSample/scripts
$scripts/check-final-send.sh IoUringTls
SCHEME=https $scripts/check-final-send.sh IoUringBio
SCHEME=http $scripts/check-final-send.sh IoUringBio
NETWORKPROTO_KTLS=1 $scripts/check-final-send.sh IoUringTls
NETWORKPROTO_FINAL_SEND=3 NETWORKPROTO_COALESCE=1 NETWORKPROTO_GUARD_PAGE_READ=1 \
    SCHEME=https $scripts/quick-rps.sh IoUringTls
NETWORKPROTO_FINAL_SEND=3 NETWORKPROTO_COALESCE=1 \
    $scripts/quick-rps.sh IoUringBio
```

Evidence: sample `results/tls-bio-final-send-20260928.json` contains all 36 benchmark runs, raw directory references, metrics, and means. Per-run labels are `tls-final-close-*`, `tls-final-long-*`, and `bio-final-tcp-*`. Deterministic checks are in `final-send-check-20260928-173036` through `final-send-check-20260928-173052`; native sanitizer, real HTTP, and TLS-alert evidence is in `tls-final-integration-20260928-173152`.

## Final send plus write shutdown experiment (2026-09-28, 16:55 onward)

The TCP output adapter can tell the native pump that a send contains the last output bytes when `PipeReader.ReadAsync()` returns `IsCompleted`, the read is not cancelled, and the segment is the last nonempty segment in that read. It does not inspect HTTP headers or assume a response means the connection is finished. If output completion is not yet known, the existing close path remains in use.

The experiment is opt-in via `NETWORKPROTO_FINAL_SEND`:

| Value | Behavior for an eligible final TCP send |
|---|---|
| Unset / `0` | Existing managed send-completion -> close-command path |
| `1` | Native send CQE handler initiates shutdown after all final bytes are sent |
| `2` | `SEND(MSG_WAITALL)` with `IOSQE_IO_LINK` -> `SHUTDOWN(SHUT_WR)` |
| `3` | Same linked pair, adding `MSG_MORE` to the final send so the following shutdown can flush the tail with the FIN |

This is a **send + write-shutdown** chain, not a premature fd `CLOSE`. Descriptor/buffer ownership still waits for terminal send, shutdown, receive and cancellation completions. A partial linked send cancels the shutdown link and the remaining bytes are retried after the linked operations have drained. `MSG_WAITALL` is required for the linked-send short-result behavior, as described in the [liburing send documentation](https://man7.org/linux/man-pages/man3/io_uring_prep_send.3.html).

The managed/native ABI uses the command's previously unused integer as a final-send hint. Both SQEs are prepared together; the code ensures space for both before adding the link head. If the managed send continuation requests close before the linked shutdown CQE arrives, it waits for the pending shutdown instead of racing a second one against it.

This initial stage gated mode 3 to the raw `IoUringTcp` engine, including SslStream above that raw transport. The later fd-TLS and BIO extension is described above. It is not enabled by default.

### Results with the original single-source-address client

Same 4 server / 12 client cores, 1,200 connections, 1,024-byte response, 200k offered RPS, 3-second warmup and 15-second measurement. The diagnostic multiple-source-address client was **not** used.

| Configuration | Short-connection achieved RPS | Evidence |
|---|---:|---|
| Native shutdown on final-send CQE, mode 1 | 49,985 | Initial single-run screen |
| Kernel-linked send/shutdown, mode 2 | 39,381 | Initial single-run screen |
| Stock Sockets | 70,465 / 65,033 | Two-order confirmation controls |
| Default IoUringTcp, final hint disabled | 40,865 / 46,204 | Two-order confirmation controls |
| Linked send + `MSG_MORE`, initial mode 3 | 81,198 / 82,877 | Two-order confirmation; later tightened pending-shutdown ownership |
| Linked send + `MSG_MORE`, final mode 3 | **84,008 / 85,485** | Final drain-aware implementation |
| Default IoUringTcp, final control afterward | 39,304 | Drift control |
| Stock Sockets, final control afterward | 58,735 | Drift control |

The final mode-3 runs had only **31 / 62 client-side TIME_WAIT entries**, versus approximately 13.4k-13.6k for the default path. Client system CPU dropped from approximately 158-160 CPU seconds to 67-69 CPU seconds per 15-second load. No TIME_WAIT-overflow events occurred in these confirmation runs.

The distinction matters: removing the managed close round trip alone was insufficient. Even a linked send can deliver the response before the peer sees FIN. Mode 3 also changes when TCP pushes the known-final bytes, greatly reducing client-first closure in this experiment. This is evidence that response/FIN sequencing materially affected the original short-connection regression, not proof that every network or workload benefits.

No wrk socket/HTTP errors were reported in the mode-3 confirmation runs. Stock reported overload timeouts. Native shutdown CQEs in the final two runs included 140 / 64 `ENOTCONN` results among approximately 1.4 million linked operations per run: those connections were no longer connected when shutdown executed. They remained logged and propagated, not treated as successful shutdowns. New logs classify this exact errno under `shutdownNotConnected`; other unexpected shutdown errors remain separate. Therefore these are not entirely error-free native teardown runs. All worker/page summaries completed and all receive pages returned.

The environment remained shared and overloaded; corrected latency grew to seconds. Do not claim the difference versus stock as sustainable capacity or combine these numbers with a baseline from a different session.

### Lifetime and behavior checks

`FinalSendCheck` exercises the real output pipe as the producer of the final hint. It completes a 1 KiB or 4 MiB output buffer, delays client reads, verifies every byte and EOF ordering, then resets a client during another backpressured 4 MiB send. Modes 0-3 passed. The linked-mode counters establish that final hints, cancelled links, and an actual partial-send path were reached. Native final-send code also passed an AddressSanitizer/UndefinedBehaviorSanitizer run; leak detection was disabled in the mixed .NET/native process.

Real Kestrel HTTP and SslStream/HTTPS keep-alive checks passed with mode 3: fragmented input, a page-spanning header, 132 requests on the same connection, pipelining and explicit close. A single-run keep-alive screen was 217,751 RPS with the option off versus 219,731 with it on; this is a no-obvious-regression screen, not a keep-alive improvement claim. Many low-concurrency responses finish sending before output completion is known and legitimately use the original close path.

```bash
cd ~/code/aspnetcore
source activate.sh
dotnet build src/Servers/Kestrel/samples/NetworkProtoSample/NetworkProtoSample.csproj -c Release --no-restore
src/Servers/Kestrel/samples/NetworkProtoSample/scripts/check-final-send.sh
NETWORKPROTO_FINAL_SEND=3 \
    src/Servers/Kestrel/samples/NetworkProtoSample/scripts/tcp-short-investigation.sh IoUringTcp 200000 final-send
```

The new path does not hard-code HTTP close semantics, does not use reset/zero-linger as an optimization, does not change other transports, and leaves the old behavior selectable. It remains experimental: non-Linux portability, every possible cancellation race, and broader latency/backpressure workloads are not established.

Evidence: `NetworkProtoSample/results/tcp-short-finalsend-*/`, `results/final-send-check-20260928-170045/`, `results/final-send-check-20260928-170508/`, `results/final-send-check-20260928-170722/`, and the aggregate `results/final-send-confirm-20260928.json` for the earlier two-order screen. The final drain-aware runs are `tcp-short-finalsend-drained-r1-20260928-170725` and `tcp-short-finalsend-drained-r2-20260928-170745`.

## TCP short-connection regression investigation (2026-09-28 afternoon)

The committed transport implementation at `9e06793bd5` was kept unchanged. The investigation compared stock Sockets with new `IoUringTcp`, using coalesced wakes for the latter. HTTPS/SslStream is not involved in this plaintext case. Native fast-path alternatives and a diagnostic wrk2 build were confined to ignored `artifacts/tmp/tcp-short-probes/`.

**Main finding: client-side source-port/TIME_WAIT pressure is a major contributor to the observed short-connection RPS gap. It is not established that the server has an intrinsic 40k-RPS ceiling.** Server close scheduling differs materially and likely contributes to which endpoint actively closes first. This is still a real end-to-end behavior difference; it must not simply be dismissed as an irrelevant benchmark artifact.

### Fresh reproduction and CPU evidence

Four server cores, twelve distinct client cores, 1,200 connections, 3-second warmup, 1-second settling interval, 15-second measurement, 1,024-byte real Kestrel response. Offered rate 200k unless stated.

| Unmodified client, one source address | Stock Sockets | IoUringTcp |
|---|---:|---:|
| First pair, achieved RPS | 71,647 | 41,287 |
| Client system CPU seconds over the 15-second load | 61.23 | 157.95 |
| Server CPU microseconds/completed request | 47.0 | 66.2 |
| Later confirmation pair, achieved RPS | 52,489 | 38,188 |
| Client-side TIME_WAIT after that load | 936 | 13,748 |
| Server-side TIME_WAIT after that load | 12,659 | 3,582 |

At a matched 30k offered rate, both achieved about 29.2k and consumed approximately 80 server CPU microseconds/request. IoUringTcp did not allocate more managed bytes at that equal-load point (about 11.8k versus stock 13.5k bytes/request). Allocation totals vary with the load regime. Thus allocation or HTTP parsing alone does not explain the high-load gap.

The high-load prototype used only about 2.7 server cores, while wrk2 used about 10.6 of its twelve client cores, predominantly system CPU. The four ring pumps each consumed roughly 30% of a core, not a single saturated pump. `pidstat` showed scheduling wait as well. Process CPU does not include all kernel/background work.

Targeted `perf` sampling of only the server/client processes found large client-side kernel hotspots in the io_uring case: `__inet_hash_connect`, `__inet_check_established`, connection-table spinlocks and `tcp_twsk_unique`, with stacks rooted in wrk2's `connect()`. The first two alone accounted for roughly 23% of combined sampled CPU in that profiled run. These are real on-CPU samples, not EventPipe thread-wall-time samples; profiled RPS is not used as a benchmark result.

The machine's unmodified ephemeral port range was 32768-60999, `tcp_tw_reuse=2`, and `tcp_tw_reuse_delay=1000`. Although loopback reuse is enabled, finding/reusing a suitable tuple still has costs and timing constraints. Merely counting client connect errors misses expensive successful connects.

### Causal probe: spread client source addresses, leave servers unchanged

An isolated wrk2 variant binds connections across eight loopback source addresses, using `IP_BIND_ADDRESS_NO_PORT` so port allocation still occurs at connect time. The same modified client is used against both servers. No sysctl, interface configuration, or transport code changes were made. This is a different client topology, not a transparent replacement for the original benchmark.

Each clean run started after a 65-second pause to avoid carrying prior TIME_WAIT pressure into the comparison. Neither TCP TIME_WAIT overflow nor TW-kill counters increased. Order was reversed in the second pair.

| Eight-source diagnostic | Stock Sockets | Unchanged IoUringTcp |
|---|---:|---:|
| Pair 1 RPS | 61,050 | 66,714 |
| Pair 2 RPS | 60,453 | 65,053 |
| Mean RPS | 60,752 | 65,884 |
| Server CPU us/request, range | 55.6-55.6 | 52.9-53.1 |
| Client system CPU seconds, range | 61.4-62.0 | 54.3-55.7 |
| Client-side TIME_WAIT, range | 2,427-2,872 | 111,720-111,721 |
| Server-side TIME_WAIT, range | 110,901-111,006 | 24-31 |

Stock reported 29/72 timeout counts in these overloaded runs; the io_uring client reported none. The result does not establish a universal 8.4% server improvement, but it does show that the original large loss disappears when client source-port pressure is relieved. The kernel profile, endpoint state distribution, and intervention agree on the mechanism.

An earlier 100-address exploratory probe approached the global 131,072 TIME_WAIT limit. Its favorable numbers are not used as confirmation; the eight-address repeats above explicitly checked overflow counters instead.

### Why close scheduling is a plausible transport-side contributor

The prototype's normal output path crosses from managed output reading to a native send command, then back through send completion, then queues a close command. After native terminal completion, managed disposal queues another command to free the connection. Stock's socket send loop can continue directly from a completed send into shutdown without that same dedicated-pump command cycle.

wrk2 reconnects as soon as it parses a response carrying `Connection: close`; it does not wait for the server FIN first. If the server FIN arrives later, the client can become the active closer and hold TIME_WAIT/source-port state. The observed endpoint distribution is consistent with this difference. We did not packet-trace exact FIN timings or isolate which individual managed handoff dominates, so that attribution remains narrower than a proven per-line latency breakdown.

### Small isolated implementation probes

| Probe, ordinary single-source client | Achieved RPS | Finding |
|---|---:|---|
| Immediate nonblocking send on the ring worker, async fallback | 42,047 | Removing send SQEs did not remove the gap. |
| Shutdown-driven receive termination without explicit receive-cancel SQE | 40,592 | Fewer cancellation operations did not remove the gap. |
| Nonblocking accepted sockets | 41,158 | No material improvement in this screen. |
| 120 rather than 1,200 client connections | 31,033 | Lower concurrency did not fix the source-port issue. |
| One ring pump rather than four | 26,381 | Collapsing the transport onto one core was worse. |

These are exploratory one-run probes, not shipping changes or evidence to promote those alternatives. No reset/zero-linger shortcut or HTTP-specific close rule was introduced.

### What to do next

1. Keep the single-source short test as an end-to-end connection-turnover workload, but also benchmark multiple source addresses or separate client machines, inspect client kernel CPU, and track TIME_WAIT pressure. Do not infer server capacity from RPS alone.
2. If single-source turnover parity is required, investigate reducing the response-completion-to-FIN delay. A transport command that combines a final send with shutdown is only valid once the output pipe actually signals completion; do not parse `Connection: close` in the transport or close every connection after one write.
3. Reduce lifecycle command/scheduler handoffs and pool completion/page wrappers only after profiling those costs. The immediate-send probe shows that removing one SQE is insufficient on its own.
4. Do not change graceful closes to abortive resets simply to improve a benchmark. Close semantics and client behavior are part of the comparison.

There is no evidence from this investigation that changing Kestrel's HTTP parser is the right fix. We have established a major churn-related bottleneck and demonstrated comparable end-to-end throughput with source-port pressure controlled; we have not implemented a general server-side fix for the original single-source workload.

### Evidence and reproduction

- `NetworkProtoSample/results/tcp-short-native-profile-20260928-133047/`: process-scoped `perf.data`, reports, and server/client logs. Sampling used the already installed `/usr/lib/linux-tools-6.8.0-138/perf`; no machine profiling settings changed.
- `results/tcp-short-*-20260928-13*/`: metrics deltas, per-thread CPU/context-switch samples, client CPU, load output, and applicable TIME_WAIT/netstat snapshots.
- `results/tcp-short-investigation-20260928.json`: aggregate validated measurements.
- `scripts/tcp-short-investigation.sh`: direct stock/IoUringTcp comparison; accepts backend, offered rate, and label. `WRK2`, `CONNECTIONS`, and `SAMPLE_DLL` select diagnostic variants.
- `scripts/wrk-source-addresses.patch`: source-address diagnostic to apply to a separate copy of the already monotonic-clock-patched wrk2 checkout. The ordinary client was not modified.

```bash
cd ~/code/aspnetcore
scripts=src/Servers/Kestrel/samples/NetworkProtoSample/scripts
$scripts/tcp-short-investigation.sh sockets 200000 stock
$scripts/tcp-short-investigation.sh IoUringTcp 200000 uring
# Diagnostic client was built separately under artifacts/tmp/tcp-short-probes/wrk2:
WRK2="$PWD/artifacts/tmp/tcp-short-probes/wrk2/wrk" WRK_SOURCE_IPS=8 \
    $scripts/tcp-short-investigation.sh IoUringTcp 200000 uring-eight-sources
```

The native transport source and native baseline binary were unchanged in the source-address confirmation runs. Only investigative scripts, this report, and ignored artifacts were added for this task.

## Post-fix coalesced rerun (2026-09-28, 12:37 onward)

Per user request, only coalesced configurations were rerun after the rejected-accept cleanup fix. **Stock and non-coalesced rows below are carried forward from the earlier same-day matrix**, not freshly measured controls. Percentages use that earlier stock mean; environmental drift limits direct conclusions about differences between old and new rows.

All fresh cells have two 15-second runs with reversed configuration order. Same four server cores, twelve separate client cores, 1,200 connections, response and TLS settings as the earlier matrix. Both client and server use OpenSSL 3.5.8. The corrected page-return read guard is enabled for fd TLS; BIO and kTLS rows use coalesced wakes.

| Transport | TCP short RPS | TCP long RPS | TLS short RPS | TLS long RPS |
|---|---:|---:|---:|---:|
| Stock Sockets / SslStream (earlier) | 56,202 | 182,773 | 4,400 | 106,327 |
| New IoUringTcp / SslStream (earlier) | 42,638 (-24.1%) | 197,916 (+8.3%) | 4,248 (-3.4%) | 116,673 (+9.7%) |
| Fd-bound IoUringTls (earlier) | N/A | N/A | 7,435 (+69.0%) | 124,557 (+17.1%) |
| IoUringTcp, coalesced wakes (rerun) | 44,331 (-21.1%) | 209,450 (+14.6%) | 4,125 (-6.2%) | 121,123 (+13.9%) |
| IoUringTls, coalesced wakes (rerun) | N/A | N/A | 6,743 (+53.2%) | 143,565 (+35.0%) |
| Layered IoUringBio, coalesced (rerun) | 38,187 (-32.1%) | 204,780 (+12.0%) | 7,408 (+68.4%) | 146,571 (+37.8%) |
| Fd IoUringTls, verified kTLS RX+TX, coalesced (rerun) | N/A | N/A | 6,734 (+53.1%) | 130,487 (+22.7%) |
| Custom-BIO IoUringBio, required kTLS | N/A | N/A | Not activated (prior probe) | Not activated (prior probe) |

All **24 fresh load runs** completed without wrk2 socket/HTTP error summaries or shutdown failures. Every run supplied all four worker summaries; plaintext and ciphertext page-return counts balanced. The runs rejected and disposed 185 late accepts in total, demonstrating that the repaired race was exercised. Required-kTLS runs reported RX and TX active on every completed handshake with no rejected activation.

The failed-cleanup cells from the earlier matrix are now covered by fresh successful runs; their original failed attempts remain documented below, not retroactively accepted. No required-kTLS custom-BIO load was run because the prior activation probe showed it unsupported.

Values are arithmetic means, and the workloads are still overloaded (200k offered TCP short, 50k TLS short, 1M long-lived). The earlier stock TCP-short baseline had timeouts; these percentages do not establish sustainable capacity or error-free equivalence with that control. Do not attribute the difference between a carried-forward non-coalesced row and a fresh coalesced row solely to coalescing.

Local evidence: `NetworkProtoSample/results/coalesced-fixed-matrix-20260928-123702/`, including `runs.tsv`, environment/binary hashes and `validated-summary.json`. Reproduce only these configurations with `COALESCED_ONLY=1` when invoking `scripts/final-matrix.sh`.

## Shutdown regression fixed (2026-09-28)

The incomplete cleanup reported in the final matrix below was reproduced and traced to **rejected-accept ownership**, not a demonstrated lost wake in the coalescing algorithm.

After `UnbindAsync` closes the accepted-connection channel, an already accepted socket can finish its TLS handshake. The pump creates its managed connection but cannot enqueue it to Kestrel. Previously it only called `Abort`. No Kestrel consumer ever received that connection to call `DisposeAsync`, so its engine dictionary entry and native allocation remained. The engine's stop loop waited forever for the dictionary to empty.

Diagnostic snapshots showed the pump continuing to advance with an empty command queue, `wakeScheduled=0`, send/native-completion tasks already finished, native `ops=0`, `leased=0`, and `done=1`. This rules out a sleeping pump or outstanding kernel I/O as the cause of those captured hangs.

The fix schedules disposal of rejected connections without blocking the pump and tracks pending rejected disposals until completion. Normal accepted connections retain their existing Kestrel ownership. Wake coalescing itself is unchanged.

### Faithful regression evidence

`RejectedAcceptCheck` uses real TLS 1.2 clients. A wrapper delays their second network write, after ClientHello/server handshake traffic but before the server can finish accepting TLS. The check closes the listener's accepted channel with `UnbindAsync`, releases the clients, and requires listener disposal within five seconds. Counters assert that all 16 late handshakes were rejected and disposed.

Before the fix, **all four cases failed with the expected disposal timeout**: fd TLS and custom-BIO TLS, each with coalescing disabled/enabled. After the fix, all four passed with `rejectedAccepts=16` and `rejectedAcceptsDisposed=16` across the four workers.

```bash
cd ~/code/aspnetcore
source activate.sh
dotnet build src/Servers/Kestrel/samples/NetworkProtoSample/NetworkProtoSample.csproj -c Release --no-restore
src/Servers/Kestrel/samples/NetworkProtoSample/scripts/check-rejected-accept.sh
```

The test is an opt-in executable check in the existing prototype sample, not a new shipping test/API. Original red evidence is in `results/rejected-accept-red/`; green evidence is in `results/rejected-accept-green/` and `results/rejected-accept-20260928-121928/`.

### Load rechecks

Eight subsequent 15-second short-TLS load runs (two each: fd without coalescing, fd with coalescing, BIO with coalescing, and kTLS with coalescing) all shut down within the original ten-second runner limit. Several runs actually rejected 83-167 late accepts and disposed every one. Thus the load checks reached the repaired branch rather than merely avoiding the race.

| Configuration | Observed post-fix short TLS RPS |
|---|---:|
| Fd TLS, wakes not coalesced | 5,544 / 5,846 |
| Fd TLS, coalesced | 5,914 / 6,049 |
| Layered BIO, coalesced | 6,167 / 6,142 |

These are follow-up load/shutdown checks, not a replacement same-time stock matrix or a claim that cleanup made throughput faster. Machine throughput drifted from the preceding matrix.

A separate reporting issue was also observed: managed `Console` and native `stdout` summaries could interleave during concurrent worker shutdown, corrupting JSON. Per-worker final reporting/destruction is now serialized; it is outside the serving hot path. Two further kTLS runs reported 6,992 / 7,265 RPS, completed shutdown, and supplied all four readable native summaries with complete RX/TX activation and balanced page returns.

The quick runner now propagates server exit/cleanup failure instead of returning success after forced termination. The historical failed attempts below remain excluded; they were not retroactively turned into passing results.

## Final same-version matrix (2026-09-28)

Fresh runs only: the table does not mix earlier baselines or library versions. OpenSSL 3.5.8 was selected for all server processes and for wrk2. Both server library paths were checked for HTTPS; the client's library linkage is recorded in the environment file. Four server cores (`0,2,4,6`), twelve disjoint client cores (`8,10,12,14,16,18,20,22,24,26,28,30`), `DOTNET_PROCESSOR_COUNT=4`, 1,200 connections, two 15-second runs per supported cell. Transport order was reversed in round two. Response remains 1,024 bytes with the same Kestrel application and TLS 1.2/RSA/AES-128-GCM settings; resumption is disabled.

Fd-TLS rows all enable the corrected page-return guard. Rows explicitly named coalesced enable wake coalescing; the layered BIO and verified-kTLS rows also enable it. Stock ignores these prototype switches. No transport implementation was changed during this matrix.

**This matrix exposed incomplete cleanup in several TLS short-connection configurations.** Those attempts are not counted as accepted results. The table uses arithmetic means of the two completed/accounted runs, except the one-run BIO short result explicitly marked below. Percentages compare against stock in this matrix.

| Transport | TCP short RPS | TCP long RPS | TLS short RPS | TLS long RPS |
|---|---:|---:|---:|---:|
| Stock Sockets / SslStream | 56,202 (baseline) | 182,773 (baseline) | 4,400 (baseline) | 106,327 (baseline) |
| New IoUringTcp / SslStream | 42,638 (-24.1%) | 197,916 (+8.3%) | 4,248 (-3.4%) | 116,673 (+9.7%) |
| Fd-bound IoUringTls | N/A | N/A | 7,435 (+69.0%) | 124,557 (+17.1%) |
| IoUringTcp, coalesced wakes | 43,968 (-21.8%) | 207,320 (+13.4%) | 4,335 (-1.5%) | 117,600 (+10.6%) |
| IoUringTls, coalesced wakes | N/A | N/A | Incomplete cleanup (a) | 137,587 (+29.4%) |
| Layered IoUringBio, coalesced | 41,943 (-25.4%) | 208,717 (+14.2%) | 7,832 (+78.0%), one completed run (b) | 144,016 (+35.4%) |
| Fd IoUringTls, verified kTLS RX+TX, coalesced | N/A | N/A | Incomplete accounting (c) | 124,198 (+16.8%) |
| Custom-BIO IoUringBio, required kTLS | N/A | N/A | Not activated | Not activated |

Failed/incomplete attempts are retained, not hidden:

- (a) The two final coalesced fd short attempts reported 7,756 and 7,524 RPS during load, but failed to shut down within 45 seconds and lacked complete worker/page accounting. An earlier 10-second-cleanup attempt also failed. None is an accepted result.
- (b) The second BIO short attempt reported 7,564 RPS, but timed out during cleanup and lacked complete plaintext/ciphertext page accounting. The 7,832 value is only the first, completed run; no two-run mean is claimed.
- (c) kTLS short attempts reported 7,039 and 7,293 RPS during load, but their final logs lacked all four worker/native summaries. Some active RX/TX handshakes were observed, but full run accounting could not be verified. These are excluded rather than called successful verified-kTLS measurements.
- The custom-BIO required-kTLS probe on the same library reported RX=0/TX=0 and rejected the connection. No fallback RPS is presented.

The load can complete even when shutdown is defective. Thus the failed observations above do not establish a valid capacity result, and fixing lifecycle behavior must precede treating those configurations as ready.

### Interpretation

All offered rates deliberately exceeded capacity: 200k TCP short, 50k TLS short and 1M long-lived. Corrected latency grew to seconds; this is not sustainable latency-SLO capacity. Stock TCP short runs reported 99 and 91 client timeouts. No other final load run printed a wrk2 socket/HTTP error summary, but that does not override the cleanup failures.

Stock TCP short varied from 62,134 to 50,271 RPS; TLS long varied from 96,934 to 115,719. The percentages are descriptive of this batch, not precise universal improvements. Among the completed TLS long cells, layered BIO was highest. kTLS was 9.7% below the equivalent coalesced fd userspace-TLS mean (124,198 versus 137,587), despite being above stock.

For accepted native runs, the final worker/page counters were checked. Accepted kTLS long runs had RX and TX active for every completed handshake, with no rejected activation. The failed short cases are not silently included in those claims.

### Reproduction and evidence

`scripts/final-matrix.sh` under NetworkProtoSample runs one workload group using the existing quick runner:

```bash
cd ~/code/aspnetcore
export MATRIX_RESULTS="$PWD/src/Servers/Kestrel/samples/NetworkProtoSample/results/my-matrix"
src/Servers/Kestrel/samples/NetworkProtoSample/scripts/final-matrix.sh http close
src/Servers/Kestrel/samples/NetworkProtoSample/scripts/final-matrix.sh http keepalive
src/Servers/Kestrel/samples/NetworkProtoSample/scripts/final-matrix.sh https close
src/Servers/Kestrel/samples/NetworkProtoSample/scripts/final-matrix.sh https keepalive
```

The script preserves failures and can resume completed groups; it is a measurement helper, not an assertion that all runs pass. The separate required-kTLS custom-BIO probe is not included as a load test.

Evidence from this matrix is in `NetworkProtoSample/results/final-matrix-20260928-110856/`: environment/assembly hashes, workload logs, `runs.tsv`, `validated-summary.json`, and the failed custom-BIO kTLS probe. Each workload log links to its full quick-run directory with native counters, process maps, pre/post metrics, and kernel TLS snapshots. All these result directories are local and gitignored.

## OpenSSL 3.5.8 and guarded page-return reads (2026-09-28)

The CLI now defaults to a separate `/opt/openssl-3.5.8` installation. Ubuntu's `/usr/bin/openssl` and system libraries remain unchanged. `NetworkProtoSample` launchers select the new libraries only for the server process; wrk2 remains on the same Ubuntu 3.0.13 libraries in every comparison. `SERVER_OPENSSL_LIB=/usr/lib/x86_64-linux-gnu` selects the old server libraries. The benchmark records and verifies both loaded library paths.

`NETWORKPROTO_GUARD_PAGE_READ=1` is a separate, opt-in fd-TLS experiment. Returning a plaintext page no longer unconditionally calls `SSL_read_ex`: the driver resumes when TLS/input work is buffered or reading was paused for lack of pages, otherwise it leaves the readiness watch active.

The first guard implementation stalled the 3,001-response slow-reader pipeline, while the guard-off control passed. The fix preserves `read_backpressured` when the fourth page is leased, even if `SSL_has_pending` is false: unread records can still be in the socket. The corrected variant passed real Kestrel fragmentation, pipelining, slow reading, peer-disconnect and close checks on both OpenSSL versions, with kTLS off and on. This is not a full proof for every pool-exhaustion or cancellation race.

Same benchmark shape as previous tests: 4 server cores, 12 disjoint client cores, 1,200 connections, TLS 1.2/RSA/AES-128-GCM, 1,024-byte response, no resumption, coalesced wakes. Two 15-second runs per fd configuration, reversing configuration order in the second pass. Table entries are arithmetic means:

| OpenSSL | Page-return guard | kTLS RX+TX | TLS short RPS | TLS keep-alive RPS | Process CPU us/request, keep-alive |
|---|---|---|---:|---:|---:|
| 3.0.13 | Off | Off | 5,570 | 142,557 | 25.7 |
| 3.0.13 | On | Off | 5,497 | 142,279 | 24.8 |
| 3.0.13 | Off | On | 5,450 | 124,315 | 28.2 |
| 3.0.13 | On | On | 5,466 | 140,306 | 25.9 |
| 3.5.8 | Off | Off | 5,517 | 140,097 | 26.2 |
| 3.5.8 | On | Off | 5,692 | 149,144 | 24.4 |
| 3.5.8 | Off | On | 5,632 | 126,234 | 27.9 |
| 3.5.8 | On | On | 5,627 | 134,638 | 26.2 |

On 3.5.8, the guard improved mean keep-alive RPS by about 6.5% without kTLS and 6.7% with it. The read counters show the clearer mechanism: approximately two `SSL_read_ex` attempts per successful read with the guard off, versus approximately one with it on. Kernel/software TLS remained active on both directions in every required-kTLS handshake, and page-return counts balanced.

There was no consistent large native-transport gain from the OpenSSL version change alone. kTLS did not become a universal win: on 3.5.8 with the guard enabled, its mean keep-alive throughput remained about 9.7% below userspace TLS. The large per-run spread (for example, 140,812-157,476 RPS in the guarded 3.5.8 userspace case) limits precise percentage claims.

Additional stock Sockets/SslStream controls, one run each:

| OpenSSL loaded by stock server | TLS short RPS | TLS keep-alive RPS |
|---|---:|---:|
| 3.0.13 | 3,666 | 121,525 |
| 3.5.8 | 4,191 | 128,260 |

The stock short runs had 3 and 2 client timeouts respectively. These single controls suggest a version effect worth further study, not a verified universal handshake speedup. The fd runs had no wrk socket/HTTP error summary. All runs were intentionally overloaded (50k offered short, 1M offered keep-alive), with corrected latency growing to seconds. Neither the achieved rates nor CPU accounting establish maximum sustainable service or full-system CPU cost.

Reproduce the new default and explicitly enable the experiment:

```bash
cd ~/code/aspnetcore
NETWORKPROTO_GUARD_PAGE_READ=1 NETWORKPROTO_COALESCE=1 \
    src/Servers/Kestrel/samples/NetworkProtoSample/scripts/quick-rps.sh IoUringTls
# Add NETWORKPROTO_KTLS=1 to require kernel RX and TX.
# Add SERVER_OPENSSL_LIB=/usr/lib/x86_64-linux-gnu for the old server library.
```

Local evidence: `results/quick-IoUringTls-*-ssl-{old,new}-{first,second}-20260928-*/`, stock controls with `stock-old` / `stock-new` labels, and aggregate `results/openssl-guard-comparison-20260928.json`. No system-wide library-loader configuration was changed.

## kTLS experiment (2026-09-27 evening)

`NETWORKPROTO_KTLS=1` requests and **requires both RX and TX kTLS** on native TLS connections. `0` or unset preserves userspace TLS. The setting applies to the native prototypes, not stock SslStream. Existing TLS version/cipher, certificate, CPU affinity, response and resumption policy are unchanged.

Activation results:

- Fd-bound `IoUringTls`: every completed handshake in the measured kTLS runs activated both directions. OpenSSL activation queries and Linux `TlsRxSw`/`TlsTxSw` deltas agreed; the kernel snapshot starts after one readiness connection, hence a one-connection offset from total handshake counters.
- Transport-driven `IoUringBio`: enabling the option produced RX=0/TX=0. Required-kTLS mode rejected the connection rather than silently benchmarking userspace TLS. Its custom source/sink BIO has no socket/kTLS control implementation; no kTLS throughput result is available for that unchanged architecture.
- This is software kTLS, not NIC offload or zero-copy networking. No kTLS was activated on the stock or native-userspace controls.

Two 15-second runs per cell, second round in reverse configuration order. Four server cores, twelve client cores, 1,200 connections, TLS 1.2, RSA/AES-128-GCM, 1,024-byte response, native wake coalescing enabled. Means:

| Transport | TLS short RPS | TLS keep-alive RPS | Server CPU us/request, keep-alive |
|---|---:|---:|---:|
| Stock Sockets / SslStream | 2,777 | 94,021 | 38.6 |
| Fd IoUringTls, userspace TLS | 4,731 | 114,575 | 31.6 |
| Fd IoUringTls, verified kTLS RX+TX | 4,707 | 102,020 | 34.6 |
| IoUringBio, userspace TLS | 4,680 | 124,672 | 27.0 |
| IoUringBio, required kTLS | Not activated | Not activated | N/A |

**No kTLS performance improvement was demonstrated:** relative to the same fd implementation without kTLS, short RPS was approximately unchanged (-0.5%), and keep-alive was 11.0% lower. Process CPU time per keep-alive request rose about 9.4%. Although the kTLS transport beat stock in this batch, that is not evidence of a kTLS-specific gain: the native userspace transport was already faster.

Offered rates again exceeded capacity (50k short, 1M keep-alive), so corrected latency grew to seconds. One stock short run had 11 client timeouts; the other runs had no wrk error summary. These measurements are not sustainable latency-SLO capacity. CPU values cover the process, not all system/kernel background work. Absolute rates have drifted since earlier batches; compare only these fresh controls. No managed-allocation improvement from kTLS was observed (both fd modes approximately 4.1 KB/request keep-alive).

All exposed pages were returned in measured shutdown counters, and kernel TLS decryption-error deltas were zero. The actual Kestrel framing/persistence check passed with kTLS required, including a header across native-page boundaries, 132 requests, pipelining and explicit close.

```bash
cd ~/code/aspnetcore
NETWORKPROTO_KTLS=1 NETWORKPROTO_COALESCE=1 \
    src/Servers/Kestrel/samples/NetworkProtoSample/scripts/quick-rps.sh IoUringTls
```

Do not use that benchmark command with `IoUringBio` and expect a fallback. It deliberately fails the required-kTLS gate. The earlier custom-filter BIO demo in the Network-Transport-Experiments repository is different: it delegates to a socket BIO and therefore preserves socket-specific kTLS machinery. Implementing a kernel-record-aware custom source/sink or handing off from completion-based ciphertext I/O would be a separate design, not simply enabling this flag.

Local evidence: `results/ktls-activation-20260927-195802/`, `results/quick-*-ktls[01]-20260927-*/`, and aggregate `results/ktls-comparison-20260927.json` under `NetworkProtoSample`.

## Follow-up: optional custom-BIO layering (2026-09-27)

The new `IoUringBio` backend supports raw TCP or TCP plus transport-driven OpenSSL TLS. Its custom BIO references received ciphertext pages instead of copying them into a memory BIO; io_uring performs the actual ciphertext receives/sends. See [the implementation and full results](../IoUringBio/README.md).

Fresh 15-second controls on the same four-core configuration, not percentages against the earlier baseline:

| Transport | TCP short RPS | TCP keep-alive RPS | TLS short RPS | TLS keep-alive RPS |
|---|---:|---:|---:|---:|
| Stock Sockets / SslStream | 65,911 (a) | 208,079 (a) | 3,930 | 128,650 |
| Raw IoUringTcp + SslStream | Not rerun | Not rerun | 3,673 (b) | 142,185 (b) |
| Fd-bound IoUringTls | N/A | N/A | 5,368 | 146,222 |
| Custom-BIO IoUringBio | 44,415 (a) | 225,150 (a) | 5,359 | 163,026 |

TLS values are medians of three runs except (b), which is the average of two runs. (a) Plaintext values are single runs. Native variants use coalesced wakes. Offered load exceeded capacity; these are throughput experiments, not sustainable rates. The custom-BIO median is approximately tied with fd TLS on short connections and 11.5% higher on keep-alive; timing ranges overlap. No end-to-end zero-copy or universal speedup is claimed.

The historical results below remain unchanged.

Measured on 2026-09-26 in Ubuntu WSL2. These are experimental results, not production-capacity or universal performance claims.

## Setup

- Four server cores: `0,2,4,6`, with `DOTNET_PROCESSOR_COUNT=4`.
- Twelve separate physical client cores: `8,10,12,14,16,18,20,22,24,26,28,30`.
- wrk2, 1,200 connections, two 15-second runs per table cell unless noted.
- Same Release sample, real Kestrel HTTP/1.1 parsing, 1,024-byte response, diagnostic response headers disabled with `--minimal true`.
- TLS 1.2, RSA-2048 certificate, `TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256`; TLS resumption disabled.
- Short-lived: `Connection: close`, a new TCP connection per request.
- Long-lived: HTTP keep-alive, multiple requests per connection.

## Average achieved RPS and change versus stock

Each cell shows the arithmetic mean of the original two runs and the percentage change versus the stock mean for the same workload. Percentages use unrounded values.

| Transport | TCP short-lived | TCP long-lived | TLS short-lived | TLS long-lived |
|---|---:|---:|---:|---:|
| **Stock Sockets / SslStream** | **55,956** | **166,011** | **3,342** | **109,143** |
| Existing single-ring io_uring / SslStream | 33,821 (-39.6%)* | 72,658 (-56.2%) | 3,108 (-7.0%) | 63,120 (-42.2%) |
| IoUringTcp owned pages / SslStream for HTTPS | 41,849 (-25.2%) | 200,273 (+20.6%) | 3,543 (+6.0%) | 125,866 (+15.3%) |
| IoUringTls native post-TLS | N/A | N/A | 5,668 (+69.6%) | 136,077 (+24.7%) |
| IoUringTcp, coalesced wakes | 39,441 (-29.5%) | 193,789 (+16.7%) | 3,060 (-8.4%) | 137,807 (+26.3%) |
| IoUringTls, coalesced wakes | N/A | N/A | 5,429 (+62.4%) | 145,915 (+33.7%) |

*One uncontended run. The other existing-io_uring TCP short-lived run briefly overlapped correctness probes and is excluded.

`IoUringTls` has no plaintext mode, hence N/A in the TCP columns.

## Observed ranges

| Transport | TCP short-lived RPS | TCP long-lived RPS | TLS short-lived RPS | TLS long-lived RPS |
|---|---:|---:|---:|---:|
| Stock Sockets / SslStream | 54,946-56,967 | 158,035-173,987 | 3,257-3,428 | 107,617-110,669 |
| Existing single-ring io_uring / SslStream | 33,821* | 72,351-72,965 | 3,098-3,117 | 63,046-63,193 |
| IoUringTcp owned pages / SslStream | 41,000-42,698 | 199,589-200,958 | 3,518-3,568 | 123,034-128,697 |
| IoUringTls native post-TLS | N/A | N/A | 5,505-5,831 | 132,348-139,806 |
| IoUringTcp, coalesced wakes | 39,236-39,645 | 192,024-195,553 | 3,013-3,107 | 132,665-142,949 |
| IoUringTls, coalesced wakes | N/A | N/A | 5,366-5,493 | 141,629-150,202 |

## Important limitations

These are **overloaded achieved-throughput measurements**, not sustainable service rates. Offered RPS was 200,000 for TCP short-lived, 50,000 for TLS short-lived, and 1,000,000 for keep-alive. Corrected latency grew to seconds.

Stock TCP short-lived runs reported 49 and 17 timeouts; one stock TLS short-lived run reported 32. The existing io_uring TCP short-lived runs also reported timeouts. The new-prototype load output reported no wrk2 socket/HTTP errors in these runs.

**Baseline drift was material.** A final stock control run measured:

| TCP short-lived | TCP long-lived | TLS short-lived | TLS long-lived |
|---:|---:|---:|---:|
| 57,435 | 183,138 | 3,174 | 129,806 |

Against that later control, the best prototype averages are approximately **+9.4% TCP keep-alive** and **+12.4% TLS keep-alive**, rather than the larger percentages against the initial baseline. Neither comparison establishes a universal speedup.

## What differs between transports

- **Existing io_uring:** the preserved single-ring implementation, with SslStream above it for HTTPS.
- **IoUringTcp:** four pinned ring pumps, multishot accept/receive into native provided-buffer pages, and an owned-buffer `PipeReader`. HTTPS still uses Kestrel's SslStream middleware.
- **IoUringTls:** OpenSSL terminates TLS inside the transport. It uses fd-bound TLS with read-ahead and multishot readiness polling. `SSL_read_ex` fills native plaintext pages exposed to Kestrel. This path does not call `UseHttps()`.
- **Coalesced wakes:** the same transport with redundant cross-thread eventfd notifications suppressed using `NETWORKPROTO_COALESCE=1`.

The new adapters avoid an additional receive-payload copy into Pipes. This is not NIC-to-application zero-copy or a claim that OpenSSL has no internal copies. Output storage stays pinned through native completion. TCP sends are one-shot; no ordinary multishot-send operation is claimed.

## Interop and allocation observations

| Coalesced path | Requests in example run | Native receive/readiness submissions | Pump-step plus wake P/Invokes per request |
|---|---:|---:|---:|
| IoUringTcp, plaintext keep-alive | 2,881,291 | 1,204 multishot receives | 0.115 |
| IoUringTls, TLS keep-alive | 2,254,019 | 1,203 readiness polls | 0.102 |

These counts cover specific P/Invokes, not every runtime interop or OS syscall. Every exposed receive page was returned in the measured shutdown counters.

Prototype keep-alive managed allocation remained about **4.0-4.3 KB/request**, versus **1.6-1.7 KB/request** for stock. Per-page wrappers, task completions, and cross-thread commands remain costs. Each worker reserves 32 MiB of native page storage. These are not zero-allocation or production-hardened implementations.

## Evidence and reproduction

The full investigation is in [NetworkProtoSample/RESULTS.md](../../../samples/NetworkProtoSample/RESULTS.md). Local raw output is in that sample's ignored `results/quick-*-20260926-*/` folders, with aggregate data in `results/quick-comparison-20260926.json`. Raw output is not necessarily included in a fresh Git clone.

```bash
cd ~/code/aspnetcore
src/Servers/Kestrel/samples/NetworkProtoSample/scripts/quick-rps.sh sockets
src/Servers/Kestrel/samples/NetworkProtoSample/scripts/quick-rps.sh io_uring
src/Servers/Kestrel/samples/NetworkProtoSample/scripts/quick-rps.sh IoUringTcp
src/Servers/Kestrel/samples/NetworkProtoSample/scripts/quick-rps.sh IoUringTls
NETWORKPROTO_COALESCE=1 src/Servers/Kestrel/samples/NetworkProtoSample/scripts/quick-rps.sh IoUringTcp
NETWORKPROTO_COALESCE=1 src/Servers/Kestrel/samples/NetworkProtoSample/scripts/quick-rps.sh IoUringTls
```
