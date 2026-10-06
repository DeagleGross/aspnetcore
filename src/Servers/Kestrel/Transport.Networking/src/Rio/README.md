# Windows RIO transport, explained simply

**RIO = Registered I/O**, a Winsock API for moving TCP or UDP data using memory registered with Windows in advance. This implementation is a non-shipping **TCP server** experiment, written in C# with direct Win32 function pointers.

It is not a different network protocol. The peer still speaks ordinary TCP. It is not TLS, and "registered" does not mean that every networking copy disappears.

The [local comparison and its limits](../Windows/RESULTS.md) include stock/IOCP/RIO short and keep-alive TCP/TLS results.

## The three things to know

**Registered buffer:** Windows is given a stable memory range once and returns a buffer ID. Each operation names that ID, an offset, and a length. Registration locks the underlying physical pages and avoids repeating some per-operation memory validation/locking work.

**Request queue, or RQ:** a socket's submission object. `RIOReceive` and `RIOSend` use it instead of the socket handle. This prototype permits one outstanding receive and one outstanding send per connection.

**Completion queue, or CQ:** a mailbox for finished RIO requests. One owning worker has one CQ, shared by its connections' receive and send requests. It retrieves up to 128 completions with `RIODequeueCompletion`.

The queue APIs are not internally synchronized for arbitrary concurrent callers. All access to a given RQ/CQ stays on its owning worker.

## How RIO and IOCP work together

```text
AcceptEx on worker 0
  -> accepted WSA_FLAG_REGISTERED_IO socket
  -> one owning worker
  -> RIOCreateRequestQueue

registered receive slice -> RIOReceive -> RIO completion queue
registered output slice  -> RIOSend    -> RIO completion queue

RIONotify -> worker's IOCP mailbox -> worker wakes
  -> RIODequeueCompletion batch
  -> leased input reader / output progress
```

RIO does not replace accept/connect with new APIs. We still use `AcceptEx` for acceptance. Connected sockets are distributed round-robin, just like the IOCP experiment.

The actual data operations are **RIOReceive and RIOSend**, not ordinary overlapped `WSARecv`/`WSASend` disguised as RIO. If RIO extension lookup or queue creation fails, startup/adoption fails explicitly. There is no hidden fallback to `System.Net.Sockets`.

The CQ is normally drained from userspace. When the worker would otherwise sleep, `RIONotify` arranges an IOCP notification when completions are available. IOCP is the doorbell; the RIO CQ contains the actual data-operation results. At most one notification is armed for that CQ at a time.

The current implementation submits each send immediately. It does not claim `RIO_MSG_DEFER`/`RIO_MSG_COMMIT_ONLY` submission batching or a busy-polling mode.

## Memory, without the jargon

Our pools allocate stable native memory in 4 MiB slabs and register each slab once. The slab is divided into reusable slices, normally 16 KiB each.

Receive completes into a slice. Kestrel reads that slice through a custom `PipeReader`; it is not copied into another input pipe. Consuming the slice returns its lease. A connection can expose four input pages before receiving pauses.

Output is an ordinary `Pipe` with a registered-memory pool. Kestrel's output writes therefore land in sendable registered memory. The worker passes the slice's buffer ID/offset/length to RIO and keeps the pipe read alive until send completion. It does not register and copy a new temporary send array on every write.

**RIO has a different idle-memory cost from IOCP.** It posts a real receive slice, so an idle connection normally occupies one 16 KiB input page. The IOCP experiment can use a zero-byte probe instead. RIO's larger CQ/RQ and registered-memory commitments are reasons not to select it blindly for every workload.

Input allocation is bounded at 32 MiB per worker; output at 128 MiB per worker. CQ capacity is 8,192, and per-worker connection admission is bounded at 4,096. The shared accepted-connection channel is bounded at 1,024. These are prototype limits, not an API proposal.

Registration and garbage-collector pinning are different concepts. The native slab's address is already stable; RIO registration additionally tells Windows about its pages. Neither implies that TCP bypasses the kernel or the network stack.

## Cancellation and close

RIO's request queue belongs to its socket. Closing the socket closes the RQ, but outstanding requests still have completions to drain. We retain the memory and request identities until those completions arrive.

Normal output completion first sends TCP FIN after all output has completed. The worker keeps the socket/RQ alive until peer EOF, with a five-second deadline. Closing the RQ immediately after the send receipt caused rare reset-before-response failures under actual short-connection HTTP load; send completion is a memory-release boundary, not proof of peer receipt.

