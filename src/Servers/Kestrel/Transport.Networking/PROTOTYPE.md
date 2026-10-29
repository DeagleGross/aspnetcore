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

All four options types derive from `NetworkEngineOptions`. They add no provider-specific members yet, so the base members are the whole option surface today.

| Option | Type | Default | Meaning |
| --- | --- | --- | --- |
| `WorkerCount` | `int` | `4` | Number of worker threads started for each bound listener. A worker is a dedicated thread that drives the native I/O model and the connections assigned to it. Must be greater than zero. |
| `WorkerProcessors` | `IReadOnlyList<int>?` | `null` | Optional logical processor ID for each worker. The list length must equal `WorkerCount` and every ID must be nonnegative. When `null`, epoll, IOCP and RIO workers are not pinned, and io_uring worker `i` is pinned to processor `2 * i`. |

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
- The continuation of an awaited receive is queued asynchronously, so application code does not run inline on the provider's worker thread.

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
