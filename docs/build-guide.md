# MMProtect Build Guide

This guide covers how to build all three MMProtect components from source on Linux and how to run the test suites. For Windows builds, see `scripts/windows/`.

---

## Prerequisites

### Linux (Ubuntu 22.04 / 24.04)

```bash
sudo apt-get update
sudo apt-get install -y \
    build-essential autoconf pkg-config \
    php8.4-dev php8.4-cli php8.4-opcache \
    libssl-dev libcurl4-openssl-dev \
    dotnet-sdk-8.0 \
    sqlite3 \
    curl git openssl

# Optional: PHP 8.5 loader build
sudo apt-get install -y php8.5-dev php8.5-cli php8.5-opcache

# Optional: Python + cryptography (for test fixtures)
pip3 install cryptography
```

### Verify prerequisites

```bash
dotnet --version           # 8.0.x
php8.4 --version           # PHP 8.4.x
php-config8.4 --version    # 8.4.x
openssl version            # OpenSSL 3.x
```

---

## Build All (one command)

```bash
scripts/linux/build-all.sh
```

This script builds the License Server, Encoder CLI, and PHP Decoder in sequence and places artefacts in `artifacts/`.

---

## Building Individual Components

### License Server

```bash
dotnet publish src/LicenseServer/LicenseServer.csproj \
    -c Release -r linux-x64 --self-contained false \
    -o artifacts/server/linux-x64
```

Output: `artifacts/server/linux-x64/MmProtect.LicenseServer.dll`

### Encoder CLI

The Encoder CLI is published as a **self-contained single-file binary** — the .NET runtime is bundled inside the binary. No .NET installation is required on the target machine.

```bash
# Linux x64 (Servers, WSL, GitHub Actions ubuntu runners)
dotnet publish src/EncoderCli/EncoderCli.csproj \
    -c Release -r linux-x64 --self-contained true \
    -o artifacts/encoder/linux-x64

# Linux ARM64 (Raspberry Pi, AWS Graviton, Apple M1 Linux)
dotnet publish src/EncoderCli/EncoderCli.csproj \
    -c Release -r linux-arm64 --self-contained true \
    -o artifacts/encoder/linux-arm64

# Windows x64
dotnet publish src/EncoderCli/EncoderCli.csproj \
    -c Release -r win-x64 --self-contained true \
    -o artifacts/encoder/win-x64

# Windows ARM64 (Surface Pro X, Snapdragon X Elite)
dotnet publish src/EncoderCli/EncoderCli.csproj \
    -c Release -r win-arm64 --self-contained true \
    -o artifacts/encoder/win-arm64
```

Or use the scripts:

```bash
scripts/linux/build-encoder.sh    # builds linux-x64 + linux-arm64
scripts/windows/build-encoder.cmd # builds win-x64 + win-arm64
```

Output:
- `artifacts/encoder/linux-x64/mmencoder` (~60–90 MB, standalone)
- `artifacts/encoder/linux-arm64/mmencoder`
- `artifacts/encoder/win-x64/mmencoder.exe`
- `artifacts/encoder/win-arm64/mmencoder.exe`

> **Hinweis:** `PublishTrimmed` ist deaktiviert weil `System.Text.Json` (Reflection-basiert) und `XDocument` nicht trim-sicher sind. Die Binary-Größe ist größer als bei getrimmten Builds (~60–90 MB), dafür ist sie stets korrekt.

### PHP Decoder/Loader (PHP 8.4)

```bash
cd src/PhpDecoderLoader
phpize
./configure --enable-mmloader
make -j$(nproc)
mkdir -p ../../artifacts/decoder/linux-x64
cp modules/mmloader.so ../../artifacts/decoder/linux-x64/mmloader.so
```

Or use the helper script:

```bash
scripts/linux/build-decoder.sh
```

Output: `artifacts/decoder/linux-x64/mmloader.so`

### PHP Decoder/Loader (PHP 8.5)

```bash
scripts/linux/build-decoder-php85.sh
```

Output: `artifacts/decoder/linux-x64/mmloader-php85.so`

Requirements: `php8.5-dev` installed (see Prerequisites).

### Dev Builds (Encoder + Decoder)

Dev builds include `MMPROTECT_DEV_BUILD` and unlock the `--dev` encoder flag and `mmloader.dev_mode` INI setting. Required for the demo test and local development without a license server.

