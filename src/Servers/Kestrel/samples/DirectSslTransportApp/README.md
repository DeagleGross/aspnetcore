# DirectSsl Transport Sample

This sample demonstrates the experimental DirectSsl transport, which uses native OpenSSL for TLS instead of SslStream.
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
