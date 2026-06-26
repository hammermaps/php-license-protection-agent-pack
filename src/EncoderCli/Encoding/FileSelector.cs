using System.Text.RegularExpressions;

namespace MmProtect.EncoderCli.Encoding;

public static class FileSelector
{
    /// <summary>
    /// Selects files matching <paramref name="include"/> patterns, minus <paramref name="exclude"/> patterns.
    /// Used by the config-driven <c>encode</c> command.
    /// </summary>
    public static List<string> SelectFiles(string root, List<string> include, List<string> exclude)
    {
        root = Path.GetFullPath(root);

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path =>
            {
                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                var included = include.Count == 0 || include.Any(p => Glob.IsMatch(rel, p));
                var excluded = exclude.Any(p => Glob.IsMatch(rel, p));
                return included && !excluded;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Selects files using .mmignore rules from the source tree, plus optional config-level
    /// include/exclude/copyPlain lists. Returns (absolutePath, action) pairs.
    /// Config rules are applied first; .mmignore rules in subdirectories can override them.
    /// </summary>
    public static List<(string AbsPath, FileAction Action)> SelectFilesWithMmIgnore(
        string root,
        MmIgnoreRuleSet mmIgnore,
        List<string>? configInclude = null,
        List<string>? configExclude = null,
        List<string>? configCopyPlain = null)
    {
        root = Path.GetFullPath(root);
        var results = new List<(string, FileAction)>();

        foreach (var absPath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Select(Path.GetFullPath)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(root, absPath).Replace('\\', '/');
            var isPhp = string.Equals(Path.GetExtension(absPath), ".php", StringComparison.OrdinalIgnoreCase);

            // 1. Config-level exclude (highest config priority — skip always)
            if (configExclude?.Any(p => Glob.IsMatch(rel, p)) == true) continue;

            // 2. .mmignore evaluation (cascading, inner rules win)
            var action = mmIgnore.Evaluate(rel, isPhp);

            // 3. Config-level include / copyPlain may override .mmignore defaults
            //    but NOT an explicit .mmignore Skip rule (Skip wins if mmIgnore decided)
            if (action == FileAction.Encode || action == FileAction.Skip)
            {
                if (configCopyPlain?.Any(p => Glob.IsMatch(rel, p)) == true)
                    action = FileAction.CopyPlain;
                else if (configInclude?.Count > 0 && !configInclude.Any(p => Glob.IsMatch(rel, p)))
                    action = FileAction.Skip; // not in config include list → skip
            }

            if (action != FileAction.Skip)
                results.Add((absPath, action));
        }

        return results;
    }
}

public static class Glob
{
    public static bool IsMatch(string path, string pattern)
    {
        path = path.Replace('\\', '/');
        pattern = pattern.Replace('\\', '/');

        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*\\*", "§DS§")
            .Replace("\\*", "[^/]*")
            .Replace("§DS§", ".*")
            .Replace("\\?", "[^/]") + "$";

        return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