```bash
# Dev encoder (output: artifacts/encoder/linux-x64-dev/)
scripts/linux/build-encoder-dev.sh

# Dev decoder for PHP 8.4 (output: artifacts/decoder/linux-x64/mmloader-dev.so)
scripts/linux/build-decoder-dev.sh

# Or build the encoder in Debug config directly to linux-x64/:
dotnet publish src/EncoderCli/EncoderCli.csproj -c Debug -r linux-x64 \
    --self-contained true -o artifacts/encoder/linux-x64
```

Dev builds must **never** be distributed to customers. In release builds, `mmloader.dev_mode`, `mmloader.dev_buildkey`, and the `--dev` encoder flag are compiled out entirely.

---

## Running Tests

### Unit/Integration tests (.NET)

```bash
dotnet test src/LicenseServer.Tests/ -v m   # 44 Tests (33 SmokeTests + 11 CryptoTests)
dotnet test src/EncoderCli.Tests/ -v m      # 90 Tests (Glob + MmIgnore + Compression + Obfuscator + Optimizer)
```

All tests run against an in-process SQLite database via `WebApplicationFactory` — no external MySQL instance needed.

### Loader smoke tests (Weeks 1–4)

Each week has its own test script in `tests/decoder-loader/`:

```bash
bash tests/decoder-loader/run-tests.sh           # Week 1: MMENC1 format, basic decrypt
bash tests/decoder-loader/run-tests-week2.sh     # Week 2: HTTP lease against mock server
bash tests/decoder-loader/run-tests-week3.sh     # Week 3: Security gates (expiry, revocation)
bash tests/decoder-loader/run-tests-week4.sh     # Week 4: ECDSA-P256, execute_ex OPcache guard
```

### Decoder fuzz tests

Corpus-based fuzz tests that load 27 purposefully malformed MMENC1 files through PHP + mmloader and verify that the loader neither crashes (SIGSEGV) nor leaks decrypted content:

```bash
bash tests/decoder-loader/run-fuzz-test.sh --ext84 artifacts/decoder/linux-x64/mmloader-dev.so
# Expected: 27 passed, 0 failed
```

Tested cases: wrong magic, empty file, truncated at various offsets, zero/huge header length, non-digit length field, invalid JSON, empty JSON, future/obsolete format version, missing required fields (nonce/tag/buildId), bad algorithm, short nonce/tag, zero/random/empty ciphertext, large buildId, null bytes in JSON, zeros after magic.

For continuous/coverage fuzzing (requires clang + AddressSanitizer):

```bash
cd tests/decoder-loader
clang -g -O1 -fsanitize=fuzzer,address \
      -I../../src/PhpDecoderLoader/vendor/cjson \
      ../../src/PhpDecoderLoader/vendor/cjson/cJSON.c \
      fuzz-mmenc-header.c \
      -o fuzz-mmenc-header
./fuzz-mmenc-header corpus/ -max_len=65536 -timeout=10
```

### Full end-to-end integration test

```bash
bash tests/integration/run-integration-test.sh
```

This test:
1. Generates ECDSA-P256 signing keys
2. Creates a fresh SQLite database
3. Starts the license server in SQLite mode
4. Runs the encoder on the demo project
5. Executes the encoded PHP with mmloader (dev_mode)
6. Executes with a live HTTP lease from the running server
7. Verifies lease records in SQLite
8. Tests with OPcache enabled
9. Tests with PHP 8.5 (if mmloader-php85.so is present)

Expected output: `7 passed, 0 failed` (PHP 8.5 skipped if not built).

### Demo-Projekt tests

The demo tests require the debug-mode encoder (includes `--dev` flag) and the dev loader:

```bash
bash tests/php-demo/run-demo-test.sh
# Expected: 31/31 passed (PHP 8.5 skip)
```

The script uses `artifacts/encoder/linux-x64/mmencoder.dll` (must be a debug build with `MMPROTECT_DEV_BUILD`) and `artifacts/decoder/linux-x64/mmloader.so` (dev build supports `mmloader.dev_mode`). To (re-)build both:

```bash
dotnet publish src/EncoderCli/EncoderCli.csproj -c Debug -r linux-x64 \
    --self-contained false -o artifacts/encoder/linux-x64
scripts/linux/build-decoder-dev.sh
cp artifacts/decoder/linux-x64/mmloader-dev.so artifacts/decoder/linux-x64/mmloader.so
```

