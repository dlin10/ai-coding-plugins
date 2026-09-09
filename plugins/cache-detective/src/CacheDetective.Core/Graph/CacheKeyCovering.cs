using System.Text;
using CacheDetective.Caching;

namespace CacheDetective.Graph;

public static class CacheKeyCovering
{
    public static bool Covers(CacheKey invalidation, CacheKey cached) =>
        Covers(invalidation, cached, CacheSemantic.Remove, cached.TagsAll);

    public static bool Covers(Invalidates invalidation, CacheKey cached,
                              IReadOnlySet<string> tags) =>
        Covers((CacheKey)invalidation.To, cached, invalidation.Semantic, tags);

    public static bool Covers(CacheKey invalidation, CacheKey cached, CacheSemantic semantic,
                              IReadOnlySet<string> tags)
    {
        if (invalidation.Store != cached.Store)
            return false;

        if (semantic == CacheSemantic.RemoveByTag)
            return tags.Contains(invalidation.Template);

        var invalidationShape = GetSkeleton(invalidation.Template);
        var cachedShape = GetSkeleton(cached.Template);
        if (invalidationShape == cachedShape)
            return true;

        return invalidationShape.EndsWith('*') &&
               cachedShape.StartsWith(invalidationShape[..^1], StringComparison.Ordinal);
    }

    internal static string GetSkeleton(string template)
    {
        var normalized = new StringBuilder(template.Length);
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] != '{')
            {
                normalized.Append(template[index]);
                continue;
            }

            var end = template.IndexOf('}', index + 1);
            if (end < 0)
            {
                normalized.Append(template[index]);
                continue;
            }

            normalized.Append("{}");
            index = end;
        }

        return normalized.ToString();
    }
}
