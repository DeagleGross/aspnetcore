# IoUringBio: TCP with an optional custom-BIO TLS layer

Non-shipping WSL prototype, measured 2026-09-27. The existing `IoUringTcp` and fd-bound `IoUringTls` native implementations are preserved. This experiment shares their managed listener/owned-PipeReader adapter, but selects a separate native library: `libnetworkprotobio.so`.

## Architecture

```text
HTTP mode:
    io_uring multishot receive -> native page -> PipeReader -> Kestrel

HTTPS mode:
    io_uring multishot receive -> ciphertext page queue
        -> custom BIO read -> OpenSSL -> plaintext page -> PipeReader -> Kestrel

HTTPS output:
    Kestrel output pipe -> SSL_write_ex
        -> custom BIO write -> stable ciphertext buffer -> io_uring send
```

Both modes use the same raw TCP receive/send machinery. TLS is optional at listener creation, rather than implied by an io_uring socket. This demonstrates the composition principle used by runtimes such as Monoio; it is not a general-purpose managed TLS adapter for arbitrary transports, nor an implementation copied from Monoio.

This differs from `IoUringTls`, where OpenSSL's socket BIO performs socket I/O and io_uring provides readiness notifications. Here there is **no `SSL_set_fd()` or socket BIO**: actual ciphertext receive/send operations use io_uring. `pollSqes` is zero in the measured HTTPS runs.

This custom BIO is also different from the kTLS passthrough filter in the separate experiments repository. It is a source/sink over the transport's buffers, has no underlying socket BIO, does not implement fd/kTLS control operations, and does not enable kTLS.

