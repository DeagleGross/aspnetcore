#!/usr/bin/env bash
# Linux counterpart of run-bench.ps1: runs the Bedrock client/server comparison cells and appends one CSV row per cell.
# Usage (after a Release build of Bedrock.csproj): ./run-bench.sh <client|server|both> <out-dir>
# Plans:
#   client - plain sockets server; the client handler varies (sockets / epoll / iouring; read-ahead -1 is the default UseConnectionFactory overload).
#   server - plain SocketsHttpHandler client; the server registration varies (sockets / AddNetworkTransport / UseNetworkTransport).
#   both   - Bedrock transport on both sides.
# Environment overrides: REPS DURATION WARMUP CONNECTIONS BODIES PORT CLIENT_WORKERS SERVER_WORKERS SERVER_CPUS CLIENT_CPUS CELLS DLL
# CELLS filters client cells ("epoll:-1 sockets:-1"), server cells ("iouring:bedrock") or both-plan cells ("epoll").
# The server plan defaults to 8 server CPUs and 4 native workers, which gives the Sockets transport twice the CPUs of the native providers; see run-wrk-server.sh for matched CPU/worker plans.
set -uo pipefail

plan=${1:-client}
out=${2:-./results}
reps=${REPS:-2}
duration=${DURATION:-8}
warmup=${WARMUP:-2}
connections=${CONNECTIONS:-64}
bodies=${BODIES:-"1024 8192 65536"}
port=${PORT:-5443}
client_workers=${CLIENT_WORKERS:-8}
server_workers=${SERVER_WORKERS:-4}
cells=${CELLS:-}
dll=${DLL:-$HOME/code/aspnetcore/artifacts/bin/Bedrock/Release/net11.0/Bedrock.dll}

case $plan in
    client) server_cpus=${SERVER_CPUS:-16-31}; client_cpus=${CLIENT_CPUS:-0-7} ;;
    server) server_cpus=${SERVER_CPUS:-0-7}; client_cpus=${CLIENT_CPUS:-16-31} ;;
    both) server_cpus=${SERVER_CPUS:-0-7}; client_cpus=${CLIENT_CPUS:-16-31} ;;
    *) echo "unknown plan $plan"; exit 2 ;;
esac

export LD_LIBRARY_PATH="/opt/openssl-3.5.8/lib:$HOME/.local/lib"
export DOTNET_ROOT=$HOME/code/aspnetcore/.dotnet
export PATH=$DOTNET_ROOT:$PATH
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

mkdir -p "$out"
csv="$out/bedrock-$plan.csv"
if [ ! -f "$csv" ]; then
    echo 'plan,rep,server,registration,client,readAhead,body,rps,cpuMsPer1kReq,p50us,p99us,errors,clientWorkingSetMB,serverRssMB' > "$csv"
fi

wanted()
{
    [ -z "$cells" ] && return 0
    case " $cells " in *" $1 "*) return 0 ;; esac
    return 1
}

kv()
{
    grep -oP "(?<=\b$2=)\S+" <<< "$1" | tail -1
}

run_cell()
{
    local rep=$1 server=$2 registration=$3 client=$4 read_ahead=$5 body=$6
    local tag="$plan-r$rep-s$server-$registration-c$client-ra$read_ahead-b$body"
    taskset -c "$server_cpus" dotnet "$dll" serve --backend "$server" --registration "$registration" --port "$port" --body-bytes "$body" --workers "$server_workers" \
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
    timeout $((duration + warmup + 90)) taskset -c "$client_cpus" dotnet "$dll" bench --handler "$client" --url "http://127.0.0.1:$port/" --connections "$connections" \
        --duration "$duration" --warmup "$warmup" --body-bytes "$body" --workers "$client_workers" --read-ahead "$read_ahead" \
        > "$out/$tag.client.txt" 2> "$out/$tag.client.err"
    local status=$?
    local server_rss
    server_rss=$(awk '/VmRSS/ { printf "%d", $2 / 1024 }' "/proc/$server_pid/status" 2> /dev/null)
    local line
    line=$(grep '^RESULT ' "$out/$tag.client.txt" | tail -1)
    if [ -z "$line" ]; then
        echo "NO RESULT $tag (exit $status)"
    else
        local row="$plan,$rep,$server,$registration,$client,$read_ahead,$body,$(kv "$line" rps),$(kv "$line" cpuMsPer1kReq),$(kv "$line" p50us),$(kv "$line" p99us),$(kv "$line" errors),$(kv "$line" workingSetMB),$server_rss"
        echo "$row" >> "$csv"
        echo "$row"
    fi
    curl -s -m 3 -X POST "http://127.0.0.1:$port/shutdown" > /dev/null
    for _ in $(seq 50); do
        kill -0 "$server_pid" 2> /dev/null || break
        sleep 0.1
    done
    kill -9 "$server_pid" 2> /dev/null
    wait "$server_pid" 2> /dev/null
}

for rep in $(seq "$reps"); do
    for body in $bodies; do
        case $plan in
            client)
                for c in sockets epoll iouring; do
                    wanted "$c:-1" && run_cell "$rep" sockets bedrock "$c" -1 "$body"
                done
                ;;
            server)
                for s in sockets:bedrock epoll:bedrock epoll:legacy iouring:bedrock iouring:legacy; do
                    wanted "$s" && run_cell "$rep" "${s%%:*}" "${s##*:}" sockets -1 "$body"
                done
                ;;
            both)
                for p in sockets epoll iouring; do
                    wanted "$p" && run_cell "$rep" "$p" bedrock "$p" -1 "$body"
                done
                ;;
        esac
    done
done
