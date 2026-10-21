# Shared network API: scenarios, providers and results

**The functional prototype is complete for epoll, io_uring, IOCP and RIO. The full Windows +/-5% performance target remains unverified.** This is non-shipping experimental `System.Net.Transport`, not an approved BCL addition. It demonstrates reusable server/protocol-library infrastructure; Orleans itself was not integrated.

## Chosen API

`Primitives\src` holds framework-neutral contracts, owned buffers and the shared connection algorithm. `src\Shared` holds the engine factory, native-provider drivers and Kestrel adaptation. Select `EpollEngineOptions` / `IoUringEngineOptions` on Linux or `IocpEngineOptions` / `RioEngineOptions` on Windows.

```csharp
await using var engine = NetworkEngineFactory.Create(
    new EpollEngineOptions
    {
        WorkerCount = 4,
        WorkerProcessors = [0, 2, 4, 6]
    });
await using var listener = await engine.ListenAsync(
    new IPEndPoint(IPAddress.Loopback, 5000));
await using var connection = await listener.AcceptAsync()
    ?? throw new IOException("Listener closed.");
var receive = await connection.ReceiveAsync();
using var message = receive.Retain(0, receive.Buffer.Length);
connection.AdvanceReceiveTo(receive.Buffer.End);
connection.EnqueueSend(message); // Takes independent ownership; no payload copy.
```

| Surface | Contract |
|---|---|
| `NetworkEngineFactory.Create(options)` | Explicit provider selection, worker count and optional processor IDs; unsupported OS/provider fails, not falls back. Workers currently start per bound listener, not as one global engine-wide pool. |
| `NetworkBuffer` / `NetworkBufferSlice` | Writable owned storage and a borrowed read-only slice. Copying a slice does not retain it; `Retain()` creates independent disposable ownership. |
| `NetworkPayload` | An owned multi-page `ReadOnlySequence<byte>`; retention adds metadata/references without linearizing the bytes. |
| `ReceiveAsync` / `AdvanceReceiveTo` | Borrowed snapshot of unconsumed bytes, with consumed/examined cursors and EOF/cancellation flags. TCP receives are not message boundaries. |
| `SendAsync` / `EnqueueSend` | Borrowed memory/sequence completes after terminal native ownership; owned slice/payload enqueue retains before returning. |
| `AsDuplexPipe` / `AsStream` | Copy-free provider-page input for Kestrel; stream reads copy into caller buffers. Both share worker-drained output. |

**Ownership rules:** retain before advancing; do not mutate queued storage; release retained messages before awaiting connection disposal, which waits for their leases. Receive pages pause native progress at the provider's four-page bound. Direct sends have a default 1 MiB admission budget and fail explicitly when exceeded. Cancelling an admitted direct send aborts the connection and completes with the original token only after native ownership ends.

Pipe output and `SendAsync`/`EnqueueSend` are **alternative modes**, not concurrent writers. Memory kinds (`Managed`, `Pinned`, `Native`, `Registered`) describe storage; the provider validates actual address/registration eligibility. None means kernel/network zero-copy.

## Application scenarios

| Scenario | How to use it | Demonstrated boundary / limitation |
|---|---|---|
| Plain TCP protocol | `ReceiveAsync`, parse frames, advance cursors, borrowed or owned sends | Real transfers, multi-page retention, half-close and cancellation passed on all four providers. Application framing remains yours. |
| Retain an Orleans-style message | Decode framing from the sequence, retain its payload range, advance input and enqueue later | `RetainedPages` re-encodes a small header and sends the original 24 KiB payload from the same two native page addresses on all four providers, even after 128 KiB later traffic. No Orleans integration, zero-allocation or kernel zero-copy claim. |
| Peek before native TLS | Set `NativeTls.HandshakePrefixCallback` | Borrowed initial raw TLS prefix, before OpenSSL/SChannel processes it; may be fragmented, no consumption or parsed ClientHello/SNI. `PeekTlsHttp1` now uses this path without a server `SslStream`. |
| Peek before stream TLS | Inspect `receive.Buffer`, then advance **consumed = Start**, **examined = End** | Compatibility alternative: hand examined bytes to `NetworkTlsExtensions` / `SslStream`. Separate from native TLS. |
| Consume a raw prefix, then TLS | Consume only the parsed prefix; leave any prefetched TLS bytes; authenticate | A real client sent a raw prefix plus ClientHello. TLS plaintext, replies, `close_notify` and native drain passed on all four providers. |
| STARTTLS-style server greeting | Write the greeting through `connection.Output`, then hand off to TLS | Composes with pipe mode; the complete greeting/negotiation protocol was not separately exercised. Direct `SendAsync` before TLS would select the incompatible direct-send mode. |
| Ordinary TLS / Kestrel HTTPS | Install `SslStream` directly, or use Kestrel `UseHttps` above the TCP adapter | Explicit wrapper path: OpenSSL through `SslStream` on Linux, SChannel on Windows. Distinct from transport-owned `NativeTls`. |
| Transport-owned TLS | Set `NetworkListenOptions.NativeTls` when binding | Linux epoll: fd-bound OpenSSL; Linux io_uring: ring-owned ciphertext through a custom BIO by default (`NETWORKPROTO_URING_TLS=fd`, or `NETWORKPROTO_KTLS=1`, selects fd-bound OpenSSL); both TLS 1.2. Windows: worker-owned SChannel (TLS 1.3). Authenticated plaintext is published. No server `SslStream` or arbitrary pre-TLS upgrade. |

