using System.Text;

namespace MmProtect.EncoderCli.Encoding;

/// <summary>
/// Two-pass PHP source obfuscator.
/// Pass 1: scan with a state machine to collect all user-defined variable names.
/// Pass 2: re-scan with the same state machine, emitting transformed output:
///   - line comments (// and #) stripped
///   - block comments (/* ... */) stripped
///   - runs of spaces/tabs collapsed to a single space
///   - runs of newlines collapsed to a single newline
///   - user-defined variable names renamed to short names ($_a, $_b, ...)
///   - single-quoted strings and nowdoc strings emitted verbatim
///   - double-quoted strings and heredoc strings have variables renamed inside them
///   - variable-variables ($$foo) are emitted as-is
/// </summary>
public static class PhpObfuscator
{
    // Variable names that must never be renamed.
    private static readonly HashSet<string> ProtectedVars = new(StringComparer.Ordinal)
    {
        "this",
        "_GET", "_POST", "_REQUEST", "_SERVER", "_SESSION",
        "_COOKIE", "_FILES", "_ENV",
        "GLOBALS",
        "argc", "argv",
        "http_response_header", "php_errormsg",
        "_"
    };

    private enum ParseState
    {
        Code,
        LineComment,
        BlockComment,
        SingleQuotedString,
        DoubleQuotedString,
        HeredocString,
        NowdocString
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public static string Obfuscate(string phpSource)
    {
        // Pass 1: collect variable names
        var varNames = CollectVariables(phpSource);

        // Build rename map: sort alphabetically, assign _a, _b, ...
        var sorted = varNames.OrderBy(v => v, StringComparer.Ordinal).ToList();
        var renameMap = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int idx = 0; idx < sorted.Count; idx++)
            renameMap[sorted[idx]] = GenerateName(idx);

        // Pass 2: emit transformed output
        return EmitTransformed(phpSource, renameMap);
    }

    // ── Name generation ───────────────────────────────────────────────────────

    // Bijective base-26: 0→_a, 1→_b, …, 25→_z, 26→_aa, 27→_ab, …
    public static string GenerateName(int index)
    {
        var chars = new List<char>();
        int n = index + 1; // make 1-based
        while (n > 0)
        {
            n--;
            chars.Insert(0, (char)('a' + n % 26));
            n /= 26;
        }
        return "_" + new string(chars.ToArray());
    }

    // ── Character classification ──────────────────────────────────────────────

