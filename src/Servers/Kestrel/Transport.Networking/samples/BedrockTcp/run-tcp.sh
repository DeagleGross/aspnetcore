#!/usr/bin/env bash
# Raw-TCP client comparison against a standalone C server (tcpserver/tcp_server.c). No HTTP, no Kestrel on either side.
# Usage (after a Release build of BedrockTcp.csproj and `gcc -O2 -pthread -o tcp_server tcpserver/tcp_server.c`): ./run-tcp.sh <out-dir>
# Arms: sockets (plain Socket), <epoll|iouring>-pipe (ConnectionContext.Transport), <epoll|iouring>-stream (the Stream SocketsHttpHandler would get).
# A case is "<request bytes>:<response bytes>"; the server reads the request, then writes the response, and repeats.
# Environment overrides: REPS DURATION WARMUP CONNECTIONS CASES ARMS PORT WORKERS SERVER_THREADS SERVER_CPUS CLIENT_CPUS SERVER DLL
set -uo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
out=${1:-./results}
reps=${REPS:-3}
duration=${DURATION:-8}
warmup=${WARMUP:-2}
connections=${CONNECTIONS:-64}
cases=${CASES:-"64:64 64:1024 64:8192 64:65536"}
arms=(${ARMS:-sockets epoll-pipe epoll-stream iouring-pipe iouring-stream})
port=${PORT:-5601}
workers=${WORKERS:-8}
server_threads=${SERVER_THREADS:-8}
server_cpus=${SERVER_CPUS:-16-31}
client_cpus=${CLIENT_CPUS:-0-7}
server=${SERVER:-$here/tcpserver/tcp_server}
dll=${DLL:-$HOME/code/aspnetcore/artifacts/bin/BedrockTcp/Release/net11.0/BedrockTcp.dll}

export LD_LIBRARY_PATH="/opt/openssl-3.5.8/lib:$HOME/.local/lib"
export DOTNET_ROOT=$HOME/code/aspnetcore/.dotnet
export PATH=$DOTNET_ROOT:$PATH
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

mkdir -p "$out"
csv="$out/tcp-client.csv"
if [ ! -f "$csv" ]; then
    echo 'rep,arm,request,response,rps,cpuMsPer1kReq,p50us,p99us,errors,clientWorkingSetMB' > "$csv"
fi

kv()
{
    grep -oP "(?<=\b$2=)\S+" <<< "$1" | tail -1
}

run_cell()
{
    local rep=$1 arm=$2 request=$3 response=$4
    local tag="r$rep-$arm-q$request-p$response"
    taskset -c "$server_cpus" "$server" --port "$port" --threads "$server_threads" --request-bytes "$request" --response-bytes "$response" \
        > "$out/$tag.server.txt" 2> "$out/$tag.server.err" &
    local server_pid=$!
    local up=0
    for _ in $(seq 150); do
        if (echo > "/dev/tcp/127.0.0.1/$port") 2> /dev/null; then
            up=1
            break
        fi
        sleep 0.2
    done
    if [ $up -eq 0 ]; then
        echo "SERVER DID NOT START $tag"
        kill -9 "$server_pid" 2> /dev/null
        wait "$server_pid" 2> /dev/null
        return
    fi
    timeout $((duration + warmup + 90)) taskset -c "$client_cpus" dotnet "$dll" --arm "$arm" --port "$port" --connections "$connections" \
        --duration "$duration" --warmup "$warmup" --request-bytes "$request" --response-bytes "$response" --workers "$workers" \
        > "$out/$tag.client.txt" 2> "$out/$tag.client.err"
    local status=$?
    local line
    line=$(grep '^RESULT ' "$out/$tag.client.txt" | tail -1)
    if [ -z "$line" ]; then
        echo "NO RESULT $tag (exit $status)"
    else
        local row="$rep,$arm,$request,$response,$(kv "$line" rps),$(kv "$line" cpuMsPer1kReq),$(kv "$line" p50us),$(kv "$line" p99us),$(kv "$line" errors),$(kv "$line" workingSetMB)"
        echo "$row" >> "$csv"
        echo "$row"
    fi
    kill -9 "$server_pid" 2> /dev/null
    wait "$server_pid" 2> /dev/null
    sleep 0.3
}

for rep in $(seq "$reps"); do
    for c in $cases; do
        request=${c%%:*}
        response=${c##*:}
        # Rotate the arm order per rep so no arm always runs first.
        for i in "${!arms[@]}"; do
            run_cell "$rep" "${arms[$(((i + rep - 1) % ${#arms[@]}))]}" "$request" "$response"
        done
    done
done
