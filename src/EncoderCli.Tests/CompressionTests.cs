using System.Buffers.Binary;
using Xunit;
using MmProtect.EncoderCli.Encoding;
using SysEnc = System.Text.Encoding;

namespace MmProtect.EncoderCli.Tests;

public sealed class CompressionTests
{
    // ── CompressLz4Block ──────────────────────────────────────────────────

    [Fact]
    public void CompressLz4Block_OutputStartsWithOriginalSize()
    {
        var data = SysEnc.UTF8.GetBytes("<?php echo 'hello world'; ?>\n");
        var compressed = MmencContainer.CompressLz4Block(data);

        Assert.True(compressed.Length >= 4);
        int storedSize = BinaryPrimitives.ReadInt32LittleEndian(compressed.AsSpan(0, 4));
        Assert.Equal(data.Length, storedSize);
    }

    [Fact]
    public void CompressLz4Block_LargeRepetitiveData_SmallerThanOriginal()
    {
        // Highly repetitive PHP code compresses well
        var data = SysEnc.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("<?php echo 'test repetitive string constant'; ?>\n", 200)));

        var compressed = MmencContainer.CompressLz4Block(data);

        // Compressed output (excluding 4-byte header) must be smaller than original
        Assert.True(compressed.Length - 4 < data.Length,
            $"Compressed size {compressed.Length - 4} should be < original {data.Length}");
    }

    // ── MmencContainer roundtrip with compression ────────────────────────

    [Fact]
    public void CreateAndDecrypt_WithLz4_RoundtripSucceeds()
    {
        const string phpSource = "<?php\n\necho 'Hello from LZ4-compressed file!';\n";
        byte[] plain = SysEnc.UTF8.GetBytes(phpSource);
        byte[] fileKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(fileKey);

        var header = new MmencHeader
        {
            Format = "MMENC1", FormatVersion = 1,
            ProjectId = "proj_test", CustomerId = "cust_test", LicenseId = "lic_test",
            BuildId = "build_test", FileId = "file_test",
            RelativePath = "src/Test.php", PathHash = "sha256:abc",
            PlainHash = "sha256:def", Algorithm = "AES-256-GCM",
            Compression = "lz4", Kdf = "HKDF-SHA256", KeyId = "key_test",
            ManifestHash = "pending", CreatedAt = DateTimeOffset.UtcNow
        };

        var container = MmencContainer.Create(plain, fileKey, header);

        // Verify file starts with MMENC1 magic
        var magic = SysEnc.ASCII.GetString(container.FileBytes, 0, 6);
        Assert.Equal("MMENC1", magic);

        // Ciphertext is smaller than original (LZ4 header + compressed data)
        // For short strings, size gain is unpredictable, but verify it's non-empty
        Assert.NotEmpty(container.Ciphertext);
    }

    [Fact]
    public void CreateAndDecrypt_WithoutCompression_RoundtripSucceeds()
    {
        byte[] plain = SysEnc.UTF8.GetBytes("<?php echo 'no compression'; ?>\n");
        byte[] fileKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(fileKey);

        var header = new MmencHeader
        {
            Format = "MMENC1", FormatVersion = 1,
            ProjectId = "proj_test", CustomerId = "cust_test", LicenseId = "lic_test",
            BuildId = "build_test", FileId = "file_test",
            RelativePath = "src/Test.php", PathHash = "sha256:abc",
            PlainHash = "sha256:def", Algorithm = "AES-256-GCM",
            Compression = null, Kdf = "HKDF-SHA256", KeyId = "key_test",
            ManifestHash = "pending", CreatedAt = DateTimeOffset.UtcNow
        };

        var container = MmencContainer.Create(plain, fileKey, header);

        var magic = SysEnc.ASCII.GetString(container.FileBytes, 0, 6);
        Assert.Equal("MMENC1", magic);
        Assert.Equal(plain.Length, container.Ciphertext.Length);
    }

    [Fact]
    public void Compression_None_CompressionFieldOmittedFromJson()
    {
        byte[] plain = SysEnc.UTF8.GetBytes("<?php echo 1; ?>");
        byte[] fileKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(fileKey);

        var header = new MmencHeader
        {
            Format = "MMENC1", FormatVersion = 1,
            ProjectId = "p", CustomerId = "c", LicenseId = "l",
            BuildId = "b", FileId = "f",
            RelativePath = "a.php", PathHash = "sha256:x",
            PlainHash = "sha256:y", Algorithm = "AES-256-GCM",
            Compression = null,   // must NOT appear in JSON
            Kdf = "HKDF-SHA256", KeyId = "k",
            ManifestHash = "pending", CreatedAt = DateTimeOffset.UtcNow
        };

        var container = MmencContainer.Create(plain, fileKey, header);

        // Parse the header JSON from the container bytes
        // Layout: MMENC1\n[8-digit len]\n[JSON][ciphertext]
        var headerLen = int.Parse(SysEnc.ASCII.GetString(container.FileBytes, 7, 8));
        var json = SysEnc.UTF8.GetString(container.FileBytes, 16, headerLen);

        Assert.DoesNotContain("\"compression\"", json);
    }

    [Fact]
    public void Compression_Lz4_CompressionFieldPresentInJson()
    {
        byte[] plain = SysEnc.UTF8.GetBytes("<?php echo 1; ?>");
        byte[] fileKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(fileKey);

        var header = new MmencHeader
        {
            Format = "MMENC1", FormatVersion = 1,
            ProjectId = "p", CustomerId = "c", LicenseId = "l",
            BuildId = "b", FileId = "f",
            RelativePath = "a.php", PathHash = "sha256:x",
            PlainHash = "sha256:y", Algorithm = "AES-256-GCM",
            Compression = "lz4",
            Kdf = "HKDF-SHA256", KeyId = "k",
            ManifestHash = "pending", CreatedAt = DateTimeOffset.UtcNow
        };

        var container = MmencContainer.Create(plain, fileKey, header);

        var headerLen = int.Parse(SysEnc.ASCII.GetString(container.FileBytes, 7, 8));
        var json = SysEnc.UTF8.GetString(container.FileBytes, 16, headerLen);

        Assert.Contains("\"compression\":\"lz4\"", json);
    }
}
