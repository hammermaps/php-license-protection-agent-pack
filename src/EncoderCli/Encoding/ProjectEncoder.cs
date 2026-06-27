using MmProtect.EncoderCli.Configuration;
using MmProtect.EncoderCli.Server;
using System.Text.Json;

namespace MmProtect.EncoderCli.Encoding;

public sealed class ProjectEncoder
{
    private readonly LicenseServerClient _client;

    public ProjectEncoder(LicenseServerClient client)
    {
        _client = client;
    }

    public async Task EncodeAsync(EncoderConfig config, ProjectOptions project, bool verbose)
    {
        var sourceRoot = Path.GetFullPath(project.SourceRoot);
        var outputRoot = Path.GetFullPath(project.OutputRoot);

        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException(sourceRoot);

        Directory.CreateDirectory(outputRoot);

        Console.WriteLine($"Projekt: {project.ProjectKey}");
        Console.WriteLine($"Quelle:  {sourceRoot}");
        Console.WriteLine($"Ziel:    {outputRoot}");

        var customer = await _client.UpsertCustomerAsync(new
        {
            project.Customer.ExternalCustomerRef,
            project.Customer.Name,
            project.Customer.Email,
            project.Customer.Notes
        });

        var serverProject = await _client.UpsertProjectAsync(new
        {
            projectKey = project.ProjectKey,
            name = project.Name,
            phpMinVersion = config.Defaults.PhpMinVersion,
            description = project.Name
        });

        var license = await _client.UpsertLicenseAsync(new
        {
            customerId = customer.CustomerId,
            projectId = serverProject.ProjectId,
            licenseKey = project.License.LicenseKey,
            validFrom = project.License.ValidFrom,
            validUntil = project.License.ValidUntil,
            maxActivations = project.License.MaxActivations,
            features = project.License.Features
        });

        var build = await _client.StartBuildAsync(new
        {
            projectId = serverProject.ProjectId,
            customerId = customer.CustomerId,
            licenseId = license.LicenseId,
            version = project.Version,
            sourceRevision = project.SourceRevision,
            encoderVersion = typeof(ProjectEncoder).Assembly.GetName().Version?.ToString() ?? "dev"
        });

        // Load .mmignore files from the source tree (cascading)
        var mmIgnoreFile = config.Defaults.MmIgnoreFile;
        var mmIgnore = MmIgnoreRuleSet.LoadFromSourceRoot(sourceRoot, mmIgnoreFile);

        List<string> files;
        if (mmIgnore.HasRules)
        {
            // .mmignore-driven selection: combine with config include/exclude/copyPlain
            var selected = FileSelector.SelectFilesWithMmIgnore(
                sourceRoot, mmIgnore,
                project.Include.Count > 0 ? project.Include : null,
                project.Exclude.Count > 0 ? project.Exclude : null,
                project.CopyPlain.Count > 0 ? project.CopyPlain : null);

            foreach (var (absPath, action) in selected.Where(f => f.Action == FileAction.CopyPlain))
            {
                var rel = Path.GetRelativePath(sourceRoot, absPath);
                var target = Path.Combine(outputRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(absPath, target, overwrite: true);
                if (verbose) Console.WriteLine($"copied: {rel.Replace('\\', '/')}");
            }

            files = selected
                .Where(f => f.Action == FileAction.Encode)
                .Where(f => string.Equals(Path.GetExtension(f.AbsPath), ".php", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.AbsPath)
                .ToList();
        }
        else
        {
            // Legacy glob-based selection (config include/exclude/copyPlain)
            CopyPlainFiles(sourceRoot, outputRoot, project.CopyPlain, project.Exclude, verbose);
            files = FileSelector.SelectFiles(sourceRoot, project.Include, project.Exclude)
                .Where(p => string.Equals(Path.GetExtension(p), ".php", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var buildKey = Convert.FromBase64String(build.BuildKey);
        var manifestFiles = new List<ManifestFileDto>();
        // Pending list for second write pass after manifest hash is known
        var pendingFiles = new List<(string OutPath, MmencHeader Header, byte[] Ciphertext)>();

        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');

            // Optional PHP syntax check before encryption
            if (!string.IsNullOrWhiteSpace(config.Defaults.PhpBinary))
                PhpLint(config.Defaults.PhpBinary, path, relative);

            var plain = await File.ReadAllBytesAsync(path);
            if (config.Defaults.Obfuscate)
            {
                var text = System.Text.Encoding.UTF8.GetString(plain);
                text = PhpObfuscator.Obfuscate(text);
                plain = System.Text.Encoding.UTF8.GetBytes(text);
            }
            var fileId = "file_" + Hashing.ShortSha256(relative);
            var pathHash = "sha256:" + Hashing.Sha256Hex(relative);
            var plainHash = "sha256:" + Hashing.Sha256Hex(plain);
            var fileKey = CryptoPrimitives.HkdfSha256(buildKey, $"{build.BuildId}:{fileId}:{pathHash}", 32);

            var fileHeader = new MmencHeader
            {
                Format = "MMENC1",
                FormatVersion = 1,
                ProjectId = serverProject.ProjectId,
                CustomerId = customer.CustomerId,
                LicenseId = license.LicenseId,
                BuildId = build.BuildId,
                FileId = fileId,
                RelativePath = relative,
                PathHash = pathHash,
                PlainHash = plainHash,
                Algorithm = config.Defaults.Algorithm,
                Compression = string.IsNullOrWhiteSpace(config.Defaults.Compression) || config.Defaults.Compression == "none"
                    ? null : config.Defaults.Compression,
                LicenseServer = string.IsNullOrWhiteSpace(config.LicenseServer.BaseUrl)
                    ? null : config.LicenseServer.BaseUrl.TrimEnd('/'),
                Kdf = "HKDF-SHA256",
                KeyId = build.KeyId,
                ManifestHash = "",   // placeholder — filled in second pass
                CreatedAt = DateTimeOffset.UtcNow
            };

            var encrypted = MmencContainer.Create(plain, fileKey, fileHeader,
                signingKeyFile: config.Defaults.Signing?.PrivateKeyFile);

            var outPath = Path.Combine(outputRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            // Don't write yet — defer until manifest hash is known
            pendingFiles.Add((outPath, fileHeader, encrypted.Ciphertext));

            manifestFiles.Add(new ManifestFileDto(
                fileId,
                relative,
                pathHash,
                plainHash,
                "sha256:" + Hashing.Sha256Hex(encrypted.Ciphertext),
                config.Defaults.Algorithm,
                "HKDF-SHA256"));

            if (verbose)
                Console.WriteLine($"encoded: {relative}");
        }

        await _client.RegisterFilesAsync(build.BuildId, new
        {
            files = manifestFiles.Select(f => new
            {
                fileId = f.FileId,
                relativePath = f.RelativePath,
                pathHash = f.PathHash,
                plainHash = f.PlainHash,
                cipherHash = f.CipherHash,
                algorithm = f.Algorithm,
                kdf = f.Kdf
            }).ToArray()
        });

        var manifest = new ManifestDto(
            "MMENC-MANIFEST-1",
            serverProject.ProjectId,
            customer.CustomerId,
            license.LicenseId,
            build.BuildId,
            project.Version,
            config.Defaults.PhpMinVersion,
            config.Defaults.Algorithm,
            "HKDF-SHA256",
            manifestFiles,
            "",
            "");

        // Hash over compact camelCase JSON (same format as the written manifest) with
        // empty manifestHash and signature fields so the hash is deterministic.
        var manifestHash = "sha256:" + Hashing.Sha256Hex(
            JsonSerializer.SerializeToUtf8Bytes(
                manifest with { ManifestHash = "", Signature = "" },
                JsonOptions.Compact));
        var sign = await _client.SignManifestAsync(build.BuildId, new
        {
            manifestHash,
            fileCount = manifestFiles.Count
        });

        manifest = manifest with
        {
            ManifestHash = manifestHash,
            Signature = sign.ManifestSignature
        };

        // Second pass: write all encrypted files with the correct manifestHash
        foreach (var (outPath, fileHeader, ciphertext) in pendingFiles)
        {
            fileHeader.ManifestHash = manifestHash;
            await File.WriteAllBytesAsync(outPath, MmencContainer.Assemble(fileHeader, ciphertext));
        }

        var protectDir = Path.Combine(outputRoot, ".mmprotect");
        Directory.CreateDirectory(protectDir);

        await File.WriteAllTextAsync(Path.Combine(protectDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions.Pretty));

        await File.WriteAllTextAsync(Path.Combine(protectDir, "license.json"),
            JsonSerializer.Serialize(new
            {
                format = "MMENC-LICENSE-1",
                licenseId = license.LicenseId,
                projectId = serverProject.ProjectId,
                customerId = customer.CustomerId,
                buildId = build.BuildId,
                licenseServer = config.LicenseServer.BaseUrl,
                features = project.License.Features
            }, JsonOptions.Pretty));

        if (config.Defaults.DevMode)
        {
            // Dev-only: write buildKey so the loader can decrypt without an HTTP lease.
            // NEVER enable in production.
            var devKeyPath = Path.Combine(protectDir, "dev-buildkey.b64");
            await File.WriteAllTextAsync(devKeyPath, build.BuildKey + "\n");
            Console.WriteLine($"[DEV] dev-buildkey.b64 geschrieben → {devKeyPath}");
        }

        Console.WriteLine($"Fertig. Geschützte Dateien: {manifestFiles.Count}");
    }

    private static void PhpLint(string phpBinary, string filePath, string relativePath)
    {
        string output;
        int exitCode;
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = phpBinary,
                Arguments = $"-l \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            exitCode = proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[WARN] PhpBinary '{phpBinary}' not found — skipping syntax check");
            return;
        }
        if (exitCode != 0)
            throw new InvalidOperationException($"PHP syntax error in '{relativePath}':\n{output.Trim()}");
    }

    private static void CopyPlainFiles(string sourceRoot, string outputRoot, List<string> copyPlain, List<string> exclude, bool verbose)
    {
        var files = FileSelector.SelectFiles(sourceRoot, copyPlain, exclude);
        foreach (var source in files)
        {
            var relative = Path.GetRelativePath(sourceRoot, source);
            var target = Path.Combine(outputRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            if (verbose)
                Console.WriteLine($"copied: {relative}");
        }
    }
}
