#!/usr/bin/env bash
# Build mmencoder with dev features (MMPROTECT_DEV_BUILD defined via Debug config).
# NOT for production — enables --dev flag and LocalDevEncoder.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

PROJECT="src/EncoderCli/EncoderCli.csproj"
OUT="artifacts/encoder/linux-x64-dev"

dotnet restore "$PROJECT"
dotnet publish "$PROJECT" -c Debug -r linux-x64 --self-contained false -o "$OUT"

echo "[build-encoder-dev] Artefakt: $OUT (DEV BUILD — nicht für Produktion)"
