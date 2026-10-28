# Transport-owned Windows TLS

IOCP and RIO now support **TCP only** or **TCP + native TLS**, just like the Linux native-TLS listener paths. With TLS selected, the transport completes the handshake before publishing the connection, exposes plaintext receive leases and encrypts application sends. **There is no server `SslStream`, stream adapter or Kestrel `UseHttps` in this path.**

## Use it

```csharp
var options = new RioEngineOptions { WorkerCount = 4 };
await using var engine = NetworkEngineFactory.Create(options);
await using var listener = await engine.ListenAsync(endpoint, new NetworkListenOptions
{
    NativeTls = new NetworkNativeTlsOptions
    {
        CertificatePath = "certificate.pem",
        PrivateKeyPath = "key.pem"
    }
});
await using var connection = await listener.AcceptAsync()
    ?? throw new IOException("Listener closed.");
// connection.IsTls is true; ReceiveAsync returns plaintext.
```

Use `IocpEngineOptions` instead for IOCP. Omit `NativeTls` for raw TCP. For Kestrel, call `UseNetworkTransport(options, nativeTls)` and do **not** also call `UseHttps`. The sample selectors are `sharedIocpTls` / `sharedRioTls`; the older `sharedIocp` / `sharedRio` HTTPS selectors remain explicit `SslStream` comparison paths.

The standalone `samples\NativeTlsHttp1` console app shows this listener/connection API without Kestrel. Its server never creates a TLS wrapper; only the independent client uses one.

`samples\PeekTlsHttp1` additionally sets `NetworkNativeTlsOptions.HandshakePrefixCallback`. The synchronous callback observes a borrowed raw prefix of the first received TLS bytes before SSPI starts; it may be fragmented and is not a parsed/complete ClientHello. The connection is still published only after authentication, and the server never installs a stream wrapper. The same option uses `MSG_PEEK` before fd-bound OpenSSL on Linux.

## What replaces the stream wrapper

```text
native ciphertext receive
  -> AcceptSecurityContext while handshaking
  -> DecryptMessage in owned native/registered memory
  -> plaintext slice + reference-counted lease
  -> shared receive reader -> Kestrel/application

application plaintext send
  -> reusable record storage with header/trailer room
  -> EncryptMessage
  -> WSASend or RIOSend -> terminal completion
```

`Schannel.cs` declares SSPI using Windows SDK layouts and owns credentials/context handles. `SchannelConnection.cs` is the record/handshake state machine. Each connection has one owning I/O worker; only initial context creation can temporarily transfer exclusive context/input ownership to the bounded handshake executor described below. Socket I/O is still real IOCP or RIO, not a `SocketAsyncEngine` wrapper.

**The useful receive optimization is the ownership contract.** `DecryptMessage` returns plaintext ranges inside its input buffer. Instead of copying those bytes to a stream caller's array or another input pipe, the transport publishes a slice backed by that same storage. Multiple records can share a storage owner; an application-retained plaintext lease prevents reuse. Extra ciphertext cannot be compacted over live plaintext.

This is **not zero-copy everywhere**. Fragmented records/handshakes may need compaction or growth, which is counted. Sends copy immutable borrowed plaintext once into writable record storage before in-place encryption; mutating the caller's buffer would violate the API. Handshake tokens are copied into sendable storage. SChannel's internal work and kernel copies are not measured by our adapter counters.

Record storage is reused after completion, not rented per keep-alive response. When the actual completed output-pipe read identifies the last segment, the last application record and `close_notify` share one send, followed by FIN after completion. RIO uses registered ciphertext storage, so it needs no additional unregistered-send staging copy.

## I/O workers and handshake executor

The initial `AcceptSecurityContext` call is synchronous. Running it in a completion batch delayed unrelated response/close work. The worker now services up to 16 commands and one local receipt between native completions, and hands initial context creation to `HandshakeScheduler.cs`. Final handshake processing, record encryption/decryption, CQ/RQ access and connection transitions remain on the owner.

