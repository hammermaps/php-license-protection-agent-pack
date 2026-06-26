# CLAUDE.md – PHP License Protection System

## Projektübersicht

Dieses Repo enthält ein **vollständig implementiertes PHP-Code-Schutzsystem** (MMProtect). Alle drei Komponenten sind funktional und durch automatisierte Tests abgedeckt. Der End-to-End-Flow — Encoder → License Server → verschlüsselte PHP-Dateien → mmloader → Runtime-Lease → Ausführung — ist vollständig lauffähig und automatisiert getestet.

### Die drei Hauptprojekte

| Projekt | Verzeichnis | Sprache | Zweck |
|---|---|---|---|
| License Server | `src/LicenseServer/` | C# / ASP.NET Core | REST API, MySQL/SQLite, verwaltet Kunden/Lizenzen/Leases |
| Encoder CLI | `src/EncoderCli/` | C# / .NET CLI | Verschlüsselt PHP-Dateien (AES-256-GCM + ECDSA-P256), kommuniziert mit Server |
| PHP Decoder/Loader | `src/PhpDecoderLoader/` | C (Zend Extension) | Entschlüsselt MMENC1-Dateien in PHP zur Laufzeit, vollständig implementiert |

---

## Kritische Architekturentscheidungen (nie brechen)

1. **`vendor/` bleibt immer Klartext.** Composer und seine Abhängigkeiten werden nie verschlüsselt.
2. **Verschlüsselte Dateien behalten die Endung `.php`** – damit Composer, Frameworks, `require`, `include` und OPcache normal funktionieren.
3. **Der PHP Decoder/Loader ist in C implementiert**, nicht in C#. Er muss gegen die Zend/PHP-ABI gebaut werden.
4. **Build-Keys und Runtime-Keys dürfen niemals geloggt werden**, weder im Server noch im Encoder noch im Loader.
5. **Private Signing-Keys kommen nicht ins Git.**
6. **OPcache ist kein Bypass** – der Loader implementiert `execute_ex`-Guards für gecachte Opcodes.

---

## Containerformat MMENC1

Jede geschützte `.php`-Datei hat diesen binären Aufbau:

```
Offset  Größe   Inhalt
0       6       Magic: MMENC1
6       1       LF (\n)
7       8       Header-Länge als ASCII-Dezimal, zero-padded
15      1       LF (\n)
16      N       Canonical JSON Header (UTF-8)
16+N    Rest    Binary Ciphertext (AES-256-GCM)
```

Pflichtfelder: `projectId`, `customerId`, `licenseId`, `buildId`, `fileId`, `relativePath`, `pathHash`, `plainHash`, `cipherHash`, `algorithm`, `kdf`, `nonce`, `tag`, `manifestHash`, `signature`.

Optionale Felder: `compression` (`"lz4"` oder weggelassen = keine Komprimierung). Rückwärtskompatibel: fehlendes Feld bedeutet unkomprimiert.

**Signaturumfang:** `buildId + ":" + fileId + ":" + cipherHash` (ECDSA-P256 DER, SHA-256). Nicht nur den Header signieren – sonst ist der Ciphertext austauschbar.

---

## Kryptografie

| Zweck | Algorithmus |
|---|---|
| Optionale Vorverarbeitung | LZ4-Block (HC, eingebettet via `vendor/lz4`) |
| Dateiverschlüsselung | AES-256-GCM |
| Hashing | SHA-256 |
| Key Derivation | HKDF-SHA256 |
| Datei- und Manifest-Signaturen | ECDSA-P256 (DER, SHA-256) |
| Lease-Signaturen | ECDSA-P256 (DER, SHA-256) |
| Transport | HTTPS/TLS |

**Schlüsselableitung pro Datei:**
```
salt    = SHA-256("MMProtect-HKDF-v1")
fileKey = HKDF-SHA256(IKM=buildKey, salt=salt, info=buildId+":"+fileId+":"+pathHash, len=32)
```

