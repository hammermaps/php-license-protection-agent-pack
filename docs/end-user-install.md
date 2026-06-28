# MMProtect End-User Installation Guide

This guide is for **PHP application operators** (hosting customers) who have received an MMProtect-protected PHP application from a software vendor. It explains how to install and configure the mmloader PHP extension so the protected application runs normally.

---

## What You Need

From your software vendor you should have received:

| File | Description |
|------|-------------|
| `mmloader.so` or `php_mmloader.dll` | The PHP extension for your PHP version (8.4 or 8.5) |
| `signing-public.pem` | Vendor's ECDSA public key — used to verify encrypted files |
| Your protected PHP application | Contains `.php` files starting with `MMENC1` magic |

Your vendor must also have provided you with:
- The **license server URL** (e.g. `https://license.vendor.com`)
- Your **license key** (already embedded in `.mmprotect/license.json` in your application)

---

## Requirements

| Requirement | Minimum version |
|-------------|----------------|
| PHP | 8.4 or 8.5 (must match the extension build) |
| OpenSSL | 3.x (usually comes with PHP) |
| Internet access | Required for initial activation and periodic lease renewal |
| Writable cache directory | `/var/cache/mmloader/` or similar |

---

## Step 1: Install the Extension

### Linux (shared hosting or self-hosted)

1. Copy `mmloader.so` to your PHP extension directory:

```bash
# Find your extension directory:
php -r "echo ini_get('extension_dir');"
# Example: /usr/lib/php/20230831

sudo cp mmloader.so /usr/lib/php/20230831/mmloader.so
sudo chmod 644 /usr/lib/php/20230831/mmloader.so
```

2. Create a lease cache directory:

```bash
sudo mkdir -p /var/cache/mmloader
sudo chown www-data:www-data /var/cache/mmloader   # adjust for your web user
sudo chmod 700 /var/cache/mmloader
```

3. Place the vendor's public key in a secure location:

```bash
sudo cp signing-public.pem /etc/mmloader/signing-public.pem
sudo chmod 644 /etc/mmloader/signing-public.pem
```

### Windows (IIS / PHP-CGI)

1. Copy `php_mmloader.dll` to your PHP `ext\` directory (usually `C:\Program Files\PHP\ext\`).
2. Create a cache directory, e.g. `C:\ProgramData\mmloader\cache\`.
3. Place `signing-public.pem` at `C:\ProgramData\mmloader\signing-public.pem`.

---

## Step 2: Configure php.ini

Open your `php.ini` (find it with `php --ini`) and add:

```ini
; MMProtect loader extension
extension=mmloader.so           ; Linux
; extension=php_mmloader.dll    ; Windows — uncomment instead

; Path to the vendor's ECDSA-P256 public key (provided by your software vendor)
mmloader.signing_public_key_file = /etc/mmloader/signing-public.pem

; Enforce ECDSA signature verification (1 = required, 0 = skip — NEVER 0 in production)
mmloader.require_signature = 1

; Directory for caching lease responses (must be writable by the web server user)
mmloader.cache_dir = /var/cache/mmloader

; License server URL (provided by your software vendor)
; Typically read from .mmprotect/license.json — only set here to override globally.
; mmloader.license_server = https://license.vendor.com

; Path to the build manifest (default: <app_root>/.mmprotect/manifest.json)
; mmloader.manifest_file = /var/www/myapp/.mmprotect/manifest.json

; Path to the license file (default: <app_root>/.mmprotect/license.json)
; mmloader.license_file = /var/www/myapp/.mmprotect/license.json

; Connection and request timeouts (milliseconds)
mmloader.connect_timeout_ms = 3000
mmloader.request_timeout_ms = 10000

; Seconds to keep a valid lease cached before renewing with the server
mmloader.lease_refresh_seconds = 3600

; How long (seconds) to continue using a cached lease when the server is unreachable
mmloader.offline_grace_seconds = 604800    ; 7 days
```

**Important:** Do NOT set `mmloader.dev_mode = 1` in production. Dev mode skips the license server entirely and is only for development environments.

---

## Optional: Error Reporting

mmloader can send PHP errors from your server back to the software vendor so they can diagnose issues in their protected code. This is **disabled by default** and requires explicit opt-in.

> No source code or build keys are transmitted. Only PHP error messages, file path (relative), line number, PHP version, and SAPI type are sent.

```ini
; Enable sending PHP error batches to the license server after each request
mmloader.error_reporting = 1

