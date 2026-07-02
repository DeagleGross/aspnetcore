#!/bin/bash
# Orchestrates the full TlsSession vs OpenSslDirect A/B sweep across all three
# scenarios (persistent / close-all / close-1-3) and prints a side-by-side
# summary of RPS and latency at the end.
#
# Usage: ./run-ab.sh [duration_seconds] [threads] [connections]
# Example: ./run-ab.sh 30 4 200

set -u

DURATION=${1:-30}
THREADS=${2:-4}
CONNECTIONS=${3:-200}
PORT=5001

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
APP_DIR="$(cd -- "$SCRIPT_DIR/.." &>/dev/null && pwd)"
OUT_DIR="${OUT_DIR:-/tmp/directssl-ab-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUT_DIR"

echo "Output: $OUT_DIR"
echo "App:    $APP_DIR"
echo "Load:   ${DURATION}s, ${THREADS} threads, ${CONNECTIONS} connections"
echo

cleanup() {
    if [ -n "${SERVER_PID:-}" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
        kill "$SERVER_PID" 2>/dev/null || true
        wait "$SERVER_PID" 2>/dev/null || true
    fi
    # belt-and-suspenders: drop any stray listener on the port
    sudo -n fuser -k "$PORT/tcp" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

run_scenario() {
    local engine=$1
    local scenario=$2
    local script_name=$3
    local out="$OUT_DIR/${engine}-${scenario}.wrk"

    echo "===== $engine / $scenario ====="

    # start server
    DIRECTSSL_ENGINE=$engine dotnet run -c Release --no-build \
        --project "$APP_DIR" \
        >"$OUT_DIR/${engine}-${scenario}.srv" 2>&1 &
    SERVER_PID=$!
    sleep 6

    # wait until it's actually accepting
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        if curl -sk -o /dev/null --max-time 2 "https://localhost:$PORT/"; then break; fi
        sleep 1
    done

    # run load
    bash "$SCRIPT_DIR/$script_name" "$PORT" "$DURATION" "$THREADS" "$CONNECTIONS" | tee "$out"

    # stop server
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
    SERVER_PID=
    sleep 2
}

for engine in OpenSslDirect Hybrid; do
    run_scenario "$engine" "persistent" "wrk-persistent.sh"
    run_scenario "$engine" "close13"    "wrk-close13.sh"
    run_scenario "$engine" "closeall"   "wrk-closeall.sh"
done

echo
echo "================ SUMMARY ================"
printf '%-18s %-12s %18s %18s %18s\n' "Scenario" "Engine" "RPS" "Lat avg" "Lat p99"
echo "-------------------------------------------------------------------------------------"
for scenario in persistent close13 closeall; do
    for engine in OpenSslDirect Hybrid; do
        f="$OUT_DIR/${engine}-${scenario}.wrk"
        [ -f "$f" ] || continue
        rps=$(grep -E 'Requests/sec' "$f" | awk '{print $2}')
        lat_avg=$(grep -E '^\s*Latency' "$f" | head -1 | awk '{print $2}')
        lat_p99=$(grep -E '99%' "$f" | head -1 | awk '{print $2}')
        printf '%-18s %-12s %18s %18s %18s\n' "$scenario" "$engine" "${rps:-?}" "${lat_avg:-?}" "${lat_p99:-?}"
    done
done

echo
echo "Raw output files in: $OUT_DIR"
