#!/usr/bin/env bash
set -eo pipefail
sample=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
root=$(cd "$sample/../../../../.." && pwd)
cd "$root"
set +e
source activate.sh >/dev/null
set -e
set -u
output="$sample/results/final-send-check-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$output"
backend=${1:-IoUringTcp}
scheme=${SCHEME:-http}
if [[ $backend == IoUringTls || $backend == epollTls ]]; then scheme=https; fi
for mode in 0 1 2 3; do
    log="$output/$backend-$scheme-mode-$mode.log"
    LD_LIBRARY_PATH=/opt/openssl-3.5.8/lib:$HOME/.local/lib \
        NETWORKPROTO_FINAL_SEND="$mode" NETWORKPROTO_COALESCE=1 DOTNET_PROCESSOR_COUNT=4 \
        timeout --signal=TERM --kill-after=2s 40s taskset -c 0,2,4,6 \
        dotnet "${SAMPLE_DLL:-$root/artifacts/bin/NetworkProtoSample/Release/net11.0/NetworkProtoSample.dll}" \
        --backend "$backend" --scheme "$scheme" --port "${PORT:-19455}" --workers "${WORKERS:-4}" \
        --cert "$sample/.certs-owned/cert.pem" --key "$sample/.certs-owned/key.pem" \
        --check-final-send true --Logging:LogLevel:Default Warning > "$log" 2>&1 \
        || { cat "$log"; exit 1; }
    node --input-type=module - "$log" "$mode" "$backend" "$scheme" "${WORKERS:-4}" <<'JS'
import fs from 'node:fs';
import assert from 'node:assert/strict';
const log = fs.readFileSync(process.argv[2], 'utf8');
const mode = Number(process.argv[3]);
const backend = process.argv[4], tls = process.argv[5] === 'https';
assert.equal([...log.matchAll(/^PASS final-send:/gm)].length, 3);
const parse = prefix => [...log.matchAll(new RegExp('^' + prefix + ' (.*)$', 'gm'))].map(m => JSON.parse(m[1]));
const managed = parse('OWNED_METRICS');
const native = parse(backend === 'epollTls' ? 'EPOLL_FINAL_SEND_METRICS' : backend === 'IoUringBio' ? 'BIO_FINAL_SEND_METRICS' : 'FINAL_SEND_METRICS');
const sum = (rows, name) => rows.reduce((total, row) => total + row[name], 0);
assert.equal(managed.length, Number(process.argv[6]));
assert.equal(native.length, Number(process.argv[6]));
assert.equal(sum(managed, 'finalSendCommands'), mode ? 3 : 0);
assert.equal(sum(managed, 'pages'), sum(managed, 'returnedPages'));
if (backend === 'epollTls') {
    assert.equal(sum(native, 'shutdownFailures'), 0);
    const epoll = parse('EPOLL_METRICS');
    assert.equal(epoll.length, managed.length);
    assert.equal(sum(epoll, 'accepts'), sum(epoll, 'closed'));
} else if (backend !== 'IoUringBio') assert.equal(sum(native, 'shutdownErrors'), 0);
if (backend === 'IoUringBio') {
    const bio = parse('BIO_METRICS');
    assert.equal(bio.length, 4);
    assert.equal(sum(bio, 'ciphertextPagesReceived'), sum(bio, 'ciphertextPagesReturned'));
}
if (mode) {
    // Two successes; peer-reset case must not falsely succeed.
    assert.equal(sum(native, tls && backend === 'IoUringTls' ? 'tlsFinalShutdowns' : 'shutdowns'), 2);
    if (tls && backend !== 'epollTls') assert.equal(sum(native, backend === 'IoUringTls' ? 'tlsCloseNotify' : 'closeNotify'), 2);
}
if (backend === 'epollTls') assert.equal(sum(native, 'closeNotify'), 2);
if (tls || backend === 'IoUringBio') {
    assert.equal(sum(native, backend === 'IoUringTls' ? 'tlsCorks' : 'corks'), mode === 3 ? 3 : 0);
}
if (mode >= 2 && backend === 'IoUringTcp') {
    assert.ok(sum(native, 'links') >= 3);
    assert.ok(sum(native, 'cancelledLinks') >= 1);
    assert.ok(sum(native, 'partialSends') >= 1);
}
console.log(`PASS ${backend} ${tls ? 'TLS' : 'TCP'} mode=${mode}: final payload/EOF, backpressure, reset, reached native path, drained all workers`);
JS
done
echo "Evidence: $output"