**Canonical JSON** (für stabile Signaturen): UTF-8, sortierte Property-Namen, kein Whitespace, Zeiten UTC ISO-8601, Hashes lowercase hex.

---

## Repo-Struktur

```
repo/
├─ src/
│  ├─ LicenseServer/             ← ASP.NET Core Minimal API (MySQL + SQLite)
│  ├─ LicenseServer.Tests/       ← 5 In-Process-Integrationstests (SQLite)
│  ├─ EncoderCli/                ← .NET CLI Encoder (AES-256-GCM + ECDSA-P256)
│  ├─ EncoderCli.Tests/          ← 12 Glob/FileSelector-Tests
│  └─ PhpDecoderLoader/          ← Zend Extension (vollständig implementiert)
│     └─ vendor/
│        ├─ cjson/               ← eingebetteter JSON-Parser
│        └─ lz4/                 ← eingebetteter LZ4-Block-Decompressor
├─ tests/
│  ├─ php-demo/                  ← kleines Demo-PHP-Projekt (Klartext)
│  ├─ decoder-loader/            ← Decoder-Tests (Weeks 1–4)
│  └─ integration/               ← E2E-Test (encode→server→decode→execute)
├─ database/
│  ├─ mysql/
│  │  ├─ schema.sql              ← MySQL-Schema
│  │  └─ seed-dev.sql
│  └─ sqlite/
│     └─ schema.sql              ← SQLite-Schema (für Tests und Entwicklung)
├─ configs/
│  ├─ encoder.config.json        ← Encoder-Konfigurationsbeispiel
│  ├─ encoder.config.xml
│  ├─ decoder.mmloader.ini       ← PHP-INI für den Loader
│  └─ server.appsettings.example.json
├─ scripts/
│  ├─ linux/                     ← build-all.sh, test-all.sh, gen-signing-keys.sh, ...
│  └─ windows/                   ← build-all.cmd, test-all.cmd, ...
├─ docs/
│  ├─ 00-system-overview.md      ← Gesamtarchitektur
│  ├─ 01-agent-license-server.md
│  ├─ 02-agent-encoder-cli.md
│  ├─ 03-agent-php-decoder-loader.md
│  ├─ 04-security-crypto-format.md
│  ├─ 05-build-test-jenkins.md
│  ├─ 06-api-contract.md
│  ├─ operator-guide.md          ← Serverbetreiber-Dokumentation
│  ├─ encryption-format.md       ← Verschlüsselungsformat-Referenz
│  ├─ build-guide.md             ← Build-Anleitung für alle Komponenten
│  └─ end-user-install.md        ← Extension-Installationsanleitung für Endkunden
├─ jenkins/
│  ├─ Jenkinsfile
│  ├─ Jenkinsfile.linux
│  └─ Jenkinsfile.windows
└─ CLAUDE.md                     ← diese Datei
```

---

## Agenten-Dokumente (Pflichtlektüre vor Implementierung)

Lies **immer zuerst** `docs/00-system-overview.md`, dann nur das für deine Aufgabe relevante Dokument:

| Dokument | Inhalt |
|---|---|
| `docs/00-system-overview.md` | Gesamtarchitektur, Flows, Akzeptanzkriterien |
| `docs/01-agent-license-server.md` | License Server: API, DB-Modell, Sicherheit |
| `docs/02-agent-encoder-cli.md` | Encoder CLI: Befehle, Encoding-Ablauf, Konfiguration |
| `docs/03-agent-php-decoder-loader.md` | Decoder/Loader: Zend-Hooks, OPcache, Build |
| `docs/04-security-crypto-format.md` | Kryptografie, Container-Format, Secrets-Regeln |
| `docs/05-build-test-jenkins.md` | Build-Skripte, Jenkins, Test-Matrix |
| `docs/06-api-contract.md` | REST API: alle Endpunkte, Request/Response, Fehlercodes |

---

## Runtime-Flow (License Server ↔ Loader)

