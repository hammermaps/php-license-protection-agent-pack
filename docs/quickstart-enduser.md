# Schnellstart: Endkunde (PHP-Extension installieren)

**Ziel:** Eine MMProtect-geschützte PHP-Anwendung in unter 10 Minuten zum Laufen bringen.

**Was Sie von Ihrem Softwarelieferanten erhalten haben sollten:**

| Datei / Information | Beschreibung |
|---|---|
| `mmloader.so` | PHP-Extension passend zu Ihrer PHP-Version (8.4 oder 8.5) |
| `signing-public.pem` | Öffentlicher Schlüssel Ihres Lieferanten |
| Geschützte PHP-Anwendung | Enthält `.mmprotect/manifest.json` und `.mmprotect/license.json` |
| License-Server-URL | z. B. `https://license.vendor.com` (oft in `license.json` enthalten) |

---

## Voraussetzungen prüfen

```bash
# PHP-Version ermitteln
php --version
# → PHP 8.4.x oder 8.5.x erforderlich

# Extension-Verzeichnis ermitteln (wird gleich benötigt)
php -r "echo ini_get('extension_dir');"
# Beispiel: /usr/lib/php/20230831
```

---

## Schritt 1 — Extension installieren

```bash
# Extension-Verzeichnis aus obigem Befehl einsetzen
EXT_DIR=$(php -r "echo ini_get('extension_dir');")

sudo cp mmloader.so "$EXT_DIR/mmloader.so"
sudo chmod 644 "$EXT_DIR/mmloader.so"
```

---

## Schritt 2 — Cache-Verzeichnis anlegen

```bash
sudo mkdir -p /var/cache/mmloader

# Web-Server-Benutzer anpassen (typisch: www-data, nginx, apache)
sudo chown www-data:www-data /var/cache/mmloader
sudo chmod 700 /var/cache/mmloader
```

---

## Schritt 3 — Öffentlichen Schlüssel ablegen

```bash
sudo mkdir -p /etc/mmloader
sudo cp signing-public.pem /etc/mmloader/signing-public.pem
sudo chmod 644 /etc/mmloader/signing-public.pem
```

---

## Schritt 4 — php.ini konfigurieren

Öffnen Sie Ihre `php.ini` (`php --ini` zeigt den Pfad) und fügen Sie hinzu:

```ini
; MMProtect Loader
extension = mmloader.so

; Öffentlicher Schlüssel Ihres Softwarelieferanten
mmloader.signing_public_key_file = /etc/mmloader/signing-public.pem

; Cache für Lizenz-Tokens (muss vom Web-Server beschreibbar sein)
mmloader.cache_dir = /var/cache/mmloader

; Timeouts in Millisekunden
mmloader.connect_timeout_ms = 3000
mmloader.request_timeout_ms = 10000

; Token-Lebensdauer und Offline-Toleranz
mmloader.lease_refresh_seconds  = 3600
mmloader.offline_grace_seconds  = 604800
```

> **OPcache:** Falls OPcache aktiv ist, muss es **vor** mmloader geladen werden.
> Benennen Sie die INI-Dateien z. B. `00-opcache.ini` und `10-mmloader.ini`.

---

## Schritt 5 — Extension-Ladevorgang prüfen

```bash
php -m | grep mmloader
# → mmloader

php -r "phpinfo();" | grep -A 5 "mmloader"
# → mmloader support => enabled
#    version => 0.1.0
```

Wenn `mmloader` nicht erscheint: PHP-Version und Extension-Verzeichnis nochmals prüfen (Schritt 1).

---

## Schritt 6 — Anwendung deployen

Entpacken Sie die geschützte Anwendung in Ihr Web-Root, zum Beispiel:

```
/var/www/ihre-anwendung/
├─ public/          ← Web-Root (index.php ist Klartext)
├─ src/             ← verschlüsselte .php-Dateien (MMENC1-Format)
├─ vendor/          ← Composer-Autoloader (Klartext)
├─ composer.json
└─ .mmprotect/
   ├─ manifest.json
   └─ license.json
```

**Sicherheit:** Das `.mmprotect/`-Verzeichnis muss nicht vom Web erreichbar sein. Sperren Sie es im Webserver:

```nginx
# nginx
location ~ /\.mmprotect {
    deny all;
}
```

```apache
# Apache
<DirectoryMatch "\.mmprotect">
    Require all denied
</DirectoryMatch>
```

---

## Schritt 7 — Ersten Start prüfen

```bash
# Webserver neu starten
sudo systemctl restart php8.4-fpm   # oder php8.5-fpm / apache2 / nginx

# Anwendung aufrufen (oder direkt per CLI testen)
php public/index.php
```

Beim ersten Aufruf kontaktiert mmloader automatisch den License Server, aktiviert die Lizenz und speichert ein Token im Cache. **Internetzugang ist beim ersten Aufruf erforderlich.**

---

## Häufige Fehler

| Fehlermeldung | Ursache | Lösung |
|---|---|---|
| `mmloader.so: cannot open shared object` | Extension nicht im richtigen Verzeichnis | `php -r "echo ini_get('extension_dir');"` → Pfad prüfen |
| `MMENC: no runtime key available` | Kein Internetzugang oder Lizenz abgelaufen | Netzwerk prüfen, Lieferant kontaktieren |
| `MMENC: signature mismatch` | Falsche `signing-public.pem` oder Datei beschädigt | Datei vom Lieferanten neu anfordern |
| `MMENC: license expired` | Lizenz abgelaufen | Verlängerung beim Softwarelieferanten |
| Extension lädt, aber `mmloader` fehlt in `php -m` | Falsche PHP-Version (Extension für 8.4, PHP ist 8.5) | Passende Extension beim Lieferanten anfordern |
| OPcache-Warnung `JIT disabled` | mmloader überschreibt `execute_ex` | Normale Warnung, kein Fehler — JIT deaktivieren oder ignorieren |

---

## Offline-Betrieb

Wenn der License Server **vorübergehend** nicht erreichbar ist, läuft die Anwendung weiter — solange das gecachte Token noch gültig ist (Standard: 7 Tage nach Ablauf). Danach werden geschützte Dateien blockiert bis der Server wieder erreichbar ist.

---

## Hilfe erhalten

Wenden Sie sich an Ihren Softwarelieferanten und nennen Sie:

```bash
php -r "phpinfo();" | grep -E "PHP Version|mmloader version"
# → PHP Version: 8.4.x
# → mmloader version: 0.1.0

# Fehlerlog (Pfad je nach Konfiguration)
tail -50 /var/log/php8.4-fpm.log
```

Vollständige Installationsreferenz: [`docs/end-user-install.md`](end-user-install.md)
