# Hybrid DirectSSL Engine — TlsSession Adoption Bisection

## Goal

Answer a customer report of **~10% RPS regression on persistent connections** when
switching from raw OpenSSL P/Invoke (`OpenSslDirect` engine, "OSD") to the new
`TlsSession` / `TlsContext` public API from
[dotnet/runtime#127928](https://github.com/dotnet/runtime/issues/127928).

The `Hybrid` engine in this folder is a step-by-step port of `OpenSslDirect`. Each
git commit swaps ONE raw P/Invoke for its `TlsSession` / `TlsContext` equivalent,
so we can benchmark each individual swap in isolation and localize the regression.

## Test rig

All measurements are taken with:

* **Client**: `wrk -t1 -c4 -d15s --timeout 60s https://localhost:5001/`
  (1 wrk thread, 4 persistent HTTPS keep-alive connections, 15-second window)
* **Server**: `DIRECTSSL_WORKERS=1 DIRECTSSL_ENGINE={OpenSslDirect|Hybrid}`
  (single epoll pump thread pinned to CPU 0)
* **Endpoint**: `GET https://localhost:5001/` returning a small (~13-byte) plaintext body

The bench harness lives in the DirectSslTransportApp sample folder.

### Normalizing raw wrk output

wrk reports `Requests/sec = requests / last_completed_request_time`. When all
N connections time out simultaneously, wrk's denominator shrinks and raw RPS is
inflated by 10-20% for whichever engine had timeouts. **Always normalize** by
`raw_RPS × actual_duration / 15s` to get true effective RPS.

## The steps

Each step adds ONE change on top of the previous.

| # | Change | Files touched |
|---|--------|---------------|
| 0 | Reset `Hybrid` to a byte-for-byte clone of `OpenSslDirect` (only namespace differs). Establishes a baseline where Hybrid and OSD are identical, so any subsequent divergence is attributable to the specific TlsSession change we made. | all `Engines/Hybrid/*.cs` |
| 1 | `SslContext` (Hybrid) wraps a `TlsContext` and reflection-extracts `SSL_CTX*`. All later steps still pass the raw `SSL_CTX*` to `SSL_new` so this is an inert wrapper — verifying that just importing `TlsContext` costs nothing. | `Ssl/SslContext.cs` |
| 2 | `TlsSession.Create(TlsContext, SafeSocketHandle)` replaces raw `SSL_new` + `SSL_set_fd` + `SSL_set_accept_state` in the accept loop. Reflection extracts the internal `SafeSslHandle` so downstream calls still use a raw `SSL*` pointer. | `SslEventPump.cs`, `SslEventPumpPool.cs`, `SslConnectionState.cs` |
| 3 | `session.Handshake()` replaces the raw `SSL_do_handshake` in `TryAdvanceHandshake`. In fd-mode `TlsSession.Handshake` internally calls the same `SSL_do_handshake` via `TryFastHandshake`, so the number of native calls per epoll wake is identical to Step 2. | `SslEventPump.cs` |
| 4 | **`_session!.Read(buffer.Span, out int bytesRead)` replaces raw `SSL_read` in `DoSslRead`.** This is the hot path — every EPOLLIN wake triggers a tight read loop until WANT_READ. | `SslConnectionState.cs` |
| 5 | `_session!.Write(buffer.Span, out int bytesWritten)` replaces raw `SSL_write` in `DoSslWrite`. Called once per response. | `SslConnectionState.cs` |

## Results (n=9 iterations each, normalized)

| Step | Delta | Hyb / OSD | SD | Distribution (per-fd %) | Verdict |
|------|-------|-----------|------|--------------------------|---------|
| 1 | wrap TlsContext | 101.4% | — | fair | flat |
| 2 | TlsSession.Create for SSL* | 99.1% | — | fair | flat |
| 3 | session.Handshake | 99.3% | 2.8% | fair | flat |
| **4** | **session.Read** | **47.3%** | 1.4% | 25.4 / 24.3 / 23.9 / 23.9 | **🎯 -53% RPS** |
| 5 | session.Write (Read raw) | 100.1% | 1.8% | 25.0 / 25.0 / 25.0 / 24.9 | flat |

Per-run at Step 4: 47.6, 45.7, 48.5, 45.9, 46.5, 46.6, 46.7, 47.9, 50.3.
Extremely tight distribution, zero overlap with any other step. **Hybrid runs at HALF speed.**

## Why Read but not Write?

Both `TlsSession.Read` and `TlsSession.Write` are structurally identical:
same entry checks, same `TryFast*` pattern, same `SafeSslHandle`-based `LibraryImport`
P/Invoke, same `MapSslError`. The two C shims (`CryptoNative_SslRead` / `SslWrite`)
are byte-for-byte mirrors.

**The asymmetry comes from the calling pattern, not the code.**

From OSD baseline diag (Step 4 run):

```
TotalReadCallCount:  1,387,558   Complete=696,504  WantRead=676,277   → ~50% return WANT_READ
TotalWriteCallCount: 1,382,298   Immediate=1,382,298  WantWrite=0      → 100% succeed first try
```

Per-call `TlsSession` overhead (SafeHandle DangerousAddRef/Release interlocked ops,
entry-check tax, `Nullable<TlsOperationStatus>` ref-passing, `MapSslError` switch,
plus the aspnetcore-side status → int conversion) is paid on EVERY call — including
the 676K/sec unproductive WANT_READ fail-fasts. In the raw path a WANT_READ return
is essentially free (one `SSL_get_error`, break). In `TlsSession.Read` the full
tax is paid whether or not decrypted bytes are produced.

Writes never pay that tax on unproductive calls because writes always succeed
immediately for small HTTP responses.

## Reproducing

```bash
# Build
source ./activate.sh
./src/Servers/Kestrel/build.sh -c Release

# Bench one step 9 times (writes to ~/bench/results_stepN/)
~/bench/runN2.sh step3 9

# For step 4 (the regression): apply the read swap first, rebuild, then bench.
# See DoSslRead in SslConnectionState.cs — swap the raw SSL_read block for
# the TlsSession.Read block shown in the git history of this branch.

# Tabulate + normalize all steps
python3 ~/bench/tab3.py
```

## Related issues / branches

* API proposal: [dotnet/runtime#127928](https://github.com/dotnet/runtime/issues/127928)
* Runtime bench (for isolated per-API microbench): `dmkorolev/tls/persistent-bench` in dotnet/runtime
  (commit `45f71bb5f58` — EpollFairnessBench)
* This branch: `dmkorolev/internal/tls-comparison-prototypes` in this fork

## Next actions

1. Modify runtime `EpollFairnessBench` to do request-response (echo N bytes,
   wait, echo again) instead of bulk data. Hypothesis: forcing the same ~50%
   WANT_READ ratio should reproduce the 50% regression outside aspnetcore.
2. BDN micro-bench: `SSL_read` (unsafe P/Invoke) vs
   `Interop.Ssl.SslRead(SafeSslHandle, ...)` vs `TlsSession.Read` on a
   pre-established `SSL*` that always returns WANT_READ. Quantifies per-call cost.
3. If confirmed: propose a `TlsSession.ReadDirect(IntPtr sslPtr, ...)` /
   equivalent that bypasses SafeHandle refcount for callers who guarantee
   `SSL*` lifetime externally (e.g. Kestrel's DirectSsl transport).