```
PHP require src/App/Application.php
  → Loader erkennt MMENC1-Magic
  → Header lesen, ECDSA-P256-Signatur prüfen
  → manifest.json + license.json lesen
  → Machine Fingerprint berechnen (/etc/machine-id + hostname)
  → POST /api/v1/runtime/lease senden
  → Server prüft Lizenz, Aktivierungen, Revocation, Ablauf
  → Server antwortet mit signierter Lease + runtimeKey (= buildKey)
  → Loader verifiziert Lease-Signatur (ECDSA-P256)
  → fileKey = HKDF(buildKey, buildId:fileId:pathHash)
  → AES-256-GCM entschlüsseln im RAM
  → wenn header.compression == "lz4": LZ4_decompress_safe() im RAM
  → PHP-Code an Zend Engine übergeben
  → RAM nullen (explicit_bzero)
  → OPcache speichert Opcodes; execute_ex-Guard schützt gecachte Ausführung
```

Der Loader cached die Lease lokal (`mmloader.cache_dir`, Modus 0600). Offline-Grace: gecachte Lease gilt bis `graceUntil`. Danach wird geschützter Code blockiert.

---

## Sicherheitsregeln für alle Agenten

**Nie ins Git einchecken:**
- Vendor Signing Private Key (`signing-private.pem`)
- Encoder API Keys
- Build Keys / Runtime Keys
- MySQL-Passwörter / Datenbankverbindungsstrings mit Credentials

**Nie in Logs schreiben:**
- `buildKey`, `runtimeKey`, `fileKey`
- Vollständige API-Keys
- Klartext-PHP-Code
- Private Key-Material

**Logs dürfen enthalten:** `licenseId` (gekürzt), `projectId`, `buildId`, `fileCount`, `success/failure`, Fehlercodes

---

## Build & Test

### Linux – Voraussetzungen

```bash
sudo apt-get install -y build-essential autoconf pkg-config \
    php8.4-dev php8.4-cli php8.4-opcache \
    libssl-dev libcurl4-openssl-dev \
    dotnet-sdk-8.0 sqlite3 curl git openssl

# Optional: PHP 8.5
sudo apt-get install -y php8.5-dev php8.5-cli php8.5-opcache
```

### Alles bauen

```bash
scripts/linux/build-all.sh
```

### .NET-Tests

```bash
dotnet test src/LicenseServer.Tests/    # 5 In-Process-Integrationstests (SQLite)
dotnet test src/EncoderCli.Tests/       # 40 Tests (Glob + MmIgnore + Compression)
```

### Decoder-Tests (Weeks 1–4)

```bash
bash tests/decoder-loader/run-tests.sh       # Week 1: Format + AES-GCM
bash tests/decoder-loader/run-tests-week2.sh # Week 2: HTTP-Lease
bash tests/decoder-loader/run-tests-week3.sh # Week 3: Security-Gates
bash tests/decoder-loader/run-tests-week4.sh # Week 4: ECDSA + OPcache-Guard
```

### Vollständiger E2E-Integrationstest

```bash
bash tests/integration/run-integration-test.sh
# Ergebnis: 7 passed, 0 failed (PHP 8.5: skip wenn mmloader-php85.so fehlt)
```

### Alles testen

```bash
scripts/linux/test-all.sh
```

### Windows

```cmd
scripts\windows\build-all.cmd
scripts\windows\test-all.cmd
```

### Signing-Keys generieren

```bash
scripts/linux/gen-signing-keys.sh /path/to/keys
# Erzeugt: signing-private.pem (geheim!), signing-public.pem (an Kunden verteilen)
```

### Artefakte nach Build

```
artifacts/
├─ server/linux-x64/         MmProtect.LicenseServer.dll
├─ server/win-x64/
├─ encoder/linux-x64/        mmencoder
├─ encoder/win-x64/          mmencoder.exe
├─ decoder/linux-x64/        mmloader.so
├─ decoder/linux-x64/        mmloader-php85.so  (wenn php8.5-dev installiert)
├─ decoder/win-x64/          php_mmloader.dll
└─ release/                  mmprotect-<version>.zip
```

