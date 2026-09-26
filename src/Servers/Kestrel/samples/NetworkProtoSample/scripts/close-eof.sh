#!/usr/bin/env bash
set -euo pipefail
export SCHEME=http NETWORKPROTO_DIAGNOSTICS=1
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
OUT="${1:-$SAMPLE/results/close-eof-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$OUT"
OUT="$(realpath "$OUT")"
for backend in sockets io_uring; do
    start_server "$backend" "$OUT/$backend-server.log"
    python3 - "$PORT" "$backend" <<'PY' | tee "$OUT/$backend.txt"
import socket
import sys

ids = set()
for _ in range(256):
    with socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=5) as client:
        client.sendall(b"GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
        with client.makefile("rb") as stream:
            assert stream.readline() == b"HTTP/1.1 200 OK\r\n"
            headers = {}
            while (line := stream.readline()) != b"\r\n":
                assert line, "Unexpected EOF before complete headers"
                key, value = line.decode().rstrip("\r\n").split(": ", 1)
                headers[key.lower()] = value
            assert headers["x-backend"] == sys.argv[2]
            assert headers["x-tls"] == "none"
            assert headers["connection"].lower() == "close"
            assert headers["x-connection-request"] == "1"
            assert headers["content-length"] == "1024"
            assert stream.read(1024) == b"x" * 1023 + b"\n"
            # Do not send client FIN: the server must initiate an orderly close.
            assert stream.read(1) == b"", "Expected server EOF, not more bytes"
            assert headers["x-connection-id"] not in ids
            ids.add(headers["x-connection-id"])
print(f"{sys.argv[2]}: 256 unique TCP connections, complete HTTP responses and server-initiated EOF; no client FIN or reset needed.")
PY
    stop_server
    check_server_log "$OUT/$backend-server.log"
    if grep -q 'warn:' "$OUT/$backend-server.log"; then
        cat "$OUT/$backend-server.log" >&2
        exit 1
    fi
done
python3 - "$OUT/io_uring-server.log" <<'PY'
import json
import pathlib
import re
import sys

counters = json.loads(re.search(r"io_uring diagnostics (\{.+\})", pathlib.Path(sys.argv[1]).read_text())[1])
if counters["shutdownOnClose"]:
    # Listener unbind/disposal can target the final pending accept.
    assert counters["cancellationRequests"] <= 2, counters
print(f"Completion ownership/cancellation counters: {counters}")
PY
