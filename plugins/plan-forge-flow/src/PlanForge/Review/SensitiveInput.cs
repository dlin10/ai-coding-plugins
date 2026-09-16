using System.Globalization;
using System.Text.RegularExpressions;

namespace PlanForge.Review;

/// <summary>
/// One of the two checks that survived the move to out-of-process workers, and the one whose value
/// the move *increased*: every act hands repository content to a third-party CLI, so a secret that
/// reaches a prompt has left the machine. Ported whole from the 1.x reviewer.
/// </summary>
internal static partial class SensitiveInput
{
    public static bool IsSensitivePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return SensitiveName().IsMatch(normalized)
               || SensitiveLocation().IsMatch(normalized)
               || KeyMaterialExtension().IsMatch(path);
    }

    /// <summary>Where a withheld value sits. The value itself is never carried: it is the one thing being withheld.</summary>
    private readonly record struct Hit(int Line, string? Path, string? Field);

    /// <summary>
    /// The first withheld value in <paramref name="content"/>, or null. A diff names the file and
    /// the line it is about, so both are tracked as the scan goes: a refusal nobody can locate
    /// cannot be told from a false positive, which is what issue #88 cost a run — the check had to
    /// be replayed by hand to find the line, and the fix was to rename a local in production code.
    /// </summary>
    private static Hit? Find(string content)
    {
        string? path = null;
        int? fileLine = null;
        var textLine = 0;

        foreach (var line in content.Split('\n'))
        {
            textLine++;

            if (FileHeader().Match(line) is { Success: true } header)
            {
                path = header.Groups[1].Value.Trim();
                fileLine = null;
                continue;
            }

            if (HunkHeader().Match(line) is { Success: true } hunk)
            {
                fileLine = int.Parse(hunk.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }

            // Outside a diff there is no file to count in, so the count is of the text as handed over.
            var number = fileLine ?? textLine;
            if (fileLine is { } next && !line.StartsWith('-')) fileLine = next + 1;

            if (PrivateKey().IsMatch(line) || AwsAccessKey().IsMatch(line)) return new Hit(number, path, null);

            if (line.Length > 4096 || !SecretKeyword().IsMatch(line)) continue;

            var match = SecretAssignment().Match(DeclarationPrefix().Replace(line, string.Empty));
            if (!match.Success || !SecretKeyword().IsMatch(match.Groups[1].Value)) continue;

            var value = match.Groups[2].Value;
            if (IsExpression(value)) continue;

            var classes = new[]
            {
                value.Any(char.IsLower),
                value.Any(char.IsUpper),
                value.Any(char.IsDigit),
                value.Any(character => "+/=_-".Contains(character, StringComparison.Ordinal))
            }.Count(present => present);

            if (classes >= 3 || LongHex().IsMatch(value)) return new Hit(number, path, match.Groups[1].Value);
        }

        return null;
    }

    /// <summary>
    /// Whether the assigned value is code rather than a literal, which is what issue #88 turned on:
    /// <c>backtick.Groups[1].Value.Trim()</c> is thirty-odd characters with no quote or space, and a
    /// capital and a digit in it were the whole of the three-classes rule, so an untouched C# line
    /// refused a whole review round. A call or an index settles it; so does a dotted path, bounded
    /// to identifier-length segments because a JWT is dot-separated too and its segments are far
    /// longer than anything a program calls a member. What this gives up is a secret that happens to
    /// be dotted in the same shape — <c>Abc.Def.Ghi123456789</c> — and that is the trade: a false
    /// refusal costs a round and a rename in production code, a literal with three dots does not
    /// occur in the formats anyone issues.
    /// </summary>
    private static bool IsExpression(string value) =>
        value.IndexOfAny(['(', ')', '[', ']']) >= 0 || DottedPath().IsMatch(value);

    /// <summary>Refuses to send <paramref name="text"/> onward. Called at every vendor boundary.</summary>
    public static void Guard(string text, string what)
    {
        if (Find(text) is { } hit) throw new SensitiveContentException(what, Where(hit));
    }

    private static string Where(Hit hit)
    {
        var where = hit.Path is { Length: > 0 } path ? $"line {hit.Line} of {path}" : $"line {hit.Line}";
        return hit.Field is { Length: > 0 } field ? $"{where}, assigned to `{field}`" : where;
    }

    [GeneratedRegex(@"(^|/|\\)(?:\.env(?:[./\\].*)?|[^/\\]+\.env|\.npmrc|\.pypirc|\.netrc|id_[^/\\]+|service[-_.]?account[^/\\]*\.json|keystore\.jks|appsettings\.[^/\\]+\.json|credentials|\.git-credentials|secrets?\.(?:json|ya?ml|toml)|kubeconfig|terraform\.tfstate(?:\.backup)?|.*(?:secret|token|password|credential).*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    [GeneratedRegex(@"(?:^|[\\/])(?:\.docker[\\/]config\.json|\.kube[\\/]config)$", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveLocation();

    [GeneratedRegex(@"\.(pem|key|p12|pfx|jks)$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyMaterialExtension();

    [GeneratedRegex("-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex("(?:api[_-]?key|secret|token|password|passwd|credential|private[_-]?key)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKeyword();

    // The '+' is the one change from the 1.x regex: this now runs over diffs as well as file
    // contents, and every added line there starts with one.
    [GeneratedRegex(@"^[\s\-*+]*[""']?([A-Za-z0-9_.$-]+)[""']?\s*[:=]\s*[""']?([^""'\s,;]{20,})", RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"^\s*(?:(?:export|public|private|protected|internal|static|readonly|final|const|let|var|val|def|string|self\.|this\.)\s+)+", RegexOptions.IgnoreCase)]
    private static partial Regex DeclarationPrefix();

    [GeneratedRegex("^[a-f0-9]{32,}$", RegexOptions.IgnoreCase)]
    private static partial Regex LongHex();

    /// <summary>The new side of a unified diff's file header, which is the path whose contents are being sent.</summary>
    [GeneratedRegex(@"^\+\+\+ (?:b/)?(.+)$")]
    private static partial Regex FileHeader();

    /// <summary>A hunk header, captured for the line the new file resumes at.</summary>
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    /// <summary>A member access of identifier-length segments: <c>a.B.C</c>, never a JWT.</summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,19}(?:\.[A-Za-z_][A-Za-z0-9_]{0,19})+$")]
    private static partial Regex DottedPath();
}

internal sealed class SensitiveContentException(string what, string? where = null)
    : Exception($"{what} contains withheld sensitive content, so it was not sent to the vendor"
                + (where is { Length: > 0 } ? $" ({where})" : string.Empty));
