import assert from 'node:assert/strict';
import net from 'node:net';
import tls from 'node:tls';
import { readFileSync } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';

const [scheme, portText, caPath, expectedBackend] = process.argv.slice(2);
const port = Number(portText);
const request = 'GET / HTTP/1.1\r\nHost: localhost\r\n\r\n';
const closing = 'GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n';
const socket = scheme === 'https'
    ? tls.connect({ host: '127.0.0.1', port, servername: 'localhost', ca: readFileSync(caPath), minVersion: 'TLSv1.2', maxVersion: 'TLSv1.2' })
    : net.connect(port, '127.0.0.1');
let buffer = Buffer.alloc(0), ended = false, error;
socket.on('data', chunk => { buffer = Buffer.concat([buffer, chunk]); });
socket.on('end', () => { ended = true; });
socket.on('error', value => { error = value; });
socket.setTimeout(5000, () => { error = Error('Socket idle timeout'); socket.destroy(); });
await new Promise((resolve, reject) => {
    socket.once(scheme === 'https' ? 'secureConnect' : 'connect', resolve);
    socket.once('error', reject);
});
let connectionId;
async function response(number) {
    const deadline = performance.now() + 5000;
    while (performance.now() < deadline) {
        if (error) throw error;
        const end = buffer.indexOf('\r\n\r\n');
        if (end >= 0) {
            const headers = buffer.subarray(0, end).toString();
            const length = Number(headers.match(/content-length: (\d+)/i)?.[1]);
            if (buffer.length >= end + 4 + length) {
                assert.match(headers, /^HTTP\/1\.1 200 OK/);
                assert.match(headers, new RegExp(`X-Backend: ${expectedBackend}`, 'i'));
                assert.match(headers, new RegExp(`X-Connection-Request: ${number}(?:\\r\\n|$)`, 'i'));
                const id = headers.match(/X-Connection-Id: ([^\r\n]+)/i)?.[1];
                if (connectionId) assert.equal(id, connectionId);
                connectionId = id;
                assert.equal(buffer.subarray(end + 4, end + 4 + length).toString(), 'x'.repeat(1023) + '\n');
                buffer = buffer.subarray(end + 4 + length);
                return;
            }
        }
        if (ended) throw Error('Premature EOF');
        await delay(1);
    }
    throw Error('Response timeout');
}
try {
    socket.write('GET / HTTP/1.1\r\nHost: local');
    await delay(30);
    assert.equal(buffer.length, 0);
    socket.write('host\r\n\r\n');
    await response(1);
    for (let i = 2; i <= 100; i++) {
        socket.write(request);
        await response(i);
    }
    // Cross a native 16 KiB receive-page boundary with an incomplete header.
    socket.write('GET / HTTP/1.1\r\nHost: localhost\r\nX-Large: ' + 'a'.repeat(10000));
    await delay(20);
    socket.write('a'.repeat(10000) + '\r\n\r\n');
    await response(101);
    socket.write(request.repeat(30) + closing);
    for (let i = 102; i <= 132; i++) await response(i);
    const deadline = performance.now() + 5000;
    while (!ended && performance.now() < deadline) {
        if (error) throw error;
        await delay(1);
    }
    assert.ok(ended, 'Connection: close should produce EOF');
    assert.equal(buffer.length, 0);
    console.log(`PASS ${expectedBackend} ${scheme}: fragmented headers, page boundary, 132 requests, pipelining, same connection, close`);
} finally {
    socket.destroy();
}
