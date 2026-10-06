# Windows IOCP transport, explained simply

This is a non-shipping Kestrel experiment. It replaces the TCP transport, not Kestrel's HTTP implementation. It uses real Windows overlapped Winsock I/O and dedicated C# workers. There is no C shim and no `Socket.ReceiveAsync`/`Socket.SendAsync` data-path wrapper.

The [local comparison and its limits](../Windows/RESULTS.md) include stock/IOCP/RIO short and keep-alive TCP/TLS results.

## What IOCP means

**IOCP = I/O Completion Port.** Think of it as a mailbox containing receipts for finished I/O.

An ordinary blocking receive says, "wait here until bytes arrive." An overlapped receive says, "put incoming bytes in this memory and tell my completion port when finished." Windows can do the waiting without keeping an application thread blocked for each connection.

The important difference from epoll is the notification's meaning. Epoll says, "the socket may be readable; try a receive." IOCP normally says, "the receive you already requested has finished; here are its byte count and status."

An `OVERLAPPED` is the native bookkeeping block for one outstanding operation. It is not the received data. We keep that block and the data's memory alive until the operation's terminal completion, including during cancellation.

**Stock Kestrel already uses IOCP on Windows.** Its sockets transport uses `System.Net.Sockets`, and the .NET runtime owns the Windows completion machinery and dispatch. This experiment does not introduce IOCP to Kestrel. It changes who owns completion processing, buffer lifetime, and output scheduling.

## How this implementation is arranged

```text
one listening socket
  -> 32 preposted AcceptEx requests on worker 0
  -> accepted sockets assigned round-robin to N workers

one owning worker per connection
  -> WSARecv / WSASend
  -> worker-local immediate completions OR IOCP completion batch
  -> leased input pages / direct output-pipe drain

normal Kestrel connection pipeline
  -> optional pre-TLS middleware
  -> optional UseHttps / SslStream / SChannel
  -> HTTP/1.1 and the application
```

Each worker has its own completion port. A connected socket is associated with exactly one worker's port for its lifetime. Windows does not provide the Linux prototype's reuse-port listener arrangement here; there is one acceptor, with round-robin handoff.

`AcceptEx` takes a socket created in advance. The accept request asks for zero initial payload bytes, so it finishes when the connection arrives rather than waiting for the client to send data. We then apply `SO_UPDATE_ACCEPT_CONTEXT` and `TCP_NODELAY`, and give the socket to its owning worker.

The worker drains a bounded amount of managed commands, handles locally completed operations, reaps up to 128 IOCP entries, and checks shutdown deadlines. Application threads enqueue commands and wake the port using `PostQueuedCompletionStatus`; wakes are coalesced. They do not perform native socket I/O themselves.

Kernel completions are dispatched directly. They do not make a detour through the managed command queue.

## Input, output, and memory

By default, an idle connection has a zero-byte `WSARecv` pending. Its completion indicates that a real receive can make progress; the following positive-sized receive determines the actual data or EOF. This avoids reserving an input page on every idle connection, at the cost of an extra receive operation.

Actual data is received into stable native memory. A custom `PipeReader` exposes those already-filled pages directly to Kestrel. `AdvanceTo` returns fully consumed pages to the owner. Unconsumed bytes and the examined cursor are preserved. At most four 16 KiB pages are exposed per connection before receives pause.

Output uses an ordinary `Pipe`, backed by the native memory pool. Its inline read notification only enqueues worker work; it does not run the application on the worker. The worker drains the output pipe, submits one segment at a time, handles partial completion, and keeps that segment pinned/owned until completion. Application continuations remain on the ThreadPool.

If a `WSARecv` or `WSASend` finishes synchronously, `FILE_SKIP_COMPLETION_PORT_ON_SUCCESS` suppresses an unnecessary kernel completion packet. The worker queues that result locally and processes it with the same ownership accounting. Pending operations still finish through IOCP. This avoids recursive completion processing and does not run arbitrary caller code inline.

Pools grow in 4 MiB slabs. Input is bounded at 32 MiB per worker; output at 128 MiB per worker. These are prototype limits, not general framework policy. Exhaustion is explicit rather than a silent fallback. A native address being stable does not mean that the kernel/network path is zero-copy.

## Closing a connection

Peer input EOF does not immediately destroy the output side: the application can still send its response.

For a normal completed output, the worker sends TCP FIN with `shutdown(SD_SEND)` after all output sends complete. It keeps the socket alive until peer EOF, with a five-second bound. This matters especially to the shared RIO path: releasing send memory is not the same as the peer receiving the response, and closing the socket/request queue immediately caused rare reset-before-response failures during real short-connection load.