    private static bool IsVarStart(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';

    private static bool IsVarPart(char c) =>
        IsVarStart(c) || (c >= '0' && c <= '9');

    /// <summary>
    /// If position <paramref name="i"/> holds a '$' that introduces a renameable
    /// user variable, returns the name (without '$'). Returns null otherwise.
    /// </summary>
    private static string? TryReadVar(string src, int i)
    {
        // Variable-variable: the char before this '$' is also '$'
        if (i > 0 && src[i - 1] == '$') return null;

        // Next char must be a valid identifier start
        int j = i + 1;
        if (j >= src.Length || !IsVarStart(src[j])) return null;

        int start = j;
        while (j < src.Length && IsVarPart(src[j])) j++;

        var name = src[start..j];
        return ProtectedVars.Contains(name) ? null : name;
    }

    // ── Pass 1: variable collection ───────────────────────────────────────────

    private static HashSet<string> CollectVariables(string src)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        int len = src.Length;
        var state = ParseState.Code;
        string heredocLabel = "";
        bool atLineStart = true;

        while (i < len)
        {
            char c = src[i];

            switch (state)
            {
                case ParseState.Code:
                    if (c == '/' && i + 1 < len && src[i + 1] == '/')
                    {
                        state = ParseState.LineComment;
                        i += 2;
                    }
                    else if (c == '#' && !(i + 1 < len && src[i + 1] == '['))
                    {
                        // '#' starts a line comment, but '#[' is a PHP 8 attribute
                        state = ParseState.LineComment;
                        i++;
                    }
                    else if (c == '/' && i + 1 < len && src[i + 1] == '*')
                    {
                        state = ParseState.BlockComment;
                        i += 2;
                    }
                    else if (c == '\'')
                    {
                        state = ParseState.SingleQuotedString;
                        i++;
                    }
                    else if (c == '"')
                    {
                        state = ParseState.DoubleQuotedString;
                        i++;
                    }
                    else if (c == '<' && i + 2 < len && src[i + 1] == '<' && src[i + 2] == '<')
                    {
                        i += 3;
                        // skip optional spaces after <<<
                        while (i < len && src[i] == ' ') i++;
                        bool isNowdoc = i < len && src[i] == '\'';
                        if (isNowdoc) i++; // skip opening quote
                        // read label: identifier chars
                        int labelStart = i;
                        while (i < len && IsVarPart(src[i])) i++;
                        heredocLabel = src[labelStart..i];
                        if (isNowdoc && i < len && src[i] == '\'') i++; // skip closing quote
                        state = isNowdoc ? ParseState.NowdocString : ParseState.HeredocString;
                        // skip rest of opening line to newline
                        while (i < len && src[i] != '\n') i++;
                        if (i < len) i++; // consume the newline
                        atLineStart = true;
                    }
                    else if (c == '$')
                    {
                        var name = TryReadVar(src, i);
                        if (name != null)
                        {
                            result.Add(name);
                            i += 1 + name.Length;
                        }
                        else
                        {
                            i++;
                        }
                    }
                    else
                    {
                        i++;
                    }
                    break;

                case ParseState.LineComment:
                    if (c == '\n') state = ParseState.Code;
                    i++;
                    break;

                case ParseState.BlockComment:
                    if (c == '*' && i + 1 < len && src[i + 1] == '/')
                    {
                        state = ParseState.Code;
                        i += 2;
                    }
                    else i++;
                    break;

                case ParseState.SingleQuotedString:
                    if (c == '\\' && i + 1 < len) i += 2; // escaped char
                    else if (c == '\'') { state = ParseState.Code; i++; }
                    else i++;
                    break;

                case ParseState.DoubleQuotedString:
                    if (c == '\\' && i + 1 < len)
                    {
                        i += 2;
                    }
                    else if (c == '"')
                    {
                        state = ParseState.Code;
                        i++;
                    }
                    else if (c == '$')
                    {
                        var name = TryReadVar(src, i);
                        if (name != null)
                        {
                            result.Add(name);
                            i += 1 + name.Length;
                        }
                        else i++;
                    }
                    else i++;
                    break;

                case ParseState.HeredocString:
                    // Check for end-of-heredoc: label at start of line followed by ; or newline
                    if (atLineStart && StartsWithAt(src, i, heredocLabel))
                    {
                        int end = i + heredocLabel.Length;
                        if (end >= len || src[end] == ';' || src[end] == '\n' || src[end] == '\r')
                        {
                            state = ParseState.Code;
                            while (i < len && src[i] != '\n') i++;
                            if (i < len) i++;
                            break; // exit switch
                        }
                    }
                    if (c == '$')
                    {
                        var name = TryReadVar(src, i);
                        if (name != null)
                        {
                            result.Add(name);
                            i += 1 + name.Length;
                        }
                        else i++;
                        atLineStart = false;
                    }
                    else
                    {
                        atLineStart = (c == '\n');
                        i++;
                    }
                    break;

                case ParseState.NowdocString:
                    // No interpolation; just look for end-of-nowdoc label
                    if (atLineStart && StartsWithAt(src, i, heredocLabel))
                    {
                        int end = i + heredocLabel.Length;
                        if (end >= len || src[end] == ';' || src[end] == '\n' || src[end] == '\r')
                        {
                            state = ParseState.Code;
                            while (i < len && src[i] != '\n') i++;
                            if (i < len) i++;
                            break;
                        }
                    }
                    atLineStart = (c == '\n');
                    i++;
                    break;
            }
        }

        return result;
    }

