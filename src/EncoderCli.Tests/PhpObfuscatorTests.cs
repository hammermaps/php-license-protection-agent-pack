using Xunit;
using MmProtect.EncoderCli.Encoding;

namespace MmProtect.EncoderCli.Tests;

public sealed class PhpObfuscatorTests
{
    // ── 1. Line comment // stripped ──────────────────────────────────────────

    [Fact]
    public void LineComment_Slashes_IsStripped()
    {
        const string src = "<?php\necho 'hi'; // this is a comment\necho 'bye';";
        var result = PhpObfuscator.Obfuscate(src);
        Assert.DoesNotContain("// this is a comment", result);
        Assert.Contains("echo 'hi'", result);
        Assert.Contains("echo 'bye'", result);
    }

    // ── 2. Block comment /* */ stripped ──────────────────────────────────────

    [Fact]
    public void BlockComment_IsStripped()
    {
        const string src = "<?php\necho /* skip this */ 'hi';";
        var result = PhpObfuscator.Obfuscate(src);
        Assert.DoesNotContain("skip this", result);
        Assert.Contains("echo", result);
        Assert.Contains("'hi'", result);
    }

    // ── 3. Doc comment /** */ stripped ───────────────────────────────────────

    [Fact]
    public void DocComment_IsStripped()
    {
        const string src = "<?php\n/**\n * Doc block\n * @param int $x\n */\nfunction foo() {}";
        var result = PhpObfuscator.Obfuscate(src);
        Assert.DoesNotContain("Doc block", result);
        Assert.DoesNotContain("@param", result);
        Assert.Contains("function foo()", result);
    }

    // ── 4. Hash comment # stripped ───────────────────────────────────────────

    [Fact]
    public void HashComment_IsStripped()
    {
        const string src = "<?php\n# hash comment\necho 1;";
        var result = PhpObfuscator.Obfuscate(src);
        Assert.DoesNotContain("hash comment", result);
        Assert.Contains("echo 1", result);
    }

    // ── 5. Comment marker inside single-quoted string is NOT stripped ─────────

    [Fact]
    public void CommentInsideSingleQuotedString_NotStripped()
    {
        const string src = "<?php\n$x = '// not a comment';\necho $x;";
        var result = PhpObfuscator.Obfuscate(src);
        Assert.Contains("// not a comment", result);
    }

    // ── 6. Multiple spaces/tabs collapsed to one space ────────────────────────

    [Fact]
    public void MultipleSpaces_CollapsedToOne()
    {
        const string src = "<?php\necho   'hello';\nfoo(  );";
        var result = PhpObfuscator.Obfuscate(src);
        Assert.DoesNotContain("   ", result);
        Assert.DoesNotContain("  ", result);
        Assert.Contains("echo 'hello'", result);
    }

    // ── 7. Multiple newlines collapsed to one ─────────────────────────────────

    [Fact]
    public void MultipleNewlines_CollapsedToOne()
    {
        const string src = "<?php\n\n\necho 'hi';\n\n\necho 'bye';";
        var result = PhpObfuscator.Obfuscate(src);
        Assert.DoesNotContain("\n\n", result);
        Assert.Contains("echo 'hi'", result);
        Assert.Contains("echo 'bye'", result);
    }

    // ── 8. User variable renamed ──────────────────────────────────────────────

    [Fact]
    public void UserVariable_IsRenamed()
    {
        // Single variable 'count' — alphabetically first (and only) → maps to _a
        const string src = "<?php\n$count = 1;\necho $count;";
        var result = PhpObfuscator.Obfuscate(src);
        Assert.DoesNotContain("$count", result);
        Assert.Contains("$_a", result);
    }

    // ── 9. Superglobals and reserved vars are NOT renamed ─────────────────────

    [Fact]
    public void Superglobals_NotRenamed()
    {
        const string src = "<?php\n$x = $_GET['id'];\n$y = $_POST['q'];\n$z = $this;\n$w = $GLOBALS['foo'];";
        var result = PhpObfuscator.Obfuscate(src);
        Assert.Contains("$_GET", result);
        Assert.Contains("$_POST", result);
        Assert.Contains("$this", result);
        Assert.Contains("$GLOBALS", result);
    }

    // ── 10. Variable inside double-quoted string is renamed ───────────────────

    [Fact]
    public void VariableInsideDoubleQuotedString_IsRenamed()
    {
        // 'name' is the only user variable → position 0 alphabetically → _a
        const string src = "<?php\n$name = 'Alice';\necho \"Hello $name\";";
        var result = PhpObfuscator.Obfuscate(src);
        Assert.DoesNotContain("$name", result);
        Assert.Contains("$_a", result);
        // The renamed variable should appear inside the string context too
        Assert.Contains("\"Hello $_a\"", result);
    }

