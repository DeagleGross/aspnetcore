# Windows IOCP / RIO: local results, October 6, 2026

The transports are implemented and their real-socket boundary checks pass. The throughput numbers below are **preliminary: one complete, error-free matched round**, not medians from a completed repeated benchmark campaign.

Further repetitions encountered Windows `WSAENOBUFS` (10055) in the load generator's bind/connect path. The runner rejects measured request errors rather than presenting those runs as clean results. Explicit binding, implicit binding, per-socket port-scalability, fresh source addresses, and cooldown experiments did not establish a reliably error-free sustained churn campaign. No Windows registry, TCP port-range, TLS protocol policy, or global ThreadPool setting was changed.

## Matched RPS table

Every entry below belongs to round 1 of `artifacts\windows-transports-final-20261006-v2`. All twelve arms had zero measured request errors and zero warmup errors. Both custom transports ended with zero outstanding native operations, connections, input leases, output leases, or graceful-close timeouts.

| Workload | Stock Windows Kestrel | Owning-worker IOCP | Registered-I/O RIO |
|---|---:|---:|---:|
| TCP, short connection | 11,852 | 12,979 (+9.5%) | 13,753 (+16.0%) |
| TCP, keep-alive | 102,054 | 107,401 (+5.2%) | 119,030 (+16.6%) |
| TCP + TLS, short connection | 2,678 | 2,719 (+1.6%) | 2,873 (+7.3%) |
| TCP + TLS, keep-alive | 110,472 | 112,041 (+1.4%) | 122,770 (+11.1%) |

These are achieved requests/second, rounded to integers. Percentages use the unrounded values and the stock arm of the same workload. Do not combine this round with later screens to manufacture a median or backend ranking.

Complete per-arm metrics and native ownership reports are preserved in [results-20261006.json](results-20261006.json). Raw server/client logs and rejected repetitions remain in ignored local `artifacts` directories.

## What was held fixed

Windows 11 Enterprise 10.0.26100, AMD Ryzen 9 7950X3D, 16 physical cores / 32 logical processors. The topology probe confirmed adjacent SMT pairs: `(0,1)`, `(2,3)`, and so on.

The server was restricted to logical processors `0,2,4,6`, one thread from each of four physical cores. Every server had `DOTNET_PROCESSOR_COUNT=4`; custom transports had four workers pinned to those same processors. The load generator was restricted to `16,18,20,22,24,26,28,30`, eight separate physical cores, with `DOTNET_PROCESSOR_COUNT=8`. No SMT sibling was shared between the server and client budgets. Affinity limits work placement; it does not reserve a machine otherwise shared with other work.

Runtime: `.NET 11.0.0-rc.1.26453.108`, commit `082832b3df0b41066458b86c91143d4c250b69fe`. SDK: `11.0.100-rc.1.26420.103`. All server arms used the same in-tree Release Kestrel/application build.

HTTP/1.1, pipeline depth one, a verified 1024-byte text response, 5 seconds warmup and approximately 10 seconds measurement per arm. Short mode used 64 concurrent lanes, one request per connection, and waited for TCP EOF. Long mode used 256 persistent connections.

The retained round used the asynchronous, explicitly bound, 64-source-address load-client variant before the later bind/connect diagnostics. The current runner includes those later diagnostic changes; its results should not be silently merged with the retained round.

Windows HTTPS used Kestrel `UseHttps` and `SslStream`/SChannel in **all three arms**, TLS 1.3, the same RSA-2048 fixture certificate, and disabled-resumption options on client and server. The observed cipher was `TLS_AES_256_GCM_SHA384`. This is not fd-bound OpenSSL, kTLS, or owner-thread buffer TLS.

The local machine's TLS 1.2 server setting is disabled. That also prevented stock Kestrel's initial TLS 1.2 handshake. TLS 1.3 was explicitly selected for Windows comparisons, not enabled through a machine-setting change. The PEM credential additionally needed PKCS#12/key-provider import for SChannel; the real-peer diagnostic failed with the ephemeral PEM key and passed with imported credentials.

