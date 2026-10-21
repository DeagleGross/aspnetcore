# Console demos

These are small, one-connection demos of the experimental public transport API, not benchmarks or general HTTP servers. Every app runs a real loopback client and server in one process, prints each important step, verifies its fixed bytes and exits. The server uses `NetworkEngine` / `NetworkConnection` directly, **without Kestrel** (since the 2026-10-21 layering inversion, providers publish a Kestrel-free connection and no `ConnectionContext` is allocated for these apps). The client uses `TcpClient` because the prototype does not expose a public connect API. `NativeTlsHttp1` also prints `connection.TlsInfo`, the provider-reported protocol and cipher.

| App | Open / breakpoint | What happens |
|---|---|---|
| [`PlainHttp1`](PlainHttp1/Program.cs) | `ReceiveAsync`, `AdvanceReceiveTo`, `SendAsync` | A hardcoded HTTP/1.1 GET and 200 response, including a response after the client send-half-close. |
| [`RetainedPages`](RetainedPages/Program.cs) | `FrameCodec.TryReadHeader`, `Retain`, `AdvanceReceiveTo`, `EnqueueSend`, `NativeSendObserver.Verify` | Decode a small header directly from the receive sequence, retain its 24 KiB payload, consume 128 KiB of later traffic, then serialize new metadata and send the original payload pages. Assert both receive-page aliasing and actual native send addresses. |
| [`PeekTlsHttp1`](PeekTlsHttp1/Program.cs) | `NativeTls.HandshakePrefixCallback`, then `connection.IsTls` / `ReceiveAsync` | Observe real raw ClientHello-prefix bytes before native TLS processes them, consume none, then exchange authenticated HTTP. No server `SslStream`. |
| [`PrefixTlsHttp1`](PrefixTlsHttp1/Program.cs) | `connection.Output`, prefix advancement, TLS handoff | Send a plaintext greeting, consume `PROTO/1\r\n`, preserve the prefetched ClientHello, then exchange HTTP over TLS. |
| [`NativeTlsHttp1`](NativeTlsHttp1/Program.cs) | `ListenAsync(... NativeTls ...)`, `connection.IsTls`, `ReceiveAsync` | The transport establishes TLS and exposes plaintext; the server never constructs a `SslStream`. Linux uses OpenSSL/TLS1.2; Windows uses direct SChannel/TLS1.3. |

The HTTP bytes, greeting and prefix are in the linked `Shared\HttpPayloads.cs` file. Only setup, fixed-fixture verification and failure cleanup are shared; each app's `Program.cs` shows the scenario-specific transport calls.

## Four-provider coverage

| Scenario / app | epoll (Linux) | io_uring (Linux) | IOCP (Windows) | RIO (Windows) |
|---|---|---|---|---|
| Plain TCP / `PlainHttp1` | Passed | Passed | Passed | Passed |
| Native HTTPS / `NativeTlsHttp1` | Passed | Passed | Passed | Passed |
| Raw prefix before native TLS / `PeekTlsHttp1` | Passed | Passed | Passed | Passed |
| Leased framed payload / `RetainedPages` | Passed | Passed | Passed | Passed |

Use `epoll`, `uring`, `iocp` or `rio` as the first argument; `Shared\DemoSettings.cs` maps these to the four public engine-options types. Unsupported OS/provider pairs fail rather than falling back. Linux native TLS is currently OpenSSL/TLS 1.2; Windows is direct SChannel/TLS 1.3.

For fd-bound Linux TLS (epoll, or io_uring with `NETWORKPROTO_URING_TLS=fd`) the observer uses `recv(MSG_PEEK)` before the first OpenSSL handshake call; the default ring-owned io_uring mode observes the first received ciphertext page instead. Windows observes its existing ciphertext storage before the initial SChannel call. The synchronous callback sees a borrowed, possibly fragmented prefix, **not a parsed or necessarily complete ClientHello**. Do not retain it or block the owning I/O worker. This is observation, not STARTTLS, certificate selection or an arbitrary native TLS upgrade.

## What the Orleans-style page demo proves

`FrameCodec` is deliberately a tiny framing codec, not Orleans serialization. It reads a 12-byte header with `SequenceReader<byte>` without flattening the receive sequence. The server retains every payload-page slice, advances input, consumes later traffic, writes a new reply header with request ID 43 instead of 42, and enqueues the original payload. Caller references are released before asynchronous native sends drain.

`NativeSendObserver` compares the retained ranges with trace events at the actual `send`, io_uring send submission, `WSASend` or `RIOSend` boundary. In these runs, all four providers sent the **same two receive-page ranges: 16,372 + 8,204 = 24,576 payload bytes**, without a payload staging copy; other TCP fragmentation can produce different slice lengths/counts. RIO can reuse its registered receive storage for sends. A temporary negative control copied the payload to a different output allocation: the client still received correct bytes, but the address assertion failed on every provider. Dropping the native prefix callback likewise failed the TLS demo on every provider; restored implementations passed all 16 combinations.

This proves **zero extra server application/transport bulk-payload copies for this plain-TCP framing/forwarding path**, not zero allocations or kernel/NIC zero-copy. Leases, sequence nodes, trace metadata and the newly generated header still have costs; the separate test client allocates its verification buffers. Ordinary DTO deserialization may allocate strings/arrays, and arbitrary reserialization may rewrite the payload. A serializer must accept segmented input and preserve a borrowed/retained binary payload to obtain this benefit. No Orleans dependency or binary-format compatibility is claimed. Native TLS can expose leased plaintext, but TLS processing/encrypted output is not this copy-free TCP send path.

