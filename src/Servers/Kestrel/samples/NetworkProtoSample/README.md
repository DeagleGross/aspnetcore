# NetworkProtoSample

Non-shipping Linux io_uring experiment. All work is intentionally uncommitted in the designated local checkout. No existing framework API or default is changed.

## Acceptance and scope

Acceptance: in-tree Release build; explicit stock sockets versus io_uring selection with no fallback; identical HTTPS endpoint, payload, TLS version/cipher and runtime; individual curl request plus repeated keep-alive and close requests; separate reproducible curl and wrk scripts; close/reuse verified from server connection IDs and per-connection request counts; one initial bounded throughput/latency comparison with raw outputs, errors, commands and environment metadata persisted. A build or a sockets-only pass is not completion.

The native shim uses liburing for accept, receive and asynchronous send. Accept supports the original demand-driven one-shot path, an eagerly rearmed one-shot queue, and multishot. Receive/send remain one-shot. One managed pump thread owns each listener's ring; an eventfd poll wakes it for submissions/cancellation. Buffers stay pinned and socket handles stay referenced until terminal CQEs. Partial sends are retried, incoming flushes and outgoing reads provide bounded pipeline backpressure, and cancellation drains terminal CQEs before releasing resources. No SQPOLL, multishot receive/send, registered files/buffers, kTLS or NIC offload are claimed.

Completions are drained in batches of up to 64. The default engine uses the simpler pump-owned action queue and pending-operation dictionary, while retaining reusable operation/completion state. Producers coalesce eventfd writes on the queue's empty-to-nonempty transition; the pump dequeues under the same lock to avoid lost wakeups. Generation-tagged slots, typed submission queues and direct SQ preparation remain opt-in experiments, described below. Small successful receives can resume transport progress on the pump; the pipe explicitly schedules Kestrel/application readers on the ThreadPool, outside the pump and its lock. A fatal ring/pump infrastructure failure terminates the prototype explicitly rather than releasing potentially live native buffers or silently falling back. This is not a production recovery policy.

**Current defaults are full-ring accept/receive/send, eager one-shot accept, and deferred kernel task work:** `NETWORKPROTO_ACCEPT_MODE=queued`, `NETWORKPROTO_DEFER_TASKRUN=1`, `NETWORKPROTO_DIRECT_SQ=0`. Direct receive was removed from the managed transport entirely; `NETWORKPROTO_SYNC_RECEIVE=1` now fails explicitly. Direct send/accept remain disabled historical experiments. The slot engine (`NETWORKPROTO_DIRECT_SQ=1`) requires `NETWORKPROTO_ACCEPT_MODE=oneshot`; its batching/combined-wait alternatives remain off. Multishot is available with `NETWORKPROTO_ACCEPT_MODE=multishot`, but eager one-shot was faster at higher close-mode concurrency. See the final section for measured rationale.

When HTTPS is selected, TLS is Kestrel's standard SslStream HTTPS middleware on **both** transports. io_uring moves encrypted bytes below it; it is not fd-bound OpenSSL readiness polling. SslStream owns handshake progress, WANT_READ/WANT_WRITE handling and TLS close behavior. There is no direct native TLS layer and no double TLS.

The current experiment source at `DeagleGross/Network-Transport-Experiments` revision `232e032b52b6d80b61c3398ef39b8c16b20d88a6` is a proposal/validation surface. This implementation retains a small internal `TransportApplication` receive/close boundary and explicit `UseIoUring` registration, not its extensive provider/options/policies. The DirectTLS design in `dotnet/aspnetcore#67912` (inspected head `c585e7fe504866ab00442f94dc47416ba5d42af9`) supplied architectural context only; no PR code was executed or imported.

API assessment: this is Linux server experiment infrastructure, not a demonstrated new general-purpose framework contract. Keep `TransportApplication`, the engine, factory and adapter internal. The only new public surface is the opt-in `IWebHostBuilder.UseIoUring()` extension in this non-shipping assembly, matching the existing sockets registration pattern. No shipping API baseline or shared-framework inclusion changes.

Deliberately excluded: production hardening, multi-platform support, HTTP/2 and HTTP/3, client transports, ClientHello callbacks, certificate negotiation policy and TLS offload. TLS session resumption must be reported as unverified unless independently measured; connection churn alone does not prove full certificate handshakes.

The user subsequently requested bounded profiling, repeated measurements, and a plaintext HTTP comparison. The initial comparison, failed intermediate optimization, corrected implementation, and observed limits are recorded in [RESULTS.md](RESULTS.md). This compares socket I/O backends underneath the same TLS layer, **not DirectTLS-style fd-bound native TLS or kTLS**.

## Build and run

Run in the designated Ubuntu checkout, `/home/deaglegross/code/aspnetcore`, branch `dmkorolev/network-proto`. Required tools: the repository SDK, a C compiler, liburing development files, OpenSSL, curl, wrk, Python 3, taskset and pidstat. Optional tracing uses the already-installed `dotnet-trace`. The prototype does not modify SDK/package configuration, install system services or request administrator privileges.

