using System.Text.RegularExpressions;

namespace CacheDetective.External;

public static partial class PathTemplates
{
    private static readonly Regex SCHEME = SchemeRegex();
    private static readonly Regex CONSTRAINT = ConstraintRegex();
    private static readonly Regex VERSION = VersionRegex();

    public static string Normalize(string raw)
    {
        var path = StripQueryAndFragment(raw);
        if (SCHEME.IsMatch(path))
        {
            var slash = path.IndexOf('/', path.IndexOf("://", StringComparison.Ordinal) + 3);
            path = slash < 0 ? string.Empty : path[slash..];
        }

        var segments = path.ToLowerInvariant().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                           .Select(segment => CONSTRAINT.Replace(segment, "{$1}"))
                           .SelectMany(segment => segment.StartsWith("{?}", StringComparison.Ordinal) && segment.Length > 3
                               ? new[] { "{?}", segment[3..] }
                               : new[] { segment })
                           .Select(segment => VERSION.IsMatch(segment) ? "{v}" : segment);
        return string.Join('/', segments);
    }

    private static string StripQueryAndFragment(string value)
    {
        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '{') depth++;
            else if (value[index] == '}' && depth > 0) depth--;
            else if (depth == 0 && value[index] is '?' or '#') return value[..index];
        }

        return value;
    }

    [GeneratedRegex("^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SchemeRegex();

    [GeneratedRegex("\\{([^}:]+):[^}]+\\}", RegexOptions.CultureInvariant)]
    private static partial Regex ConstraintRegex();

    [GeneratedRegex("^(v\\d+|v\\{[^}]+\\})$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