### Comprehensive test (36 phases)

Full E2E test with local license server, SQLite, AES-256-KEK, ECDSA-P256, live HTTP lease, OPcache, APCu, LZ4, obfuscation, hostname/IP/domain constraints, revocation, rate limiting, Admin API, concurrent execution, and MMENC1 format inspection.

```bash
bash tests/comprehensive/run-comprehensive-test.sh [--ext84 PATH] [--ext85 PATH]
```

### All tests

```bash
scripts/linux/test-all.sh
```

---

## Generating Signing Keys

```bash
scripts/linux/gen-signing-keys.sh /path/to/output

# Creates:
#   /path/to/output/signing-private.pem  (keep secret)
#   /path/to/output/signing-public.pem   (distribute to customers)
```

**Never commit signing-private.pem to version control.**

---

## Artefact Layout

After a full build:

```
artifacts/
├─ server/
│  ├─ linux-x64/       MmProtect.LicenseServer.dll + appsettings.json
│  └─ win-x64/
├─ encoder/
│  ├─ linux-x64/       mmencoder         (self-contained, ~60-90 MB, kein .NET nötig)
│  ├─ linux-arm64/     mmencoder         (self-contained, Graviton/Raspberry Pi)
│  ├─ linux-x64-dev/   mmencoder         (dev build — includes --dev, MMPROTECT_DEV_BUILD)
│  ├─ win-x64/         mmencoder.exe     (self-contained, kein .NET nötig)
│  └─ win-arm64/       mmencoder.exe     (self-contained, Surface Pro X etc.)
├─ decoder/
│  ├─ linux-x64/
│  │  ├─ mmloader.so           (release — requires signing_public_key_file)
│  │  ├─ mmloader-dev.so       (dev build — dev_mode + no signing key required)
│  │  └─ mmloader-php85.so     (PHP 8.5 release build)
│  └─ win-x64/         php_mmloader.dll
└─ release/
   └─ mmprotect-<version>.zip   (release artefacts only)
```

> **Self-contained Encoder:** Die Encoder-Binaries ab Version 0.1.0 bündeln die .NET 8-Runtime. Zielmaschinen benötigen kein .NET SDK oder Runtime-Paket. Die Binaries sind größer (~60–90 MB), aber vollständig portabel.

> **Release vs. Dev:** Release builds (`mmloader.so`, `mmencoder`) sind für Kundenverteilung gedacht. Dev builds (`mmloader-dev.so`, Encoder im Debug-Config) sind ausschließlich für interne Entwicklung — sie enthalten `MMPROTECT_DEV_BUILD`, das dev_mode aktiviert, Signing-Key-Anforderungen umgeht und weitere INI-Einträge freigibt.

---

## CI/CD (Jenkins)

Jenkinsfiles are provided in `jenkins/`:

- `Jenkinsfile` — multi-platform pipeline
- `Jenkinsfile.linux` — Linux-only build + test
- `Jenkinsfile.windows` — Windows build + test

Key environment variables expected by the pipeline:

| Variable | Description |
|----------|-------------|
| `MM_SIGNING_PRIVATE_KEY` | PEM content of ECDSA private key (Jenkins credential) |
| `MM_ENCODER_API_KEY` | API key for the staging license server |
| `MM_LICENSE_SERVER_URL` | Base URL of the staging license server |

---

## Versioning

The binary name (output) for the encoder is `mmencoder` (set via `<AssemblyName>` in `EncoderCli.csproj`).  
Loader version is set in `src/PhpDecoderLoader/php_mmloader.h` as `PHP_MMLOADER_VERSION`.  
Server version is read from the assembly at runtime (`/health` endpoint).

Update all three in sync before a release.

---

## Troubleshooting Builds

### `phpize` not found

```bash
sudo apt-get install -y php8.4-dev
```

### `libssl-dev` version mismatch

Ensure `libssl-dev` and the `openssl` command point to the same OpenSSL 3.x installation. On Ubuntu 22.04+:

```bash
apt-cache policy libssl-dev openssl
```

### `.NET` SDK version mismatch

Check `global.json` if present. The project targets `net8.0`. Use SDK 8.0.x or later.

### Loader builds but crashes at load time

Check PHP version match: `php --version` must match the PHP headers used during `phpize`. A mismatch causes an ABI error. Build with the dev package for the PHP version your production server runs.
