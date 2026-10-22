# Shared network API: scenarios, providers and results

**The functional prototype is complete for epoll, io_uring, IOCP and RIO. The full Windows +/-5% performance target remains unverified.** This is non-shipping experimental `System.Net.Transport`, not an approved BCL addition. It demonstrates reusable server/protocol-library infrastructure; Orleans itself was not integrated.

## Chosen API

`Primitives\src` holds framework-neutral contracts, owned buffers and the shared connection algorithm. `src\Shared` holds the engine factory, native-provider drivers and the Kestrel adapters. Select `EpollEngineOptions` / `IoUringEngineOptions` on Linux or `IocpEngineOptions` / `RioEngineOptions` on Windows.

**Layering (inverted 2026-10-21).** Providers publish a Kestrel-free `ProviderNetworkConnection : NetworkConnection` through the internal `IProviderListener` / `IProviderConnection` SPI. Only Kestrel's accept loop (`Registration.cs`) wraps it in a `KestrelConnectionContext`, so console samples and other consumers never allocate or see a Kestrel type. Before this change the neutral connection lived inside a Kestrel `DefaultConnectionContext` subclass and was unwrapped by casting.

```text
before: provider worker -> SharedConnectionContext : DefaultConnectionContext { .Network }
                           -> neutral listener casts it back to get .Network
after:  provider worker -> ProviderNetworkConnection : NetworkConnection  (no Kestrel types)
                           -> neutral listener            -> your code (NetworkConnection)
                           -> Kestrel accept loop wraps -> KestrelConnectionContext -> Kestrel
```

This is a layering and testability change, **not a performance change**. Kestrel still allocates one `ConnectionContext` per connection; the allocation moved from the worker to the accept loop. Non-Kestrel consumers save that allocation. The functional gain is `NetworkConnection.TlsInfo`: TLS facts now come from the provider through the neutral API (Windows reports the real SChannel suite; Linux reports TLS 1.2 ECDHE-RSA-AES128-GCM-SHA256, the only protocol/cipher its OpenSSL server context allows) and Kestrel's `ITlsConnectionFeature` / `ITlsHandshakeFeature` read it. The workers still throw `ConnectionAbortedException` for aborts (an exception type from `Microsoft.AspNetCore.Connections` accepted by `Abort(Exception)`), kept to preserve exact exception semantics; a future assembly split would have to replace it. On Windows the obsolete cipher/hash/key-exchange feature properties are now derived best-effort from the suite name rather than from SChannel `ConnectionInfo`; nothing in the repo reads them.

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

### Layering inversion: no-regression check (2026-10-21)

The inversion was meant to be performance-neutral, so it was checked against a pre-change build of the same sample (same WSL host, four server cores, twelve client cores, 1 KiB responses, 64 short/256 long connections, 4s warmup/8s measurement, io_uring in ring-owned mode). Three counterbalanced rounds, medians, reported and not independently reproduced:

| Backend | Traffic | Before | After | Delta | Range before / after |
|---|---|---:|---:|---:|---|
| epoll TCP | short | 62,531 | 62,020 | -0.8% | 60,000-63,853 / 60,629-63,432 |
| epoll TCP | long | 280,525 | 281,327 | +0.3% | 277,191-295,071 / 268,333-285,336 |
| epoll TLS | short | 7,706 | 7,571 | -1.8% | 7,700-7,825 / 7,395-7,695 |
| epoll TLS | long | 238,953 | 238,718 | -0.1% | 230,800-250,124 / 236,349-240,757 |
| io_uring TCP | short | 67,137 | 66,235 | -1.3% | 66,743-69,560 / 65,580-71,563 |
| io_uring TCP | long | 288,700 | 296,776 | +2.8% | 282,181-296,685 / 295,982-300,397 |
| io_uring TLS | short | 8,015 | 8,320 | +3.8% | 7,817-8,222 / 8,138-8,459 |
| io_uring TLS | long | 241,784 | 250,835 | +3.7% | 221,856-257,304 / 234,328-253,227 |

