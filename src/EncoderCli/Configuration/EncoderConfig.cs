namespace MmProtect.EncoderCli.Configuration;

public sealed class EncoderConfig
{
    public LicenseServerOptions LicenseServer { get; set; } = new();
    public DefaultOptions Defaults { get; set; } = new();
    public List<ProjectOptions> Projects { get; set; } = [];

    public ProjectOptions GetProject(string? projectKey, bool allowFirst)
    {
        if (!string.IsNullOrWhiteSpace(projectKey))
        {
            return Projects.FirstOrDefault(p => string.Equals(p.ProjectKey, projectKey, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Projekt nicht gefunden: {projectKey}");
        }

        if (allowFirst && Projects.Count > 0)
            return Projects[0];

        throw new InvalidOperationException("--project fehlt.");
    }
}

public sealed class LicenseServerOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;

    public string ResolveApiKey()
    {
        if (ApiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var name = ApiKey[4..];
            return Environment.GetEnvironmentVariable(name)
                ?? throw new InvalidOperationException($"Environment Variable fehlt: {name}");
        }

        return ApiKey;
    }
}

public sealed class DefaultOptions
{
    public string PhpMinVersion { get; set; } = "8.4";
    public string Algorithm { get; set; } = "AES-256-GCM";
    public bool KeepPhpExtension { get; set; } = true;
    public bool WriteManifest { get; set; } = true;
    /// <summary>
    /// When true, writes the raw buildKey as Base64 to .mmprotect/dev-buildkey.b64
    /// so the Week-1 loader can decrypt without an HTTP lease call.
    /// NEVER enable in production — the build key must not leave the server.
    /// </summary>
    public bool DevMode { get; set; } = false;
    /// <summary>Week 4: ECDSA-P256 signing options. Null = SHA-256 demo hash.</summary>
    public SigningOptions? Signing { get; set; }

    /// <summary>
    /// Optional compression applied to PHP plaintext before AES-256-GCM encryption.
    /// Supported values: null/"none" (no compression, default) or "lz4" (LZ4 block, high compression).
    /// Reduces file size for text-heavy PHP code. Transparent to the loader (field stored in MMENC1 header).
    /// </summary>
    public string? Compression { get; set; }

    /// <summary>
    /// Optional path to a global .mmignore file applied before any directory-local .mmignore files.
    /// Useful to enforce project-wide exclusions independently of the source tree.
    /// </summary>
    public string? MmIgnoreFile { get; set; }

    /// <summary>When true, PHP source is obfuscated (comments stripped, vars renamed) before encryption.</summary>
    public bool Obfuscate { get; set; } = false;
}

/// <summary>
/// Week 4: Configuration for ECDSA-P256 file header signing.
/// The private key file must NOT be committed to version control.
/// </summary>
public sealed class SigningOptions
{
    /// <summary>Path to PEM-encoded ECDSA-P256 private key file.</summary>
    public string? PrivateKeyFile { get; set; }
    /// <summary>Path to PEM-encoded ECDSA-P256 public key file (for loader configuration).</summary>
    public string? PublicKeyFile { get; set; }
}

public sealed class ProjectOptions
{
    public string ProjectKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "0.1.0";
    public string? SourceRevision { get; set; }
    public string SourceRoot { get; set; } = "";
    public string OutputRoot { get; set; } = "";
    public CustomerOptions Customer { get; set; } = new();
    public LicenseOptions License { get; set; } = new();
    public List<string> Include { get; set; } = [];
    public List<string> Exclude { get; set; } = [];
    public List<string> CopyPlain { get; set; } = [];
}

public sealed class CustomerOptions
{
    public string ExternalCustomerRef { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public string? Notes { get; set; }
}

public sealed class LicenseOptions
{
    public string LicenseKey { get; set; } = "";
    public DateTimeOffset ValidFrom { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ValidUntil { get; set; }
    public int MaxActivations { get; set; } = 1;
    public string[] Features { get; set; } = [];
}
