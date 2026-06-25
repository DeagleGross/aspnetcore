# DirectSsl Transport Sample

This sample demonstrates the experimental DirectSsl transport, which uses native OpenSSL for TLS instead of SslStream.
## Setup

Generate test certificates before running:

```bash
./generate-cert.sh
```

This creates:
- `server-p384.key` - ECDSA P-384 private key
- `server-p384.crt` - Self-signed certificate
- `server-p384.pfx` - PKCS#12 bundle for comparison mode

## Running

```bash
# Using DirectSsl transport (OpenSSL)
dotnet run

# Using standard Kestrel TLS (SslStream) for comparison
USE_STANDARD_TLS=1 dotnet run
```

## Selecting the DirectSsl engine (A/B comparison)

The DirectSsl transport ships with two interchangeable TLS engines. Pick one
per run with the `DIRECTSSL_ENGINE` environment variable:

| Value | Engine | What it uses |
| --- | --- | --- |
| `TlsSession` *(default)* | `DirectSslEngineKind.TlsSession` | `System.Net.Security.TlsContext` / `TlsSession` state machine from `dotnet/runtime` |
| `OpenSslDirect` | `DirectSslEngineKind.OpenSslDirect` | Direct `libssl` P/Invoke calls from aspnetcore (resurrected prior-approach prototype) |

```bash
# TlsSession engine (default — same as omitting the variable)
DIRECTSSL_ENGINE=TlsSession dotnet run -c Release

# OpenSslDirect engine
DIRECTSSL_ENGINE=OpenSslDirect dotnet run -c Release
```

The startup banner prints which engine was selected, e.g.
`Using DirectSsl transport — engine = OpenSslDirect (DIRECTSSL_ENGINE=OpenSslDirect)`.

## Benchmark scripts (`scripts/`)

| Script | Scenario |
| --- | --- |
| `wrk-persistent.sh` | HTTP keep-alive — steady-state record encrypt/decrypt |
| `wrk-close13.sh` | ~1 in 3 requests forces a fresh TLS handshake |
| `wrk-closeall.sh` | Every request forces a fresh TLS handshake |
| `run-ab.sh` | Full TlsSession vs OpenSslDirect sweep across all 3 scenarios; prints a side-by-side RPS / latency summary |

```bash
# Build once
dotnet build -c Release

# One-shot end-to-end A/B (start/stop server per cell, summarize at end)
./scripts/run-ab.sh                # default 30s, 4 threads, 200 connections
./scripts/run-ab.sh 60 8 800       # custom: duration, threads, connections
```
