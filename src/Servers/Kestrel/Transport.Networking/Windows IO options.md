# Windows I/O options

This page describes two Windows APIs that sit next to the prototype's `Iocp` and `Rio` providers: `ProcessSocketNotifications` and I/O rings (`IoRing`). The prototype implements neither. The descriptions come from the public Microsoft Learn documentation, read on 2026-11-03 (links at the end). Nothing here was run or measured, and the parts about the prototype are interpretation, not documentation. The provider names and the `Iocp`, `Rio` and `Epoll` models are described in [PROTOTYPE.md](PROTOTYPE.md).

## At a glance

| | `ProcessSocketNotifications` | `IoRing` |
| --- | --- | --- |
| Header and DLL | `winsock2.h`, `Ws2_32.dll` | `ioringapi.h`, kernel32 |
| Minimum Windows | Windows 10 build 20348 | Windows build 22000 |
| Model | Readiness notifications delivered through an I/O completion port | A submission queue of operations and a completion queue of results |
| What an event means | The socket can be read or written without blocking | The queued operation has finished |
| Who does the I/O | The application, with non-blocking `recv` and `send` after the notification | The kernel |
| Sockets | Yes, Microsoft Winsock provider sockets only | No socket operation appears in the documentation that was read |
| Closest relative by shape | `epoll` | `io_uring` |

## ProcessSocketNotifications

### What it is

`ProcessSocketNotifications` is a Winsock function that associates a set of sockets with an I/O completion port and retrieves the notifications that are already pending on that port. Once a socket is registered, the port receives the socket state notifications that were asked for: data can be read without blocking, data can be written without blocking, the connection was disconnected or aborted, or an error occurred. The documentation presents it as a scalable way to get socket state changes that is efficient in CPU and memory, as a replacement for applications that poll with `WSAPoll`, and as an alternative to overlapped socket I/O with completion ports. The notification moves no data. The application still calls `recv` or `send` itself.

### How it works

1. The application creates a completion port and fills one `SOCK_NOTIFY_REGISTRATION` per socket: the socket, a completion key, the events of interest, an operation and trigger flags.
2. It calls `ProcessSocketNotifications`. One call can register sockets and dequeue notifications, which saves system calls. The same notification packets can also be dequeued with `GetQueuedCompletionStatus` or `GetQueuedCompletionStatusEx`, and those packets may be mixed with other kinds of packets on the same port.
3. Each returned `OVERLAPPED_ENTRY` carries the registration's completion key in `lpCompletionKey`. The event mask is the value of `dwNumberOfBytesTransferred` and is read with `SocketNotificationRetrieveEvents`. `lpOverlapped` is reserved and is not a pointer.
4. The application performs the I/O on the socket, and re-enables the registration when it is one-shot.

A simplified version of the pattern from the documentation's multi-threaded sample:

```cpp
SOCK_NOTIFY_REGISTRATION registration = {};
registration.socket = socket;
registration.completionKey = context;
registration.eventFilter = SOCK_NOTIFY_REGISTER_EVENT_IN | SOCK_NOTIFY_REGISTER_EVENT_HANGUP;
registration.operation = SOCK_NOTIFY_OP_ENABLE;
registration.triggerFlags = SOCK_NOTIFY_TRIGGER_ONESHOT | SOCK_NOTIFY_TRIGGER_LEVEL;

OVERLAPPED_ENTRY entries[64];
UINT32 count = 0;
DWORD error = ProcessSocketNotifications(port, 1, &registration, INFINITE, 64, entries, &count);
// on success, inspect registration.registrationResult as well

for (UINT32 i = 0; i < count; i++)
{
    auto* context = (Context*)entries[i].lpCompletionKey;
    UINT32 events = SocketNotificationRetrieveEvents(&entries[i]);

    if (events & SOCK_NOTIFY_EVENT_IN)
    {
        recv(context->socket, buffer, sizeof(buffer), 0); // the application does the I/O
    }

    // a one-shot registration is now disabled; pass SOCK_NOTIFY_OP_ENABLE again in the next call
}
```

### Registration options

| Field | Values | Meaning |
| --- | --- | --- |
| `eventFilter` | `SOCK_NOTIFY_REGISTER_EVENT_IN`, `_OUT`, `_HANGUP`, `_NONE`, `_EVENTS_ALL` | Notify when data can be read without blocking, when data can be written without blocking, or when a stream connection was disconnected or aborted |
| `operation` | `SOCK_NOTIFY_OP_NONE`, `_ENABLE`, `_DISABLE`, `_REMOVE` | What to do with the registration. At most one operation per registration per call. `_NONE` is for a call that only wants to receive notifications |
| `triggerFlags` | `SOCK_NOTIFY_TRIGGER_ONESHOT` or `_PERSISTENT`, and `_LEVEL` or `_EDGE` | One-shot disables the registration (it is not removed) after the next notification. Exactly one of level and edge must be supplied |

