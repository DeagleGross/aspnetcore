# Scaling capabilities

This page answers one question for the `Rio` and `IoUringTcp` providers: after an application has started with a small CPU and memory budget and has registered its queues and buffers, can the transport use more CPUs and more memory, and can it give them back? That matters for deployments where a container, VM or pod is resized while the process runs.

Two things were done. First, two standalone repro programs were written and run on 2026-11-03, one on raw RIO and one on raw io_uring, to find out what the operating system APIs allow. Second, the prototype source was read to see what the providers do today. The two answers differ: the OS APIs allow more than the prototype uses.

All numbers come from loopback echo tests on one developer machine each. They are not benchmarks, they were measured once, and they were not independently reproduced. The repro programs are not part of this repository. The provider names and the `Iocp`, `Rio`, `Epoll` and `IoUringTcp` models are described in [PROTOTYPE.md](PROTOTYPE.md).

## Summary

| Question | RIO (repro) | io_uring (repro) | Prototype today |
| --- | --- | --- | --- |
| Add a worker after start | Yes. A new thread, completion port, RIO completion queue and registered slab; new connections can use it | Yes. A new thread, ring, listener and buffer group | No. The worker set is created when the transport binds |
| Do existing connections move to the new worker | No | No | Not applicable |
| How to use the new worker | The server closes some connections and the clients reconnect | The same | Not applicable |
| Grow queue capacity live | Yes. `RIOResizeCompletionQueue` 128 to 512 under load | Yes. `io_uring_resize_rings` SQ 128 to 1024 under load | No. The completion queue is 8192 and the submission queue is 2048, fixed |
| Shrink queue capacity live | Only down to what the live connections reserve | Only down to what the live entries need | No |
| Grow memory used for I/O live | Yes. Register one more slab; connections that were waiting for a buffer resume | Yes. Register one more provided-buffer group; receives have to be re-armed onto it | No. Lazy slabs up to a fixed cap on Windows; one fixed buffer set on io_uring |
| Shrink memory used for I/O live | Yes, after both deregister and free | Yes, once no receive is armed on the group | No |
| Retire a worker under load | Yes. Its clients reconnect to the remaining worker | Yes. 1.1 ms, its clients reconnect | No |

## How the tests were set up

Both repros run a loopback echo: each client sends 256 bytes, waits for the 256-byte reply and repeats. The clients run inside the same process as the server, so the process-level memory numbers below include the client threads. The server side has one worker per CPU. A worker is a thread pinned to a CPU that owns its queues, its memory and its connections, the same shape as the prototype's workers.

The tests start with one worker, a small queue and a small amount of registered memory, put load on it, and then change one thing at a time while the load keeps running. After each change the repro prints operations per second per worker, connection counts, how many clients were stalled, and the process memory.

| | Windows repro | Linux repro |
| --- | --- | --- |
| Language | C# calling the RIO function table directly | C with liburing 2.9 |
| Machine | Ryzen 9 7950X3D, 32 logical CPUs, Windows 11 build 26100 | The same machine, WSL2 kernel 6.18.33.2 |
| Per worker | Thread on one CPU, I/O completion port, RIO completion queue, registered slabs | Thread on one CPU, ring created with `SINGLE_ISSUER` and `DEFER_TASKRUN`, 16 KiB provided buffers, multishot receive, own `SO_REUSEPORT` listener |
| Run | `win-run3` | `linux-run3` |

Because the Linux run is under WSL2, its kernel numbers are those of a virtualized Linux kernel, not bare metal.

## RIO

### What was probed first

These probes run in a process that is already up.

| Probe | What was done | Result |
| --- | --- | --- |
| Registering memory | `RIORegisterBuffer` on 64 MiB that was already allocated with `VirtualAlloc` | The pages become resident at registration: working set +65.7 MiB, against +1.6 MiB for the `VirtualAlloc` alone |
| Releasing memory | `RIODeregisterBuffer`, then `VirtualFree` | Deregistering alone returns nothing. The working set came back (−64 MiB) only after `VirtualFree` |
| Completion queue capacity | A queue created with room for 4 entries, then eight request queues | Each request queue reserves its receive and send slots in the completion queue. Two fit; the third and later `RIOCreateRequestQueue` calls fail with `WSAENOBUFS` (10055) |
| Resizing the queue | `RIOResizeCompletionQueue` 4 to 64 with receives pending | Works. Shrinking below what the live request queues reserve fails with Winsock error 10059 |

### Scenario phases

One worker on CPU 0 at the start: 256 KiB registered (64 slots), completion queue of 128, 32 clients.

