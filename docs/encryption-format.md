# MMProtect Encryption Format Reference

This document describes the binary container format (MMENC1), the key derivation scheme, and the cryptographic algorithms used by MMProtect.

---

## Container Format: MMENC1

Every protected `.php` file has the following binary layout:

```
Offset  Size      Content
------  --------  -------------------------------------------------------
0       6 bytes   Magic string: "MMENC1"  (ASCII)
6       1 byte    Line feed: 0x0A
7       8 bytes   Header length as zero-padded ASCII decimal, e.g. "00001234"
15      1 byte    Line feed: 0x0A
16      N bytes   JSON header (canonical JSON, UTF-8)
16+N    R bytes   Binary ciphertext (AES-256-GCM output, no GCM tag appended)
```

The GCM authentication tag is stored **inside the JSON header** as `"tag"` (Base64), not appended after the ciphertext. This allows the header to be parsed and verified before touching the ciphertext.

### JSON Header Fields

All fields are required unless noted as optional.

| Field | Type | Description |
|-------|------|-------------|
| `format` | string | Always `"MMENC1"` |
| `formatVersion` | int | Always `1` |
| `projectId` | string | Opaque UID from the license server |
| `customerId` | string | Opaque UID from the license server |
| `licenseId` | string | License UID the file was encoded under |
| `buildId` | string | Build UID — ties together all files in one encoder run |
| `fileId` | string | `"file_" + sha256(relativePath)[:24]` |
| `relativePath` | string | File path relative to project source root (forward slashes) |
| `pathHash` | string | `"sha256:" + sha256hex(relativePath)` |
| `plainHash` | string | `"sha256:" + sha256hex(plaintext)` |
| `cipherHash` | string | `"sha256:" + sha256hex(ciphertext)` — covers only the ciphertext bytes, not the GCM tag |
| `algorithm` | string | `"AES-256-GCM"` |
| `kdf` | string | `"HKDF-SHA256"` |
| `keyId` | string | Key UID from the license server |
| `nonce` | string | Base64 — 12-byte AES-GCM nonce |
| `tag` | string | Base64 — 16-byte AES-GCM authentication tag |
| `manifestHash` | string | SHA-256 of the project manifest (`"pending"` during encoding, updated after sign) |
| `createdAt` | string | ISO-8601 UTC timestamp of encoding time |
| `signature` | string | Base64 — ECDSA-P256 DER signature over the signing scope (see below) |

### Signature Scope

The signature covers (all as UTF-8 bytes, concatenated with `:`):

```
buildId + ":" + fileId + ":" + cipherHash
```

The private key used for signing is the vendor's ECDSA-P256 key, configured in `defaults.signing.privateKeyFile` in the encoder config. The corresponding public key is deployed to customer machines as `mmloader.signing_public_key_file`.

Without the ECDSA key configured, the encoder falls back to a SHA-256 hash of the same data as the "signature" — this is accepted by the loader only when no public key is configured in INI.

---

## Key Derivation

### Build Key

The **build key** is a 32-byte random secret generated for each encoder build:

```
buildKey = random(32 bytes)
```

It is returned from the license server's `/api/v1/encoder/builds/start` endpoint and stored encrypted in the server's database under `crypto_keys.encrypted_secret_key`.

At runtime, the mmloader requests the build key from the license server as part of the lease response (`runtimeKey` field). The server decrypts and returns it only to authorised, non-revoked licenses.

### Per-File Key (HKDF)

Each file is encrypted with a distinct key derived from the build key:

```
fileKey = HKDF-SHA256(
    IKM  = buildKey,
    salt = SHA-256("MMProtect-HKDF-v1"),   // 32-byte fixed salt
    info = buildId + ":" + fileId + ":" + pathHash,
    len  = 32 bytes
)
```

