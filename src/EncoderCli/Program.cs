using MmProtect.EncoderCli;
using MmProtect.EncoderCli.Configuration;
using MmProtect.EncoderCli.Encoding;
using MmProtect.EncoderCli.Server;

var cli = CliArgs.Parse(args);

if (cli.Command is null)
{
    CliArgs.PrintUsage();
    return 2;
}

try
{
    switch (cli.Command)
    {
        case "validate":
        {
            var config = EncoderConfigLoader.Load(cli.ConfigPath);
            var project = config.GetProject(cli.ProjectKey, allowFirst: true);
            Console.WriteLine($"Config ok. Projekte: {config.Projects.Count}. Gewählt: {project.ProjectKey}");
            return 0;
        }

        case "encode":
        {
            var config = EncoderConfigLoader.Load(cli.ConfigPath);
            var project = config.GetProject(cli.ProjectKey, allowFirst: false);

            var apiKey = config.LicenseServer.ResolveApiKey();
            using var http = new HttpClient
            {
                BaseAddress = new Uri(config.LicenseServer.BaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(config.LicenseServer.TimeoutSeconds <= 0 ? 30 : config.LicenseServer.TimeoutSeconds)
            };

            var client = new LicenseServerClient(http, apiKey);
            var encoder = new ProjectEncoder(client);
            await encoder.EncodeAsync(config, project, cli.Verbose);
            return 0;
        }

        case "encode-dir":
        {
            var sourceDir = cli.SourceDir
                ?? throw new InvalidOperationException("--source <Verzeichnis> ist erforderlich.");
            var outputDir = cli.OutputDir
                ?? throw new InvalidOperationException("--output <Verzeichnis> ist erforderlich.");

            // Load .mmignore rule set from source tree
            var mmIgnore = MmIgnoreRuleSet.LoadFromSourceRoot(sourceDir, cli.MmIgnoreFile);

#if MMPROTECT_DEV_BUILD
            if (cli.DevMode || string.IsNullOrEmpty(GetConfigPathIfExists(cli)))
            {
                // === Dev mode: no license server ===
                if (!cli.DevMode)
                    Console.WriteLine("[WARN] Kein --config angegeben → Dev-Modus (kein License Server).");

                SigningOptions? signing = null;
                if (File.Exists(cli.ConfigPath))
                {
                    try
                    {
                        var cfg = EncoderConfigLoader.Load(cli.ConfigPath);
                        signing = cfg.Defaults.Signing;
                        // Also merge mmIgnoreFile from config
                        if (mmIgnore.HasRules == false && cfg.Defaults.MmIgnoreFile != null)
                            mmIgnore = MmIgnoreRuleSet.LoadFromSourceRoot(sourceDir, cfg.Defaults.MmIgnoreFile);
                    }
                    catch { /* config optional in dev mode */ }
                }

                var localEncoder = new LocalDevEncoder();
                await localEncoder.EncodeAsync(
                    sourceDir, outputDir, mmIgnore, signing,
                    cli.Verbose, cli.DryRun,
                    compression: cli.Compress,
                    licenseServerUrl: cli.LicenseServerUrl,
                    obfuscate: cli.Obfuscate);
            }
            else
#endif
            {
                // === Production mode: use license server from config ===
                var config = EncoderConfigLoader.Load(cli.ConfigPath);
                var project = config.GetProject(cli.ProjectKey, allowFirst: false);

                // Command-line --source / --output override the config values
                project.SourceRoot = Path.GetFullPath(sourceDir);
                project.OutputRoot = Path.GetFullPath(outputDir);

                // Merge global .mmignore from CLI flag into config
                if (cli.MmIgnoreFile != null)
                    config.Defaults.MmIgnoreFile = cli.MmIgnoreFile;

                // --compress CLI flag overrides config value
                if (cli.Compress != null)
                    config.Defaults.Compression = cli.Compress;

                // --obfuscate CLI flag overrides config value
                if (cli.Obfuscate)
                    config.Defaults.Obfuscate = true;

                // --license-server CLI flag overrides the config URL embedded in headers
                if (cli.LicenseServerUrl != null)
                    config.LicenseServer.BaseUrl = cli.LicenseServerUrl;

                var apiKey = config.LicenseServer.ResolveApiKey();
                using var http = new HttpClient
                {
                    BaseAddress = new Uri(config.LicenseServer.BaseUrl.TrimEnd('/') + "/"),
                    Timeout = TimeSpan.FromSeconds(config.LicenseServer.TimeoutSeconds <= 0 ? 30 : config.LicenseServer.TimeoutSeconds)
                };

                var client = new LicenseServerClient(http, apiKey);
                var encoder = new ProjectEncoder(client);
                await encoder.EncodeAsync(config, project, cli.Verbose);
            }

            return 0;
        }

        case "manifest":
        {
            var config = EncoderConfigLoader.Load(cli.ConfigPath);
            var project = config.GetProject(cli.ProjectKey, allowFirst: false);
            var manifestPath = Path.Combine(project.OutputRoot, ".mmprotect", "manifest.json");
            Console.WriteLine(File.Exists(manifestPath)
                ? File.ReadAllText(manifestPath)
                : $"Manifest nicht gefunden: {manifestPath}");
            return File.Exists(manifestPath) ? 0 : 1;
        }

        case "clean":
        {
            var config = EncoderConfigLoader.Load(cli.ConfigPath);
            var project = config.GetProject(cli.ProjectKey, allowFirst: false);
            if (Directory.Exists(project.OutputRoot))
                Directory.Delete(project.OutputRoot, recursive: true);
            Console.WriteLine($"Gelöscht: {project.OutputRoot}");
            return 0;
        }

        default:
            Console.Error.WriteLine($"Unbekannter Befehl: {cli.Command}");
            CliArgs.PrintUsage();
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    if (cli.Verbose)
        Console.Error.WriteLine(ex);
    return 1;
}

#if MMPROTECT_DEV_BUILD
static string? GetConfigPathIfExists(CliArgs cli)
    => File.Exists(cli.ConfigPath) ? cli.ConfigPath : null;
#endif