The first native build failed because liburing headers were absent. These exact Ubuntu 24.04 packages were then downloaded from the configured Ubuntu archive and extracted into ignored, checkout-local artifacts:

```bash
cd ~/code/aspnetcore
mkdir -p artifacts/networkproto-deps
(
    cd artifacts/networkproto-deps
    apt-get download liburing-dev=2.5-1build1 liburing2=2.5-1build1
    dpkg-deb --extract liburing-dev_2.5-1build1_amd64.deb .
    dpkg-deb --extract liburing2_2.5-1build1_amd64.deb .
)
```

The scripts detect that directory and set `CPATH`, `LIBRARY_PATH`, and `LD_LIBRARY_PATH` for their own processes. A normal system liburing installation also works without it. Downloaded package versions and SHA-256 digests are recorded in each measurement's `environment.txt`.

```bash
cd ~/code/aspnetcore
sample=src/Servers/Kestrel/samples/NetworkProtoSample
bash "$sample/scripts/build.sh" --restore
bash "$sample/scripts/run.sh" sockets
# Stop that server before starting the alternative:
bash "$sample/scripts/run.sh" io_uring
```

`build.sh` activates the repository SDK and invokes the Kestrel area build with an explicit sample project, Release configuration, and project dependencies. The native shim is compiled as part of the transport project. The transport is not added to the shipping shared framework or solution-wide packaging.

`run.sh` activates the same SDK, generates a disposable two-day RSA certificate under ignored `.certs/`, and runs the built DLL. It binds IPv4 loopback port 5443 by default; `PORT` overrides the port. Backend selection is exactly `--backend sockets` or `--backend io_uring`. An invalid backend, missing shim, unavailable kernel io_uring support, or mismatched actual connection backend fails explicitly. The sample calls `UseHttps` once on both paths, with HTTP/1.1, TLS 1.2 and `TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256`.

For a direct launch instead of the wrapper:

```bash
cd ~/code/aspnetcore
source activate.sh
export LD_LIBRARY_PATH="$PWD/artifacts/networkproto-deps/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
dotnet artifacts/bin/NetworkProtoSample/Release/net11.0/NetworkProtoSample.dll \
    --backend io_uring --port 5443 \
    --cert "$PWD/src/Servers/Kestrel/samples/NetworkProtoSample/.certs/cert.pem" \
    --key "$PWD/src/Servers/Kestrel/samples/NetworkProtoSample/.certs/key.pem"
```

The certificate must already have been generated by a wrapper. Never add `.certs/` to source control. Delete only its two PEM files and rerun a wrapper when the test certificate expires.

## Reproducible checks and measurements

Stop any manually launched sample first. The scripts start and stop their own servers sequentially, track exact PIDs, wait for HTTPS readiness and fail on unexpected server errors.

```bash
cd ~/code/aspnetcore
sample=src/Servers/Kestrel/samples/NetworkProtoSample
bash "$sample/scripts/curl-smoke.sh"
bash "$sample/scripts/rst-check.sh"
SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4 \
    THREADS=2 CONNECTIONS=16 WARMUP=10 DURATION=10 REPETITIONS=3 \
    bash "$sample/scripts/wrk-load.sh"
```

Choose affinity lists that exist on your machine and do not share SMT siblings. On the measured machine these lists select four and two distinct guest-reported physical cores. `taskset` does not reserve the host's CPUs. `SERVER_PROCESSORS=4` applies the same .NET processor budget to both backends; stock Kestrel retains its normal sockets scheduling, while the candidate has one additional dedicated ring pump.

`curl-smoke.sh` verifies the certificate with `--cacert`. It checks one independent request, three URLs in one connection-capable curl invocation for reuse, and three `Connection: close` URLs in one invocation. HTTP status, exact 1024-byte body, negotiated TLS, backend identity, connection ID, per-connection request number and curl's actual `num_connects` are asserted. Its close check disables client TLS session caching with `--no-sessionid`.

`wrk-load.sh` independently exercises both modes on both backends. Every response is checked by Lua, including backend, TLS, body, and close/reuse behavior. `/metrics` counts actual accepted connections, completed TLS handshakes and request reuse, with before/after snapshots. A metrics probe itself adds a connection. Requests in flight at wrk's deadline can explain small server/client request-count differences. The script also rejects large discrepancies between wrk's active request-rate samples and its full-interval throughput: zero completed-request errors alone did not catch a stalled intermediate implementation.

For HTTPS, wrk does not verify the disposable certificate. TLS session resumption during wrk is **unverified**; completed TLS handshakes and new TCP connections are not proof of full certificate handshakes. For HTTP, both backends have TLS disabled, the response reports `X-Tls: none`, and verification requires zero TLS handshakes. No offload measurement is involved.

The real-client `rst-check.sh` consumes an HTTPS response and then uses `SO_LINGER` to produce a TCP reset. Only native `ECONNRESET` is classified as a peer disconnect; it still propagates to the pipeline as `ConnectionResetException` and is logged with a connection ID and timestamp. Other failures remain errors. wrk can reset in-flight connections at its fixed-duration cutoff. The load script screens these explicit reset warnings by count and proximity to exit, but **timing is not proof of causation**. Deliberate-reset red/green evidence is preserved separately.

