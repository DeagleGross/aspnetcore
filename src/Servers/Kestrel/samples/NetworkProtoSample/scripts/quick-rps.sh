#!/usr/bin/env bash
set -eo pipefail
sample=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
root=$(cd "$sample/../../../../.." && pwd)
cd "$root"
source activate.sh >/dev/null
set -u
export LD_LIBRARY_PATH="$HOME/.local/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
backend=${1:-sockets}
trials=${TRIALS:-2}
duration=${DURATION:-15s}
port=${PORT:-18720}
output="$sample/results/quick-$backend${NETWORKPROTO_COALESCE:+-coalesced}-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$output"
dll="$root/artifacts/bin/NetworkProtoSample/Release/net11.0/NetworkProtoSample.dll"
cert="$sample/.certs-owned/cert.pem"
key="$sample/.certs-owned/key.pem"
wrk="$HOME/code/wrk2/wrk"
pid=''
cleanup() {
    if [[ -n $pid ]] && kill -0 "$pid" 2>/dev/null; then
        kill -TERM "$pid"
        for i in $(seq 1 100); do
            if ! kill -0 "$pid" 2>/dev/null; then break; fi
            sleep .1
        done
        if kill -0 "$pid" 2>/dev/null; then
            echo "SHUTDOWN_TIMEOUT pid=$pid" >&2
            kill -KILL "$pid"
        fi
        wait "$pid" || true
    fi
    pid=''
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
printf 'backend\tscheme\tmode\ttrial\toffered_rps\tachieved_rps\n' > "$output/summary.tsv"
schemes=(http https)
if [[ $backend == IoUringTls ]]; then schemes=(https); fi
if [[ -n ${SCHEME:-} ]]; then schemes=("$SCHEME"); fi
for scheme in "${schemes[@]}"; do
    for mode in close keepalive; do
        for trial in $(seq 1 "$trials"); do
            label="$scheme-$mode-$trial"
            rate=1000000
            headers=()
            if [[ $mode == close ]]; then
                rate=200000
                headers=(-H 'Connection: close')
                if [[ $scheme == https ]]; then rate=50000; fi
            fi
            tls=()
            if [[ $scheme == https ]]; then tls=(--cacert "$cert" --tlsv1.2 --tls-max 1.2); fi
            DOTNET_PROCESSOR_COUNT=4 taskset -c 0,2,4,6 dotnet "$dll" \
                --backend "$backend" --scheme "$scheme" --port "$port" \
                --cert "$cert" --key "$key" --minimal true --Logging:LogLevel:Default Warning \
                > "$output/$label-server.log" 2>&1 &
            pid=$!
            ready=0
            for i in $(seq 1 100); do
                if ! kill -0 "$pid" 2>/dev/null; then cat "$output/$label-server.log"; exit 1; fi
                if curl "${tls[@]}" -fsS --max-time 1 "$scheme://127.0.0.1:$port/metrics" > "$output/$label-before.json" 2>/dev/null; then
                    ready=1
                    break
                fi
                sleep .1
            done
            if [[ $ready == 0 ]]; then echo "STARTUP_TIMEOUT $label"; cat "$output/$label-server.log"; exit 1; fi
            curl "${tls[@]}" -fsS --max-time 3 "$scheme://127.0.0.1:$port/" -o "$output/$label-body"
            [[ $(wc -c < "$output/$label-body") == 1024 ]]
            echo "=== $backend $scheme $mode trial=$trial offered=$rate duration=$duration ==="
            # Same four server cores, twelve other physical client cores in every run.
            timeout --signal=TERM --kill-after=3s 35s taskset -c 8,10,12,14,16,18,20,22,24,26,28,30 \
                "$wrk" -t12 -c1200 "-d$duration" "-R$rate" --latency --timeout 2s \
                "${headers[@]}" "$scheme://127.0.0.1:$port/" > "$output/$label-wrk.txt" 2>&1
            grep -E 'Requests/sec:|Socket errors:|Non-2xx|^ 50[.]000%|^ 99[.]000%' "$output/$label-wrk.txt"
            rps=$(awk '/Requests\/sec:/ {print $2}' "$output/$label-wrk.txt")
            [[ -n $rps ]]
            printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$backend" "$scheme" "$mode" "$trial" "$rate" "$rps" >> "$output/summary.tsv"
            if ! curl "${tls[@]}" -fsS --max-time 3 "$scheme://127.0.0.1:$port/metrics" > "$output/$label-after.json"; then
                echo "POST_RUN_METRICS_FAILED $label" >&2
            fi
            cleanup
            if grep -Eq 'fail:|crit:|Unhandled|Assertion|Aborted' "$output/$label-server.log"; then
                echo "SERVER_ERRORS: $output/$label-server.log"
                tail -8 "$output/$label-server.log"
            fi
            ((port+=1))
        done
    done
done
echo "RESULTS $output"
cat "$output/summary.tsv"
