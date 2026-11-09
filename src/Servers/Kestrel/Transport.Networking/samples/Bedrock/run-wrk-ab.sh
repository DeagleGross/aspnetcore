#!/usr/bin/env bash
# Same-harness A/B of the Connections.Abstractions registration (bedrock) against the former private factory (legacy) for the native providers, with Sockets as the common baseline.
# Reps are interleaved (rep -> mode -> body -> registration) so that host drift hits both registrations equally. Output: $OUT/rep<N>/<mode>-<body>-<registration>/wrk-server.csv
# Environment overrides: OUT (default $HOME/bedrock-wrk-ab) REPS (default 3) MODES (default "none tls") BODIES (default "1024 8192") NATIVE (default "epoll iouring") CONNS PLANS
# Prints ALL_DONE when finished.
set -uo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
out=${OUT:-$HOME/bedrock-wrk-ab}

for rep in $(seq "${REPS:-3}"); do
    for mode in ${MODES:-none tls}; do
        for body in ${BODIES:-1024 8192}; do
            for registration in bedrock legacy; do
                native=${NATIVE:-epoll iouring}
                [ "$registration" = bedrock ] && servers="sockets $native" || servers="$native"
                echo "=== rep=$rep TLS=$mode BODY=$body REGISTRATION=$registration"
                OUT="$out/rep$rep/$mode-$body-$registration" REPS=1 CONNS=${CONNS:-256} BODY=$body TLS=$mode REGISTRATION=$registration SERVERS="$servers" PLANS="${PLANS:-0,2:2}" bash "$here/run-wrk-server.sh"
            done
        done
    done
done

echo ALL_DONE