Abort or listener shutdown closes the socket and cancels pending work. Cancellation is not a license to free the `OVERLAPPED` or its buffers early. Native completions must drain, then input leases and connection disposal must drain, before pools or ports are released.

A broken completion worker with live native ownership is process-fatal in this prototype. That is an explicit limit, not a production recovery strategy.

## Does IOCP include TLS?

**No. IOCP moves bytes; it does not encrypt or decrypt them.**

These Windows experiments expose raw TCP first. `UseHttps` can be added at the usual Kestrel middleware position. On Windows, its `SslStream` uses **SChannel**, the Windows TLS implementation. IOCP receives and sends ciphertext; the middleware performs the TLS handshake and record processing.

This is the same layering shape as our `IoUringTcp + UseHttps`, not the fd-bound Linux OpenSSL transport. It also means pre-TLS processing can run before `UseHttps`, and plaintext processing after it. TLS crypto does not run on our dedicated completion worker in this version.

## Usage

```csharp
builder.WebHost.UseIocp(workerCount: 4);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, 5001, listen =>
    {
        // Omit this for plain TCP.
        listen.UseHttps(certificate);
    });
});
```

The registration replaces the host's connection-listener factory. It is not yet a per-endpoint selector.

From the repository root:

```powershell
. .\activate.ps1
dotnet build src\Servers\Kestrel\samples\NetworkProtoSample\NetworkProtoSample.csproj -c Release -p:UseIisNativeAssets=false
dotnet artifacts\bin\NetworkProtoSample\Release\net11.0\NetworkProtoSample.dll --backend iocp --scheme http --workers 4 --port 5000
```

For the sample's HTTPS path, add `--scheme https --cert <certificate.pem> --key <key.pem> --tls-protocol Tls13`. The sample keeps its previous TLS 1.2 default unless explicitly overridden. Our measurement machine disables the TLS 1.2 server path, so all Windows TLS comparisons explicitly request TLS 1.3. No machine protocol settings were changed.

The sample reimports PEM credentials through PKCS#12 on Windows because the ephemeral PEM key could not complete the SChannel handshake; the same real-client handshake passed after key-provider import.

Set `NETWORKPROTO_CPUS=0,2,4,6` to pin four workers to those logical processors. `NETWORKPROTO_IOCP_ZERO_BYTE=0` posts real receive pages on idle connections instead of zero-byte probes. `NETWORKPROTO_WINDOWS_SNDBUF=4096` is the small-buffer pressure-test setting, not the benchmark default.

## Supported and deliberately missing

Supported: Windows x64; IPv4 TCP binding, including port zero; bounded accepted-connection publication; raw TCP; independent Kestrel TLS; partial output handling; input backpressure; half-close; pending-read cancellation; peer reset; unbind/disposal; explicit native ownership counters.

Not implemented: IPv6, Unix-domain sockets, client/connect transport, HTTP/2 or HTTP/3 validation, file/gather-send APIs, kernel zero-copy send, Windows native TLS on the owning worker, RIO auto-selection, or the proposed public `System.Net.Transport` API. Existing Linux transports are unchanged.

## Where to read the code

`Windows\Native.cs`: ABI-checked Win32 declarations, socket/port handles, and synchronous helpers.

`Windows\WindowsWorker.cs`: acceptor, owner loop, completion dispatch, commands, and counters.

`Windows\ConnectionState.cs`: native-operation lifetime, receive/send progress, and close state.

`Windows\Connection.cs`: Kestrel context and worker-owned output drain.

`Windows\LeasedPipeReader.cs`: provider-memory receive adapter and cursor behavior.

`Windows\NativeMemoryPool.cs`: native slab ownership, shared with RIO.

The sample's `WindowsTransportCheck.cs` drives real sockets through cancellation recovery, unconsumed-prefix handling, 256 KiB input pressure, 4 MiB slow-reader output, response after peer half-close, reset during blocked output, and listener disposal with idle peers.

Official references: [IO completion ports](https://learn.microsoft.com/windows/win32/fileio/i-o-completion-ports), [AcceptEx](https://learn.microsoft.com/windows/win32/api/mswsock/nf-mswsock-acceptex), [WSARecv](https://learn.microsoft.com/windows/win32/api/winsock2/nf-winsock2-wsarecv), [batched completion retrieval](https://learn.microsoft.com/windows/win32/api/ioapiset/nf-ioapiset-getqueuedcompletionstatusex).
