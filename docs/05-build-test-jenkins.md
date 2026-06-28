# 05 – Build, Tests und Jenkins

## Ziel

Alle drei Projekte sollen reproduzierbar gebaut und getestet werden:

```text
License Server       Windows + Linux
Encoder CLI          Windows + Linux
PHP Decoder Loader   Linux + Windows
```

## Build-Modi

| Modus | Decoder-Flag | Encoder-Modus | Dev-Code enthalten? |
|---|---|---|---|
| **Release** | `./configure --enable-mmloader` | `dotnet publish -c Release` | Nein |
| **Dev** | `./configure --enable-mmloader --enable-mmloader-dev` | `dotnet publish -c Debug` | Ja |

Im Release-Build gibt es keine `mmloader.dev_mode`/`mmloader.dev_buildkey` INI-Einträge. `mmloader.signing_public_key_file` ist Pflicht.

---

## One-Click Scripts

### Linux

```bash
# Release-Build (Standard)
scripts/linux/build-all.sh

# Dev-Builds (nur für Entwicklung und Tests)
scripts/linux/build-decoder-dev.sh   # → artifacts/decoder/linux-x64/mmloader-dev.so
scripts/linux/build-encoder-dev.sh   # → artifacts/encoder/linux-x64/mmencoder-dev

# PHP 8.5 Decoder
scripts/linux/build-decoder-php85.sh  # → artifacts/decoder/linux-x64/mmloader-php85.so

# Tests
scripts/linux/test-all.sh

# Hilfs-Scripts
scripts/linux/gen-signing-keys.sh /path/to/keys  # ECDSA-P256 Schlüsselpaar
scripts/linux/package-release.sh
scripts/linux/clean.sh
```

### Windows

```text
scripts\windows\build-all.cmd
scripts\windows\build-server.cmd
scripts\windows\build-encoder.cmd
scripts\windows\build-decoder.cmd
scripts\windows\test-all.cmd
scripts\windows\package-release.cmd
scripts\windows\clean.cmd
```

---

## Erwartete Artefakte

Der Encoder CLI wird als **self-contained single-file binary** für vier Plattformen gebaut — kein .NET auf der Zielmaschine nötig:

```text
artifacts/
├─ server/
│  ├─ linux-x64/       MmProtect.LicenseServer.dll
│  └─ win-x64/
├─ encoder/
│  ├─ linux-x64/       mmencoder         (self-contained, ~60–90 MB)
│  ├─ linux-arm64/     mmencoder         (Graviton, Raspberry Pi)
│  ├─ linux-x64-dev/   mmencoder         (Dev-Build mit --dev)
│  ├─ win-x64/         mmencoder.exe     (self-contained, kein .NET nötig)
│  └─ win-arm64/       mmencoder.exe     (Surface Pro X, Snapdragon X)
├─ decoder/
│  ├─ linux-x64/       mmloader.so, mmloader-php85.so, mmloader-dev.so (Dev)
│  └─ win-x64/         php_mmloader.dll
└─ release/
   └─ mmprotect-<version>.zip
```

---

## Linux Prerequisites

```bash
sudo apt-get update
sudo apt-get install -y \
    build-essential autoconf pkg-config \
    php8.4-dev php8.4-cli php8.4-opcache \
    libssl-dev libcurl4-openssl-dev \
    dotnet-sdk-8.0 sqlite3 curl git openssl

# Optional: PHP 8.5
sudo apt-get install -y php8.5-dev php8.5-cli php8.5-opcache
```

Voraussetzungen prüfen:

```bash
dotnet --version    # 8.0.x
php8.4 --version    # PHP 8.4.x
openssl version     # OpenSSL 3.x
```

---

## Windows Prerequisites

```text
- Visual Studio Build Tools 2022
- PHP 8.4 Devpack (Thread-Safe oder Non-Thread-Safe passend zur Zielvariante)
- PHP SDK für Windows-Builds
- .NET 8 SDK
```

---

## Test-Matrix

### .NET-Tests (automatisiert, kein externer Dienst nötig)

