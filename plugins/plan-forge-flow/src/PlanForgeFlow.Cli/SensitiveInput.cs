using System.Text.RegularExpressions;

namespace PlanForgeFlow;

internal static class SensitiveInput
{
    private static readonly Regex SensitiveName = new(@"(^|/|\\)(?:\.env(?:[./\\].*)?|[^/\\]+\.env|\.npmrc|\.pypirc|\.netrc|id_[^/\\]+|service[-_.]?account[^/\\]*\.json|keystore\.jks|appsettings\.[^/\\]+\.json|credentials|\.git-credentials|secrets?\.(?:json|ya?ml|toml)|kubeconfig|terraform\.tfstate(?:\.backup)?|.*(?:secret|token|password|credential).*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SensitiveLocation = new(@"(?:^|[\\/])(?:\.docker[\\/]config\.json|\.kube[\\/]config)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PrivateKey = new("-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.Compiled);
    private static readonly Regex AwsAccessKey = new("\\bAKIA[0-9A-Z]{16}\\b", RegexOptions.Compiled);
    private static readonly Regex SecretKeyword = new("(?:api[_-]?key|secret|token|password|passwd|credential|private[_-]?key)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecretAssignment = new(@"^[\s\-*]*[\""']?([A-Za-z0-9_.$-]+)[\""']?\s*[:=]\s*[\""']?([^\""'\s,;]{20,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DeclarationPrefix = new(@"^\s*(?:(?:export|public|private|protected|internal|static|readonly|final|const|let|var|val|def|string|self\.|this\.)\s+)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsSensitivePath(string path) => SensitiveName.IsMatch(path.Replace('\\', '/')) || SensitiveLocation.IsMatch(path.Replace('\\', '/')) || Regex.IsMatch(path, "\\.(pem|key|p12|pfx|jks)$", RegexOptions.IgnoreCase);

    public static bool IsSensitiveContent(string content)
    {
        if (PrivateKey.IsMatch(content) || AwsAccessKey.IsMatch(content)) return true;
        foreach (var line in content.Split('\n'))
        {
            if (line.Length > 4096 || !SecretKeyword.IsMatch(line)) continue;
            var match = SecretAssignment.Match(DeclarationPrefix.Replace(line, string.Empty));
            if (!match.Success || !SecretKeyword.IsMatch(match.Groups[1].Value)) continue;
            var value = match.Groups[2].Value;
            var classes = new[]
            {
                value.Any(char.IsLower), value.Any(char.IsUpper), value.Any(char.IsDigit), value.Any(character => "+/=_-".Contains(character)),
            }.Count(flag => flag);
            if (classes >= 3 || Regex.IsMatch(value, "^[a-f0-9]{32,}$", RegexOptions.IgnoreCase)) return true;
        }

        return false;
    }
}