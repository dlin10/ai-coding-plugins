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

    private static bool IsSensitiveContent(string content)
    {
        if (PrivateKey().IsMatch(content) || AwsAccessKey().IsMatch(content)) return true;

        foreach (var line in content.Split('\n'))
        {
            if (line.Length > 4096 || !SecretKeyword().IsMatch(line)) continue;

            var match = SecretAssignment().Match(DeclarationPrefix().Replace(line, string.Empty));
            if (!match.Success || !SecretKeyword().IsMatch(match.Groups[1].Value)) continue;

            var value = match.Groups[2].Value;
            var classes = new[]
            {
                value.Any(char.IsLower),
                value.Any(char.IsUpper),
                value.Any(char.IsDigit),
                value.Any(character => "+/=_-".Contains(character, StringComparison.Ordinal))
            }.Count(present => present);

            if (classes >= 3 || LongHex().IsMatch(value)) return true;
        }

        return false;
    }

    /// <summary>Refuses to send <paramref name="text"/> onward. Called at every vendor boundary.</summary>
    public static void Guard(string text, string what)
    {
        if (IsSensitiveContent(text)) throw new SensitiveContentException(what);
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
}

internal sealed class SensitiveContentException(string what)
    : Exception($"{what} contains withheld sensitive content, so it was not sent to the vendor");
