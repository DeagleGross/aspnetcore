#!/bin/bash
# Close-1/3 scenario: roughly 1 in 3 requests forces a new TLS handshake by
# sending `Connection: close`; the rest reuse the connection. Mixed workload.

PORT=${1:-5001}
DURATION=${2:-30}
THREADS=${3:-4}
CONNECTIONS=${4:-200}

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"

echo "=== Close 1/3 (Connection: close on ~33% of requests) ==="
echo "Target: https://localhost:$PORT/"
echo "Duration: ${DURATION}s, Threads: $THREADS, Connections: $CONNECTIONS"
echo "Mode: mixed (1 of every 3 requests forces new handshake)"
echo ""

wrk -t"$THREADS" -c"$CONNECTIONS" -d"${DURATION}s" \
    -s "$SCRIPT_DIR/close-one-third.lua" \
    https://localhost:"$PORT"/ \
    --latency

echo ""
echo "Usage: $0 [port] [duration] [threads] [connections]"
echo "Example: $0 5001 30 4 200"
