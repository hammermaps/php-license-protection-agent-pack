#!/usr/bin/env bash
# Build mmloader.so with dev/demo features enabled (MMPROTECT_DEV_BUILD=1).
# NOT for production — for local development and testing only.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DECODER="$ROOT/src/PhpDecoderLoader"

if [ ! -d "$DECODER" ]; then
  echo "[build-decoder-dev] Verzeichnis fehlt: $DECODER"
  exit 1
fi

if ! command -v phpize >/dev/null 2>&1; then
  echo "[build-decoder-dev] phpize fehlt. Installiere php8.4-dev/php-dev."
  exit 1
fi

cd "$DECODER"
phpize
./configure --enable-mmloader --enable-mmloader-dev
make -j"$(nproc)"

mkdir -p "$ROOT/artifacts/decoder/linux-x64"
find . -name "mmloader.so" -o -name "*.so" | head -n 1 | while read -r sofile; do
  cp "$sofile" "$ROOT/artifacts/decoder/linux-x64/mmloader-dev.so"
done

echo "[build-decoder-dev] Artefakt: artifacts/decoder/linux-x64/mmloader-dev.so (DEV BUILD — nicht für Produktion)"
