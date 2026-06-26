using System.Text.RegularExpressions;

namespace MmProtect.EncoderCli.Encoding;

/// <summary>
/// What the encoder should do with a file after .mmignore evaluation.
/// </summary>
public enum FileAction
{
    /// <summary>Encrypt the file and write to output (default for .php).</summary>
    Encode,
    /// <summary>Copy file verbatim to output without encryption.</summary>
    CopyPlain,
    /// <summary>Skip — do not include in output at all.</summary>
    Skip
}

/// <summary>
/// A single rule parsed from a .mmignore file.
/// </summary>
internal sealed record MmIgnoreRule(MmIgnoreAction Action, string Pattern);

internal enum MmIgnoreAction { Exclude, Plain, Negate }

/// <summary>
/// Loads and evaluates .mmignore files from a source directory tree.
///
/// .mmignore format (one rule per line):
///   # comment              — ignored
///   vendor/                — exclude vendor/ and all contents
///   *.test.php             — exclude matching files at any depth
///   src/Cache/             — exclude src/Cache/ and contents (path-rooted)
///   + public/              — copy public/ plain (not encrypted)
///   + composer.json        — copy composer.json plain
///   !important.php         — re-include (negate a previous exclusion)
///
/// Pattern semantics (same as .gitignore):
///   - No '/' in pattern   → matches file/dir name at any depth  (e.g. *.php → **/*.php)
///   - Trailing '/'        → directory match: everything under that directory
///   - Contains '/'        → path is anchored to the .mmignore's directory
///   - Leading '!'         → negate: restore default action for matching files
///   - Leading '+'         → copy plain: matched files are copied without encryption
///
/// Cascade: .mmignore files in subdirectories add rules that apply only to files
/// within their own directory tree. Rules from inner directories are evaluated after
/// (and can override) rules from outer directories.
/// </summary>
public sealed class MmIgnoreRuleSet
{
    // Each entry: (baseDir relative to sourceRoot, rules from the .mmignore in that dir)
    // baseDir "" = root, "src/App/" = the src/App/ subdirectory
    private readonly IReadOnlyList<(string BaseDir, IReadOnlyList<MmIgnoreRule> Rules)> _sections;

    private MmIgnoreRuleSet(IReadOnlyList<(string, IReadOnlyList<MmIgnoreRule>)> sections)
        => _sections = sections;

    /// <summary>
    /// Returns an empty rule set that applies no rules (default PHP=Encode, other=Skip).
    /// </summary>
    public static MmIgnoreRuleSet Empty { get; } = new([]);

    /// <summary>
    /// Loads all .mmignore files found in <paramref name="sourceRoot"/> and its subdirectories.
    /// Optionally a global .mmignore file can be specified (applied before all directory-local files).
    /// </summary>
    public static MmIgnoreRuleSet LoadFromSourceRoot(string sourceRoot, string? globalIgnoreFile = null)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        var sections = new List<(string, IReadOnlyList<MmIgnoreRule>)>();

        // 1. Global ignore file (explicit path, applies at root scope)
        if (!string.IsNullOrEmpty(globalIgnoreFile) && File.Exists(globalIgnoreFile))
            sections.Add(("", ParseFile(globalIgnoreFile)));

        // 2. .mmignore in source root itself
        var rootIgnore = Path.Combine(sourceRoot, ".mmignore");
        if (File.Exists(rootIgnore))
            sections.Add(("", ParseFile(rootIgnore)));

        // 3. .mmignore files in subdirectories (breadth-first so parent rules come first)
        foreach (var dir in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var ignoreFile = Path.Combine(dir, ".mmignore");
            if (!File.Exists(ignoreFile)) continue;

            var relDir = Path.GetRelativePath(sourceRoot, dir).Replace('\\', '/').TrimEnd('/') + "/";
            if (relDir == "./") relDir = "";

            // Don't duplicate root .mmignore
            if (string.IsNullOrEmpty(relDir) && sections.Any(s => s.Item1 == "")) continue;

            sections.Add((relDir, ParseFile(ignoreFile)));
        }

        return new MmIgnoreRuleSet(sections);
    }

    /// <summary>
    /// Determines what should happen to the file at <paramref name="relPath"/>
    /// (relative path from source root, forward slashes).
    /// </summary>
    public FileAction Evaluate(string relPath, bool isPhp)
    {
        FileAction? result = null;

        foreach (var (baseDir, rules) in _sections)
        {
            // This section only applies to files under baseDir
            if (baseDir.Length > 0 &&
                !relPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                continue;

            // Strip the section's base prefix to get path relative to the .mmignore
            var relToBase = baseDir.Length == 0 ? relPath : relPath[baseDir.Length..];

            foreach (var rule in rules)
            {
                if (!MatchesPath(relToBase, rule.Pattern)) continue;

                result = rule.Action switch
                {
                    MmIgnoreAction.Exclude => FileAction.Skip,
                    MmIgnoreAction.Plain   => FileAction.CopyPlain,
                    MmIgnoreAction.Negate  => isPhp ? FileAction.Encode : FileAction.CopyPlain,
                    _                      => result
                };
            }
        }

        // Default: encode .php, skip everything else
        return result ?? (isPhp ? FileAction.Encode : FileAction.Skip);
    }

    /// <summary>
    /// Returns true if any .mmignore file was found in the source tree.
    /// </summary>
    public bool HasRules => _sections.Count > 0;

    // ---- parsing ----

    private static IReadOnlyList<MmIgnoreRule> ParseFile(string path)
    {
        var rules = new List<MmIgnoreRule>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith('!'))
                rules.Add(new MmIgnoreRule(MmIgnoreAction.Negate, NormalisePattern(line[1..].TrimStart())));
            else if (line.StartsWith('+'))
                rules.Add(new MmIgnoreRule(MmIgnoreAction.Plain, NormalisePattern(line[1..].TrimStart())));
            else
                rules.Add(new MmIgnoreRule(MmIgnoreAction.Exclude, NormalisePattern(line)));
        }

        return rules;
    }

    private static string NormalisePattern(string pattern) => pattern.Replace('\\', '/').Trim();

    // ---- matching ----

    private static bool MatchesPath(string relPath, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        if (pattern.EndsWith('/'))
        {
            var dir = pattern.TrimEnd('/');

            if (dir.Contains('/'))
            {
                // Middle slash present → path is anchored (src/Cache/ only matches under src/Cache/)
                return relPath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)
                    || GlobMatch(relPath, dir + "/**");
            }
            else
            {
                // No middle slash (e.g. "vendor/", "tests/") → match at ANY depth, like .gitignore
                // Matches: "vendor/autoload.php", "src/tests/Foo.php", "a/b/tests/c.php"
                return relPath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)
                    || relPath.Contains("/" + dir + "/", StringComparison.OrdinalIgnoreCase)
                    || GlobMatch(relPath, "**/" + dir + "/**");
            }
        }

        // Pattern with '/' (non-trailing) → path-anchored to the .mmignore directory
        if (pattern.Contains('/'))
            return GlobMatch(relPath, pattern);

        // No '/' → match file/dir name at any depth (like .gitignore)
        return GlobMatch(relPath, "**/" + pattern) || GlobMatch(relPath, pattern);
    }

    private static bool GlobMatch(string path, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*\\*\\/", "(.*/)?")
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", "[^/]") + "$";

        return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