**Size/lifetime limit:** the prototype pauses a connection after four live receive pages, including retained ones. Holding all four can prevent further input. This demo deliberately leaves room for later traffic and reports that limit explicitly if fragmentation fills the window. Large Orleans-style messages would need a reviewed retained-byte/page budget or a streaming lifetime strategy; the 24 KiB demonstration does not establish arbitrary message-size support.

## Run

From the repository root on Windows:

```powershell
. .\activate.ps1
$samples = 'src\Servers\Kestrel\Transport.Networking\samples'
dotnet run --project "$samples\PlainHttp1\PlainHttp1.csproj" -p:UseIisNativeAssets=false -- iocp
dotnet run --project "$samples\RetainedPages\RetainedPages.csproj" -p:UseIisNativeAssets=false -- rio
dotnet run --project "$samples\PeekTlsHttp1\PeekTlsHttp1.csproj" -p:UseIisNativeAssets=false -- iocp
dotnet run --project "$samples\PrefixTlsHttp1\PrefixTlsHttp1.csproj" -p:UseIisNativeAssets=false -- rio
dotnet run --project "$samples\NativeTlsHttp1\NativeTlsHttp1.csproj" -p:UseIisNativeAssets=false -- rio
```

To exercise all four requested scenarios on Windows:

```powershell
foreach ($backend in 'iocp', 'rio')
{
    foreach ($app in 'PlainHttp1', 'NativeTlsHttp1', 'PeekTlsHttp1', 'RetainedPages')
    {
        dotnet run --project "$samples\$app\$app.csproj" -p:UseIisNativeAssets=false -- $backend 5016 30
        if ($LASTEXITCODE -ne 0) { throw "$backend/$app failed." }
    }
}
```

On Linux, activate `activate.sh` and run the same projects with `epoll` or `uring`. The current WSL checkout has liburing installed under `~/.local` and the earlier checkout-local development package under `artifacts`; a first Debug build must expose those headers/libraries too:

```bash
source activate.sh
export CPATH="$HOME/.local/include:$PWD/artifacts/networkproto-deps/usr/include"
export LIBRARY_PATH="$HOME/.local/lib:$PWD/artifacts/networkproto-deps/usr/lib/x86_64-linux-gnu"
export LD_LIBRARY_PATH="/opt/openssl-3.5.8/lib:$HOME/.local/lib"
dotnet run --project src/Servers/Kestrel/Transport.Networking/samples/PeekTlsHttp1/PeekTlsHttp1.csproj -- uring
```

Use your system installation instead where appropriate. With those environment variables set, run the full Linux matrix:

```bash
for backend in epoll uring; do
    for app in PlainHttp1 NativeTlsHttp1 PeekTlsHttp1 RetainedPages; do
        dotnet run --project "src/Servers/Kestrel/Transport.Networking/samples/$app/$app.csproj" -- "$backend" 5016 30 || exit
    done
done
```

For the repository's area-build entry point, select these projects with an absolute path:

```bash
./src/Servers/Kestrel/build.sh --build --projects "$PWD/src/Servers/Kestrel/Transport.Networking/samples/*/*.csproj" --configuration Debug --no-build-native --no-build-nodejs --no-build-java
```

Arguments are `[backend] [port] [timeout-seconds]`. Backend defaults to IOCP on Windows and epoll on Linux. Ports default to 5011/5012/5013/5014/5015 respectively and must be free. Each engine uses one worker on logical CPU 0.

The demo cancellation timeout defaults to **disabled**. Providers still have their own handshake/close deadlines, so a long debugger pause can trigger a provider timeout on resuming. For a bounded command, for example:

```powershell
dotnet run --project "$samples\PrefixTlsHttp1\PrefixTlsHttp1.csproj" -- iocp 5014 30
```

TLS credentials are generated per run; no certificate installation, PEM arguments or machine TLS-policy changes are needed. `NativeTlsHttp1` and `PeekTlsHttp1` write generated fixtures to uniquely created temporary directories for the path-based native listener contract, then delete the two files/directory. Windows credentials use the working key-provider import.

## Debug

Open an app's `.csproj` in Visual Studio and run it with F5; no solution-file changes are required. The default build is Debug. Use the application arguments to select another backend, or open the `samples` folder in your .NET debugger and launch the chosen project.

Step through `RunServerAsync`. `Shared\OneConnectionDemo.cs` owns the real accept and error cleanup; the client can progress concurrently while the server is paused. Inspect `received.Buffer` only before advancement, and `message.Sequence` only while its retained owner is alive.

**Start with `NativeTlsHttp1` or `PeekTlsHttp1` for transport-owned TLS.** Only `PrefixTlsHttp1` remains a separate `SslStream` compatibility demo for a greeting/application-prefix upgrade; it does not claim native TLS. After that wrapper handoff, use only the returned TLS stream. Its greeting uses pipe output because direct-send mode cannot later be mixed with stream/TLS output.

The public-only signatures are in `..\PUBLIC-API.md`; provider-only protected hooks and implementation details are intentionally not included there.
