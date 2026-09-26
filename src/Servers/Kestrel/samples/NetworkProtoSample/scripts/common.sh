#!/usr/bin/env bash
set -euo pipefail

SAMPLE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ROOT="$(cd "$SAMPLE/../../../../.." && pwd)"
cd "$ROOT"
set +u
source ./activate.sh >/dev/null
set -u
DEPS="$ROOT/artifacts/networkproto-deps/usr"
if [[ -d "$DEPS" ]]; then
    export CPATH="$DEPS/include${CPATH:+:$CPATH}"
    export LIBRARY_PATH="$DEPS/lib/x86_64-linux-gnu${LIBRARY_PATH:+:$LIBRARY_PATH}"
    export LD_LIBRARY_PATH="$DEPS/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
fi
DLL="$ROOT/artifacts/bin/NetworkProtoSample/Release/net11.0/NetworkProtoSample.dll"
PORT="${PORT:-5443}"
SCHEME="${SCHEME:-https}"
[[ "$SCHEME" == http || "$SCHEME" == https ]] || { echo "SCHEME must be http or https." >&2; exit 1; }
URL="$SCHEME://localhost:$PORT"
SERVER_PID=""
CLIENT_PID=""
OBSERVER_PID=""
TRACE_PID=""

certificates() {
    if [[ "$SCHEME" == http ]]; then return; fi
    mkdir -p "$SAMPLE/.certs"
    if [[ ! -e "$SAMPLE/.certs/cert.pem" && ! -e "$SAMPLE/.certs/key.pem" ]]; then
        (umask 077; openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 2 \
            -subj /CN=localhost -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" \
            -keyout "$SAMPLE/.certs/key.pem" -out "$SAMPLE/.certs/cert.pem" 2>"$SAMPLE/.certs/generate.log")
    fi
    [[ -f "$SAMPLE/.certs/cert.pem" && -f "$SAMPLE/.certs/key.pem" ]]
    openssl x509 -in "$SAMPLE/.certs/cert.pem" -checkend 60 -noout >/dev/null
}

curl_local() {
    local tls=()
    if [[ "$SCHEME" == https ]]; then
        tls=(--cacert "$SAMPLE/.certs/cert.pem" --tlsv1.2 --tls-max 1.2)
    fi
    curl --silent --show-error --fail --http1.1 --ipv4 --noproxy '*' \
        --connect-timeout 3 --max-time 10 "${tls[@]}" "$@"
}

start_server() {
    local backend="$1" logfile="$2"
    [[ -f "$DLL" ]] || { echo "Build NetworkProtoSample in Release first." >&2; return 1; }
    local prefix=()
    if [[ -n "${SERVER_CPUS:-}" ]]; then prefix=(taskset -c "$SERVER_CPUS"); fi
    DOTNET_PROCESSOR_COUNT="${SERVER_PROCESSORS:-4}" "${prefix[@]}" dotnet "$DLL" \
        --backend "$backend" --scheme "$SCHEME" --port "$PORT" --cert "$SAMPLE/.certs/cert.pem" \
        --key "$SAMPLE/.certs/key.pem" --Logging:LogLevel:Default Warning \
        --Logging:LogLevel:NetworkProto.IoUring Information \
        >"$logfile" 2>&1 &
    SERVER_PID=$!
    for ((attempt=0; attempt<100; attempt++)); do
        if ! kill -0 "$SERVER_PID" 2>/dev/null; then
            cat "$logfile" >&2
            local failed_pid="$SERVER_PID"
            SERVER_PID=""
            wait "$failed_pid"
            return 1
        fi
        if curl_local "$URL/metrics" >"$logfile.ready.json" 2>"$logfile.ready-error"; then
            python3 -c 'import json,sys; m=json.load(open(sys.argv[1])); assert m["backend"] == sys.argv[2] and m["scheme"] == sys.argv[3]' "$logfile.ready.json" "$backend" "$SCHEME"
            return
        fi
        sleep 0.1
    done
    echo "Server readiness timed out: $backend" >&2
    return 1
}

stop_server() {
    if [[ -n "$SERVER_PID" ]]; then
        local pid="$SERVER_PID"
        SERVER_PID=""
        kill -TERM "$pid"
        for ((attempt=0; attempt<100; attempt++)); do
            if ! kill -0 "$pid" 2>/dev/null; then
                wait "$pid"
                return
            fi
            sleep 0.1
        done
        echo "Server $pid failed graceful shutdown; terminating this exact PID." >&2
        kill -KILL "$pid"
        wait "$pid" || true
        return 1
    fi
}

cleanup() {
    local status=$?
    for pid in "$CLIENT_PID" "$OBSERVER_PID" "$TRACE_PID"; do
        if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
            kill -TERM "$pid"
            wait "$pid" || true
        fi
    done
    stop_server || status=1
    exit "$status"
}
trap cleanup EXIT

check_server_log() {
    if grep -E 'fail:|crit:|Unhandled exception|completion pump failed' "$1"; then
        echo "Server error found in $1" >&2
        return 1
    fi
}
