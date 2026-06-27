# MMProtect License Server — Operator Guide

This guide is for the organisation that **runs the license server** (the software vendor or their DevOps team). It covers system requirements, installation, database setup, TLS, API key management, backup, and monitoring.

---

## System Requirements

| Component | Minimum | Recommended |
|-----------|---------|-------------|
| OS | Ubuntu 22.04 LTS | Ubuntu 24.04 LTS |
| CPU | 1 vCPU | 2+ vCPU |
| RAM | 512 MB | 2 GB |
| Disk | 2 GB | 20 GB |
| .NET | 8.0 Runtime | 8.0 Runtime |
| Database | SQLite 3.24+ | MySQL 8.0 / MariaDB 10.6+ |
| TLS | Required in production | nginx or Caddy in front |

SQLite is suitable for small deployments (< 10 000 encoded builds, single-server). Use MySQL for high-availability or large-scale deployments.

---

## Installation

### 1. Install .NET 8 Runtime

```bash
# Ubuntu
sudo apt-get install -y dotnet-runtime-8.0
```

### 2. Build or Download the Server

```bash
# Build from source
dotnet publish src/LicenseServer/LicenseServer.csproj \
    -c Release -r linux-x64 --self-contained false \
    -o /opt/mmprotect/server
```

Or use the pre-built artefact from `artifacts/server/linux-x64/`.

### 3. Create a Service User

```bash
sudo useradd --system --no-create-home --shell /sbin/nologin mmprotect
sudo mkdir -p /opt/mmprotect/server /opt/mmprotect/keys /opt/mmprotect/data
sudo chown -R mmprotect:mmprotect /opt/mmprotect
```

---

## Database Setup

### Option A — SQLite (small deployments)

No installation needed. The server creates the database file on first start.

```json
// appsettings.Production.json
{
  "DatabaseProvider": "sqlite",
  "ConnectionStrings": {
    "Sqlite": "Data Source=/opt/mmprotect/data/mm_license.db"
  }
}
```

Initialise the schema:

```bash
sqlite3 /opt/mmprotect/data/mm_license.db < database/sqlite/schema.sql
chown mmprotect:mmprotect /opt/mmprotect/data/mm_license.db
chmod 600 /opt/mmprotect/data/mm_license.db
```

### Option B — MySQL / MariaDB

```bash
mysql -u root <<SQL
CREATE DATABASE IF NOT EXISTS mm_license CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'mm_license'@'localhost' IDENTIFIED BY 'YOUR_STRONG_PASSWORD';
GRANT SELECT, INSERT, UPDATE, DELETE ON mm_license.* TO 'mm_license'@'localhost';
SQL

mysql -u root mm_license < database/mysql/schema.sql
```

```json
// appsettings.Production.json
{
  "DatabaseProvider": "mysql",
  "ConnectionStrings": {
    "MySql": "Server=localhost;Port=3306;Database=mm_license;User=mm_license;Password=YOUR_STRONG_PASSWORD;TreatTinyAsBoolean=true;"
  }
}
```

---

## Signing Key Generation

The server signs lease responses. Clients (mmloader) verify these signatures. Generate a key pair once and keep the **private key off-disk in a secrets manager** for production.

```bash
# Generate ECDSA-P256 key pair
scripts/linux/gen-signing-keys.sh /opt/mmprotect/keys

# Files created:
#   /opt/mmprotect/keys/signing-private.pem  (keep secret, 600 permissions)
#   /opt/mmprotect/keys/signing-public.pem   (distribute to customers)

chmod 600 /opt/mmprotect/keys/signing-private.pem
chown mmprotect:mmprotect /opt/mmprotect/keys/*
```

Distribute `signing-public.pem` to customers — they configure the path in `mmloader.signing_public_key_file`.

---

## Configuration

Copy `configs/server.appsettings.example.json` to `/opt/mmprotect/server/appsettings.Production.json` and fill in your values:

```json
{
  "DatabaseProvider": "sqlite",
  "ConnectionStrings": {
    "Sqlite": "Data Source=/opt/mmprotect/data/mm_license.db"
  },
  "Security": {
    "SigningPrivateKeyFile": "/opt/mmprotect/keys/signing-private.pem",
    "KeyEncryptionKey": "<32-byte hex — generated with: openssl rand -hex 32>",
    "EncoderApiKeys": [
      "your-encoder-api-key-here"
    ],
    "AdminApiKeys": [
      "your-admin-api-key-here"
    ],
    "LeaseTtlMinutes": 1440,
    "GracePeriodDays": 7
  },
  "RateLimiting": {
    "Enabled": false
  },
  "AllowedHosts": "*",
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

**Schlüssel generieren:**

```bash
# Key Encryption Key (schützt Build-Keys in der Datenbank)
openssl rand -hex 32   # → Security:KeyEncryptionKey

# Encoder API Key
openssl rand -base64 32   # → Security:EncoderApiKeys[0]

