#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
[[ "$SCHEME" == https ]] || { echo "rst-check.sh exercises HTTPS; use SCHEME=https." >&2; exit 1; }
certificates
OUT="${1:-$SAMPLE/results/rst-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$OUT"
OUT="$(realpath "$OUT")"
start_server io_uring "$OUT/server.log"
python3 - "$SAMPLE/.certs/cert.pem" "$PORT" <<'PY'
import socket
import ssl
import struct
import sys

context = ssl.create_default_context(cafile=sys.argv[1])
context.minimum_version = context.maximum_version = ssl.TLSVersion.TLSv1_2
with socket.create_connection(("127.0.0.1", int(sys.argv[2])), timeout=5) as tcp:
    with context.wrap_socket(tcp, server_hostname="localhost") as tls:
        tls.sendall(b"GET / HTTP/1.1\r\nHost: localhost\r\n\r\n")
        data = b""
        while b"\r\n\r\n" not in data:
            data += tls.recv(4096)
        headers, body = data.split(b"\r\n\r\n", 1)
        while len(body) < 1024:
            block = tls.recv(4096)
            assert block, "unexpected EOF"
            body += block
        assert headers.startswith(b"HTTP/1.1 200") and body == b"x" * 1023 + b"\n"
        assert b"X-Backend: io_uring" in headers
        tls.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
        # Closing the real TCP socket produces RST; no callback or CQE is injected.
print("Valid HTTPS response consumed; client deliberately reset the TCP connection.")
PY
for ((attempt=0; attempt<100; attempt++)); do
    if grep -qE 'peer reset|Connection reset by peer' "$OUT/server.log"; then break; fi
    sleep 0.05
done
stop_server
check_server_log "$OUT/server.log"
grep -q 'peer reset' "$OUT/server.log"
echo "Real client RST classified and logged without an unexpected transport failure: $OUT"
