#!/usr/bin/env bash
set -eo pipefail
sample=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
root=$(cd "$sample/../../../../.." && pwd)
cd "$root"
set +e
source activate.sh >/dev/null
set -e
set -u
export LD_LIBRARY_PATH="${SERVER_OPENSSL_LIB:-/opt/openssl-3.5.8/lib}:$HOME/.local/lib"
export NETWORKPROTO_FINAL_SEND=3 NETWORKPROTO_COALESCE=1 NETWORKPROTO_GUARD_PAGE_READ=1 NETWORKPROTO_CPUS=0,2,4,6
output="$sample/results/uring-managed-check-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$output"
source="$root/src/Servers/Kestrel/Transport.Networking/src"
dll="$root/artifacts/bin/NetworkProtoSample/Release/net11.0/NetworkProtoSample.dll"
prefix=()
if [[ ${SANITIZE:-0} == 1 ]]; then
    mkdir -p "$output/deployment"
    cp -a "$(dirname "$dll")/." "$output/deployment/"
    for spec in IoUringTcp:libnetworkproto2.so IoUringBio:libnetworkprotobio.so; do
        cc -std=c11 -O1 -g -Wall -Wextra -Werror -fPIC -shared -fsanitize=address,undefined -fno-omit-frame-pointer \
            -I"$HOME/.local/include" "$source/${spec%%:*}/native.c" -L"$HOME/.local/lib" -Wl,-rpath,"$HOME/.local/lib" \
            -luring -lssl -lcrypto -o "$output/deployment/${spec#*:}"
    done
    dll="$output/deployment/NetworkProtoSample.dll"
    prefix=(env "LD_PRELOAD=$(cc -print-file-name=libasan.so)" ASAN_OPTIONS=detect_leaks=0:halt_on_error=1 UBSAN_OPTIONS=halt_on_error=1)
fi
pid=''
cleanup() {
    local status=0
    if [[ -n $pid ]]; then
        kill -TERM "$pid" 2>/dev/null || true
        for i in $(seq 1 100); do
            if ! kill -0 "$pid" 2>/dev/null; then break; fi
            sleep .1
        done
        if kill -0 "$pid" 2>/dev/null; then
            echo "SHUTDOWN_TIMEOUT pid=$pid" >&2
            kill -KILL "$pid"
            status=1
        fi
        wait "$pid" || status=$?
        pid=''
    fi
    return "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
port=${PORT:-19920}
configs=(IoUringTcp:http IoUringTcp:https IoUringTls:https IoUringBio:http IoUringBio:https ktls:https)
for config in "${configs[@]}"; do
    backend=${config%%:*}
    scheme=${config#*:}
    label="$backend-$scheme"
    export NETWORKPROTO_KTLS=0
    if [[ $backend == ktls ]]; then backend=IoUringTls; export NETWORKPROTO_KTLS=1; fi
    args=(--backend "$backend" --scheme "$scheme" --port "$port"
        --cert "$sample/.certs-owned/cert.pem" --key "$sample/.certs-owned/key.pem" --Logging:LogLevel:Default Warning)
    tls=()
    if [[ $scheme == https ]]; then tls=(--cacert "$sample/.certs-owned/cert.pem" --tlsv1.2 --tls-max 1.2); fi
    DOTNET_PROCESSOR_COUNT=4 taskset -c 0,2,4,6 "${prefix[@]}" dotnet "$dll" "${args[@]}" > "$output/$label.log" 2>&1 &
    pid=$!
    ready=0
    for i in $(seq 1 100); do
        if ! kill -0 "$pid" 2>/dev/null; then cat "$output/$label.log"; exit 1; fi
        if curl "${tls[@]}" -fsS --max-time 1 "$scheme://127.0.0.1:$port/metrics" > "$output/$label-before.json" 2>/dev/null; then ready=1; break; fi
        sleep .1
    done
    [[ $ready == 1 ]] || { echo "STARTUP_TIMEOUT $label" >&2; exit 1; }
    node "$sample/scripts/check-owned.mjs" "$scheme" "$port" "$sample/.certs-owned/cert.pem" "$backend"
    if [[ $scheme == https ]]; then
        node "$sample/scripts/check-bio.mjs" "$port" "$sample/.certs-owned/cert.pem"
    fi
    if [[ $scheme == https && $backend != IoUringTcp ]]; then
        printf 'GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' |
            timeout --kill-after=2s 10s openssl s_client -connect "127.0.0.1:$port" -servername localhost \
                -CAfile "$sample/.certs-owned/cert.pem" -verify_return_error -tls1_2 -quiet -state -ign_eof \
                > "$output/$label-response.txt" 2> "$output/$label-tls.txt"
        grep -q 'SSL3 alert read:warning:close notify' "$output/$label-tls.txt"
    fi
    cleanup
    timeout --kill-after=2s 20s "${prefix[@]}" dotnet "$dll" "${args[@]}" --check-uring-lifecycle true \
        > "$output/$label-lifecycle.log" 2>&1 || { cat "$output/$label-lifecycle.log"; exit 1; }
    grep '^PASS' "$output/$label-lifecycle.log"
    if [[ $scheme == http || $backend != IoUringTcp ]]; then
        send_buffer=4096
        if [[ $NETWORKPROTO_KTLS == 1 ]]; then send_buffer=32768; fi
        NETWORKPROTO_URING_SNDBUF="$send_buffer" timeout --kill-after=2s 45s "${prefix[@]}" dotnet "$dll" "${args[@]}" \
            --check-final-send true > "$output/$label-final.log" 2>&1 || { cat "$output/$label-final.log"; exit 1; }
        grep '^PASS' "$output/$label-final.log"
    fi
    ((port+=1))
done
node --input-type=module - "$output" <<'JS'
import fs from 'node:fs';
import assert from 'node:assert/strict';
const directory = process.argv[2];
for (const file of fs.readdirSync(directory).filter(f => f.endsWith('.log'))) {
    const text = fs.readFileSync(`${directory}/${file}`, 'utf8');
    assert.doesNotMatch(text, /ERROR: AddressSanitizer|runtime error:|fail:|crit:|Unhandled|FAIL/);
    const parse = p => [...text.matchAll(new RegExp(`^${p} (.*)$`, 'gm'))].map(m => JSON.parse(m[1]));
    const workers = parse('OWNED_METRICS');
    const sum = (rows, key) => rows.reduce((n, r) => n + r[key], 0);
    assert.equal(workers.length, 4, file);
    assert.ok(workers.every(w => w.managedEngine && w.pages === w.returnedPages && !w.liveConnections && !w.leasedPages && !w.leasedCipherPages), file);
    const bio = parse('BIO_METRICS');
    if (bio.length) {
        assert.equal(sum(bio, 'ciphertextPagesReceived'), sum(bio, 'ciphertextPagesReturned'), file);
        assert.equal(sum(bio, 'tlsErrors'), 0, file);
    }
    const ktls = parse('KTLS_METRICS');
    if (file.startsWith('ktls') && !file.includes('lifecycle')) {
        assert.ok(sum(ktls, 'handshakes') > 0);
        assert.equal(sum(ktls, 'handshakes'), sum(ktls, 'rx'));
        assert.equal(sum(ktls, 'handshakes'), sum(ktls, 'tx'));
        assert.equal(sum(ktls, 'rejected'), 0);
    }
    console.log(`PASS ownership: ${file}`);
}
JS
echo "Evidence: $output"
