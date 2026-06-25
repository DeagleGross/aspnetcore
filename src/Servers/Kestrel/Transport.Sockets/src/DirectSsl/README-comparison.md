# DirectSsl Engine Comparison Prototype

> Branch: `dmkorolev/internal/tls-comparison-prototypes` (worktree only — never push)
>
> Parent: `dmkorolev/internal/native-tls-transport` @ `04150aebf3`

## Why this branch exists

We saw a ~10% regression on the **persistent-connection** scenario after migrating
the DirectSsl transport from direct OpenSSL P/Invoke ("Net10 Private") to
`System.Net.Security.TlsContext` / `TlsSession` ("This run") — even though Close-All
and Close-1/3 stayed at parity. This branch lets both implementations coexist in a
single binary so we can A/B them on the same machine, same kernel, same OpenSSL,
same workload.

There is no shared dispatch layer. Each engine has its own listener, its own pump
pool, and its own per-connection state. The hot path stays monomorphic — exactly
the way each implementation looked when it was the only one in the tree.

## Two engines, one factory

| Engine | Source | Code lives in |
|---|---|---|
| **TlsSession** *(default)* | HEAD — calls `System.Net.Security.TlsContext` / `TlsSession` (the runtime PoC) | `src/Servers/Kestrel/Transport.Sockets/src/DirectSsl/{Connection,Ssl,SslConnectionState.cs,SslEventPump.cs,SslEventPumpPool.cs,…}` |
| **OpenSslDirect** | Resurrected from commit `82e1b108848` (the "Net10 Private" baseline). Calls `SSL_*` directly from aspnetcore via `libssl.so.3` P/Invoke | `src/Servers/Kestrel/Transport.Sockets/src/DirectSsl/Engines/OpenSslDirect/**` |

The selector lives in `DirectSslTransportFactory.cs` and is driven by two inputs:

1. `KESTREL_DIRECTSSL_ENGINE` environment variable — wins, so bench scripts can
   flip engines without rebuilding.
2. `DirectSslTransportOptions.Engine` (`DirectSslEngineKind` enum) — defaults to
   `TlsSession`.

```bash
KESTREL_DIRECTSSL_ENGINE=tlssession     dotnet run ...
KESTREL_DIRECTSSL_ENGINE=openssldirect  dotnet run ...
```

The factory logs the chosen engine at startup:

```
info: ...DirectSslTransportFactory[0]
      DirectSsl engine selected: OpenSslDirect
info: ...DirectSslTransportFactory[0]
      [OpenSslDirect] SslContext initialized with certificate: /home/.../server-p256.crt
info: ...DirectSslTransportFactory[0]
      [OpenSslDirect] event pump pool started with 4 pumps.
```

## Counters

Both engines already declared a near-identical set of `public static long Total*`
counters on their respective `SslEventPump` — registers, accepts, write/read EOFs,
each variant of `SSL_ERROR_*`, etc. They are **gated behind the
`DIRECTSSL_DEBUG_COUNTERS` compile constant** so the release hot path is unaffected.

### Enable counters

Either uncomment line 5 of `SslEventPump.cs` (in each engine) or pass the constant
at build time:

```bash
dotnet build -c Release -p:DefineConstants='DIRECTSSL_DEBUG_COUNTERS;TRACE'
```

The sample app already prints per-pump totals every few seconds via
`Console.WriteLine` from `SslEventPump`'s status loop (line ~263). When counters
are enabled, those lines start populating with real numbers.

### Side-by-side dump

`DirectSslMetrics.DumpComparison()` (in
`src/Servers/Kestrel/Transport.Sockets/src/DirectSsl/DirectSslMetrics.cs`) reads
the static counters from BOTH engines' pump types and formats them in two columns
for direct comparison. Call it from the sample app's shutdown hook or expose it
on an HTTP endpoint.

```csharp
Console.WriteLine(DirectSslMetrics.DumpComparison());
```

