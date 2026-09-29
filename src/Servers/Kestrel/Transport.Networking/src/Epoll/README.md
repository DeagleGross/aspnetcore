# Epoll TCP and TLS: managed workers with a thin native shim

This is a non-shipping Linux experiment beside the io_uring transports. It follows the fd-bound TLS/event-pump idea in [dotnet/aspnetcore#67912](https://github.com/dotnet/aspnetcore/pull/67912), but uses native OpenSSL `SSL_CTX` and `SSL` objects directly. It does not depend on the runtime's experimental `TlsContext` or `TlsSocketSession` types and does not run the PR's implementation.

There are now two separate connection modes: **`epollTcp`**, using nonblocking `recv`/`send`, and **`epollTls`**, using fd-bound OpenSSL and exposing post-TLS plaintext. They share the epoll worker/command/page plumbing, not a TLS implementation. There is no custom BIO, client transport, or TLS callback/selection framework. The existing `IoUringBio` implementation is separate; an epoll custom-BIO layering experiment remains deferred.

The initial implementation put the event dispatch and TLS state machine in C. The September 28 late-evening refactor moved these into epoll-specific C# code, reducing the native file from approximately 520 to 176 lines. Raw TCP adds just two Linux-call wrappers, bringing it to 190 lines. There is no native engine, connection list, page pool, command dispatcher, timeout scan, or metrics registry. Epoll uses no implementation type from `IoUringTcp`; a common base engine across transports remains deferred.

## Architecture

```text
N dedicated managed pump threads, each running Engine.Run in C#:

    process typed send / page-return / close commands
        -> Native.Wait -> epoll_wait(up to 128 readiness events)
        -> dispatch readiness IDs to TcpConnection or TlsConnection
        -> TCP: recv / send; TLS: one OpenSSL call plus its error result
        -> C# advances state, manages page leases, completes sends
        -> epoll-local owned PipeReader / output-pipe adapter
        -> normal Kestrel HTTP and application work on the ThreadPool
```

Every accepted fd stays with the worker that accepted it. All socket data I/O, OpenSSL calls where applicable, and epoll-interest changes for that connection run on that worker, never concurrently with application threads. Cross-thread commands use eventfd, with epoll's own copy of the optional wake-coalescing policy. A small lifetime gate protects the eventfd against a last producer reaching its wake call after the pump has processed its final command and finished.

Each C# worker owns one nonblocking `SO_REUSEPORT` listener, epoll fd, eventfd, and dictionary of managed connection states. Only TLS workers create OpenSSL contexts/sessions. This deliberately matches the existing four-pump io_uring experiment. The reference PR instead shares one listener across workers using `EPOLLEXCLUSIVE`.

The loop is blocking readiness I/O, not a busy-spin loop: waits are immediate when managed commands remain queued and otherwise bounded to 10 ms. Up to 64 accepts and 16 application TLS records per writing connection are processed before yielding. Ordinary OpenSSL writes are not epoll operations; readiness tells the worker when to retry them.

### File responsibilities

| File | Responsibility |
|---|---|
| `Transport.cs` | Listener factory, N-worker startup/unbind/disposal, accepted-connection channel |
| `Engine.cs` | Dedicated C# thread and loop, typed commands, readiness dispatch, acceptance, pinned-page pool, deadlines and counters |
| `ConnectionState.cs` | Small epoll-local dispatch contract for TCP and TLS connection states |
| `TcpConnection.cs` | Nonblocking recv/send, partial writes, half-close, read backpressure, final TCP shutdown; no OpenSSL calls |
| `TlsConnection.cs` | Handshake/read/write/shutdown state, OpenSSL retry directions, interest masks, per-connection memory ownership |
| `Connection.cs` | Kestrel connection, output-pipe send loop, completion notifications, TLS features only in TLS mode |
| `OwnedPipeReader.cs` | Epoll-local input reader, page leases, consumed/examined positions and asynchronous reader continuation |
| `Native.cs` | P/Invoke declarations, typed operation/status values, errno checks |
| `native.c` | Thin Linux calls, normalized epoll-event layout, OpenSSL context/session calls and per-call error decoding |

The C# loop is synchronous on its dedicated thread; there is no `await` that can migrate TLS work to another thread. The C wrapper keeps `SSL_get_error` immediately adjacent to the corresponding OpenSSL call because its interpretation depends on thread-local error state. It also hides OpenSSL configuration macros and Linux's packed `epoll_event` layout. Those are interop concerns, not transport scheduling.

BIO callbacks are not inherently impossible in C#: native-callable managed entry points can implement them with explicit lifetime and exception constraints. This version needs no custom BIO callbacks because OpenSSL's existing socket BIO owns fd-based TLS I/O. Whether a future custom BIO callback stays native is a separate decision, not a reason to retain the entire engine in C.

### Readiness and TLS retries

Handshake, read, write, and shutdown retain their own required direction. `WANT_READ` requests `EPOLLIN`; `WANT_WRITE` requests `EPOLLOUT`, including when an operation needs the opposite direction from its application name. The established interest mask is the union of the read and pending-write requirements, changed on the owning thread.

Polling is level-triggered. Writable interest is requested only for pending work, not permanently for every socket. After a successful read, buffered OpenSSL input can continue immediately. A consumed-page return does not unconditionally issue another speculative read; it resumes a backpressured reader. When no read/write interest remains, the fd is removed until work resumes, avoiding repeated hangup notifications while input is paused.

Each incomplete `SSL_write_ex` is retried with the identical pointer and length. Long output is split into bounded 16 KiB records; the borrowed application memory stays pinned until the whole send succeeds or is explicitly failed.

### Buffer and descriptor ownership

Each worker owns 2,048 input pages of 16 KiB, or 32 MiB, backed by a pinned managed array. Raw `recv` or TLS `SSL_read_ex` writes into these pages, and epoll's `OwnedPipeReader` exposes the same `Memory<byte>` slices to Kestrel. It does not need the native-pointer `MemoryManager` used by the io_uring adapter. A connection can lease at most four pages; returning pages resumes paused input. The pool is bounded, but this is not a production admission/memory policy.

There is no extra plaintext copy into a separate pipe buffer. This is not NIC-to-application zero-copy and does not claim that OpenSSL has no internal copies.

Closing removes the socket from epoll and closes its fd and OpenSSL session on the owning thread, when no native operation is in flight. Published connection state remains in the managed dictionary until consumers return page leases and issue `Forget`. Epoll events carry monotonic connection IDs rather than native object pointers or reusable fd numbers: a stale readiness event cannot refer to a new connection that reused a closed fd.

Normal output closure sends `close_notify` before socket shutdown. `NETWORKPROTO_FINAL_SEND=3` also corks known-final application output and initiates shutdown on the same pump immediately after the alert; it is the fd-TLS equivalent of the existing final-send experiment, not a linked send/shutdown SQE. Modes 1 and 2 select the same final-close path without corking. Eligibility still comes from real output-pipe completion, not HTTP parsing.

TLS handshakes have a fixed ten-second deadline, and stalled graceful TLS shutdown has a five-second deadline, checked by the worker. TCP has no handshake or TLS timeout sweep. Listener unbind stops acceptance without silently discarding already-progressing handshakes. Connections finishing after the accepted channel closes use the existing rejected-accept disposal path. Final listener stop also closes sockets that never completed a handshake. A failed worker initialization stops successfully started sibling workers.

### Raw TCP behavior

TCP connections are handed to Kestrel immediately after acceptance. Their state machine never creates an `SSL_CTX`/`SSL`, calls an SSL operation, or publishes TLS features. The combined native library still links OpenSSL for its TLS entry points; a loaded dependency is not evidence that the raw data path uses it.

Readable events drive `recv` into the existing page pool. A connection drains available input until `EAGAIN` or its four-page lease limit. On backpressure, readable interest is removed; returning a consumed page resumes reading. `recv == 0` completes the input reader but does not close the write side: an application may still produce a response after the peer has half-closed its output.

Output commands drive `send(MSG_NOSIGNAL)` directly. Positive partial results advance the offset; `EAGAIN` arms writable interest and retains the pinned buffer. Work per connection is bounded to 256 KiB per write drive, with remaining output resumed through level-triggered readiness. Writable interest is removed when output finishes, rather than waking continuously for idle writable sockets.

`NETWORKPROTO_FINAL_SEND=3` applies `TCP_CORK` to known-final output and initiates socket shutdown on the same worker after all bytes have been accepted. No TLS alert exists in this mode, and there is no linked SQE. Modes 1 and 2 perform immediate final-send shutdown without corking; unset/0 follows the normal output-loop close command. Input/output lifetimes and stale readiness IDs retain the same ownership rules as the TLS worker.

## Relation to the DirectTls PR

The source reference is PR head `c585e7fe504866ab00442f94dc47416ba5d42af9`, merged August 14, 2026:

| Reference design | This smaller experiment |
|---|---|
| [TlsEventPump](https://github.com/dotnet/aspnetcore/blob/c585e7fe504866ab00442f94dc47416ba5d42af9/src/Servers/Kestrel/Transport.DirectTls/src/TlsEventPump.cs): epoll worker pool, shared exclusive listener, handshake progression | Managed epoll batch engine, one reuse-port listener per worker, direct native OpenSSL handshake |
| [ConnectionIoState](https://github.com/dotnet/aspnetcore/blob/c585e7fe504866ab00442f94dc47416ba5d42af9/src/Servers/Kestrel/Transport.DirectTls/src/ConnectionIoState.cs): runtime session and lock-serialized initiating/completing operations | Every TLS operation runs on its owning pump; application threads only queue commands |
| Endpoint selection, per-connection TLS context/certificate callbacks, ALPN and client-certificate features | One static PEM certificate/key, fixed TLS 1.2/cipher, HTTP/1.1 only |
| Transport receive/send loops and pipes | Epoll-local owned-page input reader and output pipe, initially copied from the experiment adapter and now independent |

This is an architecture comparison with our fd io_uring prototype, not a benchmark of that PR or an exact performance reproduction of its runtime-backed implementation.

## Configuration and running

The public registration follows this non-shipping project's existing experiment registrations:

```csharp
builder.WebHost.UseEpollTls("cert.pem", "key.pem", workerCount: 4);
// Or raw TCP, without certificates or TLS termination:
builder.WebHost.UseEpollTcp(workerCount: 4);
```

Do not add `UseHttps` to `epollTls`: the transport already supplies plaintext and the prototype's TLS features. The sample selects it with `--backend epollTls --scheme https --workers N`. Raw TCP uses `--backend epollTcp --scheme http --workers N` and needs no certificate/key; its sample rejects HTTPS rather than silently adding another layer. The raw listener itself exposes protocol-agnostic bytes. `N` must be positive. Without `NETWORKPROTO_CPUS`, workers inherit process affinity; with it, the comma-separated list must contain exactly N nonnegative CPU IDs and pins one worker per entry. This option does not change how existing io_uring backends select their workers.

Example from the WSL repository root:

```bash
source activate.sh
dotnet build src/Servers/Kestrel/samples/NetworkProtoSample/NetworkProtoSample.csproj -c Release --no-restore
sample=src/Servers/Kestrel/samples/NetworkProtoSample
LD_LIBRARY_PATH=/opt/openssl-3.5.8/lib:$HOME/.local/lib \
NETWORKPROTO_CPUS=0,2,4,6 NETWORKPROTO_COALESCE=1 NETWORKPROTO_FINAL_SEND=3 \
DOTNET_PROCESSOR_COUNT=4 taskset -c 0,2,4,6 \
dotnet artifacts/bin/NetworkProtoSample/Release/net11.0/NetworkProtoSample.dll \
    --backend epollTls --workers 4 --scheme https --port 19800 \
    --cert "$sample/.certs-owned/cert.pem" --key "$sample/.certs-owned/key.pem"
```

The sample expects the existing local test certificate/key. The native epoll library links to OpenSSL and libc, not liburing; building the containing experiment project still builds the neighboring io_uring libraries and therefore retains that project's existing liburing prerequisite.

Scope limits: IPv4 loopback and a fixed nonzero port, TLS 1.2 with `ECDHE-RSA-AES128-GCM-SHA256`, no resumption or renegotiation, no TLS 1.3, ALPN/HTTP2, mTLS, callback selection, custom BIO, or kTLS. A nonzero `NETWORKPROTO_KTLS` request fails explicitly. The shared sample's TLS features reflect these fixed settings, not a general negotiated-feature API. PublicAPI.Unshipped tracks the experiment registration, not approval for a framework API.

## Raw TCP comparison, September 29

The new raw backend was compared with fresh stock Sockets and the C# IoUringTcp engine. All runs used the same binary/application, original single-source wrk2 client, four server cores/workers (`0,2,4,6`), twelve separate client cores, 1,200 connections, 1,024-byte response, and 15-second measurements. Coalesced wakes and mode-3 final-send batching were enabled for both experimental transports. No TLS layer or handshake ran. Two rounds reversed backend order.

| Transport | TCP short RPS, runs | Mean and change vs stock | TCP long RPS, runs | Mean and change vs stock |
|---|---|---:|---|---:|
| Stock Sockets | 47,830 / 42,966 | 45,398 | 158,045 / 136,645 | 147,345 |
| C# IoUringTcp | 56,502 / 58,361 | 57,432 (+26.5%) | 169,784 / 180,540 | 175,162 (+18.9%) |
| **C# epollTcp** | **61,616 / 57,090** | **59,353 (+30.7%)** | **160,399 / 160,935** | **160,667 (+9.0%)** |

Epoll was about +3.3% short / -8.3% long compared with io_uring in this batch. The short ordering changed between rounds, so it does not establish a stable short-connection lead. This is not a comparison against the higher stock/transport numbers from earlier sessions; the unchanged stock baseline drifted substantially even within this batch.

The raw epoll path reported zero handshakes, SSL reads/writes, and TLS close notifications. Its short runs recorded 917,643 / 848,832 corked final sends with successful final shutdowns, approximately 99% of sends. All workers exited and all input pages returned. TCP TIME_WAIT-overflow deltas were zero in all twelve load runs.

The initial implementation drains reads until `EAGAIN`: the keep-alive runs had about two recv calls per delivered input page (roughly one successful read plus one would-block read), and one send per response. This is actual syscall accounting, not proof that it explains the entire gap to io_uring. Stable keep-alive read interest required about 2,418 `epoll_ctl` calls per run, not one registration per request.

Neither experimental transport reported wrk socket/HTTP errors. Stock short reported 55 / 50 timeout counts. Raw epoll separately counted peer disconnects, including 1,139 / 1,172 peer-abort results in keep-alive runs and 2 / 24 already-disconnected shutdowns in short runs. No unexpected socket-error or shutdown-failure counter was recorded. Do not call this zero native teardown errors.

Offered load exceeded achieved capacity: 200k short and 1M long, with corrected latency growing to seconds. These are overloaded throughput measurements on a shared WSL/Windows loopback host, not sustainable latency-SLO capacity or a general epoll-versus-io_uring ranking.

Reproduce the raw run or its matched comparison:

```bash
cd ~/code/aspnetcore
source activate.sh
sample=src/Servers/Kestrel/samples/NetworkProtoSample
dotnet build "$sample/NetworkProtoSample.csproj" -c Release --no-restore
LD_LIBRARY_PATH=/opt/openssl-3.5.8/lib:$HOME/.local/lib \
NETWORKPROTO_CPUS=0,2,4,6 NETWORKPROTO_COALESCE=1 NETWORKPROTO_FINAL_SEND=3 \
DOTNET_PROCESSOR_COUNT=4 taskset -c 0,2,4,6 \
dotnet artifacts/bin/NetworkProtoSample/Release/net11.0/NetworkProtoSample.dll \
    --backend epollTcp --scheme http --workers 4 --port 19800

# After stopping the foreground server:
SANITIZE=1 "$sample/scripts/check-epoll-tcp.sh"
"$sample/scripts/compare-epoll-tcp.sh"
```

The raw checks exercised one/two/four workers through real Kestrel framing, a fragmented 40 KiB body, 3,001 pipelined responses with a paused reader, reset/continued service, and idle listener disposal. The direct transport check recovered a cancelled read, consumed 256 KiB after filling the four-page lease limit, then produced a response after observing real peer half-close. No fake readiness was injected.

The 1 KiB/4 MiB final-output and blocked 16 MiB reset cases passed in modes 0-3. With a diagnostic 4 KiB send buffer and final mode 3, one/two/four-worker checks reached respectively 69/69/130 partial sends and 48/48/85 actual `EAGAIN` results, while preserving exact payloads and EOF. Native ASan/UBSan was enabled for the raw checks; mixed managed/native leak detection was disabled. The existing TLS sanitizer, handshake-deadline, and late-accept rejection checks also passed after sharing the worker contract.

The sanitizer scripts now exclude old `results` directories when copying deployments, and the sample excludes benchmark evidence from default MSBuild items. This prevents nested benchmark artifacts from being repeatedly copied into later sanitizer deployments. Original measurement evidence remains untouched; only duplicate copies made by this task were cleaned.

Raw evidence is `NetworkProtoSample/results/epoll-tcp-matrix-20260929-114236/validated-summary.json` with per-run logs, binary hashes, counters and kernel snapshots. Correctness evidence is `epoll-tcp-check-20260929-113413`; TLS regression evidence is `epoll-check-20260929-114211` and `rejected-accept-20260929-114235`. These paths are local/gitignored. The first TLS sanitizer attempt was interrupted during deployment copying, not accepted as a test run; it was rerun after fixing the copy scope.

## Original native-engine results, September 28 evening

Before moving dispatch/TLS state to C#, the native-engine binary was measured against fresh controls: four server cores (`0,2,4,6`), twelve separate client cores, 1,200 connections, the original single-source-address wrk2 client, 1,024-byte real Kestrel responses, and two 15-second runs per cell with configuration order reversed in round two. Client/server TLS library selection was fixed. Native variants used wake coalescing and final-send batching; the fd io_uring control used the page-return read guard. These historical numbers are not measurements of the later managed loop.

| Transport | TLS short RPS, runs | Mean and change vs stock | TLS long RPS, runs | Mean and change vs stock |
|---|---|---:|---|---:|
| Stock Sockets / SslStream | 3,158 / 3,162 | 3,160 | 95,797 / 94,318 | 95,058 |
| Fd-bound IoUringTls | 5,919 / 5,823 | 5,871 (+85.8%) | 119,296 / 115,418 | 117,357 (+23.5%) |
| Layered IoUringBio | 6,006 / 5,907 | 5,957 (+88.5%) | 131,397 / 129,488 | 130,442 (+37.2%) |
| **epollTls** | **5,755 / 5,733** | **5,744 (+81.8%)** | **126,843 / 127,236** | **127,039 (+33.6%)** |

Epoll was approximately 2.2% below fd io_uring on short connections and 8.3% above it on keep-alive in this final batch. It was about 2.6% below BIO on keep-alive. An earlier screen was much closer to fd io_uring (about +2.1% short and +1.1% long), so these small-sample shared-host runs do not establish a universal epoll/io_uring ranking. The later binary additionally protects its final wake/native-destruction lifetime; differences between the two batches are not isolated measurements of that gate's cost.

All native runs supplied complete worker summaries and balanced receive-page accounting. Epoll accepted/closed counts matched, no TLS-error or handshake/shutdown-timeout counters were recorded, and all accepted late handshakes were disposed. The final short runs recorded 86,480 / 86,000 corked final sends and successful final native shutdowns. Other connections could already have closed when queued commands arrived.

The epoll runs had no wrk socket/HTTP error summary. They did count peer aborts, including 1,201 per keep-alive run at client cutoff, and `ENOTCONN` teardown results; this is not a zero-error native-close claim. One stock short run had 65 timeout counts. Offered rates were deliberately excessive (50k short and 1M long), and corrected latency grew to seconds. These are achieved-throughput results under overload, not sustainable latency-SLO capacity.

In those native-engine keep-alive runs, about 1.90 million requests required roughly 40k-43k native waits and 27k-28k managed wake interops, about 0.036 combined step/wake interops per request. `epoll_ctl` was about 2,418 calls per run rather than one per request; stable read interest does not need to be re-registered. This experiment does not claim allocation parity with stock.

## Moving the state machine to C#: same-session comparison

The original native-engine deployment was preserved before refactoring. Both binaries were measured with the same client, response, four worker/server cores, twelve client cores, 1,200 connections, coalesced wakes, and final-send batching. Two 15-second runs per cell reversed configuration order. These compare different implementations of the same epoll backend, not epoll versus io_uring.

| Implementation | TLS short RPS, runs | Mean | TLS long RPS, runs | Mean |
|---|---|---:|---|---:|
| Fresh stock Sockets / SslStream | 3,835 / 4,010 | 3,922 | 101,326 / 104,044 | 102,685 |
| Preserved native engine | 6,734 / 6,962 | 6,848 | 134,879 / 130,827 | 132,853 |
| C# engine / thin shim | 6,742 / 6,728 | 6,735 | 129,257 / 131,761 | 130,509 |

The managed version was about 1.7% lower on short connections and 1.8% lower on persistent connections in this screen; its persistent runs overlap the native-engine range. This is not evidence of a large unavoidable interop penalty, nor proof of identical performance. It remained about 72% / 27% above the fresh stock means. Keep the small sample, shared-host drift, and overload limitations in mind; one stock short run had 29 timeout counts.

All 12 runs completed with balanced worker/page accounting. Epoll recorded no TLS-error or timeout counters, while peer-abort/disconnected teardown counters remained visible. Later teardown-only refinements ensure ownership is released even if epoll removal or a connection-closed callback fails; the table records the earlier refactor screen, not an isolated estimate of those refinements.

`stepInterop` is now zero because there is no native step. `pumpIterations` counts the C# loop; wait, TLS-read/write, control, and wake counts describe the individual native boundaries. Do not compare the old combined step/wake figure to the new zero step count as if native calls disappeared.

## Verification and evidence

```bash
sample=src/Servers/Kestrel/samples/NetworkProtoSample
SANITIZE=1 "$sample/scripts/check-epoll.sh"
NETWORKPROTO_CPUS=0,2,4,6 "$sample/scripts/check-final-send.sh" epollTls
NETWORKPROTO_CPUS=0,2,4,6 "$sample/scripts/check-rejected-accept.sh" epollTls
LD_LIBRARY_PATH=/opt/openssl-3.5.8/lib:$HOME/.local/lib \
NETWORKPROTO_CPUS=0,2,4,6 NETWORKPROTO_COALESCE=1 NETWORKPROTO_FINAL_SEND=3 \
WORKERS=4 SCHEME=https "$sample/scripts/quick-rps.sh" epollTls
```

`check-epoll.sh` exercises one, two, and four workers through real Kestrel: fragmented/page-spanning headers, persistent requests, 40 KiB body fragmentation, 3,001 pipelined responses with a paused reader, peer disconnects, observed TLS `close_notify`, listener disposal with 16 real sockets stalled before ClientHello, and the actual ten-second idle-handshake deadline.

The old C-only pressure executable was removed with the native engine. Its replacement exercises the real C# engine and output pipe with `NETWORKPROTO_EPOLL_SNDBUF=4096`, an internal diagnostic socket-buffer setting. The first managed sanitizer run forced 50 actual OpenSSL write retries, verified byte-for-byte 1 KiB/4 MiB delivery before EOF, and reset cleanup during blocked 16 MiB output. This setting is off during benchmarks; no fake readiness or scripted OpenSSL result produces the retries.

The final-output check exercised modes 0-3 with exact 1 KiB/4 MiB delivery and reset during blocked 16 MiB output. The rejected-accept check used real handshake gates, with all 16 late accepts disposed for coalescing off/on. Existing raw TCP and fd TLS final-output controls, plus the fd/BIO rejected-accept controls, also passed after shared-adapter integration. Invalid worker counts, mismatched CPU lists, partial worker-start failure, and unsupported kTLS requests failed within bounded time. Two unpinned workers were also exercised.

Current mixed .NET/native Kestrel and managed-pressure sanitizer runs instrument the thin C shim with ASan/UBSan and disable leak detection. The original C-only pressure executable used leak detection before it was removed. No sanitizer diagnostic was reported. Exhaustive global pool exhaustion, arbitrary cancellation/fatal-engine races, production overload policy, and TLS versions outside the fixed prototype are not established.

Local evidence under sample `results/`:

| Evidence | Directory |
|---|---|
| Final 16-run matrix, environment/binary hashes, audited JSON | `epoll-final-matrix-20260928-225808` |
| Earlier 16-run screen | `epoll-matrix-20260928-225111` |
| Final native sanitizer and real Kestrel checks | `epoll-check-20260928-225743` |
| Final output modes | `final-send-check-20260928-225803` |
| Final rejected-accept checks | `rejected-accept-20260928-225807` |
| Worker configuration/failure checks | `epoll-config-20260928-225612` |
| First managed/native comparison and audited JSON | `epoll-managed-matrix-20260928-234344` |
| First managed-loop sanitizer/data/pressure checks | `epoll-check-20260928-234233` |
| Managed-loop rejected-accept checks plus io_uring controls | `rejected-accept-20260928-234246` |
| Managed-loop startup/failure configurations | `epoll-managed-config-20260928-234744` |
| Final managed-loop sanitizer, idle deadline, and constrained-send checks | `epoll-check-20260928-235140` |
| Final managed-loop output modes and rejected accepts | `final-send-check-20260928-235205`, `rejected-accept-20260928-235209` |

Raw output is gitignored. The source and this report preserve the implementation and main results without claiming the PR's broader features have been replicated.
