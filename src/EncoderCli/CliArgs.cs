namespace MmProtect.EncoderCli;

public sealed class CliArgs
{
    public string? Command { get; private set; }

    // Shared flags
    public string ConfigPath { get; private set; } = "configs/encoder.config.json";
    public string? ProjectKey { get; private set; }
    public bool Verbose { get; private set; }

    // encode-dir specific flags
    public string? SourceDir { get; private set; }
    public string? OutputDir { get; private set; }
    public string? MmIgnoreFile { get; private set; }
    public bool DryRun { get; private set; }
    public bool DevMode { get; private set; }

    public static CliArgs Parse(string[] args)
    {
        var result = new CliArgs();
        if (args.Length > 0)
            result.Command = args[0].Trim().ToLowerInvariant();

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config":
                case "-c":
                    result.ConfigPath = args[++i];
                    break;
                case "--project":
                case "-p":
                    result.ProjectKey = args[++i];
                    break;
                case "--source":
                case "-s":
                    result.SourceDir = args[++i];
                    break;
                case "--output":
                case "-o":
                    result.OutputDir = args[++i];
                    break;
                case "--mmignore":
                    result.MmIgnoreFile = args[++i];
                    break;
                case "--dry-run":
                    result.DryRun = true;
                    break;
                case "--dev":
                    result.DevMode = true;
                    break;
                case "--verbose":
                case "-v":
                    result.Verbose = true;
                    break;
            }
        }

        return result;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
        Verwendung:

          mmencoder validate   --config <path> [--project <key>]
          mmencoder encode     --config <path> --project <key>
          mmencoder manifest   --config <path> --project <key>
          mmencoder clean      --config <path> --project <key>

          mmencoder encode-dir --source <dir> --output <dir>
                               [--config <path> --project <key>]
                               [--mmignore <file>]
                               [--dev]
                               [--dry-run]
                               [--verbose]

        encode-dir Modi:
          --dev                Lokaler Dev-Modus: zufälliger Build-Key, kein License Server.
                               Schreibt .mmprotect/dev-buildkey.b64 in den Output.
          --config + --project Produktionsmodus: License Server aus Config.
          --mmignore <file>    Globale .mmignore-Datei (gilt vor verzeichnislokalen Dateien).
          --dry-run            Nur anzeigen, was passieren würde. Nichts schreiben.

        .mmignore-Format (in jedem Quellverzeichnis):
          # Kommentar
          vendor/              Ausschließen (Verzeichnis und Inhalt)
          *.test.php           Ausschließen (überall im Teilbaum)
          + public/            Als Klartext kopieren (nicht verschlüsseln)
          + composer.json      Als Klartext kopieren
          !wichtig.php         Einschluss erzwingen (Ausschluss aufheben)
        """);
    }
}