```text
IOCP/RIO owner: receive ClientHello, hold context/input
  -> bounded ThreadPool job: initial AcceptSecurityContext
  -> owner command: apply result, free token, release work hold
  -> worker-owned handshake I/O, final handshake and plaintext records
```

This is a scheduling choice, not an IOCP requirement or faster cryptography. OpenSSL handshake computation is synchronous too; readiness/WANT states make network waiting nonblocking, not the cryptographic call itself. In this implementation the measured short-path improvement comes from reducing head-of-line blocking.

**Concurrency and lifetime rules:**

- One context has at most one SSPI caller. While a job owns it, the worker cannot process records, overwrite/compact input or dispose the context. The result crosses the existing concurrent command queue before owner-side processing resumes.
- Queued cancellation can skip the native call; running cancellation cannot interrupt it. Both retain input/context ownership until exactly one owner completion, which frees or applies the returned token. Close and pool disposal wait for that hold and native I/O to drain.
- Credentials are shared across independent contexts but outlive all workers/jobs. Listener disposal awaits worker and executor drain before freeing them.
- Per listener, at most `2 * workers` running/dispatched jobs and `128 * workers` pending jobs: **8 slots + 512 queued jobs with four workers**. These are not extra CPU cores or a global ThreadPool-thread limit. Queue overflow is an explicit connection error, not an inline fallback.

The executor uses `UnsafeQueueUserWorkItem` without application `ExecutionContext` flow; it does not change ThreadPool minimums/maximums. All server threads retain the same four-core process affinity. `NETWORKPROTO_TLS_HANDSHAKE_OFFLOAD=0` restores direct initial calls for same-binary comparisons; the experiment defaults to enabled.

**Application tradeoff:** these jobs share the application's ThreadPool. Blocking application work can delay handshakes and cancellation drain; multiple listeners multiply the per-listener budget. The limits do not bound all accepted connections or promise latency under starvation. No mixed-application/starvation benchmark was performed, and the prototype's fatal policy for an undrained shutdown is not production recovery.

## Opt-in switches

- `NETWORKPROTO_WINDOWS_TLS=1.2` restricts the transport-owned SChannel server to TLS 1.2. The default negotiates the platform's best protocol (TLS 1.3 where enabled). It exists because the crank lab's `wrk` build uses OpenSSL 1.1.0g, which cannot speak TLS 1.3.
- `NETWORKPROTO_WINDOWS_TLS_IMPL=session` replaces the direct SSPI state machine (`SchannelConnection.cs`) with the runtime's sans-I/O `TlsBufferSession` ([dotnet/runtime#128871](https://github.com/dotnet/runtime/issues/128871), `[Experimental("SYSLIB5007")]`) in `TlsSessionConnection.cs`. Both implementations sit behind `IWindowsTlsConnection`, share the handshake executor and publish plaintext the same way. The default is the direct SSPI path.

The session path differs in cost, not in contract: `TlsBufferSession.Read` and `Write` copy through runtime-owned scratch buffers, so decrypted records are published from a separate 16 KiB plaintext page instead of in place. A server session always suspends at handshake completion with `NeedsCertificateValidation`; the adapter resolves it with `SetRemoteCertificateValidationResult(SslPolicyErrors.None)` because no client certificate is requested. Single crank runs (TLS 1.2, keep-alive, 8 KiB, 256 connections) were within run-to-run noise of the direct path for RIO and about 8% lower for IOCP; this is reported, not independently reproduced.

## Public-source review and experiments