The events that can come back are `SOCK_NOTIFY_EVENT_IN`, `_OUT`, `_HANGUP`, `_ERR` and `_REMOVE`. `_ERR` and `_REMOVE` can be indicated regardless of the event filter.

### Behavior worth knowing

- **Level and edge.** A level-triggered registration produces a notification whenever the condition holds while it is enabled. An edge-triggered registration produces one when the condition changes from not holding to holding while it is enabled, so after registering, the receive buffer of the socket must be drained completely to be sure a notification will come. Edge-triggered use needs non-blocking sockets and a receive loop that runs until `WSAEWOULDBLOCK`.
- **Coalescing.** Notifications for one socket are coalesced into a single notification, so one event mask can describe several events.
- **One registration per socket.** A socket can be registered with only one completion port, and only once. Registering again overwrites the registration. To change the completion key, remove the registration, wait for `SOCK_NOTIFY_EVENT_REMOVE` and register again.
- **Removal and memory.** `SOCK_NOTIFY_OP_REMOVE` produces `SOCK_NOTIFY_EVENT_REMOVE`, and after it no more notifications are issued for that completion key. Data structures that the registration refers to should be freed only after that event. Closing the socket with `closesocket` deregisters it automatically, but it produces no `REMOVE` event and notifications that were already queued might still be delivered.
- **No buffers.** A registration does not name a buffer. The documentation says that this avoids the need for permanent per-socket I/O buffers.

### What the documentation recommends

- Use a single completion port with several threads dequeuing, rather than one port per thread, because a port per thread can bottleneck on one thread while others are idle.
- For several threads and level-triggered notifications, use one-shot registrations so that several threads do not get notified of the same state change, and re-register after the notification is processed. This also makes it less likely that several threads receive fragments of one message on a stream connection.
- Do not combine one-shot with edge-triggered, because the socket must be drained after each enable, which is harder to implement and costs a call that returns `WSAEWOULDBLOCK`.
- For connection acceptance on a listening socket, use `AcceptEx` instead of notifications on the listener, because accepting in response to notifications limits how fast connections can be accepted.
- Overlapped I/O with a completion port might have less overhead when the number of sockets is small or the sockets are always busy, because it needs fewer kernel transitions and has a simpler model.

### Relation to overlapped I/O on a completion port

Both use a completion port, but the packets mean different things.

| | Overlapped I/O (`WSARecv`, `WSASend`) | `ProcessSocketNotifications` |
| --- | --- | --- |
| What the application submits | An operation with a buffer | A registration of interest in readiness |
| What a packet says | The operation completed, with a byte count | The socket is ready, with an event mask in the byte-count field |
| Buffer | Named when the operation is posted | Named only when the application calls `recv` or `send` afterwards |
| Idle socket | A posted receive holds its buffer | Nothing is posted |

### In terms of the prototype

This part is interpretation. The notification model is the same shape as the `Epoll` provider, readiness first and then non-blocking `recv` and `send`, so a provider built on it would choose its receive page after the notification, since the registration carries no buffer. The Windows providers already use `AcceptEx` for accepts, which is what the documentation recommends for the listener. No provider is built on this API, and whether it is faster or slower than `Iocp` for the prototype's workloads is not known.

## IoRing

### What it is

I/O rings are a Windows kernel API in `ioringapi.h`, filed under Data Access and Storage. The header page describes them as maintaining "a submission queue of I/O operations to be performed asynchronously and a completion queue containing the results of the completed operations". A ring is created with `CreateIoRing`, which returns a handle to a submission and completion queue pair. The pages that were read give build 22000 as the minimum Windows version.

### Lifecycle

```text
QueryIoRingCapabilities(&caps)
CreateIoRing(version, flags, submissionQueueSize, completionQueueSize, &ring)
BuildIoRingRegisterBuffers(ring, ...)          // optional
BuildIoRingReadFile(ring, fileRef, bufferRef, length, fileOffset, userData, sqeFlags)   // repeat
SubmitIoRing(ring, waitOperations, milliseconds, &submitted)
PopIoRingCompletion(ring, &cqe)                // until it returns S_FALSE
CloseIoRing(ring)
```

- **Create.** The submission queue size is rounded up to a power of two. The completion queue size is rounded up to a power of two that is at least twice the submission queue size, so that new operations can be submitted while others are still in progress. `GetIoRingInfo` returns the real sizes, and `QueryIoRingCapabilities` returns the maximum sizes and the maximum API version on the current system. The ring is closed with `CloseIoRing`, not `CloseHandle`.
- **Build.** The `BuildIoRing*` functions only write an entry into the submission queue. When the queue is full they return `IORING_E_SUBMISSION_QUEUE_FULL`, and the application has to submit and wait for completions before adding more. Each entry has a `userData` value that comes back in the completion.
- **Submit.** `SubmitIoRing` submits all entries that were built and not yet submitted, and can also wait until a given number of completions are available. If one entry fails while it is processed, a completion with an error is posted for that entry synchronously. If the call fails in another way, all entries stay in the submission queue.
- **Complete.** `PopIoRingCompletion` returns `S_OK` with an `IORING_CQE`, or `S_FALSE` when the queue is empty. The entry holds `UserData`, an `HRESULT` `ResultCode` and an `Information` value.
- **Wake-up.** `SetIoRingCompletionEvent` registers one event per ring. The kernel signals it only when the completion queue goes from empty to non-empty, so the application has to pop until the queue is empty before it waits on the event.

