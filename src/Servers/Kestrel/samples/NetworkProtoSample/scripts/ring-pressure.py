import ctypes
import os
import pathlib
import runpy
import socket
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor

def native_checks(library):
    native = ctypes.CDLL(library)
    native.np_create_size.argtypes = [ctypes.c_uint, ctypes.POINTER(ctypes.c_int)]
    native.np_create_size.restype = ctypes.c_void_p
    native.np_stage.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint, ctypes.c_uint64]
    native.np_flush.argtypes = [ctypes.c_void_p]
    native.np_collect.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint64), ctypes.POINTER(ctypes.c_int), ctypes.c_uint, ctypes.c_int]
    native.np_submit_and_collect.argtypes = native.np_collect.argtypes
    native.np_destroy.argtypes = [ctypes.c_void_p]
    error = ctypes.c_int()
    ring = native.np_create_size(2, ctypes.byref(error))
    assert ring, error.value
    sockets = [socket.socketpair() for _ in range(4)]
    buffers = [ctypes.create_string_buffer(32) for _ in sockets]
    ids = (ctypes.c_uint64 * 64)()
    results = (ctypes.c_int * 64)()
    active = set()
    combined = os.environ.get("NETWORKPROTO_COMBINED_WAIT", "0") == "1"

    def flush():
        if not combined:
            assert native.np_flush(ring) >= 0

    def collect(expected):
        found = {}
        deadline = time.monotonic() + 5
        while len(found) < expected:
            assert time.monotonic() < deadline, (active, found)
            collect_native = native.np_submit_and_collect if combined else native.np_collect
            count = collect_native(ring, ids, results, 64, 0)
            assert count >= 0, count
            for i in range(count):
                assert ids[i] not in found, "Duplicate terminal completion"
                found[ids[i]] = results[i]
                active.discard(ids[i])
            if count == 0:
                time.sleep(0.001)
        return found

    try:
        pending = list(range(4))
        overflow_observed = False
        while pending:
            staged = []
            for i in pending:
                result = native.np_stage(ring, 1, sockets[i][0].fileno(), buffers[i], 32, i + 2)
                assert result in (0, 1), result
                if result == 0:
                    overflow_observed = True
                    break
                staged.append(i)
                active.add(i + 2)
            assert staged
            pending = pending[len(staged):]
            flush()
            for i in staged:
                sockets[i][1].sendall(bytes([65 + i]) * 8)
            completions = collect(len(staged))
            for i in staged:
                assert completions[i + 2] == 8
                assert buffers[i].raw[:8] == bytes([65 + i]) * 8
        assert overflow_observed, "Did not reach real SQ capacity"

        assert native.np_stage(ring, 1, sockets[0][0].fileno(), buffers[0], 32, 20) == 1
        assert native.np_stage(ring, 3, 0, None, 0, 20) == 1
        active.update([1, 20])
        flush()
        completions = collect(2)
        assert completions == {1: 0, 20: -125}, completions

        sockets[0][0].setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 4096)
        payload = ctypes.create_string_buffer(b"x" * 16383)
        assert native.np_stage(ring, 2, sockets[0][0].fileno(), payload, 16383, 21) == 1
        active.add(21)
        flush()
        sent = collect(1)[21]
        assert 0 < sent < 16383, f"Partial ring send not reached: {sent}"
        received = bytearray()
        sockets[0][1].settimeout(5)
        while len(received) < sent:
            received.extend(sockets[0][1].recv(sent - len(received)))
        assert received == b"x" * sent
        print(f"Native ring: real SQ-full/retry, buffer identity, terminal cancellation and partial send verified; combined={combined}.")
    finally:
        # Never release ctypes buffers while the kernel may still access them.
        if active:
            print(f"Probe failed with live kernel buffers: {active}; terminating without unwinding their owners.", file=sys.stderr, flush=True)
            os._exit(1)
        native.np_destroy(ring)
        for pair in sockets:
            for sock in pair:
                sock.close()


def burst_checks(port, backend, pid):
    barrier = threading.Barrier(32)
    ids = set()
    idle_fds = len(list(pathlib.Path(f"/proc/{pid}/fd").iterdir()))

    def run_batch(_):
        result = []
        for _ in range(16):
            with socket.create_connection(("127.0.0.1", port), timeout=10) as client:
                barrier.wait(timeout=10)
                client.sendall(b"GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
                with client.makefile("rb") as stream:
                    assert stream.readline() == b"HTTP/1.1 200 OK\r\n"
                    headers = {}
                    while (line := stream.readline()) != b"\r\n":
                        assert line
                        key, value = line.decode().rstrip("\r\n").split(": ", 1)
                        headers[key.lower()] = value
                    assert headers["x-backend"] == backend
                    assert headers["x-tls"] == "none"
                    assert headers["x-connection-request"] == "1"
                    assert stream.read(1024) == b"x" * 1023 + b"\n"
                    assert stream.read(1) == b""
                    result.append(headers["x-connection-id"])
        return result

    with ThreadPoolExecutor(max_workers=32) as pool:
        for batch in pool.map(run_batch, range(32)):
            for connection in batch:
                assert connection not in ids
                ids.add(connection)
    assert len(ids) == 512
    # Allow completed connections to finish async disposal; don't accept growing fd use.
    deadline = time.monotonic() + 5
    while len(list(pathlib.Path(f"/proc/{pid}/fd").iterdir())) > idle_fds + 8:
        assert time.monotonic() < deadline, "Server fd count did not return near its warmed baseline"
        time.sleep(0.01)
    print(f"{backend}: 512 synchronized close connections, correct bodies/EOF and bounded fd count.")


if __name__ == "__main__":
    native_checks(sys.argv[1])
    checks = runpy.run_path(str(pathlib.Path(__file__).with_name("send-pressure.py")))
    checks["check_slow_reader"](int(sys.argv[2]), sys.argv[3])
    burst_checks(int(sys.argv[2]), sys.argv[3], int(sys.argv[4]))
