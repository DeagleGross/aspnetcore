#!/usr/bin/env bash
set -euo pipefail
export SCHEME=http NETWORKPROTO_DIAGNOSTICS=1 NETWORKPROTO_DIRECT_SQ=0
export NETWORKPROTO_SYNC_SEND=0 NETWORKPROTO_SYNC_ACCEPT=0 NETWORKPROTO_SYNC_RECEIVE=0
export NETWORKPROTO_BATCH_SUBMISSIONS=0 NETWORKPROTO_COMBINED_WAIT=0
export NETWORKPROTO_ACCEPT_PAUSE_AT=1
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
OUT="${1:-$SAMPLE/results/accept-pressure-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$OUT"
OUT="$(realpath "$OUT")"
timeout 30s python3 "$SAMPLE/scripts/multishot-native.py" "$(dirname "$DLL")/libnetworkproto.so" | tee "$OUT/native.txt"
for mode in queued multishot; do
    export NETWORKPROTO_ACCEPT_MODE="$mode"
    start_server io_uring "$OUT/$mode-server.log"
    python3 "$SAMPLE/scripts/ring-pressure.py" "$(dirname "$DLL")/libnetworkproto.so" "$PORT" io_uring "$SERVER_PID" \
        | tee "$OUT/$mode.txt"
    stop_server
    check_server_log "$OUT/$mode-server.log"
    if grep -q 'warn:' "$OUT/$mode-server.log"; then
        cat "$OUT/$mode-server.log" >&2
        exit 1
    fi
done
python3 - "$OUT" <<'PY'
import json
import pathlib
import re
import sys

for mode in ("queued", "multishot"):
    text = (pathlib.Path(sys.argv[1]) / f"{mode}-server.log").read_text()
    c = json.loads(re.search(r"io_uring diagnostics (\{.+\})", text)[1])
    assert c["acceptMode"] == mode and c["acceptResults"] >= 514, c
    assert c["acceptPauses"] > 0 and c["acceptResumes"] > 0, c
    assert c["acceptTerminals"] == c["accepts"], c
    assert c["acceptQueueHighWater"] <= 1024 and c["acceptQueueDepth"] == c["acceptQueueOverflows"] == 0, c
    assert c["pendingOperations"] == c["queuedActions"] == c["cancellationAcknowledgments"] == 0, c
    assert c["directAttempts"] == c["directAcceptAttempts"] == c["directReceiveAttempts"] == 0, c
    if mode == "multishot":
        assert c["acceptMore"] > 0, c
    else:
        assert c["acceptMore"] == 0, c
    print(f"{mode}: actual pause/resume, accept ownership, terminal CQEs and ring-only execution: {c}")
PY