This ensures:
- Knowing one file key does not reveal the build key.
- Knowing the build key alone is not sufficient without knowing `buildId`, `fileId`, and `pathHash`.
- Replacing one encrypted file with another (different `pathHash`) cannot decrypt under the same key.

### HKDF Parameters

| Parameter | Value |
|-----------|-------|
| Hash | SHA-256 |
| Salt | `SHA-256(b"MMProtect-HKDF-v1")` = 32-byte constant |
| Info | `"{buildId}:{fileId}:{pathHash}"` — ASCII/UTF-8 |
| Output length | 32 bytes |

---

## Encryption: AES-256-GCM

| Parameter | Value |
|-----------|-------|
| Algorithm | AES-256-GCM |
| Key size | 256 bits (32 bytes, derived via HKDF) |
| Nonce size | 96 bits (12 bytes, random per file) |
| Tag size | 128 bits (16 bytes) |
| AAD | None |

The GCM tag is stored in the JSON header as the `"tag"` field (Base64). Ciphertext is stored raw (binary) after the JSON header in the MMENC1 container.

The loader:
1. Reads the header and verifies the file signature (ECDSA-P256 or SHA-256 fallback).
2. Requests a runtime lease from the license server to obtain the `runtimeKey` (= `buildKey`).
3. Derives `fileKey` using HKDF (same parameters as the encoder).
4. Decrypts with `AES-256-GCM(key=fileKey, nonce=nonce, tag=tag, ciphertext=ciphertext)`.
5. Verifies GCM tag authentication (built into EVP_DecryptFinal).
6. Passes the resulting PHP plaintext to the Zend engine.
7. Zeroes the plaintext buffer before freeing.

---

## Optional: LZ4 Compression

When `"compression": "lz4"` is set in the MMENC1 header, the plaintext PHP is compressed with LZ4-Block (HC) **before** AES-GCM encryption:

```
ciphertext = AES-256-GCM( LZ4_compress( phpPlaintext ) )
```

The compressed block format prepended to the LZ4 data:

```
Offset  Size      Content
0       4 bytes   Original (uncompressed) size, little-endian uint32
4       N bytes   LZ4 block data
```

The loader checks `header.compression`:
- Missing or `null` → no decompression (backward compatible)
- `"lz4"` → call `LZ4_decompress_safe(ciphertext+4, originalSize)` after AES-GCM decrypt

**Compression savings:** Typically 40–60 % for PHP source code. LZ4-Block (not LZ4-Frame) is used — the LZ4 decompressor is embedded in the loader (`vendor/lz4/`) without requiring `liblz4-dev` on customer machines.

The `licenseServer` field in the header (optional) embeds the base URL of the license server used for lease requests. If absent, `mmloader.license_server` from `php.ini` is used as fallback. This field is **not** part of the ECDSA signature scope — tampering can only cause a lease failure, not a key leak.

---

## Signing: ECDSA-P256

| Parameter | Value |
|-----------|-------|
| Curve | NIST P-256 (secp256r1) |
| Hash | SHA-256 |
| Signature format | DER-encoded (RFC 3279 DER sequence) |
| Key format | PEM (`-----BEGIN PRIVATE KEY-----` / `-----BEGIN PUBLIC KEY-----`) |

The .NET encoder uses `ECDsa.SignData(..., DSASignatureFormat.Rfc3279DerSequence)`.  
The C loader uses `EVP_DigestVerifyInit + EVP_DigestVerify` with `EVP_sha256()`.  
Both produce/consume the same DER wire format.

Lease signatures (server → loader) are also ECDSA-P256 over:

```
leaseId + ":" + buildId + ":" + machineFingerprint + ":" + expiresAt(ISO-8601)
```

---

## Manifest

After encoding all files, the encoder creates `.mmprotect/manifest.json`:

