using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using K4os.Compression.LZ4;

namespace MmProtect.EncoderCli.Encoding;

public sealed class MmencContainer
{
    public required byte[] FileBytes { get; init; }
    public required byte[] Ciphertext { get; init; }

    /// <summary>
    /// Encrypt <paramref name="plain"/> with AES-256-GCM and assemble an MMENC1 container.
    /// </summary>
    /// <param name="signingKeyFile">
    ///   Week 4: path to ECDSA-P256 PEM private key for header signing.
    ///   Null = fall back to SHA-256 demo hash (loader accepts both).
    /// </param>
    /// <summary>
    /// Compress <paramref name="data"/> with LZ4-HC block format.
    /// Returns a buffer: [4-byte LE original size][LZ4 block data].
    /// </summary>
    public static byte[] CompressLz4Block(byte[] data)
    {
        int origSize = data.Length;
        int maxOut = LZ4Codec.MaximumOutputSize(origSize);
        byte[] buf = new byte[4 + maxOut];
        BinaryPrimitives.WriteInt32LittleEndian(buf, origSize);
        int compressedLen = LZ4Codec.Encode(data, 0, origSize, buf, 4, maxOut, LZ4Level.L09_HC);
        return buf[..(4 + compressedLen)];
    }

    public static MmencContainer Create(byte[] plain, byte[] fileKey, MmencHeader header,
                                         string? signingKeyFile = null)
    {
        // Compress before encryption when requested
        if (header.Compression == "lz4")
            plain = CompressLz4Block(plain);

        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];

        using (var aes = new AesGcm(fileKey, tag.Length))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        header.Nonce = Convert.ToBase64String(nonce);
        header.Tag = Convert.ToBase64String(tag);
        header.CipherHash = "sha256:" + Hashing.Sha256Hex(cipher);

        var sigData = System.Text.Encoding.UTF8.GetBytes(
            $"{header.BuildId}:{header.FileId}:{header.CipherHash}");

        if (!string.IsNullOrWhiteSpace(signingKeyFile) && File.Exists(signingKeyFile))
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ecdsa.ImportFromPem(File.ReadAllText(signingKeyFile));
            var sig = ecdsa.SignData(sigData, HashAlgorithmName.SHA256,
                                     DSASignatureFormat.Rfc3279DerSequence);
            header.Signature = Convert.ToBase64String(sig);
        }
        else
        {
            /* Demo fallback: SHA-256 hash (loader accepts when no pubkey is configured) */
            header.Signature = Convert.ToBase64String(SHA256.HashData(sigData));
        }

        return new MmencContainer
        {
            FileBytes = Assemble(header, cipher),
            Ciphertext = cipher
        };
    }

    /// <summary>
    /// Assembles an MMENC1 file from an already-signed/encrypted header and ciphertext.
    /// Used for the second write pass after the manifest hash is known.
    /// </summary>
    public static byte[] Assemble(MmencHeader header, byte[] ciphertext)
    {
        var headerJson = JsonSerializer.Serialize(header, JsonOptions.Compact);
        var headerBytes = System.Text.Encoding.UTF8.GetBytes(headerJson);
        var lengthLine = headerBytes.Length.ToString("D8");

        using var ms = new MemoryStream();
        ms.Write(System.Text.Encoding.ASCII.GetBytes("MMENC1\n"));
        ms.Write(System.Text.Encoding.ASCII.GetBytes(lengthLine));
        ms.WriteByte((byte)'\n');
        ms.Write(headerBytes);
        ms.Write(ciphertext);
        return ms.ToArray();
    }
}

public sealed class MmencHeader
{
    public string Format { get; set; } = "MMENC1";
    public int FormatVersion { get; set; } = 1;
    public string ProjectId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string LicenseId { get; set; } = "";
    public string BuildId { get; set; } = "";
    public string FileId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string PathHash { get; set; } = "";
    public string PlainHash { get; set; } = "";
    public string CipherHash { get; set; } = "";
    public string Algorithm { get; set; } = "AES-256-GCM";
    /// <summary>Optional compression before encryption. Null/absent = no compression. "lz4" = LZ4 block.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Compression { get; set; }
    /// <summary>
    /// License server base URL embedded per-file (e.g. "https://license.example.com").
    /// The loader uses this URL instead of the global mmloader.license_server INI setting.
    /// Null/absent = fall back to INI. Allows multiple license servers on one PHP instance.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LicenseServer { get; set; }
    public string Kdf { get; set; } = "HKDF-SHA256";
    public string KeyId { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Tag { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Signature { get; set; } = "";
}

public sealed record ManifestDto(
    string Format,
    string ProjectId,
    string CustomerId,
    string LicenseId,
    string BuildId,
    string Version,
    string PhpMinVersion,
    string Algorithm,
    string Kdf,
    List<ManifestFileDto> Files,
    string ManifestHash,
    string Signature);

public sealed record ManifestFileDto(
    string FileId,
    string RelativePath,
    string PathHash,
    string PlainHash,
    string CipherHash,
    string Algorithm,
    string Kdf);

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