---

## REST API Kurzreferenz

Basis-URL: `https://license.example.com` (konfigurierbar)

| Methode | Pfad | Auth | Zweck |
|---|---|---|---|
| GET | `/health` | – | Status prüfen |
| POST | `/api/v1/encoder/customers/upsert` | Bearer API-Key | Kunde anlegen/finden |
| POST | `/api/v1/encoder/projects/upsert` | Bearer API-Key | Projekt anlegen/finden |
| POST | `/api/v1/encoder/licenses/upsert` | Bearer API-Key | Lizenz anlegen/finden |
| POST | `/api/v1/encoder/builds/start` | Bearer API-Key | Build starten → buildKey |
| POST | `/api/v1/encoder/builds/{buildId}/files` | Bearer API-Key | Datei-Metadaten registrieren |
| POST | `/api/v1/encoder/builds/{buildId}/manifest/sign` | Bearer API-Key | Manifest signieren |
| POST | `/api/v1/runtime/lease` | Signierte Lizenzdaten | Runtime Lease anfordern |

Fehlerformat: `{ "error": { "code": "...", "message": "...", "traceId": "..." } }`

Fehlercodes: `AUTH_REQUIRED`, `AUTH_INVALID`, `LICENSE_EXPIRED`, `LICENSE_REVOKED`, `ACTIVATION_LIMIT_REACHED`, `LEASE_DENIED`, `RATE_LIMITED` – vollständige Liste in `docs/06-api-contract.md`.

---

## Demo-Projekt

`tests/php-demo/` ist ein kleines Composer-kompatibles PHP-Projekt als Encoder-Testbasis.

```bash
cd tests/php-demo
composer dump-autoload -o -a
php public/index.php   # → "MMProtect Demo: protected project code executed"
```

---

## Nicht-Ziele

- Kein absoluter Schutz gegen Root/Admin auf Kundensystemen
- Kein Schutz gegen Debugger auf Prozessspeicher oder modifizierte PHP-Engine
- Keine Verschlüsselung von `vendor/` oder Composer selbst
- Kein Speichern von Klartext-PHP auf dem License Server

---

## Skill routing

When the user's request matches an available skill, invoke it via the Skill tool. When in doubt, invoke the skill.

Key routing rules:
- Product ideas/brainstorming → invoke /office-hours
- Strategy/scope → invoke /plan-ceo-review
- Architecture → invoke /plan-eng-review
- Design system/plan review → invoke /design-consultation or /plan-design-review
- Full review pipeline → invoke /autoplan
- Bugs/errors → invoke /investigate
- QA/testing site behavior → invoke /qa or /qa-only
- Code review/diff check → invoke /review
- Visual polish → invoke /design-review
- Ship/deploy/PR → invoke /ship or /land-and-deploy
- Save progress → invoke /context-save
- Resume context → invoke /context-restore
- Author a backlog-ready spec/issue → invoke /spec

---

## Implementierungsstand (Stand 2026-06-26, zuletzt aktualisiert 2026-06-26)

### LicenseServer (`src/LicenseServer/`) — **funktional, Demo-Krypto**

**Dateien:**

| Datei | Inhalt |
|---|---|
| `Program.cs` | Alle 8 REST-Endpunkte als ASP.NET Core Minimal API; DapperDateTimeHandler; DatabaseProvider-Switch |
| `Models/Contracts.cs` | Alle Request/Response-Records |
| `Security/ApiKeyValidator.cs` | Bearer-Token-Prüfung gegen `Security:EncoderApiKeys`-Liste |
| `Security/CryptoService.cs` | **DEMO** – HMAC-SHA256 statt ECDSA-P256; Build-Key als `"demo:"+plaintext` gespeichert |
| `Data/IDbConnectionFactory.cs` | Abstraktion für MySQL und SQLite |
| `Data/MySqlConnectionFactory.cs` | MySqlConnector-Implementierung |
| `Data/SqliteConnectionFactory.cs` | Microsoft.Data.Sqlite-Implementierung |
| `Data/DbLookup.cs` | UID→DB-ID-Hilfsfunktionen mit Dapper |
| `appsettings.json` | DatabaseProvider, MySQL/SQLite ConnStr, API-Keys, Lease-TTL |
| `LicenseServer.csproj` | .NET 8, Dapper 2.1.66, MySqlConnector 2.4.0, Microsoft.Data.Sqlite 8.0.16 |

