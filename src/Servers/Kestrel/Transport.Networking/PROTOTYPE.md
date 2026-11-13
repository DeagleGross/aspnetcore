# Networking.Transport prototype

The public surface lives in the `System.Net.Transport` namespace. This first section covers only how an application selects a provider, configures its workers and starts listening. Connections, buffers and TLS are described in later sections.

### Choosing a provider

A provider is selected by the type of the options object passed to `NetworkEngineFactory.Create`, not by an enum argument. `NetworkEngineKind` is the read-only answer to "which provider is running", exposed as `NetworkEngine.Kind`.

| Options type | `NetworkEngineKind` | OS | Native I/O model |
| --- | --- | --- | --- |
| `EpollEngineOptions` | `Epoll` | Linux | Readiness notifications with non-blocking receive and send |
| `IoUringEngineOptions` | `IoUring` | Linux | Submission and completion rings |
| `IocpEngineOptions` | `Iocp` | Windows | Overlapped I/O with completion ports |
| `RioEngineOptions` | `Rio` | Windows | Registered I/O |

Selection is explicit. Creating an engine for a provider that the current OS does not have throws `PlatformNotSupportedException`. There is no fallback to another provider or to `Socket`, so portable code chooses the options type itself:

```csharp
NetworkEngineOptions options = OperatingSystem.IsWindows()
    ? new RioEngineOptions()
    : new IoUringEngineOptions();
```

### Engine options