# Admin API Key
openssl rand -base64 32   # → Security:AdminApiKeys[0]
```

**Never log or commit:**
- `SigningPrivateKeyFile` contents
- `KeyEncryptionKey` value
- `EncoderApiKeys` / `AdminApiKeys` values
- `ConnectionStrings` passwords

---

## Running as a systemd Service

```ini
# /etc/systemd/system/mmprotect-server.service
[Unit]
Description=MMProtect License Server
After=network.target

[Service]
Type=notify
User=mmprotect
WorkingDirectory=/opt/mmprotect/server
ExecStart=/usr/bin/dotnet /opt/mmprotect/server/MmProtect.LicenseServer.dll
Restart=on-failure
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
# Never expose the server port directly to the internet — use TLS termination in front.

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now mmprotect-server
sudo systemctl status mmprotect-server
```

---

## TLS (nginx reverse proxy)

```nginx
server {
    listen 443 ssl;
    server_name license.example.com;

    ssl_certificate     /etc/letsencrypt/live/license.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/license.example.com/privkey.pem;

    location / {
        proxy_pass         http://127.0.0.1:5000;
        proxy_set_header   Host $host;
        proxy_set_header   X-Real-IP $remote_addr;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
    }
}
```

---

## API Key Management

- Generate unique random API keys (minimum 32 bytes, base64-encoded) for each encoder instance.
- Store in the `Security:EncoderApiKeys` array in `appsettings.Production.json`.
- Rotate keys by adding the new key, reloading the server, then removing the old key.
- **Never reuse** API keys between customers or environments.

```bash
# Generate a key
openssl rand -base64 32
```

---

## Health Check

```bash
curl -s https://license.example.com/health
# → {"status":"ok","version":"0.1.0","timeUtc":"...","database":"ok"}
```

Bei DB-Ausfall: `"database": "error"` mit HTTP 503.

---

## API Key Management (Admin API)

Der **Admin API Key** (`Security:AdminApiKeys`) erlaubt Zugriff auf alle `/api/v1/admin/`-Endpunkte:
- Lizenzen auflisten / sperren
- Builds sperren (Revocation)
- Aktivierungen verwalten
- Audit-Log abfragen
- Statistiken abrufen
- API-Clients anlegen und löschen

```bash
# Admin-Endpunkt testen
curl -s https://license.example.com/api/v1/admin/stats \
    -H "Authorization: Bearer YOUR_ADMIN_KEY" | python3 -m json.tool
```

Der Encoder API Key (`Security:EncoderApiKeys`) berechtigt nur zu `/api/v1/encoder/`-Endpunkten — nicht zum Admin-Bereich.

---

## Backup

### SQLite

```bash
# Online backup (safe while server is running)
sqlite3 /opt/mmprotect/data/mm_license.db ".backup /backup/mm_license_$(date +%Y%m%d).db"
```

### MySQL

```bash
mysqldump --single-transaction mm_license | gzip > /backup/mm_license_$(date +%Y%m%d).sql.gz
```

Schedule daily with cron and store off-site.

---

## Monitoring

- **Health endpoint**: `GET /health` — HTTP 200 with `{"status":"ok"}` confirms the server is up.
- **Logs**: `journalctl -u mmprotect-server -f` — logs include lease grants/denials (without key material).
- **Database size**: Watch for rapid growth in `runtime_leases` (prune expired leases older than 90 days).

Prometheus-compatible metrics can be added via `OpenTelemetry.Instrumentation.AspNetCore` if needed.

---

## Firewall

- Expose only port 443 (HTTPS) externally.
- The internal Kestrel port (5000) must **never** be reachable from the internet.
- Whitelist encoder source IPs if possible.

---

## Key Rotation

1. Generate a new ECDSA key pair.
2. Update `Security:SigningPrivateKeyFile` in config.
3. **Do not delete the old private key yet** — existing leases signed with it may still be cached on client machines until their TTL expires.
4. Distribute the new `signing-public.pem` to customers with adequate rollout time (e.g. 30 days).
5. Remove old key from config after TTL + grace period elapses.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| `AUTH_REQUIRED` / `AUTH_INVALID` from encoder | Wrong or missing API key | Check `Security:EncoderApiKeys` in config |
| `AUTH_INVALID` from admin API | Wrong admin key | Check `Security:AdminApiKeys` in config |
| `LEASE_DENIED` | Build or manifest hash mismatch | Re-encode the project |
| `LICENSE_NOT_YET_VALID` | License `validFrom` is in the future | Update `validFrom` or wait |
| `LICENSE_EXPIRED` | License `validUntil` passed | Update the license via upsert API |
| `LICENSE_REVOKED` | License manually revoked | Re-issue a new license |
| `ACTIVATION_LIMIT_REACHED` | Too many machines activated | Increase `maxActivations` or delete stale activations via Admin API |
| `ACTIVATION_REVOKED` | Specific activation was revoked | Machine needs re-activation or contact vendor |
| HTTP 500 on `/runtime/lease` | Database error or missing `KeyEncryptionKey` | Check `journalctl` for stack trace |
| Server startup warning about HMAC | `Security:SigningPrivateKeyFile` not set | Configure the ECDSA signing key |
| Loader: "signature mismatch" | Wrong public key deployed | Verify customer has the current `signing-public.pem` |
