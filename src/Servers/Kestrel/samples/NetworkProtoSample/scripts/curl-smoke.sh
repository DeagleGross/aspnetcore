#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
certificates
OUT="${1:-$SAMPLE/results/curl-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$OUT"
OUT="$(realpath "$OUT")"
for backend in sockets io_uring; do
    start_server "$backend" "$OUT/$backend-server.log"
    for mode in single keepalive close; do
        prefix="$OUT/$backend-$mode"
        options=()
        if [[ "$mode" == close ]]; then options=(-H 'Connection: close' --no-sessionid); fi
        urls=(-o "$prefix-1.body" "$URL/")
        if [[ "$mode" != single ]]; then
            urls+=(-o "$prefix-2.body" "$URL/" -o "$prefix-3.body" "$URL/")
        fi
        curl_local --verbose -D "$prefix.headers" -w '%{json}\n' \
            "${options[@]}" "${urls[@]}" >"$prefix.transfers.jsonl" 2>"$prefix.trace"
        python3 "$SAMPLE/scripts/verify-curl.py" "$prefix" "$backend" "$mode" "$SCHEME"
    done
    curl_local "$URL/metrics" >"$OUT/$backend-metrics.json"
    stop_server
    check_server_log "$OUT/$backend-server.log"
done
echo "$SCHEME single/reuse/close checks passed for both backends: $OUT"
