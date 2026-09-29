# Managed io_uring transport engines

The September 29, 2026 refactor moves the newer `IoUringTcp`, fd-bound `IoUringTls`, and layered `IoUringBio` worker/connection logic into C#. The previous C implementations are preserved in the benchmark baseline deployment, not retained as an alternate production path.

The older `IoUring` single-ring experiment and the separate `Epoll` implementation are unchanged. No common base engine across epoll and io_uring is introduced. The three newer io_uring modes already shared a managed adapter, and continue to share their managed completion engine.

## What moved, and what remains native

| File | Responsibility |
|---|---|
| `Transport.cs` | Listener registration/binding, worker creation, accepted channel, unbind/stop coordination |
| `Engine.cs` | Dedicated C# worker loop, command drain, completion-batch dispatch, connection identities, page pools, counters and ownership accounting |
| `RingConnection.cs` | TCP/TLS state machines, multishot lifetime, cancellation, partial sends, linked final send/shutdown, BIO ciphertext queue, TLS retry/shutdown |
| `Connection.cs` | Kestrel connection, output-pipe send loop, pinned output ownership, TLS features, asynchronous completions |
| `OwnedPipeReader.cs` | Existing provider-owned input-page reader; unchanged by this refactor |
| `Native.cs` / `native.c` | Liburing/OpenSSL/Linux interop: prepare operations, copy raw CQEs into a caller-provided array, return buffers, perform a TLS call and decode its result |
| `../IoUringBio/Native.cs` / `native.c` | Small custom BIO callbacks and buffer accessors; no socket I/O, event loop, connection list, or completion dispatch |

The main C file fell from 559 to 190 lines; the BIO C file from 619 to 94. Together that is **1,178 -> 284 lines, approximately 76% less C**. Native `np2_step`, native connection allocation/list management, native TLS progression, and native transport metrics are gone.

The remaining ring struct is an opaque liburing handle plus the buffer-ring handle. Keeping that ABI in C avoids reproducing liburing's inline helpers and layout in C#. C does not decide which connection should run or how an application send is retried. `SSL_get_error` remains adjacent to its OpenSSL operation on the same OS thread.

BIO callbacks could be written as native-callable managed functions, but this version deliberately leaves the small synchronous copy/retry callbacks in C. Moving the entire transport state machine is independent of that callback choice.

## The C# loop

```text
Kestrel/ThreadPool producers
    -> send / return-page / close / forget commands
    -> coalesced eventfd wake

Engine.Run on the connection's owning thread:
    drain bounded command batch
    -> C# decides which SQEs to prepare
    -> Native.Collect submits and returns raw CQEs
    -> C# dispatches CQEs by connection ID and operation kind
    -> RingConnection advances TCP/TLS and reports application completion
```

All state mutation and OpenSSL operations stay on the owning worker. The output producer keeps the pipe segment pinned until its managed send completion. Kestrel/application continuations remain asynchronous; they do not run arbitrary HTTP application code on the ring pump.

Raw TCP uses provided-buffer multishot receive and ordinary send SQEs. Fd TLS uses multishot readable polls and one-shot writable polls, while OpenSSL performs socket I/O. Custom-BIO TLS uses actual multishot ciphertext receive and send SQEs.

`NETWORKPROTO_COALESCE`, `NETWORKPROTO_FINAL_SEND`, `NETWORKPROTO_GUARD_PAGE_READ`, `NETWORKPROTO_KTLS`, `NETWORKPROTO_CPUS`, and existing registration/sample names remain available. Port zero is explicitly rejected because independently bound reuse-port listeners cannot report one common dynamically assigned port.

## Completion, buffer, and descriptor ownership

CQEs use connection IDs and operation-kind tokens rather than native object pointers. Each connection tracks original read/write/shutdown operations separately from cancellation acknowledgments. A cancel completion is not permission to free the original operation; multishot remains live while `IORING_CQE_F_MORE` is present.

The connection object, socket, pinned sends, and BIO output remain alive until all relevant terminal CQEs drain. Managed page leases additionally gate final disposal. Cancellation/rearming does not reuse a read slot until the old request and its cancellation acknowledgment have both been accounted for.

The page pools now use pinned managed arrays. Raw receives reference their published slices through the native provided-buffer ring. Fd/BIO TLS reads decrypt into a separate plaintext pool. Existing input-reader ownership still prevents reusing a page until Kestrel consumes it. The pool remains 32 MiB per worker, plus another 32 MiB for BIO ciphertext; this refactor is not a memory-minimization change.

The custom BIO borrows one queued ciphertext page at a time. C# lends the next page, calls OpenSSL, checks how much was consumed, and returns an exhausted page to the ring. The BIO still copies into OpenSSL's requested destination; no memory-BIO staging copy was introduced. Its 32 KiB output buffer remains unchanged while a send is outstanding; only a terminal send CQE permits advancing/reusing it. Handshake, application output, and close-notify use the same path.