    // ── 11. Variable inside single-quoted string is NOT renamed ───────────────

    [Fact]
    public void VariableInsideSingleQuotedString_NotRenamed()
    {
        const string src = "<?php\n$foo = 1;\necho '$foo is literal';";
        var result = PhpObfuscator.Obfuscate(src);
        // Inside single-quoted string the literal text must stay
        Assert.Contains("'$foo is literal'", result);
    }

    // ── 12. Variable-variable ($$dynamic) not renamed ─────────────────────────

    [Fact]
    public void VariableVariable_NotRenamed()
    {
        const string src = "<?php\n$key = 'foo';\n$$key = 'bar';";
        var result = PhpObfuscator.Obfuscate(src);
        // $$key must remain: second $ and its identifier are emitted verbatim
        Assert.Contains("$$key", result);
    }

    // ── 13. Nowdoc string emitted unchanged ───────────────────────────────────

    [Fact]
    public void NowdocString_EmittedUnchanged()
    {
        // Variables inside nowdoc must NOT be renamed; also whitespace preserved
        const string src = "<?php\n$out = <<<'EOT'\nHello   $world\nEOT;\necho $out;";
        var result = PhpObfuscator.Obfuscate(src);
        // Variable inside nowdoc should be unchanged
        Assert.Contains("$world", result);
        // Spaces inside nowdoc must be preserved
        Assert.Contains("Hello   $world", result);
    }

    // ── 14. Heredoc string: variables renamed ─────────────────────────────────

    [Fact]
    public void HeredocString_VariablesRenamed()
    {
        // 'value' is the only user var → position 0 alphabetically → _a
        const string src = "<?php\n$value = 42;\n$out = <<<EOT\nResult: $value\nEOT;\necho $out;";
        var result = PhpObfuscator.Obfuscate(src);
        // 'value' should be renamed to _a; 'out' should be renamed too (sorted: out < value → _a, _b)
        Assert.DoesNotContain("$value", result);
        // The heredoc body must still contain the renamed variable
        Assert.Contains("Result:", result);
    }

    // ── 15. Complete PHP snippet ──────────────────────────────────────────────

    [Fact]
    public void CompleteSnippet_CommentStripped_VarsRenamed_WhitespaceCollapsed()
    {
        const string src = """
            <?php
            /**
             * Greet a user.
             */
            class Greeter
            {
                // instance variable
                private string $message;

                public function __construct(string $greeting)
                {
                    $this->message = $greeting; // store it
                }

                public function greet(string $name): string
                {
                    /* format greeting */
                    return $this->message . ' ' . $name;
                }
            }
            """;

        var result = PhpObfuscator.Obfuscate(src);

        // No comments remain
        Assert.DoesNotContain("Greet a user", result);
        Assert.DoesNotContain("instance variable", result);
        Assert.DoesNotContain("store it", result);
        Assert.DoesNotContain("format greeting", result);

        // $this must NOT be renamed (protected)
        Assert.Contains("$this", result);

        // User variables $message, $greeting, $name must be renamed
        Assert.DoesNotContain("$message", result);
        Assert.DoesNotContain("$greeting", result);
        Assert.DoesNotContain("$name", result);

        // Structure still intact
        Assert.Contains("class Greeter", result);
        Assert.Contains("__construct", result);

        // No double spaces
        Assert.DoesNotContain("  ", result);
    }

    // ── Bonus: GenerateName bijective base-26 ─────────────────────────────────

    [Fact]
    public void GenerateName_ProducesCorrectSequence()
    {
        Assert.Equal("_a",  PhpObfuscator.GenerateName(0));
        Assert.Equal("_b",  PhpObfuscator.GenerateName(1));
        Assert.Equal("_z",  PhpObfuscator.GenerateName(25));
        Assert.Equal("_aa", PhpObfuscator.GenerateName(26));
        Assert.Equal("_ab", PhpObfuscator.GenerateName(27));
        Assert.Equal("_az", PhpObfuscator.GenerateName(51));
        Assert.Equal("_ba", PhpObfuscator.GenerateName(52));
    }

    // ── Bonus: comment inside double-quoted string NOT stripped ───────────────

    [Fact]
    public void CommentInsideDoubleQuotedString_NotStripped()
    {
        const string src = "<?php\n$x = \"some // text\";\necho $x;";
        var result = PhpObfuscator.Obfuscate(src);
        Assert.Contains("// text", result);
    }
}
