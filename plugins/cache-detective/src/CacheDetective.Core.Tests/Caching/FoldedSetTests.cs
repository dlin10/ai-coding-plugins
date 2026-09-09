using CacheDetective.Caching;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CacheDetective.Tests.Caching;

/// <summary>A fold yields every value a site may produce, and says when it collapsed one. The set is
/// de-duplicated, ordered ordinally because the order reaches vertex creation, and capped on the result;
/// the collapsed mark is what separates a fold that named its values from one that merely bounded them.
/// See <c>docs/adr/0015</c>.</summary>
public sealed class FoldedSetTests
{
    private static readonly FoldedPlaceholders PLACEHOLDERS = new(name => $"{{{name}}}", "{?}");

    [Fact]
    public async Task A_local_assigned_in_two_places_yields_both()
    {
        var set = await FoldAsync(nameof(Cases.TwoBranchLocal));

        Assert.Equal(["one", "two"], Values(set));
    }

    [Fact]
    public async Task A_local_assigned_in_an_if_else_yields_both()
    {
        var set = await FoldAsync(nameof(Cases.IfElseLocal));

        Assert.Equal(["no", "yes"], Values(set));
    }

    /// <summary>The same branching as a multi-assigned local, written differently.</summary>
    [Fact]
    public async Task A_conditional_expression_yields_both_branches()
    {
        var set = await FoldAsync(nameof(Cases.Conditional));

        Assert.Equal(["false", "true"], Values(set));
    }

    [Fact]
    public async Task A_nested_conditional_yields_every_branch()
    {
        var set = await FoldAsync(nameof(Cases.NestedConditional));

        Assert.Equal(["a", "b", "c"], Values(set));
    }

    [Fact]
    public async Task A_composite_of_two_multi_valued_parts_is_their_product()
    {
        var set = await FoldAsync(nameof(Cases.Product));

        Assert.Equal(["a1", "a2", "b1", "b2"], Values(set));
    }

    [Fact]
    public async Task Two_branches_folding_to_the_same_value_are_one()
    {
        var set = await FoldAsync(nameof(Cases.Duplicate));

        Assert.Equal(["same"], Values(set));
    }

    /// <summary>Some members nameable and one not: every member is kept, and the fold says it collapsed.
    /// </summary>
    [Fact]
    public async Task A_mixed_set_keeps_every_member_and_is_collapsed()
    {
        var set = await FoldAsync(nameof(Cases.Mixed));

        // The second member names an argument but no key: it carries no literal segment, so it is not a
        // template the graph can hold, and the set says so.
        Assert.Equal(["known:key", "{runtime}"], Values(set));
        Assert.True(set.Collapsed);
    }

    /// <summary>The consumer's half of the mixed set: the nameable member becomes a key, the unnameable
    /// one becomes the site's single unresolved row.</summary>
    [Fact]
    public async Task A_mixed_key_produces_both_a_vertex_and_an_unresolved_row()
    {
        var graph = await IndexAsync("SourceFiles/MixedKeys.cs");

        Assert.Contains(graph.CacheKeys, key => key.Template == "known:key");
        Assert.Single(graph.Unresolved, item => item.Kind == UnresolvedKind.Key);
    }

    /// <summary>Out of scope, and its two spellings differ. A compound assignment is not a simple one, so
    /// the folder does not see it and the local folds to its initialiser alone — a value the site may
    /// never produce, which is why the mark matters more here than the value.</summary>
    [Fact]
    public async Task A_compound_update_folds_to_the_initializer_and_is_collapsed()
    {
        var set = await FoldAsync(nameof(Cases.CompoundUpdate));

        Assert.Equal(["start"], Values(set));
        Assert.True(set.Collapsed);
    }

    /// <summary>The other spelling: a simple assignment reading its own variable is collected, folding it
    /// re-enters the local, and the candidates disagree — exactly the outcome this had before.</summary>
    [Fact]
    public async Task A_self_assignment_folds_to_assigned_differently_and_is_collapsed()
    {
        var set = await FoldAsync(nameof(Cases.SelfAssignment));

        Assert.Equal(["{?}"], Values(set));
        Assert.Contains("assigned differently in 2 places", Assert.Single(set.Values).Reason);
        Assert.True(set.Collapsed);
    }

    [Fact]
    public async Task A_product_past_the_cap_names_the_cap_and_is_collapsed()
    {
        var set = await FoldAsync(nameof(Cases.OverCap));

        Assert.Equal(["{?}"], Values(set));
        Assert.Contains("limit of 8 values", Assert.Single(set.Values).Reason);
        Assert.True(set.Collapsed);
    }

    /// <summary>The case that makes the mark necessary rather than cosmetic: one element, and still not
    /// to be read as a site that always writes it.</summary>
    [Fact]
    public async Task A_single_element_composite_over_an_over_cap_part_stays_collapsed()
    {
        var set = await FoldAsync(nameof(Cases.CompositeOverCap));

        Assert.Equal(["key:{?}"], Values(set));
        Assert.True(set.Collapsed);
    }

    [Fact]
    public async Task Exactly_eight_values_is_not_past_the_cap()
    {
        var set = await FoldAsync(nameof(Cases.AtCap));

        Assert.Equal(["a1", "a2", "a3", "a4", "b1", "b2", "b3", "b4"], Values(set));
        Assert.False(set.Collapsed);
    }

    [Fact]
    public async Task A_single_valued_fold_is_one_element_and_unmarked()
    {
        var set = await FoldAsync(nameof(Cases.Single));

        Assert.Equal(["single:{id}"], Values(set));
        Assert.False(set.Collapsed);
    }

