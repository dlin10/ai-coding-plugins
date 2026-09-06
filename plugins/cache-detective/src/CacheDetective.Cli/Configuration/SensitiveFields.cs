using System.Text.RegularExpressions;

namespace CacheDetective.Configuration;

/// <summary>
/// Which field names never leave the machine as values. The five defaults always apply and a workspace
/// adds to them rather than replacing them: a configuration that could switch redaction off by omission
/// is a configuration that leaks the first time somebody trims it.
/// <para>A mask matches a whole field name, ignoring case, with <c>*</c> standing for any run of
/// characters. So <c>email</c> matches only that field, while <c>*password*</c> matches anything with the
/// word in it.</para>
/// </summary>
public static class SensitiveFields
{
    public static readonly string[] Default = ["email", "phone", "*password*", "*token*", "*secret*"];

    public static IReadOnlyList<string> Masks(IEnumerable<string>? configured) =>
        [.. Default, .. configured ?? []];

    public static bool IsSensitive(string field, IEnumerable<string>? configured) =>
        !string.IsNullOrEmpty(field) && Masks(configured).Any(mask => Matches(field, mask));

    private static bool Matches(string field, string mask) =>
        Regex.IsMatch(field,
                      $"^{string.Join(".*", mask.Split('*').Select(Regex.Escape))}$",
                      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