All four options types derive from `NetworkEngineOptions`. The Windows providers add pool settings through `WindowsEngineOptions`; epoll and io_uring add nothing.

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `WorkerCount` | `int` | `4` | Number of worker threads started for each bound listener. A worker is a dedicated thread that drives the native I/O model and the connections assigned to it. Must be greater than zero. |
| `WorkerProcessors` | `IReadOnlyList<int>?` | `null` | Optional logical processor ID for each worker. The list length must equal `WorkerCount` and every ID must be nonnegative. When `null`, epoll, IOCP and RIO workers are not pinned, and the workers of an io_uring listener are pinned: worker `i` to processor `2 * i`. An io_uring client engine is not pinned unless processors are given. |
| `CompleteReceivesInline` | `bool` | `false` | Where the continuation of a pending receive runs. `false`: it is queued to the thread pool, so application code never runs on a provider worker. `true`: it runs on the worker that received the bytes, which saves a thread hand-off per request and lets a consumer that blocks stall every connection assigned to that worker. [Scheduling](#scheduling) explains the trade-off. |
| `InputPoolMaximumBytes`, `OutputPoolMaximumBytes`, `PoolIdleTrimDelay` | `int`, `int`, `TimeSpan` | 256 MiB, 512 MiB, 10 s | `WindowsEngineOptions` only (IOCP and RIO). Maximum size of each worker's receive and output pools (4 MiB to 1 GiB), and how long an idle pool waits before it is trimmed (`Timeout.InfiniteTimeSpan` disables trimming). |

Invalid values are rejected by `NetworkEngineFactory.Create` with `ArgumentOutOfRangeException` for `WorkerCount` and `ArgumentException` for `WorkerProcessors`. The Windows providers additionally accept only processors 0 through 63, and that is checked later, when the listener is bound.

When `WorkerProcessors` is `null`, the prototype also reads the `NETWORKPROTO_CPUS` environment variable, a comma-separated processor list. It is a benchmarking knob and not part of the API.

### Creating an engine and listening

`NetworkEngineFactory.Create` returns a `NetworkEngine`. Creating it binds nothing and starts no thread. Each `ListenAsync` call starts a new set of `WorkerCount` workers and completes when they are running and the endpoint is bound. `NetworkListener.AcceptAsync` then yields `NetworkConnection` objects, and returns `null` after the listener is unbound.

```csharp
using System.Net;
using System.Net.Transport;

var options = new EpollEngineOptions
{
    WorkerCount = 2,
    WorkerProcessors = [0, 1]
};

await using var engine = NetworkEngineFactory.Create(options);
Console.WriteLine(engine.Kind); // Epoll

await using var listener = await engine.ListenAsync(new IPEndPoint(IPAddress.Loopback, 5000));

while (await listener.AcceptAsync() is { } connection)
{
    _ = HandleAsync(connection);
}

static async Task HandleAsync(NetworkConnection connection)
{
    await using (connection)
    {
        // See NetworkConnection below for reading and writing.
    }
}
```

`ListenAsync` takes an optional `NetworkListenOptions`. Leaving it `null` binds a plain TCP listener, and the only member today is `NativeTls`, which is covered with TLS.

Shutdown:

- `NetworkListener.UnbindAsync` stops new accepts without releasing live connections.
- `NetworkListener.DisposeAsync` stops the listener and drains its outstanding native ownership.
- `NetworkEngine.DisposeAsync` disposes every listener that the engine created.

### NetworkConnection

A `NetworkConnection` is what `AcceptAsync` returns: one accepted TCP connection, or one TCP connection whose TLS handshake has already completed when native TLS is configured. It is the application's only handle on that connection. The native socket is never exposed, so applications receive, send and close through the connection, and the provider worker that accepted it performs the native I/O.

Two things make it different from a `Socket` wrapper:

- **The provider owns the receive memory.** Applications do not pass a buffer to read into. The provider fills pages (pinned, native or registered memory, depending on the provider) and the connection exposes them in place as a `ReadOnlySequence<byte>`, without a copy. The application reads from the sequence and then tells the connection how much it consumed, which lets the provider reuse the pages.
- **Sends are completion based.** Native I/O keeps referencing the bytes being sent until the provider reports that the send has finished, so a send either carries an ownership contract or completes only after that point.

The connection is an abstract class. A shared base type implements the receive page list, the send queue and the output pump, and each provider derives from it and supplies only the native parts, such as submitting a send and closing the socket. Those protected members are for provider authors and are not covered here.

| Group | Members | Purpose |
| --- | --- | --- |
| Identity | `Id`, `LocalEndPoint`, `RemoteEndPoint` | A provider-generated identifier for logging and correlation, and the two endpoints (each can be `null`). |
| TLS | `IsTls`, `TlsInfo` | Whether the bytes are authenticated TLS plaintext, and the negotiated protocol and cipher suite. `TlsInfo` is `null` for plain TCP. |
| Receive | `ReceiveAsync`, `AdvanceReceiveTo`, `NetworkReceiveResult` | Read provider-owned bytes in place, then advance. |
| Send | `SendAsync`, `EnqueueSend`, `AllocateBuffer`, `MaxQueuedSendBytes` | Queue bytes for sending, with a limit on queued bytes. |
| Pipelines and streams | `Output`, `AsDuplexPipe()`, `AsStream()` | Adapters for code written against `PipeWriter`, `IDuplexPipe` or `Stream`. |
| Lifetime | `ConnectionClosed`, `Abort`, `DisposeAsync` | Observe, force and complete the close. |

#### Receiving

`ReceiveAsync` returns a `NetworkReceiveResult` as soon as there are unconsumed bytes, and waits otherwise. `Buffer` holds every byte that has been received and not yet consumed, as a `ReadOnlySequence<byte>` that can span several provider pages. `IsCompleted` is `true` once the peer has closed its side, and `Buffer` can still hold the final bytes in that same result. A reset or other receive failure makes `ReceiveAsync` throw.

After looking at the bytes, call `connection.AdvanceReceiveTo(consumed)` or `connection.AdvanceReceiveTo(consumed, examined)`:

- `consumed` is the position up to which the application is finished with the bytes. The provider can reuse those pages.
- `examined` is the position up to which the application has looked. If it is the end of the buffer and `consumed` is earlier, the next `ReceiveAsync` waits for new bytes instead of returning the same partial data again. This is how a parser waits for the rest of a message.

Rules:

- There is one outstanding receive at a time. Calling `ReceiveAsync` again before `AdvanceReceiveTo` throws `InvalidOperationException`.
- `Buffer` is only valid until `AdvanceReceiveTo`. To keep bytes longer, use `NetworkReceiveResult.Retain`, which is covered separately.
- A token that is already cancelled when `ReceiveAsync` is called throws `OperationCanceledException`. A token cancelled while the receive is waiting completes it with `IsCanceled` set instead of throwing, and the connection stays usable.
- The continuation of an awaited receive is queued to the thread pool by default, so application code does not run on the provider's worker thread. With `CompleteReceivesInline = true` (an engine option, or a per-connection property) it runs on the worker instead.

#### Sending

There are three ways to send, and they fall into two modes that are chosen by the first use on a connection:

| Way | Members | Ownership | Completion |
| --- | --- | --- | --- |
| Borrowed bytes | `SendAsync(ReadOnlyMemory<byte>)`, `SendAsync(ReadOnlySequence<byte>)`, each with an optional `CancellationToken` | The application keeps ownership. The memory must stay valid and unchanged until the returned task completes. | The task completes after the native send no longer references the memory. |
| Retained bytes | `EnqueueSend(NetworkBufferSlice)`, `EnqueueSend(NetworkPayload)`, with memory from `AllocateBuffer` or a retained receive | The connection takes its own reference, so the caller can release its own immediately. | Fire and forget. A later send failure is reported by the connection closing. |
| Pipe or stream | `Output`, `AsDuplexPipe()`, `AsStream()` | Standard `PipeWriter` and `Stream` semantics. | Flush completes under the usual `PipeWriter` flow control. |

`SendAsync` and `EnqueueSend` can be mixed with each other, and sends are written in the order they were admitted. Mixing either of them with the pipe or stream way on the same connection throws `InvalidOperationException`.

Backpressure for the two direct ways is an admission limit, not a wait. The bytes of sends that have not finished are counted against `MaxQueuedSendBytes`, which defaults to 1 MiB, and a send that would exceed it throws `InvalidOperationException`. A single send larger than the limit therefore fails unless the limit is raised first, and callers that issue many sends should await them. Cancelling the token of a `SendAsync` aborts the whole connection, because a message that is half sent cannot be skipped.

#### Closing

- `ConnectionClosed` is cancelled after the provider has finished closing the native connection.
- `Abort(exception)` asks the owning worker to close immediately. A pending receive throws the exception, and queued sends fail.
- `DisposeAsync()` is the graceful close. It lets queued sends finish, closes the connection, and then waits until native operations have ended and every retained receive page has been released. Each connection must be disposed, and a retained payload that is never disposed keeps `DisposeAsync` from completing.

#### Example: echo

This handler can replace the placeholder body of `HandleAsync` in the previous section. It echoes each received chunk back, then closes after the peer closes.

```csharp
static async Task HandleAsync(NetworkConnection connection)
{
    await using (connection)
    {
        while (true)
        {
            var result = await connection.ReceiveAsync();
            if (!result.Buffer.IsEmpty)
            {
                await connection.SendAsync(result.Buffer);
            }

            connection.AdvanceReceiveTo(result.Buffer.End);
            if (result.IsCompleted)
            {
                break;
            }
        }
    }
}
```

`SendAsync` is awaited before `AdvanceReceiveTo`, so the received pages are still valid while the native send uses them. This is the borrowed-bytes contract in practice: the send reads the provider's own receive pages in place, without a copy.

### Pages

A page is a fixed-size block of memory that the transport reads into or sends from. Applications never see pages as a type: they see a `ReadOnlySequence<byte>` when receiving and a `NetworkBuffer` when sending. Pages exist because native I/O needs memory with a stable address for as long as an operation is in flight, and some providers (RIO) need that memory registered with the OS ahead of time. Allocating, pinning or registering memory for every operation would cost more than the I/O itself, so each provider worker owns pools of long-lived pages and lends them out.

#### How pages work

- **Receive pages.** Each worker owns a pool of fixed-size pages (16 KiB in the current prototype). When data arrives, the provider reads it into a free page and publishes that page to the connection it belongs to. The connection keeps its published pages in order, and `ReceiveAsync` exposes them as one `ReadOnlySequence<byte>`. Nothing is copied or joined, so a message that spans two pages is simply two segments of the sequence. `AdvanceReceiveTo` releases the pages that have been consumed.
- **Reference counting.** Every page has a reference count. The connection's own list holds one reference, and `Retain` adds more. When the last reference is released, the page is queued back to the worker that owns it, and that worker puts it back in its pool.
- **Backpressure.** A connection can hold only a few unreleased receive pages (four in the current prototype). At that limit the provider stops reading from that connection, and reading resumes when pages come back. A slow consumer therefore pushes back on the peer through TCP flow control instead of the transport buffering without bound.
- **Output blocks.** Sending uses a separate per-worker pool, and `AllocateBuffer` rents from it (so does the `Output` pipe). Output blocks are not receive pages. Their size is a provider detail and can be larger than requested: a request for 25 bytes returned a 64 KiB block on both IOCP and RIO, and the Linux providers use a 4 KiB minimum. They follow the same reference counting: a block goes back to the pool when its last reference is released, which includes the connection's own reference while a send is in flight.

The memory kind describes what a block is, and it is reported by `NetworkBuffer.Kind`: `Managed` is an ordinary array that the provider pins during the I/O, `Pinned` is address-stable managed memory, `Native` is unmanaged memory, and `Registered` is native memory that is also registered with the provider.

| Provider | Receive pages | Output blocks |
| --- | --- | --- |
| IOCP | `Native` | `Native` |
| RIO | `Registered` | `Registered` |
| epoll | `Pinned` | `Native` |
| io_uring | `Pinned` | `Native` |

On the Linux providers the output pool is `Native` by default. If the adaptive output pool is switched off, which is only a benchmark option, output falls back to `Managed`.

#### NetworkBuffer and AllocateBuffer

Three public types carry page ownership, and they all share the same reference count:

- `NetworkBuffer` is one owned, disposable reference to a block. `AllocateBuffer` returns one, and so does `NetworkBuffer.TakeOwnership` when the application wraps its own `IMemoryOwner<byte>`.
- `NetworkBufferSlice` is a borrowed view of part of a buffer. Copying a slice does not add a reference, but `Retain()` creates an independent `NetworkBuffer` for it.
- `NetworkPayload` is a retained message from `NetworkReceiveResult.Retain`. It can span several receive pages and has a `Sequence` over them.

`AllocateBuffer` helps in these situations:

- **Building a message in provider-ready memory.** RIO can only send from registered memory, and a borrowed `SendAsync` over memory that is not registered is first copied into a staging block. A block from `AllocateBuffer` is already registered, so the send needs no copy.
- **Fire-and-forget sends.** `EnqueueSend` takes its own reference, so the caller can dispose its buffer immediately and does not have to await each send or keep the memory alive.
- **Forwarding received data.** A retained `NetworkPayload` can be passed to `EnqueueSend` as it is, so received pages can be sent without being copied. The `RetainedPages` sample does this.

It is not needed for small constant messages. A `static readonly byte[]` is valid forever, so `SendAsync(array)` is enough, and an allocated block would be much bigger than the message.

#### Example: allocate a buffer and send it

This sends a greeting from a block that the connection allocated. `AllocateBuffer` can return more bytes than requested, so the example sends only the bytes it wrote.

```csharp
static void SendGreeting(NetworkConnection connection)
{
    var text = "hello from the transport\n";
    using var buffer = connection.AllocateBuffer(Encoding.UTF8.GetMaxByteCount(text.Length));
    var written = Encoding.UTF8.GetBytes(text, buffer.Memory.Span);

    connection.EnqueueSend(buffer.Slice.Slice(0, written));
}   // our reference is released here; the connection keeps its own until the send finishes
```

The same message with `SendAsync` borrows the memory instead, so the buffer has to stay alive until the send completes:

```csharp
static async Task SendGreetingAsync(NetworkConnection connection)
{
    var text = "hello from the transport\n";
    using var buffer = connection.AllocateBuffer(Encoding.UTF8.GetMaxByteCount(text.Length));
    var written = Encoding.UTF8.GetBytes(text, buffer.Memory.Span);

    await connection.SendAsync(buffer.Memory[..written]);
}   // disposed only after the awaited send has finished
```

Rules for buffers:

- Write into `buffer.Memory` before handing the bytes to the connection, and do not change them afterwards. The connection can read them at any time until the send completes.
- Dispose each `NetworkBuffer` exactly once. With `EnqueueSend` that can be right after the call. With `SendAsync` it must be after the task completes.
- A buffer that is never disposed keeps its block out of the pool.

### How epoll works (Linux)

epoll is readiness based: the kernel reports that a socket can be read or written, and the worker then does the non-blocking `recv` or `send` itself. It is the only provider that does the I/O call on the worker instead of having the OS finish it.

- **Setup.** Each worker owns an epoll instance, an `eventfd` for wake-ups and its own listening socket with `SO_REUSEPORT`, so the kernel spreads new connections across the workers. Registrations are level-triggered and carry a monotonic id, so an event for a closed and reused descriptor cannot be mistaken for the new one.
- **Memory.** A server worker owns 2048 pages of 16 KiB in one pinned managed array. A client worker uses 64 KiB pages from a pool of the same total size, because a client reads whole responses per call.
- **Accept.** The listener becomes readable, and the worker calls `accept4` with `SOCK_NONBLOCK`.
- **Receive.** When a connection is readable the worker rents a free page, calls `recv` once and publishes the page. A read shorter than a page ends the pass without a second `recv`, because level-triggered epoll reports any remaining input. A connection that holds four unreleased pages loses its read interest, and gets it back when a page is returned, so TCP flow control pushes back on the peer.
- **Send.** The worker calls `send` with `MSG_NOSIGNAL` on the bytes that the output pipe or a borrowed send provides. A partial send registers write interest and continues when the socket is writable again.
- **Commands and wake-ups.** Other threads enqueue a command (`OutputReady`, `ReturnPage`, `Close`, `Connected`, `Unbind`, `Stop`) and wake the worker with an `eventfd` write. A command joins a wake that is already pending instead of writing again (`NETWORKPROTO_COALESCE=0` turns that off), so a busy worker is woken once per drain and not once per request. The loop drains up to 1024 commands, then calls `epoll_wait` for up to 128 events, with a 10 ms timeout when no command is pending.
- **TLS.** After the readiness event, OpenSSL (or the runtime's socket-based session, `NETWORKPROTO_LINUX_TLS_IMPL=session`) reads and writes the socket itself, so the worker never sees ciphertext pages. The TLS table below has the other providers.

### How RIO and io_uring work

This section describes what the two completion-based providers do under the hood. It is read from the prototype source, and it is a high-level map, not a specification. Both providers follow the same pattern. A worker thread starts an operation (accept, receive or send), the OS finishes it later and reports a completion, and the worker turns that completion into a published receive page or a finished send. All state of a connection is touched only by its worker. Other threads put a command on the worker's queue and wake it, with `PostQueuedCompletionStatus` on Windows or a write to an `eventfd` on Linux. Each connection has at most one receive and one send in flight.

#### RIO (Windows)

- **Setup.** Sockets are created with `WSASocketW` and the `WSA_FLAG_REGISTERED_IO` flag, and the RIO function table is obtained with `WSAIoctl`. Each worker owns one I/O completion port and one RIO completion queue (`RIOCreateCompletionQueue`, 8192 entries) that is wired to that port. Each accepted connection gets its own request queue (`RIOCreateRequestQueue`) that allows one outstanding receive and one outstanding send.
- **Accept.** Accept is not RIO. The listener posts 32 overlapped `AcceptEx` calls on worker 0, and every accepted socket is handed to a worker round robin through the command queue. The `NETWORKPROTO_WINDOWS_ACCEPT=pool` knob moves the accepts to the .NET I/O thread pool.
- **Memory.** The page pools are native slabs of 4 MiB that are registered once with `RIORegisterBuffer`. An operation names its memory as a registered buffer id plus an offset and a length, so nothing is pinned or locked per operation.
- **Receive.** The page is chosen before the receive is posted. The worker rents a page from the input pool and calls `RIOReceive` with it, so a connection that is waiting for data holds one page even when it is idle. If the pool is empty, the connection waits until a page is returned.
- **Completion.** The worker arms the queue with `RIONotify`. When completions are ready, Windows posts a packet to the worker's completion port, `GetQueuedCompletionStatusEx` returns, and the worker drains the queue with `RIODequeueCompletion` (up to 128 entries at a time) and arms it again. Each entry carries a status, the number of bytes and the request context that identifies the connection and the operation. For plain TCP a receive completion means that N bytes were written into the page that was posted, and zero bytes means the peer closed. A send completion means that N bytes were sent.
- **Send.** `RIOSend` reads from registered memory. A borrowed send from memory outside the registered slabs is first copied into a registered output block, which is the staging copy mentioned in the Pages section. A partial send is posted again with the remainder.

IOCP uses the same worker, the same completion port and the same TLS code. It calls `WSARecv` and `WSASend` directly with an `OVERLAPPED` and takes completions straight from the port. It has no registered memory, and to avoid holding a page for every idle connection it first posts a zero-byte receive as a probe (`NETWORKPROTO_IOCP_ZERO_BYTE=0` turns that off). The probe completion only says that data is available, and the worker then rents a page and posts the real receive. RIO does not use the probe.

#### io_uring (Linux)

- **Native layer.** A small C library (`networkproto2`) wraps liburing and OpenSSL. The managed code decides what to submit and owns all connection state, and the C code only prepares entries, submits them and reads completions.
- **Setup.** Each worker owns one ring (2048 entries, created with `IORING_SETUP_SINGLE_ISSUER` and `IORING_SETUP_DEFER_TASKRUN`), and the thread is pinned to a processor. Each worker also has its own listening socket with `SO_REUSEPORT`, so the kernel spreads new connections across the workers.
- **Accept.** One multishot accept stays armed per worker, and every new connection produces a completion without another submission.
- **Memory.** Each ring owns a pinned 32 MiB array cut into 16 KiB pages. The pages are registered as a provided-buffer ring (`io_uring_setup_buf_ring`), which is a pool the kernel takes buffers from. The prototype does not use `io_uring_register_buffers`.
- **Receive.** One multishot receive (`IORING_OP_RECV` with `IOSQE_BUFFER_SELECT`) stays armed per connection. The kernel picks a free page from the provided-buffer ring only when data arrives, so an idle connection holds no page. A completion carries the byte count, the id of the page the kernel chose, and a flag that says whether the receive is still armed. Zero bytes means the peer closed. When a page is released it is added back to the provided-buffer ring. When a connection holds four unreleased pages, the worker cancels the multishot receive and starts it again after a page comes back. An empty buffer ring (`-ENOBUFS`) is not an error.
- **Send.** `IORING_OP_SEND` reads from pinned or native memory. A partial send is submitted again with the remainder.
- **Submission and completion.** The worker drains its command queue, prepares the entries, and then calls into C once. That call submits everything that was prepared, waits for a completion (10 ms at most) and copies up to 1024 completions to managed memory. The `user_data` of every entry holds the connection id and the kind of operation. The `eventfd` that wakes the worker is a read that is armed on the ring, and a command joins a wake that is already pending (`NETWORKPROTO_COALESCE=0` turns that off).
- **Cancel and close.** Cancels use `IORING_OP_ASYNC_CANCEL`. A connection keeps its state and its memory until every operation it started has produced its final completion, even a cancelled one.
- **Not used.** The prototype does not use `IORING_SETUP_SQPOLL`, registered buffers, registered files or zero-copy send.

The main difference is who picks the memory. RIO needs the page before the receive is posted, and io_uring lets the kernel pick it when the data arrives.

#### What a completion means with TLS

With TLS the completion only reports ciphertext, and the plaintext is produced afterwards by code that runs on the same worker thread. The table shows what the OS reports and who decrypts.

| Provider and TLS implementation | What the OS completion reports | How plaintext is produced |
| --- | --- | --- |
| epoll with OpenSSL on the socket (default) or the runtime session | The socket is readable or writable (readiness, not a completion) | After the readiness the worker lets OpenSSL read and write the non-blocking socket, and a read decrypts straight into a free page. |
| RIO and IOCP with Schannel (default) | N bytes of ciphertext are in the receive page | The worker calls `DecryptMessage` on the page and publishes the plaintext as a slice of the same page, so nothing is copied. A send calls `EncryptMessage` into an output block, and that block is sent. |
| RIO and IOCP with the runtime session (`NETWORKPROTO_WINDOWS_TLS_IMPL=session`) | N bytes of ciphertext are in the receive page | The worker calls the runtime's buffer-based TLS session, which decrypts into a separate page. |
| io_uring with OpenSSL over a custom BIO (default) | A ciphertext page has arrived, or a ciphertext send has finished | The worker hands the page to OpenSSL through a memory-style BIO and calls `SSL_do_handshake`, `SSL_read_ex` and `SSL_write_ex`. The plaintext is copied into a free page. The ciphertext that OpenSSL produces is sent by the ring. |
| io_uring with OpenSSL on the socket (`NETWORKPROTO_URING_TLS=fd`, or `NETWORKPROTO_KTLS=1`) | The socket is readable or writable (`POLLIN` multishot and `POLLOUT` polls) | The ring moves no data. After the readiness the worker lets OpenSSL read and write the non-blocking socket itself. With kTLS the kernel does the record encryption. |
| io_uring with the runtime session (`NETWORKPROTO_LINUX_TLS_IMPL=session`) | The socket is readable or writable | The same readiness pattern, but the runtime's socket-based TLS session reads and writes the socket. |

A few more details:

- **Handshake.** On Windows the handshake runs through a bounded scheduler off the worker thread by default (`NETWORKPROTO_TLS_HANDSHAKE_OFFLOAD=0` turns it off), and the result comes back to the worker as a command. In the io_uring source the OpenSSL calls are made on the worker thread.
- **Protocol.** Schannel is TLS 1.3 by default (`NETWORKPROTO_WINDOWS_TLS=1.2` selects TLS 1.2). The Linux OpenSSL and session contexts are configured for TLS 1.2 with `ECDHE-RSA-AES128-GCM-SHA256`, with no tickets and no session cache.
- **Backpressure.** The limit of four unreleased plaintext pages applies to decrypted pages too. At the limit the provider stops decrypting buffered ciphertext and stops reading, and both resume when a page is returned.
- **Page kinds.** Plaintext pages are `Registered` on RIO, `Native` on IOCP and `Pinned` on io_uring, as in the table in the Pages section.

### Scheduling

Where the application's continuation runs decides most of the throughput of a native provider. The workers make the system calls cheap, and what is left is hand-offs between threads. Per request, the default path is:

1. The worker receives into a pooled page and publishes it to the connection.
2. Completing the pending `ReceiveAsync` (or a read of the pipe from `AsDuplexPipe()`) queues the application's continuation to the thread pool.
3. The application runs on a thread-pool thread and writes its response. Flushing enqueues an `OutputReady` command to the worker, and releasing the page enqueues a `ReturnPage` command. Both wake the worker only if it is parked, and a wake that is already pending is joined.
4. The worker sends the response and returns the page to its pool.

`CompleteReceivesInline = true` removes step 2: the continuation runs on the worker that received the bytes, and the two commands land on the worker's own queue without a wake. That is the largest lever the providers have (see Results), and it comes with one rule. The continuation shares the worker with every other connection assigned to it, so a consumer that blocks (synchronous I/O, `.Result`) stalls those connections. It is the analogue of `SocketTransportOptions.UnsafePreferInlineScheduling` for the Sockets transport, and it is off by default for the same reason. `NetworkConnection.CompleteReceivesInline` overrides the engine setting for one connection, and `NetworkConnection.CompleteSendsInline` does the same for the continuation of an awaited `SendAsync`, which otherwise also resumes on the thread pool.

The scheduling model, the cost per request and the limits of default mode are measured and explained in [Bedrock.md](Bedrock.md#scheduling-is-the-performance-contract).

### Results

Measured on crank with Kestrel serving a 1 KB response over HTTP/1.1 keep-alive, `wrk` on a separate machine, through the Bedrock registration ([Bedrock.md](Bedrock.md) describes the registration; the providers are the ones described here). Each cell is the mean of 2 runs unless it says "1 run" and is **measured, not independently reproduced**. "% of Sockets" is throughput as a fraction of the stock Kestrel Sockets transport in the same table (100% is parity, 120% is 20% more requests per second than Sockets). The application is limited to `W` cores of a 56-core machine (`W` workers), so "4 workers" is four cores of a larger machine. Inline Sockets, where it is shown, is Sockets with `UnsafePreferInlineScheduling` (and `DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1` on Linux).

Linux (`aspnet-gold-lin-relay`), % of Sockets default. "inline" is `CompleteReceivesInline = true`.

| Workers | epoll default | epoll inline | io_uring default | io_uring inline |
| --- | --- | --- | --- | --- |
| 4 | 123% | 172% | 126% | 191% |
| 8 | 100% | 161% | 111% | 183% |
| 16 (1 run) | 118% | not run | 117% | 174% |
| 28 | 104% | 136% | 102% | 146% |
| 56, the whole machine (1 run) | 108% | not run | 108% | not run |

Windows (`aspnet-gold-win-relay`), % of Sockets default.

| Workers | IOCP default | IOCP inline | RIO default | RIO inline |
| --- | --- | --- | --- | --- |
| 4 | 94% | 113% | 115% | 157% |
| 28 | 100% | 122% | 121% | 154% |

- On a 2-CPU Intel server (`idna-intel-lin-relay`, 256 connections, 2 runs, default mode) epoll is 128% and io_uring 148% of Sockets. Small machines show the largest margins.
- Against a Sockets transport that is also run inline, the Linux inline margin is 114-136% and the Windows margin 111-156%.
- With TLS owned by the transport (2026-10 builds, 4-vCPU Intel machines, before `CompleteReceivesInline` existed; not re-run) keep-alive 1 KB was +42% (epoll) and +54% (io_uring) over Sockets with `SslStream` on Linux, and +15% (IOCP) and +46% (RIO) on Windows. The full TCP and TLS matrices are in [LINUX-RESULTS.md](https://github.com/aspnet/Benchmarks/blob/dmkorolev/networking-proto/src/BenchmarksApps/Networking-Transports/LINUX-RESULTS.md) and [WINDOWS-RESULTS.md](https://github.com/aspnet/Benchmarks/blob/dmkorolev/networking-proto/src/BenchmarksApps/Networking-Transports/WINDOWS-RESULTS.md).
- Default mode has a ceiling at many cores: about 8 µs of the 14-15 µs of CPU per request is kernel TCP work that Sockets also pays. The margin shrinks with the core count in a cpuset partly because the NIC's receive work lands on other CPUs ([Bedrock.md](Bedrock.md#the-cpuset-is-not-a-smaller-machine)).
- Raw rows, the 56-core and small-machine tables and the CPU-per-request breakdown are in [GOLD-RESULTS.md](https://github.com/aspnet/Benchmarks/blob/dmkorolev/networking-proto/src/BenchmarksApps/Networking-Transports/GOLD-RESULTS.md) and [SCALING-RESULTS.md](https://github.com/aspnet/Benchmarks/blob/dmkorolev/networking-proto/src/BenchmarksApps/Networking-Transports/SCALING-RESULTS.md).

### Other Windows I/O APIs

The prototype does not use `ProcessSocketNotifications` or Windows I/O rings. What the public documentation says about them is in [Windows I/O options](Windows%20IO%20options.md).

### Scaling and memory

How the providers behave when CPUs or memory are added after start is in [ScalingCapabilities.md](ScalingCapabilities.md). What they cost in memory, per connection and after a burst, is in [MemoryFootprint.md](MemoryFootprint.md).

### Project Bedrock

An experiment that reaches the providers through `IConnectionListenerFactory` and `IConnectionFactory`, and uses them as the connect side of `SocketsHttpHandler`, is in [Bedrock.md](Bedrock.md), and the API it adds is in [Bedrock-API.md](Bedrock-API.md).