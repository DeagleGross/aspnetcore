#!/usr/bin/env bash
# Same-harness comparison of Sockets, the Connections.Abstractions registration (bedrock) and the former private factory (legacy) for the native providers.
# Every arm is one server process, so a rep visits each arm once per (mode, body) cell. The arm order is rotated by one position per rep (a Latin square when REPS is a multiple of the arm count), so no arm is always measured first or last.
# Output: $OUT/rep<N>/<mode>-<body>-<server>-<registration>/wrk-server.csv and $OUT/order.csv (rep;mode;body;position;server;registration).
# Environment overrides: OUT (default $HOME/bedrock-wrk-rotate) REPS (default 5) MODES (default "none tls") BODIES (default "1024 8192 65536") NATIVE (default "epoll iouring") CONNS (default 256) PLANS (default 0,2:2)
# WARMUP and DURATION (seconds, defaults 3 and 10) are passed to run-wrk-server.sh. Prints ALL_DONE when finished.
set -uo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
out=${OUT:-$HOME/bedrock-wrk-rotate}
mkdir -p "$out"
echo 'rep;mode;body;position;server;registration' > "$out/order.csv"

arms=("sockets:bedrock")
for native in ${NATIVE:-epoll iouring}; do
    arms+=("$native:bedrock")
done
for native in ${NATIVE:-epoll iouring}; do
    arms+=("$native:legacy")
done
count=${#arms[@]}

for rep in $(seq "${REPS:-5}"); do
    for mode in ${MODES:-none tls}; do
        for body in ${BODIES:-1024 8192 65536}; do
            for position in $(seq 0 $((count - 1))); do
                arm=${arms[$(((position + rep - 1) % count))]}
                server=${arm%%:*}
                registration=${arm##*:}
                echo "$rep;$mode;$body;$position;$server;$registration" >> "$out/order.csv"
                echo "=== rep=$rep TLS=$mode BODY=$body position=$position $server/$registration"
                OUT="$out/rep$rep/$mode-$body-$server-$registration" REPS=1 CONNS=${CONNS:-256} BODY=$body TLS=$mode REGISTRATION=$registration SERVERS="$server" PLANS="${PLANS:-0,2:2}" \
                    WARMUP="${WARMUP:-3}" DURATION="${DURATION:-10}" bash "$here/run-wrk-server.sh"
            done
        done
    done
done

echo ALL_DONE