## CPU, allocation, and latency prevent a stronger claim

| Workload | Stock server CPU cores | IOCP server CPU cores | RIO server CPU cores |
|---|---:|---:|---:|
| TCP short | 2.75 | 2.84 | 2.85 |
| TCP keep-alive | 3.74 | 3.86 | 3.84 |
| TLS short | 0.89 | 1.23 | 1.08 |
| TLS keep-alive | 3.81 | 3.86 | 3.89 |

Persistent runs approached the four-core server budget; short runs did not. Consequently the short table is not a server handshake-capacity or universal maximum-throughput measurement.

Persistent managed allocation was approximately 1,563 bytes/request for stock, 1,779 for custom TCP, and 2,001–2,002 for custom TLS. Short-connection allocation was somewhat lower in the custom transports, but the prototypes do not demonstrate a universal allocation win.

TLS-short p99 was about 32.9 ms stock, 47.6 ms IOCP, and 42.3 ms RIO in this round. Higher RPS therefore did not mean universally better tail latency. Persistent TLS p99 was about 5.03 ms stock, 4.14 ms IOCP, and 3.54 ms RIO.

The load generator is a managed closed-loop client, not wrk2, and both processes use Windows loopback. Crypto differs from the earlier OpenSSL/Linux experiments. Do not compare absolute RPS with WSL results, infer physical-NIC scaling, or claim that these numbers reproduce the official Crank workload.

## Correctness evidence

`windows-check.ps1` passed IOCP and RIO at 1, 2, and 4 workers against the final source. Each case reached actual four-page input backpressure, used a 4 KiB send-buffer pressure setting, and ended with all native ownership counters zero.

The checks exercise pending-read cancellation and recovery, examined-but-unconsumed prefixes, 256 KiB input, exact 4 MiB output with a withheld reader, response after peer send-half-close, peer RST during blocked output, ephemeral-port binding, and listener disposal with queued idle peers and pending accepts.

An early cursor implementation failed on both actual IOCP and RIO receives because sequence offsets were treated as relative instead of based on the segment running index. The corrected snapshot/origin handling passed the same wire-driven assertions.

The initial close path released the RIO socket/RQ immediately after final send completion and produced reset-before-response failures in short HTTP churn. The revised path sends FIN, retains the socket/RQ through peer EOF, and drains cancellation/completions before releasing storage. Subsequent valid short runs had zero request resets. Later `WSAENOBUFS` errors were observed in load-client bind/connect, not silently reclassified as successful requests.

No partial native send was observed in the retained counters. The implementation handles a short completion, but the measured pressure checks establish pending-send ownership/backpressure, not proof that Windows produced a partial send row.

## Reproduction

From the repository root:

```powershell
. .\activate.ps1
dotnet build src\Servers\Kestrel\samples\NetworkProtoSample\NetworkProtoSample.csproj -c Release -p:UseIisNativeAssets=false
dotnet build src\Servers\Kestrel\testassets\WindowsTransportLoad\WindowsTransportLoad.csproj -c Release -p:UseIisNativeAssets=false
.\src\Servers\Kestrel\samples\NetworkProtoSample\scripts\windows-check.ps1 -SkipBuild
.\src\Servers\Kestrel\samples\NetworkProtoSample\scripts\windows-bench.ps1 -SkipBuild -Repetitions 3 -Seconds 8 -Warmup 4 -Connections 256 -ShortConnections 64 -ShortCooldownSeconds 60
```

Adjust CPU lists to your physical topology. The script preserves error details and rejects measured failures. Completing a clean repeated short-connection matrix on this machine remains unverified; the existence of the command is not evidence that it completed.

The checked-in API is still the small non-shipping `UseIocp` / `UseRio` registration surface, not a shipped `System.Net.Transport` implementation.