### Operations

The `ioringapi.h` page lists build functions for reading a file (documented as similar to `ReadFileEx`), cancelling a request, registering buffers and registering file handles. `IsIoRingOpSupported` tells whether an operation is supported on the system. The `IORING_OP_CODE` enumeration in `ntioring_x.h` lists `NOP`, `READ`, `REGISTER_FILES`, `REGISTER_BUFFERS`, `CANCEL`, `WRITE`, `FLUSH`, `READ_SCATTER` and `WRITE_GATHER`. All of these are file or buffer operations. No accept, connect, receive or send operation for sockets appears in the pages that were read.

### Registered buffers and file handles

Buffers and file handles can be referenced in two ways: directly (`IoRingBufferRefFromPointer`, `IoRingHandleRefFromHandle`) or by an index into a registration plus an offset (`IoRingBufferRefFromIndexAndOffset`, `IoRingHandleRefFromIndex`). A registration is itself an entry in the submission queue. It replaces any earlier registration completely, can contain sparse entries (`INVALID_HANDLE_VALUE`), requires the input array to stay valid until the operation completes, and acts as a link: entries after it do not start until it has completed.

### Cancellation

`BuildIoRingCancelRequest` identifies the operation by the `userData` it was built with, so `userData` must be unique when cancellation is used. The cancel completes after the cancelled operation completes, unless an error occurs, for example when no operation has that `userData`.

### What the pages do not describe

The pages that were read do not describe socket operations, multishot operations or buffers that the kernel picks when data arrives. This is a statement about those pages on the date above, not about every Windows build.

### In terms of the prototype

This part is interpretation. The shape matches the ring in the `IoUring` provider: queues of entries, one call that submits a batch, a `userData` that identifies the operation, and registered buffers. But without a socket operation there is nothing a transport could submit for accept, receive or send, so no provider can be built on the documented API today.

## Compared with the prototype's Windows providers

| | `Iocp` | `Rio` | `ProcessSocketNotifications` | `IoRing` |
| --- | --- | --- | --- | --- |
| Submitted to the OS | `WSARecv`, `WSASend` with a buffer | `RIOReceive`, `RIOSend` with a registered buffer | A registration of interest | Entries in a submission queue |
| What the OS reports | The operation completed | The operation completed | The socket is ready | The operation completed |
| Report arrives on | I/O completion port | RIO completion queue, with a port notification | I/O completion port | Completion queue of the ring |
| Buffer is named | When the operation is posted | When the operation is posted, from registered memory | When the application calls `recv` or `send` | When the entry is built |
| Sockets | Yes | Yes | Yes | No socket operation documented |

## Sources

- [ioringapi.h header](https://learn.microsoft.com/en-us/windows/win32/api/ioringapi/)
- [CreateIoRing](https://learn.microsoft.com/en-us/windows/win32/api/ioringapi/nf-ioringapi-createioring)
- [SubmitIoRing](https://learn.microsoft.com/en-us/windows/win32/api/ioringapi/nf-ioringapi-submitioring)
- [PopIoRingCompletion](https://learn.microsoft.com/en-us/windows/win32/api/ioringapi/nf-ioringapi-popioringcompletion)
- [SetIoRingCompletionEvent](https://learn.microsoft.com/en-us/windows/win32/api/ioringapi/nf-ioringapi-setioringcompletionevent)
- [BuildIoRingReadFile](https://learn.microsoft.com/en-us/windows/win32/api/ioringapi/nf-ioringapi-buildioringreadfile)
- [IORING_CQE](https://learn.microsoft.com/en-us/windows/win32/api/ioringapi/ns-ioringapi-ioring_cqe)
- [IORING_CAPABILITIES](https://learn.microsoft.com/en-us/windows/win32/api/ioringapi/ns-ioringapi-ioring_capabilities)
- [IORING_OP_CODE](https://learn.microsoft.com/en-us/windows/win32/api/ntioring_x/ne-ntioring_x-ioring_op_code)
- [ProcessSocketNotifications](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-processsocketnotifications)
- [SOCK_NOTIFY_REGISTRATION](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/ns-winsock2-sock_notify_registration)
- [SocketNotificationRetrieveEvents](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-socketnotificationretrieveevents)
- [Winsock socket state notifications](https://learn.microsoft.com/en-us/windows/win32/winsock/winsock-socket-state-notifications)
