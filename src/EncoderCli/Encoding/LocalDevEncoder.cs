using MmProtect.EncoderCli.Configuration;
using System.Security.Cryptography;
using System.Text.Json;

namespace MmProtect.EncoderCli.Encoding;

#if MMPROTECT_DEV_BUILD
/// <summary>
/// Encodes a directory without a license server. Generates a local random build key
/// and writes dev-buildkey.b64 to the output .mmprotect/ directory.
///
/// FOR DEVELOPMENT AND TESTING ONLY — compiled out in release builds.
/// </summary>
public sealed class LocalDevEncoder
{
    public async Task EncodeAsync(
        string sourceRoot,
        string outputRoot,
        MmIgnoreRuleSet mmIgnore,
        SigningOptions? signing,
        bool verbose,
        bool dryRun,
        string? compression = null,
        string? licenseServerUrl = null,
        bool obfuscate = false,
        string? optimize = null)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        outputRoot = Path.GetFullPath(outputRoot);

        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Quellverzeichnis nicht gefunden: {sourceRoot}");

        var buildKey = RandomNumberGenerator.GetBytes(32);
        var buildId = "build_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var projectId = "proj_local_dev";
        var customerId = "cust_local_dev";
        var licenseId = "lic_local_dev";
        var keyId = "key_local_dev";

        Console.WriteLine($"[DEV] Quelle:  {sourceRoot}");
        Console.WriteLine($"[DEV] Ziel:    {outputRoot}");
        Console.WriteLine($"[DEV] BuildId: {buildId}");

        if (!dryRun)
            Directory.CreateDirectory(outputRoot);

        var files = FileSelector.SelectFilesWithMmIgnore(sourceRoot, mmIgnore);

        if (verbose)
            PrintPlan(sourceRoot, files);

        if (dryRun)
        {
            Console.WriteLine($"[DRY-RUN] Würde {files.Count(f => f.Action == FileAction.Encode)} Dateien verschlüsseln, " +
                              $"{files.Count(f => f.Action == FileAction.CopyPlain)} kopieren.");
            return;
        }

        var manifestFiles = new List<ManifestFileDto>();
        // Pending list for second write pass after manifest hash is known
        var pendingFiles = new List<(string OutPath, MmencHeader Header, byte[] Ciphertext)>();

