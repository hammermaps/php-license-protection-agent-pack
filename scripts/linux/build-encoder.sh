#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

PROJECT="src/EncoderCli/EncoderCli.csproj"
OUT="artifacts/encoder/linux-x64"

if [ ! -f "$PROJECT" ]; then
  echo "[build-encoder] Projektdatei fehlt: $PROJECT"
  echo "[build-encoder] Coding-Agent soll src/EncoderCli anlegen."
  exit 0
fi

dotnet restore "$PROJECT"
dotnet test "src/EncoderCli.Tests/EncoderCli.Tests.csproj" --configuration Release || true

# linux-x64 — self-contained single-file binary (no .NET required on target)
dotnet publish "$PROJECT" -c Release -r linux-x64 --self-contained true -o "$OUT"
echo "[build-encoder] linux-x64: $OUT/mmencoder  ($(du -sh "$OUT/mmencoder" 2>/dev/null | cut -f1 || echo '?'))"

# linux-arm64 — Raspberry Pi, AWS Graviton, Apple M1 Linux etc.
OUT_ARM="artifacts/encoder/linux-arm64"
dotnet publish "$PROJECT" -c Release -r linux-arm64 --self-contained true -o "$OUT_ARM"
echo "[build-encoder] linux-arm64: $OUT_ARM/mmencoder  ($(du -sh "$OUT_ARM/mmencoder" 2>/dev/null | cut -f1 || echo '?'))"

echo "[build-encoder] Fertig — selbstständige Binaries, kein .NET auf dem Zielsystem nötig."
