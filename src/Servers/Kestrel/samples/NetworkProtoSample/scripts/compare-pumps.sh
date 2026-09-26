#!/usr/bin/env bash
set -euo pipefail
SAMPLE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$SAMPLE/results/pumps-$(date -u +%Y%m%dT%H%M%SZ)}"
ROUNDS="${ROUNDS:-3}"
[[ "$ROUNDS" =~ ^[0-9]+$ && "$ROUNDS" -ge 1 && "$ROUNDS" -le 3 ]]
mkdir -p "$OUT"
OUT="$(realpath "$OUT")"
export SCHEME=http THREADS=2 WARMUP="${WARMUP:-10}" DURATION="${DURATION:-10}" REPETITIONS=1
export NETWORKPROTO_SYNC_SEND=0 NETWORKPROTO_SYNC_ACCEPT=0 NETWORKPROTO_SYNC_RECEIVE=0
export NETWORKPROTO_REUSE_OPERATIONS=1 NETWORKPROTO_INLINE_RECEIVE=1
export NETWORKPROTO_COMPACT_CONNECTIONS=1 NETWORKPROTO_SHUTDOWN_CLOSE=1
export NETWORKPROTO_DIRECT_SQ=1 NETWORKPROTO_STAGE_ON_PUMP=1 NETWORKPROTO_RING_ENTRIES=1024
export NETWORKPROTO_DIAGNOSTICS=0 TRACE=0
export NETWORKPROTO_ACCEPT_MODE=oneshot NETWORKPROTO_DEFER_TASKRUN=0
directories=()
for ((round=1; round<=ROUNDS; round++)); do
    case "$round" in
        1) variants="sockets previous batch combined both" ;;
        2) variants="both combined batch previous sockets" ;;
        3) variants="batch previous sockets both combined" ;;
    esac
    for mode in keepalive close; do
        export MODES="$mode" CONNECTIONS=64
        if [[ "$mode" == close ]]; then export CONNECTIONS=16; fi
        for variant in $variants; do
            export BACKENDS=io_uring NETWORKPROTO_BATCH_SUBMISSIONS=0 NETWORKPROTO_COMBINED_WAIT=0
            case "$variant" in
                sockets) export BACKENDS=sockets ;;
                batch) export NETWORKPROTO_BATCH_SUBMISSIONS=1 ;;
                combined) export NETWORKPROTO_COMBINED_WAIT=1 ;;
                both) export NETWORKPROTO_BATCH_SUBMISSIONS=1 NETWORKPROTO_COMBINED_WAIT=1 ;;
            esac
            directory="$OUT/r$round-$mode-$variant"
            [[ ! -e "$directory" && ! -e "$directory.log" ]] || { echo "Refusing to overwrite $directory" >&2; exit 1; }
            directories+=("$directory")
            if ! bash "$SAMPLE/scripts/wrk-load.sh" "$directory" >"$directory.log" 2>&1; then
                tail -n 50 "$directory.log" >&2
                exit 1
            fi
            echo "$directory"
            grep -h -E 'Requests/sec:|^Server allocation' "$directory/"*-measured.*txt
        done
    done
done
python3 "$SAMPLE/scripts/summarize.py" "${directories[@]}" >"$OUT/summary.json" 2>"$OUT/statistics.txt"