Each script accepts an optional output directory as its first argument. By default, all raw data is preserved under ignored `results/`: commands, headers/bodies, TLS traces, environment and binary hashes, server logs, metrics, exact affinity, and per-thread CPU/memory/context-switch data. `REPETITIONS` is capped at five and individual warmup/measurement intervals at 30 seconds. `BACKENDS` and `MODES` can restrict a diagnostic run, for example `MODES=keepalive CONNECTIONS=64`. `TRACE=1` additionally captures EventPipe sampled-thread-time traces and top-method reports; those perturbed rates are not benchmark results. The trace includes blocked-thread wall time, not just CPU execution.

```bash
python3 "$sample/scripts/summarize.py" "$sample"/results/wrk-YYYYMMDDTHHMMSSZ \
    > "$sample/results/summary.json"
```

The `/metrics` endpoint also reports process CPU time, allocated bytes, GC counts, working set and the runtime actually loaded. These counters are collected outside the timed response path. All results are local and all source changes remain uncommitted for review.

## Plaintext comparison and small-send optimization

The following iteration sections are historical. To reproduce their earlier scheduling, explicitly set `NETWORKPROTO_ACCEPT_MODE=oneshot NETWORKPROTO_DEFER_TASKRUN=0`. Historical direct-receive experiments require their archived source because that transport path has now been removed. Current configuration is summarized above and in the final section.

`--scheme http` selects plaintext HTTP/1.1; the default remains `--scheme https`. All run/curl/wrk wrappers accept `SCHEME=http`, skip certificate generation in that mode, and verify the actual scheme and TLS state. The application, 1024-byte payload and connection identity checks are otherwise shared. `rst-check.sh` specifically requires HTTPS.

The historical small-send experiment attempts a nonblocking native `send(MSG_DONTWAIT | MSG_NOSIGNAL)` for buffers smaller than 16 KiB. Success avoids an operation allocation, command-queue handoff, send SQE/CQE and task continuation. Partial writes retain the same output-buffer ownership and retry loop. EAGAIN falls back to the existing io_uring send; every other error remains explicit. The P/Invoke SafeHandle parameter keeps the socket alive, and the managed buffer is pinned for the duration of the synchronous syscall. Accept and receive remain real io_uring operations. This is deliberately a hybrid, not an all-SQE backend.

`NETWORKPROTO_SYNC_SEND=0` selects ring-only sends and is now the default; `1` enables the historical fast path. The environment switch is internal experiment configuration, not a shipping framework option. The optimization improved keep-alive throughput but regressed connection-close throughput in the measured workload; it is not an unconditional performance win. `NETWORKPROTO_DIAGNOSTICS=1` logs operation/wakeup/completion counters at listener shutdown. Leave diagnostics off for headline measurements and collect them in a separate run.

The installed wrk 4.1.0 uses realtime for request latency and throughput. During this follow-up WSL's realtime clock stepped backward by about 1.85 seconds: initial 10-second stock runs reported only 8.2 seconds and nonzero timeout counts. Those runs were rejected, not forgiven by the verifier. `scripts/build-wrk.sh` builds upstream wrk 4.1.0 commit `7594a95186ebdfa7cb35477a8a811f84e2a31b62` under ignored `artifacts/networkproto-wrk`, applying the small checked-in `wrk-monotonic.patch` to duration, latency and event-loop clocks. It retains the bundled LuaJIT and uses system OpenSSL. Archive, patch and executable hashes are recorded. Upstream license notices remain in the extracted source; no machine clock, SDK or package configuration is changed.

```bash
cd ~/code/aspnetcore
sample=src/Servers/Kestrel/samples/NetworkProtoSample
bash "$sample/scripts/build.sh"
bash "$sample/scripts/build-wrk.sh"
SCHEME=http bash "$sample/scripts/curl-smoke.sh"
bash "$sample/scripts/send-pressure.sh"
SCHEME=http WRK="$PWD/artifacts/networkproto-wrk/wrk" \
    SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4 \
    THREADS=2 CONNECTIONS=64 WARMUP=10 DURATION=10 REPETITIONS=3 \
    MODES=keepalive NETWORKPROTO_SYNC_SEND=1 NETWORKPROTO_SYNC_ACCEPT=0 NETWORKPROTO_SHUTDOWN_CLOSE=0 \
    NETWORKPROTO_DIRECT_SQ=0 NETWORKPROTO_REUSE_OPERATIONS=0 NETWORKPROTO_INLINE_RECEIVE=0 NETWORKPROTO_COMPACT_CONNECTIONS=0 \
    bash "$sample/scripts/wrk-load.sh"
```

Use `NETWORKPROTO_SYNC_SEND=0` for a same-binary control, and omit `MODES=keepalive` to include close mode. The measured follow-up additionally interleaved all three variants and reversed their order in the second round; exact commands and ordering are in `RESULTS.md`. The wrk verifier compares reported runtime with an independent monotonic duration and checks it against the requested interval, in addition to the existing response/error/liveness checks.

