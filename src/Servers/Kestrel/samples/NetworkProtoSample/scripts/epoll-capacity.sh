#!/usr/bin/env bash
set -eo pipefail
sample=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
root=$(cd "$sample/../../../../.." && pwd)
cd "$root"
set +e
source activate.sh >/dev/null
set -e
set -u
backend=${1:?backend}
workers=${2:?worker/core count}
scheme=${3:?http or https}
mode=${4:?close or keepalive}
label=${5:?run label}
[[ $workers =~ ^[1-8]$ && $scheme =~ ^https?$ && $mode =~ ^(close|keepalive)$ ]] || { echo "Invalid workload" >&2; exit 1; }
server_cores=${SERVER_CORES:-$workers}
[[ $server_cores =~ ^[1-8]$ && $workers -le $server_cores ]] || { echo "Invalid server core budget" >&2; exit 1; }
cpus=()
for ((i=0; i<workers; i++)); do cpus+=("$((i*2))"); done
worker_cpus=$(IFS=,; echo "${cpus[*]}")
cpus=()
for ((i=0; i<server_cores; i++)); do cpus+=("$((i*2))"); done
server_cpus=$(IFS=,; echo "${cpus[*]}")
export NETWORKPROTO_CPUS="$worker_cpus" NETWORKPROTO_COALESCE=1 NETWORKPROTO_FINAL_SEND=3 NETWORKPROTO_KTLS=0
export LD_LIBRARY_PATH="${SERVER_OPENSSL_LIB:-/opt/openssl-3.5.8/lib}:$HOME/.local/lib"
unset NETWORKPROTO_EPOLL_SNDBUF
dll=${SAMPLE_DLL:-$root/artifacts/bin/NetworkProtoSample/Release/net11.0/NetworkProtoSample.dll}
port=${PORT:-20300}
duration=${DURATION_SECONDS:-15}
warmup=${WARMUP_SECONDS:-3}
connections=${CONNECTIONS:-1200}
rate=${RATE:-1000000}
headers=()
if [[ $mode == close ]]; then
    headers=(-H 'Connection: close')
    rate=${RATE:-200000}
    if [[ $scheme == https ]]; then rate=${RATE:-50000}; fi