; Override the reporting endpoint (default: <license_server>/api/v1/runtime/errors)
; mmloader.error_report_url =

; Maximum number of errors to collect per request (default: 20)
mmloader.error_report_max_per_request = 20

; PHP error level bitmask (default: 32767 = E_ALL)
; E_ERROR=1, E_WARNING=2, E_PARSE=4, E_NOTICE=8, E_USER_ERROR=256 etc.
; Example — only fatal errors and warnings: 3
mmloader.error_report_level = 32767
```

---

## Optional: Telemetry

mmloader can send lease-lifecycle events (when a lease was acquired or used from offline cache) to the license server. This helps the vendor monitor deployment health per license. **Disabled by default.**

> Transmitted data: event type, licenseId, buildId, PHP version, SAPI. No secrets, no source code, no build key.

```ini
; Enable sending telemetry events
mmloader.telemetry = 1

; Override the telemetry endpoint (default: <license_server>/api/v1/telemetry/loader)
; mmloader.telemetry_url =
```

---

## PHP Functions

mmloader registers the following PHP functions that you can call from protected PHP code:

### `mmprotect_has_feature(string $feature): bool`

Returns `true` if the current license includes the given feature string.

```php
if (mmprotect_has_feature('premium')) {
    // unlock premium features
}
```

### `mmprotect_license_info(): array|false`

Returns license metadata from the current lease, or `false` if no active lease exists (e.g. in dev mode without a server).

```php
$info = mmprotect_license_info();
if ($info !== false) {
    echo "License: " . $info['licenseId'];
    echo "Valid until: " . $info['validUntil'];
    echo "Server: " . $info['licenseServer'];
}
```

Returned array keys:

| Key | Type | Description |
|-----|------|-------------|
| `licenseId` | string | The license UID |
| `buildId` | string | The current build UID |
| `customerId` | string | The customer UID |
| `projectId` | string | The project UID |
| `validFrom` | string\|null | License start date (ISO-8601, UTC) |
| `validUntil` | string\|null | License expiry date (ISO-8601, UTC), or null = no expiry |
| `licenseServer` | string\|null | Effective license server URL used for this lease |
| `leaseExpiresAt` | string | When the current lease expires (ISO-8601, UTC) |
| `features` | array | Feature strings from the license (may be empty) |

---

## Step 3: Deploy Your Application

Deploy the protected PHP application to your web root as usual. The directory structure typically looks like:

```
/var/www/myapp/
├─ public/
│  └─ index.php            ← plain PHP (web root)
├─ src/
│  ├─ App/
│  │  └─ Application.php   ← MMENC1-encrypted PHP
│  └─ ...
├─ vendor/                 ← plain (Composer autoloader, unencrypted)
├─ composer.json
├─ .mmprotect/
│  ├─ manifest.json        ← build manifest (signed by vendor)
│  └─ license.json         ← your license and server details
```

The `.mmprotect/` directory must be **readable** by the PHP process but does **not** need to be web-accessible. Deny access in nginx/Apache:

```nginx
# nginx
location ~ /\.mmprotect {
    deny all;
}
```

```apache
# Apache .htaccess
<DirectoryMatch "\.mmprotect">
    Require all denied
</DirectoryMatch>
```

---

## Step 4: Verify the Extension Loads

```bash
php -m | grep mmloader
# → mmloader