| Phase | What changed | What was measured |
| --- | --- | --- |
| A | Baseline: 32 clients | About 47,000 operations per second on one worker |
| B | Add 64 clients (96 total). The completion queue of 128 holds two entries per request queue, so only 64 connections fit | 64 connections admitted and 32 clients stalled. In the measurement window there were about 6,000 reconnect attempts and the worker logged about 7,000 error 10055. Throughput stays at about 42,000 |
| C | `RIOResizeCompletionQueue` 128 to 512 on the live worker | The extra connections are now admitted but have no receive buffer, because the pool still has 64 slots. 32 connections are parked and their clients stay stalled |
| D | Register one more 1 MiB slab on the live worker | The parked connections resume. 96 connections, 0 stalled, about 47,600 operations per second |
| E | Shrink the completion queue 512 to 128 while 96 connections need 192 entries | Refused with error 10059 |
| F | Add worker 1 (new thread on CPU 2, new port, new completion queue, new 1 MiB slab). Then close 48 connections on worker 0 | Worker 1 serves 0 operations per second until the shed, because existing connections do not move. After the shed the 48 clients reconnect and the split is 48 and 48. Total throughput goes from about 43,000 to about 86,000 operations per second |
| G | Shrink worker 0's completion queue 512 to 128 now that it has 48 connections (needs 96) | Works. 128 to 64 is refused |
| H | Stop all clients, deregister and free the extra slabs | Working set −1.8 MiB, private bytes −2.0 MiB |
| I | Retire worker 1 while 32 clients are connected to it | The worker thread exits. Its clients reconnect to worker 0, which then serves 64 connections with 0 stalled at about 44,000 operations per second |

### What this shows

- A completion queue is a hard admission limit, not a soft hint. When it is full the failure is at `RIOCreateRequestQueue`, so the connection cannot be served at all, and a client that retries creates a reconnect storm.
- The completion queue can grow while receives are pending. It can shrink only to what live request queues reserve.
- Registered memory is committed when it is registered, and it comes back only after deregistering and freeing. A transport that registers a slab on demand grows its footprint immediately by that slab. A transport that wants to give memory back has to track which slabs are idle.
- The `RIOResizeCompletionQueue` calls were made on the worker's own thread. Calling it from another thread while the worker dequeues was not tested.
- The worker loop has to interleave a command queue with the dequeue loop. In an earlier version of the repro the resize and slab commands starved under load because they were only looked at when the loop was idle.

## io_uring

### What was probed first

| Probe | What was done | Result |
| --- | --- | --- |
| Provided-buffer ring | `io_uring_setup_buf_ring` over 32 MiB of `mmap` memory that was never touched | Costs about 0 resident memory. The resident set grows only as buffers are used |
| Registering the same group again | A second `io_uring_setup_buf_ring` with the same group id | Fails with −17. A second group needs a new id |
| Fixed buffers | `io_uring_register_buffers` on 32 MiB, then unregister | Resident +32 MiB and pinned +32 MiB at registration. Unregistering unpins, but the resident set drops only when the memory is unmapped. A sparse table with a live `update` behaves the same |
| Closing a socket with a multishot receive armed | `close(fd)` while the receive is in flight | The peer does not see end of stream. After cancelling the receive first, the peer does. The prototype already does shutdown and cancel before close (`RingConnection.RequestClose`, line 705) |
| Control calls from another thread | `io_uring_resize_rings`, `io_uring_register_buffers_sparse` and `io_uring_setup_buf_ring` from a thread other than the ring's submitter, on a ring created with `SINGLE_ISSUER` and `DEFER_TASKRUN` | All refused with −17. These calls have to run on the ring's own thread |

### Scenario phases

One worker on CPU 0 at the start: submission queue of 128 and completion queue of 256, one buffer group of 16 buffers of 16 KiB, 32 clients.

