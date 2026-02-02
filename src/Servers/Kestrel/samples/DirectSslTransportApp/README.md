# DirectSsl Transport Sample

This sample demonstrates the experimental DirectSsl transport, which uses native OpenSSL for TLS instead of SslStream.

## Prerequisites

- Linux with libssl.so.3 and libcrypto.so.3 (OpenSSL 3.x)
- openssl CLI tool (for certificate generation)

## Setup

Generate test certificates before running:

```bash
./generate-cert.sh
```

This creates:
- `server-p384.key` - ECDSA P-384 private key
- `server-p384.crt` - Self-signed certificate
- `server-p384.pfx` - PKCS#12 bundle for comparison mode

## Running

```bash
# Using DirectSsl transport (OpenSSL)
dotnet run

# Using standard Kestrel TLS (SslStream) for comparison
USE_STANDARD_TLS=1 dotnet run
```

## Testing

```bash
curl -k https://localhost:5001/
```

## Architecture

The DirectSsl transport bypasses .NET's SslStream and uses:
- Native OpenSSL TLS handshake and encryption
- EPOLLEXCLUSIVE for scalable multi-worker accept
- Dedicated epoll threads for TLS I/O events
- Zero-allocation async patterns with ManualResetValueTaskSourceCore

This is experimental and intended for scenarios requiring maximum TLS throughput on Linux.
