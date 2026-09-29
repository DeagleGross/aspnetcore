#!/usr/bin/env bash
set -eo pipefail
sample=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
root=$(cd "$sample/../../../../.." && pwd)
cd "$root"
scheme=${1:-https}
[[ $scheme == http || $scheme == https ]] || { echo "Expected http or https" >&2; exit 1; }
baseline=${BASELINE_DLL:-$root/artifacts/tmp/uring-native-baseline-20260929/NetworkProtoSample.dll}
current="$root/artifacts/bin/NetworkProtoSample/Release/net11.0/NetworkProtoSample.dll"
[[ -f $baseline && -f $current ]] || { echo "Build current and preserve the baseline deployment first" >&2; exit 1; }
out="$sample/results/uring-managed-$scheme-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$out"
export LD_LIBRARY_PATH=/opt/openssl-3.5.8/lib:$HOME/.local/lib
export NETWORKPROTO_COALESCE=1 NETWORKPROTO_FINAL_SEND=3 NETWORKPROTO_GUARD_PAGE_READ=1 NETWORKPROTO_KTLS=0
export NETWORKPROTO_CPUS=0,2,4,6 TRIALS=1 SCHEME="$scheme"
unset NETWORKPROTO_URING_SNDBUF
printf 'round\tscheme\tmode\tvariant\tbackend\tresult\n' > "$out/runs.tsv"
{
    date --iso-8601=seconds
    git rev-parse HEAD
    for dll in "$baseline" "$current"; do
        sha256sum "$dll" "$(dirname "$dll")/Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.dll" \
            "$(dirname "$dll")/libnetworkproto2.so" "$(dirname "$dll")/libnetworkprotobio.so"
    done
    printf '4 server cores / 12 client cores; 1200 connections; 1024-byte body; 15s; original single-source wrk2; coalescing and final batching enabled\n'
} > "$out/environment.txt"
port=${PORT:-19940}
for mode in close keepalive; do
    export MODE="$mode"
    for round in 1 2; do
        configs=(stock:sockets old:IoUringTcp new:IoUringTcp old:IoUringBio new:IoUringBio)
        if [[ $scheme == https ]]; then configs+=(old:IoUringTls new:IoUringTls); fi
        if [[ $round == 2 ]]; then
            reverse=()
            for ((i=${#configs[@]}-1; i>=0; i--)); do reverse+=("${configs[i]}"); done
            configs=("${reverse[@]}")
        fi
        for config in "${configs[@]}"; do
            variant=${config%%:*}
            backend=${config#*:}
            export SAMPLE_DLL="$current"
            if [[ $variant == old ]]; then export SAMPLE_DLL="$baseline"; fi
            logfile="$out/$mode-r$round-$variant-$backend.log"
            echo "=== $scheme $mode r$round $variant $backend ==="
            PORT="$port" RUN_LABEL="uring-managed-$scheme-$mode-r$round-$variant" \
                "$sample/scripts/quick-rps.sh" "$backend" > "$logfile" 2>&1 || { tail -40 "$logfile"; exit 1; }
            grep -E 'Requests/sec:|Socket errors:|RESULTS|SERVER_ERRORS|SHUTDOWN_TIMEOUT' "$logfile"
            result=$(awk '/^RESULTS / {print $2}' "$logfile")
            printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$round" "$scheme" "$mode" "$variant" "$backend" "$result" >> "$out/runs.tsv"
            ((port+=1))
        done
    done
done
node --input-type=module - "$out" <<'JS'
import fs from 'node:fs';
import assert from 'node:assert/strict';
const out = process.argv[2], results = [];
for (const line of fs.readFileSync(`${out}/runs.tsv`, 'utf8').trim().split('\n').slice(1)) {
    const [round, scheme, mode, variant, backend, path] = line.split('\t');
    const files = fs.readdirSync(path);
    const log = fs.readFileSync(`${path}/${files.find(f => f.endsWith('-server.log'))}`, 'utf8');
    const wrk = fs.readFileSync(`${path}/${files.find(f => f.endsWith('-wrk.txt'))}`, 'utf8');
    assert.doesNotMatch(log, /fail:|crit:|Unhandled|Assertion|ERROR: AddressSanitizer/);
    const parse = p => [...log.matchAll(new RegExp(`^${p} (.*)$`, 'gm'))].map(m => JSON.parse(m[1]));
    const owned = parse('OWNED_METRICS'), bio = parse('BIO_METRICS');
    if (variant !== 'stock') {
        assert.equal(owned.length, 4, path);
        assert.ok(owned.every(w => w.pages === w.returnedPages), path);
        if (variant === 'new') assert.ok(owned.every(w => w.managedEngine && !w.liveConnections && !w.leasedPages && !w.leasedCipherPages), path);
        if (bio.length) assert.equal(bio.reduce((s, w) => s + w.ciphertextPagesReceived, 0), bio.reduce((s, w) => s + w.ciphertextPagesReturned, 0), path);
    }
    results.push({ round: Number(round), scheme, mode, variant, backend, path,
        rps: Number(wrk.match(/Requests\/sec:\s+(\S+)/)[1]), errors: wrk.match(/^.*(?:Socket errors:|Non-2xx).*$/gm) || [],
        owned, bio, ktls: parse('KTLS_METRICS'), tls: parse('TLS_READ_METRICS'),
        final: parse(backend === 'IoUringBio' ? 'BIO_FINAL_SEND_METRICS' : 'FINAL_SEND_METRICS') });
}
const means = {};
for (const r of results) {
    const key = `${r.variant}-${r.backend}-${r.mode}`;
    const group = results.filter(x => `${x.variant}-${x.backend}-${x.mode}` === key);
    means[key] = group.reduce((s, x) => s + x.rps, 0) / group.length;
}
for (const [key, mean] of Object.entries(means)) console.log(`${key}: ${mean.toFixed(2)}`);
fs.writeFileSync(`${out}/validated-summary.json`, JSON.stringify({ means, results }, null, 2) + '\n');
console.log(`AUDIT ${results.length} benchmark runs, complete native/managed ownership accounting`);
JS
echo "MATRIX $out"
