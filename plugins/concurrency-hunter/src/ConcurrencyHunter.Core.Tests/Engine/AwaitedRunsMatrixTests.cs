using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class AwaitedRunsMatrixTests
{
    public static readonly string[] Relations = ["concurrent", "sequential", "sequential-awaited"];
    public static readonly string[] Calls = ["once", "loop", "http"];
    public static readonly (string Member, string Wait)[] Results =
    [
        ("Run", "await"), ("Run", "wait"), ("Run", "get"), ("Run", "none"),
        ("RunValue", "await"), ("RunValue", "get"), ("RunValue", "none"),
        ("TaskOf", "await"), ("TaskOf", "result"), ("TaskOf", "wait"), ("TaskOf", "none"),
        ("ValueOf", "await"), ("ValueOf", "result"), ("ValueOf", "get"), ("ValueOf", "none"),
        ("TaskBare", "await"), ("TaskBare", "result"), ("ValueBare", "await"), ("ValueBare", "get"), ("Fire", "none")
    ];

    public static IEnumerable<object[]> Cells() =>
        from relation in Relations from result in Results from call in Calls
        select new object[] { relation, result.Member, result.Wait, call };

    [Fact]
    public void There_are_180_cells_covering_every_axis()
    {
        var cells = Cells().ToArray();
        Assert.Equal(180, cells.Length);
        Assert.Equal(180, cells.Select(cell => string.Join("/", cell)).Distinct().Count());
        Assert.All(Relations, relation => Assert.Equal(60, cells.Count(cell => (string)cell[0] == relation)));
        Assert.All(Results, result => Assert.Equal(9, cells.Count(cell => (string)cell[1] == result.Member && (string)cell[2] == result.Wait)));
        Assert.All(Calls, call => Assert.Equal(60, cells.Count(cell => (string)cell[3] == call)));
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_has_exactly_the_required_pairs(string relation, string member, string wait, string call)
    {
        var generic = member is "TaskOf" or "ValueOf" or "TaskBare" or "ValueBare";
        var invocation = $"Runs.Lib.{member}(async () => {{ await Task.Yield(); _state.C = 1; {(generic ? "return 1;" : "")} }})";
        var work = AwaitedRunsFixture.Consume(invocation, wait) + "\n_state.C = 2;";
        if (call == "loop")
            work = "for (var i = 0; i < 2; i++) { " + work + " }";
        var run = AwaitedRunsFixture.Run(work, relation, http: call == "http");
        var serialized = relation == "sequential-awaited" && call == "once";
        AwaitedRunsFixture.AssertPairs(run, after: !(serialized && (wait != "none" || member == "Fire")), self: !serialized);
    }
}