        foreach (var (absPath, action) in files)
        {
            var rel = Path.GetRelativePath(sourceRoot, absPath).Replace('\\', '/');
            var outPath = Path.Combine(outputRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            if (action == FileAction.CopyPlain)
            {
                File.Copy(absPath, outPath, overwrite: true);
                if (verbose) Console.WriteLine($"copied:   {rel}");
                continue;
            }

            // action == Encode
            var plain = await File.ReadAllBytesAsync(absPath);
            var optimizePasses = PhpOptimizer.ParsePasses(optimize);
            if (optimizePasses != OptimizePasses.None)
            {
                var text = System.Text.Encoding.UTF8.GetString(plain);
                text = PhpOptimizer.Optimize(text, optimizePasses);
                plain = System.Text.Encoding.UTF8.GetBytes(text);
            }
            if (obfuscate)
            {
                var text = System.Text.Encoding.UTF8.GetString(plain);
                text = PhpObfuscator.Obfuscate(text);
                plain = System.Text.Encoding.UTF8.GetBytes(text);
            }
            var fileId = "file_" + Hashing.ShortSha256(rel);
            var pathHash = "sha256:" + Hashing.Sha256Hex(rel);
            var plainHash = "sha256:" + Hashing.Sha256Hex(plain);
            var fileKey = CryptoPrimitives.HkdfSha256(buildKey, $"{buildId}:{fileId}:{pathHash}", 32);

            var fileHeader = new MmencHeader
            {
                Format = "MMENC1",
                FormatVersion = 1,
                ProjectId = projectId,
                CustomerId = customerId,
                LicenseId = licenseId,
                BuildId = buildId,
                FileId = fileId,
                RelativePath = rel,
                PathHash = pathHash,
                PlainHash = plainHash,
                Algorithm = "AES-256-GCM",
                Compression = string.IsNullOrWhiteSpace(compression) || compression == "none"
                    ? null : compression,
                LicenseServer = string.IsNullOrWhiteSpace(licenseServerUrl)
                    ? null : licenseServerUrl.TrimEnd('/'),
                Kdf = "HKDF-SHA256",
                KeyId = keyId,
                ManifestHash = "",   // placeholder — filled in second pass
                CreatedAt = DateTimeOffset.UtcNow
            };

            var encrypted = MmencContainer.Create(plain, fileKey, fileHeader,
                signingKeyFile: signing?.PrivateKeyFile);

            // Don't write yet — defer until manifest hash is known
            pendingFiles.Add((outPath, fileHeader, encrypted.Ciphertext));
            manifestFiles.Add(new ManifestFileDto(
                fileId, rel, pathHash, plainHash,
                "sha256:" + Hashing.Sha256Hex(encrypted.Ciphertext),
                "AES-256-GCM", "HKDF-SHA256"));

            if (verbose) Console.WriteLine($"encoded:  {rel}");
        }

        // Write .mmprotect/ artefacts
        var protectDir = Path.Combine(outputRoot, ".mmprotect");
        Directory.CreateDirectory(protectDir);

        // dev-buildkey.b64 — loader can use this to decrypt without a server
        var devKeyPath = Path.Combine(protectDir, "dev-buildkey.b64");
        await File.WriteAllTextAsync(devKeyPath, Convert.ToBase64String(buildKey) + "\n");

        // Compute manifest hash from compact camelCase JSON with empty hash/sig fields
        var manifestForHash = new
        {
            format = "MMENC-MANIFEST-1",
            dev = true,
            projectId,
            customerId,
            licenseId,
            buildId,
            version = "0.0.0-dev",
            algorithm = "AES-256-GCM",
            kdf = "HKDF-SHA256",
            files = manifestFiles.Select(f => new
            {
                f.FileId, f.RelativePath, f.PathHash, f.PlainHash,
                f.CipherHash, f.Algorithm, f.Kdf
            }).ToList(),
            manifestHash = "",
            signature = ""
        };
        var manifestHashValue = "sha256:" + Hashing.Sha256Hex(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifestForHash, JsonOptions.Compact)));

        // Second pass: write files with the correct manifestHash
        foreach (var (outPath, fileHeader, ciphertext) in pendingFiles)
        {
            fileHeader.ManifestHash = manifestHashValue;
            await File.WriteAllBytesAsync(outPath, MmencContainer.Assemble(fileHeader, ciphertext));
        }

        // Minimal manifest (with actual hash)
        var manifest = new
        {
            format = "MMENC-MANIFEST-1",
            dev = true,
            projectId,
            customerId,
            licenseId,
            buildId,
            version = "0.0.0-dev",
            algorithm = "AES-256-GCM",
            kdf = "HKDF-SHA256",
            files = manifestFiles.Select(f => new
            {
                f.FileId, f.RelativePath, f.PathHash, f.PlainHash,
                f.CipherHash, f.Algorithm, f.Kdf
            }),
            manifestHash = manifestHashValue,
            signature = ""
        };

        await File.WriteAllTextAsync(
            Path.Combine(protectDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions.Pretty));

        await File.WriteAllTextAsync(
            Path.Combine(protectDir, "license.json"),
            JsonSerializer.Serialize(new
            {
                format = "MMENC-LICENSE-1",
                dev = true,
                licenseId,
                projectId,
                customerId,
                buildId,
                licenseServer = "",
                features = Array.Empty<string>()
            }, JsonOptions.Pretty));

        Console.WriteLine($"[DEV] dev-buildkey.b64 → {devKeyPath}");
        Console.WriteLine($"Fertig. Verschlüsselt: {manifestFiles.Count}, " +
                          $"Kopiert: {files.Count(f => f.Action == FileAction.CopyPlain)}");
    }

    private static void PrintPlan(string sourceRoot, List<(string AbsPath, FileAction Action)> files)
    {
        Console.WriteLine($"{"Aktion",-12} Datei");
        Console.WriteLine(new string('-', 60));
        foreach (var (abs, action) in files)
        {
            var rel = Path.GetRelativePath(sourceRoot, abs).Replace('\\', '/');
            var label = action switch
            {
                FileAction.Encode    => "[ENCODE]   ",
                FileAction.CopyPlain => "[PLAIN]    ",
                _                   => "[SKIP]     "
            };
            Console.WriteLine($"{label} {rel}");
        }
        Console.WriteLine(new string('-', 60));
    }
}
#endif // MMPROTECT_DEV_BUILD