### Peeking before native TLS

Set `NetworkNativeTlsOptions.HandshakePrefixCallback = bytes => ...` together with the certificate/key paths. The observer runs once on the owning I/O worker and can inspect only its borrowed span during the callback. Linux epoll and fd-bound io_uring use `MSG_PEEK` before OpenSSL; ring-owned io_uring observes the first received ciphertext page, still before the first handshake call; Windows uses ciphertext already received into provider storage before SSPI. The hook is opt-in/null by default, does not consume input, and does not provide async certificate selection or arbitrary raw-prefix upgrades. `samples\PeekTlsHttp1` demonstrates the complete real-client HTTPS exchange.

### Peeking or consuming a prefix before stream TLS

Here `prefixLength` is **zero for a peek**, or the length of a complete, application-validated prefix to remove. Accumulate fragmented input before choosing it.

```csharp
var received = await connection.ReceiveAsync(cancellationToken);
var prefixLength = 0; // Inspect received.Buffer while its snapshot is active.
connection.AdvanceReceiveTo(
    received.Buffer.GetPosition(prefixLength), received.Buffer.End);

using var tls = await connection.AuthenticateAsServerAsync(
    new SslServerAuthenticationOptions
    {
        ServerCertificate = certificate,
        AllowTlsResume = false
    }, cancellationToken);
await tls.WriteAsync("plaintext reply"u8.ToArray(), cancellationToken);
await tls.ShutdownAsync();
```

The helper forwards ordinary `SslServerAuthenticationOptions`, including certificate callbacks. It installs a non-owning stream view; dispose TLS before the underlying connection. After handoff, use **only the returned TLS stream** for application I/O: the original connection still carries ciphertext, and `IsTls` does not change to represent this wrapper. Do not concurrently read raw bytes or install a second TLS wrapper. Stream TLS is not a zero-copy plaintext-page API, and its throughput was not shown to match fd-bound TLS.

### Allocating sendable memory / adapting Kestrel

This is a separate plain-connection example using direct-send mode, not a continuation of the TLS-stream example.

```csharp
using (var buffer = connection.AllocateBuffer(1024))
{
    buffer.Memory.Span[..1024].Fill(0x7e);
    connection.EnqueueSend(buffer.Slice.Slice(0, 1024));
}
```

On RIO this allocation is registered; on IOCP it is native. Linux output currently uses managed pooled memory pinned for sends. RIO stages ordinary unregistered borrowed arrays, but sends valid registered slices directly.

Kestrel uses `builder.WebHost.UseNetworkTransport(options)` and the same `AsDuplexPipe()` connection, not a separate HTTP implementation. Pass native TLS options for transport-owned TLS on Linux **or Windows**, without calling `UseHttps` again. `UseHttps` and `NetworkTlsExtensions` remain explicitly separate wrapper/compatibility paths, not how native TLS is established. Registration is host-wide in this prototype.

## How providers onboard