An explicit activation probe is now available via `NETWORKPROTO_KTLS=1`: it requests OpenSSL kTLS and rejects any handshake without both RX and TX active. On 2026-09-27 this source/sink returned RX=0/TX=0 and the probe correctly rejected the connection; it is not a supported kTLS data path. In contrast, the fd-bound `IoUringTls` prototype enabled both directions but did not improve RPS in the matched small-response comparison. See [kTLS results and limitations](../IoUring/RPS-RESULTS.md#ktls-experiment-2026-09-27-evening). Leave this variable unset or `0` for ordinary layered BIO runs.

## Buffer and operation ownership

- Raw receives use ordinary (non-incremental) provided-buffer multishot receive.
- In TCP mode, those pages become the managed reader's input and are returned on consumption.
- In TLS mode, ciphertext page IDs form a per-connection queue. The BIO references the existing received pages; no copy into `BIO_s_mem()` occurs.
- OpenSSL's BIO read interface supplies a destination. The callback copies from the queued ciphertext pages into that destination. Completely consumed pages return to the provided-buffer ring.
- `SSL_read_ex` writes plaintext directly into a different native page pool. The managed `PipeReader` exposes those same plaintext pages without another payload copy.
- BIO output copies into a bounded per-connection ciphertext buffer. It must not borrow OpenSSL's pointer after returning success. This buffer remains unchanged until the send's terminal CQE; partial sends advance its offset.
- Pending output causes BIO retry-write rather than overwriting the buffer. Handshake, application writes, and close-notify all drain through the same send path.
- Application output pipe storage remains pinned through its native operation's completion. TLS completion is not reported to that sender until generated ciphertext has drained.
- Shutdown cancels/drains outstanding receive operations before releasing connection state and unconsumed ciphertext pages.

TLS reserves 32 MiB of ciphertext pages plus 32 MiB of plaintext pages per worker, as well as connection state including a 32 KiB output buffer. This is intentionally a simple bounded-pool prototype, not a minimal-memory implementation.

### Copy counters are explicit

`BIO_METRICS` reports:

| Counter | Meaning |
|---|---|
| `inputBioCopyBytes` | Ciphertext copied from receive pages into the destination OpenSSL requests |
| `outputBioCopyBytes` | Ciphertext copied from OpenSSL into stable asynchronous send storage |
| `ciphertextPagesReceived` / `ciphertextPagesReturned` | Native buffer ownership accounting |
| `partialCiphertextSends` | Send completions which did not finish the submitted buffer |
| `tlsErrors` / `peerAborts` | TLS errors and classified peer disconnects |
| `memoryBioStagingBytes` | Zero by implementation: there is no memory-BIO staging buffer |

`OWNED_METRICS` still reports plaintext page return, native submissions, and managed pump/wake interop counts. Neither zero staging nor zero adapter-copy bytes means end-to-end zero-copy TLS/networking.

## API and sample use

The experimental registration is:

```csharp
builder.WebHost.UseIoUringBio(tls: false, certificatePath: "", keyPath: "");
// Or use tls: true and PEM certificate/key paths.
```

The sample selects it using `--backend IoUringBio`. `--scheme http` disables TLS; `--scheme https` enables the custom-BIO layer and supplies TLS features. The HTTPS path must **not** add Kestrel's `UseHttps()`/SslStream middleware again.

The same native ABI and managed pipe integration are reused so the comparison changes the TLS I/O architecture rather than the HTTP parser or application.

```bash
cd ~/code/aspnetcore
source activate.sh
dotnet build src/Servers/Kestrel/samples/NetworkProtoSample/NetworkProtoSample.csproj -c Release --no-restore
NETWORKPROTO_COALESCE=1 src/Servers/Kestrel/samples/NetworkProtoSample/scripts/quick-rps.sh IoUringBio
```

Use `SCHEME=https` to limit that script to TLS, or `SCHEME=http` for plaintext. The runner retains the same four server cores and twelve separate client cores. It prints results; no service is left running afterward.

## Fresh results

All results below use the same sample, 1,024-byte response, HTTP/1.1, four server cores (`0,2,4,6`), twelve client cores, 1,200 connections and 15-second wrk2 runs. All native variants use coalesced wakes. TLS uses TLS 1.2, RSA-2048, AES-128-GCM and disabled resumption, matching the existing sample. These are new controls, not a comparison against yesterday's lower baseline.

### TLS, three runs per primary variant

| Transport | Short-connection RPS, runs | Median | Keep-alive RPS, runs | Median |
|---|---|---:|---|---:|
| Stock Sockets + SslStream | 3,844.54 / 4,028.24 / 3,930.48 | 3,930.48 | 130,748.51 / 125,059.64 / 128,650.33 | 128,650.33 |
| Existing fd-bound IoUringTls | 5,205.10 / 5,409.51 / 5,367.92 | 5,367.92 | 144,317.00 / 146,222.01 / 159,231.77 | 146,222.01 |
| **Custom-BIO IoUringBio** | **5,733.90 / 5,327.88 / 5,359.46** | **5,359.46** | **163,025.98 / 157,159.21 / 167,236.19** | **163,025.98** |

Custom BIO was essentially tied with fd TLS on median short-connection throughput and approximately 11.5% higher on median keep-alive throughput. Relative to stock, its medians were approximately +36.4% short and +26.7% keep-alive. The third fd result overlaps the custom-BIO range: this is evidence of a promising design, not proof of universal superiority.

An additional raw IoUringTcp + SslStream control was run twice: short 3,534.49 / 3,810.89 RPS and keep-alive 142,123.27 / 142,247.72 RPS.

### Plaintext, one-run smoke comparison

| Transport | Short RPS | Keep-alive RPS |
|---|---:|---:|
| Stock Sockets | 65,910.52 | 208,079.08 |
| IoUringBio with TLS disabled | 44,415.06 | 225,150.16 |

The new backend works without TLS, but the short-connection regression remains. Do not use a single run to establish a stable plaintext improvement.

### Measurement and correctness limits

Offered rates deliberately exceeded capacity: 200k TCP short, 50k TLS short, 1M keep-alive. Corrected latency grew to seconds. These are achieved-throughput comparisons, not sustainable latency-SLO capacity. Stock TCP short reported 56 timeouts. The new backend's load runs reported no wrk2 socket/HTTP errors; its `tlsErrors` counters were zero, while peer aborts were counted separately.

The first TLS keep-alive run completed about 2.45 million requests with 1,204 receive submissions, zero poll submissions, and about 0.114 measured pump/wake interops per request. Every exposed plaintext page and every received ciphertext page was returned at shutdown across the new backend's measured runs.

Managed allocation remained approximately 4,100-4,200 bytes/request, compared with roughly 1,650 bytes/request for stock. This change targets I/O composition, not zero-allocation.

`scripts/check-owned.mjs` passed for HTTP and HTTPS: fragmented headers, a page-spanning header, pipelining, reuse, explicit close and exact response bodies. `scripts/check-bio.mjs` adds a fragmented 40 KiB body, 3,001 pipelined responses with a paused reader, and peer disconnects before/during TLS. The native library was also instrumented with AddressSanitizer/UndefinedBehaviorSanitizer in a separate build-output copy while running real Kestrel. This exercised an actual partial ciphertext send with no native sanitizer diagnostic; leak detection was disabled for that mixed .NET/native process. It is not proof of all cancellation, pool-exhaustion, or TLS-control-message cases.

Raw data lives in the sample's ignored `results/quick-*-20260927-14*/` directories. The aggregate `results/layered-bio-comparison-20260927.json` includes per-run RPS, allocation, page-return and BIO copy counters. The source remains an experimental TLS-1.2-only implementation without generic TLS callbacks, kTLS, comprehensive handshake timeouts, or production-hardening.