| Projekt | Befehl | Tests | Abdeckung |
|---|---|---|---|
| `LicenseServer.Tests` | `dotnet test src/LicenseServer.Tests/` | **44 Tests** | 33 SmokeTests (Encoder-Flow, Lease, Revocation, Constraints, Admin-API, Telemetrie) + 11 CryptoTests (KEK AES-GCM, ECDSA, HMAC-Fallback, JsonCanonical) |
| `EncoderCli.Tests` | `dotnet test src/EncoderCli.Tests/` | **90 Tests** | Glob/FileSelector, MmIgnore, Compression, Obfuscator, Optimizer, Assemble |

Alle Tests laufen gegen eine In-Memory-SQLite-Datenbank via `WebApplicationFactory`. Keine MySQL-Instanz erforderlich.

### Decoder-Tests (Shell, Weeks 1–4)

```bash
bash tests/decoder-loader/run-tests.sh           # Week 1: MMENC1-Format + AES-GCM
bash tests/decoder-loader/run-tests-week2.sh     # Week 2: HTTP-Lease (mock server)
bash tests/decoder-loader/run-tests-week3.sh     # Week 3: Security-Gates
bash tests/decoder-loader/run-tests-week4.sh     # Week 4: ECDSA-P256 + OPcache-Guard
```

### E2E-Integrationstest

```bash
bash tests/integration/run-integration-test.sh
# Ergebnis: 7 passed, 0 failed
```

### Demo-Projekt-Tests

```bash
bash tests/php-demo/run-demo-test.sh
# Ergebnis: 31/31 passed (PHP 8.5: skip wenn mmloader-php85.so fehlt)
```

### Umfassender Comprehensive Test (36 Phasen)

```bash
bash tests/comprehensive/run-comprehensive-test.sh [--ext84 PATH] [--ext85 PATH]
```

Testet End-to-End mit lokalem License Server, SQLite, ECDSA-P256-Signaturen, AES-256-KEK, Live-HTTP-Lease, OPcache, APCu, LZ4, Obfuskation, Hostname/IP/Domain-Constraints, Revocation, Rate-Limiting, Audit-Log, Admin-API, Concurrent-Execution und MMENC1-Format-Inspection.

---

## Jenkins Linux Pipeline

Datei: `jenkins/Jenkinsfile.linux`

Stages:

```text
checkout
restore
build-server
build-encoder
build-decoder
test-server         # dotnet test — 41 Tests
test-encoder        # dotnet test — 57 Tests
test-decoder        # bash tests/decoder-loader/run-tests*.sh
integration-test    # bash tests/integration/run-integration-test.sh
package
archive
```

## Jenkins Windows Pipeline

Datei: `jenkins/Jenkinsfile.windows`

Stages:

```text
checkout
restore
build-server
build-encoder
build-decoder-windows
test-server
test-encoder
test-decoder-windows
package
archive
```

## Gesamt Jenkinsfile

Datei: `jenkins/Jenkinsfile` — kann Linux- und Windows-Agents parallel verwenden.

---

## Akzeptanzkriterien CI

- Pull Request darf nur grün werden, wenn alle Tests bestehen.
- Kein Test-Log darf Keys, Passwörter oder Klartextcode enthalten.
- Artefakte werden versioniert und als Jenkins-Artefakte archiviert.
- Encoder wird für alle vier Plattformen selbstenthalten gebaut (linux-x64, linux-arm64, win-x64, win-arm64).
- Server wird für Windows und Linux veröffentlicht.
- Decoder erzeugt passende `.so` (Linux) oder `.dll` (Windows).

---

## Weitere Dokumentation

| Dokument | Thema |
|----------|-------|
| `docs/build-guide.md` | Vollständige Build-Anleitung, alle Plattformen |
| `docs/telemetry-error-reporting.md` | Telemetrie und Fehlerberichte — Opt-in-Features |
| `docs/operator-guide.md` | Serverbetrieb, Schlüsselverwaltung, Admin-API |
| `docs/end-user-install.md` | PHP-Extension-Installation (Endkunde) |
