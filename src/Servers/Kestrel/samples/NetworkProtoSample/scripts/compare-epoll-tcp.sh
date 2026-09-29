#!/usr/bin/env bash
set -eo pipefail
sample=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
root=$(cd "$sample/../../../../.." && pwd)
cd "$root"
export LD_LIBRARY_PATH="${SERVER_OPENSSL_LIB:-/opt/openssl-3.5.8/lib}:$HOME/.local/lib"
export NETWORKPROTO_COALESCE=1 NETWORKPROTO_FINAL_SEND=3 NETWORKPROTO_KTLS=0
export NETWORKPROTO_CPUS=0,2,4,6 WORKERS=4 SCHEME=http TRIALS=1
unset NETWORKPROTO_EPOLL_SNDBUF NETWORKPROTO_URING_SNDBUF SAMPLE_DLL
out="$sample/results/epoll-tcp-matrix-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$out"
printf 'round\tmode\tbackend\tresult\n' > "$out/runs.tsv"
{
    date --iso-8601=seconds
    uname -r
    git rev-parse HEAD
    sha256sum "$root/artifacts/bin/NetworkProtoSample/Release/net11.0/"{NetworkProtoSample.dll,Microsoft.AspNetCore.Server.Kestrel.Transport.Networking.dll,libnetworkprotoepoll.so,libnetworkproto2.so}
    printf 'HTTP only; original one-source wrk2; 4 server/12 client cores; 1200 connections; 1024 bytes; 15s; coalesced wakes and final batching\n'
} > "$out/environment.txt"
port=${PORT:-20200}
for mode in close keepalive; do
    export MODE="$mode"
    for round in 1 2; do
        backends=(sockets IoUringTcp epollTcp)
        if [[ $round == 2 ]]; then backends=(epollTcp IoUringTcp sockets); fi
        for backend in "${backends[@]}"; do
            log="$out/$mode-r$round-$backend.log"
            cat /proc/net/netstat > "$out/$mode-r$round-$backend-netstat-before.txt"
            echo "=== TCP $mode r$round $backend ==="
            PORT="$port" RUN_LABEL="epoll-tcp-$mode-r$round-$backend" "$sample/scripts/quick-rps.sh" "$backend" > "$log" 2>&1 \
                || { tail -40 "$log"; exit 1; }
            grep -E 'Requests/sec:|Socket errors:|RESULTS|SERVER_ERRORS|SHUTDOWN_TIMEOUT' "$log"
            directory=$(awk '/^RESULTS / {print $2}' "$log")
            printf '%s\t%s\t%s\t%s\n' "$round" "$mode" "$backend" "$directory" >> "$out/runs.tsv"
            cat /proc/net/netstat > "$out/$mode-r$round-$backend-netstat-after.txt"
            ((port+=1))
        done
    done
done
node --input-type=module - "$out" <<'JS'
import fs from 'node:fs';
import assert from 'node:assert/strict';
const out = process.argv[2], results = [];
for (const line of fs.readFileSync(`${out}/runs.tsv`, 'utf8').trim().split('\n').slice(1)) {
    const [round, mode, backend, path] = line.split('\t');
    const files = fs.readdirSync(path);
    const log = fs.readFileSync(`${path}/${files.find(f => f.endsWith('-server.log'))}`, 'utf8');
    const wrk = fs.readFileSync(`${path}/${files.find(f => f.endsWith('-wrk.txt'))}`, 'utf8');
    const before = JSON.parse(fs.readFileSync(`${path}/${files.find(f => f.endsWith('-before.json'))}`));
    const after = JSON.parse(fs.readFileSync(`${path}/${files.find(f => f.endsWith('-after.json'))}`));
    const parse = p => [...log.matchAll(new RegExp(`^${p} (.*)$`, 'gm'))].map(m => JSON.parse(m[1]));
    const sum = (rows, key) => rows.reduce((n, r) => n + r[key], 0);
    const owned = parse('OWNED_METRICS'), epoll = parse('EPOLL_METRICS');
    const final = parse(backend === 'epollTcp' ? 'EPOLL_FINAL_SEND_METRICS' : 'FINAL_SEND_METRICS');
    assert.doesNotMatch(log, /fail:|crit:|Unhandled|Assertion|ERROR: AddressSanitizer/);
    assert.equal(after.backend, backend);
    assert.equal(after.scheme, 'http');
    assert.equal(after.handshakes, 0);
    if (backend !== 'sockets') {
        assert.equal(owned.length, 4);
        assert.ok(owned.every(w => !w.tls && w.managedEngine && w.pages === w.returnedPages && !w.liveConnections && !w.leasedPages));
    }
    if (backend === 'epollTcp') {
        assert.equal(epoll.length, 4);
        assert.equal(sum(epoll, 'accepts'), sum(epoll, 'closed'));
        for (const key of ['sslReads', 'sslWrites', 'handshakes', 'socketErrors', 'tlsErrors']) assert.equal(sum(epoll, key), 0);
        assert.ok(sum(epoll, 'recvCalls') > 0 && sum(epoll, 'sendCalls') > 0);
        assert.equal(sum(final, 'closeNotify'), 0);
        assert.equal(sum(final, 'shutdownFailures'), 0);
    }
    const netstat = suffix => {
        const lines = fs.readFileSync(`${out}/${mode}-r${round}-${backend}-netstat-${suffix}.txt`, 'utf8').trim().split('\n');
        const header = lines.findIndex(l => l.startsWith('TcpExt:'));
        const keys = lines[header].split(/\s+/), values = lines[header + 1].split(/\s+/);
        return Object.fromEntries(keys.slice(1).map((k, i) => [k, Number(values[i + 1])]));
    };
    const n0 = netstat('before'), n1 = netstat('after');
    results.push({
        round: Number(round), mode, backend, path, rps: Number(wrk.match(/Requests\/sec:\s+(\S+)/)[1]),
        errors: wrk.match(/^.*(?:Socket errors:|Non-2xx).*$/gm) || [],
        timeWaitOverflow: n1.TCPTimeWaitOverflow - n0.TCPTimeWaitOverflow,
        before, after, owned, epoll, final
    });
}
const means = {};
for (const backend of ['sockets', 'IoUringTcp', 'epollTcp']) {
    means[backend] = {};
    for (const mode of ['close', 'keepalive']) {
        const group = results.filter(r => r.backend === backend && r.mode === mode);
        means[backend][mode] = group.reduce((n, r) => n + r.rps, 0) / group.length;
    }
    console.log(`${backend}: short=${means[backend].close.toFixed(2)} long=${means[backend].keepalive.toFixed(2)}; vs stock=${((means[backend].close / means.sockets.close - 1) * 100).toFixed(1)}% / ${((means[backend].keepalive / means.sockets.keepalive - 1) * 100).toFixed(1)}%`);
}
fs.writeFileSync(`${out}/validated-summary.json`, JSON.stringify({ means, results }, null, 2) + '\n');
console.log(`AUDIT ${results.length} runs: actual raw TCP, zero TLS calls, all worker/page totals complete`);
JS
echo "MATRIX $out"
