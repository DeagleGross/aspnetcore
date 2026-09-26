#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
certificates
OUT="${1:-$SAMPLE/results/wrk-$(date -u +%Y%m%dT%H%M%SZ)}"
DURATION="${DURATION:-10}"
WARMUP="${WARMUP:-3}"
CONNECTIONS="${CONNECTIONS:-16}"
THREADS="${THREADS:-2}"
WRK="${WRK:-wrk}"
REPETITIONS="${REPETITIONS:-1}"
[[ "$DURATION" =~ ^[0-9]+$ && "$DURATION" -ge 1 && "$DURATION" -le 30 ]]
[[ "$WARMUP" =~ ^[0-9]+$ && "$WARMUP" -ge 1 && "$WARMUP" -le 30 ]]
[[ "$REPETITIONS" =~ ^[0-9]+$ && "$REPETITIONS" -ge 1 && "$REPETITIONS" -le 5 ]]
mkdir -p "$OUT"
OUT="$(realpath "$OUT")"
client_prefix=()
if [[ -n "${CLIENT_CPUS:-}" ]]; then client_prefix=(taskset -c "$CLIENT_CPUS"); fi
{
    date -u --iso-8601=seconds
    git rev-parse HEAD
    git branch --show-current
    git status --short
    uname -a
    cat /etc/os-release
    lscpu
    dotnet --info
    gcc --version
    openssl version -a
    curl --version
    "$WRK" --version 2>&1 || true
    sha256sum "$(command -v "$WRK")"
    echo "SERVER_CPUS=${SERVER_CPUS:-unrestricted} CLIENT_CPUS=${CLIENT_CPUS:-unrestricted} DOTNET_PROCESSOR_COUNT=${SERVER_PROCESSORS:-4}"
    echo "duration=$DURATION warmup=$WARMUP threads=$THREADS connections=$CONNECTIONS repetitions=$REPETITIONS payload=1024"
    echo "NETWORKPROTO_SYNC_SEND=${NETWORKPROTO_SYNC_SEND:-0} NETWORKPROTO_DIAGNOSTICS=${NETWORKPROTO_DIAGNOSTICS:-0}"
    echo "NETWORKPROTO_SYNC_ACCEPT=${NETWORKPROTO_SYNC_ACCEPT:-0} NETWORKPROTO_SYNC_RECEIVE=${NETWORKPROTO_SYNC_RECEIVE:-0}"
    echo "NETWORKPROTO_DIRECT_SQ=${NETWORKPROTO_DIRECT_SQ:-0} NETWORKPROTO_RING_ENTRIES=${NETWORKPROTO_RING_ENTRIES:-1024}"
    echo "NETWORKPROTO_STAGE_ON_PUMP=${NETWORKPROTO_STAGE_ON_PUMP:-1}"
    echo "NETWORKPROTO_BATCH_SUBMISSIONS=${NETWORKPROTO_BATCH_SUBMISSIONS:-0} NETWORKPROTO_COMBINED_WAIT=${NETWORKPROTO_COMBINED_WAIT:-0}"
    echo "NETWORKPROTO_ACCEPT_MODE=${NETWORKPROTO_ACCEPT_MODE:-queued} NETWORKPROTO_ACCEPT_PAUSE_AT=${NETWORKPROTO_ACCEPT_PAUSE_AT:-256}"
    echo "NETWORKPROTO_DEFER_TASKRUN=${NETWORKPROTO_DEFER_TASKRUN:-1}"
    echo "NETWORKPROTO_SHUTDOWN_CLOSE=${NETWORKPROTO_SHUTDOWN_CLOSE:-1}"
    echo "NETWORKPROTO_REUSE_OPERATIONS=${NETWORKPROTO_REUSE_OPERATIONS:-1}"
    echo "NETWORKPROTO_INLINE_RECEIVE=${NETWORKPROTO_INLINE_RECEIVE:-1}"
    echo "NETWORKPROTO_COMPACT_CONNECTIONS=${NETWORKPROTO_COMPACT_CONNECTIONS:-1}"
    echo "scheme=$SCHEME HTTP/1.1; HTTPS uses TLS1.2 ECDHE-RSA-AES128-GCM-SHA256 with resumption unverified; HTTP has no TLS"
    echo "wrk does not verify the disposable test certificate; curl smoke verifies it using --cacert."
    sha256sum "$DLL" "$(dirname "$DLL")/Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.dll" \
        "$(dirname "$DLL")/Microsoft.AspNetCore.Server.Kestrel.Core.dll" "$(dirname "$DLL")/libnetworkproto.so"
    if [[ -d "$ROOT/artifacts/networkproto-deps" ]]; then
        for package in "$ROOT"/artifacts/networkproto-deps/*.deb; do
            dpkg-deb --show "$package"
            sha256sum "$package"
        done
    fi
} >"$OUT/environment.txt"

for ((round=1; round<=REPETITIONS; round++)); do
for backend in ${BACKENDS:-sockets io_uring}; do
    for mode in ${MODES:-keepalive close}; do
        [[ "$mode" == keepalive || "$mode" == close ]] || { echo "Unknown connection mode: $mode" >&2; exit 1; }
        prefix="$OUT/r$round-$backend-$mode"
        start_server "$backend" "$prefix-server.log"
        grep -E 'libcoreclr.so|libssl.so|liburing.so' "/proc/$SERVER_PID/maps" >"$prefix-libraries.txt"
        for phase in warmup measured; do
            seconds="$DURATION"
            if [[ "$phase" == warmup ]]; then seconds="$WARMUP"; fi
            output="$prefix-$phase"
            curl_local "$URL/metrics" >"$output.before.json"
            command=("${client_prefix[@]}" bash -c 'taskset -pc "$$" >"$1"; shift; exec "$@"' _ "$output.client-affinity.txt" \
                "$WRK" -t "$THREADS" -c "$CONNECTIONS" -d "${seconds}s" \
                --timeout 5s --latency -s "$SAMPLE/scripts/verify-wrk.lua" "$SCHEME://127.0.0.1:$PORT/" -- "$mode" "$backend" "$SCHEME")
            printf '%q ' "${command[@]}" >>"$OUT/commands.txt"
            printf '\n' >>"$OUT/commands.txt"
            date +%s%3N >"$output.start-ms"
            cut -d ' ' -f 1 /proc/uptime >"$output.start-monotonic"
            "${command[@]}" >"$output.txt" 2>&1 &
            CLIENT_PID=$!
            {
                taskset -pc "$SERVER_PID"
            } >"$output.affinity.txt"
            if [[ "$phase" == measured ]]; then
                pidstat -u -r -w -t -p "$SERVER_PID,$CLIENT_PID" 1 "$seconds" >"$output.pidstat.txt" &
                OBSERVER_PID=$!
                if [[ "${TRACE:-0}" == 1 ]]; then
                    dotnet-trace collect --process-id "$SERVER_PID" --profile dotnet-sampled-thread-time,gc-collect \
                        --duration "00:00:00:$seconds" --output "$output.nettrace" --format Speedscope \
                        >"$output.trace-log.txt" 2>&1 &
                    TRACE_PID=$!
                fi
            fi
            wait "$CLIENT_PID"
            CLIENT_PID=""
            date +%s%3N >"$output.end-ms"
            cut -d ' ' -f 1 /proc/uptime >"$output.end-monotonic"
            if [[ -n "$OBSERVER_PID" ]]; then
                wait "$OBSERVER_PID"
                OBSERVER_PID=""
            fi
            if [[ -n "$TRACE_PID" ]]; then
                wait "$TRACE_PID"
                TRACE_PID=""
                dotnet-trace report "$output.nettrace" topN -n 30 >"$output.top-exclusive.txt"
                dotnet-trace report "$output.nettrace" topN -n 30 --inclusive >"$output.top-inclusive.txt"
            fi
            curl_local "$URL/metrics" >"$output.after.json"
            python3 "$SAMPLE/scripts/verify-wrk.py" "$output" "$mode" "$seconds" | tee "$output.verification.txt"
            cat "$output.txt"
        done
        stop_server
        check_server_log "$prefix-server.log"
        python3 "$SAMPLE/scripts/verify-server.py" "$prefix" "$CONNECTIONS" | tee "$prefix-server-verification.txt"
    done
done
done
echo "Validated comparison completed: $OUT"