No cell moved beyond run-to-run noise in either direction. Epoll TLS short was lower in all three paired rounds (-4.0%, -0.1%, -3.2%); with three samples that cannot be separated from noise, but it is the one cell worth rechecking if short-connection cost matters, because the per-connection `ConnectionContext` allocation now happens on Kestrel's accept loop instead of the worker. Windows was not re-benchmarked; its functional checks all passed.

### Response-size matrix: 1, 16 and 256 KiB bodies (2026-10-21)

**The 1 KiB wins above do not extend to large responses.** This batch measured the current code with per-segment sends (no vectored/gather send) at three body sizes. All four providers use their native TLS, stock is default in-tree Kestrel sockets. Counterbalanced (backend order rotated every round), three rounds, medians, 4s warmup/8s measurement, four pinned server cores, 64 short/256 long connections, zero load errors, native ownership drained. Linux is WSL loopback with 12 client cores, TLS 1.2/OpenSSL 3.5.8, io_uring ring-owned. Windows uses eight client cores and TLS 1.3/SChannel. Reported, not independently reproduced; single host per OS, absolute RPS is not comparable across platforms. Five Windows short-connection cases were rerun after a client `WSAENOBUFS`; none failed after retry.

| Linux | Body | Stock | epoll | vs stock | io_uring | vs stock |
|---|---|---:|---:|---:|---:|---:|
| TCP short | 1 KiB | 44,798 | 50,568 | +12.9% | 54,189 | +21.0% |
| TCP short | 16 KiB | 36,866 | 32,033 | -13.1% | 32,217 | -12.6% |
| TCP short | 256 KiB | 13,543 | 4,922 | -63.7% | 4,251 | -68.6% |
| TCP long | 1 KiB | 192,248 | 211,158 | +9.8% | 219,287 | +14.1% |
| TCP long | 16 KiB | 132,790 | 73,381 | -44.7% | 59,575 | -55.1% |
| TCP long | 256 KiB | 18,579 | 5,317 | -71.4% | 4,417 | -76.2% |
| TLS short | 1 KiB | 4,303 | 6,245 | +45.1% | 6,702 | +55.8% |
| TLS short | 16 KiB | 4,165 | 5,523 | +32.6% | 6,513 | +56.4% |
| TLS short | 256 KiB | 2,787 | 2,419 | -13.2% | 2,311 | -17.1% |
| TLS long | 1 KiB | 123,939 | 156,415 | +26.2% | 160,975 | +29.9% |
| TLS long | 16 KiB | 83,846 | 54,204 | -35.4% | 50,013 | -40.4% |
| TLS long | 256 KiB | 7,934 | 4,229 | -46.7% | 3,952 | -50.2% |

| Windows | Body | Stock | IOCP | vs stock | RIO | vs stock |
|---|---|---:|---:|---:|---:|---:|
| TCP short | 1 KiB | 10,733 | 10,291 | -4.1% | 11,658 | +8.6% |
| TCP short | 16 KiB | 10,396 | 9,533 | -8.3% | 11,316 | +8.8% |
| TCP short | 256 KiB | 8,088 | 4,184 | -48.3% | 4,851 | -40.0% |
| TCP long | 1 KiB | 91,791 | 96,402 | +5.0% | 100,442 | +9.4% |
| TCP long | 16 KiB | 101,763 | 71,396 | -29.8% | 74,147 | -27.1% |
| TCP long | 256 KiB | 14,798 | 8,023 | -45.8% | 7,480 | -49.5% |
| TLS short | 1 KiB | 2,139 | 2,167 | +1.3% | 2,380 | +11.3% |
| TLS short | 16 KiB | 2,066 | 2,058 | -0.4% | 2,140 | +3.6% |
| TLS short | 256 KiB | 1,724 | 1,704 | -1.2% | 1,673 | -2.9% |
| TLS long | 1 KiB | 81,723 | 89,892 | +10.0% | 96,931 | +18.6% |
| TLS long | 16 KiB | 58,955 | 46,599 | -21.0% | 51,089 | -13.3% |
| TLS long | 256 KiB | 6,192 | 5,341 | -13.7% | 5,464 | -11.7% |

