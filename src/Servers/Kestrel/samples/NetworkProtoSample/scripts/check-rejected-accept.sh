#!/usr/bin/env bash
set -eo pipefail
sample=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
root=$(cd "$sample/../../../../.." && pwd)
cd "$root"
set +e
source activate.sh >/dev/null
set -e
set -u
output="$sample/results/rejected-accept-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$output"
backends=(IoUringTls IoUringBio epollTls)
if [[ $# != 0 ]]; then backends=("$1"); fi
for backend in "${backends[@]}"; do
    for coalesced in 0 1; do
        logfile="$output/$backend-$coalesced.log"
        LD_LIBRARY_PATH="${SERVER_OPENSSL_LIB:-/opt/openssl-3.5.8/lib}:$HOME/.local/lib" \
            NETWORKPROTO_KTLS=0 NETWORKPROTO_COALESCE="$coalesced" DOTNET_PROCESSOR_COUNT=4 \
            timeout --signal=TERM --kill-after=2s 25s taskset -c 0,2,4,6 \
            dotnet "$root/artifacts/bin/NetworkProtoSample/Release/net11.0/NetworkProtoSample.dll" \
            --backend "$backend" --scheme https --port "${PORT:-19440}" \
            --cert "$sample/.certs-owned/cert.pem" --key "$sample/.certs-owned/key.pem" \
            --check-rejected-accept true --Logging:LogLevel:Default Warning > "$logfile" 2>&1 \
            || { cat "$logfile"; exit 1; }
        node --input-type=module - "$logfile" <<'JS'
import fs from 'node:fs';
import assert from 'node:assert/strict';
const log = fs.readFileSync(process.argv[2], 'utf8');
assert.match(log, /PASS rejected-accept: 16 gated handshakes/);
const workers = [...log.matchAll(/^OWNED_METRICS (.*)$/gm)].map(m => JSON.parse(m[1]));
assert.equal(workers.length, 4);
assert.equal(workers.reduce((n, w) => n + w.rejectedAccepts, 0), 16);
assert.equal(workers.reduce((n, w) => n + w.rejectedAcceptsDisposed, 0), 16);
assert.ok(workers.every(w => w.pages === w.returnedPages));
console.log(`PASS ${process.argv[2]}: all 16 late accepts rejected and disposed`);
JS
    done
done
echo "Evidence: $output"
