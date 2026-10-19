using PlanForge.Acts;
using PlanForge.Evidence;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Checks R9's complete source-by-occurrence matrix against literal token inventories.</summary>
public sealed class CandidateSelectionMatrixTests
{
    /// <summary>The independent ways an identifier can enter candidate selection.</summary>
    private enum TokenSource
    {
        /// <summary>A repository path and its file names.</summary>
        LocatorPath,
        /// <summary>A repository symbol and its segments.</summary>
        LocatorSymbol,
        /// <summary>A single-backtick code span.</summary>
        CodeSpan,
        /// <summary>A word with Pascal casing.</summary>
        PascalCase,
        /// <summary>A Pascal-cased word containing an acronym.</summary>
        AcronymPascalCase,
        /// <summary>A word with camel casing.</summary>
        CamelCase,
        /// <summary>A word containing an underscore.</summary>
        SnakeCase,
        /// <summary>A dotted identifier and its segments.</summary>
        DottedIdentifier,
        /// <summary>A path named in item text.</summary>
        TextPath,
        /// <summary>An identifier connected through a Change point.</summary>
        ChangePointBridge,
        /// <summary>A symbol segment shorter than the token minimum.</summary>
        TwoCharacterSymbolPart,
        /// <summary>A plain capitalised word without identifier casing.</summary>
        CapitalisedWord,
        /// <summary>A plain lowercase word.</summary>
        LowerCaseWord
    }

    /// <summary>The spelling and boundary variations applied to each source's whole spelling.</summary>
    private enum Occurrence
    {
        /// <summary>The exact token with identifier boundaries.</summary>
        Whole,
        /// <summary>The token embedded in a longer identifier.</summary>
        LongerIdentifier,
        /// <summary>A spelling with different letter casing.</summary>
        DifferentCase,
        /// <summary>A path written with backslashes.</summary>
        Backslashes,
        /// <summary>No matching occurrence.</summary>
        Absent
    }

    /// <summary>A literal source and its independently enumerated R9 token inventory.</summary>
    /// <param name="Kind">The token-source axis value.</param>
    /// <param name="Spelling">The whole source spelling to place in a unit.</param>
    /// <param name="Tokens">Every token this source yields, literally listed.</param>
    /// <param name="Repository">Whether the spelling is a repository locator instead of item text.</param>
    /// <param name="ChangePoint">The optional Change point bridging this item to additional tokens.</param>
    /// <param name="BridgeTokens">Every token yielded by that Change point, literally listed.</param>
    private sealed record Source(TokenSource Kind, string Spelling, string[] Tokens, bool Repository = false,
                                  string? ChangePoint = null, string[]? BridgeTokens = null);

    /// <summary>All source values, including symbol cuts, short tokens and separator normalization.</summary>
    private static readonly Source[] Sources =
    [
        new(TokenSource.LocatorPath, "src/Run/FooBar.cs:12", ["src/Run/FooBar.cs", "FooBar.cs", "FooBar"], true),
        new(TokenSource.LocatorPath, @"src\Run\FooBar.cs:12", ["src/Run/FooBar.cs", "FooBar.cs", "FooBar"], true),
        new(TokenSource.LocatorSymbol, "Run/Scout.cs#Namespace.Widget.Run",
            ["Run/Scout.cs", "Scout.cs", "Scout", "Namespace.Widget.Run", "Namespace", "Widget", "Run"], true),
        new(TokenSource.LocatorSymbol, "Run/Scout.cs#Namespace.Widget.Run(int)",
            ["Run/Scout.cs", "Scout.cs", "Scout", "Namespace.Widget.Run", "Namespace", "Widget", "Run"], true),
        new(TokenSource.LocatorSymbol, "Run/Scout.cs#Namespace.Widget.Run<T>",
            ["Run/Scout.cs", "Scout.cs", "Scout", "Namespace.Widget.Run", "Namespace", "Widget", "Run"], true),
        new(TokenSource.LocatorSymbol, "Run/Scout.cs#UnknownCalls.Of",
            ["Run/Scout.cs", "Scout.cs", "Scout", "UnknownCalls.Of", "UnknownCalls"], true),
        new(TokenSource.CodeSpan, "`  plain token_name  `", ["plain token_name", "token_name"]),
        new(TokenSource.CodeSpan, "``plain``", []),
        new(TokenSource.CodeSpan, "`Of`", []),
        new(TokenSource.PascalCase, "ScoutReport", ["ScoutReport"]),
        new(TokenSource.AcronymPascalCase, "IAdvisor", ["IAdvisor"]),
        new(TokenSource.AcronymPascalCase, "HTTPServer", ["HTTPServer"]),
        new(TokenSource.CamelCase, "changePoints", ["changePoints"]),
        new(TokenSource.SnakeCase, "source_changed", ["source_changed"]),
        new(TokenSource.DottedIdentifier, "Namespace.Widget.Run", ["Namespace.Widget.Run", "Namespace", "Widget", "Run"]),
        new(TokenSource.TextPath, "src/Run/FooBar.cs", ["src/Run/FooBar.cs", "FooBar.cs", "FooBar"]),
        new(TokenSource.TextPath, "src/Run/FooBar.cs:12", ["src/Run/FooBar.cs:12", "FooBar.cs", "FooBar"]),
        new(TokenSource.TextPath, "/src/FooBar.cs", ["/src/FooBar.cs", "FooBar.cs", "FooBar"]),
        new(TokenSource.TextPath, @"src\Run\FooBar.cs", ["src/Run/FooBar.cs", "FooBar.cs", "FooBar"]),
        new(TokenSource.ChangePointBridge, "ScoutReport", ["ScoutReport"], ChangePoint: "ScoutReport routes to ConsumerCache",
            BridgeTokens: ["ScoutReport", "ConsumerCache"]),
        new(TokenSource.TwoCharacterSymbolPart, "UnknownCalls.Of", ["UnknownCalls.Of", "UnknownCalls"]),
        new(TokenSource.CapitalisedWord, "Scout", []),
        new(TokenSource.LowerCaseWord, "evidence", [])
    ];

