#!/bin/bash
# Close-All scenario: every request forces a brand-new TLS handshake by sending
# `Connection: close`. Stresses handshake / accept / TLS-session-resume paths.

PORT=${1:-5001}
DURATION=${2:-30}
THREADS=${3:-4}
CONNECTIONS=${4:-200}

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"

echo "=== Close-All (Connection: close on every request) ==="
echo "Target: https://localhost:$PORT/"
echo "Duration: ${DURATION}s, Threads: $THREADS, Connections: $CONNECTIONS"
echo "Mode: new handshake per request"
echo ""

wrk -t"$THREADS" -c"$CONNECTIONS" -d"${DURATION}s" \
    -s "$SCRIPT_DIR/close-connection.lua" \
    https://localhost:"$PORT"/ \
    --latency

echo ""
echo "Usage: $0 [port] [duration] [threads] [connections]"
echo "Example: $0 5001 30 4 200"
