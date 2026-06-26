# Schnellstart: Serverbetreiber

**Ziel:** License Server in unter 15 Minuten betriebsbereit haben — von Null bis zum ersten funktionierenden Encoder-Aufruf.

**Voraussetzungen:** Ubuntu 22.04/24.04, Root-Zugriff, Domain mit DNS-Eintrag (für TLS-Produktion).

---

## Schritt 1 — Abhängigkeiten installieren

```bash
sudo apt-get update
sudo apt-get install -y dotnet-runtime-8.0 nginx sqlite3 openssl curl
```

---

## Schritt 2 — Server-Binary bereitstellen

```bash
# Aus dem Repo bauen (benötigt dotnet-sdk-8.0):
dotnet publish src/LicenseServer/LicenseServer.csproj \
    -c Release -r linux-x64 --self-contained false \
    -o /opt/mmprotect/server

# Alternativ: vorgefertigtes Artefakt verwenden:
sudo cp -r artifacts/server/linux-x64/. /opt/mmprotect/server/
```

---

## Schritt 3 — Service-Benutzer und Verzeichnisse anlegen

```bash
sudo useradd --system --no-create-home --shell /sbin/nologin mmprotect
sudo mkdir -p /opt/mmprotect/{server,keys,data}
sudo chown -R mmprotect:mmprotect /opt/mmprotect
```

---

## Schritt 4 — Datenbank initialisieren

**Option A — SQLite** (empfohlen für Einstieg und kleine Deployments):

```bash
sudo sqlite3 /opt/mmprotect/data/mm_license.db \
    < database/sqlite/schema.sql
sudo chown mmprotect:mmprotect /opt/mmprotect/data/mm_license.db
```

**Option B — MySQL** (Produktion mit hohem Volumen):

```bash
mysql -u root -p < database/mysql/schema.sql
```

---

## Schritt 5 — Konfiguration anlegen

```bash
sudo tee /opt/mmprotect/server/appsettings.Production.json > /dev/null << 'EOF'
{
  "DatabaseProvider": "sqlite",
  "ConnectionStrings": {
    "Sqlite": "Data Source=/opt/mmprotect/data/mm_license.db"
  },
  "Security": {
    "LeaseTtlMinutes": 1440,
    "GracePeriodDays": 7,
    "EncoderApiKeys": [
      "HIER-EINEN-SICHEREN-API-KEY-EINTRAGEN"
    ]
  },
  "Urls": "http://localhost:5000"
}
EOF
```

> **API-Key generieren:**
> ```bash
> openssl rand -hex 32
> ```
> Den generierten Wert in `EncoderApiKeys` eintragen. Diesen Key bekommt später der Encoder.

---

## Schritt 6 — systemd-Service einrichten

```bash
sudo tee /etc/systemd/system/mmprotect.service > /dev/null << 'EOF'
[Unit]
Description=MMProtect License Server
After=network.target

[Service]
User=mmprotect
WorkingDirectory=/opt/mmprotect/server
ExecStart=/usr/bin/dotnet /opt/mmprotect/server/MmProtect.LicenseServer.dll \
    --contentRoot /opt/mmprotect/server
Environment=ASPNETCORE_ENVIRONMENT=Production
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now mmprotect
```

**Status prüfen:**

```bash
sudo systemctl status mmprotect
curl http://localhost:5000/health
# → {"status":"ok","version":"..."}
```

---

## Schritt 7 — TLS mit nginx einrichten

```bash
sudo tee /etc/nginx/sites-available/mmprotect << 'EOF'
server {
    listen 80;
    server_name license.ihre-domain.de;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    server_name license.ihre-domain.de;

    ssl_certificate     /etc/letsencrypt/live/license.ihre-domain.de/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/license.ihre-domain.de/privkey.pem;

    location / {
        proxy_pass http://localhost:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
EOF

sudo ln -s /etc/nginx/sites-available/mmprotect /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx

# TLS-Zertifikat (Let's Encrypt):
sudo apt-get install -y certbot python3-certbot-nginx
sudo certbot --nginx -d license.ihre-domain.de
```

---

## Schritt 8 — Signing-Keys generieren

```bash
sudo -u mmprotect bash scripts/linux/gen-signing-keys.sh /opt/mmprotect/keys
# → /opt/mmprotect/keys/signing-private.pem   (geheim! nie weitergeben)
# → /opt/mmprotect/keys/signing-public.pem    (an Encoder-Betreiber und Endkunden verteilen)

sudo chmod 600 /opt/mmprotect/keys/signing-private.pem
```

> `signing-public.pem` muss an jeden Endkunden ausgeliefert werden, der die geschützte Anwendung installiert.

---

## Schritt 9 — Encoder-Zugangsdaten übergeben

Geben Sie dem Encoder-Betreiber (Softwareentwickler) folgende Informationen:

| Information | Wert |
|---|---|
| License Server URL | `https://license.ihre-domain.de` |
| Encoder API-Key | Der Key aus Schritt 5 |
| Signing Private Key | `/opt/mmprotect/keys/signing-private.pem` (sicher übertragen!) |

---

## Ergebnis prüfen

```bash
# Health-Endpunkt
curl -s https://license.ihre-domain.de/health | python3 -m json.tool

# Ersten Kunden anlegen (Test)
curl -s -X POST https://license.ihre-domain.de/api/v1/encoder/customers/upsert \
    -H "Authorization: Bearer HIER-IHREN-API-KEY" \
    -H "Content-Type: application/json" \
    -d '{"externalCustomerRef":"test-001","name":"Test GmbH","email":"test@example.com"}' \
    | python3 -m json.tool
# → {"customerId":"cust_...","created":true}
```

---

## Nächste Schritte

- Backup: täglicher Cron für `/opt/mmprotect/data/mm_license.db` (SQLite) oder MySQL-Dump
- Monitoring: `/health`-Endpunkt in Uptime-Monitor einbinden
- Logs: `journalctl -u mmprotect -f`
- Vollständige Referenz: [`docs/operator-guide.md`](operator-guide.md)
