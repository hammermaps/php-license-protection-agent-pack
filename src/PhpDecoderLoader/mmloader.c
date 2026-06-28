#ifdef HAVE_CONFIG_H
#include "config.h"
#endif

#include "php.h"
#include "php_ini.h"
#include "ext/standard/info.h"
#include "Zend/zend_compile.h"
#include "Zend/zend_extensions.h"
#include "main/SAPI.h"
#include "php_mmloader.h"
#include "mmloader_zend.h"

#include <openssl/evp.h>
#include <openssl/hmac.h>
#include <openssl/kdf.h>
#include <openssl/bio.h>
#include <openssl/buffer.h>
#include <openssl/err.h>
#include <openssl/rand.h>
#include <openssl/params.h>
#include <openssl/pem.h>
#include <curl/curl.h>
#include <string.h>
#include <stdlib.h>
#include <time.h>
#include <unistd.h>
#include <fcntl.h>
#include <limits.h>

#include "vendor/cjson/cJSON.h"
#include "vendor/lz4/lz4_decompress.h"

/* ZEND_EXT_API is not defined by the PHP Linux headers but is required by
 * zend_extensions.h for the zend_extension_entry symbol export declaration.
 * On Linux, it's equivalent to ZEND_API = visibility("default"). */
#ifndef ZEND_EXT_API
# define ZEND_EXT_API ZEND_API
#endif

/* Forward declarations */
static int    mmloader_read_json_file(const char *path, cJSON **out);
static char  *mmloader_base64_encode(const unsigned char *data, size_t len);
static int    mmloader_is_mmenc1_file(const char *filename);
static void   mmloader_mark_file_protected(const char *filename);

/* ====================================================================
 * Module globals
 * ==================================================================== */

/* Saved originals — restored in MSHUTDOWN */
static mm_compile_file_fn s_orig_compile_file = NULL;
static mm_execute_fn      s_orig_execute_ex   = NULL;
static void (*s_orig_error_cb)(int, zend_string *, const uint32_t, zend_string *) = NULL;

ZEND_BEGIN_MODULE_GLOBALS(mmloader)
    zend_bool  enabled;
    char      *license_server;
    char      *manifest_file;
    char      *license_file;
    char      *cache_dir;
    char      *protected_magic;
#ifdef MMPROTECT_DEV_BUILD
    char      *dev_buildkey;
#endif
    char      *signing_public_key_file;
    zend_long  connect_timeout_ms;
    zend_long  request_timeout_ms;
    zend_long  lease_refresh_seconds;
    zend_long  offline_grace_seconds;
    zend_bool  require_signature;
    /* Proactive refresh: trigger re-fetch when remaining TTL falls below this
     * percentage of lease_refresh_seconds. Default: 10 (= 10 %). */
    zend_long  lease_refresh_threshold_pct;
    /* Maximum accepted AES-GCM ciphertext size in MiB. Default: 256. */
    zend_long  max_file_size_mb;
#ifdef MMPROTECT_DEV_BUILD
    zend_bool  dev_mode;
    zend_bool  dev_mode_warned;
#endif
    /* Per-process feature set from last successful lease response */
    HashTable *lease_features;  /* persistent; NULL = no features */
    /* Per-process lease cache (in-RAM) */
    unsigned char cached_runtime_key[32];
    zend_bool     has_cached_key;
    time_t        cached_lease_expires;
    time_t        cached_lease_grace;
    char         *cached_build_id;        /* persistent: pestrdup-owned */
    /* Cached license metadata from last successful lease */
    char         *cached_license_id;      /* persistent: pestrdup-owned */
    char         *cached_customer_id;     /* persistent: pestrdup-owned */
    char         *cached_project_id;      /* persistent: pestrdup-owned */
    char         *cached_valid_from;      /* ISO-8601 string, persistent */
    char         *cached_valid_until;     /* ISO-8601 string, persistent */
    char         *cached_effective_server;/* URL used for the lease, persistent */
    CURL         *curl_handle;
    /* Week 3: per-process protected-files set */
    HashTable    *protected_files;   /* persistent */
    /* Week 4: per-process MMENC1 magic check cache */
    HashTable    *file_magic_cache;  /* persistent: filename → 1/0 */
    /* Error reporting */
    zend_bool     error_reporting_enabled;
    char         *error_report_url;
    zend_long     error_report_max;
    zend_long     error_report_level_mask;
    cJSON        *error_batch;        /* per-request JSON array, NULL when disabled */
    int           error_batch_count;  /* entries collected this request */
    /* Telemetry — optional lease-lifecycle events to the license server */
    zend_bool     telemetry_enabled;
    char         *telemetry_url;      /* override; NULL = license_server + /api/v1/telemetry/loader */
ZEND_END_MODULE_GLOBALS(mmloader)

ZEND_DECLARE_MODULE_GLOBALS(mmloader)

#define MMLOADER_G(v) ZEND_MODULE_GLOBALS_ACCESSOR(mmloader, v)

/* Pre-computed static secrets (initialised in MINIT) */
static unsigned char s_hkdf_salt[32];
#ifdef MMPROTECT_DEV_BUILD
static unsigned char s_demo_signing_key[32];
#endif
static char          s_machine_fingerprint[65];

/* Week 4: ECDSA-P256 public key (NULL = use SHA-256/HMAC demo fallback) */
static EVP_PKEY *s_signing_public_key = NULL;

/* Week 4: prevent double-MINIT when loaded as both extension= and zend_extension= */
static zend_bool s_minit_done = 0;

/* ====================================================================
 * INI
 * ==================================================================== */