| Provider | Keeps its native machinery | Shares |
|---|---|---|
| epoll | Readiness, `recv`/send retries, fd OpenSSL, page IDs | Refcounted pinned pages; common receive/cursors/output; return callback marshals page ID to owner. |
| io_uring | SQ/CQ, multishot/provided pages, cancellation acknowledgements, linked FIN | Same core; page return and output work use ring-worker commands. |
| IOCP | `AcceptEx`, `OVERLAPPED`, local synchronous receipts, completion ports; optional SChannel SSPI | Native leases/output, in-place decrypted receive slices, worker-owned encryption and bounded inline send progress. |
| RIO | Buffer registration, RQ/CQ, notification rearming; optional SChannel SSPI | Registered ciphertext storage and decrypted leases; native TLS encrypts into reusable registered record buffers. |

The common flow is **native receive -> shared refcounted reader -> application -> shared send progression -> native terminal completion -> memory release**. Drivers implement scheduling, submission, close/abort and page returns. `SubmitSendCore` borrows the core's existing pin; providers aggregate partial progress and report completion only when native ownership ends. Readiness, multishot CQEs and RIO notifications stay provider-specific rather than being forced into one universal event loop.

Reference-type ownership avoids copyable release handles; one pin avoids double pinning; output notifications coalesce and unused state is lazy. Real checks exposed cancelled-linked-FIN close ordering and window-blocked RIO abort bugs; both were fixed without early memory release. **Batches belong in the common `SendRequest` queue**: sequences are already ordered requests; packing, files and deferred submissions remain future work.

## Results and confidence

Verified HTTP/1.1, 1 KiB body, pipeline one, four pinned server cores. Saved unmodified `4d6a47c2d6` deployments are the raw controls. Linux: 12 disjoint client cores, TLS 1.2/OpenSSL 3.5.8. Windows: eight client cores, TLS 1.3/SChannel. Absolute cross-platform RPS is not comparable.

**The full matrix below is the preserved earlier implementation snapshot, not a final stable-capacity claim. Its Windows TLS rows use `SslStream`, not the new native TLS below.** Linux and Windows long have one 3s-warmup/5s-measurement round; Linux uses 64 short/256 long connections, Windows long 256. Windows short (*) is an earlier iteration: 32 lanes, 4s warmup/5s measurement, one clean TCP matrix and three-sample TLS medians; not remeasured after final cleanup. Included windows have zero request errors and native ownership drained.

**Shared vs stock is the end-to-end benefit over default in-tree Kestrel's sockets transport:** `(shared RPS / stock RPS - 1) * 100`. Shared vs raw measures the abstraction's effect on the custom provider. Percentages use unrounded measurements from the same profile. Linux TLS gains include the native fd-bound TLS strategy versus stock `SslStream`, not just a socket-I/O backend change; Windows TLS uses `SslStream`/SChannel in every arm.

| Platform/provider | Traffic | Stock | Raw provider | Shared API | Shared vs stock | Shared vs raw |
|---|---|---:|---:|---:|---:|---:|
| Linux epoll | TCP short | 37,550 | 42,618 | 47,541 | +26.6% | +11.6% |
| Linux epoll | TCP long | 171,587 | 180,833 | 197,976 | +15.4% | +9.5% |
| Linux epoll | TLS short | 3,774 | 5,488 | 5,238 | +38.8% | -4.6% |
| Linux epoll | TLS long | 115,820 | 157,999 | 152,646 | +31.8% | -3.4% |
| Linux io_uring | TCP short | 37,550 | 44,773 | 45,472 | +21.1% | +1.6% |
| Linux io_uring | TCP long | 171,587 | 172,957 | 196,501 | +14.5% | +13.6% |
| Linux io_uring | TLS short | 3,774 | 5,625 | 5,498 | +45.7% | -2.2% |
| Linux io_uring | TLS long | 115,820 | 125,709 | 137,504 | +18.7% | +9.4% |
| Windows IOCP | TCP short * | 13,208 | 17,578 | 17,521 | +32.7% | -0.3% |
| Windows IOCP | TCP long | 90,410 | 89,172 | 86,289 | -4.6% | -3.2% |
| Windows IOCP | TLS short * | 2,695 | 2,790 | 2,531 | -6.1% | -9.3% |
| Windows IOCP | TLS long | 72,455 | 85,182 | 73,643 | +1.6% | -13.5% |
| Windows RIO | TCP short * | 13,208 | 18,001 | 18,931 | +43.3% | +5.2% |
| Windows RIO | TCP long | 90,410 | 113,389 | 102,380 | +13.2% | -9.7% |
| Windows RIO | TLS short * | 2,695 | 2,568 | 2,743 | +1.8% | +6.8% |
| Windows RIO | TLS long | 72,455 | 93,379 | 91,802 | +26.7% | -1.7% |

