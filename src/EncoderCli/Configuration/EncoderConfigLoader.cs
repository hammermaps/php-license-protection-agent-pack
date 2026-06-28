using System.Text.Json;
using System.Xml.Linq;

namespace MmProtect.EncoderCli.Configuration;

public static class EncoderConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static EncoderConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Config nicht gefunden.", path);

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".json" => LoadJson(path),
            ".xml" => LoadXml(path),
            _ => throw new InvalidOperationException($"Nicht unterstütztes Config-Format: {ext}")
        };
    }

    private static EncoderConfig LoadJson(string path)
    {
        var config = JsonSerializer.Deserialize<EncoderConfig>(File.ReadAllText(path), JsonOptions);
        return config ?? throw new InvalidOperationException("JSON Config konnte nicht gelesen werden.");
    }

    private static EncoderConfig LoadXml(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidOperationException("XML Root fehlt.");

        var server = root.Element("licenseServer") ?? throw new InvalidOperationException("licenseServer fehlt.");
        var defaultsEl = root.Element("defaults");

        var telEl = root.Element("telemetry");
        var config = new EncoderConfig
        {
            LicenseServer = new LicenseServerOptions
            {
                BaseUrl = server.Element("baseUrl")?.Value ?? "",
                ApiKey = server.Element("apiKey")?.Value ?? "",
                TimeoutSeconds = int.TryParse(server.Element("timeoutSeconds")?.Value, out var t) ? t : 30
            },
            Defaults = new DefaultOptions
            {
                PhpMinVersion = defaultsEl?.Attribute("phpMinVersion")?.Value ?? "8.4",
                Algorithm = defaultsEl?.Attribute("algorithm")?.Value ?? "AES-256-GCM",
                KeepPhpExtension = bool.TryParse(defaultsEl?.Attribute("keepPhpExtension")?.Value, out var keep) && keep,
                WriteManifest = !bool.TryParse(defaultsEl?.Attribute("writeManifest")?.Value, out var write) || write
            },
            Telemetry = new TelemetryOptions
            {
                Enabled     = bool.TryParse(telEl?.Attribute("enabled")?.Value, out var telEnabled) && telEnabled,
                EndpointUrl = telEl?.Attribute("endpointUrl")?.Value
            }
        };

        foreach (var p in root.Element("projects")?.Elements("project") ?? [])
        {
            var project = new ProjectOptions
            {
                ProjectKey = p.Attribute("projectKey")?.Value ?? "",
                Name = p.Attribute("name")?.Value ?? "",
                Version = p.Attribute("version")?.Value ?? "0.1.0",
                SourceRevision = p.Attribute("sourceRevision")?.Value,
                SourceRoot = p.Element("sourceRoot")?.Value ?? "",
                OutputRoot = p.Element("outputRoot")?.Value ?? "",
            };

            var c = p.Element("customer");
            if (c is not null)
            {
                project.Customer = new CustomerOptions
                {
                    ExternalCustomerRef = c.Attribute("externalCustomerRef")?.Value ?? "",
                    Name = c.Attribute("name")?.Value ?? "",
                    Email = c.Attribute("email")?.Value,
                    Notes = c.Element("notes")?.Value
                };
            }

            var l = p.Element("license");
            if (l is not null)
            {
                project.License = new LicenseOptions
                {
                    LicenseKey = l.Attribute("licenseKey")?.Value ?? "",
                    ValidFrom = DateTimeOffset.TryParse(l.Attribute("validFrom")?.Value, out var vf) ? vf : DateTimeOffset.UtcNow,
                    ValidUntil = DateTimeOffset.TryParse(l.Attribute("validUntil")?.Value, out var vu) ? vu : null,
                    MaxActivations = int.TryParse(l.Attribute("maxActivations")?.Value, out var ma) ? ma : 1,
                    Features = l.Elements("feature").Select(x => x.Value).ToArray()
                };
            }

            project.Include = ReadPatterns(p, "include");
            project.Exclude = ReadPatterns(p, "exclude");
            project.CopyPlain = ReadPatterns(p, "copyPlain");
            config.Projects.Add(project);
        }

        return config;
    }

    private static List<string> ReadPatterns(XElement root, string element)
        => root.Element(element)?.Elements("pattern").Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
}
