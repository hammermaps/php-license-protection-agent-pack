# 06 – REST API Contract

## Base URL

```text
https://license.example.com
```

## Authentifizierung

### Encoder API

```http
Authorization: Bearer <encoder-api-key>
```

### Runtime API

Runtime Requests enthalten signierte Lizenzdaten und Machine Fingerprint. Optional kann zusätzlich ein Loader-Client-Zertifikat verwendet werden.

## Endpunkte

### GET /health

Response:

```json
{
  "status": "ok",
  "version": "0.1.0",
  "timeUtc": "2026-06-26T12:00:00Z",
  "database": "ok"
}
```

> Bei DB-Ausfall ist `"database": "error"` und der HTTP-Status 503.

### POST /api/v1/encoder/customers/upsert

Request:

```json
{
  "externalCustomerRef": "demo-kunde",
  "name": "Demo Kunde GmbH",
  "email": "demo@example.invalid",
  "notes": "Demo"
}
```

Response:

```json
{
  "customerId": "cust_01J...",
  "created": true
}
```

### POST /api/v1/encoder/projects/upsert

Request:

```json
{
  "projectKey": "mangelmelder",
  "name": "Mangelmelder",
  "phpMinVersion": "8.4",
  "description": "Projektcode"
}
```

Response:

```json
{
  "projectId": "proj_01J...",
  "created": true
}
```

### POST /api/v1/encoder/licenses/upsert

Request:

```json
{
  "customerId": "cust_01J...",
  "projectId": "proj_01J...",
  "licenseKey": "MM-DEMO-0001",
  "validFrom": "2026-01-01T00:00:00Z",
  "validUntil": "2027-01-01T00:00:00Z",
  "maxActivations": 3,
  "features": ["base"]
}
```

Response:

```json
{
  "licenseId": "lic_01J...",
  "created": true
}
```

### POST /api/v1/encoder/builds/start

Request:

```json
{
  "projectId": "proj_01J...",
  "customerId": "cust_01J...",
  "licenseId": "lic_01J...",
  "version": "1.0.0",
  "sourceRevision": "git-sha",
  "encoderVersion": "0.1.0"
}
```

Response:

```json
{
  "buildId": "build_01J...",
  "keyId": "key_01J...",
  "buildKey": "base64...",
  "manifestSalt": "base64..."
}
```

### POST /api/v1/encoder/builds/{buildId}/files

Request:

```json
{
  "files": [
    {
      "fileId": "file_01J...",
      "relativePath": "src/App/Application.php",
      "pathHash": "sha256:...",
      "plainHash": "sha256:...",
      "cipherHash": "sha256:...",
      "algorithm": "AES-256-GCM",
      "kdf": "HKDF-SHA256"
    }
  ]
}
```

Response:

```json
{
  "accepted": 1,
  "rejected": 0
}
```

### POST /api/v1/encoder/builds/{buildId}/manifest/sign

Request:

```json
{
  "manifestHash": "sha256:...",
  "fileCount": 42
}
```

Response:

```json
{
  "manifestSignature": "base64...",
  "vendorPublicKeyId": "vpub_01J...",
  "serverTimeUtc": "2026-06-26T12:00:00Z"
}
```

### POST /api/v1/runtime/lease

Request:

```json
{
  "projectId": "proj_01J...",
  "customerId": "cust_01J...",
  "licenseId": "lic_01J...",
  "buildId": "build_01J...",
  "manifestHash": "sha256:...",
  "machineFingerprint": "sha256:...",
  "loaderVersion": "0.1.0",
  "phpVersion": "8.4.0",
  "sapi": "fpm-fcgi",
  "nonce": "base64...",
  "hostname": "webserver01",
  "domain": "example.com",
  "publicIp": "203.0.113.42"
}
```

> `hostname`, `domain`, `publicIp` sind optional. Sie werden gegen `license.constraints` (`allowedHostnames`, `allowedDomains`, `allowedIps`) geprüft, falls das Feld in der Lizenz gesetzt ist.

Response:

