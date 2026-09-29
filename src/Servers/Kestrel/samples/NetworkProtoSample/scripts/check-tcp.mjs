import assert from 'node:assert/strict';
import net from 'node:net';
import { setTimeout as delay } from 'node:timers/promises';

const [portText, backend = 'epollTcp'] = process.argv.slice(2);
const port = Number(portText);
const get = 'GET / HTTP/1.1\r\nHost: localhost\r\n\r\n';
const close = 'GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n';

async function connect() {
    const socket = net.connect(port, '127.0.0.1');
    let buffer = Buffer.alloc(0), ended = false, error;
    socket.on('data', data => { buffer = Buffer.concat([buffer, data]); });
    socket.on('end', () => { ended = true; });
    socket.on('error', value => { error = value; });
    await new Promise((resolve, reject) => {
        socket.once('connect', resolve);
        socket.once('error', reject);
    });
    return {
        socket,
        async response() {
            const deadline = performance.now() + 10000;
            while (performance.now() < deadline) {
                if (error) throw error;
                const end = buffer.indexOf('\r\n\r\n');
                if (end >= 0) {
                    const header = buffer.subarray(0, end).toString();
                    const length = Number(header.match(/Content-Length: (\d+)/i)?.[1]);
                    assert.equal(length, 1024);
                    if (buffer.length >= end + 4 + length) {
                        assert.match(header, /^HTTP\/1.1 200 OK/);
                        assert.match(header, new RegExp(`X-Backend: ${backend}`, 'i'));
                        assert.match(header, /X-Tls: none/i);
                        assert.equal(buffer.subarray(end + 4, end + 4 + length).toString(), 'x'.repeat(1023) + '\n');
                        buffer = buffer.subarray(end + 4 + length);
                        return;
                    }
                }
                if (ended) throw Error('Premature TCP EOF');
                await delay(1);
            }
            throw Error('Response timeout');
        },
        async eof() {
            for (let i = 0; i < 1000 && !ended && !error; i++) await delay(2);
            if (error) throw error;
            assert.ok(ended);
            assert.equal(buffer.length, 0);
        }
    };
}

let peer = await connect();
try {
    peer.socket.write('GET / HTTP/1.1\r\nHost: localhost\r\nContent-Length: 40000\r\n\r\n' + 'x'.repeat(20000));
    await delay(30);
    peer.socket.write('x'.repeat(20000) + get);
    await peer.response();
    await peer.response();
    peer.socket.write(close);
    await peer.response();
    await peer.eof();
} finally {
    peer.socket.destroy();
}
peer = await connect();
try {
    peer.socket.pause();
    peer.socket.write(get.repeat(3000) + close);
    await delay(300);
    peer.socket.resume();
    for (let i = 0; i < 3001; i++) await peer.response();
    await peer.eof();
} finally {
    peer.socket.destroy();
}
peer = await connect();
peer.socket.write(get);
await peer.response();
peer.socket.resetAndDestroy();
await delay(100);
peer = await connect();
try {
    peer.socket.write(close);
    await peer.response();
    await peer.eof();
} finally {
    peer.socket.destroy();
}
console.log(`PASS ${backend} TCP: 40 KiB body, 3001 pipelined/slow-read responses, reset, continued service, no TLS`);