Linked final TCP output still reserves and prepares both SQEs together: `SEND(MSG_WAITALL | MSG_MORE)` followed by `SHUTDOWN(SHUT_WR)`. C# waits for canceled successor CQEs before retrying a partial linked send. Fd TLS and BIO retain their cork-plus-close-notify final-output sequencing, without pretending an OpenSSL call is an SQE.

Worker stop now explicitly owns incomplete handshakes as well as published connections, cancels/drains accept and wake requests, and checks page/connection totals at shutdown. Partial worker-start failure stops successful sibling workers. Fatal worker/invariant failures with live connections are process-fatal in this non-shipping prototype: it reports the error and terminates rather than incorrectly release buffers still owned by the kernel. Recoverable per-connection errors use the normal completion/close path. General production recovery from a broken ring is not implemented.

## Matched September 29 RPS

The previous deployment was saved before editing under `artifacts/tmp/uring-native-baseline-20260929`. The comparison scripts alternate preserved native-engine and rewritten managed-engine runs and reverse the configuration order in the second round. The old values are freshly measured, not carried forward from yesterday.

Same WSL host, four server cores (`0,2,4,6`), twelve separate client cores, original single-source-address wrk2, 1,200 connections, 1,024-byte real Kestrel responses, two 15-second runs per cell. TLS settings/libraries were held fixed. Coalesced wakes and final-send batching were enabled on both versions; the fd path also enabled the read guard. Offered rates were 200k TCP short, 50k TLS short, and 1M persistent.

### Rewritten transports against fresh stock controls

| Transport | TCP short RPS | TCP long RPS | TLS short RPS | TLS long RPS |
|---|---:|---:|---:|---:|
| Stock Sockets / SslStream | 59,443 | 190,437 | 4,519 | 119,829 |
| C# IoUringTcp / SslStream | 76,120 (+28.1%) | 209,512 (+10.0%) | 4,466 (-1.2%) | 130,683 (+9.1%) |
| C# fd-bound IoUringTls | N/A | N/A | 7,781 (+72.2%) | 141,072 (+17.7%) |
| C# layered IoUringBio | 76,496 (+28.7%) | 201,080 (+5.6%) | 8,119 (+79.6%) | 153,437 (+28.0%) |

### Cost of moving the engine from C to C#

Each cell below is old -> new and the change against the old implementation, not stock:

| Transport | TCP short | TCP long | TLS short | TLS long |
|---|---|---|---|---|
| IoUringTcp / SslStream | 74,949 -> 76,120 (+1.6%) | 215,760 -> 209,512 (-2.9%) | 4,400 -> 4,466 (+1.5%) | 132,128 -> 130,683 (-1.1%) |
| Fd IoUringTls | N/A | N/A | 8,179 -> 7,781 (-4.9%) | 143,494 -> 141,072 (-1.7%) |
| Layered IoUringBio | 65,951 -> 76,496 (+16.0%) | 208,687 -> 201,080 (-3.6%) | 8,219 -> 8,119 (-1.2%) | 155,591 -> 153,437 (-1.4%) |

The refactor retains useful throughput without a large persistent-I/O collapse, but it is **not a no-regression result**. Persistent means were 1-4% lower, and fd TLS short was about 5% lower. The BIO plaintext short improvement also changes allocation/lifetime placement relative to its old larger C connection struct; no CPU profile isolates which part produced that gain. More individual interop calls and managed connection state are expected tradeoffs, not independently measured explanations for every percentage.

There are only two runs per cell on a shared host. For example, rewritten BIO TCP long ranged from 192,775 to 209,384, while its old control ranged 200,728-216,646. Keep both runs rather than selecting favorable samples or declaring small differences statistically established.

All 48 principal load runs had complete worker/page accounting. New native-transport runs had no wrk socket/HTTP error summaries. Stock TCP short reported 77 / 48 timeout counts. Raw linked shutdown still recorded 94 / 48 `ENOTCONN` completions in the new TCP short runs; these were surfaced through the connection error path and counted, not converted into successful shutdowns. Corrected latency grew to seconds under overload: these are achieved-throughput comparisons, not sustainable capacity.

### Required kTLS follow-up

The managed fd transport additionally measured 7,285 TLS short / 134,602 TLS long RPS in two runs each, with RX and TX active for every completed handshake. The old persistent control averaged 133,103 RPS, about 1.1% lower, with overlapping ranges.