Windows stock TCP long at 256 KiB ranged 12,217-24,655 across rounds, so that row is not stable. Short TLS is handshake-dominated, so body size matters little there until 256 KiB.

On Linux the server is CPU-saturated (about 4 cores) in every large-body long cell, so the gap is cost per request: TCP long at 256 KiB spends roughly 0.76 (epoll) and 0.88 (io_uring) server CPU-ms per request versus 0.22 for stock. This is consistent with per-segment sends, since a 256 KiB response is many output-pool segments and stock submits them together, but this matrix did not isolate the cause. A vectored-send prototype measured earlier (WSL loopback, TCP long only) closed most of the gap and is stored, not merged, on branch `dmkorolev/network-proto-vectored-sends`.

### Adaptive provider-owned output pool (2026-10-22)

**Root cause of the large-body losses above, and an internal fix with no public API change.** Kestrel renders a response into the output `Pipe`, and the `Pipe` uses whatever block size its `MemoryPool<byte>` returns (4 KiB shared-pool blocks on Linux, 16 KiB native pages on Windows). `NetworkConnection.PumpOutputCore` issues one native send per segment, so a 64 KiB response cost 17 sends on Linux, while stock Kestrel sends all segments in one multi-buffer syscall. The transport now gives the pipe its own pool, `AdaptiveOutputPool`, over a per-worker `NativeMemoryPool` with power-of-two size classes. The pipe rents another block only after the current one is full, so every block of a burst except the last was filled completely, and the pool sizes the next burst's first block from that lower bound. Shrinking uses an exponential backoff so a failed probe does not repeat soon. A connection starts from the worker's `OutputBlockHint`, which connections raise when they fill a block and lower when they close without filling one; without it one-request connections relearned the size on every request and paid 6 sends per 64 KiB response. Block sizes are per provider: Linux starts at 4 KiB and is capped at 256 KiB, IOCP starts at 64 KiB and is capped at 256 KiB (1 MiB hurt it by about 13% at 256 KiB), and RIO starts at 64 KiB and is capped at 1 MiB because RIO cannot gather and benefits from one large registered block. `NETWORKPROTO_OUT_MAX=0` disables the pool for A/B runs and any other value overrides the cap.

Same method as the matrix above: counterbalanced, 4s warmup/8s measurement, four pinned server cores, single host per OS, two to three rounds per cell, every response body compared byte-for-byte by the load client with zero errors. Means of same-batch rounds, reported, not independently reproduced. Ratios are only comparable within a batch. Stock drifted between batches, and a few Linux cells compare against stock from an adjacent batch of the same method.

| Linux (WSL), pool vs stock | Body | Stock | epoll | vs stock | io_uring | vs stock | sends/response |
|---|---|---:|---:|---:|---:|---:|---|
| TCP long | 64 KiB | 84,200 | 99,800 | +18% | 100,000 | +19% | 17 -> 1 |
| TCP short | 64 KiB | 29,100 | 34,600 | +19% | 35,600 | +22% | 6 -> 1 |
| TLS long | 64 KiB | 42,200 | 48,400 | +15% | 45,600 | +8% | 17 -> 1 (epoll), 17 -> 5 (ring) |
| TLS short | 64 KiB | 4,330 | 5,740 | +33% | 5,920 | +37% | 6 / 10 |
| TCP short | 1 KiB | 40,100 | 53,500 | +33% | 56,400 | +41% | 1 |
| TCP long | 256 KiB | 25,500 | 28,300 | +11% | 28,400 | +11% | 2 |
| TCP short | 256 KiB | 17,500 | 20,300 | +16% | 20,500 | +17% | 2 |
| TLS long | 256 KiB | 9,540 | 14,360 | +51% | 12,640 | +32% | 2 (epoll), 17 (ring) |
| TLS short | 256 KiB | 3,390 | 4,900 | +45% | 4,960 | +46% | 2 (epoll), 20 (ring) |
| TLS long | 16 KiB | 107,040 | 119,450 | +12% | 96,960 | -9% | 1 (epoll), 2 (ring) |

