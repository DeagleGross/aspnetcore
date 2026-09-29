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
out="$sample/results/epoll-tcp-check-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$out"
dll="$root/artifacts/bin/NetworkProtoSample/Release/net11.0/NetworkProtoSample.dll"
prefix=()
if [[ ${SANITIZE:-0} == 1 ]]; then
    mkdir -p "$out/deployment"
    find "$(dirname "$dll")" -mindepth 1 -maxdepth 1 ! -name results -exec cp -a -t "$out/deployment" {} +
    cc -std=c11 -O1 -g -Wall -Wextra -Werror -fPIC -shared -fsanitize=address,undefined -fno-omit-frame-pointer \
        "$root/src/Servers/Kestrel/Transport.Networking/src/Epoll/native.c" -lssl -lcrypto -o "$out/deployment/libnetworkprotoepoll.so"
    dll="$out/deployment/NetworkProtoSample.dll"
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
port=${PORT:-20150}
for workers in 1 2 4; do
    case $workers in 1) export NETWORKPROTO_CPUS=0;; 2) export NETWORKPROTO_CPUS=0,2;; 4) export NETWORKPROTO_CPUS=0,2,4,6;; esac
    args=(--backend epollTcp --workers "$workers" --scheme http --port "$port" --Logging:LogLevel:Default Warning)
    DOTNET_PROCESSOR_COUNT=4 taskset -c 0,2,4,6 "${prefix[@]}" dotnet "$dll" "${args[@]}" > "$out/server-$workers.log" 2>&1 &
    pid=$!
    ready=0
    for i in $(seq 1 100); do
        if ! kill -0 "$pid" 2>/dev/null; then cat "$out/server-$workers.log"; exit 1; fi
        if curl -fsS --max-time 1 "http://127.0.0.1:$port/metrics" > "$out/before-$workers.json" 2>/dev/null; then ready=1; break; fi
        sleep .1
    done
    [[ $ready == 1 ]] || { echo "STARTUP_TIMEOUT" >&2; exit 1; }
    node "$sample/scripts/check-owned.mjs" http "$port" unused epollTcp
    node "$sample/scripts/check-tcp.mjs" "$port" epollTcp
    curl -fsS --max-time 3 "http://127.0.0.1:$port/metrics" > "$out/after-$workers.json"
    cleanup
    for check in check-epoll-lifecycle check-epoll-tcp; do
        timeout --kill-after=2s 30s "${prefix[@]}" dotnet "$dll" "${args[@]}" "--$check" true > "$out/$check-$workers.log" 2>&1 \
            || { cat "$out/$check-$workers.log"; exit 1; }
        grep '^PASS' "$out/$check-$workers.log"
    done
    for mode in 0 3; do
        NETWORKPROTO_FINAL_SEND="$mode" NETWORKPROTO_EPOLL_SNDBUF=4096 \
            timeout --kill-after=2s 40s "${prefix[@]}" dotnet "$dll" "${args[@]}" --check-final-send true > "$out/pressure$mode-$workers.log" 2>&1 \
            || { cat "$out/pressure$mode-$workers.log"; exit 1; }
        grep '^PASS' "$out/pressure$mode-$workers.log"
    done
    node --input-type=module - "$out" "$workers" <<'JS'
import fs from 'node:fs';
import assert from 'node:assert/strict';
const [out, workersText] = process.argv.slice(2), workers = Number(workersText);
const metrics = JSON.parse(fs.readFileSync(`${out}/after-${workers}.json`));
assert.equal(metrics.backend, 'epollTcp');
assert.equal(metrics.scheme, 'http');
assert.equal(metrics.handshakes, 0);
for (const kind of ['server', 'check-epoll-lifecycle', 'check-epoll-tcp', 'pressure0', 'pressure3']) {
    const log = fs.readFileSync(`${out}/${kind}-${workers}.log`, 'utf8');
    assert.doesNotMatch(log, /ERROR: AddressSanitizer|runtime error:|fail:|crit:|Unhandled|FAIL|EPOLL_TLS_LIBRARY/);
    const parse = p => [...log.matchAll(new RegExp(`^${p} (.*)$`, 'gm'))].map(m => JSON.parse(m[1]));
    const owned = parse('OWNED_METRICS'), epoll = parse('EPOLL_METRICS'), final = parse('EPOLL_FINAL_SEND_METRICS');
    const sum = (rows, key) => rows.reduce((n, r) => n + r[key], 0);
    assert.equal(owned.length, workers);
    assert.equal(epoll.length, workers);
    assert.ok(owned.every(w => !w.tls && w.epoll && !w.liveConnections && !w.leasedPages && w.pages === w.returnedPages));
    assert.equal(sum(epoll, 'accepts'), sum(epoll, 'closed'));
    for (const field of ['handshakes', 'sslReads', 'sslWrites', 'tlsErrors', 'socketErrors']) assert.equal(sum(epoll, field), 0);
    assert.equal(sum(final, 'closeNotify'), 0);
    assert.equal(sum(final, 'shutdownFailures'), 0);
    if (kind.startsWith('pressure')) {
        assert.ok(sum(epoll, 'partialSends') > 0);
        assert.ok(sum(epoll, 'sendWouldBlock') > 0);
        assert.equal(sum(final, 'shutdowns'), kind === 'pressure3' ? 2 : 0);
        assert.equal(sum(owned, 'finalSendCommands'), kind === 'pressure3' ? 3 : 0);
    } else if (kind === 'check-epoll-tcp') {
        assert.ok(sum(epoll, 'readPauses') > 0);
        assert.ok(sum(epoll, 'readEofs') > 0);
        assert.equal(sum(epoll, 'recvBytes'), 256 * 1024);
        assert.equal(sum(epoll, 'sendBytes'), 1024);
    } else if (kind === 'check-epoll-lifecycle') {
        assert.equal(sum(epoll, 'accepts'), 16);
    } else {
        assert.ok(sum(epoll, 'recvCalls') > 0 && sum(epoll, 'sendCalls') > 0);
    }
}
console.log(`PASS epoll TCP workers=${workers}: actual recv/send, partial/EAGAIN sends, page backpressure, half-close, reset and drained ownership`);
JS
    ((port+=1))
done
echo "Evidence: $out"
