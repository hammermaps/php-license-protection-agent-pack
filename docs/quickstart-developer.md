# Schnellstart: Entwickler

**Ziel:** Lokale Entwicklungsumgebung aufsetzen, PHP-Dateien verschlüsseln und den vollständigen E2E-Flow testen — alles lokal, ohne produktiven License Server.

**Voraussetzungen:** Ubuntu 22.04/24.04 mit `sudo`-Rechten.

---

## Schritt 1 — Abhängigkeiten installieren

```bash
sudo apt-get update
sudo apt-get install -y \
    build-essential autoconf pkg-config \
    php8.4-dev php8.4-cli php8.4-opcache \
    libssl-dev libcurl4-openssl-dev \
    dotnet-sdk-8.0 \
    sqlite3 curl git openssl composer
# Hinweis: LZ4 ist als Vendor-Bibliothek eingebettet — kein liblz4-dev nötig.

# Optional: PHP 8.5-Unterstützung
sudo apt-get install -y php8.5-dev php8.5-cli php8.5-opcache
```

**Installationen prüfen:**

```bash
dotnet --version   # 8.0.x
php8.4 --version   # PHP 8.4.x
openssl version    # OpenSSL 3.x
```

---

## Schritt 2 — Repo klonen und alles bauen

```bash
git clone <repo-url> mmprotect && cd mmprotect

# Alle drei Komponenten bauen
scripts/linux/build-all.sh

# Ergebnis:
# artifacts/server/linux-x64/MmProtect.LicenseServer.dll
# artifacts/encoder/linux-x64/mmencoder
# artifacts/decoder/linux-x64/mmloader.so
```

---

## Schritt 3 — Signing-Keys generieren

```bash
scripts/linux/gen-signing-keys.sh ./keys

ls -la keys/
# signing-private.pem   ← geheim halten, nie ins Git!
# signing-public.pem    ← wird an Endkunden verteilt
```

---

## Schritt 4 — License Server lokal starten (SQLite)

```bash
# Schema anlegen
sqlite3 /tmp/mm_dev.db < database/sqlite/schema.sql

# KEK und API-Key generieren
DEV_KEK=$(openssl rand -hex 32)
DEV_API_KEY="dev-key-1234"   # für lokale Entwicklung OK

# Server starten mit CLI-Overrides (läuft im Hintergrund)
SERVER_DLL=artifacts/server/linux-x64/MmProtect.LicenseServer.dll
ASPNETCORE_URLS="http://localhost:15380" \
dotnet "$SERVER_DLL" \
    --contentRoot "$(dirname "$(realpath "$SERVER_DLL")")" \
    --DatabaseProvider sqlite \
    --ConnectionStrings:Sqlite "Data Source=/tmp/mm_dev.db" \
    --Security:SigningPrivateKeyFile "$(realpath keys/signing-private.pem)" \
    --Security:KeyEncryptionKey "$DEV_KEK" \
    --Security:EncoderApiKeys:0 "$DEV_API_KEY" \
    --Security:AdminApiKeys:0 "dev-admin-key" \
    &
SERVER_PID=$!

# Health-Check
sleep 2 && curl -s http://localhost:15380/health
# → {"status":"ok","version":"...","database":"ok"}
```

> **Reihenfolge:** Schritt 3 (Signing-Keys generieren) muss vor Schritt 4 abgeschlossen sein, da `signing-private.pem` beim Serverstart benötigt wird.

---

## Schritt 5 — Demo-Projekt vorbereiten

```bash
cd tests/php-demo
composer dump-autoload -o -a
php8.4 public/index.php
# → MMProtect Demo: protected project code executed
cd ../..
```

---

## Schritt 6 — Encoder konfigurieren

```bash
cat > /tmp/encoder-dev.json << EOF
{
  "defaults": {
    "licenseServer": {
      "baseUrl": "http://localhost:15380",
      "apiKey": "dev-key-1234"
    },
    "signing": {
      "privateKeyFile": "$(realpath keys/signing-private.pem)"
    }
  },
  "projects": [
    {
      "key": "demo",
      "name": "MMProtect Demo",
      "sourceRoot": "$(realpath tests/php-demo)",
      "outputRoot": "/tmp/mm_encoded_demo",
      "customer": {
        "externalRef": "dev-customer-001",
        "name": "Dev Customer",
        "email": "dev@example.com"
      },
      "license": {
        "licenseKey": "MM-DEV-0001",
        "validFrom": "2026-01-01T00:00:00Z",
        "validUntil": "2028-12-31T23:59:59Z",
        "maxActivations": 10
      },
      "include": ["src/**/*.php"],
      "exclude": [],
      "copyPlain": ["public/**", "vendor/**", "composer.json", "composer.lock"]
    }
  ]
}
EOF
```

---

## Schritt 7 — PHP-Dateien verschlüsseln

```bash
artifacts/encoder/linux-x64/mmencoder encode-dir \
    --source tests/php-demo \
    --output /tmp/mm_encoded_demo \
    --config /tmp/encoder-dev.json \
    --project demo

# Verschlüsselung prüfen
head -c 6 /tmp/mm_encoded_demo/src/App/Application.php
# → MMENC1

ls /tmp/mm_encoded_demo/.mmprotect/
# manifest.json  license.json
```

---

## Schritt 8 — Verschlüsselte Dateien ausführen

### 8a — Dev-Mode (kein Server, sofort):