    /// <summary>One cell of the complete Cartesian product.</summary>
    /// <param name="Source">The source spelling and literal tokens.</param>
    /// <param name="Occurrence">The variation applied to that whole spelling.</param>
    private sealed record Cell(Source Source, Occurrence Occurrence);

    /// <summary>Every listed source is exercised against every occurrence form.</summary>
    private static readonly Cell[] Cells =
        (from source in Sources
         from occurrence in Enum.GetValues<Occurrence>()
         select new Cell(source, occurrence)).ToArray();

    /// <summary>Every matrix answer follows only the literal tokens and identifier-boundary rule.</summary>
    [Fact]
    public void Every_cell_gets_its_expected_answer()
    {
        foreach (var cell in Cells)
        {
            var candidates = CandidateSelection.Select(Item(cell.Source), [Unit(UnitText(cell))], ChangePoints(cell.Source));
            Assert.True(Expected(cell) == (candidates.Count == 1), cell.ToString());
        }

        var bridge = Sources.Single(source => source.Kind == TokenSource.ChangePointBridge);
        Assert.Single(CandidateSelection.Select(Item(bridge), [Unit("ConsumerCache")], ChangePoints(bridge)));
        Assert.Empty(CandidateSelection.Select(Item(bridge), [Unit("ConsumerCache")], []));
        Assert.Empty(CandidateSelection.Select(Item(bridge), [Unit("ConsumerCache")], ["OtherItem ConsumerCache"]));
        // A bridge shares with the original item, never with another bridge's newly added tokens.
        Assert.Empty(CandidateSelection.Select(Item(bridge), [Unit("SecondConsumer")],
            [bridge.ChangePoint!, "ConsumerCache SecondConsumer"]));
        foreach (var boundary in new[] { "prefix", "_", "7", "é" })
        {
            var source = Sources.Single(source => source.Kind == TokenSource.PascalCase);
            Assert.Empty(CandidateSelection.Select(Item(source), [Unit(boundary + source.Spelling + boundary)], []));
        }
        var shortPart = Sources.Single(source => source.Kind == TokenSource.TwoCharacterSymbolPart);
        Assert.Empty(CandidateSelection.Select(Item(shortPart), [Unit("Of")], []));
        Assert.Single(CandidateSelection.Select(Item(shortPart), [Unit("UnknownCalls")], []));
    }

    /// <summary>Every axis value occurs and the Cartesian product contains no missing or repeated cell.</summary>
    [Fact]
    public void Every_axis_value_occurs_in_some_cell()
    {
        Assert.Equal(Enum.GetValues<TokenSource>(), Cells.Select(cell => cell.Source.Kind).Distinct().ToArray());
        Assert.Equal(Enum.GetValues<Occurrence>(), Cells.Select(cell => cell.Occurrence).Distinct().ToArray());
        Assert.Equal(Sources.Length * 5, Cells.Length);
        Assert.Equal(Cells.Length, Cells.Distinct().Count());
    }

    /// <summary>Even items without identifier tokens select every exclusion and no unrelated unit.</summary>
    [Fact]
    public void Exclusion_units_are_always_candidates()
    {
        var units = new[] { Unit("unrelated", "T1"), Unit("no identifiers", "X", "exclusion"),
                            Unit("still unrelated", "X~2", "exclusion") };
        foreach (var source in Sources)
            Assert.Equal(["X", "X~2"], CandidateSelection.Select(Item(source), units, []).Select(unit => unit.Id));
    }

