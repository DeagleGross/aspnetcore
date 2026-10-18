#!/usr/bin/env bash
set -eo pipefail
sample=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
root=$(cd "$sample/../../../../.." && pwd)
cd "$root"
set +e
source activate.sh >/dev/null
set -e
set -u
backend=${1:?backend required}
scheme=${2:?http or https required}
mode=${3:?short or long required}
port=${PORT:-23500}
seconds=${SECONDS_TO_MEASURE:-10}
warmup=${WARMUP:-5}
connections=${CONNECTIONS:-256}
if [[ $mode == short ]]; then connections=${SHORT_CONNECTIONS:-64}; fi
output="$sample/results/shared-$backend-$scheme-$mode${RUN_LABEL:+-$RUN_LABEL}-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$output"
export LD_LIBRARY_PATH="/opt/openssl-3.5.8/lib:$HOME/.local/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export NETWORKPROTO_CPUS=0,2,4,6
export NETWORKPROTO_COALESCE=1 NETWORKPROTO_KTLS=0 NETWORKPROTO_FINAL_SEND=3
sample_dll="${SAMPLE_DLL:-$root/artifacts/bin/NetworkProtoSample/Release/net11.0/NetworkProtoSample.dll}"
load_dll="$root/artifacts/bin/WindowsTransportLoad/Release/net11.0/WindowsTransportLoad.dll"
cert="$sample/.certs-owned/cert.pem"
key="$sample/.certs-owned/key.pem"
server=''
client=''
cleanup() {
    for pid in "$client" "$server"; do
        if [[ -n $pid ]] && kill -0 "$pid" 2>/dev/null; then
            kill -TERM "$pid"
            wait "$pid" || true
        fi
    done
}
trap cleanup EXIT
tls=()
if [[ $scheme == https ]]; then tls=(--cacert "$cert" --tlsv1.2 --tls-max 1.2); fi
url="$scheme://127.0.0.1:$port"
DOTNET_PROCESSOR_COUNT=4 taskset -c 0,2,4,6 dotnet "$sample_dll" \
    --backend "$backend" --scheme "$scheme" --port "$port" --workers 4 \
    --cert "$cert" --key "$key" --minimal true --benchmark true \
    --Logging:LogLevel:Default Warning > "$output/server.log" 2>&1 &
server=$!
ready=0
for i in $(seq 1 100); do
    if ! kill -0 "$server" 2>/dev/null; then cat "$output/server.log"; exit 1; fi
    if curl "${tls[@]}" -fsS --max-time 1 "$url/metrics" > /dev/null 2>&1; then ready=1; break; fi
    sleep .1
done
[[ $ready == 1 ]] || { echo "Server startup failed."; exit 1; }
DOTNET_PROCESSOR_COUNT=12 taskset -c 8,10,12,14,16,18,20,22,24,26,28,30 \
    timeout --signal=TERM --kill-after=5s "$((warmup+seconds+40))s" dotnet "$load_dll" \
    --url "$url/" --mode "$mode" --connections "$connections" --seconds "$seconds" \
    --warmup "$warmup" --tls-protocol Tls12 --source-network auto > "$output/load.log" 2>&1 &
client=$!
for i in $(seq 1 500); do
    if grep -q '^MEASURE_START' "$output/load.log"; then break; fi
    if ! kill -0 "$client" 2>/dev/null; then cat "$output/load.log"; exit 1; fi
    sleep .05
done
grep -q '^MEASURE_START' "$output/load.log"
curl "${tls[@]}" -fsS --max-time 5 "$url/metrics" > "$output/before.json"
wait "$client"
client=''
curl "${tls[@]}" -fsS --max-time 5 "$url/metrics" > "$output/after.json"
curl "${tls[@]}" -fsS --max-time 5 --request POST "$url/shutdown" > /dev/null
wait "$server"
server=''
python3 - "$output" "$backend" "$scheme" "$mode" "$sample_dll" <<'PY'
from pathlib import Path
import hashlib, json, sys
path=Path(sys.argv[1])
lines=path.joinpath('load.log').read_text().splitlines()
result=json.loads(next(line.removeprefix('LOAD_RESULT ') for line in lines if line.startswith('LOAD_RESULT ')))
assert result['errors']==0 and result['warmupErrors']==0, result['firstError']
assert result['readyLanes']==result['connections']
server=path.joinpath('server.log').read_text()
assert not any(x in server for x in ('fail:', 'crit:', 'Unhandled', 'Assertion'))
native=[json.loads(line.removeprefix('OWNED_METRICS ')) for line in server.splitlines() if line.startswith('OWNED_METRICS ')]
if sys.argv[2]!='sockets':
    assert len(native)==4
    assert all(m['liveConnections']==0 and m['leasedPages']==0 and m.get('leasedCipherPages',0)==0 for m in native)
    assert sum(m['pages'] for m in native)>0 and sum(m['receiveAdapterCopyBytes'] for m in native)==0
record=dict(backend=sys.argv[2],scheme=sys.argv[3],mode=sys.argv[4],result=result,transport=native,
    sampleSha256=hashlib.sha256(Path(sys.argv[5]).read_bytes()).hexdigest(),
    serverBefore=json.loads(path.joinpath('before.json').read_text()),
    serverAfter=json.loads(path.joinpath('after.json').read_text()))
path.joinpath('run.json').write_text(json.dumps(record,indent=2)+'\n')
print(f"{sys.argv[2]} {sys.argv[3]} {sys.argv[4]} RPS={result['rps']:.0f} errors=0 ready={result['readyLanes']} clientCPU={result['clientCpuCores']:.2f}")
print(f'RESULTS {path}')
PY
