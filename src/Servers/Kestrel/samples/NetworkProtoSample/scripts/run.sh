#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
certificates
prefix=()
if [[ -n "${SERVER_CPUS:-}" ]]; then prefix=(taskset -c "$SERVER_CPUS"); fi
exec env DOTNET_PROCESSOR_COUNT="${SERVER_PROCESSORS:-4}" "${prefix[@]}" dotnet "$DLL" \
    --backend "${1:-sockets}" --scheme "$SCHEME" --port "$PORT" \
    --cert "$SAMPLE/.certs/cert.pem" --key "$SAMPLE/.certs/key.pem"
