#!/usr/bin/env bash
set -euo pipefail
scripts=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
scheme=${1:?Pass http or https}
mode=${2:?Pass close or keepalive}
[[ $scheme == http || $scheme == https ]]
[[ $mode == close || $mode == keepalive ]]
output=${MATRIX_RESULTS:?Set an absolute output directory for the complete matrix}
mkdir -p "$output"
export SERVER_OPENSSL_LIB=/opt/openssl-3.5.8/lib
export LD_LIBRARY_PATH="$SERVER_OPENSSL_LIB:$HOME/.local/lib"
export NETWORKPROTO_CPUS=0,2,4,6 NETWORKPROTO_GUARD_PAGE_READ=1
export TRIALS=1 DURATION=15s SCHEME="$scheme" MODE="$mode"
export SHUTDOWN_ATTEMPTS=${SHUTDOWN_ATTEMPTS:-100}
unset NETWORKPROTO_KTLS NETWORKPROTO_COALESCE

port=19010
[[ $scheme == https ]] && port=19110
[[ $mode == keepalive ]] && ((port+=40))
for round in 1 2; do
    if [[ $scheme == http ]]; then
        ids=(stock tcp tcp-coalesced bio)
    else
        ids=(stock tcp fd tcp-coalesced fd-coalesced bio ktls)
    fi
    if [[ ${COALESCED_ONLY:-0} == 1 ]]; then
        if [[ $scheme == http ]]; then
            ids=(tcp-coalesced bio)
        else
            ids=(tcp-coalesced fd-coalesced bio ktls)
        fi
    fi
    if [[ $round == 2 ]]; then
        reverse=()
        for ((i=${#ids[@]}-1; i>=0; i--)); do reverse+=("${ids[i]}"); done
        ids=("${reverse[@]}")
    fi
    for id in "${ids[@]}"; do
        export NETWORKPROTO_COALESCE=0 NETWORKPROTO_KTLS=0
        case "$id" in
            stock) backend=sockets;;
            tcp) backend=IoUringTcp;;
            fd) backend=IoUringTls;;
            tcp-coalesced) backend=IoUringTcp; export NETWORKPROTO_COALESCE=1;;
            fd-coalesced) backend=IoUringTls; export NETWORKPROTO_COALESCE=1;;
            bio) backend=IoUringBio; export NETWORKPROTO_COALESCE=1;;
            ktls) backend=IoUringTls; export NETWORKPROTO_COALESCE=1 NETWORKPROTO_KTLS=1;;
        esac
        label="$scheme-$mode-r$round-$id"
        if [[ -f $output/$label.log ]] && grep -q '^RESULTS ' "$output/$label.log" \
            && ! grep -Eq 'SERVER_ERRORS|SHUTDOWN_TIMEOUT|POST_RUN_METRICS_FAILED' "$output/$label.log"; then
            ((port+=1))
            continue
        fi
        if [[ -f $output/$label.log ]]; then
            cp "$output/$label.log" "$output/$label.invalid-$(date +%H%M%S).log"
        fi
        echo "=== MATRIX $label ==="
        export RUN_LABEL="final-$label" PORT="$port"
        if ! "$scripts/quick-rps.sh" "$backend" > "$output/$label.log" 2>&1; then
            tail -40 "$output/$label.log"
            exit 1
        fi
        grep -E 'Requests/sec:|Socket errors:|Non-2xx|SERVER_ERRORS|SHUTDOWN_TIMEOUT|POST_RUN_METRICS_FAILED' "$output/$label.log"
        result=$(sed -n 's/^RESULTS //p' "$output/$label.log")
        [[ -n $result && -f $result/summary.tsv ]]
        printf '%s\t%s\t%s\t%s\t%s\n' "$id" "$scheme" "$mode" "$round" "$result" >> "$output/runs.tsv"
        if grep -Eq 'SERVER_ERRORS|SHUTDOWN_TIMEOUT|POST_RUN_METRICS_FAILED' "$output/$label.log"; then
            echo "Invalid run: $label" >&2
        fi
        ((port+=1))
    done
done