**Implementierte Endpunkte:**
- `GET /health` ✓
- `POST /api/v1/encoder/customers/upsert` ✓
- `POST /api/v1/encoder/projects/upsert` ✓
- `POST /api/v1/encoder/licenses/upsert` ✓
- `POST /api/v1/encoder/builds/start` ✓
- `POST /api/v1/encoder/builds/{buildId}/files` ✓
- `POST /api/v1/encoder/builds/{buildId}/manifest/sign` ✓
- `POST /api/v1/runtime/lease` ✓ (inkl. Lizenzstatus, Ablauf, Aktivierungszähler)

**Datenbankunterstützung:**
- MySQL (Produktion): `"DatabaseProvider": "mysql"` in `appsettings.json`
- SQLite (Entwicklung/Tests): `"DatabaseProvider": "sqlite"`, Schema in `database/sqlite/schema.sql`

**Bekannte Lücken / TODO für Produktion:**

| Problem | Priorität |
|---|---|
| `CryptoService.SignForDemoOnly` nutzt HMAC-SHA256 statt ECDSA-P256 | KRITISCH |
| `ProtectForDemoOnly` speichert Build-Key als `"demo:"+plaintext` in DB | KRITISCH |
| Keine echte Revocation-Prüfung (`revocations`-Tabelle wird nie abgefragt) | HOCH |
| Kein Audit-Log (Tabelle `audit_log` existiert im Schema, wird nie beschrieben) | HOCH |
| Kein Rate-Limiting für `/runtime/lease` | HOCH |
| `JsonCanonical.Serialize` sortiert Properties nicht (kein echter Canonical-JSON) | MITTEL |
| Aktivierungszähler-Logik: `activeCount > maxActivations` statt `>=` | NIEDRIG |

**Tests:** `LicenseServer.Tests/SmokeTests.cs` – **5 echte In-Process-Integrationstests** via `WebApplicationFactory` + SQLite (Health, Customer-Dedup, Project, voller Encoder-Flow, Runtime-Lease). Alle 5 bestehen.

---

### EncoderCli (`src/EncoderCli/`) — **vollständig funktional**

**Dateien:**

| Datei | Inhalt |
|---|---|
| `Program.cs` | Dispatcher für `validate`/`encode`/`manifest`/`clean`/`encode-dir` |
| `CliArgs.cs` | Argument-Parser inkl. `--source`, `--output`, `--mmignore`, `--compress`, `--dev`, `--dry-run` |
| `Configuration/EncoderConfig.cs` | Config-Modell (JSON + XML); `DefaultOptions.Compression` für LZ4 |
| `Configuration/EncoderConfigLoader.cs` | JSON via `System.Text.Json`, XML via `XDocument` |
| `Encoding/CryptoPrimitives.cs` | HKDF-SHA256 (Salt `SHA-256("MMProtect-HKDF-v1")`) + SHA-256-Hashing |
| `Encoding/FileSelector.cs` | Glob-Matching mit `**`-Support, include/exclude; `exclude` gilt auch für `copyPlain` |
| `Encoding/MmIgnoreRules.cs` | `.mmignore`-Parser + `MmIgnoreRuleSet.Evaluate()` (gitignore-Semantik, Cascade) |
| `Encoding/MmencContainer.cs` | AES-256-GCM Container, MMENC1-Format, LZ4-Komprimierung, ECDSA-P256-Signatur |
| `Encoding/ProjectEncoder.cs` | Vollständiger Encoding-Ablauf (Upsert→Build→Encrypt→Sign), `.mmignore`-Unterstützung |
| `Encoding/LocalDevEncoder.cs` | Dev-Modus ohne License Server: zufälliger Build-Key, `dev-buildkey.b64` |
| `Server/LicenseServerClient.cs` | HTTP-Client gegen License Server mit Bearer-Token |