PHP_INI_BEGIN()
    STD_PHP_INI_BOOLEAN("mmloader.enabled", "1", PHP_INI_SYSTEM,
        OnUpdateBool, enabled, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_ENTRY("mmloader.license_server", "", PHP_INI_SYSTEM,
        OnUpdateString, license_server, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_ENTRY("mmloader.manifest_file", ".mmprotect/manifest.json", PHP_INI_SYSTEM,
        OnUpdateString, manifest_file, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_ENTRY("mmloader.license_file", ".mmprotect/license.json", PHP_INI_SYSTEM,
        OnUpdateString, license_file, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_ENTRY("mmloader.cache_dir", "/var/cache/mmloader", PHP_INI_SYSTEM,
        OnUpdateString, cache_dir, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_ENTRY("mmloader.protected_magic", "MMENC1", PHP_INI_SYSTEM,
        OnUpdateString, protected_magic, zend_mmloader_globals, mmloader_globals)
#ifdef MMPROTECT_DEV_BUILD
    STD_PHP_INI_ENTRY("mmloader.dev_buildkey", "", PHP_INI_SYSTEM,
        OnUpdateString, dev_buildkey, zend_mmloader_globals, mmloader_globals)
#endif
    STD_PHP_INI_ENTRY("mmloader.signing_public_key_file", "", PHP_INI_SYSTEM,
        OnUpdateString, signing_public_key_file, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_ENTRY("mmloader.connect_timeout_ms", "3000", PHP_INI_SYSTEM,
        OnUpdateLong, connect_timeout_ms, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_ENTRY("mmloader.request_timeout_ms", "5000", PHP_INI_SYSTEM,
        OnUpdateLong, request_timeout_ms, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_ENTRY("mmloader.lease_refresh_seconds", "3600", PHP_INI_SYSTEM,
        OnUpdateLong, lease_refresh_seconds, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_ENTRY("mmloader.offline_grace_seconds", "604800", PHP_INI_SYSTEM,
        OnUpdateLong, offline_grace_seconds, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_BOOLEAN("mmloader.require_signature", "1", PHP_INI_SYSTEM,
        OnUpdateBool, require_signature, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_ENTRY("mmloader.lease_refresh_threshold_pct", "10", PHP_INI_SYSTEM,
        OnUpdateLong, lease_refresh_threshold_pct, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_ENTRY("mmloader.max_file_size_mb", "256", PHP_INI_SYSTEM,
        OnUpdateLong, max_file_size_mb, zend_mmloader_globals, mmloader_globals)
#ifdef MMPROTECT_DEV_BUILD
    STD_PHP_INI_BOOLEAN("mmloader.dev_mode", "0", PHP_INI_SYSTEM,
        OnUpdateBool, dev_mode, zend_mmloader_globals, mmloader_globals)
#endif
    /* Error reporting — optional telemetry back to the license server */
    STD_PHP_INI_BOOLEAN("mmloader.error_reporting", "0", PHP_INI_SYSTEM,
        OnUpdateBool, error_reporting_enabled, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_ENTRY("mmloader.error_report_url", "", PHP_INI_SYSTEM,
        OnUpdateString, error_report_url, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_ENTRY("mmloader.error_report_max_per_request", "20", PHP_INI_SYSTEM,
        OnUpdateLong, error_report_max, zend_mmloader_globals, mmloader_globals)
    /* Bitmask of PHP error levels to forward (default: E_ALL = 32767).
     * E_ERROR=1, E_WARNING=2, E_NOTICE=8, E_USER_ERROR=256, E_USER_WARNING=512 etc. */
    STD_PHP_INI_ENTRY("mmloader.error_report_level", "32767", PHP_INI_SYSTEM,
        OnUpdateLong, error_report_level_mask, zend_mmloader_globals, mmloader_globals)
    /* Telemetry: optional lease-lifecycle events (lease_acquired, lease_offline_grace) */
    STD_PHP_INI_BOOLEAN("mmloader.telemetry", "0", PHP_INI_SYSTEM,
        OnUpdateBool, telemetry_enabled, zend_mmloader_globals, mmloader_globals)
    STD_PHP_INI_ENTRY("mmloader.telemetry_url", "", PHP_INI_SYSTEM,
        OnUpdateString, telemetry_url, zend_mmloader_globals, mmloader_globals)
PHP_INI_END()

static void php_mmloader_init_globals(zend_mmloader_globals *g)
{
    g->enabled              = 1;
    g->license_server       = NULL;
    g->manifest_file        = NULL;
    g->license_file         = NULL;
    g->cache_dir            = NULL;
    g->protected_magic      = NULL;
#ifdef MMPROTECT_DEV_BUILD
    g->dev_buildkey         = NULL;
#endif
    g->signing_public_key_file = NULL;
    g->connect_timeout_ms   = 3000;
    g->request_timeout_ms   = 5000;
    g->lease_refresh_seconds  = 3600;
    g->offline_grace_seconds  = 604800;
    g->require_signature              = 1;
    g->lease_refresh_threshold_pct    = 10;
    g->max_file_size_mb               = 256;
#ifdef MMPROTECT_DEV_BUILD
    g->dev_mode             = 0;
    g->dev_mode_warned      = 0;
#endif
    g->lease_features       = NULL;
    memset(g->cached_runtime_key, 0, sizeof(g->cached_runtime_key));
    g->has_cached_key         = 0;
    g->cached_lease_expires   = 0;
    g->cached_lease_grace     = 0;
    g->cached_build_id        = NULL;
    g->cached_license_id      = NULL;
    g->cached_customer_id     = NULL;
    g->cached_project_id      = NULL;
    g->cached_valid_from      = NULL;
    g->cached_valid_until     = NULL;
    g->cached_effective_server = NULL;
    g->curl_handle            = NULL;
    g->protected_files        = NULL;
    g->file_magic_cache       = NULL;
    g->error_reporting_enabled = 0;
    g->error_report_url       = NULL;
    g->error_report_max       = 20;
    g->error_report_level_mask = 32767;
    g->error_batch            = NULL;
    g->error_batch_count      = 0;
    g->telemetry_enabled      = 0;
    g->telemetry_url          = NULL;
}

/* Called by the ZTS thread-local storage subsystem when a thread exits,
 * and manually in MSHUTDOWN for NTS (where ZEND_INIT_MODULE_GLOBALS ignores
 * the dtor argument).  Releases per-thread / per-process resources that
 * are stored inside the module globals struct. */
static void php_mmloader_shutdown_globals(zend_mmloader_globals *g)
{
    if (g->curl_handle) {
        curl_easy_cleanup(g->curl_handle);
        g->curl_handle = NULL;
    }
    if (g->cached_build_id)         { pefree(g->cached_build_id, 1);         g->cached_build_id = NULL; }
    if (g->cached_license_id)       { pefree(g->cached_license_id, 1);       g->cached_license_id = NULL; }
    if (g->cached_customer_id)      { pefree(g->cached_customer_id, 1);      g->cached_customer_id = NULL; }
    if (g->cached_project_id)       { pefree(g->cached_project_id, 1);       g->cached_project_id = NULL; }
    if (g->cached_valid_from)       { pefree(g->cached_valid_from, 1);       g->cached_valid_from = NULL; }
    if (g->cached_valid_until)      { pefree(g->cached_valid_until, 1);      g->cached_valid_until = NULL; }
    if (g->cached_effective_server) { pefree(g->cached_effective_server, 1); g->cached_effective_server = NULL; }
    ZEND_SECURE_ZERO(g->cached_runtime_key, sizeof(g->cached_runtime_key));
    g->has_cached_key = 0;
    if (g->error_batch) { cJSON_Delete(g->error_batch); g->error_batch = NULL; }
    mm_protected_destroy(&g->protected_files);
    mm_magic_cache_destroy(&g->file_magic_cache);
    mm_features_destroy(&g->lease_features);
}

/* ====================================================================
 * Crypto helpers
 * ==================================================================== */

static int mmloader_base64_decode(const char *input, size_t input_len,
                                   unsigned char *output, size_t *output_len)
{
    BIO *b64 = BIO_new(BIO_f_base64());
    if (!b64) return 0;
    BIO *mem = BIO_new_mem_buf(input, (int)input_len);
    if (!mem) { BIO_free(b64); return 0; }
    BIO_set_flags(b64, BIO_FLAGS_BASE64_NO_NL);
    BIO_push(b64, mem);
    int n = BIO_read(b64, output, (int)input_len);
    BIO_free_all(b64);
    if (n <= 0) return 0;
    *output_len = (size_t)n;
    return 1;
}

static char *mmloader_base64_encode(const unsigned char *data, size_t len)
{
    BIO *mem = BIO_new(BIO_s_mem());
    if (!mem) return NULL;
    BIO *b64 = BIO_new(BIO_f_base64());
    if (!b64) { BIO_free(mem); return NULL; }
    BIO_set_flags(b64, BIO_FLAGS_BASE64_NO_NL);
    BIO_push(b64, mem);
    BIO_write(b64, data, (int)len);
    BIO_flush(b64);

    BUF_MEM *bptr;
    BIO_get_mem_ptr(b64, &bptr);
    char *out = emalloc(bptr->length + 1);
    memcpy(out, bptr->data, bptr->length);
    out[bptr->length] = '\0';
    BIO_free_all(b64);
    return out;
}

static int mmloader_hkdf(const unsigned char *ikm, size_t ikm_len,
                          const char *info,         size_t info_len,
                          unsigned char *out,        size_t out_len)
{
    int ok = 0;
    EVP_KDF     *kdf  = EVP_KDF_fetch(NULL, "HKDF", NULL);
    if (!kdf) return 0;
    EVP_KDF_CTX *kctx = EVP_KDF_CTX_new(kdf);
    EVP_KDF_free(kdf);
    if (!kctx) return 0;

    char digest[] = "SHA-256";
    OSSL_PARAM params[] = {
        OSSL_PARAM_construct_utf8_string("digest",  digest, 0),
        OSSL_PARAM_construct_octet_string("key",    (void *)ikm,        ikm_len),
        OSSL_PARAM_construct_octet_string("salt",   (void *)s_hkdf_salt, sizeof(s_hkdf_salt)),
        OSSL_PARAM_construct_octet_string("info",   (void *)info,       info_len),
        OSSL_PARAM_END
    };
    if (EVP_KDF_derive(kctx, out, out_len, params) > 0) ok = 1;
    EVP_KDF_CTX_free(kctx);
    return ok;
}

static int mmloader_aes256gcm_decrypt(
    const unsigned char *key,
    const unsigned char *nonce,      size_t nonce_len,
    const unsigned char *ciphertext, size_t ct_len,
    const unsigned char *tag,        size_t tag_len,
    unsigned char       *plaintext,  size_t *pt_len)
{
    int ok = 0, len = 0, final_len = 0;
    EVP_CIPHER_CTX *ctx = EVP_CIPHER_CTX_new();
    if (!ctx) return 0;
    if (!EVP_DecryptInit_ex(ctx, EVP_aes_256_gcm(), NULL, NULL, NULL))            goto done;
    if (!EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_IVLEN, (int)nonce_len, NULL)) goto done;
    if (!EVP_DecryptInit_ex(ctx, NULL, NULL, key, nonce))                         goto done;
    if (!EVP_DecryptUpdate(ctx, plaintext, &len, ciphertext, (int)ct_len))        goto done;
    if (!EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_TAG, (int)tag_len, (void *)tag)) goto done;
    if (EVP_DecryptFinal_ex(ctx, plaintext + len, &final_len) <= 0)               goto done;
    *pt_len = (size_t)(len + final_len);
    ok = 1;
done:
    EVP_CIPHER_CTX_free(ctx);
    return ok;
}

#ifdef MMPROTECT_DEV_BUILD
static int mmloader_hmac_sha256_demo(const char *data, size_t data_len,
                                      unsigned char *hmac_out)
{
    unsigned int len = 32;
    return HMAC(EVP_sha256(),
                s_demo_signing_key, sizeof(s_demo_signing_key),
                (const unsigned char *)data, data_len,
                hmac_out, &len) != NULL;
}
#endif /* MMPROTECT_DEV_BUILD */

/* ====================================================================
 * Week 4: ECDSA-P256 signature verification
 * ==================================================================== */

/*
 * Verify a DER-encoded ECDSA-P256 (or Ed25519) signature produced by
 * ECDsa.SignData(..., DSASignatureFormat.Rfc3279DerSequence) on the .NET side.
 * pkey must already be loaded (e.g. PEM_read_PUBKEY).
 * Returns 1 on success, 0 on failure.
 */
static int mmloader_ecdsa_verify(EVP_PKEY *pkey,
                                  const unsigned char *msg,  size_t msg_len,
                                  const char          *sig_b64)
{
    unsigned char sig[256]; /* DER ECDSA-P256 signature: at most ~72 bytes */
    size_t sig_len = 0;
    if (!mmloader_base64_decode(sig_b64, strlen(sig_b64), sig, &sig_len)) return 0;

    EVP_MD_CTX *ctx = EVP_MD_CTX_new();
    if (!ctx) return 0;

    int ok = 0;
    if (EVP_DigestVerifyInit(ctx, NULL, EVP_sha256(), NULL, pkey) == 1 &&
        EVP_DigestVerify(ctx, sig, sig_len, msg, msg_len) == 1) {
        ok = 1;
    }

    EVP_MD_CTX_free(ctx);
    return ok;
}

/* ====================================================================
 * Week 3: Machine fingerprint
 * ==================================================================== */

static void mmloader_compute_machine_fingerprint(void)
{
    char machine_id[256] = {0};
    FILE *fp = fopen("/etc/machine-id", "r");
    if (fp) {
        if (!fgets(machine_id, sizeof(machine_id), fp)) machine_id[0] = '\0';
        fclose(fp);
        size_t n = strlen(machine_id);
        while (n > 0 && (machine_id[n-1] == '\n' || machine_id[n-1] == '\r'
                         || machine_id[n-1] == ' '))
            machine_id[--n] = '\0';
    }

    char hostname[256] = {0};
    gethostname(hostname, sizeof(hostname) - 1);

    char combined[512] = {0};
    snprintf(combined, sizeof(combined), "%s:%s", machine_id, hostname);

    unsigned char hash[32];
    unsigned int  hash_len = 0;
    EVP_Digest(combined, strlen(combined), hash, &hash_len, EVP_sha256(), NULL);

    for (int i = 0; i < 32; i++)
        snprintf(s_machine_fingerprint + i * 2, 3, "%02x", hash[i]);
}

/* ====================================================================
 * Week 3+4: File header signature verification
 * ==================================================================== */

/*
 * Verify the per-file signature from the MMENC1 header.
 *
 * With ECDSA key configured (Week 4):
 *   Encoder signs: ECDSA-P256(DER, SHA-256("{buildId}:{fileId}:{cipherHash}"))
 *   Verification is hard (blocking on mismatch when require_signature=1).
 *
 * Without key (Week 1-3 demo fallback):
 *   Encoder writes: Base64(SHA-256("{buildId}:{fileId}:{cipherHash}"))
 *   Same blocking behaviour.
 */
static int mmloader_verify_file_signature(cJSON *root, const char *filename)
{
    if (!MMLOADER_G(require_signature)) return 1;

    cJSON *j_sig      = cJSON_GetObjectItemCaseSensitive(root, "signature");
    cJSON *j_buildId  = cJSON_GetObjectItemCaseSensitive(root, "buildId");
    cJSON *j_fileId   = cJSON_GetObjectItemCaseSensitive(root, "fileId");
    cJSON *j_cipherHash = cJSON_GetObjectItemCaseSensitive(root, "cipherHash");

    if (!cJSON_IsString(j_sig) || !cJSON_IsString(j_buildId)
        || !cJSON_IsString(j_fileId) || !cJSON_IsString(j_cipherHash))
        return 0;

#ifdef MMPROTECT_DEV_BUILD
    /* Skip legacy test placeholder (dev build only) */
    if (strncmp(j_sig->valuestring, "dev-", 4) == 0
        || strncmp(j_sig->valuestring, "sha256-placeholder", 18) == 0) {
        php_error_docref(NULL, E_NOTICE,
            "MMENC: skipping placeholder signature in %s (dev build)", filename);
        return 1;
    }
#endif

    size_t data_len = strlen(j_buildId->valuestring) + 1 +
                      strlen(j_fileId->valuestring)  + 1 +
                      strlen(j_cipherHash->valuestring);
    char *data = emalloc(data_len + 1);
    snprintf(data, data_len + 1, "%s:%s:%s",
             j_buildId->valuestring, j_fileId->valuestring, j_cipherHash->valuestring);

    int ok;
    if (s_signing_public_key) {
        /* ECDSA-P256 verification */
        ok = mmloader_ecdsa_verify(s_signing_public_key,
                                    (const unsigned char *)data, data_len,
                                    j_sig->valuestring);
#ifdef MMPROTECT_DEV_BUILD
    } else {
        /* Dev fallback: SHA-256 hash comparison */
        unsigned char hash[32];
        unsigned int  hash_len = 0;
        EVP_Digest(data, data_len, hash, &hash_len, EVP_sha256(), NULL);
        char *expected_b64 = mmloader_base64_encode(hash, 32);
        ok = (expected_b64 && strcmp(expected_b64, j_sig->valuestring) == 0);
        if (expected_b64) efree(expected_b64);
#else
    } else {
        /* Release: signing key is mandatory — hard error */
        ZEND_SECURE_ZERO(data, data_len);
        efree(data);
        php_error_docref(NULL, E_ERROR,
            "MMENC: no signing public key configured — file signature cannot be "
            "verified (mmloader.signing_public_key_file required in release build)");
        return 0;
#endif
    }

    ZEND_SECURE_ZERO(data, data_len);
    efree(data);

    if (!ok) {
        php_error_docref(NULL, E_WARNING,
            "MMENC: file signature mismatch in %s "
            "(corrupted file or wrong encoder version)", filename);
    }
    return ok;
}

/* ====================================================================
 * Week 3+4: Lease signature verification
 * ==================================================================== */

/*
 * Verify the lease signature from the server response.
 *
 * With ECDSA key configured (Week 4):
 *   Server signs: ECDSA-P256(DER, SHA-256("{leaseId}:{buildId}:{machineFingerprint}:{expiresAt_O}"))
 *   Mismatch is a hard error (blocks decryption).
 *
 * Without key (Week 1-3 demo fallback):
 *   Server uses: HMAC-SHA256(demo_signing_key, same string)
 *   Mismatch emits E_NOTICE only (non-blocking demo crypto limitation).
 */
static int mmloader_verify_lease_signature(cJSON *resp, const char *build_id)
{
    cJSON *j_leaseId  = cJSON_GetObjectItemCaseSensitive(resp, "leaseId");
    cJSON *j_expires  = cJSON_GetObjectItemCaseSensitive(resp, "expiresAt");
    cJSON *j_sig      = cJSON_GetObjectItemCaseSensitive(resp, "signature");

    if (!cJSON_IsString(j_leaseId) || !cJSON_IsString(j_expires)
        || !cJSON_IsString(j_sig)) {
        php_error_docref(NULL, E_WARNING,
            "MMENC: lease response missing leaseId/expiresAt/signature");
        return 0;
    }

    size_t data_len = strlen(j_leaseId->valuestring) + 1 +
                      strlen(build_id) + 1 +
                      strlen(s_machine_fingerprint) + 1 +
                      strlen(j_expires->valuestring);
    char *data = emalloc(data_len + 1);
    snprintf(data, data_len + 1, "%s:%s:%s:%s",
             j_leaseId->valuestring, build_id,
             s_machine_fingerprint, j_expires->valuestring);

    int ok;
    if (s_signing_public_key) {
        /* Week 4: ECDSA-P256 — hard error on mismatch */
        ok = mmloader_ecdsa_verify(s_signing_public_key,
                                    (const unsigned char *)data, data_len,
                                    j_sig->valuestring);
        ZEND_SECURE_ZERO(data, data_len);
        efree(data);
        if (!ok) {
            php_error_docref(NULL, E_WARNING,
                "MMENC: lease ECDSA signature invalid — rejecting lease");
        }
        return ok;
    }

#ifdef MMPROTECT_DEV_BUILD
    /* Dev fallback: HMAC-SHA256 (non-blocking) */
    unsigned char hmac[32];
    int hmac_ok = mmloader_hmac_sha256_demo(data, data_len, hmac);
    ZEND_SECURE_ZERO(data, data_len);
    efree(data);
    if (!hmac_ok) return 0;

    char *expected_b64 = mmloader_base64_encode(hmac, 32);
    if (!expected_b64) return 0;

    if (strcmp(expected_b64, j_sig->valuestring) != 0) {
        php_error_docref(NULL, E_NOTICE,
            "MMENC: lease HMAC signature mismatch (dev crypto — configure "
            "mmloader.signing_public_key_file for ECDSA-P256)");
    }
    efree(expected_b64);
    return 1; /* non-blocking in dev build */
#else
    /* Release: ECDSA key is mandatory */
    ZEND_SECURE_ZERO(data, data_len);
    efree(data);
    php_error_docref(NULL, E_WARNING,
        "MMENC: no signing public key — lease signature cannot be verified "
        "(mmloader.signing_public_key_file required in release build)");
    return 0;
#endif
}

/* ====================================================================
 * Week 3: Disk lease cache
 * ==================================================================== */

static void mmloader_cache_path(const char *build_id, char *out, size_t out_len)
{
    unsigned char hash[32];
    unsigned int  hash_len = 0;
    EVP_Digest(build_id, strlen(build_id), hash, &hash_len, EVP_sha256(), NULL);

    char hex[65] = {0};
    for (int i = 0; i < 32; i++) snprintf(hex + i * 2, 3, "%02x", hash[i]);

    snprintf(out, out_len, "%s/mmloader_%.32s.lease",
             MMLOADER_G(cache_dir), hex);
}

static void mm_cache_write_feature_cb(const char *name, void *ud)
{
    cJSON_AddItemToArray((cJSON *)ud, cJSON_CreateString(name));
}

static void mmloader_cache_write(const char *build_id,
                                  const unsigned char *key,
                                  time_t expires_at, time_t grace_until)
{
    const char *dir = MMLOADER_G(cache_dir);
    if (!dir || !dir[0]) return;

    char path[PATH_MAX], tmp_path[PATH_MAX];
    mmloader_cache_path(build_id, path, sizeof(path));
    snprintf(tmp_path, sizeof(tmp_path), "%s.tmp", path);

    char *key_b64 = mmloader_base64_encode(key, 32);
    if (!key_b64) return;

    cJSON *obj = cJSON_CreateObject();
    cJSON_AddStringToObject(obj, "buildId",    build_id);
    cJSON_AddStringToObject(obj, "runtimeKey", key_b64);
    cJSON_AddNumberToObject(obj, "expiresAt",  (double)expires_at);
    cJSON_AddNumberToObject(obj, "graceUntil", (double)grace_until);
    /* Persist feature set so offline-grace runs can restore mmprotect_has_feature() */
    if (MMLOADER_G(lease_features)) {
        cJSON *farr = cJSON_AddArrayToObject(obj, "features");
        mm_features_each(MMLOADER_G(lease_features), mm_cache_write_feature_cb, farr);
    }
    char *json = cJSON_PrintUnformatted(obj);
    cJSON_Delete(obj);

    ZEND_SECURE_ZERO(key_b64, strlen(key_b64));
    efree(key_b64);

    if (!json) return;

    int fd = open(tmp_path, O_WRONLY | O_CREAT | O_TRUNC, 0600);
    if (fd >= 0) {
        size_t  json_len = strlen(json);
        ssize_t written  = write(fd, json, json_len);
        close(fd);
        if (written == (ssize_t)json_len) {
            rename(tmp_path, path); /* POSIX-atomic — only on complete write */
        } else {
            unlink(tmp_path); /* discard partial/failed write */
        }
    }

    ZEND_SECURE_ZERO(json, strlen(json));
    cJSON_free(json);
}

static int mmloader_cache_read(const char *build_id,
                                unsigned char *key_out,
                                time_t *expires_out,
                                time_t *grace_out)
{
    const char *dir = MMLOADER_G(cache_dir);
    if (!dir || !dir[0]) return 0;

    char path[PATH_MAX];
    mmloader_cache_path(build_id, path, sizeof(path));

    cJSON *obj = NULL;
    if (!mmloader_read_json_file(path, &obj)) return 0;

    int ok = 0;
    cJSON *j_key     = cJSON_GetObjectItemCaseSensitive(obj, "runtimeKey");
    cJSON *j_expires = cJSON_GetObjectItemCaseSensitive(obj, "expiresAt");
    cJSON *j_grace   = cJSON_GetObjectItemCaseSensitive(obj, "graceUntil");
    cJSON *j_bid     = cJSON_GetObjectItemCaseSensitive(obj, "buildId");

    if (!cJSON_IsString(j_key)    || !cJSON_IsNumber(j_expires)
        || !cJSON_IsNumber(j_grace) || !cJSON_IsString(j_bid))
        goto done;

    if (strcmp(j_bid->valuestring, build_id) != 0) goto done;

    time_t grace = (time_t)j_grace->valuedouble;
    if (time(NULL) > grace) goto done;

    size_t key_len = 0;
    if (!mmloader_base64_decode(j_key->valuestring, strlen(j_key->valuestring),
                                key_out, &key_len) || key_len != 32)
        goto done;

    *expires_out = (time_t)j_expires->valuedouble;
    *grace_out   = grace;

    /* Restore feature set so mmprotect_has_feature() works during offline grace */
    cJSON *j_feat = cJSON_GetObjectItemCaseSensitive(obj, "features");
    if (cJSON_IsArray(j_feat)) {
        mm_features_reset(&MMLOADER_G(lease_features),
                          (uint32_t)cJSON_GetArraySize(j_feat));
        cJSON *fitem;
        cJSON_ArrayForEach(fitem, j_feat) {
            if (cJSON_IsString(fitem) && fitem->valuestring)
                mm_features_add(MMLOADER_G(lease_features), fitem->valuestring);
        }
    }

    ok = 1;

done:
    cJSON_Delete(obj);
    return ok;
}

/* ====================================================================
 * Week 2: HTTP lease call
 * ==================================================================== */

static size_t mmloader_lease_write_cb(char *ptr, size_t size, size_t nmemb, void *userdata)
{
    size_t n = size * nmemb;
    typedef struct { char *data; size_t len; size_t cap; } lease_buf_t;
    lease_buf_t *buf = (lease_buf_t *)userdata;
    if (buf->len + n + 1 > buf->cap) {
        size_t new_cap = (buf->len + n + 1) * 2;
        char *nd = erealloc(buf->data, new_cap);
        if (!nd) return 0;
        buf->data = nd; buf->cap = new_cap;
    }
    memcpy(buf->data + buf->len, ptr, n);
    buf->len += n;
    buf->data[buf->len] = '\0';
    return n;
}

typedef struct { char *data; size_t len; size_t cap; } lease_buf_t;

static int mmloader_read_json_file(const char *path, cJSON **out)
{
    *out = NULL;
    if (!path || !path[0]) return 0;
    FILE *fp = fopen(path, "rb");
    if (!fp) return 0;
    fseek(fp, 0, SEEK_END);
    long fsize = ftell(fp);
    fseek(fp, 0, SEEK_SET);
    if (fsize <= 0 || fsize > 512 * 1024) { fclose(fp); return 0; }
    char *buf = emalloc((size_t)fsize + 1);
    size_t n  = fread(buf, 1, (size_t)fsize, fp);
    fclose(fp);
    buf[n] = '\0';
    *out = cJSON_ParseWithLength(buf, n);
    efree(buf);
    if (!*out) {
        php_error_docref(NULL, E_WARNING, "MMENC: JSON parse error in %s", path);
        return 0;
    }
    return 1;
}

static int mmloader_nonce_hex(char *nonce_out)
{
    unsigned char rnd[16];
    if (RAND_bytes(rnd, sizeof(rnd)) != 1) return 0;
    for (int i = 0; i < 16; i++) snprintf(nonce_out + i * 2, 3, "%02x", rnd[i]);
    return 1;
}

static time_t mmloader_parse_iso8601(const char *ts)
{
    if (!ts || !*ts) return 0;
    struct tm tm = {0};
    if (sscanf(ts, "%d-%d-%dT%d:%d:%d",
               &tm.tm_year, &tm.tm_mon, &tm.tm_mday,
               &tm.tm_hour, &tm.tm_min, &tm.tm_sec) < 6) return 0;
    tm.tm_year -= 1900;
    tm.tm_mon  -= 1;
    return timegm(&tm);
}

static int mmloader_post_lease(const char *body, const char *build_id,
                                unsigned char *key_out,
                                const char *server_override)
{
    CURL *curl = MMLOADER_G(curl_handle);
    if (!curl) return 0;

    /* Header-embedded URL takes priority over global INI setting */
    const char *server = (server_override && server_override[0])
        ? server_override : MMLOADER_G(license_server);
    size_t url_len = strlen(server) + strlen("/api/v1/runtime/lease") + 1;
    char *url = emalloc(url_len);
    snprintf(url, url_len, "%s/api/v1/runtime/lease", server);

    lease_buf_t resp = {0};
    resp.data = emalloc(4096);
    resp.cap  = 4096;
    resp.data[0] = '\0';

    struct curl_slist *headers = NULL;
    int ok = 0;

    curl_easy_reset(curl);
    curl_easy_setopt(curl, CURLOPT_URL,           url);
    curl_easy_setopt(curl, CURLOPT_POST,          1L);
    curl_easy_setopt(curl, CURLOPT_POSTFIELDS,    body);
    curl_easy_setopt(curl, CURLOPT_POSTFIELDSIZE, (long)strlen(body));
    headers = curl_slist_append(headers, "Content-Type: application/json");
    headers = curl_slist_append(headers, "Accept: application/json");
    curl_easy_setopt(curl, CURLOPT_HTTPHEADER,    headers);
    curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, mmloader_lease_write_cb);
    curl_easy_setopt(curl, CURLOPT_WRITEDATA,     &resp);
    curl_easy_setopt(curl, CURLOPT_CONNECTTIMEOUT_MS, (long)MMLOADER_G(connect_timeout_ms));
    curl_easy_setopt(curl, CURLOPT_TIMEOUT_MS,        (long)MMLOADER_G(request_timeout_ms));
#ifdef MMPROTECT_DEV_BUILD
    if (MMLOADER_G(dev_mode)) {
        curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 0L);
        curl_easy_setopt(curl, CURLOPT_SSL_VERIFYHOST, 0L);
    }
#endif

    CURLcode res = curl_easy_perform(curl);
    curl_slist_free_all(headers);
    efree(url);

    if (res != CURLE_OK) {
        php_error_docref(NULL, E_WARNING,
            "MMENC: lease request failed: %s", curl_easy_strerror(res));
        goto cleanup;
    }

    long http_code = 0;
    curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &http_code);
    if (http_code != 200) {
        php_error_docref(NULL, E_WARNING,
            "MMENC: lease server returned HTTP %ld", http_code);
        goto cleanup;
    }

    cJSON *json = cJSON_Parse(resp.data);
    if (!json) {
        php_error_docref(NULL, E_WARNING, "MMENC: lease response not valid JSON");
        goto cleanup;
    }

    /* Verify lease signature before trusting the key */
    if (!mmloader_verify_lease_signature(json, build_id)) {
        cJSON_Delete(json);
        goto cleanup;
    }

    cJSON *j_key    = cJSON_GetObjectItemCaseSensitive(json, "runtimeKey");
    cJSON *j_expires = cJSON_GetObjectItemCaseSensitive(json, "expiresAt");
    cJSON *j_grace   = cJSON_GetObjectItemCaseSensitive(json, "graceUntil");

    if (!cJSON_IsString(j_key)) {
        php_error_docref(NULL, E_WARNING, "MMENC: lease response missing runtimeKey");
        cJSON_Delete(json);
        goto cleanup;
    }

    size_t decoded_len = 0;
    if (!mmloader_base64_decode(j_key->valuestring, strlen(j_key->valuestring),
                                key_out, &decoded_len) || decoded_len != 32) {
        php_error_docref(NULL, E_WARNING, "MMENC: lease runtimeKey is not 32 bytes");
        cJSON_Delete(json);
        goto cleanup;
    }

    time_t expires_at = time(NULL) + MMLOADER_G(lease_refresh_seconds);
    time_t grace_until = expires_at + MMLOADER_G(offline_grace_seconds);
    if (cJSON_IsString(j_expires)) {
        time_t t = mmloader_parse_iso8601(j_expires->valuestring);
        if (t > 0) expires_at = t;
    }
    if (cJSON_IsString(j_grace)) {
        time_t t = mmloader_parse_iso8601(j_grace->valuestring);
        if (t > 0) grace_until = t;
    }

    memcpy(MMLOADER_G(cached_runtime_key), key_out, 32);
    MMLOADER_G(has_cached_key)        = 1;
    MMLOADER_G(cached_lease_expires)  = expires_at;
    MMLOADER_G(cached_lease_grace)    = grace_until;
    if (MMLOADER_G(cached_build_id))  pefree(MMLOADER_G(cached_build_id), 1);
    MMLOADER_G(cached_build_id) = build_id ? pestrdup(build_id, 1) : NULL;

    /* Extract feature set from lease response and store persistently */
    cJSON *j_features = cJSON_GetObjectItemCaseSensitive(json, "features");
    if (cJSON_IsArray(j_features)) {
        mm_features_reset(&MMLOADER_G(lease_features),
                          (uint32_t)cJSON_GetArraySize(j_features));
        cJSON *item;
        cJSON_ArrayForEach(item, j_features) {
            if (cJSON_IsString(item) && item->valuestring)
                mm_features_add(MMLOADER_G(lease_features), item->valuestring);
        }
    }

    /* Store license metadata so mmprotect_license_info() can expose it to PHP */
    #define MM_CACHE_STR(field, key) do { \
        cJSON *_j = cJSON_GetObjectItemCaseSensitive(json, key); \
        if (MMLOADER_G(field)) { pefree(MMLOADER_G(field), 1); MMLOADER_G(field) = NULL; } \
        if (cJSON_IsString(_j) && _j->valuestring[0]) \
            MMLOADER_G(field) = pestrdup(_j->valuestring, 1); \
    } while(0)
    MM_CACHE_STR(cached_license_id,  "licenseId");
    MM_CACHE_STR(cached_customer_id, "customerId");
    MM_CACHE_STR(cached_project_id,  "projectId");
    MM_CACHE_STR(cached_valid_from,  "validFrom");
    MM_CACHE_STR(cached_valid_until, "validUntil");
    #undef MM_CACHE_STR
    /* Store the effective server URL (header-embedded > INI setting) */
    {
        const char *eff = server ? server : "";
        if (MMLOADER_G(cached_effective_server)) { pefree(MMLOADER_G(cached_effective_server), 1); MMLOADER_G(cached_effective_server) = NULL; }
        if (eff[0]) MMLOADER_G(cached_effective_server) = pestrdup(eff, 1);
    }

    mmloader_cache_write(build_id, key_out, expires_at, grace_until);

    cJSON_Delete(json);
    ok = 1;

cleanup:
    efree(resp.data);
    return ok;
}

#ifdef MMPROTECT_DEV_BUILD
static int mmloader_read_dev_buildkey(unsigned char *key_out)
{
    const char *path = MMLOADER_G(dev_buildkey);
    if (!path || !path[0]) {
        php_error_docref(NULL, E_WARNING, "MMENC: mmloader.dev_buildkey not configured");
        return 0;
    }
    FILE *fp = fopen(path, "rb");
    if (!fp) {
        php_error_docref(NULL, E_WARNING, "MMENC: cannot open dev_buildkey: %s", path);
        return 0;
    }
    char b64[256] = {0};
    size_t n = fread(b64, 1, sizeof(b64) - 1, fp);
    fclose(fp);
    while (n > 0 && (b64[n-1] == '\n' || b64[n-1] == '\r'
                     || b64[n-1] == ' '  || b64[n-1] == '\t'))
        b64[--n] = '\0';
    size_t key_len = 0;
    int ok = mmloader_base64_decode(b64, n, key_out, &key_len) && key_len == 32;
    memset(b64, 0, sizeof(b64));
    if (!ok)
        php_error_docref(NULL, E_WARNING,
            "MMENC: dev_buildkey must be Base64 of exactly 32 bytes");
    return ok;
}
#endif /* MMPROTECT_DEV_BUILD */

static int mmloader_fetch_lease(const char *build_id, unsigned char *key_out,
                                 const char *server_override)
{
    cJSON *lic = NULL, *mf = NULL;
    char  *body = NULL;
    int    ok   = 0;

    if (!mmloader_read_json_file(MMLOADER_G(license_file), &lic)) goto done;
    if (!mmloader_read_json_file(MMLOADER_G(manifest_file), &mf)) goto done;

    const char *project_id    = cJSON_GetStringValue(cJSON_GetObjectItemCaseSensitive(lic, "projectId"));
    const char *customer_id   = cJSON_GetStringValue(cJSON_GetObjectItemCaseSensitive(lic, "customerId"));
    const char *license_id    = cJSON_GetStringValue(cJSON_GetObjectItemCaseSensitive(lic, "licenseId"));
    const char *manifest_hash = cJSON_GetStringValue(cJSON_GetObjectItemCaseSensitive(mf, "manifestHash"));

    if (!project_id || !customer_id || !license_id) {
        php_error_docref(NULL, E_WARNING, "MMENC: license.json missing required fields");
        goto done;
    }

    char nonce[33] = {0};
    if (!mmloader_nonce_hex(nonce)) goto done;

    cJSON *req = cJSON_CreateObject();
    cJSON_AddStringToObject(req, "projectId",          project_id);
    cJSON_AddStringToObject(req, "customerId",         customer_id);
    cJSON_AddStringToObject(req, "licenseId",          license_id);
    cJSON_AddStringToObject(req, "buildId",            build_id);
    cJSON_AddStringToObject(req, "manifestHash",       manifest_hash ? manifest_hash : "pending");
    cJSON_AddStringToObject(req, "machineFingerprint", s_machine_fingerprint);
    cJSON_AddStringToObject(req, "loaderVersion",      PHP_MMLOADER_VERSION);
    cJSON_AddStringToObject(req, "phpVersion",         PHP_VERSION);
    cJSON_AddStringToObject(req, "sapi",               sapi_module.name ? sapi_module.name : "cli");
    cJSON_AddStringToObject(req, "nonce",              nonce);
    {
        char req_hostname[256] = {0};
        gethostname(req_hostname, sizeof(req_hostname) - 1);
        cJSON_AddStringToObject(req, "hostname", req_hostname);
    }
    body = cJSON_PrintUnformatted(req);
    cJSON_Delete(req);
    if (!body) goto done;

    ok = mmloader_post_lease(body, build_id, key_out, server_override);

done:
    if (body) cJSON_free(body);
    if (lic)  cJSON_Delete(lic);
    if (mf)   cJSON_Delete(mf);
    return ok;
}

static int mmloader_get_or_fetch_runtime_key(const char *build_id,
                                              unsigned char *key_out,
                                              const char *server_override)
{
    time_t now = time(NULL);

    /* 1. RAM cache hit */
    if (MMLOADER_G(has_cached_key)
        && MMLOADER_G(cached_build_id)
        && strcmp(MMLOADER_G(cached_build_id), build_id) == 0
        && now < MMLOADER_G(cached_lease_expires)) {
        memcpy(key_out, MMLOADER_G(cached_runtime_key), 32);
        return 1;
    }

    /* 2. Disk cache (before HTTP — needed for offline grace path) */
    time_t disk_expires = 0, disk_grace = 0;
    unsigned char disk_key[32] = {0};
    int have_disk_cache = mmloader_cache_read(build_id, disk_key, &disk_expires, &disk_grace);

    /* 3. HTTP lease */
    const char *server = (server_override && server_override[0])
        ? server_override : MMLOADER_G(license_server);
    if (server && server[0]) {
        if (mmloader_fetch_lease(build_id, key_out, server_override)) {
            mmloader_send_telemetry("lease_acquired", build_id);
            ZEND_SECURE_ZERO(disk_key, sizeof(disk_key));
            return 1;
        }

        /* HTTP failed: use disk cache if within graceUntil */
        if (have_disk_cache && now < disk_grace) {
            memcpy(key_out, disk_key, 32);
            memcpy(MMLOADER_G(cached_runtime_key), disk_key, 32);
            MMLOADER_G(has_cached_key)       = 1;
            MMLOADER_G(cached_lease_expires) = disk_expires;
            MMLOADER_G(cached_lease_grace)   = disk_grace;
            if (MMLOADER_G(cached_build_id)) pefree(MMLOADER_G(cached_build_id), 1);
            MMLOADER_G(cached_build_id) = pestrdup(build_id, 1);
            ZEND_SECURE_ZERO(disk_key, sizeof(disk_key));
            php_error_docref(NULL, E_NOTICE,
                "MMENC: using offline cached lease for build %s (grace period active)", build_id);
            mmloader_send_telemetry("lease_offline_grace", build_id);
            return 1;
        }

        if (have_disk_cache && now >= disk_grace) {
            ZEND_SECURE_ZERO(disk_key, sizeof(disk_key));
            php_error_docref(NULL, E_WARNING,
                "MMENC: license server unreachable and grace period exceeded for build %s",
                build_id);
            return 0;
        }
    }

#ifdef MMPROTECT_DEV_BUILD
    /* 4. dev_buildkey fallback (dev build only).
     *    Populate the RAM lease cache so the execute_ex OPcache guard passes
     *    on subsequent cache-hit requests (e.g. when running under Xdebug
     *    or after the first FPM request warms OPcache). */
    const char *dbk = MMLOADER_G(dev_buildkey);
    if (dbk && dbk[0]) {
        ZEND_SECURE_ZERO(disk_key, sizeof(disk_key));
        if (!mmloader_read_dev_buildkey(key_out)) return 0;
        memcpy(MMLOADER_G(cached_runtime_key), key_out, 32);
        MMLOADER_G(has_cached_key) = 1;
        return 1;
    }
#endif

    ZEND_SECURE_ZERO(disk_key, sizeof(disk_key));
    php_error_docref(NULL, E_WARNING,
        "MMENC: no runtime key available "
#ifdef MMPROTECT_DEV_BUILD
        "(server unreachable, no disk cache in grace, no dev_buildkey)");
#else
        "(server unreachable, no disk cache within grace period)");
#endif
    return 0;
}

/* ====================================================================
 * Week 3+4: Protected-files set
 * ==================================================================== */

static void mmloader_mark_file_protected(const char *filename)
{
    mm_protected_mark(MMLOADER_G(protected_files), filename);
}

/* ====================================================================
 * Week 4: MMENC1 magic cache + OPcache execute_ex guard
 * ==================================================================== */

/*
 * Return 1 if the file starts with MMENC1 magic, 0 otherwise.
 * Results are cached per-process in file_magic_cache to avoid
 * repeated file I/O on every function call from the same source file.
 */
static int mmloader_is_mmenc1_file(const char *filename)
{
    if (!MMLOADER_G(file_magic_cache) || !filename) return 0;

    size_t fname_len = strlen(filename);

    /* Per-process cache lookup */
    int cached = mm_magic_cache_lookup(MMLOADER_G(file_magic_cache),
                                        filename, fname_len);
    if (cached >= 0) return cached;

    /* Open file and read magic */
    int is_mmenc1 = 0;
    FILE *fp = fopen(filename, "rb");
    if (fp) {
        char magic[7];
        if (fread(magic, 1, 7, fp) == 7 && memcmp(magic, "MMENC1\n", 7) == 0)
            is_mmenc1 = 1;
        fclose(fp);
    }

    mm_magic_cache_store(MMLOADER_G(file_magic_cache), filename, fname_len, is_mmenc1);

    return is_mmenc1;
}

/*
 * execute_ex hook: OPcache guard.
 *
 * When OPcache serves an op_array from shared memory (cache hit), the
 * compile_file hook is skipped. This hook intercepts execution of every
 * user-defined function and verifies that MMENC1 source files were
 * authorised by this loader process. Prevents OPcache from being used
 * as a bypass mechanism.
 *
 * Design note (load order): zend_extension=opcache.so must appear BEFORE
 * zend_extension=mmloader.so in php.ini. This makes mmloader the outermost
 * compile_file hook: on a cache miss mmloader decrypts first, then hands
 * the plaintext to zend_compile_string; OPcache has no opportunity to cache
 * the MMENC1 binary blob. The execute_ex guard provides defence-in-depth
 * for any path that bypasses compile_file.
 */
static void mmloader_execute_ex_hook(zend_execute_data *execute_data)
{
    const char *filename;
    if (MMLOADER_G(enabled) &&
        (filename = mm_execute_data_source_path(execute_data)) != NULL) {

        if (mmloader_is_mmenc1_file(filename)) {
            /* Is this file authorised by mmloader in this process? */
            if (!mm_protected_check(MMLOADER_G(protected_files), filename)) {
                /* Not yet in protected set.
                 * Could be an OPcache cache hit in a fresh FPM worker.
                 * Accept if we have a valid RAM lease and mark it. */
                if (MMLOADER_G(has_cached_key)) {
                    mmloader_mark_file_protected(filename);
                } else {
                    zend_error(E_ERROR,
                        "MMENC: execution of unverified protected file blocked: %s",
                        filename);
                    return;
                }
            }

            /* Enforce lease expiry even for cached op_arrays */
            if (MMLOADER_G(cached_lease_expires) > 0) {
                time_t now = time(NULL);
                if (now > MMLOADER_G(cached_lease_grace)) {
                    zend_error(E_ERROR,
                        "MMENC: lease expired and grace period exceeded "
                        "— blocking %s", filename);
                    return;
                }
            }
        }
    }

    s_orig_execute_ex(execute_data);
}

/* ====================================================================
 * MMENC1 parser + decrypt pipeline
 * ==================================================================== */

static zend_string *mmloader_decrypt_from_fp(FILE *fp, const char *filename)
{
    zend_string  *result           = NULL;
    char         *header_json      = NULL;
    unsigned char *ciphertext      = NULL;
    unsigned char *plaintext       = NULL;
    size_t        plain_alloc_len  = 0; /* tracks current plaintext buffer size for secure zero */
    cJSON        *root             = NULL;
    char         *info             = NULL;
    size_t        ct_len           = 0;

    char len_buf[9];
    if (fread(len_buf, 1, 9, fp) != 9 || len_buf[8] != '\n') {
        php_error_docref(NULL, E_WARNING, "MMENC: malformed header length in %s", filename);
        goto cleanup;
    }
    len_buf[8] = '\0';
    size_t header_len = (size_t)strtoul(len_buf, NULL, 10);
    if (header_len == 0 || header_len > 65536) {
        php_error_docref(NULL, E_WARNING, "MMENC: header length out of range in %s", filename);
        goto cleanup;
    }

    header_json = emalloc(header_len + 1);
    if (fread(header_json, 1, header_len, fp) != header_len) {
        php_error_docref(NULL, E_WARNING, "MMENC: truncated JSON header in %s", filename);
        goto cleanup;
    }
    header_json[header_len] = '\0';

    long ct_start = 16L + (long)header_len;
    fseek(fp, 0, SEEK_END);
    long file_size = ftell(fp);
    if (file_size < 0 || file_size <= ct_start) {
        php_error_docref(NULL, E_WARNING, "MMENC: no ciphertext in %s", filename);
        goto cleanup;
    }
    fseek(fp, ct_start, SEEK_SET);
    ct_len = (size_t)(file_size - ct_start);
    if (ct_len > (size_t)MMLOADER_G(max_file_size_mb) * 1024 * 1024) {
        php_error_docref(NULL, E_WARNING,
            "MMENC: ciphertext in %s exceeds mmloader.max_file_size_mb (%ld MiB)",
            filename, MMLOADER_G(max_file_size_mb));
        goto cleanup;
    }

    ciphertext = emalloc(ct_len);
    if (fread(ciphertext, 1, ct_len, fp) != ct_len) {
        php_error_docref(NULL, E_WARNING, "MMENC: truncated ciphertext in %s", filename);
        goto cleanup;
    }
    fclose(fp); fp = NULL;

    root = cJSON_ParseWithLength(header_json, header_len);
    if (!root) {
        php_error_docref(NULL, E_WARNING, "MMENC: JSON header parse error in %s", filename);
        goto cleanup;
    }

    /* Format version compatibility gate */
    {
        cJSON *j_fv = cJSON_GetObjectItemCaseSensitive(root, "formatVersion");
        long fv = cJSON_IsNumber(j_fv) ? (long)j_fv->valuedouble : 1L;
        if (fv < MMLOADER_FORMAT_VERSION_MIN) {
            php_error_docref(NULL, E_WARNING,
                "MMENC: file %s uses obsolete format version %ld (minimum %d). "
                "Re-encode with a current encoder.",
                filename, fv, MMLOADER_FORMAT_VERSION_MIN);
            goto cleanup;
        }
        if (fv > MMLOADER_FORMAT_VERSION_MAX) {
            php_error_docref(NULL, E_WARNING,
                "MMENC: file %s requires format version %ld but this loader "
                "supports up to version %d. Update the mmloader extension.",
                filename, fv, MMLOADER_FORMAT_VERSION_MAX);
            goto cleanup;
        }
    }

    if (!mmloader_verify_file_signature(root, filename)) goto cleanup;

    cJSON *j_buildId  = cJSON_GetObjectItemCaseSensitive(root, "buildId");
    cJSON *j_fileId   = cJSON_GetObjectItemCaseSensitive(root, "fileId");
    cJSON *j_pathHash = cJSON_GetObjectItemCaseSensitive(root, "pathHash");
    cJSON *j_nonce    = cJSON_GetObjectItemCaseSensitive(root, "nonce");
    cJSON *j_tag      = cJSON_GetObjectItemCaseSensitive(root, "tag");
    cJSON *j_algo     = cJSON_GetObjectItemCaseSensitive(root, "algorithm");
    cJSON *j_comp          = cJSON_GetObjectItemCaseSensitive(root, "compression");
    int    use_lz4         = (cJSON_IsString(j_comp) &&
                              strcmp(j_comp->valuestring, "lz4") == 0) ? 1 : 0;
    cJSON *j_licenseServer = cJSON_GetObjectItemCaseSensitive(root, "licenseServer");
    const char *server_override = (cJSON_IsString(j_licenseServer) &&
                                   j_licenseServer->valuestring[0])
        ? j_licenseServer->valuestring : NULL;

    if (!cJSON_IsString(j_buildId)  || !cJSON_IsString(j_fileId) ||
        !cJSON_IsString(j_pathHash) || !cJSON_IsString(j_nonce)  ||
        !cJSON_IsString(j_tag)      || !cJSON_IsString(j_algo)) {
        php_error_docref(NULL, E_WARNING, "MMENC: missing header fields in %s", filename);
        goto cleanup;
    }

    if (strcmp(j_algo->valuestring, "AES-256-GCM") != 0) {
        php_error_docref(NULL, E_WARNING,
            "MMENC: unsupported algorithm '%s' in %s", j_algo->valuestring, filename);
        goto cleanup;
    }

    unsigned char nonce[12], tag[16];
    size_t nonce_len = 0, tag_len = 0;

    if (!mmloader_base64_decode(j_nonce->valuestring, strlen(j_nonce->valuestring),
                                nonce, &nonce_len) || nonce_len != 12) {
        php_error_docref(NULL, E_WARNING, "MMENC: nonce decode failed in %s", filename);
        goto cleanup;
    }
    if (!mmloader_base64_decode(j_tag->valuestring, strlen(j_tag->valuestring),
                                tag, &tag_len) || tag_len != 16) {
        php_error_docref(NULL, E_WARNING, "MMENC: tag decode failed in %s", filename);
        goto cleanup;
    }

    size_t info_len = strlen(j_buildId->valuestring) + 1 +
                      strlen(j_fileId->valuestring)  + 1 +
                      strlen(j_pathHash->valuestring);
    info = emalloc(info_len + 1);
    snprintf(info, info_len + 1, "%s:%s:%s",
             j_buildId->valuestring, j_fileId->valuestring, j_pathHash->valuestring);

    unsigned char build_key[32];
    if (!mmloader_get_or_fetch_runtime_key(j_buildId->valuestring, build_key,
                                           server_override)) {
        ZEND_SECURE_ZERO(info, info_len);
        efree(info); info = NULL;
        goto cleanup;
    }

    unsigned char file_key[32];
    int hkdf_ok = mmloader_hkdf(build_key, 32, info, info_len, file_key, 32);
    ZEND_SECURE_ZERO(build_key, sizeof(build_key));
    ZEND_SECURE_ZERO(info, info_len);
    efree(info); info = NULL;
    if (!hkdf_ok) {
        php_error_docref(NULL, E_WARNING, "MMENC: HKDF failed for %s", filename);
        goto cleanup;
    }

    plaintext = emalloc(ct_len);
    plain_alloc_len = ct_len;
    size_t pt_len = 0;
    int dec_ok = mmloader_aes256gcm_decrypt(
        file_key, nonce, 12, ciphertext, ct_len, tag, 16, plaintext, &pt_len);

    ZEND_SECURE_ZERO(file_key, sizeof(file_key));
    ZEND_SECURE_ZERO(nonce,    sizeof(nonce));
    ZEND_SECURE_ZERO(tag,      sizeof(tag));
    ZEND_SECURE_ZERO(ciphertext, ct_len);
    efree(ciphertext); ciphertext = NULL;

    if (!dec_ok) {
        php_error_docref(NULL, E_WARNING,
            "MMENC: AES-GCM authentication failed for %s "
            "(wrong key or corrupted file)", filename);
        goto cleanup;
    }

    if (use_lz4) {
        /* Decompress LZ4 block: plaintext = [4-byte LE orig_size][LZ4 block data] */
        if (pt_len < 4) {
            php_error_docref(NULL, E_WARNING,
                "MMENC: LZ4 payload too short in %s", filename);
            goto cleanup;
        }
        int32_t orig_size;
        memcpy(&orig_size, plaintext, 4);
        if (orig_size <= 0 || orig_size > 64 * 1024 * 1024) {
            php_error_docref(NULL, E_WARNING,
                "MMENC: invalid LZ4 decompressed size %d in %s", orig_size, filename);
            goto cleanup;
        }
        unsigned char *decompressed = emalloc((size_t)orig_size + 1);
        int lz4_result = LZ4_decompress_safe(
            (const char *)plaintext + 4,
            (char *)decompressed,
            (int)(pt_len - 4),
            orig_size);
        ZEND_SECURE_ZERO(plaintext, plain_alloc_len);
        efree(plaintext);
        plaintext = NULL;
        plain_alloc_len = 0;
        if (lz4_result != orig_size) {
            php_error_docref(NULL, E_WARNING,
                "MMENC: LZ4 decompression failed in %s (got %d, expected %d)",
                filename, lz4_result, orig_size);
            ZEND_SECURE_ZERO(decompressed, (size_t)orig_size);
            efree(decompressed);
            goto cleanup;
        }
        plaintext = decompressed;
        plain_alloc_len = (size_t)orig_size;
        pt_len = (size_t)orig_size;
    }

    result = mm_zstr_new((const char *)plaintext, pt_len);
    ZEND_SECURE_ZERO(plaintext, plain_alloc_len);
    efree(plaintext); plaintext = NULL;
    plain_alloc_len = 0;

cleanup:
    if (fp)          fclose(fp);
    if (info)        efree(info);
    if (header_json) efree(header_json);
    if (ciphertext)  { ZEND_SECURE_ZERO(ciphertext, ct_len);         efree(ciphertext); }
    if (plaintext)   { ZEND_SECURE_ZERO(plaintext,  plain_alloc_len); efree(plaintext); }
    if (root)        cJSON_Delete(root);
    return result;
}

/* ====================================================================
 * Compile hook
 * ==================================================================== */

static zend_op_array *mmloader_compile_file(zend_file_handle *file_handle, int type)
{
    if (!MMLOADER_G(enabled))
        return s_orig_compile_file(file_handle, type);

    const char *filename = mm_file_handle_path(file_handle);

    FILE *fp = fopen(filename, "rb");
    if (!fp)
        return s_orig_compile_file(file_handle, type);

    char magic[7];
    if (fread(magic, 1, 7, fp) != 7 || memcmp(magic, "MMENC1\n", 7) != 0) {
        fclose(fp);
        return s_orig_compile_file(file_handle, type);
    }

    zend_string *plain = mmloader_decrypt_from_fp(fp, filename);
    /* fp closed inside mmloader_decrypt_from_fp */

    if (!plain) {
        /* Do NOT call zend_error(E_COMPILE_ERROR) from inside a compile_file
         * hook: E_COMPILE_ERROR triggers zend_bailout() (longjmp), which skips
         * the 'return NULL' and leaves the engine in an undefined state during
         * shutdown, causing SIGSEGV.  Emit a warning instead and return NULL;
         * PHP's own include/require machinery will raise E_COMPILE_ERROR for
         * 'require' and E_WARNING for 'include' when compile_file returns NULL. */
        php_error_docref(NULL, E_WARNING,
            "MMENC: failed to decrypt protected file: %s", filename);
        return NULL;
    }

    zend_op_array *op_array = mm_compile_plaintext(plain, filename);

    mm_zstr_secure_release(plain);

    if (op_array) mmloader_mark_file_protected(filename);

    return op_array;
}

/* ====================================================================
 * Extension lifecycle
 * ==================================================================== */

/* ====================================================================
 * Error reporting
 * ==================================================================== */

static void mmloader_error_cb(int type, zend_string *error_filename,
                               const uint32_t error_lineno, zend_string *message)
{
    if (MMLOADER_G(error_reporting_enabled)
        && MMLOADER_G(has_cached_key)
        && MMLOADER_G(error_batch) != NULL
        && MMLOADER_G(error_batch_count) < (int)MMLOADER_G(error_report_max)
        && (type & (int)MMLOADER_G(error_report_level_mask))) {

        cJSON *entry = cJSON_CreateObject();
        if (entry) {
            cJSON_AddNumberToObject(entry, "level", (double)type);
            cJSON_AddStringToObject(entry, "message",
                (message && ZSTR_LEN(message) > 0) ? ZSTR_VAL(message) : "");
            if (error_filename && ZSTR_LEN(error_filename) > 0)
                cJSON_AddStringToObject(entry, "file", ZSTR_VAL(error_filename));
            cJSON_AddNumberToObject(entry, "line", (double)error_lineno);
            char ts[32];
            time_t now = time(NULL);
            struct tm *t = gmtime(&now);
            strftime(ts, sizeof(ts), "%Y-%m-%dT%H:%M:%SZ", t);
            cJSON_AddStringToObject(entry, "timestamp", ts);
            if (cJSON_AddItemToArray(MMLOADER_G(error_batch), entry))
                MMLOADER_G(error_batch_count)++;
            else
                cJSON_Delete(entry);
        }
    }
    if (s_orig_error_cb)
        s_orig_error_cb(type, error_filename, error_lineno, message);
}

static size_t mmloader_discard_write_cb(char *ptr, size_t size, size_t nmemb, void *ud)
{
    (void)ptr; (void)ud;
    return size * nmemb;
}

/* Send a single telemetry event to the license server.
 * Fire-and-forget with 3 s timeout. Never throws.
 * Only sent when mmloader.telemetry = 1 (disabled by default). */
static void mmloader_send_telemetry(const char *event_type, const char *build_id)
{
    if (!MMLOADER_G(telemetry_enabled)) return;
    if (!MMLOADER_G(has_cached_key))   return; /* only after a successful lease */

    const char *base_url = MMLOADER_G(telemetry_url);
    char auto_url[2048] = {0};
    if (!base_url || !base_url[0]) {
        const char *srv = MMLOADER_G(cached_effective_server);
        if (!srv || !srv[0]) srv = MMLOADER_G(license_server);
        if (!srv || !srv[0]) return;
        snprintf(auto_url, sizeof(auto_url), "%s/api/v1/telemetry/loader", srv);
        base_url = auto_url;
    }

    cJSON *payload = cJSON_CreateObject();
    if (!payload) return;
    cJSON_AddStringToObject(payload, "source",     "loader");
    cJSON_AddStringToObject(payload, "eventType",  event_type);
    if (MMLOADER_G(cached_license_id))
        cJSON_AddStringToObject(payload, "licenseId", MMLOADER_G(cached_license_id));
    if (build_id && build_id[0])
        cJSON_AddStringToObject(payload, "buildId", build_id);
    if (MMLOADER_G(cached_project_id))
        cJSON_AddStringToObject(payload, "projectId", MMLOADER_G(cached_project_id));

    /* ISO-8601 timestamp */
    time_t now = time(NULL);
    struct tm tm_utc;
    char ts[32] = {0};
    gmtime_r(&now, &tm_utc);
    strftime(ts, sizeof(ts), "%Y-%m-%dT%H:%M:%SZ", &tm_utc);
    cJSON_AddStringToObject(payload, "occurredAt", ts);

    /* Extra data (phpVersion, sapi) */
    cJSON *data = cJSON_CreateObject();
    if (data) {
        cJSON_AddStringToObject(data, "phpVersion", PHP_VERSION);
        cJSON_AddStringToObject(data, "sapi", sapi_module.name ? sapi_module.name : "unknown");
        cJSON_AddItemToObject(payload, "data", data);
    }

    char *body = cJSON_PrintUnformatted(payload);
    cJSON_Delete(payload);
    if (!body) return;

    CURL *c = curl_easy_init();
    if (c) {
        struct curl_slist *hdrs = NULL;
        hdrs = curl_slist_append(hdrs, "Content-Type: application/json");
        curl_easy_setopt(c, CURLOPT_URL, base_url);
        curl_easy_setopt(c, CURLOPT_POSTFIELDS, body);
        curl_easy_setopt(c, CURLOPT_POSTFIELDSIZE, (long)strlen(body));
        curl_easy_setopt(c, CURLOPT_HTTPHEADER, hdrs);
        curl_easy_setopt(c, CURLOPT_TIMEOUT_MS, 3000L);
        curl_easy_setopt(c, CURLOPT_CONNECTTIMEOUT_MS, 2000L);
        curl_easy_setopt(c, CURLOPT_WRITEFUNCTION, mmloader_discard_write_cb);
#ifdef MMPROTECT_DEV_BUILD
        if (MMLOADER_G(dev_mode)) {
            curl_easy_setopt(c, CURLOPT_SSL_VERIFYPEER, 0L);
            curl_easy_setopt(c, CURLOPT_SSL_VERIFYHOST, 0L);
        }
#endif
        curl_easy_perform(c); /* ignore result — fire and forget */
        curl_slist_free_all(hdrs);
        curl_easy_cleanup(c);
    }
    free(body);
}

/* Send collected error batch to the license server.
 * Called from RSHUTDOWN; fire-and-forget with short timeout. */
static void mmloader_send_error_batch(void)
{
    if (!MMLOADER_G(error_reporting_enabled)) return;
    if (!MMLOADER_G(has_cached_key))          return;
    if (!MMLOADER_G(error_batch) || MMLOADER_G(error_batch_count) == 0) return;

    /* Determine URL: error_report_url overrides license_server + path */
    const char *base_url = MMLOADER_G(error_report_url);
    char auto_url[2048] = {0};
    if (!base_url || !base_url[0]) {
        const char *srv = MMLOADER_G(license_server);
        if (!srv || !srv[0]) return;
        snprintf(auto_url, sizeof(auto_url), "%s/api/v1/runtime/errors", srv);
        base_url = auto_url;
    }

    cJSON *payload = cJSON_CreateObject();
    if (!payload) return;
    if (MMLOADER_G(cached_license_id))
        cJSON_AddStringToObject(payload, "licenseId", MMLOADER_G(cached_license_id));
    if (MMLOADER_G(cached_build_id))
        cJSON_AddStringToObject(payload, "buildId", MMLOADER_G(cached_build_id));
    cJSON_AddStringToObject(payload, "machineFingerprint", s_machine_fingerprint);
    cJSON_AddStringToObject(payload, "phpVersion", PHP_VERSION);
    cJSON_AddStringToObject(payload, "sapi", sapi_module.name ? sapi_module.name : "unknown");
    /* Reference the batch array — do NOT delete it here, RSHUTDOWN owns it */
    cJSON_AddItemReferenceToObject(payload, "errors", MMLOADER_G(error_batch));

    char *body = cJSON_PrintUnformatted(payload);
    /* Detach reference before delete so the batch array is not freed */
    cJSON_DetachItemFromObjectCaseSensitive(payload, "errors");
    cJSON_Delete(payload);
    if (!body) return;

    CURL *c = curl_easy_init();
    if (c) {
        struct curl_slist *hdrs = NULL;
        hdrs = curl_slist_append(hdrs, "Content-Type: application/json");
        curl_easy_setopt(c, CURLOPT_URL, base_url);
        curl_easy_setopt(c, CURLOPT_POSTFIELDS, body);
        curl_easy_setopt(c, CURLOPT_POSTFIELDSIZE, (long)strlen(body));
        curl_easy_setopt(c, CURLOPT_HTTPHEADER, hdrs);
        curl_easy_setopt(c, CURLOPT_TIMEOUT_MS, 3000L);
        curl_easy_setopt(c, CURLOPT_CONNECTTIMEOUT_MS, 2000L);
        curl_easy_setopt(c, CURLOPT_WRITEFUNCTION, mmloader_discard_write_cb);
#ifdef MMPROTECT_DEV_BUILD
        if (MMLOADER_G(dev_mode)) {
            curl_easy_setopt(c, CURLOPT_SSL_VERIFYPEER, 0L);
            curl_easy_setopt(c, CURLOPT_SSL_VERIFYHOST, 0L);
        }
#endif
        curl_easy_perform(c); /* ignore result — fire and forget */
        curl_slist_free_all(hdrs);
        curl_easy_cleanup(c);
    }
    free(body);
}

PHP_MINIT_FUNCTION(mmloader)
{
    if (s_minit_done) return SUCCESS;
    s_minit_done = 1;

    /* Register globals constructor + destructor.
     * In ZTS: destructor is called by ts_allocate_id when a thread exits.
     * In NTS: destructor is ignored here and called manually in MSHUTDOWN. */
    ZEND_INIT_MODULE_GLOBALS(mmloader, php_mmloader_init_globals,
                             php_mmloader_shutdown_globals);
    REGISTER_INI_ENTRIES();

    /* Pre-compute HKDF salt */
    unsigned int salt_len = 0;
    if (!EVP_Digest("MMProtect-HKDF-v1", strlen("MMProtect-HKDF-v1"),
                    s_hkdf_salt, &salt_len, EVP_sha256(), NULL) || salt_len != 32) {
        php_error(E_CORE_ERROR, "MMENC: HKDF salt initialisation failed");
        return FAILURE;
    }

#ifdef MMPROTECT_DEV_BUILD
    /* Pre-compute demo signing key (dev build only) */
    unsigned int sk_len = 0;
    if (!EVP_Digest("mmprotect-dev-signing-key",
                    strlen("mmprotect-dev-signing-key"),
                    s_demo_signing_key, &sk_len, EVP_sha256(), NULL) || sk_len != 32) {
        php_error(E_CORE_ERROR, "MMENC: demo signing key initialisation failed");
        return FAILURE;
    }
#endif

    /* Compute machine fingerprint once */
    mmloader_compute_machine_fingerprint();

    /* Load ECDSA-P256 public key */
    const char *pubkey_path = MMLOADER_G(signing_public_key_file);
    if (pubkey_path && pubkey_path[0]) {
        FILE *kfp = fopen(pubkey_path, "r");
        if (kfp) {
            s_signing_public_key = PEM_read_PUBKEY(kfp, NULL, NULL, NULL);
            fclose(kfp);
            if (!s_signing_public_key) {
#ifdef MMPROTECT_DEV_BUILD
                php_error(E_CORE_WARNING,
                    "MMENC: failed to load signing public key from %s "
                    "(falling back to SHA-256 demo verification)", pubkey_path);
#else
                php_error(E_CORE_ERROR,
                    "MMENC: failed to load signing public key from %s "
                    "(release build cannot start without a valid signing key)", pubkey_path);
                return FAILURE;
#endif
            }
        } else {
#ifdef MMPROTECT_DEV_BUILD
            php_error(E_CORE_WARNING,
                "MMENC: cannot open signing_public_key_file: %s", pubkey_path);
#else
            php_error(E_CORE_ERROR,
                "MMENC: cannot open signing_public_key_file: %s "
                "(release build cannot start without a valid signing key)", pubkey_path);
            return FAILURE;
#endif
        }
#ifndef MMPROTECT_DEV_BUILD
    } else {
        /* Release build: signing key is mandatory */
        php_error(E_CORE_ERROR,
            "MMENC: mmloader.signing_public_key_file is not configured "
            "(required in release builds)");
        return FAILURE;
#endif
    }

    /* Initialise persistent protected-files set and MMENC1 magic cache */
    mm_protected_init(&MMLOADER_G(protected_files));
    mm_magic_cache_init(&MMLOADER_G(file_magic_cache));

    /* Global libcurl initialisation (not thread-safe; must run once here).
     * Per-thread handles are created lazily in RINIT. */
    if (curl_global_init(CURL_GLOBAL_ALL) != CURLE_OK) {
        php_error(E_CORE_ERROR, "MMENC: curl_global_init failed");
        return FAILURE;
    }

    /* Install engine hooks */
    s_orig_compile_file = mm_hook_compile_file(mmloader_compile_file);
    s_orig_execute_ex   = mm_hook_execute_ex(mmloader_execute_ex_hook);
    s_orig_error_cb     = zend_error_cb;
    zend_error_cb       = mmloader_error_cb;

    /* Xdebug compatibility notice: hooks chain correctly regardless of load
     * order, but encrypted files cannot be step-debugged. */
    if (zend_get_extension("Xdebug")) {
        php_error(E_CORE_WARNING,
            "MMENC mmloader: Xdebug detected. "
            "MMENC1-encrypted files cannot be step-debugged. "
            "Recommended load order: opcache, mmloader, xdebug.");
    }

    return SUCCESS;
}

PHP_MSHUTDOWN_FUNCTION(mmloader)
{
    /* Restore engine hooks */
    mm_unhook_compile_file(s_orig_compile_file); s_orig_compile_file = NULL;
    mm_unhook_execute_ex(s_orig_execute_ex);     s_orig_execute_ex   = NULL;
    if (s_orig_error_cb) { zend_error_cb = s_orig_error_cb; s_orig_error_cb = NULL; }

    /* Release globals stored in the current-thread's / process's storage.
     *
     * NTS: ZEND_INIT_MODULE_GLOBALS ignores the dtor, so we clean up here.
     * ZTS: MSHUTDOWN runs on the main thread; MMLOADER_G() accesses that
     *      thread's storage.  php_mmloader_shutdown_globals is registered as
     *      a ts_allocate_id dtor, so worker threads are cleaned up later by
     *      tsrm_shutdown() → the per-thread dtor. */
    if (MMLOADER_G(curl_handle)) {
        curl_easy_cleanup(MMLOADER_G(curl_handle));
        MMLOADER_G(curl_handle) = NULL;
    }
    if (MMLOADER_G(cached_build_id)) {
        pefree(MMLOADER_G(cached_build_id), 1);
        MMLOADER_G(cached_build_id) = NULL;
    }
    ZEND_SECURE_ZERO(MMLOADER_G(cached_runtime_key),
                     sizeof(MMLOADER_G(cached_runtime_key)));
    MMLOADER_G(has_cached_key) = 0;
    mm_protected_destroy(&MMLOADER_G(protected_files));
    mm_magic_cache_destroy(&MMLOADER_G(file_magic_cache));
    mm_features_destroy(&MMLOADER_G(lease_features));

    if (s_signing_public_key) {
        EVP_PKEY_free(s_signing_public_key);
        s_signing_public_key = NULL;
    }

    curl_global_cleanup();

    /* Zero static (process-wide) secrets */
    ZEND_SECURE_ZERO(s_hkdf_salt,           sizeof(s_hkdf_salt));
#ifdef MMPROTECT_DEV_BUILD
    ZEND_SECURE_ZERO(s_demo_signing_key,    sizeof(s_demo_signing_key));
#endif
    ZEND_SECURE_ZERO(s_machine_fingerprint, sizeof(s_machine_fingerprint));

    UNREGISTER_INI_ENTRIES();
    s_minit_done = 0;
    return SUCCESS;
}

PHP_RINIT_FUNCTION(mmloader)
{
#if defined(ZTS) && defined(COMPILE_DL_MMLOADER)
    ZEND_TSRMLS_CACHE_UPDATE();
#endif
    /* Initialise per-thread CURL handle on first request.
     * curl_global_init() was already called in MINIT (process-wide, once).
     * curl_easy_init() is per-thread safe and runs here so ZTS worker threads
     * each get their own handle without MINIT needing to know about threads. */
    if (MMLOADER_G(enabled) && !MMLOADER_G(curl_handle)) {
        MMLOADER_G(curl_handle) = curl_easy_init();
        if (!MMLOADER_G(curl_handle)) {
            php_error_docref(NULL, E_WARNING,
                "MMENC: curl_easy_init failed — lease requests will not work");
        }
    }
#ifdef MMPROTECT_DEV_BUILD
    if (MMLOADER_G(dev_mode) && !MMLOADER_G(dev_mode_warned)) {
        php_error_docref(NULL, E_WARNING,
            "MMENC mmloader: dev_mode is enabled — do not use in production");
        MMLOADER_G(dev_mode_warned) = 1;
    }
#endif
    /* Initialise per-request error batch (freed / sent in RSHUTDOWN) */
    if (MMLOADER_G(error_reporting_enabled) && !MMLOADER_G(error_batch)) {
        MMLOADER_G(error_batch)       = cJSON_CreateArray();
        MMLOADER_G(error_batch_count) = 0;
    }

    /* Proactive lease refresh: within lease_refresh_threshold_pct % of TTL,
     * evict the RAM cache so the next require triggers a fresh HTTP lease. */
    if (MMLOADER_G(has_cached_key) && MMLOADER_G(cached_lease_expires) > 0) {
        time_t now       = time(NULL);
        time_t ttl       = MMLOADER_G(cached_lease_expires) - now;
        zend_long pct    = MMLOADER_G(lease_refresh_threshold_pct);
        time_t threshold = MMLOADER_G(lease_refresh_seconds) * (pct > 0 ? pct : 10) / 100;
        if (ttl > 0 && ttl < threshold) {
            MMLOADER_G(cached_lease_expires) = 0;
            MMLOADER_G(has_cached_key)       = 0;
        }
    }
    return SUCCESS;
}

PHP_RSHUTDOWN_FUNCTION(mmloader)
{
    /* Send collected error batch (fire-and-forget) then free it */
    if (MMLOADER_G(error_batch)) {
        if (MMLOADER_G(error_batch_count) > 0)
            mmloader_send_error_batch();
        cJSON_Delete(MMLOADER_G(error_batch));
        MMLOADER_G(error_batch)       = NULL;
        MMLOADER_G(error_batch_count) = 0;
    }
    /* The CURL handle is kept alive across requests; freed in MSHUTDOWN. */
    return SUCCESS;
}

PHP_MINFO_FUNCTION(mmloader)
{
    php_info_print_table_start();
    php_info_print_table_header(2, "MMProtect Loader", "enabled");
    php_info_print_table_row(2, "Version", PHP_MMLOADER_VERSION);
    php_info_print_table_row(2, "Magic",
        MMLOADER_G(protected_magic) ? MMLOADER_G(protected_magic) : "MMENC1");
#ifdef MMPROTECT_DEV_BUILD
    php_info_print_table_row(2, "Build", "dev");
    php_info_print_table_row(2, "Dev mode",
        MMLOADER_G(dev_mode) ? "on" : "off");
#else
    php_info_print_table_row(2, "Build", "release");
#endif
    php_info_print_table_row(2, "Lease cache",
        MMLOADER_G(has_cached_key) ? "populated" : "empty");
    php_info_print_table_row(2, "Fingerprint",
        s_machine_fingerprint[0] ? s_machine_fingerprint : "(not computed)");
#ifdef MMPROTECT_DEV_BUILD
    php_info_print_table_row(2, "Signing",
        s_signing_public_key ? "ECDSA-P256" : "SHA-256 demo (no key configured)");
#else
    php_info_print_table_row(2, "Signing", "ECDSA-P256");
#endif
    php_info_print_table_row(2, "execute_ex hook", "active");
    php_info_print_table_end();
    DISPLAY_INI_ENTRIES();
}

/* ====================================================================
 * PHP userland function: mmprotect_has_feature(string $feature): bool
 *
 * Returns true when the current lease grants the named feature.
 * Returns false when the extension is disabled, no lease is active,
 * or the feature is not in the lease's feature list.
 * ==================================================================== */

PHP_FUNCTION(mmprotect_has_feature)
{
    char   *feature;
    size_t  feature_len;

    ZEND_PARSE_PARAMETERS_START(1, 1)
        Z_PARAM_STRING(feature, feature_len)
    ZEND_PARSE_PARAMETERS_END();

    RETURN_BOOL(mm_features_has(MMLOADER_G(lease_features), feature, feature_len));
}

ZEND_BEGIN_ARG_WITH_RETURN_TYPE_INFO_EX(arginfo_mmprotect_has_feature, 0, 1, _IS_BOOL, 0)
    ZEND_ARG_TYPE_INFO(0, feature, IS_STRING, 0)
ZEND_END_ARG_INFO()

/* ====================================================================
 * PHP userland function: mmprotect_license_info(): array|false
 *
 * Returns an associative array with the current license metadata from
 * the active lease. Returns false when no lease is active.
 *
 * Keys returned:
 *   licenseId      — license UID from the server
 *   buildId        — current build UID
 *   customerId     — customer UID
 *   projectId      — project UID
 *   validFrom      — ISO-8601 UTC: when the license became valid
 *   validUntil     — ISO-8601 UTC: when the license expires (absent if unlimited)
 *   licenseServer  — URL of the license server used for the current lease
 *   leaseExpiresAt — ISO-8601 UTC: when the current lease must be renewed
 *   features       — list of feature strings granted by the license
 * ==================================================================== */
PHP_FUNCTION(mmprotect_license_info)
{
    ZEND_PARSE_PARAMETERS_NONE();

    if (!MMLOADER_G(has_cached_key)) {
        RETURN_FALSE;
    }

    array_init(return_value);

#define MM_ADD_STR(key, field) do { \
    if (MMLOADER_G(field)) \
        add_assoc_string(return_value, key, MMLOADER_G(field)); \
} while(0)
    MM_ADD_STR("licenseId",     cached_license_id);
    MM_ADD_STR("buildId",       cached_build_id);
    MM_ADD_STR("customerId",    cached_customer_id);
    MM_ADD_STR("projectId",     cached_project_id);
    MM_ADD_STR("validFrom",     cached_valid_from);
    MM_ADD_STR("validUntil",    cached_valid_until);
    MM_ADD_STR("licenseServer", cached_effective_server);
#undef MM_ADD_STR

    if (MMLOADER_G(cached_lease_expires) > 0) {
        char ts[32];
        time_t exp = MMLOADER_G(cached_lease_expires);
        struct tm *t = gmtime(&exp);
        if (t) {
            strftime(ts, sizeof(ts), "%Y-%m-%dT%H:%M:%SZ", t);
            add_assoc_string(return_value, "leaseExpiresAt", ts);
        }
    }

    /* Build features array from the in-memory feature set */
    zval features_arr;
    array_init(&features_arr);
    if (MMLOADER_G(lease_features)) {
        zend_string *feat_key;
        zval *feat_val;
        ZEND_HASH_FOREACH_STR_KEY_VAL(MMLOADER_G(lease_features), feat_key, feat_val) {
            (void)feat_val;
            if (feat_key)
                add_next_index_string(&features_arr, ZSTR_VAL(feat_key));
        } ZEND_HASH_FOREACH_END();
    }
    add_assoc_zval(return_value, "features", &features_arr);
}

ZEND_BEGIN_ARG_WITH_RETURN_TYPE_MASK_EX(arginfo_mmprotect_license_info, 0, 0, MAY_BE_ARRAY|MAY_BE_FALSE)
ZEND_END_ARG_INFO()

static const zend_function_entry mmloader_functions[] = {
    PHP_FE(mmprotect_has_feature,  arginfo_mmprotect_has_feature)
    PHP_FE(mmprotect_license_info, arginfo_mmprotect_license_info)
    PHP_FE_END
};

zend_module_entry mmloader_module_entry = {
    STANDARD_MODULE_HEADER,
    "mmloader",
    mmloader_functions,
    PHP_MINIT(mmloader),
    PHP_MSHUTDOWN(mmloader),
    PHP_RINIT(mmloader),
    PHP_RSHUTDOWN(mmloader),
    PHP_MINFO(mmloader),
    PHP_MMLOADER_VERSION,
    STANDARD_MODULE_PROPERTIES
};

#ifdef COMPILE_DL_MMLOADER
# ifdef ZTS
ZEND_TSRMLS_CACHE_DEFINE()
# endif
ZEND_GET_MODULE(mmloader)
#endif

/* ====================================================================
 * Week 4: Zend extension entry
 *
 * Exporting this symbol allows loading with:
 *   zend_extension=mmloader.so
 *
 * Required php.ini load order:
 *   zend_extension=opcache.so   ; must come first
 *   zend_extension=mmloader.so  ; mmloader wraps opcache's compile hook
 *
 * The startup callback registers the PHP module (INI handling, MINIT, etc.).
 * ==================================================================== */

static int mmloader_zext_startup(zend_extension *extension)
{
    /* Register and start the PHP module so INI entries and MINIT run */
    return zend_startup_module(&mmloader_module_entry);
}

ZEND_EXT_API zend_extension zend_extension_entry = {
    "mmloader",
    PHP_MMLOADER_VERSION,
    "MMProtect",
    "",
    "Copyright 2026 MMProtect",
    mmloader_zext_startup,
    NULL,  /* shutdown */
    NULL,  /* activate */
    NULL,  /* deactivate */
    NULL,  /* message_handler */
    NULL,  /* op_array_handler */
    NULL,  /* statement_handler */
    NULL,  /* fcall_begin_handler */
    NULL,  /* fcall_end_handler */
    NULL,  /* op_array_ctor */
    NULL,  /* op_array_dtor */
    STANDARD_ZEND_EXTENSION_PROPERTIES
};