    // ── Pass 2: emit transformed output ──────────────────────────────────────

    private static string EmitTransformed(string src, Dictionary<string, string> renameMap)
    {
        var sb = new StringBuilder(src.Length);
        int i = 0;
        int len = src.Length;
        var state = ParseState.Code;
        string heredocLabel = "";
        bool atLineStart = true;
        bool lastWasSpace = false;
        bool lastWasNewline = false;

        while (i < len)
        {
            char c = src[i];

            switch (state)
            {
                case ParseState.Code:
                    if (c == '/' && i + 1 < len && src[i + 1] == '/')
                    {
                        // Line comment: skip until end of line (don't emit)
                        i += 2;
                        while (i < len && src[i] != '\n') i++;
                        // Leave \n to be processed by next iteration
                    }
                    else if (c == '#' && !(i + 1 < len && src[i + 1] == '['))
                    {
                        // Hash comment (but not PHP 8 attribute #[...])
                        i++;
                        while (i < len && src[i] != '\n') i++;
                    }
                    else if (c == '/' && i + 1 < len && src[i + 1] == '*')
                    {
                        // Block comment: skip until */
                        i += 2;
                        while (i + 1 < len && !(src[i] == '*' && src[i + 1] == '/')) i++;
                        if (i + 1 < len) i += 2;
                    }
                    else if (c == '\'')
                    {
                        state = ParseState.SingleQuotedString;
                        sb.Append('\'');
                        lastWasSpace = lastWasNewline = false;
                        i++;
                    }
                    else if (c == '"')
                    {
                        state = ParseState.DoubleQuotedString;
                        sb.Append('"');
                        lastWasSpace = lastWasNewline = false;
                        i++;
                    }
                    else if (c == '<' && i + 2 < len && src[i + 1] == '<' && src[i + 2] == '<')
                    {
                        sb.Append("<<<");
                        i += 3;
                        // emit optional spaces
                        while (i < len && src[i] == ' ') { sb.Append(' '); i++; }
                        bool isNowdoc = i < len && src[i] == '\'';
                        if (isNowdoc) { sb.Append('\''); i++; }
                        // emit label
                        int labelStart = i;
                        while (i < len && IsVarPart(src[i])) i++;
                        heredocLabel = src[labelStart..i];
                        sb.Append(heredocLabel);
                        if (isNowdoc && i < len && src[i] == '\'') { sb.Append('\''); i++; }
                        state = isNowdoc ? ParseState.NowdocString : ParseState.HeredocString;
                        // emit rest of opening line
                        while (i < len && src[i] != '\n') { sb.Append(src[i]); i++; }
                        if (i < len) { sb.Append('\n'); i++; }
                        atLineStart = true;
                        lastWasSpace = lastWasNewline = false;
                    }
                    else if (c == '$')
                    {
                        // Variable-variable: next char is also '$'
                        if (i + 1 < len && src[i + 1] == '$')
                        {
                            sb.Append('$');
                            lastWasSpace = lastWasNewline = false;
                            i++;
                            // The second '$' and its identifier are handled in the next iteration.
                            // TryReadVar will return null because src[i-1] == '$'.
                        }
                        else
                        {
                            var name = TryReadVar(src, i);
                            if (name != null)
                            {
                                sb.Append('$');
                                sb.Append(renameMap.TryGetValue(name, out var renamed) ? renamed : name);
                                i += 1 + name.Length;
                            }
                            else
                            {
                                sb.Append('$');
                                i++;
                            }
                            lastWasSpace = lastWasNewline = false;
                        }
                    }
                    else if (c == '\n')
                    {
                        if (!lastWasNewline)
                        {
                            sb.Append('\n');
                            lastWasNewline = true;
                        }
                        lastWasSpace = false;
                        i++;
                    }
                    else if (c == ' ' || c == '\t')
                    {
                        if (!lastWasSpace && !lastWasNewline)
                        {
                            sb.Append(' ');
                            lastWasSpace = true;
                        }
                        i++;
                    }
                    else
                    {
                        sb.Append(c);
                        lastWasSpace = lastWasNewline = false;
                        i++;
                    }
                    break;

                // ── Single-quoted string: no interpolation, emit verbatim ──────
                case ParseState.SingleQuotedString:
                    if (c == '\\' && i + 1 < len)
                    {
                        sb.Append(c); sb.Append(src[i + 1]);
                        i += 2;
                    }
                    else if (c == '\'')
                    {
                        sb.Append('\'');
                        state = ParseState.Code;
                        lastWasSpace = lastWasNewline = false;
                        i++;
                    }
                    else { sb.Append(c); i++; }
                    break;

                // ── Double-quoted string: rename variables, emit rest verbatim ─
                case ParseState.DoubleQuotedString:
                    if (c == '\\' && i + 1 < len)
                    {
                        sb.Append(c); sb.Append(src[i + 1]);
                        i += 2;
                    }
                    else if (c == '"')
                    {
                        sb.Append('"');
                        state = ParseState.Code;
                        lastWasSpace = lastWasNewline = false;
                        i++;
                    }
                    else if (c == '$')
                    {
                        var name = TryReadVar(src, i);
                        if (name != null)
                        {
                            sb.Append('$');
                            sb.Append(renameMap.TryGetValue(name, out var renamed) ? renamed : name);
                            i += 1 + name.Length;
                        }
                        else { sb.Append('$'); i++; }
                    }
                    else { sb.Append(c); i++; }
                    break;

                // ── Heredoc string: rename variables, emit rest verbatim ───────
                case ParseState.HeredocString:
                    if (atLineStart && StartsWithAt(src, i, heredocLabel))
                    {
                        int end = i + heredocLabel.Length;
                        if (end >= len || src[end] == ';' || src[end] == '\n' || src[end] == '\r')
                        {
                            // End marker: emit label + rest of closing line
                            sb.Append(heredocLabel);
                            i += heredocLabel.Length;
                            while (i < len && src[i] != '\n') { sb.Append(src[i]); i++; }
                            if (i < len) { sb.Append('\n'); i++; }
                            state = ParseState.Code;
                            lastWasSpace = lastWasNewline = false;
                            atLineStart = false;
                            break;
                        }
                    }
                    if (c == '$')
                    {
                        var name = TryReadVar(src, i);
                        if (name != null)
                        {
                            sb.Append('$');
                            sb.Append(renameMap.TryGetValue(name, out var renamed) ? renamed : name);
                            i += 1 + name.Length;
                        }
                        else { sb.Append('$'); i++; }
                        atLineStart = false;
                    }
                    else
                    {
                        sb.Append(c);
                        atLineStart = (c == '\n');
                        i++;
                    }
                    break;

                // ── Nowdoc string: no interpolation, emit verbatim ────────────
                case ParseState.NowdocString:
                    if (atLineStart && StartsWithAt(src, i, heredocLabel))
                    {
                        int end = i + heredocLabel.Length;
                        if (end >= len || src[end] == ';' || src[end] == '\n' || src[end] == '\r')
                        {
                            sb.Append(heredocLabel);
                            i += heredocLabel.Length;
                            while (i < len && src[i] != '\n') { sb.Append(src[i]); i++; }
                            if (i < len) { sb.Append('\n'); i++; }
                            state = ParseState.Code;
                            lastWasSpace = lastWasNewline = false;
                            atLineStart = false;
                            break;
                        }
                    }
                    sb.Append(c);
                    atLineStart = (c == '\n');
                    i++;
                    break;
            }
        }

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if <paramref name="src"/> at position <paramref name="pos"/>
    /// begins with <paramref name="label"/> (ordinal comparison).
    /// </summary>
    private static bool StartsWithAt(string src, int pos, string label)
    {
        if (pos + label.Length > src.Length) return false;
        return src.AsSpan(pos, label.Length).SequenceEqual(label.AsSpan());
    }
}
