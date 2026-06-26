# MMProtect License Server — Proxy & Load Balancer Setup

Dieses Dokument beschreibt den Betrieb des License Servers hinter einem Reverse-Proxy
oder Load Balancer sowie die eingebaute Brute-Force-Schutzfunktion (Rate Limiting).

---

## Inhaltsverzeichnis

1. [Warum ein Reverse Proxy?](#warum-ein-reverse-proxy)
2. [Real-IP-Erkennung (Konfiguration)](#real-ip-erkennung-konfiguration)
3. [Rate Limiting (Brute-Force-Schutz)](#rate-limiting-brute-force-schutz)
4. [nginx](#nginx)
5. [Apache (mod_proxy)](#apache-modproxy)
6. [HAProxy](#haproxy)
7. [Load Balancer mit mehreren Instanzen](#load-balancer-mit-mehreren-instanzen)
8. [Sicherheitshinweise](#sicherheitshinweise)

---

## Warum ein Reverse Proxy?

Der License Server ist eine schlanke ASP.NET Core Minimal API. Für den Produktivbetrieb
sollte er **nicht** direkt dem Internet ausgesetzt werden, sondern hinter einem
Reverse Proxy laufen, der:

- TLS terminiert (HTTPS)
- Zugriffe loggt
- Request-Größen begrenzt
- Statische Fehlerseiten ausliefert
- Optionally: Load Balancing über mehrere Instanzen

---

## Real-IP-Erkennung (Konfiguration)

Sobald ein Proxy vorgeschaltet ist, sieht der License Server als Client-IP die
**IP des Proxys**, nicht die IP des PHP-Loaders. Das betrifft:

- Rate Limiting (würde alle Requests dem Proxy zurechnen)
- Audit-Logs
- Aktivierungszähler

Der License Server wertet `X-Forwarded-For` und `X-Forwarded-Proto` nur dann aus,
wenn `ReverseProxy:Enabled = true` ist **und** die Anfrage von einer explizit
konfigurierten vertrauenswürdigen IP/Netzwerk stammt.

### `appsettings.json`

```json
"ReverseProxy": {
  "Enabled": true,
  "TrustedProxies": [
    "127.0.0.1",
    "::1"
  ],
  "TrustedNetworks": [
    "10.0.0.0/8",
    "172.16.0.0/12"
  ],
  "ForwardLimit": 1
}
```

| Feld | Typ | Beschreibung |
|---|---|---|
| `Enabled` | bool | `false` = direkter Betrieb (kein Proxy); `true` = Proxy-Modus |
| `TrustedProxies` | `string[]` | Einzelne Proxy-IPs, von denen `X-Forwarded-For` akzeptiert wird |
| `TrustedNetworks` | `string[]` | CIDR-Blöcke (z.B. `"10.0.0.0/8"`) für Proxy-Netzwerke |
| `ForwardLimit` | int | Maximale Anzahl `X-Forwarded-For`-Hops, die ausgewertet werden (1 = nur letzter Proxy) |

**Wichtig:** Trage ausschließlich die IP(s) deines Proxys ein. Wenn `TrustedProxies`
und `TrustedNetworks` leer sind und `Enabled = true`, werden **keine** IPs vertraut
und die Middleware ignoriert alle `X-Forwarded-*`-Header.

---

## Rate Limiting (Brute-Force-Schutz)

Der Endpunkt `POST /api/v1/runtime/lease` vergibt kryptografische Schlüssel. Ohne
Rate Limiting könnte ein Angreifer systematisch `buildId`/`licenseId`-Kombinationen
ausprobieren.

Der eingebaute Schutz ist ein **Fixed-Window-Limiter pro Client-IP**:

- Erlaubt `PermitLimit` Requests pro `WindowSeconds`-Sekunden
- Überschreitung → HTTP 429 mit `RATE_LIMITED` + `Retry-After`-Header
- Rate Limiting läuft **nach** ForwardedHeaders, d.h. bei korrekter Proxy-Konfiguration
  wird die Real-IP des Loaders bewertet, nicht die Proxy-IP

### Konfiguration

```json
"RateLimiting": {
  "Enabled": true,
  "LeaseEndpoint": {
    "PermitLimit": 10,
    "WindowSeconds": 60,
    "QueueLimit": 0
  }
}
```

| Feld | Standard | Beschreibung |
|---|---|---|
| `Enabled` | `true` | `false` deaktiviert den Rate Limiter komplett |
| `LeaseEndpoint:PermitLimit` | `10` | Max. Anfragen pro Fenster pro IP |
| `LeaseEndpoint:WindowSeconds` | `60` | Fensterlänge in Sekunden |
| `LeaseEndpoint:QueueLimit` | `0` | Puffergröße (0 = kein Puffer, sofort 429) |

**Richtlinien für die Werte:**

| Szenario | Empfohlene Werte |
|---|---|
| Kleines Deployment (< 50 Loader) | `PermitLimit: 10, WindowSeconds: 60` |
| Mittleres Deployment (50–500 Loader) | `PermitLimit: 30, WindowSeconds: 60` |
| Großes Deployment / Hochverfügbarkeit | `PermitLimit: 60, WindowSeconds: 30` |
| Hinter privatem LAN-Proxy | `Enabled: false` (Firewall schützt) |

**Hinweis:** Der Lease-TTL (`Security:LeaseTtlMinutes`) sollte so gewählt sein, dass
normale PHP-Loaders selten genug Leases anfordern. Mit Standard-TTL von 1440 Minuten
(24 Stunden) reicht `PermitLimit: 10 / 60s` für fast alle Deployments.

### HTTP-Fehlerantwort bei Überschreitung

```http
HTTP/1.1 429 Too Many Requests
Content-Type: application/json
Retry-After: 60

{
  "error": {
    "code": "RATE_LIMITED",
    "message": "Too many lease requests. Try again later.",
    "traceId": "..."
  }
}
```

---

## nginx

### Minimalkonfiguration (TLS-Terminierung + Proxy)

```nginx
server {
    listen 443 ssl http2;
    server_name license.example.com;

    ssl_certificate     /etc/ssl/certs/license.example.com.crt;
    ssl_certificate_key /etc/ssl/private/license.example.com.key;
    ssl_protocols       TLSv1.2 TLSv1.3;
    ssl_ciphers         HIGH:!aNULL:!MD5;

    # Maximale Request-Größe (Lease-Body ist < 1 KB)
    client_max_body_size 64k;

    location / {
        proxy_pass         http://127.0.0.1:5000;
        proxy_http_version 1.1;

        # Real-IP-Header für License Server
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_set_header   Host              $host;

        # Keine externen X-Forwarded-For-Header durchleiten
        proxy_set_header   X-Real-IP         $remote_addr;
    }
}

# HTTP → HTTPS redirect
server {
    listen 80;
    server_name license.example.com;
    return 301 https://$host$request_uri;
}
```

### `appsettings.json` für nginx

```json
"ReverseProxy": {
  "Enabled": true,
  "TrustedProxies": ["127.0.0.1"],
  "TrustedNetworks": [],
  "ForwardLimit": 1
}
```

---

## Apache (mod_proxy)

```apache
<VirtualHost *:443>
    ServerName license.example.com

    SSLEngine on
    SSLCertificateFile    /etc/ssl/certs/license.example.com.crt
    SSLCertificateKeyFile /etc/ssl/private/license.example.com.key

    ProxyPreserveHost On
    ProxyPass         / http://127.0.0.1:5000/
    ProxyPassReverse  / http://127.0.0.1:5000/

    # Real-IP-Header weiterleiten
    RequestHeader set X-Forwarded-Proto "https"

    # mod_remoteip: schreibt RemoteAddr auf echte Client-IP
    # (alternativ: mod_headers + X-Forwarded-For)
    RemoteIPHeader X-Forwarded-For
    RemoteIPInternalProxy 127.0.0.1

    # Angreifer können X-Forwarded-For nicht fälschen, wenn
    # die Verbindung nur vom lokalen Proxy kommt.
</VirtualHost>
```

Aktiviere die benötigten Module:

```bash
sudo a2enmod proxy proxy_http ssl headers remoteip
sudo systemctl reload apache2
```

---

## HAProxy

```haproxy
frontend https_in
    bind *:443 ssl crt /etc/ssl/license.pem
    mode http
    option forwardfor

    # Eingehende X-Forwarded-For-Header vom Client entfernen (Sicherheit)
    http-request del-header X-Forwarded-For
    http-request set-header X-Forwarded-For %[src]
    http-request set-header X-Forwarded-Proto https

    default_backend license_servers

backend license_servers
    mode http
    balance roundrobin
    option httpchk GET /health

    server license1 127.0.0.1:5000 check inter 10s
    server license2 127.0.0.1:5001 check inter 10s  # zweite Instanz (optional)
```

### `appsettings.json` für HAProxy

```json
"ReverseProxy": {
  "Enabled": true,
  "TrustedProxies": ["127.0.0.1"],
  "TrustedNetworks": [],
  "ForwardLimit": 1
}
```

---

## Load Balancer mit mehreren Instanzen

Mehrere License-Server-Instanzen können hinter einem Load Balancer betrieben werden,
solange sie **dieselbe Datenbank** und **denselben Signing-Key** teilen.

### Anforderungen

| Anforderung | Details |
|---|---|
| Gemeinsame Datenbank | MySQL (Produktion) — alle Instanzen zeigen auf dasselbe Schema |
| Gemeinsamer Signing-Key | `Security:SigningPrivateKeyFile` muss auf allen Instanzen identisch sein |
| Kein Session-Stickiness | Lease-Requests sind stateless; keine Sticky-Sessions nötig |
| Lease-Cache des Loaders | Der PHP-Loader cached Leases lokal — wenige Load-Balancer-Requests |

### Systemd-Unit für zwei Instanzen

```ini
# /etc/systemd/system/mmprotect-license@.service
[Unit]
Description=MMProtect License Server (Instanz %i)
After=network.target mysql.service

[Service]
Type=notify
User=mmprotect
WorkingDirectory=/opt/mmprotect/server
ExecStart=dotnet /opt/mmprotect/server/MmProtect.LicenseServer.dll
Environment=ASPNETCORE_URLS=http://127.0.0.1:%i
Environment=ASPNETCORE_ENVIRONMENT=Production
EnvironmentFile=/etc/mmprotect/license-server.env
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now mmprotect-license@5000
sudo systemctl enable --now mmprotect-license@5001
```

---

## Sicherheitshinweise

### TrustedProxies — warum wichtig?

Wenn `ReverseProxy:Enabled = true` und eine IP nicht in `TrustedProxies`/`TrustedNetworks`
steht, ignoriert der Server `X-Forwarded-For` von dieser IP. Das ist gewollt:

**Ohne diese Einschränkung** könnte ein Angreifer den Header selbst setzen:

```http
POST /api/v1/runtime/lease
X-Forwarded-For: 127.0.0.1
```

...und so das Rate Limiting mit einer gefälschten IP umgehen.

**Regel:** Trage nur die IP(s) deines tatsächlichen Proxys ein. Nicht `0.0.0.0/0`.

### Firewall

Der License Server sollte **nicht direkt** aus dem Internet erreichbar sein.
Nur der Proxy-Port (443) soll offen sein:

```bash
# ufw (Ubuntu/Debian)
sudo ufw allow 443/tcp
sudo ufw deny 5000/tcp   # License Server direkt blockieren

# firewalld (RHEL/CentOS)
firewall-cmd --permanent --add-service=https
firewall-cmd --permanent --remove-port=5000/tcp
firewall-cmd --reload
```

### Rate Limiting mit Redis (Hochverfügbarkeit)

Der eingebaute Rate Limiter ist **In-Process** — bei mehreren Instanzen zählt jede
Instanz separat. Für echte Cluster-Rate-Limits:

1. `RateLimiting:Enabled = false` in `appsettings.json` deaktivieren
2. Rate Limiting in HAProxy/nginx konfigurieren (empfohlen für Hochverfügbarkeit)

**nginx-Beispiel (1 Req/s pro IP auf dem Lease-Endpoint):**

```nginx
limit_req_zone $binary_remote_addr zone=lease:10m rate=10r/m;

location /api/v1/runtime/lease {
    limit_req zone=lease burst=5 nodelay;
    limit_req_status 429;
    proxy_pass http://127.0.0.1:5000;
    ...
}
```

### `/health`-Endpoint absichern

Der `/health`-Endpoint gibt Versions- und Zeitstempelinformationen zurück.
In Produktionsumgebungen nur für interne Load-Balancer-Checks öffnen:

```nginx
location /health {
    allow 127.0.0.1;
    allow 10.0.0.0/8;
    deny all;
    proxy_pass http://127.0.0.1:5000;
}
```