Buffer registration is removed only after all native operations and application leases drain. Safe handles own registrations, queues, ports, sockets, and native allocations. Shutdown counters expose remaining operations, connections, and leases; nonzero ownership prevents a successful benchmark result.

A fatal completion-worker failure with live kernel references is process-fatal here. Production recovery is out of scope.

## TLS is a separate layer

RIO itself has no handshake, encryption, certificate, or TLS record API.

```text
TCP bytes through RIO
  -> optional pre-TLS Kestrel middleware
  -> optional UseHttps / SslStream / SChannel
  -> plaintext HTTP and application
```

Omit `UseHttps` for plain TCP. Add it for TLS. RIO then moves ciphertext in registered buffers while SChannel handles TLS above it. This is deliberately the same TLS implementation as the stock Windows baseline, so the comparison isolates the transport.

It is comparable in architecture to our raw `IoUringTcp + UseHttps`, not to fd-bound OpenSSL on an epoll worker. A future buffer-TLS implementation could drive SChannel's record processing from the owner, but that is not implemented or included in these results.

## Usage

```csharp
builder.WebHost.UseRio(workerCount: 4);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, 5001, listen =>
    {
        // Omit this for plain TCP.
        listen.UseHttps(certificate);
    });
});
```

From the repository root:

```powershell
. .\activate.ps1
dotnet build src\Servers\Kestrel\samples\NetworkProtoSample\NetworkProtoSample.csproj -c Release -p:UseIisNativeAssets=false
dotnet artifacts\bin\NetworkProtoSample\Release\net11.0\NetworkProtoSample.dll --backend rio --scheme http --workers 4 --port 5000
```

For sample HTTPS add `--scheme https --cert <certificate.pem> --key <key.pem> --tls-protocol Tls13`. The local Windows comparisons explicitly use TLS 1.3 because this machine's SChannel TLS 1.2 server path is disabled. The same protocol, fixture certificate, and disabled-resumption options are used for stock, IOCP, and RIO. Machine policy is not changed.

The implementation requires Windows x64 and a Winsock provider exposing RIO. RIO is explicitly selected, never automatic. Worker affinity uses `NETWORKPROTO_CPUS`; `NETWORKPROTO_WINDOWS_SNDBUF=4096` selects the socket-pressure diagnostic. Do not confuse the latter with the benchmark default.

## Supported and missing

Supported: IPv4 TCP server binding, including ephemeral ports; worker-affine registered I/O; batched completion reaping; registered output memory; bounded receive leases; partial-completion handling; raw TCP and independent Kestrel TLS; peer half-close/reset; native ownership drain.

Missing: IPv6, UDP, client/connect, gather/file sends, deferred RIO commits, busy polling, kernel TLS, owner-thread TLS, HTTP/2/3 validation, and the proposed public transport API. Host-wide registration does not yet provide per-endpoint transport selection.

## Code map

`RioApi.cs` mirrors the installed Windows SDK's `mswsock.h`/`mswsockdef.h` function table and layouts. It loads the provider's functions with `WSAIoctl` and owns CQ/registration handles.

`Windows\WindowsWorker.cs` owns CQ/RQ access, batched dispatch, IOCP notifications, accepts, and managed commands.

`Windows\NativeMemoryPool.cs` owns native slabs, registrations, and reusable leases. Its rental paths are thread-safe; only the RQ/CQ data path must remain worker-owned.

`Windows\ConnectionState.cs` retains operation state and memory through cancellation/close. `Windows\Connection.cs` and `Windows\LeasedPipeReader.cs` adapt the result to Kestrel.

The real-socket checks and Windows benchmark runner live in `samples\NetworkProtoSample`. The benchmark load generator is the dependency-free `testassets\WindowsTransportLoad` application.

Official references: [RIO function table](https://learn.microsoft.com/windows/win32/api/mswsock/ns-mswsock-rio_extension_function_table), [buffer registration](https://learn.microsoft.com/windows/win32/api/mswsock/nc-mswsock-lpfn_rioregisterbuffer), [request queues and synchronization](https://learn.microsoft.com/windows/win32/api/mswsock/nc-mswsock-lpfn_riocreaterequestqueue), [RIONotify](https://learn.microsoft.com/windows/win32/api/mswsock/nc-mswsock-lpfn_rionotify).