**The 5% target is not met by all Windows rows.** Larger Windows TCP-short runs hit client bind/connect `WSAENOBUFS`, including on stock, and are excluded. [Historical Windows numbers](src/Windows/RESULTS.md) and Linux history are not percentage baselines here.

### Linux io_uring TLS: ring-owned ciphertext (2026-10-21)

The Linux io_uring TLS rows above were measured with fd-bound OpenSSL, where the ring only provides readiness. The shared engine now defaults to the custom-BIO path: ring multishot receive delivers ciphertext, OpenSSL sees it only through a BIO, and ring `SEND` writes the output. `NETWORKPROTO_URING_TLS=fd` restores fd-bound OpenSSL, as does `NETWORKPROTO_KTLS=1` (a custom BIO cannot activate kernel TLS). The handshake-prefix callback also works in this mode.

Counterbalanced, three rounds, medians; same WSL host, four server cores, twelve client cores, 1 KiB responses, 64 short/256 long connections, 4s warmup/8s measurement. Absolute RPS is not comparable with the tables above (different session/host state); only within-batch ratios are valid.

| Traffic | Stock | io_uring fd-bound | io_uring ring-owned | Ring-owned vs stock | Ring-owned vs fd-bound |
|---|---:|---:|---:|---:|---:|
| TLS short | 5,453 | 7,573 | 7,937 | +45.6% | +4.8% (ranges overlap) |
| TLS long | 185,518 | 215,389 | 242,103 | +30.5% | +12.4% (won all 3 paired rounds) |

TCP-only io_uring in the same batch: short 64,109 vs stock 50,176 (+27.8%), long 286,599 vs 261,378 (+9.6%). Mid-run `/proc/<pid>/io` sampling showed about 3.2 `read`+`write` syscalls per request for fd-bound OpenSSL and about zero for ring-owned. TLS short is dominated by the RSA-2048 handshake, so the ring change is not expected to move it much. Ring-owned mode costs another 32 MiB of pinned ciphertext storage per worker.

[Exact metrics and profile boundaries](src/Shared/shared-results-20261007.json); raw/rejected logs remain in ignored artifact folders.

### Later Windows stabilization: keep-alive only

Initial two-round **same-binary controls**, 8s warmup/10s measurement and 256 connections, gave these averages. They are separate from the archived-raw snapshot above; stock was not included in this control.

| Workload | Raw IOCP | Shared IOCP | Difference | Raw RIO | Shared RIO | Difference |
|---|---:|---:|---:|---:|---:|---:|
| TCP keep-alive | 159,067 | 154,492 | -2.9% | 168,544 | 173,716 | +3.1% |
| TLS keep-alive | 158,264 | 155,929 | -1.5% | 173,519 | 173,990 | +0.3% |

These show that the earlier large regressions were not consistently reproduced, **not that the full target passed**. A later counterbalanced archived-raw campaign had mostly steady one-second windows (typically 0.5-2.2% variation), but changes between process launches; one IOCP TCP pair was -13.6%. Its last shared IOCP TLS arm also had a setup `10055`, so that campaign was not accepted wholesale.

Outgoing client bind/connect failures occurred with both the preview .NET 11 and already-installed stable .NET 10 clients, including during keep-alive setup. Explicit-endpoint and socket-handle-reuse experiments were rejected and removed. No API/core performance rewrite or machine TCP-policy change was made. The runner now preserves per-second throughput, error stages, binary hashes and counterbalanced ordering, and rejects warmup, measured and teardown errors. Evidence remains under `artifacts\windows-stability-*`, `windows-stable-long-20261007` and the rejected diagnostic directories. Windows-to-Windows testing remains the chosen topology; a WSL-client comparison was not run.

### Native Windows TLS, October 18

**IOCP/RIO now implement TCP or TCP+TLS inside the transport.** Select `NativeTls` on the listener; Kestrel receives plaintext. `samples\NativeTlsHttp1` demonstrates it without a server TLS wrapper. [Architecture, public-source review, limits and exact evidence](src/Windows/NATIVE-TLS.md).

