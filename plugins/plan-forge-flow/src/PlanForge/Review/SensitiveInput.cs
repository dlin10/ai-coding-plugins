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
    /// <summary>
    /// Whether the name of <paramref name="path"/> marks it sensitive. For a file that is
    /// <see cref="IsDecidedByContent"/> that is a reason to read it, not a verdict.
    /// </summary>
    /// <param name="path">The changed path, as git names it.</param>
    public static bool IsSensitivePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return IsKnownSecretFile(normalized) || IsNamedForASecret(normalized);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is marked sensitive by a keyword in its name alone — or an SSH
    /// key's name, see <see cref="SshKeyName"/> — and is JSON or YAML, so its lines are read instead
    /// of the name refusing it. A name says what a file is about, and for a generated model it is the
    /// library: <c>Microsoft.Extensions.Configuration.UserSecrets.json</c> holds no secret, and the
    /// generator hash in it changed with every engine edit and refused every code-review round that
    /// touched it, issue #200. What the name rule is for is a secret on a line with nothing beside
    /// it, as in <c>tokens.json</c>, and that is what the read is for: <see cref="FindInStructuredFile"/>
    /// withholds a value that stands alone, on top of the content rule. A value with a key beside it is
    /// left to the content rule, which reads the key. Withholding it regardless would refuse nearly
    /// every model — measured on the 76 JSON files of concurrency-hunter's demo, 2026-10-27, 206 values of
    /// the shape of a secret stand beside a key, the generator hash in 75 files, member ids and GUIDs
    /// among them, and none stands alone. What this gives up is a secret under a key that does not say
    /// so, <c>"auth": "ghp_…"</c>, in a file whose name does, and one shorter than the content rule's
    /// twenty characters; and that is the trade: a refused round costs the run a question to the user
    /// every time, and the same secret in <c>settings.json</c> was never guarded by name. XML is not
    /// read, because neither rule can: the content rule starts at a line's first token, and an
    /// element's text sits between tags. A file known to hold secrets — <c>secrets.json</c>,
    /// <c>appsettings.Production.json</c> — key material and a sensitive location keep the name as
    /// the verdict.
    /// </summary>
    /// <param name="path">The changed path, as git names it.</param>
    public static bool IsDecidedByContent(string path)
    {
        var normalized = path.Replace('\\', '/');
        return IsNamedForASecret(normalized) && StructuredExtension().IsMatch(normalized) && !IsKnownSecretFile(normalized);
    }

    /// <summary>Whether a file is known to hold secrets whatever its lines say: by its name, its location or its kind.</summary>
    /// <param name="normalized">The path with forward slashes.</param>
    private static bool IsKnownSecretFile(string normalized) =>
        SensitiveName().IsMatch(normalized)
        || SensitiveLocation().IsMatch(normalized)
        || KeyMaterialExtension().IsMatch(normalized);

    /// <summary>
    /// Whether a secret keyword in the path — or an SSH key's name, see <see cref="SshKeyName"/> —
    /// names what the file holds, which for source code it never does:
    /// <c>src/Infra/CancellationTokenExtensions.cs</c>, <c>tests/PasswordPolicyTests.cs</c> and
    /// <c>src/Auth/TokenCache.cs</c> each refused a whole code-review round, the false positive
    /// issue #88 was for content lines. A source file is exempt because its contents still reach
    /// <see cref="Guard"/> line by line, and a secret written in code is assigned to a name that says
    /// so, which is the form that rule reads. Everything else keeps the keyword, a directory's
    /// included — data, config, text, an unknown extension or none — because there a secret can sit
    /// on a line with nothing beside it, as in <c>tokens.txt</c> or <c>api-token</c>, and the path
    /// is all there is to go on, JSON and YAML aside: their lines are read, see
    /// <see cref="IsDecidedByContent"/>. Matching the keyword as a whole word was the other way
    /// to narrow it, and it does not reach far enough: <c>token_cache.py</c> and <c>Token.cs</c>
    /// are whole words. What this gives up is a secret in a source file that is neither assigned to a
    /// secret-sounding name nor key material — a bearer string in a header table, say — and that is
    /// the trade: a false refusal costs a round and a rename in production code, and the same secret
    /// in <c>Program.cs</c> was never guarded by name.
    /// </summary>
    /// <param name="path">The changed path with forward slashes.</param>
    private static bool IsNamedForASecret(string path) =>
        (SecretPathKeyword().IsMatch(path) || SshKeyName().IsMatch(path)) && !SourceExtension().IsMatch(path);

    /// <summary>Where a withheld value sits. The value itself is never carried: it is the one thing being withheld.</summary>
    private readonly record struct Hit(int Line, string? Path, string? Field);

    /// <summary>
    /// The first withheld value in <paramref name="content"/>, or null. A diff names the file and
    /// the line it is about, so both are tracked as the scan goes: a refusal nobody can locate
    /// cannot be told from a false positive, which is what issue #88 cost a run — the check had to
    /// be replayed by hand to find the line, and the fix was to rename a local in production code.
    /// </summary>
    /// <param name="content">A diff, or any text.</param>
    /// <param name="bareValues">
    /// Whether a value that stands alone on its line, with nothing beside it, is withheld too:
    /// what <see cref="FindInStructuredFile"/> reads a file named for a secret for.
    /// </param>
    private static Hit? Find(string content, bool bareValues = false)
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
            var marked = fileLine is not null;
            if (fileLine is { } next && !line.StartsWith('-')) fileLine = next + 1;

            if (PrivateKey().IsMatch(line) || AwsAccessKey().IsMatch(line)) return new Hit(number, path, null);

            if (bareValues && BareValueOf(line, marked) is { } alone && IsSecretShaped(alone))
                return new Hit(number, path, null);

            if (line.Length > 4096 || !SecretKeyword().IsMatch(line)) continue;

            var match = SecretAssignment().Match(DeclarationPrefix().Replace(line, string.Empty));
            if (!match.Success || !SecretKeyword().IsMatch(match.Groups[1].Value)) continue;

            if (IsSecretShaped(match.Groups[2].Value)) return new Hit(number, path, match.Groups[1].Value);
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

    /// <summary>
    /// Whether a literal reads as a secret rather than a name: it is not an expression, and it is a
    /// long hex run or has three of the four kinds of character. The one test the content rule and
    /// the read of a value that stands alone share, so they cannot disagree about what a secret
    /// looks like.
    /// </summary>
    /// <param name="value">The literal, with its quotes off.</param>
    private static bool IsSecretShaped(string value)
    {
        if (IsExpression(value)) return false;

        var classes = new[]
        {
            value.Any(char.IsLower),
            value.Any(char.IsUpper),
            value.Any(char.IsDigit),
            value.Any(character => "+/=_-".Contains(character, StringComparison.Ordinal))
        }.Count(present => present);

        return classes >= 3 || LongHex().IsMatch(value);
    }

    /// <summary>
    /// The value on a line that has nothing beside it, or null. Brackets, list dashes, one pair of
    /// quotes and a trailing comma are not something beside it; a key, a space or a colon is, so a
    /// pair, a sentence and a URL are never one.
    /// </summary>
    /// <param name="line">A line of a diff or of a file.</param>
    /// <param name="marked">Whether the line opens with the diff's own <c>+</c>, <c>-</c> or space.</param>
    private static string? BareValueOf(string line, bool marked)
    {
        if (line.Length > 4096) return null;

        var match = BareValue().Match(marked && line.Length > 0 ? line[1..] : line);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Refuses to send <paramref name="text"/> onward. Called at every vendor boundary.</summary>
    public static void Guard(string text, string what)
    {
        if (Find(text) is { } hit) throw new SensitiveContentException(what, Where(hit));
    }

    /// <summary>
    /// Where a changed file that is <see cref="IsDecidedByContent"/> holds a secret, or null: the first
    /// line the content rule withholds, or a value that stands alone on its line and has the shape of
    /// one, see <see cref="IsSecretShaped"/>. The place is the file's own line, and never the value.
    /// </summary>
    /// <param name="diff">The file's block of the review window's diff.</param>
    public static string? FindInStructuredFile(string diff) =>
        Find(diff, bareValues: true) is { } hit ? Where(hit with { Path = null }) : null;

    private static string Where(Hit hit)
    {
        var where = hit.Path is { Length: > 0 } path ? $"line {hit.Line} of {path}" : $"line {hit.Line}";
        return hit.Field is { Length: > 0 } field ? $"{where}, assigned to `{field}`" : where;
    }

    [GeneratedRegex(@"(^|/|\\)(?:\.env(?:[./\\].*)?|[^/\\]+\.env|\.npmrc|\.pypirc|\.netrc|service[-_.]?account[^/\\]*\.json|keystore\.jks|appsettings\.[^/\\]+\.json|credentials|\.git-credentials|secrets?\.(?:json|ya?ml|toml)|kubeconfig|terraform\.tfstate(?:\.backup)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    [GeneratedRegex("(?:secret|token|password|credential)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPathKeyword();

    /// <summary>
    /// A file name in the shape ssh-keygen gives a key, <c>id_rsa</c> or <c>id_ed25519</c> and
    /// whatever a user appends to it. It takes the keywords' source exemption because <c>id_</c>
    /// opens <c>id_generator.py</c> and <c>id_mapping.cs</c> just as readily, and each refused a
    /// whole code-review round. Listing the algorithms was the other way to narrow it, and it cuts
    /// the wrong side: <c>id_github</c> names no algorithm and is a key, while no key is called
    /// <c>id_mapping.cs</c>; requiring no extension was a third, and it loses <c>id_rsa.bak</c>.
    /// The public half, <c>.pub</c>, stays guarded although it holds nothing secret: freeing it
    /// would be a rule of its own for a file a run rarely changes, and the refusal names it. What
    /// this gives up is a key saved under a source extension, which ssh-keygen never writes — and a
    /// private key's contents are refused by <see cref="PrivateKey"/> whatever the file is called.
    /// What it keeps is a data file that merely starts with <c>id_</c>, <c>id_mapping.json</c> say,
    /// and that is the trade: a refused round over a fixture beats a key read by a vendor.
    /// </summary>
    [GeneratedRegex("(?:^|/)id_[^/]+$", RegexOptions.IgnoreCase)]
    private static partial Regex SshKeyName();

    [GeneratedRegex(@"\.(cs|fs|vb|razor|cshtml|ts|tsx|js|jsx|mjs|cjs|py|go|rs|java|kt|scala|rb|php|swift|c|h|cc|cpp|hpp|m|mm)$", RegexOptions.IgnoreCase)]
    private static partial Regex SourceExtension();

    [GeneratedRegex(@"\.(json|ya?ml)$", RegexOptions.IgnoreCase)]
    private static partial Regex StructuredExtension();

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

    /// <summary>
    /// A line with nothing beside its value, see <see cref="BareValueOf"/>: one run of at least twenty
    /// characters, the content rule's own minimum, with no space, quote, comma, semicolon, colon or
    /// bracket in it.
    /// </summary>
    [GeneratedRegex(@"^[\s\[{]*(?:-\s+)*[""']?([^\s""',;:\[\]{}]{20,})[""']?[\s,\]}]*$")]
    private static partial Regex BareValue();

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

/// <summary>A refusal to send text onward, written for the orchestrator that has to act on it.</summary>
/// <param name="what">What was refused, as the noun phrase the message goes on from.</param>
/// <param name="where">Where the withheld value sits, or null. Never the value.</param>
/// <param name="advice">What the orchestrator can do about the refusal, or null.</param>
internal sealed class SensitiveContentException(string what, string? where = null, string? advice = null)
    : Exception($"{what} contains withheld sensitive content, so it was not sent to the vendor"
                + (where is { Length: > 0 } ? $" ({where})" : string.Empty)
                + (advice is { Length: > 0 } ? $". {advice}" : string.Empty));
