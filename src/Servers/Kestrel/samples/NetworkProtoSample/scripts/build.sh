#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
exec ./src/Servers/Kestrel/build.sh \
    --projects "$SAMPLE/NetworkProtoSample.csproj" --configuration Release \
    --no-build-native --no-build-nodejs --no-build-java "$@"