    /// <summary>Selection has no top-K cap and preserves every matching unit in plan order.</summary>
    [Fact]
    public void Every_matching_unit_is_a_candidate()
    {
        var source = Sources.Single(source => source.Kind == TokenSource.PascalCase);
        var units = Enumerable.Range(1, 40).Select(number => Unit($"Uses ScoutReport ({number})", $"T{number}")).ToArray();

        Assert.Equal(units, CandidateSelection.Select(Item(source), units, []));
        Assert.Equal("1", CandidateSelection.SELECTION_VERSION);
    }

    /// <summary>Extraction matches every literal inventory, including the Change-point text rules.</summary>
    [Fact]
    public void A_source_yields_exactly_its_listed_tokens()
    {
        foreach (var source in Sources)
        {
            Assert.Equal(source.Tokens.Order(StringComparer.Ordinal), CandidateSelection.Tokens(Item(source)).Order(StringComparer.Ordinal));
            if (source.ChangePoint is not null)
                Assert.Equal(source.BridgeTokens!.Order(StringComparer.Ordinal),
                             CandidateSelection.TextTokens(source.ChangePoint).Order(StringComparer.Ordinal));
        }
        var external = new EvidenceItem("A1.confirmedFacts.1", "scout", "confirmedFacts", "plain words",
                                        "https://example.com/ScoutReport.cs:12", null, null, false, []);
        Assert.Empty(CandidateSelection.Tokens(external));
        Assert.Equal(["ScoutReport"], CandidateSelection.Tokens(new EvidenceItem("H1", "implication", null,
            "ScoutReport", null, null, null, false, ["A1.confirmedFacts.1"])));
    }

    /// <summary>Builds an indexed item without reading repository files.</summary>
    /// <param name="source">The independent source value supplying either a locator or text.</param>
    private static EvidenceItem Item(Source source) => new("A1.confirmedFacts.1", "scout", "confirmedFacts",
        source.Repository ? "plain words" : source.Spelling, source.Repository ? source.Spelling : "https://example.com",
        null, source.Repository ? new LocatorCheck(false, null, "missing_file") : null, false, []);

    /// <summary>Builds a unit preserving the exact test text.</summary>
    /// <param name="text">The text against which identifiers are matched.</param>
    /// <param name="id">The unit id, which has no role in selection.</param>
    /// <param name="kind">The unit kind, which can make exclusions unconditional.</param>
    private static PlanUnit Unit(string text, string id = "T1", string kind = "task") =>
        new(id, kind, text, text, PlanOutline.Hash(text), 0, text.Length);

    /// <summary>Supplies only the optional Change point literally specified for a source value.</summary>
    /// <param name="source">The source whose bridge is being exercised.</param>
    private static string[] ChangePoints(Source source) => source.ChangePoint is null ? [] : [source.ChangePoint];

    /// <summary>Applies an occurrence to the whole original spelling, never to an extracted token.</summary>
    /// <param name="cell">The source and occurrence axes.</param>
    private static string UnitText(Cell cell) => cell.Occurrence switch
    {
        Occurrence.Whole => "(" + cell.Source.Spelling + ")",
        Occurrence.LongerIdentifier => "prefix" + cell.Source.Spelling + "Suffix",
        Occurrence.DifferentCase => DifferentCase(cell.Source.Spelling),
        Occurrence.Backslashes => cell.Source.Spelling.Replace('/', '\\'),
        Occurrence.Absent => "unrelated words",
        _ => throw new ArgumentOutOfRangeException(nameof(cell))
    };

    /// <summary>Changes a path's leading segment, retaining filename matches such as SRC/Run/FooBar.cs.</summary>
    /// <param name="spelling">The whole source spelling to vary.</param>
    private static string DifferentCase(string spelling)
    {
        var separator = spelling.IndexOfAny(['/', '\\']);
        return separator > 0 ? spelling[..separator].ToUpperInvariant() + spelling[separator..] : spelling.ToUpperInvariant();
    }

    /// <summary>Computes a cell's expectation solely from its explicitly listed tokens and R9 boundaries.</summary>
    /// <param name="cell">The matrix cell being evaluated independently of production code.</param>
    private static bool Expected(Cell cell)
    {
        var tokens = cell.Source.Tokens.AsEnumerable();
        if (cell.Source.BridgeTokens is { } bridge && bridge.Intersect(cell.Source.Tokens, StringComparer.Ordinal).Any())
            tokens = tokens.Concat(bridge);
        var text = UnitText(cell).Replace('\\', '/');
        return tokens.Any(token => Enumerable.Range(0, Math.Max(0, text.Length - token.Length + 1)).Any(index =>
            text.AsSpan(index, token.Length).SequenceEqual(token.AsSpan())
            && (index == 0 || !(char.IsLetterOrDigit(text[index - 1]) || text[index - 1] == '_'))
            && (index + token.Length == text.Length || !(char.IsLetterOrDigit(text[index + token.Length]) || text[index + token.Length] == '_'))));
    }
}
