#!/usr/bin/env bash
# Linux Bedrock server cells (sockets / epoll / io_uring) driven by native wrk instead of the C# HttpClient load generator of run-bench.sh.
# Usage (after a Release build of Bedrock.csproj, with wrk installed): ./run-wrk-server.sh
# Each plan is "<server cpu list>:<native worker count>". Keep the worker count equal to the number of server CPUs: the native providers own their workers,
# while the Sockets transport scales over every CPU it is given, so a CPU list larger than the worker count favors Sockets.
# Environment overrides: OUT REPS DLL CLIENT_CPUS BODY PLANS CONNS PORT TLS (none|tls: https with Sockets+SslStream versus transport-owned OpenSSL, TLS 1.2 ECDHE-RSA-AES128-GCM-SHA256)
# REGISTRATION (bedrock|legacy: Connections.Abstractions factories versus the former private UseNetworkTransport factory; Sockets ignores it) SERVERS (default "sockets epoll iouring")
# Example: PLANS="0,2,4,6:4 0-7:8" CONNS="64 1200" ./run-wrk-server.sh
set -uo pipefail

out=${OUT:-$HOME/bedrock-wrk}
reps=${REPS:-2}
port=${PORT:-5444}
dll=${DLL:-$HOME/code/aspnetcore/artifacts/bin/Bedrock/Release/net11.0/Bedrock.dll}
client_cpus=${CLIENT_CPUS:-16-27}
body=${BODY:-1024}
tls=${TLS:-none}
registration=${REGISTRATION:-bedrock}
scheme=http
[ "$tls" = none ] || scheme=https

export LD_LIBRARY_PATH="/opt/openssl-3.5.8/lib:$HOME/.local/lib"
export DOTNET_ROOT=$HOME/code/aspnetcore/.dotnet
export PATH=$DOTNET_ROOT:$PATH
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

mkdir -p "$out"
csv="$out/wrk-server.csv"
echo 'rep;server;cpus;workers;connections;body;rps;latAvg;latP99;errors' > "$csv"

run_cell()
{
    local rep=$1 server=$2 cpus=$3 conns=$4 workers=$5
    local tlsMode=none
    if [ "$tls" != none ]; then
        # Sockets terminates TLS with SslStream through Kestrel UseHttps; the native providers terminate it inside the transport.
        tlsMode=native
        [ "$server" = sockets ] && tlsMode=stream
    fi
    taskset -c "$cpus" dotnet "$dll" serve --backend "$server" --registration "$registration" --port "$port" --body-bytes "$body" --workers "$workers" --tls "$tlsMode" \
        > "$out/server.txt" 2> "$out/server.err" &
    local pid=$!
    local up=0
    for _ in $(seq 150); do
        if (echo > "/dev/tcp/127.0.0.1/$port") 2> /dev/null; then
            up=1
            break
        fi
        sleep 0.2
    done
    if [ $up -eq 0 ]; then
        echo "SERVER DID NOT START $server"
        kill -9 "$pid" 2> /dev/null
        return
    fi
    taskset -c "$client_cpus" wrk -t12 -c"$conns" -d3s "$scheme://127.0.0.1:$port/" > /dev/null 2>&1
    local res
    res=$(taskset -c "$client_cpus" wrk -t12 -c"$conns" -d8s --latency "$scheme://127.0.0.1:$port/" 2>&1)
    local rps lat p99 err
    rps=$(grep -oP 'Requests/sec:\s+\K\S+' <<< "$res")
    lat=$(grep -P '^\s+Latency\s+\d' <<< "$res" | awk '{print $2}')
    p99=$(grep -P '^\s+99%' <<< "$res" | awk '{print $2}' | head -1)
    err=$(grep -oP 'Non-2xx|Socket errors: .*' <<< "$res" | head -1)
    echo "$rep;$server;$cpus;w$workers;$conns;$body;$rps;$lat;$p99;${err:-none}" | tee -a "$csv"
    curl -sk -m 3 -X POST "$scheme://127.0.0.1:$port/shutdown" > /dev/null
    for _ in $(seq 50); do
        kill -0 "$pid" 2> /dev/null || break
        sleep 0.1
    done
    kill -9 "$pid" 2> /dev/null
    wait "$pid" 2> /dev/null
}

for rep in $(seq "$reps"); do
    for plan in ${PLANS:-0,2,4,6:4 0-7:8}; do
        cpus=${plan%%:*}
        workers=${plan##*:}
        for conns in ${CONNS:-1200}; do
            for server in ${SERVERS:-sockets epoll iouring}; do
                run_cell "$rep" "$server" "$cpus" "$conns" "$workers"
            done
        done
    done
done
