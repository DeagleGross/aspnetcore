import ctypes
import os
import signal
import socket
import sys


def timeout(signum, frame):
    raise TimeoutError("Native multishot accept did not make progress")


signal.signal(signal.SIGALRM, timeout)
signal.alarm(20)
native = ctypes.CDLL(sys.argv[1])
native.np_create_size.argtypes = [ctypes.c_uint, ctypes.POINTER(ctypes.c_int)]
native.np_create_size.restype = ctypes.c_void_p
native.np_stage.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint, ctypes.c_uint64]
native.np_flush.argtypes = [ctypes.c_void_p]
native.np_wait.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint64), ctypes.POINTER(ctypes.c_int), ctypes.c_uint, ctypes.POINTER(ctypes.c_uint)]
native.np_destroy.argtypes = [ctypes.c_void_p]
error = ctypes.c_int()
ring = native.np_create_size(32, ctypes.byref(error))
assert ring, error.value
ids = (ctypes.c_uint64 * 64)()
results = (ctypes.c_int * 64)()
flags = (ctypes.c_uint * 64)()
clients = []
accepted = []
listener = socket.socket()
listener.bind(("127.0.0.1", 0))
listener.listen(32)


def collect():
    count = native.np_wait(ring, ids, results, 64, flags)
    assert count > 0, count
    return [(ids[i], results[i], flags[i]) for i in range(count)]


try:
    for generation, connection_count in ((2, 8), (3, 3)):
        assert native.np_stage(ring, 4, listener.fileno(), None, 0, generation) == 1
        assert native.np_flush(ring) >= 0
        for i in range(connection_count):
            client = socket.create_connection(listener.getsockname(), timeout=5)
            client.sendall(bytes([65 + i]))
            clients.append(client)
        received = []
        while len(received) < connection_count:
            for operation, result, flag in collect():
                assert operation == generation and result >= 0 and flag & 2, (operation, result, flag)
                assert not os.get_inheritable(result), "Accepted descriptor was not close-on-exec"
                server = socket.socket(fileno=result)
                accepted.append(server)
                server.settimeout(5)
                received.append(server.recv(1))
        assert sorted(received) == [bytes([65 + i]) for i in range(connection_count)], received
        assert native.np_stage(ring, 3, 0, None, 0, generation) == 1
        terminal = {}
        while len(terminal) < 2:
            for operation, result, flag in collect():
                assert operation not in terminal and not flag & 2, (operation, result, flag)
                terminal[operation] = result
        assert terminal == {1: 0, generation: -125}, terminal
    print("Native multishot: one accept SQE produced 8 MORE CQEs with real sockets; terminal cancellation, fd flags, data and new-generation rearm passed.")
finally:
    native.np_destroy(ring)
    listener.close()
    for sock in accepted + clients:
        sock.close()
    signal.alarm(0)
