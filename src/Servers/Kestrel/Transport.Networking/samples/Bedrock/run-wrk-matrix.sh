#!/usr/bin/env bash
# Runs run-wrk-server.sh for plain TCP and TLS across response sizes, mirroring the crank setup of LINUX-RESULTS.md: 256 connections, 2 server CPUs, 2 native workers.
# Environment overrides: OUT (default $HOME/bedrock-wrk-c256) REPS (default 3) MODES (default "none tls") BODIES (default "1024 8192 65536") CONNS PLANS
# Prints ALL_DONE when finished.
set -uo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
out=${OUT:-$HOME/bedrock-wrk-c256}

for mode in ${MODES:-none tls}; do
    for body in ${BODIES:-1024 8192 65536}; do
        echo "=== TLS=$mode BODY=$body"
        OUT="$out/$mode-$body" REPS=${REPS:-3} CONNS=${CONNS:-256} BODY=$body TLS=$mode PLANS="${PLANS:-0,2:2}" bash "$here/run-wrk-server.sh"
    done
done

echo ALL_DONE
