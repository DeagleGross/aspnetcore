#!/bin/bash
# Enable or disable DIRECTSSL_DEBUG_COUNTERS across the four pump/state files
# in both engines. Without the define the counter fields don't exist and the
# instrumentation is fully compiled out (zero overhead).
#
# Usage:
#   ./toggle-counters.sh on      # enable
#   ./toggle-counters.sh off     # disable

set -eu
MODE="${1:-}"
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../../../../.." &>/dev/null && pwd)"
FILES=(
  "$ROOT/src/Servers/Kestrel/Transport.Sockets/src/DirectSsl/SslEventPump.cs"
  "$ROOT/src/Servers/Kestrel/Transport.Sockets/src/DirectSsl/SslConnectionState.cs"
  "$ROOT/src/Servers/Kestrel/Transport.Sockets/src/DirectSsl/Engines/OpenSslDirect/SslEventPump.cs"
  "$ROOT/src/Servers/Kestrel/Transport.Sockets/src/DirectSsl/Engines/OpenSslDirect/SslConnectionState.cs"
)

case "$MODE" in
  on)
    for f in "${FILES[@]}"; do
      sed -i 's|^// #define DIRECTSSL_DEBUG_COUNTERS|#define DIRECTSSL_DEBUG_COUNTERS|' "$f"
    done
    echo "Counters enabled. Rebuild with: dotnet build -c Release"
    ;;
  off)
    for f in "${FILES[@]}"; do
      sed -i 's|^#define DIRECTSSL_DEBUG_COUNTERS|// #define DIRECTSSL_DEBUG_COUNTERS|' "$f"
    done
    echo "Counters disabled. Rebuild with: dotnet build -c Release"
    ;;
  *)
    echo "Usage: $0 on|off" >&2
    exit 2
    ;;
esac

echo "Affected files:"
for f in "${FILES[@]}"; do
  head -5 "$f" | grep -n 'DIRECTSSL_DEBUG_COUNTERS' | sed "s|^|  $f:|"
done