`send-pressure.sh` exercises real native partial writes, EAGAIN, recovery and explicit EPIPE on a socket pair, then validates 8192 pipelined HTTP responses through each real Kestrel backend while temporarily withholding client reads. It requires actual io_uring fallback in the shutdown diagnostics, rather than assuming a slow-reader delay reached that path. The observed custom run exercised both partial direct send and EAGAIN-to-SQE fallback without corruption or unexpected errors.

## Short-lived connection follow-up

The subsequent short-lived comparison retained the full Kestrel HTTP path and tried two independently selectable fast paths:

| Switch | Current default | Behavior |
|---|---|---|
| `NETWORKPROTO_SYNC_ACCEPT` | `0` | Opt in to nonblocking `accept4` before submitting an accept SQE. Only the listener is set nonblocking; accepted sockets retain the same flags as before. EAGAIN falls back to io_uring. |
| `NETWORKPROTO_SYNC_RECEIVE` | `0` | Former direct-receive experiment, now removed. `1` is rejected. Its native helper remains only for the standalone instructional syscall probe. |

Direct accept was retained at that stage because repeated close runs nearly doubled throughput and keep-alive checks did not show a regression; it is now disabled by default to evaluate full-ring I/O. Direct receive remains opt-in: it helped close mode further but reduced keep-alive throughput compared with direct accept alone. All errors other than would-block remain explicit, and SafeHandle/buffer lifetime is maintained across each native call.

To reproduce the earlier send-only implementation, set both new switches and `NETWORKPROTO_SHUTDOWN_CLOSE` to `0`, and disable the three allocation-follow-up switches described below. To reproduce the original all-ring implementation, also set `NETWORKPROTO_SYNC_SEND=0`. The fastest close configuration at this stage used both accept/receive switches at `1`; it still did not beat the matched stock sockets control.

```bash
SCHEME=http WRK="$PWD/artifacts/networkproto-wrk/wrk" \
    SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4 \
    THREADS=2 CONNECTIONS=16 WARMUP=10 DURATION=10 REPETITIONS=3 \
    MODES=close NETWORKPROTO_SYNC_SEND=1 NETWORKPROTO_SYNC_ACCEPT=1 NETWORKPROTO_SYNC_RECEIVE=1 NETWORKPROTO_SHUTDOWN_CLOSE=0 \
    NETWORKPROTO_DIRECT_SQ=0 NETWORKPROTO_REUSE_OPERATIONS=0 NETWORKPROTO_INLINE_RECEIVE=0 NETWORKPROTO_COMPACT_CONNECTIONS=0 \
    bash "$sample/scripts/wrk-load.sh"
```

`send-pressure.sh` also checks the historical native accept/receive boundaries: empty-backlog EAGAIN, a queued TCP accept, close-on-exec ownership, data, receive EAGAIN and EOF. The direct-receive Kestrel variant now requires the archived source; the current transport always submits a receive SQE. Details and the alternating comparison order are recorded in the short-lived section of `RESULTS.md`; evidence lives under `results/short-lived/`.

## Graceful teardown follow-up

`NETWORKPROTO_SHUTDOWN_CLOSE=1` is now the default. When Kestrel's output completes normally and all writes have finished, the transport calls `Socket.Shutdown(Both)` and unblocks pipe backpressure. This sends the TCP close without waiting for an asynchronous receive-cancellation round trip. A pending receive finishes through its normal terminal CQE; only that completion releases its buffer pin and fd reference. The socket itself remains owned until both loops have finished. Abort, cancelled output and error paths still use explicit cancellation; shutdown errors remain logged as errors.

`NETWORKPROTO_SHUTDOWN_CLOSE=0` restores the earlier cancellation-first behavior for controlled comparison. The existing send/accept/receive/shutdown switches are independent. Their current defaults are direct send/accept/receive off and graceful shutdown on; the measurements in this historical section used direct send/accept on. Additional allocation/continuation switches are documented below. This is not native TLS or kTLS; HTTPS still uses Kestrel's SslStream middleware.

```bash
SCHEME=http WRK="$PWD/artifacts/networkproto-wrk/wrk" \
    SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4 \
    THREADS=2 CONNECTIONS=16 WARMUP=10 DURATION=10 REPETITIONS=3 MODES=close \
    NETWORKPROTO_SHUTDOWN_CLOSE=1 NETWORKPROTO_SYNC_SEND=1 NETWORKPROTO_SYNC_ACCEPT=1 NETWORKPROTO_SYNC_RECEIVE=0 \
    NETWORKPROTO_DIRECT_SQ=0 NETWORKPROTO_REUSE_OPERATIONS=0 NETWORKPROTO_INLINE_RECEIVE=0 NETWORKPROTO_COMPACT_CONNECTIONS=0 \
    bash "$sample/scripts/wrk-load.sh"
bash "$sample/scripts/close-eof.sh"
```

`close-eof.sh` verifies 256 complete HTTP responses on distinct connections for each backend and waits for server-initiated EOF without sending a client FIN. With graceful shutdown enabled it also checks that native cancellation requests are limited to listener shutdown, not one per normal connection. The control run produced 257 cancellation requests; the new run produced one. See `results/teardown/` and the graceful-teardown `RESULTS.md` section for this evidence, repeated short-lived wins and the keep-alive check.

