using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MmProtect.LicenseServer.Security;

public sealed class CryptoService
{
    private static readonly byte[] DemoSigningKey =
        SHA256.HashData(Encoding.UTF8.GetBytes("mmprotect-dev-signing-key"));

    private readonly ECDsa? _ecKey;
    private readonly byte[]? _kek; /* 32-byte key-encryption key for AES-256-GCM wrapping */

    public CryptoService(IConfiguration config)
    {
        /* ECDSA-P256 signing key */
        var keyFile = config["Security:SigningPrivateKeyFile"];
        if (!string.IsNullOrWhiteSpace(keyFile) && File.Exists(keyFile))
        {
            try
            {
                var pem = File.ReadAllText(keyFile);
                var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                ecdsa.ImportFromPem(pem);
                _ecKey = ecdsa;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[mmprotect] WARNING: could not load signing key from {keyFile}: {ex.Message}");
            }
        }

        /* Key-encryption key for AES-256-GCM build-key wrapping */
        var kekHex = config["Security:KeyEncryptionKey"];
        if (!string.IsNullOrWhiteSpace(kekHex))
        {
            try
            {
                _kek = Convert.FromHexString(kekHex.Trim());
                if (_kek.Length != 32)
                {
                    Console.Error.WriteLine(
                        "[mmprotect] WARNING: Security:KeyEncryptionKey must be exactly 32 bytes (64 hex chars) — ignoring");
                    _kek = null;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[mmprotect] WARNING: invalid Security:KeyEncryptionKey (must be hex): {ex.Message}");
            }
        }
    }

    /*
     * Wrap a 32-byte build key for storage.
     *
     * With KEK configured:
     *   AES-256-GCM: Base64(nonce[12] || ciphertext[32] || tag[16])
     *
     * Without KEK (dev fallback — never in production):
     *   "demo:" + base64_buildKey (plaintext, clearly marked)
     */
    public string ProtectBuildKey(string buildKey)
    {
        if (_kek == null)
            return "demo:" + buildKey;

        using var aes = new AesGcm(_kek, AesGcm.TagByteSizes.MaxSize);
        var nonce      = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plaintext  = Encoding.UTF8.GetBytes(buildKey);
        var ciphertext = new byte[plaintext.Length];
        var tag        = new byte[AesGcm.TagByteSizes.MaxSize];

        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var blob = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(blob, 0);
        ciphertext.CopyTo(blob, nonce.Length);
        tag.CopyTo(blob, nonce.Length + ciphertext.Length);

        return Convert.ToBase64String(blob);
    }

    /*
     * Unwrap a build key from storage.
     * Handles both the AES-256-GCM format and the "demo:" plaintext fallback.
     */
    public string UnprotectBuildKey(string encrypted)
    {
        if (encrypted.StartsWith("demo:", StringComparison.Ordinal))
            return encrypted[5..];

        if (_kek == null)
        {
            /* Can't decrypt without key — return as-is; caller will fail gracefully */
            Console.Error.WriteLine(
                "[mmprotect] WARNING: Security:KeyEncryptionKey not configured — cannot decrypt build key");
            return encrypted;
        }

        const int nonceLen = 12, tagLen = 16;
        var blob = Convert.FromBase64String(encrypted);
        if (blob.Length < nonceLen + tagLen)
            throw new CryptographicException("Encrypted build key blob is too short");

        var nonce      = blob[..nonceLen];
        var ciphertext = blob[nonceLen..^tagLen];
        var tag        = blob[^tagLen..];
        var plaintext  = new byte[ciphertext.Length];

        using var aes = new AesGcm(_kek, tagLen);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    /*
     * Sign data for lease responses and manifest hashes.
     *
     * With ECDSA key configured:
     *   ECDSA-P256-DER(SHA-256(data)), Base64-encoded
     *
     * Without key (dev fallback):
     *   HMAC-SHA256(demoKey, data), Base64-encoded
     */
    public string SignLease(string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);

        if (_ecKey != null)
        {
            var sig = _ecKey.SignData(bytes, HashAlgorithmName.SHA256,
                                      DSASignatureFormat.Rfc3279DerSequence);
            return Convert.ToBase64String(sig);
        }

        using var hmac = new HMACSHA256(DemoSigningKey);
        return Convert.ToBase64String(hmac.ComputeHash(bytes));
    }

    /* Whether a production key-encryption key is configured. */
    public bool HasKeyEncryptionKey => _kek != null;

    /* Whether a production signing key is configured. */
    public bool HasSigningKey => _ecKey != null;
}

public static class Ids
{
    public static string NewId() => Guid.NewGuid().ToString("N");
}

public static class JsonCanonical
{
    public static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
}