Before the shared hint, Linux TCP short at 64 KiB was -12% against stock for both providers (6 sends per response). The TLS short 64 KiB row is from that earlier batch, where the first-block cost is small next to the handshake. The 1 KiB TCP long rows were unchanged by the pool (epoll 239,000 and io_uring 248,000 with the pool disabled against 243,000 and 235,000 with it, within noise).

| Windows, pool vs stock | Body | Stock | IOCP | vs stock | RIO | vs stock |
|---|---|---:|---:|---:|---:|---:|
| TCP long | 64 KiB | 54,100 | 59,000 | +9% | 58,100 | +7% |
| TCP long | 256 KiB | 17,800 | 18,900 | +6% | 22,500 | +26% |
| TCP short | 64 KiB | 11,650 | 10,340 | -11% | 12,740 | +9% |
| TCP short | 256 KiB | 8,910 | 7,670 | -14% | 9,890 | +11% |
| TLS short | 64 KiB | 2,060 | 2,120 | +3% | 2,190 | +7% |
| TLS short | 256 KiB | 2,090 | 2,130 | +2% | 2,180 | +4% |

Windows TCP long at 1 and 16 KiB (two rounds) was +1%/+9% for IOCP and +9%/+12% for RIO. IOCP TCP short is still behind stock, but it already was before this change (-4% at 1 KiB and -8% at 16 KiB in the matrix above), so the pool is not the cause; at 256 KiB it moved from -48% to -14%. Windows TLS long with the pool was not remeasured. Linux ring-owned TLS still emits one send per 16 KiB TLS record (17 sends at 256 KiB), which is why it trails epoll TLS and loses to stock at 16 KiB; that is the ciphertext send path, not the plaintext pool, and is not changed here. A 16 KiB first-block floor on Windows measured 12-15% slower than a 64 KiB floor with identical per-operation counts, and the cause of that gap was not established.

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
| `src\Shared\NetworkEngineFactory.cs`, `IProviderConnection.cs`, `ProviderNetworkConnection.cs` | Provider construction, the Kestrel-free provider SPI and native drivers. |
| `src\Shared\KestrelAdapters.cs`, `Registration.cs` | The only shared-path code that touches Kestrel connection types: `KestrelConnectionContext`, TLS features and the raw-provider listener adapter. |
| `src\Epoll`, `src\IoUringTcp`, `src\Windows`, `src\Rio` | Native engines and operation lifetimes; `Windows\Schannel.cs` / `SchannelConnection.cs` own native Windows TLS. |

The exact signature baseline is `Primitives\src\PublicAPI.Unshipped.txt`. [PUBLIC-API.md](PUBLIC-API.md) captures public-only declarations without implementation. The standalone [console demos](samples/README.md) cover plain TCP, native HTTPS, native raw TLS-prefix observation and framed retained-page sends on all four providers. They also preserve a separate stream-based application-prefix upgrade example. `..\samples\NetworkProtoSample\SharedTransportCheck.cs` contains the broader real-socket ownership/cancellation checks.

Selectors: `sharedEpollTcp`, `sharedEpollTls`, `sharedIoUringTcp`, `sharedIoUringTls`, `sharedIocp`, `sharedRio`, `sharedIocpTls`, `sharedRioTls`. `--check-windows-native-tls true` checks transport-owned Windows TLS, retention, fragmented records, half-close, blocked output/reset and shutdown. Existing `--check-shared-tls true` remains a wrapper-based prefix/ClientHello handoff check. Pressure checks use `NETWORKPROTO_WINDOWS_SNDBUF=4096` or the Linux equivalents.

Activate the repository SDK before building. Runners: Linux `scripts\shared-bench.sh`; Windows `scripts\windows-bench.ps1`. All source is on branch `dmkorolev/network-proto`. Excluded: client/connect, IPv6, HTTP/2/3 validation, production recovery, a public worker scheduler, generic fd-TLS upgrade, kernel zero-copy and advanced batches. The unresolved Windows measurements do not justify treating this as a finalized framework API.
