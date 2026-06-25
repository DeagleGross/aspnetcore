#!/bin/bash
# Persistent-connection scenario: HTTP keep-alive, one TLS handshake per connection.
# Stresses steady-state record encrypt/decrypt and event-pump throughput.

PORT=${1:-5001}
DURATION=${2:-30}
THREADS=${3:-4}
CONNECTIONS=${4:-200}

echo "=== Persistent (keep-alive) scenario ==="
echo "Target: https://localhost:$PORT/"
echo "Duration: ${DURATION}s, Threads: $THREADS, Connections: $CONNECTIONS"
echo "Mode: HTTP keep-alive (1 handshake / connection)"
echo ""

wrk -t"$THREADS" -c"$CONNECTIONS" -d"${DURATION}s" \
    https://localhost:"$PORT"/ \
    --latency

echo ""
echo "Usage: $0 [port] [duration] [threads] [connections]"
echo "Example: $0 5001 30 4 200"
