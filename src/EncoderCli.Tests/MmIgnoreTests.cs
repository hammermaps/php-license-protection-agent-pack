using Xunit;
using MmProtect.EncoderCli.Encoding;

namespace MmProtect.EncoderCli.Tests;

public sealed class MmIgnoreTests
{
    // ── MmIgnoreRuleSet.Evaluate ────────────────────────────────────────────

    [Theory]
    // vendor/ directory exclusion
    [InlineData("vendor/autoload.php",               "vendor/",           FileAction.Skip)]
    [InlineData("vendor/composer/ClassLoader.php",   "vendor/",           FileAction.Skip)]
    [InlineData("src/App/Application.php",           "vendor/",           FileAction.Encode)]
    // plain-copy
    [InlineData("public/index.php",                  "+ public/",         FileAction.CopyPlain)]
    [InlineData("composer.json",                     "+ composer.json",   FileAction.CopyPlain)]
    [InlineData("composer.lock",                     "+ composer.json",   FileAction.Skip)]    // no match → skip (non-php)
    // wildcard exclusion without slash → matches at any depth
    [InlineData("src/Foo.test.php",                  "*.test.php",        FileAction.Skip)]
    [InlineData("src/deep/Bar.test.php",             "*.test.php",        FileAction.Skip)]
    [InlineData("src/App.php",                       "*.test.php",        FileAction.Encode)]
    // path-anchored exclusion (contains slash)
    [InlineData("tests/Unit/FooTest.php",            "tests/",            FileAction.Skip)]
    [InlineData("src/tests/FooTest.php",             "tests/",            FileAction.Skip)]   // no slash prefix → matches anywhere
    // default: php=Encode, non-php=Skip
    [InlineData("src/App/Application.php",           "",                  FileAction.Encode)]
    [InlineData("public/logo.png",                   "",                  FileAction.Skip)]
    public void SingleRule_Evaluate(string relPath, string rawRule, FileAction expected)
    {
        var ruleSet = BuildRuleSet(rawRule);
        var isPhp = relPath.EndsWith(".php", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, ruleSet.Evaluate(relPath, isPhp));
    }

    [Fact]
    public void Negation_ReincludesExcludedFile()
    {
        // vendor/ excluded, but vendor/my-lib/ re-included
        var ruleSet = BuildRuleSet("vendor/", "!vendor/my-lib/");
        Assert.Equal(FileAction.Encode,    ruleSet.Evaluate("vendor/my-lib/Foo.php", isPhp: true));
        Assert.Equal(FileAction.Skip,      ruleSet.Evaluate("vendor/other/Bar.php",  isPhp: true));
    }

    [Fact]
    public void LastRuleWins()
    {
        // First rule says plain, second says exclude — exclude wins
        var ruleSet = BuildRuleSet("+ public/", "public/admin.php");
        Assert.Equal(FileAction.Skip,      ruleSet.Evaluate("public/admin.php",  isPhp: true));
        Assert.Equal(FileAction.CopyPlain, ruleSet.Evaluate("public/index.php",  isPhp: true));
    }

    // ── MmIgnoreRuleSet.LoadFromSourceRoot ──────────────────────────────────

    [Fact]
    public void Load_RootMmIgnore_AppliesGlobally()
    {
        using var tmp = new TempDir();
        tmp.WriteFile(".mmignore", "vendor/\n+ public/");
        tmp.WriteFile("vendor/autoload.php", "<?php");
        tmp.WriteFile("src/App.php", "<?php");
        tmp.WriteFile("public/index.php", "<?php");

        var rules = MmIgnoreRuleSet.LoadFromSourceRoot(tmp.Root);

        Assert.Equal(FileAction.Skip,      rules.Evaluate("vendor/autoload.php", isPhp: true));
        Assert.Equal(FileAction.Encode,    rules.Evaluate("src/App.php",          isPhp: true));
        Assert.Equal(FileAction.CopyPlain, rules.Evaluate("public/index.php",     isPhp: true));
    }

    [Fact]
    public void Load_SubdirMmIgnore_AppliesOnlyToSubtree()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("src/.mmignore", "Cache/\n+ Static/");
        tmp.WriteFile("src/App.php",          "<?php");
        tmp.WriteFile("src/Cache/Foo.php",    "<?php");
        tmp.WriteFile("src/Static/logo.png",  "data");
        tmp.WriteFile("other/Cache/Bar.php",  "<?php");  // NOT under src/ → unaffected

        var rules = MmIgnoreRuleSet.LoadFromSourceRoot(tmp.Root);