**Encoding-Ablauf:** Kunde/Projekt/Lizenz upsert → Build starten → Dateien per Glob oder `.mmignore` selektieren → optional LZ4-komprimieren → AES-256-GCM verschlüsseln → ECDSA-P256 signieren → Hashes registrieren → Manifest signieren → `.mmprotect/manifest.json` + `.mmprotect/license.json` schreiben.

**LZ4-Komprimierung:** optionales `"compression": "lz4"` in `DefaultOptions` (Config) oder `--compress lz4` (CLI). Komprimiert Klartext-PHP mit LZ4-Block (HC) vor AES-GCM. Format im Ciphertext: `[4-Byte-LE-Originalgröße][LZ4-Blockdaten]`. Aktivierung spart typisch 40–60 % bei PHP-Code. Backward-kompatibel: fehlendes Feld = keine Komprimierung.

**`encode-dir`-Befehl:** Verzeichnis-basierte Verschlüsselung mit optionalem `.mmignore`-Support.
- `--dev`: kein License Server, schreibt `dev-buildkey.b64`
- `--config + --project`: Produktionsmodus mit License Server
- `--mmignore <file>`: globale `.mmignore`-Datei
- `--compress lz4|none`: LZ4-Komprimierung aktivieren/deaktivieren
- `--dry-run`: nur Plan ausgeben, nichts schreiben

**Bekannte Lücken / TODO:**

| Problem | Priorität |
|---|---|
| `ManifestHash` in per-Datei-Header bleibt `"pending"` (zweiter Schreibdurchlauf fehlt) | HOCH |
| Keine PHP-Syntax-Prüfung vor Verschlüsselung | NIEDRIG |

**Tests:** `EncoderCli.Tests/` – **40 Tests** (12 Glob/FileSelector + 22 MmIgnore + 6 Compression). Alle 40 bestehen.

---

### PhpDecoderLoader (`src/PhpDecoderLoader/`) — **vollständig implementiert**

**Dateien:**

| Datei | Inhalt |
|---|---|
| `php_mmloader.h` | Header, Versionskonstante `0.1.0` |
| `config.m4` | Linux phpize-Build-Config, linkt `-lssl -lcrypto -lcurl`; kompiliert vendor/lz4 |
| `config.w32` | Windows PHP-SDK-Build-Config (Skeleton) |
| `mmloader.c` | Vollständige Zend Extension inkl. LZ4-Dekomprimierung |
| `vendor/cjson/cJSON.{c,h}` | Eingebetteter JSON-Parser |
| `vendor/lz4/lz4_decompress.{c,h}` | Eingebetteter LZ4-Block-Decompressor (kein `liblz4-dev` nötig) |

**Implementierter Funktionsumfang:**

