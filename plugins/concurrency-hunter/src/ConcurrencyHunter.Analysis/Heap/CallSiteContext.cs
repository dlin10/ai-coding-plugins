using System.Text;
using System.Text.RegularExpressions;

namespace ConcurrencyHunter.Heap;

/// <summary>The admitted project call carried by library contexts, apart from their ordinary bounded context.</summary>
internal static class CallSiteContext
{
    private const string PREFIX = "|site:";
    private static readonly Regex Sites = new(@"\|site:([A-Za-z0-9+/=]+);", RegexOptions.CultureInvariant);

    internal static string Of(string body, int operation) =>
        PREFIX + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{body}#{operation}")) + ";";

    internal static string Site(string context) => Sites.Matches(context).LastOrDefault()?.Value ?? "";

    internal static string WithoutSites(string text) => Sites.Replace(text, "");
}