The preserved native short-kTLS baseline aborted during cleanup twice and supplied incomplete worker accounting. Those attempted 7,458 / 7,626-RPS runs are excluded. Only one old short run completed at 7,286; it is not a two-run baseline and does not justify a precise short-kTLS improvement claim. The old binary was not modified to hide its failed cleanup.

## Verification and limits

`check-uring-managed.sh` runs actual Kestrel TCP, SslStream-over-TCP, fd TLS, BIO TCP/TLS, and required-kTLS workloads. It covers fragmented/page-spanning headers, 132-request reuse, 40 KiB fragmented bodies, 3,001 pipelined responses with a paused reader, peer disconnects, and continued service. Native fd/BIO TLS clients also observe `close_notify`.

The final-output check completes the real output pipe, verifies exact 1 KiB/4 MiB delivery before EOF, and resets during blocked 16 MiB output. Modes 0-3 passed for TCP, fd TLS, BIO TCP/TLS, and required kTLS. In the final constrained-socket sanitizer run, raw TCP exercised a partial linked send and two canceled successor links, BIO exercised 69 partial ciphertext sends and 256 TLS write retries, fd TLS 54 write retries, and kTLS 64 retries.

Sixteen idle peers per mode also observed EOF when the listener stopped with pending receives or uncompleted handshakes. The real gated-handshake rejection checks passed for fd/BIO with coalescing off/on. Partial CPU-affinity startup failure and required-kTLS rejection in the unchanged unsupported BIO architecture were checked.

Two limitations are preserved explicitly:

- The strict OpenSSL close-notify probe over **SslStream** failed with unexpected EOF on the preserved raw transport, rewritten raw transport, and stock Sockets control. `check-uring-managed.sh` therefore asserts that TLS alert for the native TLS modes, not for SslStream. HTTP payload/reuse/EOF tests still exercise the actual SslStream/Kestrel boundary. This refactor does not claim to repair the pre-existing SslStream shutdown behavior.
- The required-kTLS 4 MiB delayed-reader check did not finish with a diagnostic 4 KiB `SO_SNDBUF`. It passed with 32 KiB and normal buffers, including actual retries. The tiny-buffer kTLS behavior is unresolved and is not labeled verified or silently included in passing evidence.

ASan/UBSan instrumented both thin native libraries; leak detection was disabled in the mixed managed/native process. No sanitizer diagnostic was recorded in the accepted runs. Exhaustive global page-pool exhaustion, kernel fault injection, TLS configurations beyond the fixed prototype, and production-grade admission/failure policy remain unverified.

## Reproduction and evidence

```bash
cd ~/code/aspnetcore
source activate.sh
sample=src/Servers/Kestrel/samples/NetworkProtoSample
dotnet build "$sample/NetworkProtoSample.csproj" -c Release --no-restore
SANITIZE=1 "$sample/scripts/check-uring-managed.sh"
"$sample/scripts/check-final-send.sh" IoUringTcp
"$sample/scripts/check-final-send.sh" IoUringTls
SCHEME=https "$sample/scripts/check-final-send.sh" IoUringBio
"$sample/scripts/check-rejected-accept.sh"
"$sample/scripts/compare-managed-uring.sh" http
"$sample/scripts/compare-managed-uring.sh" https
```

`BASELINE_DLL` selects the saved baseline deployment; the comparison script defaults to the local September 29 snapshot and fails if it is missing. `NETWORKPROTO_URING_SNDBUF` is a diagnostic-only socket-buffer control, unset during benchmarks.

| Local sample `results/` evidence | Contents |
|---|---|
| `uring-managed-http-20260929-101939` | 20 TCP load runs, binary hashes, commands, audited means and ownership |
| `uring-managed-https-20260929-102528` | 28 TLS load runs and corresponding audit |
| `uring-managed-ktls-recheck-20260929-103922` | Accepted kTLS results plus retained failed old-baseline attempt |
| `uring-managed-check-20260929-103603` | Final sanitizer/data/pressure/idle-listener checks |
| `final-send-check-20260929-103720` through `final-send-check-20260929-103734` | Final modes 0-3 across five configurations |
| `rejected-accept-20260929-101937` | Gated real late TLS handshakes and disposal accounting |
| `final-send-check-20260929-104729` through `final-send-check-20260929-104736`, `rejected-accept-20260929-104740` | Final formatted build rechecks |
| `uring-sslstream-close-control-20260929-101531` | Old/new/stock strict TLS-close boundary |
| `uring-managed-check-20260929-101550/ktls-https-final.log` | Failed tiny-send-buffer kTLS attempt |
| `uring-managed-config-20260929-104155` | Configuration failures and unsupported BIO-kTLS activation |

Raw output and baseline binaries are local/gitignored. The [RPS history](../IoUring/RPS-RESULTS.md) retains prior experiments; this report documents a new architecture change rather than rewriting their old measurements.