php -r 'phpinfo();' | grep -A5 "mmloader"
```

You should see something like:

```
mmloader
mmloader support => enabled
version => 0.1.0
signing => ECDSA-P256
license_server => https://license.vendor.com
```

---

## Step 5: First Run (Online Activation)

On the first request to your application, mmloader will:

1. Read `.mmprotect/license.json` to find the license server URL and your license ID.
2. Read `.mmprotect/manifest.json` to get the build hash.
3. Send a lease request to the license server (your machine fingerprint is computed from `/etc/machine-id` and hostname — no personally identifiable information is sent).
4. The server validates your license and returns a signed lease containing the decryption key.
5. mmloader decrypts the PHP file in RAM and passes it to PHP.
6. The lease is cached to disk for future requests.

**Internet access is required** for the first run and periodically when the cached lease expires (configurable via `mmloader.lease_refresh_seconds`).

---

## Using OPcache

If OPcache is enabled, load it **before** mmloader. In `php.ini`:

```ini
; Load order matters: OPcache must come before mmloader
zend_extension=opcache.so        ; or zend_extension=php_opcache.dll
extension=mmloader.so
```

Or in separate `.ini` files, name them so OPcache loads first (e.g. `00-opcache.ini`, `10-mmloader.ini`).

mmloader includes an OPcache guard: when PHP reuses a cached opcode array for a protected file, mmloader verifies the runtime key is still valid before allowing execution.

---

## Offline Operation

When the license server is temporarily unreachable:

- mmloader uses the **disk-cached lease** if it is still within the `graceUntil` time set by the server (typically 7 days after the lease's `expiresAt`).
- If the cached lease's grace period has also expired, protected files will fail to execute until the server is reachable again.

To increase resilience to connectivity issues, configure:

```ini
mmloader.offline_grace_seconds = 604800   ; 7 days
```

Contact your software vendor to adjust the server-side grace period if you need longer offline operation.

---

## Troubleshooting

### "MMENC: no runtime key available"

The loader could not get a decryption key. Check:
1. Internet connectivity to the license server.
2. The lease cache directory is writable: `ls -la /var/cache/mmloader/`
3. The license has not expired: contact your vendor.
4. The machine fingerprint has not changed (e.g. after a VM migration).

### "MMENC: signature mismatch"

The file's ECDSA signature could not be verified. Check:
1. `mmloader.signing_public_key_file` points to the correct vendor-provided public key.
2. The file has not been corrupted during transfer.

### "MMENC: failed to decrypt protected file"

Decryption failed — the ciphertext may have been tampered with, the file is corrupted, or the wrong build key was used.

This message appears as a PHP `Warning` in the error log, followed by an include/require failure. Steps to resolve:

1. Verify file integrity (re-download from your vendor).
2. Check that the `.mmprotect/manifest.json` and `.mmprotect/license.json` are present and match the encrypted files.
3. If you see this for ALL protected files, the license server may be returning the wrong build key — contact your vendor with the `buildId` from `.mmprotect/manifest.json`.

### "MMENC: file uses obsolete format version"

The protected files were encoded with an old encoder that produced a format version below the minimum supported by your mmloader. Solution: ask your vendor to re-encode the application with the current encoder.

### "MMENC: file requires format version … but this loader supports up to version …"

Your mmloader is older than the format version used by the encoder. Solution: update the mmloader extension to the version provided by your vendor.

### PHP version mismatch

The extension must be built for your exact PHP version. Check:

```bash
php --version
php -m | grep mmloader
```

If mmloader does not load, the extension ABI may not match. Request the correct build from your vendor, specifying the output of `php --version`.

### Extension loads but PHP crashes

Check the PHP error log. Common causes:
- Missing `libssl.so.3` — install `libssl3` package.
- Missing `libcurl.so.4` — install `libcurl4`.

```bash
ldd /usr/lib/php/20230831/mmloader.so
```

---

## Updating the Extension

When your vendor releases a new mmloader version:

1. Stop your web server.
2. Replace the `mmloader.so` / `php_mmloader.dll` file.
3. Start your web server.

Existing lease caches are forward-compatible and do not need to be cleared.

When a new protected application version is deployed (new build from the vendor):

1. Deploy the new application files (new encrypted `.php` files and updated `.mmprotect/` directory).
2. The loader will automatically request a new lease for the new build.
3. Old lease caches are not reused — the new build has a different `buildId`.

---

## Auto-Update

If your vendor has provided the `update.php` script, you can check for and apply updates automatically:

```bash
# Check for updates (dry run — reports available version, does nothing)
php update.php

# Force re-apply even if version matches
php update.php --force
```

The script reads `.mmprotect/license.json` to authenticate with the license server, compares the available `buildId` with your current build, and either updates only the manifest or downloads and atomically deploys a new application archive (if the vendor provides a download URL).

**Atomic deployment (Linux):** When the app root is a symlink, the script extracts the new version to a parallel directory and atomically replaces the symlink — no downtime window.

> `update.php` requires PHP curl and ZipArchive extensions.

---

## Getting Help

Contact your software vendor with:
- The mmloader version: `php -r 'phpinfo();' | grep "mmloader version"`
- PHP version: `php --version`
- The error message from PHP logs
- Whether the issue is first activation or after a period of operation