```bash
php8.4 \
  -d extension=artifacts/decoder/linux-x64/mmloader.so \
  -d mmloader.dev_mode=1 \
  -d mmloader.dev_buildkey=/tmp/mm_encoded_demo/.mmprotect/dev-buildkey.b64 \
  /tmp/mm_encoded_demo/public/index.php
# → MMProtect Demo: protected project code executed
```

### 8b — Live-Lease (mit lokalem License Server):

```bash
php8.4 \
  -d extension=artifacts/decoder/linux-x64/mmloader.so \
  -d mmloader.signing_public_key_file=keys/signing-public.pem \
  -d mmloader.cache_dir=/tmp/mmloader_cache \
  /tmp/mm_encoded_demo/public/index.php
# → MMProtect Demo: protected project code executed
```

### 8c — Mit OPcache:

```bash
php8.4 \
  -d zend_extension=opcache.so \
  -d opcache.enable_cli=1 \
  -d extension=artifacts/decoder/linux-x64/mmloader.so \
  -d mmloader.dev_mode=1 \
  -d mmloader.dev_buildkey=/tmp/mm_encoded_demo/.mmprotect/dev-buildkey.b64 \
  /tmp/mm_encoded_demo/public/index.php
```

---

## Schritt 9 — Tests ausführen

```bash
# .NET-Tests
dotnet test src/LicenseServer.Tests/    # 41 Tests (33 SmokeTests + 8 CryptoTests)
dotnet test src/EncoderCli.Tests/       # 57 Tests (Glob + MmIgnore + Compression + Obfuscator)

# Decoder-Tests (Week 1–4)
bash tests/decoder-loader/run-tests.sh
bash tests/decoder-loader/run-tests-week2.sh
bash tests/decoder-loader/run-tests-week3.sh
bash tests/decoder-loader/run-tests-week4.sh

# E2E-Integrationstest (startet eigenen Server, räumt auf)
bash tests/integration/run-integration-test.sh
# → 7 passed, 0 failed

# Demo-Projekt (31/31 Tests)
bash tests/php-demo/run-demo-test.sh

# Umfassender Comprehensive Test (36 Phasen — benötigt mmloader.so)
bash tests/comprehensive/run-comprehensive-test.sh

# Alles auf einmal
scripts/linux/test-all.sh
```

---

## Schritt 10 — Server aufräumen

```bash
kill $SERVER_PID 2>/dev/null
rm -f /tmp/mm_dev.db /tmp/mm_dev_settings.json
rm -rf /tmp/mm_encoded_demo /tmp/mmloader_cache
```

---

## Encoder-Konfiguration im Detail

```json
{
  "defaults": {
    "licenseServer": {
      "baseUrl": "https://license.ihre-domain.de",
      "apiKey": "env:MM_ENCODER_API_KEY"        // Alternativ: aus Umgebungsvariable
    },
    "signing": {
      "privateKeyFile": "/secure/path/signing-private.pem"
    }
  },
  "projects": [
    {
      "key":        "mein-projekt",              // Eindeutiger Projektname
      "sourceRoot": "/pfad/zum/quellcode",
      "outputRoot": "/pfad/zum/output",
      "include":    ["src/**/*.php"],            // Was verschlüsselt wird
      "exclude":    [],                          // Ausnahmen (gilt auch für copyPlain!)
      "copyPlain":  ["public/**", "vendor/**", "composer.json", "composer.lock"]
    }
  ]
}
```

> **Wichtig:** `vendor/**` darf **nicht** in `exclude` stehen, wenn es unter `copyPlain` aufgeführt ist — `exclude` filtert beide Listen.

---

## Tipps für den Entwicklungsalltag

**LZ4-Komprimierung aktivieren** (spart 40–60 % bei großem PHP-Code):
```bash
# Via CLI-Flag (Dev-Mode):
artifacts/encoder/linux-x64/mmencoder encode-dir \
    --source tests/php-demo --output /tmp/encoded --dev --compress lz4

# Via Config (Produktion):
# "defaults": { "compression": "lz4", ... }
```
Der Loader erkennt das Feld `"compression": "lz4"` im MMENC1-Header automatisch und dekomprimiert transparent. Klartext-Dateien ohne Komprimierung funktionieren unverändert (rückwärtskompatibel).

**Nur neu geänderte Dateien neu verschlüsseln:** Derzeit verschlüsselt der Encoder immer alle Dateien neu. Builds sind idempotent (neuer `buildId` pro Lauf). Für schnelle Iterationen Dev-Mode nutzen.

**Klartext-PHP testen:** Alle `.php`-Dateien können vor der Verschlüsselung normal mit `php8.4 src/App/Application.php` ausgeführt werden.

**mmloader neu bauen** (nach Änderungen an `mmloader.c`):
```bash
cd src/PhpDecoderLoader
make clean
make -j$(nproc)
cp modules/mmloader.so ../../artifacts/decoder/linux-x64/mmloader.so
```

**PHP 8.5-Extension bauen:**
```bash
# Nur wenn php8.5-dev installiert ist
scripts/linux/build-decoder-php85.sh
# → artifacts/decoder/linux-x64/mmloader-php85.so
```

---

## Weiterführende Dokumentation

| Dokument | Inhalt |
|---|---|
| [`docs/encryption-format.md`](encryption-format.md) | MMENC1-Containerformat, HKDF, AES-GCM, ECDSA im Detail |
| [`docs/build-guide.md`](build-guide.md) | Vollständige Build-Anleitung, Jenkins CI/CD |
| [`docs/06-api-contract.md`](06-api-contract.md) | REST API — alle Endpunkte mit Request/Response-Beispielen |
| [`docs/04-security-crypto-format.md`](04-security-crypto-format.md) | Kryptografie-Spezifikation |