        Assert.Equal(FileAction.Encode,    rules.Evaluate("src/App.php",         isPhp: true));
        Assert.Equal(FileAction.Skip,      rules.Evaluate("src/Cache/Foo.php",   isPhp: true));
        Assert.Equal(FileAction.CopyPlain, rules.Evaluate("src/Static/logo.png", isPhp: false));
        Assert.Equal(FileAction.Encode,    rules.Evaluate("other/Cache/Bar.php", isPhp: true));
    }

    [Fact]
    public void Load_InnerMmIgnore_OverridesOuter()
    {
        using var tmp = new TempDir();
        // Root says: exclude vendor/
        tmp.WriteFile(".mmignore",              "vendor/");
        // But vendor/my-lib/.mmignore re-includes itself
        tmp.WriteFile("vendor/my-lib/.mmignore", "!*.php");

        var rules = MmIgnoreRuleSet.LoadFromSourceRoot(tmp.Root);

        Assert.Equal(FileAction.Skip,   rules.Evaluate("vendor/other/Foo.php",       isPhp: true));
        Assert.Equal(FileAction.Encode, rules.Evaluate("vendor/my-lib/MyClass.php",  isPhp: true));
    }

    [Fact]
    public void Load_GlobalMmIgnoreFile_AppliedFirst()
    {
        using var tmp = new TempDir();
        tmp.WriteFile(".mmignore", "+ public/");  // local: copy public plain
        var globalIgnore = tmp.WriteFile("global.mmignore", "public/");  // global: exclude public

        // Local rule wins (applied later)
        var rules = MmIgnoreRuleSet.LoadFromSourceRoot(tmp.Root, globalIgnore);
        Assert.Equal(FileAction.CopyPlain, rules.Evaluate("public/index.php", isPhp: true));
    }

    [Fact]
    public void EmptyRuleSet_UsesDefaults()
    {
        var rules = MmIgnoreRuleSet.Empty;
        Assert.Equal(FileAction.Encode, rules.Evaluate("src/App.php",   isPhp: true));
        Assert.Equal(FileAction.Skip,   rules.Evaluate("composer.json", isPhp: false));
        Assert.False(rules.HasRules);
    }

    // ── FileSelector.SelectFilesWithMmIgnore ───────────────────────────────

    [Fact]
    public void SelectFilesWithMmIgnore_RespectsRules()
    {
        using var tmp = new TempDir();
        tmp.WriteFile(".mmignore",              "vendor/\n+ public/");
        tmp.WriteFile("src/App.php",            "<?php");
        tmp.WriteFile("vendor/autoload.php",    "<?php");
        tmp.WriteFile("public/index.php",       "<?php");
        tmp.WriteFile("public/logo.png",        "data");

        var rules = MmIgnoreRuleSet.LoadFromSourceRoot(tmp.Root);
        var selected = FileSelector.SelectFilesWithMmIgnore(tmp.Root, rules);

        Assert.Contains(selected, f => f.AbsPath.EndsWith("App.php")    && f.Action == FileAction.Encode);
        Assert.Contains(selected, f => f.AbsPath.EndsWith("index.php")  && f.Action == FileAction.CopyPlain);
        Assert.Contains(selected, f => f.AbsPath.EndsWith("logo.png")   && f.Action == FileAction.CopyPlain);
        Assert.DoesNotContain(selected, f => f.AbsPath.EndsWith("autoload.php"));
    }

    [Fact]
    public void SelectFilesWithMmIgnore_ConfigExcludeAlwaysWins()
    {
        using var tmp = new TempDir();
        tmp.WriteFile(".mmignore",           "!src/Secret.php");  // .mmignore tries to include
        tmp.WriteFile("src/Secret.php",      "<?php");

        var rules = MmIgnoreRuleSet.LoadFromSourceRoot(tmp.Root);
        // But config says exclude src/Secret.php
        var selected = FileSelector.SelectFilesWithMmIgnore(
            tmp.Root, rules,
            configExclude: ["src/Secret.php"]);

        Assert.DoesNotContain(selected, f => f.AbsPath.EndsWith("Secret.php"));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static MmIgnoreRuleSet BuildRuleSet(params string[] lines)
    {
        using var tmp = new TempDir();
        var content = string.Join("\n", lines.Where(l => l.Length > 0));
        if (content.Length == 0) return MmIgnoreRuleSet.Empty;
        tmp.WriteFile(".mmignore", content);
        return MmIgnoreRuleSet.LoadFromSourceRoot(tmp.Root);
    }
}

/// <summary>Temporary directory that is deleted on Dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "mmtest_" + Path.GetRandomFileName());

    public TempDir() => Directory.CreateDirectory(Root);

    public string WriteFile(string rel, string content)
    {
        var abs = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
        return abs;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