## Allocation and latency follow-up

Acceptance for this iteration: reduce measured server allocation and request latency relative to the preceding prototype, retain the full Kestrel HTTP path and its pipe backpressure, preserve completion/cancellation ownership, exercise real partial-I/O and disconnect paths, and repeat same-binary comparisons with stock and the previous implementation. This is a bounded experiment, not a requirement to beat every stock latency percentile. The three switches default to `1`; set all three and `NETWORKPROTO_DIRECT_SQ` to `0` for the previous implementation. To reproduce this historical hybrid comparison, explicitly enable `NETWORKPROTO_SYNC_SEND=1` and `NETWORKPROTO_SYNC_ACCEPT=1`.

| Switch | Behavior and source |
|---|---|
| `NETWORKPROTO_REUSE_OPERATIONS` | Lazily reuse one versioned `IValueTaskSource<int>` per listener accept and per connection receive/send direction. `Transport.Networking/src/IoUring/IoUringEngine.ReusableOperation.cs` replaces per-I/O operation/task/captured-callback allocation; the old implementation remains in `IoUringEngine.cs` as a control. |
| `NETWORKPROTO_INLINE_RECEIVE` | Allow a positive receive of less than 4096 bytes to resume transport progress on the completion pump. EOF, errors, larger receives, accept and send completions remain asynchronous. Requires reuse enabled; set inline to `0` when disabling reuse. Kestrel readers remain explicitly ThreadPool-scheduled in `TransportApplication.cs`. |
| `NETWORKPROTO_COMPACT_CONNECTIONS` | Share immutable `PipeOptions` in `TransportApplication.cs` and avoid an unnecessary linked cancellation source when the caller supplies a non-cancellable accept token in `IoUringTransportFactory.cs`. Real caller cancellation still links with listener shutdown. |

Reusable state is not a global connection pool. Each submission still takes a SafeHandle reference and pins its own buffer until its terminal CQE. Every submission gets a fresh engine ID, even when its managed completion state is reused. Terminal completion disposes the old cancellation registration before allowing another generation to start; queued cancellation captures the old ID, not mutable reusable state. `GetResult` consumes the versioned result before the next receive/send can reuse it. Cancellation acknowledgment alone never releases the original I/O's memory.

The retained tradeoff is fewer allocations and improved tail/low-concurrency latency, not maximum throughput in every workload. Across three interleaved runs, 64-connection keep-alive allocation fell from approximately 2016 to 1632 bytes/request and median p99 from 1420 to 1010 microseconds; throughput median fell 2.4%. Close-mode allocation fell from approximately 14591 to 13629 bytes/request and p50 from 271 to 243 microseconds. Stock still had lower 64-connection p50 and lower close-mode mean/p99 latency. See `RESULTS.md` for all controls, ranges and qualifications rather than extrapolating the small stock differences.

The same build also passed real HTTP/HTTPS individual/reuse/close checks, pressured partial-send/fallback, server-initiated EOF and deliberate TLS-client reset checks. A separate all-ring load check disables direct accept/send and exercises repeated reuse in both receive and send directions. These scripts and their educational probes remain under `scripts/`; no standalone investigation application was removed. Raw evidence, source snapshots and binary hashes persist under `results/allocations/`. Production-scale cancellation fault injection and fairness across mixed large/small requests remain unverified.

**Unresolved supplementary check:** the final HTTPS wrk close-mode run was rejected because native peer-reset warnings occurred during traffic, outside the strict end-of-run window. Same-binary checks reproduced this with the previous, reuse-only and inline completion paths. This is not an accepted TLS performance comparison or a demonstrated new allocation-change regression. HTTP measurements and verified HTTPS curl responses remain valid, but the HTTPS wrk-close warning cause is not established; the verifier and logging were not weakened. Details and failed evidence are in the supplementary HTTPS section of `RESULTS.md`.

## Full-ring submission and completion experiment

Acceptance for this iteration: remove direct syscall bypasses from defaults and timed comparisons; replace generic command/dictionary bookkeeping with typed operations and generation-tagged completion slots; implement and measure caller-thread SQ preparation with SQ-full overflow; retain pin/fd ownership until terminal completion; force actual overflow, partial sends, slot reuse/growth and cancellation; compare matched stock, old full-ring and candidate runs. A performance win is not an acceptance assumption.

`IoUringEngine.DirectSubmission.cs` owns a reusable slot table. A submission places `(generation << 32) | (slotIndex + 2)` in SQE `user_data`; CQEs identify their operation without a dictionary lookup. IDs 0 and 1 are reserved for wake and cancellation acknowledgment. A slot is made inactive before signaling its continuation, but cannot be reused until its consumer calls `GetResult`. Connection disposal releases slots only after both I/O loops finish. Generation validation rejects stale completions/cancellations; the versioned `ValueTask` still enforces single consumption. There is no multishot receive, buffer selection, buffer registration or multiple messages inside a CQE: these remain one-shot operations, with up to 64 CQEs returned per collection call.

