using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The CoreLib generation's member list, its snapshot rows and the comparison of two snapshots.</summary>
public sealed class CoreLibGenerationTests
{
    [Fact]
    public void Public_members_are_the_methods_a_caller_outside_the_assembly_can_reach()
    {
        var library = GenerationRuns.Compile("""
            namespace Lib
            {
                public class A
                {
                    public void P() { } protected void Q() { } protected internal void R() { } internal void S() { } private void T() { }
                    private protected void U() { }
                    public class Nested { public void V() { } }
                    internal class Hidden { public void W() { } }
                }
                internal class Internal { public void X() { } }
            }
            """);

        Assert.Equal(["M:Lib.A.#ctor", "M:Lib.A.Nested.#ctor", "M:Lib.A.Nested.V", "M:Lib.A.P", "M:Lib.A.Q", "M:Lib.A.R"],
                     CoreLibGeneration.PublicMembers(library.Compilation!));
    }

    [Fact]
    public void A_row_names_the_outcome_the_cause_codes_and_the_model_and_reads_back()
    {
        var model = GenerationSnapshotRow.Of(GenerationRuns.Trace("public static class Api { public static void Run(Action done) { done(); } }",
                                                                  "M:Lib.Api.Run(System.Action)").Answer);
        var refused = GenerationSnapshotRow.Of(GenerationRuns.Trace("public abstract class Node { } public static class Api { " +
                                                                    "public static void Send(Node node, Action done) { Sink.Take(node); done(); } }",
                                                                    "M:Lib.Api.Send(Lib.Node,System.Action)").Answer);

        Assert.Equal(GenerationSnapshotRow.MODEL, model.Outcome);
        Assert.Empty(model.Causes);
        Assert.StartsWith("{", model.Model);
        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, refused.Outcome);
        Assert.Contains(ModelCauses.PROBE_HANDED_TO_UNSEEN, refused.Causes);
        Assert.Equal("", refused.Model);
        foreach (var row in new[] { model, refused })
        {
            var read = GenerationSnapshotRow.Parse(row.Line);
            Assert.Equal(row.Line, read.Line);
            Assert.Equal(row.Causes, read.Causes);
        }
    }

    [Fact]
    public void A_snapshot_reads_back_its_header_and_rows_sorted()
    {
        var snapshot = new GenerationSnapshot("CoreLib 8.0.0.0", [Row("b", "vocabulary", ["value-unnamed"]), Row("a", "not-a-candidate")]);

        var read = GenerationSnapshot.Parse(snapshot.Lines);

        Assert.Equal("CoreLib 8.0.0.0", read.Implementation);
        Assert.Equal(["a", "b"], read.Rows.Select(row => row.Member));
        Assert.Equal(snapshot.Lines, read.Lines);
    }

    [Fact]
    public void A_member_twice_in_a_snapshot_is_refused()
    {
        var lines = new[] { "# CoreLib", Row("a", "model").Line, Row("a", "vocabulary").Line };

        Assert.Throws<FormatException>(() => GenerationSnapshot.Parse(lines));
    }

    [Fact]
    public void The_same_snapshots_compare_empty()
    {
        var snapshot = new GenerationSnapshot("X", [Row("a", "model", model: "{}"), Row("b", "vocabulary", ["value-unnamed"])]);

        Assert.Empty(CoreLibGeneration.Compare(snapshot, snapshot, 5));
    }

    [Fact]
    public void The_comparison_names_the_implementation_first_then_members_outcomes_models_and_causes()
    {
        var before = new GenerationSnapshot("X", [Row("a", "model", model: "{}"), Row("b", "vocabulary", ["value-unnamed"]), Row("c", "not-a-candidate")]);
        var after = new GenerationSnapshot("Y", [Row("a", "model", model: "{\"m\":1}"), Row("b", "model", model: "{}"), Row("d", "not-a-candidate")]);

        Assert.Equal(
        [
            "implementation: X -> Y",
            "members gone: 1, e.g. c",
            "members new: 1, e.g. d",
            "outcome model: 1 -> 2",
            "outcome vocabulary: 1 -> 0",
            "vocabulary -> model: 1, e.g. b",
            "models changed: 1, e.g. a",
            "cause value-unnamed: 1 -> 0"
        ], CoreLibGeneration.Compare(before, after, 5));
    }

    [Fact]
    public void Causes_that_change_under_the_same_outcome_are_named()
    {
        var before = new GenerationSnapshot("X", [Row("a", "vocabulary", ["value-unnamed"])]);
        var after = new GenerationSnapshot("X", [Row("a", "vocabulary", ["result-unnamed"])]);

        Assert.Equal(["cause result-unnamed: 0 -> 1", "cause value-unnamed: 1 -> 0", "causes changed, outcome the same: 1, e.g. a"],
                     CoreLibGeneration.Compare(before, after, 5));
    }

    /// <summary>A snapshot row.</summary>
    /// <param name="member">The member.</param>
    /// <param name="outcome">The outcome.</param>
    /// <param name="causes">The cause codes.</param>
    /// <param name="model">The model's JSON.</param>
    private static GenerationSnapshotRow Row(string member, string outcome, string[]? causes = null, string model = "") =>
        new(member, outcome, causes ?? [], model);
}
