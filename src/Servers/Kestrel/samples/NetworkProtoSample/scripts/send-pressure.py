import ctypes
import os
import select
import socket
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor


def check_native_accept_receive(library):
    native = ctypes.CDLL(library)
    accept = native.np_try_accept
    accept.argtypes = [ctypes.c_ssize_t]
    accept.restype = ctypes.c_int
    receive = native.np_try_recv
    receive.argtypes = [ctypes.c_ssize_t, ctypes.c_void_p, ctypes.c_uint]
    receive.restype = ctypes.c_int
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        listener.setblocking(False)
        assert accept(listener.fileno()) == -11, "Empty backlog must return EAGAIN"
        with socket.create_connection(listener.getsockname(), timeout=5) as client:
            fd = accept(listener.fileno())
            assert fd >= 0, fd
            with socket.socket(fileno=fd) as accepted:
                assert not os.get_inheritable(fd), "Accepted fd must have CLOEXEC"
                buffer = ctypes.create_string_buffer(128)
                assert receive(fd, buffer, 128) == -11, "Empty receive must return EAGAIN"
                client.sendall(b"fragment")
                received = bytearray()
                while len(received) < 8:
                    assert select.select([accepted], [], [], 5)[0], "Data did not arrive"
                    count = receive(fd, buffer, 128)
                    assert count > 0, count
                    received.extend(buffer.raw[:count])
                assert received == b"fragment"
                client.shutdown(socket.SHUT_WR)
                assert select.select([accepted], [], [], 5)[0], "FIN did not arrive"
                assert receive(fd, buffer, 128) == 0, "FIN must return EOF"
        assert accept(listener.fileno()) == -11
    print("Native accept/receive: empty-backlog EAGAIN, queued accept, CLOEXEC, data, receive EAGAIN and EOF verified.")


def check_native_send(library):
    native = ctypes.CDLL(library)
    send = native.np_try_send
    send.argtypes = [ctypes.c_ssize_t, ctypes.c_void_p, ctypes.c_uint]
    send.restype = ctypes.c_int
    payload = ctypes.create_string_buffer(b"x" * 16383)
    writer, reader = socket.socketpair()
    with writer, reader:
        writer.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 4096)
        received = bytearray()
        total = 0
        partial = False
        for _ in range(100):
            started = time.monotonic()
            count = send(writer.fileno(), payload, 16383)
            assert time.monotonic() - started < 1, "MSG_DONTWAIT send blocked"
            if count == -11:
                break
            assert count > 0, count
            partial |= count < 16383
            total += count
        else:
            raise AssertionError("Failed to reach real socket backpressure")
        assert partial, "Partial-send branch was not reached"
        while len(received) < total:
            received.extend(reader.recv(total - len(received)))
        assert received == b"x" * total
        assert send(writer.fileno(), payload, 128) == 128
        assert reader.recv(128) == b"x" * 128
        reader.close()
        assert send(writer.fileno(), payload, 128) == -32, "EPIPE must remain explicit"
    print("Native send: real partial write, EAGAIN, recovery and EPIPE verified.")


def check_slow_reader(port, backend):
    count = 8192
    request = b"GET / HTTP/1.1\r\nHost: localhost\r\n\r\n"
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as client:
        client.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4096)
        client.settimeout(30)
        client.connect(("127.0.0.1", port))
        started = threading.Event()

        def write_requests():
            started.set()
            client.sendall(request * count)

        with ThreadPoolExecutor(max_workers=1) as executor:
            sending = executor.submit(write_requests)
            assert started.wait(5)
            # Deliberately withhold reads; the caller asserts actual EAGAIN diagnostics.
            time.sleep(1)
            connection_id = None
            with client.makefile("rb") as stream:
                for index in range(1, count + 1):
                    assert stream.readline() == b"HTTP/1.1 200 OK\r\n"
                    headers = {}
                    while (line := stream.readline()) != b"\r\n":
                        assert line, "Unexpected EOF in headers"
                        key, value = line.decode().rstrip("\r\n").split(": ", 1)
                        headers[key.lower()] = value
                    assert headers["x-backend"] == backend
                    assert headers["x-tls"] == "none"
                    assert int(headers["x-connection-request"]) == index
                    assert int(headers["content-length"]) == 1024
                    if connection_id is None:
                        connection_id = headers["x-connection-id"]
                    assert headers["x-connection-id"] == connection_id
                    assert stream.read(1024) == b"x" * 1023 + b"\n"
            sending.result(timeout=5)
    print(f"{backend}: {count} pipelined responses intact after slow-reader backpressure.")


if __name__ == "__main__":
    check_native_send(sys.argv[1])
    check_native_accept_receive(sys.argv[1])
    check_slow_reader(int(sys.argv[2]), sys.argv[3])