| Configuration | Submission mechanism |
|---|---|
| `NETWORKPROTO_DIRECT_SQ=1 NETWORKPROTO_STAGE_ON_PUMP=1` | Cross-thread callers enqueue `ReusableOperation` objects, not delegates. The pump dequeues and prepares SQEs outside the producer lock; its own receive continuations prepare directly or use the SQ-full overflow queue. Kernel submission is outside the producer lock. |
| `NETWORKPROTO_DIRECT_SQ=1 NETWORKPROTO_STAGE_ON_PUMP=0` | Any caller prepares an SQE under the shared lock, queueing only if the SQ is full or older overflow work is waiting. The pump submits under that lock and consumes CQEs outside it. This is the requested direct-preparation experiment, not the recommended configuration. |
| `NETWORKPROTO_DIRECT_SQ=0` (default) | Simpler action queue and pending-operation dictionary. Reusable operations still avoid per-I/O operation/task/delegate allocations. `NETWORKPROTO_STAGE_ON_PUMP` is ignored. |

The native boundary is `np_stage` (prepare without submitting), `np_flush` (submit staged work and rearm the wake poll), and `np_collect` (CQ drain/wait without modifying the SQ). Producer/wait transitions are synchronized so no queued work loses its wake. Cancellation IDs are queued separately and issued only after older unsubmitted operations. Original terminal CQEs, not cancel acknowledgments, own buffer release. `NETWORKPROTO_RING_ENTRIES` accepts powers of two from 2 to 4096, default 1024. A tiny ring is a stress setting, not a throughput recommendation.

The direct-preparation experiment was substantially slower. The typed-queue/slot variant recovered previous full-ring throughput, but did **not** establish a latency or throughput improvement. It reduced close-mode allocation by about 104 bytes/request, partly by not creating the legacy submission delegates; keep-alive allocation was unchanged. Stock remained faster overall. Exact repeated numbers and rejected-as-default experiments are preserved in the full-ring submission section of `RESULTS.md`. The subsequent pump-alternatives section explains why the simpler default was restored.

```bash
sample=src/Servers/Kestrel/samples/NetworkProtoSample
bash "$sample/scripts/build.sh"
NETWORKPROTO_DIRECT_SQ=1 NETWORKPROTO_STAGE_ON_PUMP=0 bash "$sample/scripts/ring-pressure.sh"
NETWORKPROTO_DIRECT_SQ=1 NETWORKPROTO_STAGE_ON_PUMP=1 bash "$sample/scripts/ring-pressure.sh"
SCHEME=http bash "$sample/scripts/curl-smoke.sh"
SCHEME=https bash "$sample/scripts/curl-smoke.sh"
bash "$sample/scripts/close-eof.sh"
bash "$sample/scripts/rst-check.sh"
```

`ring-pressure.sh` forces a two-entry SQ and zero direct-I/O shortcuts. It verifies native SQ-full/retry, exact receive-buffer identity, original receive cancellation plus acknowledgment, and a genuinely partial io_uring send. Through real Kestrel it checks 8192 pipelined responses after withholding client reads, 512 synchronized close connections, correct bodies/EOF and bounded fd count. All modes require no pending operations, queued actions or cancellation acknowledgments at shutdown. With `NETWORKPROTO_DIRECT_SQ=1`, it additionally requires nonzero managed overflow, generation/slot reuse, and no remaining active/occupied slots. The prior `send-pressure.sh` remains a historical direct-syscall/fallback probe; use the new ring-pressure script for the full-ring path. Neither script is exhaustive race/fault injection.

For a slot-array growth check, run `wrk-load.sh` with `SCHEME=http BACKENDS=io_uring MODES=keepalive CONNECTIONS=256 NETWORKPROTO_DIRECT_SQ=1 NETWORKPROTO_DIAGNOSTICS=1`; inspect shutdown diagnostics for `slotCapacity > 256`, zero active/occupied slots and zero direct-attempt counters. Both slot-engine strategies reached 513 slots in the recorded checks. Keep diagnostics off for timed comparisons. TLS curl passes do not resolve the previously rejected HTTPS wrk-close warning pattern; the new performance matrix is plaintext only.

## Pump alternatives and simplicity decision

The next bounded experiment compared two mechanisms independently and together, while retaining the same Kestrel pipes, endpoint, one-shot operations and full-ring data path. Acceptance: correct native and real-server lifecycles, same-binary controls, repeated matched measurements with errors rejected, and no default promotion without a repeatable benefit.

| Opt-in switch | Mechanism |
|---|---|
| `NETWORKPROTO_BATCH_SUBMISSIONS=1` | Swap the shared submission queue with an empty pump-local queue once per iteration under the producer lock. Stage that finite snapshot outside the lock instead of reacquiring the lock for every dequeue. Both queues are reused. |
| `NETWORKPROTO_COMBINED_WAIT=1` | Use `np_submit_and_collect`, which rearms wake polling and calls `io_uring_submit_and_wait` before draining CQEs. Wait for one CQE only when there is no queued backlog and none already available. This follows the combined submission/wait pattern observed in Tokio, libxev and Reuben's engine. |

