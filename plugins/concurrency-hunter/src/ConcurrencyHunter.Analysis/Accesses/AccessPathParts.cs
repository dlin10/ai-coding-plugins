using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Accesses;

/// <summary>
/// An access path read into its parts, the one reading every consumer of a finding asks instead of splitting the path itself (R10):
/// the fields, outermost first; the cell's selector and how many fields stand before it, the last of them holding the collection;
/// the last field; and on a closure region the captured variable, <c>name</c> or <c>name#n</c>, which is the path's first segment
/// (R4). A box's whole storage has the empty path and is named by the boxed struct's type (R3).
/// </summary>
/// <param name="Segments">The path as the resource carries it: every field, the selector right after the collection field.</param>
/// <param name="Fields">The fields of the path, outermost first; no selector among them.</param>
/// <param name="Selector">The cell's selector as the path prints it, such as <c>[0]</c>; null where the path is in no cell.</param>
/// <param name="Cell">How many of <paramref name="Fields"/> stand before <paramref name="Selector"/>; 0 where there is none.</param>
/// <param name="Holder">What the path starts at: the type declaring its first field, the boxed struct's type for a box's whole
/// storage, or the declaring member of a closure.</param>
/// <param name="IsClosure">Whether the path is on a closure region, its first field a captured variable.</param>
public sealed record AccessPathParts(IReadOnlyList<string> Segments, IReadOnlyList<string> Fields, string? Selector, int Cell, string Holder,
                                     bool IsClosure)
{
    private const string IDENTIFIER = @"@?[\p{L}\p{Nl}_][\p{L}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}\p{Cf}]*";
    private const string FIELD = IDENTIFIER + @"(?:#\d+)?";
    // A selector's brackets match outside string literals: a key such as `["a[0]\"#"]` holds brackets, escaped quotes and a `#`.
    private const string SELECTOR = @"\[(?:""(?:[^""\\]|\\.)*""|[^\[\]""])*\]";

    // A path written in text: a field first, then fields after a dot and selectors with or without one, as `_items[0].Count`,
    // `Items.[?]` or `count#2.Total`.
    private static readonly Regex TEXT_PATTERN = new($@"^(?<segment>{FIELD})(?:\.?(?<segment>{SELECTOR})|\.(?<segment>{FIELD}))*$");
    private static readonly Regex NUMBERED_PATTERN = new(@"#\d+$");

    // A key selector written as one C# string literal, its content still escaped.
    private static readonly Regex KEY_PATTERN = new(@"^\[""(?<literal>(?:[^""\\]|\\.)*)""\]$");

    /// <summary>The path's last field, null for a box's whole storage.</summary>
    public string? LastField => Fields.Count == 0 ? null : Fields[^1];

    /// <summary>Whether the path names the cell itself, its selector the last segment: what an atomic member of the collection
    /// answers (ADR 0010). A field of a struct the cell holds is no such cell.</summary>
    public bool IsCellItself => Selector is not null && Cell == Fields.Count;

    /// <summary>The variable a closure region's path starts at, <c>name</c> or <c>name#n</c>; null on any other region.</summary>
    public string? CapturedVariable => IsClosure && Fields.Count != 0 ? Fields[0] : null;

    /// <summary>Whether the path is a box's whole storage: empty, on the box's region.</summary>
    public bool IsWholeStorage => Segments.Count == 0;

    /// <summary>What a finding names as its field: the path's last field, or the boxed struct's type for a box's whole
    /// storage.</summary>
    public string Label => LastField ?? Holder;

    /// <summary>The path as a reader writes it: the fields joined by dots and the selector right after the collection field, such
    /// as <c>_items[0].Count</c>; the boxed struct's type for a box's whole storage.</summary>
    public string Text => IsWholeStorage ? Holder : string.Concat(Segments.Select((segment, index) =>
        index == 0 || segment.StartsWith('[') ? segment : "." + segment));

    /// <summary>The holder with the whole path, as a finding names its resource: <c>Ns.Store._state.Count</c>; the boxed struct's type
    /// for a box's whole storage; the captured variable and the member declaring it for a closure, <c>note of Ns.Worker.Run()</c>.</summary>
    public string Named => IsWholeStorage ? Holder : IsClosure ? $"{Text} of {Holder}" : $"{Holder}.{Text}";

    /// <summary><see cref="Named"/> up to the path's last field: for the cell itself, the collection it is a cell of, as a finding's
    /// title names it; any other path as <see cref="Named"/> names it.</summary>
    public string NamedToLastField => IsCellItself ? (this with { Segments = Fields, Selector = null, Cell = 0 }).Named : Named;

    /// <summary>The parts of a resource's path.</summary>
    /// <param name="resource">The resource.</param>
    public static AccessPathParts Of(AccessResource resource)
    {
        var segments = resource.AccessPath;
        var selector = segments.ToList().FindIndex(segment => segment.StartsWith('['));
        var fields = segments.Where(segment => !segment.StartsWith('[')).ToArray();
        var isClosure = resource.Region.StartsWith(IrClosureCell.REGION_PREFIX, StringComparison.Ordinal);
        var holder = isClosure
            ? resource.Region[IrClosureCell.REGION_PREFIX.Length..]
            : resource.Storage is { Fields.Count: > 0 } storage ? storage.Fields[0].ContainingType : resource.Member.DeclaringType;
        return new AccessPathParts(segments, fields, selector < 0 ? null : segments[selector], Math.Max(selector, 0), holder, isClosure);
    }

    /// <summary>The segments of a path written in text that carries a selector in brackets or a numbered captured variable, as
    /// <see cref="Segments"/> holds them: a key written as a C# string literal is the key itself, as the path prints it,
    /// <c>["a"b"]</c> for <c>["a\"b"]</c>, and one whose literal holds an escape C# does not have stays as written, so it is
    /// compared by that spelling and never left unchecked. Null for any other text, which is no such path.</summary>
    /// <param name="text">The text.</param>
    public static IReadOnlyList<string>? ParseNamedPath(string text)
    {
        var match = TEXT_PATTERN.Match(text);
        if (!match.Success)
            return null;
        var segments = match.Groups["segment"].Captures.Select(capture => AsPrinted(capture.Value)).ToArray();
        return segments.Any(segment => segment.StartsWith('[') || NUMBERED_PATTERN.IsMatch(segment)) ? segments : null;
    }

    /// <summary>A segment written in text as the path prints it: a key selector's literal unescaped, any other segment as it is, and
    /// a literal with an escape C# does not have as it is written.</summary>
    /// <param name="segment">The segment as the text writes it.</param>
    private static string AsPrinted(string segment) =>
        KEY_PATTERN.Match(segment) is { Success: true } key && Unescaped(key.Groups["literal"].Value) is { } literal
            ? $"[\"{literal}\"]"
            : segment;

    /// <summary>The value of a C# string literal's content: its simple escapes, <c>\e</c>, <c>\x</c> with one to four hex digits,
    /// <c>\u</c> with four and <c>\U</c> with eight naming a code point read; null for any other escape.</summary>
    /// <param name="literal">The content between the quotes.</param>
    private static string? Unescaped(string literal)
    {
        var value = new StringBuilder(literal.Length);
        for (var index = 0; index < literal.Length; index++)
        {
            WorkCancellation.Check();
            if (literal[index] != '\\')
            {
                value.Append(literal[index]);
                continue;
            }

            var escaped = literal[++index];
            switch (escaped)
            {
                case '"' or '\'' or '\\':
                    value.Append(escaped);
                    break;
                case 'x' when HexDigits(literal, index + 1, 4) is var digits and > 0:
                    value.Append((char)int.Parse(literal.AsSpan(index + 1, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                    index += digits;
                    break;
                case 'u' when HexDigits(literal, index + 1, 4) == 4:
                    value.Append((char)int.Parse(literal.AsSpan(index + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                    index += 4;
                    break;
                case 'U' when HexDigits(literal, index + 1, 8) == 8 &&
                              uint.Parse(literal.AsSpan(index + 1, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) is var point &&
                              point <= 0x10FFFF && point is not (>= 0xD800 and <= 0xDFFF):
                    value.Append(char.ConvertFromUtf32((int)point));
                    index += 8;
                    break;
                default:
                    var simple = escaped switch
                    {
                        '0' => '\0', 'a' => '\a', 'b' => '\b', 'e' => '\e', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v',
                        _ => (char?)null
                    };
                    if (simple is not { } character)
                        return null;
                    value.Append(character);
                    break;
            }
        }

        return value.ToString();
    }

    /// <summary>How many hex digits stand in a row from <paramref name="start"/>, at most <paramref name="most"/>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="start">Where the digits start.</param>
    /// <param name="most">The most digits to count.</param>
    private static int HexDigits(string text, int start, int most)
    {
        var count = 0;
        while (count < most && start + count < text.Length && char.IsAsciiHexDigit(text[start + count]))
        {
            WorkCancellation.Check();
            count++;
        }
        return count;
    }
}