The output looks like:

```
================ DirectSsl engine comparison ================
Counter                          TlsSession     OpenSslDirect
------------------------------------------------------------------------
TotalWriteEof                             0                 0
TotalReadEof                             27                42
TotalWriteErrors                          0                 0
TotalReadErrors                           0                 0
TotalSslErrorSyscall                     12                23
TotalRequestsCompleted              280,154           281,917
…
============================================================
```

## How to A/B benchmark (suggested loop)

The branch only RUNS on Linux/WSL — the OpenSslDirect engine P/Invokes
`libssl.so.3` directly (it builds on Windows but cannot bind). Build on the
WSL/VM environment that already has the runtime overlay (the patched
`System.Net.Security.dll` is required only for the `TlsSession` engine; the
`OpenSslDirect` engine has no dependency on the overlay).

```bash
# 1. Build once with counters enabled
dotnet build -c Release \
    -p:DefineConstants='DIRECTSSL_DEBUG_COUNTERS;TRACE' \
    src/Servers/Kestrel/samples/DirectSslTransportApp

# 2. Run engine A
KESTREL_DIRECTSSL_ENGINE=tlssession \
    dotnet run -c Release --no-build \
    --project src/Servers/Kestrel/samples/DirectSslTransportApp &
SERVER_PID=$!
sleep 2
wrk -t4 -c100 -d60s --latency https://localhost:5001/
kill $SERVER_PID
# (the shutdown log prints DirectSslMetrics.DumpComparison())

# 3. Run engine B
KESTREL_DIRECTSSL_ENGINE=openssldirect \
    dotnet run -c Release --no-build \
    --project src/Servers/Kestrel/samples/DirectSslTransportApp &
SERVER_PID=$!
sleep 2
wrk -t4 -c100 -d60s --latency https://localhost:5001/
kill $SERVER_PID
```

For deeper attribution use `strace -c -p <pid>` (syscall histogram diff),
`perf record --call-graph dwarf` (flamegraph), and `dotnet-trace collect
--providers Microsoft-Diagnostics-DiagnosticSource:0xFFFFFFFFFFFFFFFF:5
--profile cpu-sampling` (managed allocations).

## What this branch deliberately does NOT do

- No polymorphic dispatch between engines. Engine selection happens once at
  startup; the hot path has zero added branches.
- No shared per-connection state class. Each engine kept its own
  `SslConnectionState`, its own awaitables, its own pump.
- No edits to either engine's hot path. The OpenSslDirect engine is a verbatim
  resurrection of `82e1b108848` with only namespaces rewritten to live under
  `…DirectSsl.Engines.OpenSslDirect`.

This is what "apples to apples" means here — the same behaviour each engine had
when it was the only one in the tree.

## File layout

```
src/Servers/Kestrel/Transport.Sockets/src/DirectSsl/
├── DirectSslEngineKind.cs            ← NEW enum
├── DirectSslMetrics.cs               ← NEW aggregator
├── DirectSslTransportFactory.cs      ← rewritten to switch on engine
├── DirectSslTransportOptions.cs      ← +Engine property
├── Connection/                       ← TlsSession engine listener
├── Ssl/                              ← TlsSession engine ssl wrappers
├── SslConnectionState.cs             ← TlsSession engine state
├── SslEventPump.cs                   ← TlsSession engine pump
├── SslEventPumpPool.cs               ← TlsSession engine pump pool
└── Engines/
    └── OpenSslDirect/                ← resurrected from 82e1b108848
        ├── Connection/DirectSslConnectionListener.cs
        ├── Interop/{NativeSsl.cs,OpenSsl.cs,NativeLibc.cs,Models.cs}
        ├── Ssl/SslContext.cs
        ├── SslAwaitable.cs
        ├── SslConnectionState.cs
        ├── SslEventPump.cs
        ├── SslEventPumpPool.cs
        └── SslException.cs
```
