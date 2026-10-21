#!/usr/bin/env bash
# Counterbalanced A/B of Kestrel stock sockets vs shared io_uring TLS modes (fd-bound vs ring-owned ciphertext).
# Usage: uring-tls-ab.sh <rounds> [tcp|tls|both]
set -eo pipefail
sample=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
rounds=${1:-3}
which=${2:-both}
stamp=$(date +%Y%m%d-%H%M%S)
out="$sample/results/uring-tls-ab-$stamp"
mkdir -p "$out"
csv="$out/summary.csv"
echo "round,scheme,mode,arm,rps" > "$csv"
export SECONDS_TO_MEASURE=${SECONDS_TO_MEASURE:-8} WARMUP=${WARMUP:-4}
export PORT_BASE=${PORT_BASE:-23500}

run_arm() {
    local round=$1 scheme=$2 mode=$3 arm=$4 backend=$5 uring_tls=$6
    local line
    line=$(NETWORKPROTO_URING_TLS=$uring_tls RUN_LABEL="$arm-r$round" PORT=$((PORT_BASE + round)) \
        bash "$sample/scripts/shared-bench.sh" "$backend" "$scheme" "$mode" | grep -E ' RPS=')
    local rps
    rps=$(sed -E 's/.* RPS=([0-9]+).*/\1/' <<<"$line")
    echo "$round,$scheme,$mode,$arm,$rps" | tee -a "$csv"
}

for ((round = 1; round <= rounds; round++)); do
    for mode in short long; do
        lm=0
        [[ $mode == long ]] && lm=1
        if [[ $which != tcp ]]; then
            arms=("sockets:sockets:fd" "uring-fd:sharedIoUringTls:fd" "uring-ring:sharedIoUringTls:ring")
            shift_by=$(((round + lm) % 3))
            for ((i = 0; i < 3; i++)); do
                IFS=: read -r arm backend uring <<<"${arms[$(((i + shift_by) % 3))]}"
                run_arm "$round" https "$mode" "$arm" "$backend" "$uring"
            done
        fi
        if [[ $which != tls ]]; then
            arms=("sockets:sockets:fd" "uring-tcp:sharedIoUringTcp:fd")
            shift_by=$(((round + lm) % 2))
            for ((i = 0; i < 2; i++)); do
                IFS=: read -r arm backend uring <<<"${arms[$(((i + shift_by) % 2))]}"
                run_arm "$round" http "$mode" "$arm" "$backend" "$uring"
            done
        fi
    done
done

python3 - "$csv" <<'PY'
import csv, statistics, sys, collections
rows = collections.defaultdict(list)
for r in csv.DictReader(open(sys.argv[1])):
    rows[(r['scheme'], r['mode'], r['arm'])].append(int(r['rps']))
for key in sorted(rows):
    v = rows[key]
    print(f"{key[0]:5} {key[1]:5} {key[2]:11} median={statistics.median(v):>9.0f} min={min(v):>8} max={max(v):>8} n={len(v)}")
PY
echo "RESULTS $out"
