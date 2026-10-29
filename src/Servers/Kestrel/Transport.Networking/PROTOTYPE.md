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

After looking at the bytes, call `AdvanceReceiveTo(consumed)` or `AdvanceReceiveTo(consumed, examined)`:

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
