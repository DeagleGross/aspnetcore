#!/usr/bin/env bash
set -euo pipefail
export SCHEME=http NETWORKPROTO_DIAGNOSTICS=1 NETWORKPROTO_SYNC_SEND=1
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
OUT="${1:-$SAMPLE/results/send-pressure-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$OUT"
OUT="$(realpath "$OUT")"
for backend in sockets io_uring; do
    start_server "$backend" "$OUT/$backend-server.log"
    python3 "$SAMPLE/scripts/send-pressure.py" "$(dirname "$DLL")/libnetworkproto.so" "$PORT" "$backend" \
        | tee "$OUT/$backend.txt"
    stop_server
    check_server_log "$OUT/$backend-server.log"
done
python3 - "$OUT/io_uring-server.log" <<'PY'
import json
import pathlib
import re
import sys

text = pathlib.Path(sys.argv[1]).read_text()
assert "warn:" not in text, text
counters = json.loads(re.search(r"io_uring diagnostics (\{.+\})", text)[1])
assert counters["directWouldBlock"] > 0 and counters["sends"] > 0, counters
assert counters["directCompletions"] > 0, counters
print(f"Real Kestrel slow-reader fallback reached: {counters}")
PY