| Source | What was verified / used |
|---|---|
| [SChannel IOCP server example](https://github.com/coin8086/WinSocket/blob/35c5a7bd409fa7306b547400131da44efebd4919/IocpServer/ServerSocket.cpp) | TLS state integrated into an overlapped server. Its stream-like receive API copies decrypted data into user buffers; our leases avoid that adapter copy. It is reference code, not performance proof. |
| [Asio SChannel encryption](https://github.com/laudrup/boost-wintls/blob/5a16a857e03b8d75d0aed8a5ad918bca442dbe27/include/wintls/detail/encrypt_buffers.hpp) and [decryption](https://github.com/laudrup/boost-wintls/blob/5a16a857e03b8d75d0aed8a5ad918bca442dbe27/include/wintls/detail/sspi_decrypt.hpp) | Reusable header/data/trailer storage; incomplete records and extra data must be handled. Removing .NET `SslStream` does not remove those requirements. |
| [Microsoft ctsTraffic RIO](https://github.com/microsoft/ctsTraffic/blob/4468b8085b6ac58c531596216f10e812287174ea/ctsTraffic/ctsRioIocp.cpp) | CQ capacity and synchronized dequeue/notification rearming. It is a traffic tool, not a native TLS server. Our worker-owned CQ/RQ design already avoids its shared-CQ lock. |
| [RIOSend flags](https://learn.microsoft.com/windows/win32/api/mswsock/nc-mswsock-lpfn_riosend) | Deferred submission is per socket RQ, not a magic cross-connection flush; with one outstanding record per connection it offers no immediate batch to exploit. It was not added. |
| [SChannel shutdown](https://learn.microsoft.com/windows/win32/secauthn/shutting-down-an-schannel-connection) | Apply shutdown control token, generate/send `close_notify`, retain context/buffers until I/O completes. |
| [AcceptSecurityContext concurrency](https://learn.microsoft.com/windows/win32/secauthn/acceptsecuritycontext--schannel) | Concurrent calls using the same context are prohibited. The executor transfers exclusive ownership; it does not parallelize one connection's handshake. |

The implementation retained reusable encrypted storage and final-record coalescing. A separate experiment posting receives while handshake-token output was pending made short-connection results worse and was reverted. The existing preposted `AcceptEx`, batched completion retrieval, synchronous-success skip and RIO notification mechanisms remain; no HTTP.sys replacement or kernel-TLS claim is made.

## Latest result: short medians reached parity

Fresh **three-round counterbalanced medians** with offload enabled, native Windows loopback. Same RSA-2048 fixture, TLS 1.3 / `TLS_AES_256_GCM_SHA384`, disabled resumption, HTTP/1.1 pipeline one and exact 1 KiB responses. Short requests still wait for TLS EOF and TCP EOF; no protocol/close shortcut was introduced.

These measurements predate the later opt-in handshake-prefix callback and native-send trace used by the console demos. Those additions were not rebenchmarked; prefix observation and trace logging are disabled by default.

Four server physical cores (`0,2,4,6`), eight disjoint client cores (`16,18,...,30`); 32 short lanes or 256 keep-alive connections; **5s warmup + 8s measurement**. Same Release binaries/runtime `.NET 11.0.0-rc.1.26453.108`; diagnostic timing disabled. All 30 included arms have zero warmup/request/teardown/native I/O errors, close timeouts and remaining ownership. These are achieved loopback rates, not physical-NIC/server-capacity claims.

| Traffic | Stock Kestrel | IOCP + SslStream | IOCP native TLS | Native vs stock / wrapper | RIO + SslStream | RIO native TLS | Native vs stock / wrapper |
|---|---:|---:|---:|---:|---:|---:|---:|
| TLS short | 1,918 | 1,935 | 1,944 | +1.4% / +0.5% | 1,947 | 1,980 | +3.2% / +1.7% |
| TLS keep-alive | 91,693 | 90,672 | 92,791 | +1.2% / +2.3% | 100,236 | 102,507 | +11.8% / +2.3% |

**Median parity is not a stable per-run +/-5% guarantee.** Native short ranges were 1,664-1,976 IOCP and 1,926-1,989 RIO. The slow IOCP arm was -19.6% against its same-round wrapper. Every arm remains included. Small keep-alive differences are not definitive.

A separate **same-binary off/on A/B**, three paired rounds with reversed ordering and the exact same certificate/load profile, isolates initial-call offload from the command-fairness change (present on both sides):

| Native provider, short | Direct initial call | Bounded offload | Median gain |
|---|---:|---:|---:|
| IOCP | 1,448 | 1,877 | +29.6% |
| RIO | 1,493 | 1,912 | +28.1% |

Median client-observed post-handshake request/response interval fell from 6.36 to 2.44 ms IOCP and 6.37 to 2.81 ms RIO. The RIO handshake interval itself did not improve (11.76 to 11.99 ms); scheduling other connections' response/close work is the benefit, not less TLS work. Do not compare absolute rates between this A/B, the fresh stock comparison and older snapshots.

Native HTTP receive-compaction copy bytes remained zero. Current native short allocation was about 13,175/13,324 bytes per IOCP/RIO request versus 16,319 for the wrappers; keep-alive 1,919 versus 2,063/2,060. Stock keep-alive remained lower at about 1,536. In the fresh campaigns the executor reached at most 8 slots, 24 pending short jobs or 177 during keep-alive setup; all submitted work completed.

Exact samples, profiles, binary/fixture hashes and ownership evidence: [native-handshake-offload-results-20261018.json](native-handshake-offload-results-20261018.json). All **42** fresh comparison/A/B arms passed strict error/drain checks. Raw logs remain in ignored `artifacts\native-short-offload-*`.

## Earlier inline-only snapshot

The original native implementation used only owner-thread handshakes: three counterbalanced rounds, the same protocol/payload/CPU budgets, but **3s warmup + 5s measurement** and earlier binaries. These historical rates are not the current result.

| Traffic | Stock Kestrel | IOCP + SslStream | IOCP native TLS | Native vs stock / wrapper | RIO + SslStream | RIO native TLS | Native vs stock / wrapper |
|---|---:|---:|---:|---:|---:|---:|---:|
| TLS short | 2,261 | 2,216 | 1,685 | -25.5% / -24.0% | 2,150 | 1,693 | -25.1% / -21.3% |
| TLS keep-alive | 79,068 | 84,246 | 91,566 | +15.8% / +8.7% | 94,910 | 98,459 | +24.5% / +3.7% |

This snapshot improved keep-alive but regressed churn despite lower CPU/allocation than the wrappers. [Original runs, counters, binary hashes and tuning screens](native-tls-results-20261018.json) remain unchanged; its table uses only `windows-native-tls-confirmed-20261018`. Metrics probes complete HTTP/TLS close exchanges instead of abruptly disposing keep-alive control connections; no errors are silently ignored.

## Limits and correctness boundary

Windows x64, IPv4 TCP server, one PEM leaf certificate/private key, TLS 1.3 only. A custom PEM-chain import API was not added. Machine TLS policy is not modified. Raw initial TLS-prefix observation is supported; arbitrary native pre-TLS upgrades, a complete ClientHello/SNI parser, ALPN/HTTP2, client certificates, certificate-selection callbacks, resumption, post-handshake renegotiation/key updates and production recovery are not implemented. TCP mode remains available unchanged; old wrapper paths are explicitly separate, not fallbacks.

Actual checks passed IOCP/RIO at 1 and 4 workers: authenticated publication, retained plaintext surviving 256 KiB later traffic, fragmented records, owned sends, exact 4 MiB withheld-reader output, peer TLS/TCP half-close with reply, RST during blocked output and disposal of incomplete handshakes/accepts. A real peer-close check failed on both providers without the close-record fix and passed with it: encrypted `close_notify` is never exposed as plaintext. A peer TCP EOF without TLS shutdown also reports an explicit I/O error while input remains open. Terminal native operations and application leases drain before contexts/pools are released.

The offload checks additionally start 64 real TLS clients, observe actual executor-start events, then dispose the listener. All four provider/worker combinations observed cancellation of a running initial-handshake job and drained jobs, native operations, contexts and leases. This is not a fabricated completion or a conditional pass when no job ran. Linux rebuilt through the area build script and both epoll/io_uring native-TLS console exchanges still passed; no Linux executor was added.