```json
{
  "leaseId": "lease_01J...",
  "format": "mmprotect-lease-v1",
  "projectId": "proj_01J...",
  "customerId": "cust_01J...",
  "licenseId": "lic_01J...",
  "buildId": "build_01J...",
  "keyId": "key_01J...",
  "runtimeKey": "base64...",
  "issuedAt": "2026-06-26T12:00:00Z",
  "expiresAt": "2026-06-27T12:00:00Z",
  "graceUntil": "2026-07-03T12:00:00Z",
  "features": ["base", "premium"],
  "signature": "base64..."
}
```

## Fehlerformat

Alle Fehlerantworten:

```json
{
  "error": {
    "code": "LICENSE_EXPIRED",
    "message": "License is expired.",
    "traceId": "..."
  }
}
```

---

## Admin API

Basis-Pfad: `/api/v1/admin/`
Authentifizierung: `Authorization: Bearer <admin-api-key>` (aus `Security:AdminApiKeys`)

### GET /api/v1/admin/licenses

Listet Lizenzen auf. Optionaler Query-Parameter: `?status=active|revoked|suspended|expired`

Response: `{ "licenses": [...] }` — Array von `AdminLicenseDto`.

### POST /api/v1/admin/licenses/{licenseUid}/revoke

Setzt Lizenzstatus auf `revoked` und schreibt Eintrag in `revocations`.

Request: `{ "reason": "optional reason" }`
Response: `{ "revoked": true, "message": "..." }`

### POST /api/v1/admin/builds/{buildUid}/revoke

Setzt `builds.status = 'revoked'` und schreibt Eintrag in `revocations`.

Request: `{ "reason": "optional reason" }`
Response: `{ "revoked": true, "message": "..." }`

### GET /api/v1/admin/activations

Listet Aktivierungen auf. Optionaler Query-Parameter: `?licenseUid=lic_...`

Response: `{ "activations": [...] }` — Array von `AdminActivationDto`.

### POST /api/v1/admin/activations/{activationUid}/revoke

Setzt Aktivierungsstatus auf `revoked`.

Request: `{ "reason": "optional reason" }`
Response: `{ "revoked": true, "message": "..." }`

### DELETE /api/v1/admin/activations/{activationUid}

Löscht eine Aktivierung (ermöglicht Re-Aktivierung von derselben Maschine).

Response: `{ "revoked": true, "message": "..." }`

### GET /api/v1/admin/audit-log

Abfrage des Audit-Logs. Optionale Parameter: `?entityType=license&entityUid=lic_...&limit=100`

Response: `{ "events": [...] }` — Array von `AdminAuditEventDto`.

### GET /api/v1/admin/stats

Liefert aggregierte Zählerstände.

Response:
```json
{
  "licenses": 12,
  "builds": 45,
  "activations": 103,
  "leases": 8204
}
```

### GET /api/v1/admin/api-clients

Listet alle API-Clients auf.

Response: `{ "clients": [...] }` — Array mit `uid`, `name`, `isActive`, `createdAt`.

### POST /api/v1/admin/api-clients

Legt einen neuen API-Client an. Der `apiKey` wird nur einmalig in der Response zurückgegeben (danach nur noch SHA-256-Hash gespeichert).

Request: `{ "name": "CI-Pipeline" }`
Response: `{ "uid": "client_...", "name": "CI-Pipeline", "apiKey": "mmk_..." }`

### DELETE /api/v1/admin/api-clients/{clientUid}

Soft-löscht einen API-Client (`is_active = 0`). Der Eintrag bleibt in der Listantwort mit `"isActive": false`.

Response: `{ "deleted": true }`

---

## Fehlercodes

```text
AUTH_REQUIRED
AUTH_INVALID
VALIDATION_FAILED
CUSTOMER_NOT_FOUND
PROJECT_NOT_FOUND
LICENSE_NOT_FOUND
LICENSE_EXPIRED
LICENSE_NOT_YET_VALID
LICENSE_REVOKED
ACTIVATION_LIMIT_REACHED
ACTIVATION_REVOKED
BUILD_NOT_FOUND
MANIFEST_INVALID
LEASE_DENIED
RATE_LIMITED
NOT_FOUND
SERVER_ERROR
```
