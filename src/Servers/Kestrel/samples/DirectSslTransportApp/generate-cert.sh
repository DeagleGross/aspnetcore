#!/bin/bash
# Generate self-signed ECDSA P-384 certificate for testing DirectSsl transport
# Run this script before starting the sample app

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Generate P-384 ECDSA private key
openssl ecparam -genkey -name secp384r1 -noout -out server-p384.key

# Generate self-signed certificate (valid for 365 days)
openssl req -new -x509 -key server-p384.key -out server-p384.crt -days 365 \
    -subj "/C=US/ST=Test/L=Test/O=Test/CN=localhost" \
    -addext "subjectAltName=DNS:localhost,IP:127.0.0.1,IP:::1"

# Create PFX for standard Kestrel TLS comparison mode
openssl pkcs12 -export -out server-p384.pfx -inkey server-p384.key -in server-p384.crt \
    -passout pass:testpassword

echo "Generated test certificates:"
echo "  server-p384.key  - Private key (PEM)"
echo "  server-p384.crt  - Certificate (PEM)"
echo "  server-p384.pfx  - PKCS#12 bundle (password: testpassword)"
