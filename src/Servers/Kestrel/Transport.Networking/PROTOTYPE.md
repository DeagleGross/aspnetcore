# Networking.Transport prototype

Working draft. The prototype is experimental and non-shipping, and nothing here is an approved API.

## Public API

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
        // Reading and writing are covered in a later section.
    }
}
```

`ListenAsync` takes an optional `NetworkListenOptions`. Leaving it `null` binds a plain TCP listener, and the only member today is `NativeTls`, which is covered with TLS.

Current endpoint limits:

- Epoll and io_uring require an IPv4 address and a fixed nonzero port.
- IOCP and RIO require an IPv4 address.

Shutdown:

- `NetworkListener.UnbindAsync` stops new accepts without releasing live connections.
- `NetworkListener.DisposeAsync` stops the listener and drains its outstanding native ownership.
- `NetworkEngine.DisposeAsync` disposes every listener that the engine created.