| Phase | What changed | What was measured |
| --- | --- | --- |
| A | Baseline: 32 clients | About 92,000 operations per second on one worker. A buffer group of 16 is small, so receives end with `-ENOBUFS` at about 267,000 per second and are re-armed straight away |
| B | Add 64 clients (96 total) against the 16-buffer group | All 96 connections are served, 0 stalled, because io_uring does not refuse the connection. Receive failures with `-ENOBUFS` rise to about 886,000 per second and throughput is about 81,000 |
| C | Register a second group of 2048 buffers of 16 KiB (32 MiB virtual) on the live ring, then cancel the receives bound to group 1 so they re-arm on group 2 | Before migrating, new receives use group 2 and throughput is about 111,000. After migrating, `-ENOBUFS` is 0 and throughput is about 118,000. Resident memory goes from 3.0 to 11.0 MiB as the buffers are touched, not at registration |
| D | `io_uring_resize_rings` on the live ring | Submission queue 128 to 1024 works under load. 1024 to 16 with 96 live connections is refused with −75. Back to 128 works |
| E | Add worker 1 (new thread on CPU 2, new ring, new `SO_REUSEPORT` listener, 2048-buffer group). Then close 48 connections on worker 0 | Worker 1 serves 0 until the shed. After it the 48 clients reconnect and `SO_REUSEPORT` hashing places 69 on worker 0 and 27 on worker 1. Total throughput goes from about 118,000 to about 219,000 operations per second |
| F | Free group 1 on the loaded worker 0 | Works once no receive is armed on it. Freeing group 2 while 69 connections were still armed on it was refused by the repro itself |
| G | Retire worker 1 under load: cancel accept, close its listener, shut its connections down, drain, `io_uring_queue_exit` | Took 1.1 ms. 27 clients reconnected to worker 0, 0 stalled |
| H | Stop all clients, free the last group | Resident −8.0 MiB |

### What this shows

- A buffer shortage does not refuse a connection on io_uring. It ends the multishot receive with `-ENOBUFS`, and what happens next depends on the code. The prototype re-arms immediately (`RingConnection.cs` line 223 treats `Canceled` and `NoBuffers` as non-fatal and starts the read again), and the repro did the same. With an empty group that is a hot loop of about 770,000 to 890,000 re-arms per second that cost throughput but do not stall clients.
- A receive stays bound to the group it was armed with. Registering a new group fixes nothing until the armed receives are cancelled and re-armed, which is what Phase C does.
- A provided-buffer ring is virtual until used, so a large pool does not cost resident memory up front. A fixed-buffer registration does pin its memory at once.
- Queue and group changes are only valid on the ring's own thread when the ring uses `SINGLE_ISSUER`, which the prototype does. A command queue plus a wake-up is needed to reach it from outside, and the prototype's engine already has one (`Engine._commands` and the eventfd wake).
- `SO_REUSEPORT` hashes new connections across the listeners. A new worker receives a share, but not an even one: 69 and 27 in this run.

## What the prototype does today

These points come from reading the source, not from running it.

### Windows (`Rio` and `Iocp`)

- The workers are created once when the transport binds. `Transport.cs` builds a fixed `WindowsWorker[]` (line 99) and `Dispatch` hands each accepted socket to the next worker round-robin (lines 128-145). There is no way to add or remove a worker, and there is no rebalance of existing connections.
- The count and CPUs come from `NetworkEngineOptions.WorkerCount` and `WorkerProcessors` ([Primitives/src/NetworkEngine.cs](Primitives/src/NetworkEngine.cs)). Affinity uses `SetThreadAffinityMask` with a 64-bit mask, so only the first processor group is reachable (`Native.cs` line 227).
- Each worker's completion queue is created with 8192 entries. A worker adopts at most 4096 connections, which matches the 8192 entries at two per request queue.
- The pool caps are options on `WindowsEngineOptions`, the base of `IocpEngineOptions` and `RioEngineOptions`: `InputPoolMaximumBytes` (default 256 MiB per worker), `OutputPoolMaximumBytes` (default 512 MiB per worker) and `PoolIdleTrimDelay` (default 10 seconds). Earlier prototype builds hard-coded 32 MiB and 128 MiB, which made 4096 connections of 64 KiB abort. The caps are per worker, so the process total is the cap times `WorkerCount`.
- `NativeMemoryPool` allocates and registers 4 MiB slabs on demand. The worker also calls `TrimIdle` about every `PoolIdleTrimDelay / 4` (between 100 ms and 1 s), at most 8 slabs per pass and pool. A slab is freed only when every one of its slots is back on the free stack and it has been unused for the delay, so no lease and no pending I/O can reference it. For RIO the slab is deregistered with `RIODeregisterBuffer` and then freed. Input exhaustion parks the connection until a slot is free (`ConnectionState.cs` and `WaitForPool`). Output exhaustion throws.
- `RioApi` declares the `ResizeCompletionQueue` and `ResizeRequestQueue` entries in the function table but nothing calls them (`Rio/RioApi.cs` lines 50-51).

### Linux (`IoUringTcp`)