Both switches default to `0` and require `NETWORKPROTO_DIRECT_SQ=1 NETWORKPROTO_STAGE_ON_PUMP=1`. Invalid combinations throw before ring creation; they do not silently select another path. Combined waiting cannot safely run while another thread is preparing SQEs without synchronization, so it is not available on the caller-thread direct-SQ experiment.

Neither alternative produced a repeatable throughput/CPU/latency improvement in three matched rounds. Allocation remained unchanged. Both together ranged from a 10.2% keep-alive regression to a 3.4% improvement relative to the preceding typed-queue path; host throughput drift makes a favorable aggregate median misleading. See the final `RESULTS.md` section for per-round comparisons rather than treating these numbers as a new speedup.

**The default is restored to the simpler action-queue/dictionary engine (`NETWORKPROTO_DIRECT_SQ=0`).** The extra slot machinery saves about 0.8% of close-mode allocation, but does not have a repeatable throughput or material latency advantage. A separate three-round confirmation of the restored default found essentially equal throughput and about 107 bytes/request additional allocation on close, with no keep-alive allocation difference. Reusable completion state, bounded receive inlining and graceful shutdown remain enabled; the meaningful earlier per-I/O allocation reduction is not reverted. This simplifies the active path, not the total experimental source: all alternatives remain selectable for instructional use.

Build once, then reproduce the five-variant matrix without overlapping measurements:

```bash
cd ~/code/aspnetcore
sample=src/Servers/Kestrel/samples/NetworkProtoSample
bash "$sample/scripts/build.sh"
SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4 \
    WRK="$PWD/artifacts/networkproto-wrk/wrk" \
    bash "$sample/scripts/compare-pumps.sh"
```

The script runs stock sockets, the preceding typed queue, batched queue handoff, combined submit/wait, and both changes. It forces plaintext/full-ring settings, uses three reordered rounds with separate ten-second warmup and measurement, and exercises keep-alive/64 and close/16. `ROUNDS` may be 1-3; `WARMUP`/`DURATION` retain the load script's bounds. Each result directory and its log must not already exist. Its `previous` label refers to the preceding typed-queue/slot engine, not the now-restored simpler default.

```bash
bash "$sample/scripts/ring-pressure.sh"
NETWORKPROTO_DIRECT_SQ=1 NETWORKPROTO_BATCH_SUBMISSIONS=1 \
    NETWORKPROTO_COMBINED_WAIT=1 bash "$sample/scripts/ring-pressure.sh"
```

The native pressure probe also exercises the combined submit/collect boundary when enabled, with no separate flush beforehand. Managed diagnostics prove queue batches larger than one and actual SQ overflow rather than assuming the selected flag was exercised. All alternatives passed HTTPS curl; the default additionally passed HTTP/HTTPS curl, server-initiated EOF and intentional TLS-client reset checks. These do not resolve the earlier strict HTTPS wrk-close warning failure. Exact sources, build logs, raw runs and confirmation results persist under `results/pump-alternatives/`, with all implementation left uncommitted.

## Full-ring receive, accept pipelines and multishot

Acceptance for this iteration: remove the managed direct-receive path, rerun matched stock/full-ring performance before further changes, implement real multishot with `CQE_F_MORE` and terminal ownership, compare against an eagerly rearmed one-shot control, exercise bounded queue pause/resume and cancellation, and continue only through bounded, measured follow-ups. Improvement must not depend on bypassing io_uring or skipping Kestrel HTTP.

`ReceiveAsync` in `IoUringEngine.cs` now only submits receive operations. Its direct native import and syscall branch are gone. `NETWORKPROTO_INLINE_RECEIVE=1` still allows a small successful receive's managed continuation to run on the pump **after its CQE**; it is not a direct `recv` or an alternative I/O path. The `np_try_recv` function remains solely for the old standalone native probe and is not imported by the transport.

### Accept modes

| `NETWORKPROTO_ACCEPT_MODE` | Behavior |
|---|---|
| `oneshot` | Original demand-driven accept: Kestrel awaits a reusable operation, constructs the connection, and requests the next accept. Useful as the historical control. |
| `queued` (default) | Pump-owned `AcceptStream` rearms one-shot accept independently of Kestrel consuming the accepted socket. A bounded channel transfers accepted handles to the listener. |
| `multishot` | Same channel/ownership as `queued`, but `io_uring_prep_multishot_accept` leaves one SQE active across many successful CQEs. The pending dictionary retains the operation while `CQE_F_MORE` is set. |

These are all real io_uring accepts. Queued modes require `NETWORKPROTO_DIRECT_SQ=0 NETWORKPROTO_SYNC_ACCEPT=0`; invalid combinations fail rather than falling back. `IoUringEngine.AcceptStream.cs` owns the accept lifecycle; the listener converts each delivered `SafeSocketHandle` to its normal `Socket`/`IoUringConnection`. HTTP parsing, endpoint execution, TLS and the receive/send pipeline remain unchanged.

