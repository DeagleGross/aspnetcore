# Kestrel transport RPS comparison

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
