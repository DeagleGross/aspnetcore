#!/bin/bash

# Benchmark measurement script - captures context switches, ThreadPool metrics, and RPS
# Usage: ./bench-measure.sh [port] [duration] [threads] [connections] [label]

PORT=${1:-5001}
DURATION=${2:-10}
THREADS=${3:-2}
CONNECTIONS=${4:-500}
LABEL=${5:-"baseline"}

PID=$(pgrep -f DirectSslTransportApp | head -1)
if [ -z "$PID" ]; then
    echo "ERROR: DirectSslTransportApp not running"
    exit 1
fi

# Verify PID is valid
if [ ! -f "/proc/$PID/status" ]; then
    echo "ERROR: /proc/$PID/status not found"
    exit 1
fi

echo "============================================"
echo "  Benchmark: $LABEL"
echo "  PID: $PID, Port: $PORT"
echo "  Duration: ${DURATION}s, Threads: $THREADS, Connections: $CONNECTIONS"
echo "============================================"

# Parse context switches summed across ALL threads (main thread only sees its own)
read_ctxt() {
    local pid=$1
    VOL=0
    INVOL=0
    for tid in $(ls /proc/$pid/task/ 2>/dev/null); do
        local v=$(awk '/^voluntary_ctxt_switches:/ {print $2}' /proc/$pid/task/$tid/status 2>/dev/null)
        local iv=$(awk '/^nonvoluntary_ctxt_switches:/ {print $2}' /proc/$pid/task/$tid/status 2>/dev/null)
        VOL=$((VOL + ${v:-0}))
        INVOL=$((INVOL + ${iv:-0}))
    done
}

# Capture context switches BEFORE
read_ctxt $PID
VOL_BEFORE=$VOL
INVOL_BEFORE=$INVOL

echo ""
echo "--- Context switches before ---"
echo "  voluntary:    $VOL_BEFORE"
echo "  nonvoluntary: $INVOL_BEFORE"

# Run wrk benchmark
echo ""
echo "--- Running wrk ---"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
WRK_OUTPUT=$(wrk -t$THREADS -c$CONNECTIONS -d${DURATION}s \
    -s "$SCRIPT_DIR/close-connection.lua" \
    https://localhost:$PORT/ \
    --latency 2>&1)

echo "$WRK_OUTPUT"

# Capture context switches AFTER
read_ctxt $PID
VOL_AFTER=$VOL
INVOL_AFTER=$INVOL

echo ""
echo "--- Context switches after ---"
echo "  voluntary:    $VOL_AFTER"
echo "  nonvoluntary: $INVOL_AFTER"

# Calculate deltas
VOL_DELTA=$((VOL_AFTER - VOL_BEFORE))
INVOL_DELTA=$((INVOL_AFTER - INVOL_BEFORE))
TOTAL_DELTA=$((VOL_DELTA + INVOL_DELTA))

# Extract RPS from wrk output
RPS=$(echo "$WRK_OUTPUT" | grep "Requests/sec" | awk '{print $2}')
TOTAL_REQUESTS=$(echo "$WRK_OUTPUT" | grep "requests in" | awk '{print $1}')

echo ""
echo "============================================"
echo "  RESULTS: $LABEL"
echo "============================================"
echo "  RPS:                        $RPS"
echo "  Total requests:             $TOTAL_REQUESTS"
echo "  Voluntary ctx switches:     $VOL_DELTA"
echo "  Involuntary ctx switches:   $INVOL_DELTA"
echo "  Total ctx switches:         $TOTAL_DELTA"
if [ -n "$RPS" ] && [ "$RPS" != "0" ]; then
    # Calculate ctx switches per request (integer math, multiply by 100 for 2 decimal places)
    CTX_PER_REQ=$(echo "scale=2; $TOTAL_DELTA / $TOTAL_REQUESTS" | bc 2>/dev/null || echo "N/A")
    echo "  Ctx switches / request:     $CTX_PER_REQ"
fi
echo "============================================"
