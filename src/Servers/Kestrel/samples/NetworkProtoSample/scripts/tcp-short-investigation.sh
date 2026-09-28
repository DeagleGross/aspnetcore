#!/usr/bin/env bash
set -eo pipefail
sample=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
root=$(cd "$sample/../../../../.." && pwd)
cd "$root"
set +e
source activate.sh >/dev/null
set -e
set -u
backend=${1:-IoUringTcp}
rate=${2:-200000}
label=${3:-baseline}
port=${PORT:-19400}
dll=${SAMPLE_DLL:-"$root/artifacts/bin/NetworkProtoSample/Release/net11.0/NetworkProtoSample.dll"}
output="$sample/results/tcp-short-$label-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$output"
export LD_LIBRARY_PATH="/opt/openssl-3.5.8/lib:$HOME/.local/lib"
pid='' observer=''
cleanup() {
    for child in "$observer"; do
        if [[ -n $child ]]; then
            if kill -0 "$child" 2>/dev/null; then kill -TERM "$child"; fi
            wait "$child" || true
        fi
    done
    if [[ -n $pid ]]; then
        if kill -0 "$pid" 2>/dev/null; then kill -TERM "$pid"; fi
        for i in $(seq 1 100); do
            if ! kill -0 "$pid" 2>/dev/null; then break; fi
            sleep .1
        done
        if kill -0 "$pid" 2>/dev/null; then
            echo SHUTDOWN_TIMEOUT >&2
            kill -KILL "$pid"
            wait "$pid" || true
            pid=''
            return 1
        fi
        local result=0
        wait "$pid" || result=$?
        pid=''
        return "$result"
    fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
NETWORKPROTO_COALESCE=1 DOTNET_PROCESSOR_COUNT=4 taskset -c 0,2,4,6 dotnet "$dll" \
    --backend "$backend" --scheme http --port "$port" --minimal true --Logging:LogLevel:Default Warning \
    > "$output/server.log" 2>&1 &
pid=$!
for i in $(seq 1 100); do
    if curl -fsS --max-time 1 "http://127.0.0.1:$port/metrics" > "$output/ready.json" 2>/dev/null; then break; fi
    kill -0 "$pid"
    sleep .1
done
[[ -s $output/ready.json ]]
command=(taskset -c 8,10,12,14,16,18,20,22,24,26,28,30 "${WRK2:-$HOME/code/wrk2/wrk}"
    -t12 "-c${CONNECTIONS:-1200}" "-R$rate" --timeout 2s -H "Connection: close" "http://127.0.0.1:$port/")
"${command[@]}" -d3s > "$output/warmup.txt" 2>&1
sleep 1
curl -fsS --max-time 3 "http://127.0.0.1:$port/metrics" > "$output/before.json"
cat /proc/$pid/stat > "$output/process-before.stat"
cat /proc/net/netstat > "$output/netstat-before.txt"
pidstat -u -w -t -p "$pid" 1 15 > "$output/threads.txt" &
observer=$!
/usr/bin/time -f 'client_user_seconds=%U client_system_seconds=%S' -o "$output/client-cpu.txt" \
    timeout --kill-after=2s 35s "${command[@]}" -d15s --latency > "$output/wrk.txt" 2>&1
wait "$observer"
observer=''
cat /proc/$pid/stat > "$output/process-after.stat"
cat /proc/net/netstat > "$output/netstat-after.txt"
curl -fsS --max-time 3 "http://127.0.0.1:$port/metrics" > "$output/after.json"
{
    printf 'server_side_timewait='
    ss -Htan state time-wait "( sport = :$port )" | wc -l
    printf 'client_side_timewait='
    ss -Htan state time-wait "( dport = :$port )" | wc -l
} | tee "$output/timewait.txt"
cleanup
grep -E 'Requests/sec:|Socket errors:|Non-2xx' "$output/wrk.txt"
cat "$output/client-cpu.txt"
grep '^OWNED_METRICS ' "$output/server.log" || true
if grep -Eq 'fail:|crit:|Unhandled|Assertion' "$output/server.log"; then cat "$output/server.log"; exit 1; fi
echo "RESULTS $output"
