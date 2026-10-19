using System.Globalization;
using System.Text.RegularExpressions;
using PlanForge.Vendors;

namespace PlanForge.Evidence;

/// <summary>Selects every plan unit sharing an evidence identifier, including Change-point bridges.</summary>
internal static partial class CandidateSelection
{
    /// <summary>The candidate-selection version included in judgment keys.</summary>
    public const string SELECTION_VERSION = "1";

    /// <summary>Returns all matching units in plan order, with every exclusion included.</summary>
    /// <param name="item">The Scout item or implication whose identifiers are matched.</param>
    /// <param name="units">Every unit of the current plan in document order.</param>
    /// <param name="changePoints">The Run's complete list of Change points.</param>
    public static IReadOnlyList<PlanUnit> Select(EvidenceItem item, IReadOnlyList<PlanUnit> units,
                                                IReadOnlyList<string> changePoints)
    {
        var tokens = Tokens(item);
        var matching = new HashSet<string>(tokens, StringComparer.Ordinal);
        foreach (var point in changePoints)
        {
            var bridge = TextTokens(point);
            if (bridge.Overlaps(tokens)) matching.UnionWith(bridge);
        }
        return units.Where(unit =>
        {
            var text = unit.Text.Replace('\\', '/');
            return unit.Kind == "exclusion" || matching.Any(token => Contains(text, token));
        }).ToArray();
    }

    /// <summary>Extracts text identifiers and, for repository items only, locator identifiers.</summary>
    /// <param name="item">The indexed evidence item, including its optional repository locator.</param>
    public static HashSet<string> Tokens(EvidenceItem item)
    {
        var tokens = TextTokens(item.Text);
        if (item.Kind != "scout" || item.Source is not { } source || !ScoutItem.IsRepositoryLocator(source)) return tokens;
        // External URLs may contain a colon followed by digits, but never carry a local check.
        if (item.CurrentCheck is null) return tokens;

        var separator = source.LastIndexOf(':');
        var isLine = separator > 0
                     && int.TryParse(source[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture,
                                     out var line) && line > 0;
        if (!isLine) separator = source.LastIndexOf('#');
        AddPath(tokens, source[..separator]);
        if (!isLine)
        {
            var symbol = source[(separator + 1)..];
            var cut = symbol.IndexOfAny(['(', '<']);
            if (cut >= 0) symbol = symbol[..cut];
            Add(tokens, symbol);
            foreach (var part in symbol.Split('.')) Add(tokens, part);
        }
        return tokens;
    }

    /// <summary>Extracts only the structured identifiers and single-backtick spans specified by R9.</summary>
    /// <param name="text">Evidence text or a Change point; never a source URL.</param>
    public static HashSet<string> TextTokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match span in CodeSpan().Matches(text)) Add(tokens, span.Groups[1].Value.Trim());
        foreach (Match path in TextPath().Matches(text))
        {
            Add(tokens, path.Value);
            AddFileName(tokens, path.Groups[1].Value.Replace('\\', '/'));
        }
        foreach (Match dotted in DottedIdentifier().Matches(text))
        {
            Add(tokens, dotted.Value);
            foreach (var part in dotted.Value.Split('.')) Add(tokens, part);
        }
        foreach (Match word in Word().Matches(text))
        {
            var value = word.Value;
            var pascal = char.IsUpper(value[0]) && value.Count(char.IsUpper) >= 2 && value.Any(char.IsLower);
            var camel = char.IsLower(value[0]) && value.Any(char.IsUpper);
            var snake = Enumerable.Range(1, value.Length - 2).Any(index => value[index] == '_'
                && char.IsLetterOrDigit(value[index - 1]) && char.IsLetterOrDigit(value[index + 1]));
            if ((value.All(char.IsLetterOrDigit) && (pascal || camel)) || snake) Add(tokens, value);
        }
        return tokens;
    }

    /// <summary>Adds a normalized token only when it has at least three characters.</summary>
    /// <param name="tokens">The ordinal token set being built.</param>
    /// <param name="token">The exact extracted spelling.</param>
    private static void Add(HashSet<string> tokens, string token)
    {
        if (token.Length >= 3) tokens.Add(token.Replace('\\', '/'));
    }

    /// <summary>Adds a path, its file name and its file name without the final extension.</summary>
    /// <param name="tokens">The ordinal token set being built.</param>
    /// <param name="path">The path without a line or symbol qualifier.</param>
    private static void AddPath(HashSet<string> tokens, string path)
    {
        path = path.Replace('\\', '/');
        Add(tokens, path);
        AddFileName(tokens, path);
    }

    /// <summary>Adds a normalized path's file name and its name without the final extension.</summary>
    /// <param name="tokens">The ordinal token set being built.</param>
    /// <param name="path">The normalized path without a line or symbol qualifier.</param>
    private static void AddFileName(HashSet<string> tokens, string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        Add(tokens, name);
        var extension = name.LastIndexOf('.');
        Add(tokens, extension < 0 ? name : name[..extension]);
    }

    /// <summary>Finds an ordinal occurrence bounded on both sides by non-identifier characters.</summary>
    /// <param name="text">The unit text with normalized path separators.</param>
    /// <param name="token">A normalized, non-empty identifier token.</param>
    private static bool Contains(string text, string token)
    {
        for (var start = 0; start <= text.Length - token.Length;)
        {
            var index = text.IndexOf(token, start, StringComparison.Ordinal);
            if (index < 0) return false;
            var end = index + token.Length;
            if ((index == 0 || !IdentifierCharacter(text[index - 1]))
                && (end == text.Length || !IdentifierCharacter(text[end]))) return true;
            start = index + 1;
        }
        return false;
    }

    /// <summary>Identifies a character that prevents an identifier boundary.</summary>
    /// <param name="character">The character immediately beside a token occurrence.</param>
    private static bool IdentifierCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    /// <summary>Recognizes a code span delimited by exactly one backtick at each end.</summary>
    [GeneratedRegex(@"(?<!`)`(?!`)([^`]+)`(?!`)", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpan();

    /// <summary>Recognizes path-like words, retaining an optional line qualifier separately from the path.</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{Nd}_./\\~@%+\-])((?:[A-Za-z]:)?(?:[/\\]|[\p{L}\p{Nd}_.~@%+\-]+[/\\])+[\p{L}\p{Nd}_.~@%+\-]+\.[\p{L}\p{Nd}]{1,10})(?::[0-9]+)?(?![\p{L}\p{Nd}_])", RegexOptions.CultureInvariant)]
    private static partial Regex TextPath();

    /// <summary>Recognizes dotted identifiers and leaves their parts available for independent matching.</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{Nd}_])[\p{L}_][\p{L}\p{Nd}_]*(?:\.[\p{L}_][\p{L}\p{Nd}_]*)+(?![\p{L}\p{Nd}_])", RegexOptions.CultureInvariant)]
    private static partial Regex DottedIdentifier();

    /// <summary>Recognizes whole words for PascalCase, camelCase and snake_case classification.</summary>
    [GeneratedRegex(@"[\p{L}\p{Nd}_]{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex Word();
}