```json
{
  "format": "MMENC-MANIFEST-1",
  "projectId": "proj_...",
  "customerId": "cust_...",
  "licenseId": "lic_...",
  "buildId": "build_...",
  "version": "1.0.0",
  "phpMinVersion": "8.4",
  "algorithm": "AES-256-GCM",
  "kdf": "HKDF-SHA256",
  "files": [
    {
      "fileId": "file_...",
      "relativePath": "src/App/Application.php",
      "pathHash": "sha256:...",
      "plainHash": "sha256:...",
      "cipherHash": "sha256:...",
      "algorithm": "AES-256-GCM",
      "kdf": "HKDF-SHA256"
    }
  ],
  "manifestHash": "sha256:...",
  "signature": "Base64(ECDSA-P256 over manifestHash)"
}
```

`manifestHash` is the SHA-256 of the manifest JSON with `manifestHash` and `signature` set to empty strings (canonical form for signing).

The loader reads the manifest to know which files are protected and verifies that the manifest hash matches what the server has on record before granting a lease.

---

## Runtime Lease

The loader POSTs to `/api/v1/runtime/lease` with:

```json
{
  "projectId": "proj_...",
  "customerId": "cust_...",
  "licenseId": "lic_...",
  "buildId": "build_...",
  "manifestHash": "sha256:...",
  "machineFingerprint": "sha256hex(/etc/machine-id:hostname)",
  "loaderVersion": "0.1.0",
  "phpVersion": "8.4.0",
  "sapi": "fpm-fcgi",
  "nonce": "Base64(12 random bytes)",
  "hostname": "webserver01",
  "domain": "example.com",
  "publicIp": "203.0.113.42"
}
```

> `hostname`, `domain`, `publicIp` sind optional. Sie werden gegen `license.constraints` (`allowedHostnames`, `allowedDomains`, `allowedIps`) geprüft, falls das jeweilige Feld in der Lizenz gesetzt ist.

The server responds with a signed lease:

```json
{
  "format": "mmprotect-lease-v1",
  "leaseId": "lease_...",
  "projectId": "proj_...",
  "customerId": "cust_...",
  "licenseId": "lic_...",
  "buildId": "build_...",
  "keyId": "key_...",
  "runtimeKey": "Base64(buildKey)",
  "issuedAt": "2026-06-26T12:00:00Z",
  "expiresAt": "2026-06-27T12:00:00Z",
  "graceUntil": "2026-07-04T12:00:00Z",
  "features": ["base", "premium"],
  "signature": "Base64(ECDSA-P256 over leaseId:buildId:fingerprint:expiresAt)"
}
```

The loader:
1. Verifies the lease signature with the vendor's public key.
2. Checks `expiresAt > now`.
3. Caches the lease to disk at `{mmloader.cache_dir}/mmloader_{sha256(buildId)[:32]}.lease` (mode 0600).
4. Offline grace: if the server is unreachable and a cached lease exists with `graceUntil > now`, the loader uses the cached lease.

---

## Security Properties

| Property | Achieved by |
|----------|-------------|
| Ciphertext confidentiality | AES-256-GCM with per-file derived key |
| Ciphertext integrity | GCM authentication tag (prevents tampering) |
| Path binding | `pathHash` in HKDF info — a ciphertext cannot be moved to a different path |
| File identity binding | `fileId` in HKDF info |
| Build binding | `buildId` in HKDF info |
| Header authenticity | ECDSA-P256 signature over signing scope |
| Manifest authenticity | ECDSA-P256 signature over manifest hash |
| Lease authenticity | ECDSA-P256 signature from server |
| Machine binding | `machineFingerprint` in lease and lease signature |
| Forward secrecy | Not provided — build key is long-lived |
| Offline operation | Disk-cached lease within grace period |

---

## What MMProtect Does NOT Protect Against

- An attacker with **root/admin access** on the customer machine: they can extract the runtime key from process memory.
- A **modified PHP binary** that intercepts after decryption.
- A **debugger** attached to the PHP process.
- Decryption of `vendor/` — vendor directories are always stored in cleartext.