- Each engine is a thread pinned to one CPU with its own ring and its own `SO_REUSEPORT` listener (`Engine.cs`, `native.c` line 114). The engine array is created when the transport binds. When no processors are passed, the CPU list comes from `NETWORKPROTO_CPUS` and defaults to `0,2,4,6` (`Transport.cs` line 38). There is no way to add or remove an engine.
- The ring is created with 2048 submission entries and the `SINGLE_ISSUER` flag, plus `DEFER_TASKRUN` (`native.c` line 26), and one buffer ring of 2048 entries under group id 1 (line 29). Receive is multishot with buffer selection on group 1 (line 49 onwards).
- The receive buffers are a pinned managed array of 2048 pages of 16 KiB, 32 MiB per engine, allocated once at start (`Engine.cs` lines 22 and 129). There is no second group and no shrink. With the BIO TLS mode there is another 32 MiB array for ciphertext.
- Per-connection backpressure stops a connection from taking too many pages: at four leased pages the receive is cancelled and resumed when the application returns them (`RingConnection.cs` line 248).

## What ASP.NET Core users have to do

Neither provider scales by itself. With the prototype as it is today, the deployment decisions are made at startup and stay.

1. Size the workers before the host starts. Set `WorkerCount` and `WorkerProcessors` on `NetworkEngineOptions` to the CPUs the process should use at its peak, not at its start. A CPU that is added to the container later is not used by a transport that was built for fewer workers. This is a limit of the prototype, not of RIO or io_uring: the repros added and retired workers while running.
2. Plan memory for the high-water mark. On Windows the registered slabs are committed when they are created, so a burst raises the working set up to the per-worker caps. Idle slabs are unregistered and freed after `PoolIdleTrimDelay`, and the working set falls back close to the idle level. Set `InputPoolMaximumBytes`, `OutputPoolMaximumBytes` and `PoolIdleTrimDelay` on `RioEngineOptions` or `IocpEngineOptions`; `Timeout.InfiniteTimeSpan` turns trimming off. A trimmed pool has to allocate and, for RIO, register slabs again on the next burst. On Linux the 32 MiB receive array per engine is allocated at start and is neither configurable nor trimmed yet.
3. Expect new CPUs to be used only by new connections. Existing connections stay on the worker that accepted them in both models. To move load after adding a worker, something has to end connections so that clients reconnect, for example by limiting the lifetime of keep-alive connections or by closing them gracefully. On Linux the new connections are spread by `SO_REUSEPORT` hashing, not evenly, so a placement policy would need to be added if an even split matters.
4. Size the completion queue for RIO, and the buffer pool for io_uring, for the connection count the process has to hold, because the two fail differently. A full RIO completion queue refuses new connections. An empty io_uring buffer group keeps serving but loses throughput to the re-arm loop.
5. Container and VM resizing is a separate layer. The repros did not test a cgroup or hypervisor changing CPU or memory limits under a running .NET process. As far as is generally known, .NET reads the processor count once at startup, so code that sizes itself from `Environment.ProcessorCount` would see the old number. That was not tested here.

## What a production-ready version would need

These are prototype gaps. Workers, queues and pools are provider internals, so most of the work is inside the providers. Exposing pool sizes or a runtime worker count to applications would add options next to `WorkerCount` and `WorkerProcessors` on `NetworkEngineOptions`; the connection contract in [PUBLIC-API.md](PUBLIC-API.md) does not depend on it.

- A mutable worker set with a placement policy and a drain step that closes some connections when a worker is added or retired.
- Commands that reach a worker's own thread for resizing queues and adding or trimming buffer memory. The io_uring engine and the Windows worker already have command queues, and the repros show the loop has to give those commands time under load.
- On Windows, wrappers for `RIOResizeCompletionQueue` and `RIOResizeRequestQueue`, and a bound on registered memory that is tied to the connection count. Slab trimming and configurable caps exist now; a minimum number of warm slabs, and compaction of a 4 MiB slab that one long-lived lease keeps alive, do not.
- On io_uring, a buffer group that can be added, a way to re-arm receives onto it, and trimming of groups that no receive uses.

## Not tested

- Resizing a RIO completion queue from a thread other than the one dequeuing from it.
- Moving a live connection to another worker. For io_uring the descriptor is not bound to a ring, so cancelling the receive and re-arming it on another ring looks possible but needs care with sends in flight and with ordering. For RIO the request queue is created with its completion queue, and no RIO function changes that.
- A container or VM that changes its CPU or memory limits while the process runs.
- Any of this against the real prototype providers, with TLS, with HTTP traffic or with Kestrel on top.
- Native Linux. The Linux repro ran under WSL2.
