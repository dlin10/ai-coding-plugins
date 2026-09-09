using CacheDetective.Caching;
using CacheDetective.Graph;
using Xunit;

namespace CacheDetective.Tests.Graph;

/// <summary>
/// Two solutions declaring one key, one of which cannot classify it because an unresolved call blocks
/// the walk. Reclassifying only the rows the incoming replacement brought in left the earlier solution's
/// opinion stuck at <c>unknown</c>, so A then B gave <c>cache</c> and one unresolved row while B then A
/// gave <c>unknown</c> and three — the same two solutions, the same graph, two answers.
/// </summary>
public sealed class BlockedRoleOrderTests
{
    private const string Template = "product:{id}";

    [Fact]
    public void Either_order_gives_the_same_role_when_one_solution_is_blocked()
    {
        var first = Merge("one", "two");
        var second = Merge("two", "one");

        Assert.Equal(Role(first), Role(second));
    }

    [Fact]
    public void Either_order_gives_the_same_unresolved_rows_when_one_solution_is_blocked()
    {
        var first = Rows(Merge("one", "two"));
        var second = Rows(Merge("two", "one"));

        Assert.Equal(first, second);
    }

    /// <summary>
    /// A role that only holds because of another solution does not survive that solution leaving. Merged,
    /// "two"'s own contribution classifies as <c>cache</c> — it can reach the read in "one" — and its
    /// blocked row is dropped. Removing "one" then left <c>cache</c> standing with nothing to support it:
    /// the replacement reclassified only the rows some solution still had blocked, and there were none.
    /// <para>What the workspace holds afterwards has to be what a graph built from "two" alone holds, and
    /// that is how this asserts it: the same role and the same unresolved rows, by the same semantic
    /// identity the order tests above use.</para>
    /// </summary>
    [Fact]
    public void Removing_the_resolving_solution_restores_the_blocked_role()
    {
        var workspace = Merge("one", "two");
        Assert.Equal("cache", Role(workspace));

        workspace.ReplaceSolution("one", new CacheGraph());
        _ = workspace.Edges.Count;

        var alone = new CacheGraph();
        alone.ReplaceSolution("two", Solution("two"));
        _ = alone.Edges.Count;

        Assert.Equal(Role(alone), Role(workspace));
        Assert.Equal(Rows(alone), Rows(workspace));
    }

    /// <summary>
    /// Re-indexing one solution of a merged pair has to leave what building both from scratch would build.
    /// While "two" was blocked, the merge answered for it and recorded that the answer was borrowed; when
    /// "two" came back with a classification of its own — <c>store</c>, decided on its own graph — that
    /// record was still there, and the reclassification overwrote the incoming answer with <c>cache</c>
    /// read off the merged graph. A fresh build of the same two solutions reports the disagreement instead.
    /// </summary>
    [Fact]
    public void Re_indexing_a_solution_matches_a_fresh_graph_of_the_same_solutions()
    {
        var workspace = Merge("one", "two");
        Assert.Equal("cache", Role(workspace));

        workspace.ReplaceSolution("two", Storing("two"));
        _ = workspace.Edges.Count;

        var fresh = new CacheGraph();
        fresh.ReplaceSolution("one", Solution("one"));
        _ = fresh.Edges.Count;
        fresh.ReplaceSolution("two", Storing("two"));
        _ = fresh.Edges.Count;

        Assert.Equal(Role(fresh), Role(workspace));
        Assert.Equal(Rows(fresh), Rows(workspace));
    }

    /// <summary>A contribution that classifies itself: its handler caches the key and reaches nothing at
    /// all, so it is a store on its own graph and needs nobody to answer for it.</summary>
    private static CacheGraph Storing(string name)
    {
        var graph = new CacheGraph();
        var handler = new Handler(name, $"{name}.Put", "http", $"{name}.cs", 1) { Project = name };
        graph.AddEdge(new Caches(handler, new CacheKey(Template, "redis", null, [], null), Confidence.Confirmed));
        new CacheRoleClassifier().Classify(graph, name);
        return graph;
    }

    private static string? Role(CacheGraph graph) =>
        graph.CacheKeys.Single(key => key.Template == Template).Role;

    private static IReadOnlyList<string> Rows(CacheGraph graph) =>
        graph.Unresolved.Select(item => $"{item.Kind}|{item.Solution ?? "-"}").Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Merges the two solutions in the order given. "one" reads a table through its handler, so its key is
    /// a cache; "two" declares the same key on a handler whose only call is unresolved, so its own
    /// classification cannot finish.
    /// <para>The read of <c>Edges</c> between the two replaces is not decoration: <c>CurrentCounts</c> in
    /// the MCP session reads it after every <c>index_solution</c>, so the working path always has it.
    /// It materialises the edge cache against the version as it then stands, and until the replacement
    /// moved the version on, the reclassification that followed ran against that stale list — a graph
    /// with none of the incoming solution's handlers or reads in it.</para>
    /// </summary>
    private static CacheGraph Merge(string firstSolution, string secondSolution)
    {
        var workspace = new CacheGraph();
        foreach (var name in new[] { firstSolution, secondSolution })
        {
            workspace.ReplaceSolution(name, Solution(name));
            _ = workspace.Edges.Count;
        }

        return workspace;
    }

    private static CacheGraph Solution(string name)
    {
        var graph = new CacheGraph();
        var handler = new Handler(name, $"{name}.Get", "http", $"{name}.cs", 1) { Project = name };
        graph.AddEdge(new Caches(handler, new CacheKey(Template, "redis", null, [], null), Confidence.Confirmed));
        if (name == "one")
        {
            graph.AddEdge(new Reads(handler, new Table("dbo.Products", "shop"), Confidence.Confirmed));
        }
        else
        {
            graph.AddUnresolved(UnresolvedKind.Call, handler, $"{name}.cs", 2, "Mystery()", "the call could not be resolved");
        }

        new CacheRoleClassifier().Classify(graph, name);
        return graph;
    }
}
