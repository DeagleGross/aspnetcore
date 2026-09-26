#!/usr/bin/env bash
set -euo pipefail
export SCHEME=http NETWORKPROTO_DIAGNOSTICS=1 NETWORKPROTO_DIRECT_SQ="${NETWORKPROTO_DIRECT_SQ:-0}"
export NETWORKPROTO_SYNC_SEND=0 NETWORKPROTO_SYNC_ACCEPT=0 NETWORKPROTO_SYNC_RECEIVE=0
export NETWORKPROTO_RING_ENTRIES=2
export NETWORKPROTO_ACCEPT_MODE="${NETWORKPROTO_ACCEPT_MODE:-oneshot}"
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
OUT="${1:-$SAMPLE/results/ring-pressure-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$OUT"
OUT="$(realpath "$OUT")"
for backend in sockets io_uring; do
    start_server "$backend" "$OUT/$backend-server.log"
    python3 "$SAMPLE/scripts/ring-pressure.py" "$(dirname "$DLL")/libnetworkproto.so" "$PORT" "$backend" "$SERVER_PID" \
        | tee "$OUT/$backend.txt"
    stop_server
    check_server_log "$OUT/$backend-server.log"
    if grep -q 'warn:' "$OUT/$backend-server.log"; then
        cat "$OUT/$backend-server.log" >&2
        exit 1
    fi
done
python3 - "$OUT/io_uring-server.log" <<'PY'
import json
import os
import pathlib
import re
import sys

c = json.loads(re.search(r"io_uring diagnostics (\{.+\})", pathlib.Path(sys.argv[1]).read_text())[1])
assert c["directSubmissions"] == (os.environ["NETWORKPROTO_DIRECT_SQ"] == "1") and c["ringEntries"] == 2, c
assert c["directAttempts"] == c["directAcceptAttempts"] == c["directReceiveAttempts"] == 0, c
assert c["accepts"] > 512 and c["sends"] > 512 and c["receives"] > 512, c
assert c["activeOperations"] == c["slotsInUse"] == 0, c
assert c["pendingOperations"] == c["queuedActions"] == c["cancellationAcknowledgments"] == 0, c
if c["directSubmissions"]:
    assert c["overflowEnqueued"] > 0 and c["overflowHighWater"] > 0, c
    assert c["completedOperations"] == c["accepts"] + c["receives"] + c["sends"], c
    assert c["slotCapacity"] < c["accepts"], "Slot reuse was not exercised"
assert c["batchSubmissions"] == (os.environ.get("NETWORKPROTO_BATCH_SUBMISSIONS", "0") == "1"), c
assert c["combinedWait"] == (os.environ.get("NETWORKPROTO_COMBINED_WAIT", "0") == "1"), c
if c["batchSubmissions"]:
    assert c["submissionBatches"] > 0 and c["largestSubmissionBatch"] > 1, c
print(f"Ring-only execution and terminal ownership; slot/overflow assertions apply when directSubmissions is true: {c}")
PY