| Feature | Status |
|---|---|
| `zend_compile_file`-Hook | ✓ |
| MMENC1-Magic-Erkennung | ✓ |
| JSON-Header parsen (cJSON) | ✓ |
| ECDSA-P256-Signatur prüfen (OpenSSL EVP) | ✓ |
| manifest.json + license.json lesen | ✓ |
| Machine Fingerprint (`/etc/machine-id` + hostname, SHA-256) | ✓ |
| HTTPS Runtime-Lease-Request (libcurl) | ✓ |
| Lease-Signatur verifizieren (ECDSA-P256) | ✓ |
| HKDF-SHA256 per-Datei-Key ableiten | ✓ |
| AES-256-GCM entschlüsseln (OpenSSL EVP) | ✓ |
| LZ4-Dekomprimierung nach AES-GCM (wenn `compression: "lz4"`) | ✓ |
| Klartext als `zend_string` an Zend Engine übergeben | ✓ |
| RAM nach Entschlüsselung nullen (`explicit_bzero`, inkl. LZ4-Buffer) | ✓ |
| Lease-Cache lokal speichern (Modus 0600) | ✓ |
| Offline-Grace-Logik (graceUntil) | ✓ |
| `op_array` als geschützt markieren | ✓ |
| `execute_ex`-Hook / OPcache-Guard | ✓ |
| Dev-Mode (Buildkey aus Datei, kein Server) | ✓ |
| INI-Parameter (enabled, license_server, cache_dir, timeouts, …) | ✓ |

**Build:**
```bash
# PHP 8.4
cd src/PhpDecoderLoader && phpize && ./configure --enable-mmloader && make -j$(nproc)
# PHP 8.5 (benötigt php8.5-dev)
scripts/linux/build-decoder-php85.sh
```

---

### Integrationstests

| Test | Status |
|---|---|
| E2E: encode → license server → MMENC1 → mmloader (dev_mode) | ✓ |
| E2E: mmloader → live HTTP-Lease → Ausführung | ✓ |
| E2E: SQLite-Verifikation (lease in DB) | ✓ |
| E2E: OPcache + mmloader | ✓ |
| E2E: PHP 8.5 | SKIP (mmloader-php85.so nicht gebaut; `sudo apt install php8.5-dev` + `scripts/linux/build-decoder-php85.sh`) |

### Demo-Projekt-Tests (`tests/php-demo/run-demo-test.sh`)

| Test | Inhalt | Status |
|---|---|---|
| 1 | Klartext-PHP ausführen + Smoke-Test | ✓ |
| 2 | encode-dir (Dev-Modus, .mmignore) | ✓ |
| 3 | MMENC1-Magic + vendor Klartext | ✓ |
| 4 | Ausführung mit mmloader (PHP 8.4, Dev-Modus) | ✓ |
| 5 | Smoke-Test verschlüsselte Anwendung | ✓ |
| 6 | mmloader + OPcache (PHP 8.4) | ✓ |
| 7 | PHP 8.5 (optional) | SKIP |
| 8 | Dry-Run erzeugt keinen Output | ✓ |
| 9 | .mmignore Ausschluss | ✓ |
| 10 | Klartext-PHP mit aktivem mmloader | ✓ |
| 11 | LZ4-Komprimierung: encode mit `--compress lz4` | ✓ |
| 12 | LZ4: Header-Feld `"compression":"lz4"` + Rückwärtskompatibilität | ✓ |
| 13 | LZ4: Ausführung + Smoke-Test (PHP 8.4, Dev-Modus) | ✓ |
| 14 | LZ4 + OPcache (PHP 8.4) | ✓ |

**Ergebnis:** 27/27 bestanden, 1 Skip (PHP 8.5)

---

### Gesamtübersicht Reifegrad

| Komponente | Reifegrad | Offene Punkte für Produktion |
|---|---|---|
| License Server | Funktional (Demo-Krypto) | ECDSA-P256 für Lease-Signaturen, Audit-Log, Revocation, Rate-Limiting |
| Encoder CLI | Vollständig lauffähig | ManifestHash-Update nach Encoding-Durchlauf |
| PHP Decoder/Loader | Vollständig implementiert | – |
| LicenseServer.Tests | 5/5 echte Integrationstests | Mehr Fehlerpfad-Tests (abgelaufene Lizenz, Revocation) |
| EncoderCli.Tests | 40/40 (Glob + MmIgnore + Compression) | – |
| E2E-Integrationstest | 7/7 (PHP 8.5 skip) | PHP 8.5: `sudo apt install php8.5-dev` + build |
| Demo-Projekt-Tests | 27/27 (PHP 8.5 skip) | PHP 8.5: siehe oben |
