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
export NETWORKPROTO_FINAL_SEND=3 NETWORKPROTO_COALESCE=1 NETWORKPROTO_KTLS=0
output="$sample/results/epoll-check-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$output"
native="$root/src/Servers/Kestrel/Transport.Networking/src/Epoll"
dll="$root/artifacts/bin/NetworkProtoSample/Release/net11.0/NetworkProtoSample.dll"
prefix=()
if [[ ${SANITIZE:-0} == 1 ]]; then
    deployment="$output/deployment"
    mkdir -p "$deployment"
    find "$(dirname "$dll")" -mindepth 1 -maxdepth 1 ! -name results -exec cp -a -t "$deployment" {} +
    cc -std=c11 -O1 -g -Wall -Wextra -Werror -fPIC -shared -fsanitize=address,undefined -fno-omit-frame-pointer \
        "$native/native.c" -lssl -lcrypto -o "$deployment/libnetworkprotoepoll.so"
    dll="$deployment/NetworkProtoSample.dll"
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
port=${PORT:-19780}
for workers in 1 2 4; do
    case $workers in 1) export NETWORKPROTO_CPUS=0;; 2) export NETWORKPROTO_CPUS=0,2;; 4) export NETWORKPROTO_CPUS=0,2,4,6;; esac
    args=(--backend epollTls --workers "$workers" --scheme https --port "$port"
        --cert "$sample/.certs-owned/cert.pem" --key "$sample/.certs-owned/key.pem" --Logging:LogLevel:Default Warning)
    DOTNET_PROCESSOR_COUNT=4 taskset -c 0,2,4,6 "${prefix[@]}" dotnet "$dll" "${args[@]}" > "$output/server-$workers.log" 2>&1 &
    pid=$!
    ready=0
    for i in $(seq 1 100); do
        if ! kill -0 "$pid" 2>/dev/null; then cat "$output/server-$workers.log"; exit 1; fi
        if curl --cacert "$sample/.certs-owned/cert.pem" --tlsv1.2 --tls-max 1.2 -fsS --max-time 1 "https://127.0.0.1:$port/metrics" > "$output/before-$workers.json" 2>/dev/null; then ready=1; break; fi
        sleep .1
    done
    [[ $ready == 1 ]] || { echo "STARTUP_TIMEOUT" >&2; exit 1; }
    node "$sample/scripts/check-owned.mjs" https "$port" "$sample/.certs-owned/cert.pem" epollTls
    node "$sample/scripts/check-bio.mjs" "$port" "$sample/.certs-owned/cert.pem"
    if [[ $workers == 4 ]]; then
        node --input-type=module - "$port" <<'JS'
import net from 'node:net';
import assert from 'node:assert/strict';
const start = performance.now();
const socket = net.connect(Number(process.argv[2]), '127.0.0.1');
const timer = setTimeout(() => socket.destroy(Error('Idle handshake did not time out')), 15000);
try {
    await new Promise((resolve, reject) => {
        socket.on('data', () => reject(Error('Unexpected data without a ClientHello')));
        socket.once('error', reject);
        socket.once('end', resolve);
    });
    assert.ok(performance.now() - start >= 9500, 'Handshake closed before its deadline');
    console.log('PASS managed epoll deadline: real idle handshake closed after timeout');
} finally {
    clearTimeout(timer);
    socket.destroy();
}
JS
    fi
    printf 'GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' |
        timeout --kill-after=2s 10s openssl s_client -connect "127.0.0.1:$port" -servername localhost \
            -CAfile "$sample/.certs-owned/cert.pem" -verify_return_error -tls1_2 -quiet -state -ign_eof \
            > "$output/response-$workers.txt" 2> "$output/tls-state-$workers.txt"
    grep -q 'SSL3 alert read:warning:close notify' "$output/tls-state-$workers.txt"
    cleanup
    timeout --kill-after=2s 20s "${prefix[@]}" dotnet "$dll" "${args[@]}" --check-epoll-lifecycle true > "$output/lifecycle-$workers.log" 2>&1 \
        || { cat "$output/lifecycle-$workers.log"; exit 1; }
    node --input-type=module - "$output" "$workers" <<'JS'
import fs from 'node:fs';
import assert from 'node:assert/strict';
const [output, workersText] = process.argv.slice(2), workers = Number(workersText);
for (const kind of ['server', 'lifecycle']) {
    const text = fs.readFileSync(`${output}/${kind}-${workers}.log`, 'utf8');
    assert.doesNotMatch(text, /ERROR: AddressSanitizer|runtime error:|fail:|crit:|Unhandled|FAIL/);
    const parse = prefix => [...text.matchAll(new RegExp(`^${prefix} (.*)$`, 'gm'))].map(m => JSON.parse(m[1]));
    const owned = parse('OWNED_METRICS'), native = parse('EPOLL_METRICS');
    const sum = (rows, key) => rows.reduce((s, r) => s + r[key], 0);
    assert.equal(owned.length, workers);
    assert.equal(native.length, workers);
    assert.ok(owned.every(r => r.epoll && r.pages === r.returnedPages && r.acceptSqes === 0 && r.cqes === 0));
    assert.equal(sum(native, 'accepts'), sum(native, 'closed'));
    assert.equal(sum(native, 'tlsErrors'), 0);
    assert.equal(sum(native, 'shutdownTimeouts'), 0);
    if (kind === 'lifecycle') {
        assert.match(text, /PASS epoll lifecycle/);
        assert.equal(sum(native, 'accepts'), 16);
        assert.equal(sum(native, 'handshakes'), 0);
    } else {
        assert.ok(sum(native, 'handshakes') > 0);
        assert.ok(sum(native, 'readPauses') > 0);
        assert.equal(sum(native, 'handshakeTimeouts'), workers === 4 ? 1 : 0);
    }
}
console.log(`PASS epoll workers=${workers}: Kestrel data/close, reader backpressure, handshake-stop ownership, drained workers`);
JS
    ((port+=1))
done
NETWORKPROTO_EPOLL_SNDBUF=4096 timeout --kill-after=2s 40s "${prefix[@]}" dotnet "$dll" "${args[@]}" --check-final-send true > "$output/pressure.log" 2>&1 \
    || { cat "$output/pressure.log"; exit 1; }
grep '^PASS' "$output/pressure.log"
node --input-type=module - "$output/pressure.log" <<'JS'
import fs from 'node:fs';
import assert from 'node:assert/strict';
const text = fs.readFileSync(process.argv[2], 'utf8');
assert.doesNotMatch(text, /ERROR: AddressSanitizer|runtime error:|fail:|crit:|Unhandled|FAIL/);
assert.equal([...text.matchAll(/^PASS final-send:/gm)].length, 3);
const parse = prefix => [...text.matchAll(new RegExp(`^${prefix} (.*)$`, 'gm'))].map(m => JSON.parse(m[1]));
const workers = parse('EPOLL_METRICS'), final = parse('EPOLL_FINAL_SEND_METRICS');
const sum = (rows, key) => rows.reduce((n, r) => n + r[key], 0);
assert.equal(workers.length, 4);
assert.ok(sum(workers, 'writeRetries') > 0);
assert.equal(sum(workers, 'tlsErrors'), 0);
assert.equal(sum(workers, 'accepts'), sum(workers, 'closed'));
assert.equal(sum(final, 'shutdowns'), 2);
assert.equal(sum(final, 'closeNotify'), 2);
console.log(`PASS managed epoll pressure: ${sum(workers, 'writeRetries')} real OpenSSL write retries, complete payload/EOF and reset cleanup`);
JS
echo "Evidence: $output"