    [Fact]
    public async Task The_set_is_ordered_ordinally()
    {
        var set = await FoldAsync(nameof(Cases.Ordering));

        Assert.Equal(["a", "b", "c", "d"], Values(set));
    }

    [Fact]
    public async Task Two_folds_of_the_same_site_agree_on_the_order()
    {
        var first = await FoldAsync(nameof(Cases.Ordering));
        var second = await FoldAsync(nameof(Cases.Ordering));

        Assert.Equal(Values(first), Values(second));
    }

    [Fact]
    public async Task A_branchy_key_becomes_one_vertex_per_value()
    {
        var graph = await IndexAsync("SourceFiles/KeyTemplates.cs");
        var templates = graph.CacheKeys.Select(key => key.Template).ToArray();

        Assert.Contains("branch:one", templates);
        Assert.Contains("branch:two", templates);
        Assert.Contains("conditional:yes", templates);
        Assert.Contains("conditional:no", templates);
    }

    [Fact]
    public async Task A_branchy_url_becomes_one_external_source_per_value()
    {
        var graph = await IndexAsync("SourceFiles/External.cs");
        var templates = graph.ExternalSources.Select(source => source.Template).ToArray();

        Assert.Contains("branch/one", templates);
        Assert.Contains("branch/two", templates);
        Assert.Contains("conditional/yes", templates);
        Assert.Contains("conditional/no", templates);
    }

    /// <summary>Multi-valued SQL is out of scope, and out of scope means the branchy <em>value</em> reads as
    /// one neutral parameter — not that the statement holding it is thrown away. The write is the sharp end:
    /// reducing the whole query would take the <c>UPDATE</c> with it, and a lost write edge is a lost
    /// finding, which is worse than a parameter the parser was never going to read.</summary>
    [Fact]
    public async Task A_branchy_value_in_an_update_keeps_its_table_and_its_write()
    {
        var graph = await IndexAsync("SourceFiles/UnparsedSql.cs");

        Assert.Contains(graph.Tables, table => table.Name.EndsWith("Products", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(graph.Edges.OfType<Writes>(),
                        edge => ((Handler)edge.From).Symbol.Contains("BranchyUpdate", StringComparison.Ordinal) &&
                                ((Table)edge.To).Name.EndsWith("Products", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The same, on a read: the branchy column is a parameter and <c>dbo.Categories</c> survives.
    /// </summary>
    [Fact]
    public async Task A_branchy_value_in_a_select_keeps_its_table_and_its_read()
    {
        var graph = await IndexAsync("SourceFiles/UnparsedSql.cs");

        Assert.Contains(graph.Edges.OfType<Reads>(),
                        edge => ((Handler)edge.From).Symbol.Contains("BranchySelect", StringComparison.Ordinal) &&
                                edge.To is Table table && table.Name.EndsWith("Categories", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>And the branchy statement is not recorded as unresolved SQL: it parsed.</summary>
    [Fact]
    public async Task A_branchy_query_is_not_an_unresolved_sql_row()
    {
        var graph = await IndexAsync("SourceFiles/UnparsedSql.cs");

        Assert.DoesNotContain(graph.Unresolved, item => item.Kind == UnresolvedKind.Sql &&
                                                        item.Snippet.Contains("dbo.Products", StringComparison.Ordinal));
    }

    private static async Task<CacheGraph> IndexAsync(string sourceFile)
    {
        var solution = await FixtureSolution.CreateAsync(sourceFile);
        return await new CallGraphIndexer().IndexAsync(solution, "fixture");
    }

    private static string[] Values(FoldedSet set) => set.Values.Select(value => value.Value).ToArray();

    /// <summary>Folds the argument the named fixture case hands to <c>Use</c>, which is the whole point of
    /// that method: it puts one expression per case where a test can reach it.</summary>
    private static async Task<FoldedSet> FoldAsync(string caseName)
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/FoldedSets.cs");
        var document = solution.Projects.Single().Documents.Single();
        var root = await document.GetSyntaxRootAsync();
        var semanticModel = await document.GetSemanticModelAsync();
        var method = root!.DescendantNodes().OfType<MethodDeclarationSyntax>()
                          .Single(candidate => candidate.Identifier.Text == caseName);
        var use = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                        .Single(candidate => candidate.Expression.ToString() == "Use");

        return await new StringConstantFolder(solution, PLACEHOLDERS)
            .FoldAsync(use.ArgumentList.Arguments[0].Expression, semanticModel!, CancellationToken.None);
    }

    /// <summary>Names for <see cref="FoldAsync"/>, so a renamed fixture case fails to compile rather than
    /// failing to be found at run time.</summary>
    private static class Cases
    {
        public const string TwoBranchLocal = nameof(TwoBranchLocal);
        public const string IfElseLocal = nameof(IfElseLocal);
        public const string Conditional = nameof(Conditional);
        public const string NestedConditional = nameof(NestedConditional);
        public const string Product = nameof(Product);
        public const string Duplicate = nameof(Duplicate);
        public const string Mixed = nameof(Mixed);
        public const string CompoundUpdate = nameof(CompoundUpdate);
        public const string SelfAssignment = nameof(SelfAssignment);
        public const string OverCap = nameof(OverCap);
        public const string CompositeOverCap = nameof(CompositeOverCap);
        public const string AtCap = nameof(AtCap);
        public const string Single = nameof(Single);
        public const string Ordering = nameof(Ordering);
    }
}
