#!/bin/bash
# Generate self-signed ECDSA certificates for testing DirectSsl transport
# Generates both P-256 (faster handshakes) and P-384 (stronger security) variants
# Run this script before starting the sample app

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

SUBJ="/C=US/ST=Test/L=Test/O=Test/CN=localhost"
SAN="subjectAltName=DNS:localhost,IP:127.0.0.1,IP:::1"

# --- P-256 (secp256r1) - faster TLS handshakes, 128-bit security ---
echo "Generating P-256 ECDSA certificate..."

openssl ecparam -genkey -name prime256v1 -noout -out server-p256.key

openssl req -new -x509 -key server-p256.key -out server-p256.crt -days 365 \
    -subj "$SUBJ" \
    -addext "$SAN"

openssl pkcs12 -export -out server-p256.pfx -inkey server-p256.key -in server-p256.crt \
    -passout pass:testpassword

# --- P-384 (secp384r1) - stronger security, 192-bit equivalent ---
echo "Generating P-384 ECDSA certificate..."

openssl ecparam -genkey -name secp384r1 -noout -out server-p384.key

openssl req -new -x509 -key server-p384.key -out server-p384.crt -days 365 \
    -subj "$SUBJ" \
    -addext "$SAN"

openssl pkcs12 -export -out server-p384.pfx -inkey server-p384.key -in server-p384.crt \
    -passout pass:testpassword

echo ""
echo "Generated test certificates:"
echo "  P-256 (recommended for perf testing):"
echo "    server-p256.key  - Private key (PEM)"
echo "    server-p256.crt  - Certificate (PEM)"
echo "    server-p256.pfx  - PKCS#12 bundle (password: testpassword)"
echo "  P-384:"
echo "    server-p384.key  - Private key (PEM)"
echo "    server-p384.crt  - Certificate (PEM)"
echo "    server-p384.pfx  - PKCS#12 bundle (password: testpassword)"