There is one accepted fd per CQE, not several connections inside one CQE. `native.c:np_wait` now returns CQE flags as well as IDs/results. A nonterminal multishot CQE must not remove the pending entry or release the listening socket reference. Only the terminal CQE does that. Each rearm gets a fresh ID so an older cancellation cannot target a later submission. The native probe demonstrates eight accepted sockets from one SQE, then original-operation terminal cancellation plus its separate acknowledgment, followed by a new-generation rearm.

The channel has a hard capacity of 1024 accepted handles. At `NETWORKPROTO_ACCEPT_PAUSE_AT` (default 256, range 1-512), the pump pauses one-shot rearming or cancels multishot; after the terminal CQE and consumption down to half the threshold, it rearms. Headroom allows already-produced CQEs to arrive after cancellation was requested. Terminal shutdown closes queued/unclaimed handles and drains native ownership. If that headroom is exhausted, the prototype explicitly logs an error and fails the listener while closing unclaimed descriptors; it does not silently drop traffic or promise unlimited overload handling.

### Deferred kernel task work

`NETWORKPROTO_DEFER_TASKRUN=1` is the new default, based on repeated lower CPU/request and improved throughput. The ring is created with `R_DISABLED | SINGLE_ISSUER | DEFER_TASKRUN | TASKRUN_FLAG`, then enabled **on the pump thread**, which becomes its single kernel issuer. Completion work is driven when that thread enters io_uring to collect/wait for events. This is not SQPOLL, userspace busy waiting, an extra kernel polling thread, or elimination of all syscalls. `0` preserves the prior ring setup for a same-binary control.

This mode requires Linux 6.1 or newer and was exercised on the recorded WSL 6.18 kernel with liburing 2.5. Setup/enable failures are explicit; there is no fallback to sockets or to another ring mode. The ownership requirements follow the [io_uring_setup manual](https://man7.org/linux/man-pages/man2/io_uring_setup.2.html). Multishot accept itself requires kernel support (introduced in Linux 5.19); unsupported operations also fail explicitly.

### What improved, and what did not

The first three-round accept comparison raised close/16 throughput from about 16k requests/s on demand-driven one-shot to 30.8k on eager one-shot and 31k on multishot. The shared eager accept pipeline explains most of that improvement; multishot was not consistently faster than the eager one-shot control. At close/64 with four client cores, eager one-shot consistently beat multishot. Deferred task work then improved both modes across all three matched rounds, with lower CPU/request. Therefore the default is **eager one-shot plus deferred task work**, with multishot kept selectable and fully exercised.

Keep-alive still trails stock throughput in the final matched workload. The single pump uses roughly a full core on that workload; this is evidence of the present implementation's limit, not io_uring's universal ceiling. The accept modes change little once persistent connections are established. Multishot receive would require a provided-buffer pool and new data/backpressure ownership; it was not implemented in this iteration. Ordinary send remains one-shot; no feature that automatically sends arbitrary future application buffers is claimed. Exact rates, latency, ranges, controls and source snapshots are in the final `RESULTS.md` section.

### Reproduction

```bash
cd ~/code/aspnetcore
sample=src/Servers/Kestrel/samples/NetworkProtoSample
bash "$sample/scripts/build.sh"
SCHEME=http bash "$sample/scripts/curl-smoke.sh"
SCHEME=https bash "$sample/scripts/curl-smoke.sh"
NETWORKPROTO_ACCEPT_MODE=multishot SCHEME=https bash "$sample/scripts/curl-smoke.sh"
bash "$sample/scripts/accept-pressure.sh"
```

`accept-pressure.sh` tests eager one-shot and multishot with the pause threshold forced to one. It requires observed pause/resume, terminal CQEs for every accept submission, zero outstanding operations/cancel acknowledgments/queued handles, zero queue overflow and zero direct-I/O attempts. It also runs the native multishot probe and real Kestrel slow-reader/512-connection burst checks. The original `ring-pressure.sh` selects `oneshot` accept by default to isolate tiny-SQ read/write/slot experiments; `accept-pressure.sh` covers the new accept modes.

```bash
SERVER_CPUS=0,2,4,6 CLIENT_CPUS=8,10 SERVER_PROCESSORS=4 \
    WRK="$PWD/artifacts/networkproto-wrk/wrk" \
    bash "$sample/scripts/compare-accept.sh"
SERVER_CPUS=0,2,4,6 KEEPALIVE_CLIENT_CPUS=8,10 CLOSE_CLIENT_CPUS=8,10,12,14 \
    SERVER_PROCESSORS=4 WRK="$PWD/artifacts/networkproto-wrk/wrk" \
    bash "$sample/scripts/compare-taskrun.sh"
```

The first wrapper compares stock/original one-shot/eager one-shot/multishot at keep-alive/64 and close/16, with deferred task work disabled unless explicitly enabled. The second isolates deferred task work at keep-alive/64 with two client threads and close/64 with four client threads. Both use three reordered rounds by default, ten-second warmup/measurement, strict existing gates, and refuse to overwrite existing run directories. They use wrk, not wrk2. All throughput tables concern plaintext Kestrel HTTP over TCP. HTTPS curl functionality is verified, but the earlier strict HTTPS wrk-close warning issue remains unresolved; no new TLS performance win is claimed.
