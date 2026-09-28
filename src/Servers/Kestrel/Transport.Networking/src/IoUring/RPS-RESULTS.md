# Kestrel transport RPS comparison

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