Latest three-round counterbalanced medians after bounded initial-handshake offload; 32 short lanes or 256 keep-alive connections, four server/eight client physical cores, 5s warmup + 8s measurement. Same binaries/client/RSA-2048 fixture, TLS 1.3/AES-256-GCM, resumption disabled, exact 1 KiB HTTP/1.1 response and full TLS/TCP EOF. All 30 included arms are error-free and drained. Do not compare absolute rates against earlier snapshots.

| TLS traffic | Stock | IOCP native | Vs stock / IOCP wrapper | RIO native | Vs stock / RIO wrapper |
|---|---:|---:|---:|---:|---:|
| Short | 1,918 | 1,944 | +1.4% / +0.5% | 1,980 | +3.2% / +1.7% |
| Keep-alive | 91,693 | 92,791 | +1.2% / +2.3% | 102,507 | +11.8% / +2.3% |

The initial `AcceptSecurityContext` call moves to a bounded per-listener ThreadPool executor: 8 running/dispatched slots and 512 pending jobs with four workers, within the same process CPU affinity. Context/input ownership is exclusive until an owner completion command returns it; final handshake, record I/O and RIO queues remain worker-owned. A separate three-round same-binary off/on comparison improved short medians by 29.6% IOCP / 28.1% RIO. Shared ThreadPool contention remains an application tradeoff, not a universal improvement claim.

**Short medians now match stock/wrappers, not a per-run +/-5% guarantee:** native IOCP ranged 1,664-1,976 and RIO 1,926-1,989; one IOCP arm was -19.6% against its same-round wrapper. The receive-adapter copy remains eliminated in this HTTP workload, but immutable sends still copy into writable record storage and fragmented input may compact. [Exact current samples and hashes](src/Windows/native-handshake-offload-results-20261018.json); the linked architecture report preserves the original inline-only results separately.

## Code map and remaining scope

| Read | What it owns |
|---|---|
| `Primitives\src\NetworkEngine.cs`, `NetworkBuffer.cs` | Engine/listener/options and buffer/slice/payload contracts. |
| `Primitives\src\NetworkConnection.cs`, `NetworkInputReader.cs` | Common send progression, ownership, receive cursors and stream handoff. |
| `Primitives\src\NetworkTlsExtensions.cs` | Caller-installed TLS, preserving unread ciphertext. |
| `src\Shared\NetworkEngineFactory.cs`, `SharedConnectionContext.cs`, `Registration.cs` | Provider construction, native drivers and Kestrel adaptation. |
| `src\Epoll`, `src\IoUringTcp`, `src\Windows`, `src\Rio` | Native engines and operation lifetimes; `Windows\Schannel.cs` / `SchannelConnection.cs` own native Windows TLS. |

The exact signature baseline is `Primitives\src\PublicAPI.Unshipped.txt`. [PUBLIC-API.md](PUBLIC-API.md) captures public-only declarations without implementation. The standalone [console demos](samples/README.md) cover plain TCP, native HTTPS, native raw TLS-prefix observation and framed retained-page sends on all four providers. They also preserve a separate stream-based application-prefix upgrade example. `..\samples\NetworkProtoSample\SharedTransportCheck.cs` contains the broader real-socket ownership/cancellation checks.

Selectors: `sharedEpollTcp`, `sharedEpollTls`, `sharedIoUringTcp`, `sharedIoUringTls`, `sharedIocp`, `sharedRio`, `sharedIocpTls`, `sharedRioTls`. `--check-windows-native-tls true` checks transport-owned Windows TLS, retention, fragmented records, half-close, blocked output/reset and shutdown. Existing `--check-shared-tls true` remains a wrapper-based prefix/ClientHello handoff check. Pressure checks use `NETWORKPROTO_WINDOWS_SNDBUF=4096` or the Linux equivalents.

Activate the repository SDK before building. Runners: Linux `scripts\shared-bench.sh`; Windows `scripts\windows-bench.ps1`. All source remains on `dmkorolev/network-proto`, uncommitted. Excluded: client/connect, IPv6, HTTP/2/3 validation, production recovery, a public worker scheduler, generic fd-TLS upgrade, kernel zero-copy and advanced batches. The unresolved Windows measurements do not justify treating this as a finalized framework API.