fi
out="$sample/results/epoll-capacity-$label-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$out"
tls=()
if [[ $scheme == https ]]; then tls=(--cacert "$sample/.certs-owned/cert.pem" --tlsv1.2 --tls-max 1.2); fi
pid='' observer=''
cleanup() {
    local status=0
    if [[ -n $observer ]]; then
        kill -TERM "$observer" 2>/dev/null || true
        wait "$observer" 2>/dev/null || true
        observer=''
    fi
    if [[ -n $pid ]]; then
        kill -TERM "$pid" 2>/dev/null || true
        for i in $(seq 1 100); do
            if ! kill -0 "$pid" 2>/dev/null; then break; fi
            sleep .1
        done
        if kill -0 "$pid" 2>/dev/null; then
            echo "SHUTDOWN_TIMEOUT pid=$pid" >&2
            kill -KILL "$pid"
            status=1
        fi
        wait "$pid" || status=$?
        pid=''
    fi
    return "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
{
    date --iso-8601=seconds
    uname -r
    git rev-parse HEAD
    sha256sum "$dll" "$(dirname "$dll")/Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.dll" "$(dirname "$dll")/libnetworkprotoepoll.so"
    printf 'backend=%s workers=%s server_cores=%s worker_cpus=%s server_cpus=%s client_cpus=16,18,20,22,24,26,28,30 threads=8 scheme=%s mode=%s connections=%s offered=%s warmup=%s duration=%s\n' \
        "$backend" "$workers" "$server_cores" "$worker_cpus" "$server_cpus" "$scheme" "$mode" "$connections" "$rate" "$warmup" "$duration"
} > "$out/environment.txt"
DOTNET_PROCESSOR_COUNT="$server_cores" taskset -c "$server_cpus" dotnet "$dll" \
    --backend "$backend" --workers "$workers" --scheme "$scheme" --port "$port" \
    --cert "$sample/.certs-owned/cert.pem" --key "$sample/.certs-owned/key.pem" \
    --minimal true --Logging:LogLevel:Default Warning > "$out/server.log" 2>&1 &
pid=$!
ready=0
for i in $(seq 1 100); do
    if ! kill -0 "$pid" 2>/dev/null; then cat "$out/server.log"; exit 1; fi
    if curl "${tls[@]}" -fsS --max-time 1 "$scheme://127.0.0.1:$port/metrics" > "$out/ready.json" 2>/dev/null; then ready=1; break; fi
    sleep .1
done
[[ $ready == 1 ]] || { echo "STARTUP_TIMEOUT" >&2; exit 1; }
curl "${tls[@]}" -fsS --max-time 3 "$scheme://127.0.0.1:$port/" > "$out/body"
[[ $(wc -c < "$out/body") == 1024 ]]
awk '/libssl[.]so|libcrypto[.]so/ {print $NF}' "/proc/$pid/maps" | sort -u > "$out/openssl-maps.txt"
if [[ $scheme == https ]]; then grep -Fq "${SERVER_OPENSSL_LIB:-/opt/openssl-3.5.8/lib}/libssl.so.3" "$out/openssl-maps.txt"; fi
client=(taskset -c 16,18,20,22,24,26,28,30 "$HOME/code/wrk2/wrk" -t8 "-c$connections" "-R$rate" --timeout 2s "${headers[@]}" "$scheme://127.0.0.1:$port/")
if [[ $warmup != 0 ]]; then
    timeout --kill-after=3s "$((warmup+10))s" "${client[@]}" "-d${warmup}s" > "$out/warmup.txt" 2>&1
    sleep 1
fi
curl "${tls[@]}" -fsS --max-time 3 "$scheme://127.0.0.1:$port/metrics" > "$out/before.json"
cat /proc/net/netstat > "$out/netstat-before.txt"
LC_ALL=C pidstat -h -u -w -t -p "$pid" 1 > "$out/server-threads.txt" &
observer=$!
/usr/bin/time -f '{"userSeconds":%U,"systemSeconds":%S,"elapsedSeconds":%e}' -o "$out/client-cpu.json" \
    timeout --kill-after=3s "$((duration+15))s" "${client[@]}" "-d${duration}s" --latency > "$out/wrk.txt" 2>&1
curl "${tls[@]}" -fsS --max-time 3 "$scheme://127.0.0.1:$port/metrics" > "$out/after.json"
cat /proc/net/netstat > "$out/netstat-after.txt"
cleanup
node --input-type=module - "$out" "$backend" "$workers" "$scheme" "$mode" "$label" "$rate" "$connections" "$server_cores" <<'JS'
import fs from 'node:fs';
import assert from 'node:assert/strict';
const [out,backend,workersText,scheme,mode,label,rate,connections,serverCoresText]=process.argv.slice(2),workers=Number(workersText),serverCoreBudget=Number(serverCoresText);
const read=f=>fs.readFileSync(`${out}/${f}`,'utf8'), json=f=>JSON.parse(read(f));
const log=read('server.log'),wrk=read('wrk.txt'),before=json('before.json'),after=json('after.json'),client=json('client-cpu.json');
const parse=p=>[...log.matchAll(new RegExp(`^${p} (.*)$`,'gm'))].map(m=>JSON.parse(m[1]));
const owned=parse('OWNED_METRICS'),native=parse('EPOLL_METRICS'),final=parse('EPOLL_FINAL_SEND_METRICS');
const sum=(rows,k)=>rows.reduce((s,r)=>s+r[k],0);
assert.doesNotMatch(log,/fail:|crit:|Unhandled|FAIL|Assertion/);
assert.equal(after.backend,backend);assert.equal(after.scheme,scheme);
if(backend!=='sockets'){
    assert.equal(owned.length,workers);assert.equal(native.length,workers);
    assert.ok(owned.every(w=>w.pages===w.returnedPages&&!w.liveConnections&&!w.leasedPages));
    assert.equal(sum(native,'accepts'),sum(native,'closed'));
    assert.equal(sum(native,'socketErrors'),0);assert.equal(sum(native,'tlsErrors'),0);
    assert.equal(sum(native,'handshakeTimeouts'),0);assert.equal(sum(native,'shutdownTimeouts'),0);
    assert.equal(sum(final,'shutdownFailures'),0);
}
if(scheme==='http'){assert.equal(after.handshakes,0);if(native.length){assert.equal(sum(native,'sslReads'),0);assert.equal(sum(native,'sslWrites'),0);}}
const delta=k=>after[k]-before[k];
const rps=Number(wrk.match(/Requests\/sec:\s+(\S+)/)[1]);
const counters=file=>{
    const lines=read(file).trim().split('\n'),i=lines.findIndex(l=>l.startsWith('TcpExt:'));
    const keys=lines[i].split(/\s+/).slice(1),values=lines[i+1].split(/\s+/).slice(1).map(Number);
    return Object.fromEntries(keys.map((k,i)=>[k,values[i]]));
};
const n0=counters('netstat-before.txt'),n1=counters('netstat-after.txt');
const result={label,backend,workers,serverCoreBudget,scheme,mode,rate:Number(rate),connections:Number(connections),rps,
    allocationPerRequest:delta('allocatedBytes')/delta('requests'),serverCpuUsPerRequest:delta('cpuMilliseconds')*1000/delta('requests'),
    serverCores:delta('cpuMilliseconds')/1000/client.elapsedSeconds,clientCores:(client.userSeconds+client.systemSeconds)/client.elapsedSeconds,client,
    errors:wrk.match(/^.*(?:Socket errors:|Non-2xx).*$/gm)||[],timeWaitOverflow:n1.TCPTimeWaitOverflow-n0.TCPTimeWaitOverflow,
    owned,native,final,before,after};
fs.writeFileSync(`${out}/summary.json`,JSON.stringify(result,null,2)+'\n');
console.log(`${label}: ${rps.toFixed(0)} RPS; server ${result.serverCores.toFixed(2)}/${serverCoreBudget} cores, client ${result.clientCores.toFixed(2)}/8; ${result.allocationPerRequest.toFixed(0)} B/request; errors=${JSON.stringify(result.errors)}; TW overflow=${result.timeWaitOverflow}`);
JS
echo "RESULTS $out"
