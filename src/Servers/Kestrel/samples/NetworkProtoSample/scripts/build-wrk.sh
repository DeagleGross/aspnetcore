#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
directory="$ROOT/artifacts/networkproto-wrk"
revision=7594a95186ebdfa7cb35477a8a811f84e2a31b62
mkdir -p "$directory"
cd "$directory"
if [[ ! -f source.tar.gz ]]; then
    curl -fL --silent --show-error "https://api.github.com/repos/wg/wrk/tarball/$revision" -o source.tar.gz
fi
echo '49c309c834c484243d1f381505e7723326c5a9b6e328d88adef9ead804c8d83e  source.tar.gz' | sha256sum -c -
tar -xzf source.tar.gz --strip-components=1
patch --batch --fuzz=0 -p1 <"$SAMPLE/scripts/wrk-monotonic.patch"
# Upstream's make rule expands PATH without quoting; WSL imports Windows paths with spaces.
PATH=/usr/local/bin:/usr/bin:/bin make -j4 WITH_OPENSSL=/usr VER=4.1.0-monotonic
sha256sum source.tar.gz "$SAMPLE/scripts/wrk-monotonic.patch" wrk >provenance.sha256
echo "Local monotonic-clock wrk: $directory/wrk"
