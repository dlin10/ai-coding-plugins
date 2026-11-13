using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class FreshDeduplicationTests
{
    [Theory]
    [InlineData("Equal", "FreshFirst")]
    [InlineData("Equal", "NotFreshFirst")]
    [InlineData("Stronger", "FreshFirst")]
    [InlineData("Stronger", "NotFreshFirst")]
    [InlineData("Weaker", "FreshFirst")]
    [InlineData("Weaker", "NotFreshFirst")]
    [InlineData("Incomparable", "FreshFirst")]
    [InlineData("Incomparable", "NotFreshFirst")]
    public void Fresh_and_not_fresh_observations_of_one_access_are_kept_apart(string relation, string order)
    {
        var (freshGuard, otherGuard) = relation switch
        {
            "Equal" => ("i > 0", "i > 0"),
            "Stronger" => ("i > 0 && i < 10", "i > 0"),
            "Weaker" => ("i > 0", "i > 0 && i < 10"),
            "Incomparable" => ("i > 0", "i < 10"),
            _ => throw new ArgumentOutOfRangeException(nameof(relation))
        };
        var freshCall = $"if ({freshGuard}) Helper.Write(h);";
        var otherCall = $"if ({otherGuard}) Helper.Write(repository.Latest);";
        var calls = order == "FreshFirst" ? freshCall + otherCall : otherCall + freshCall;
        var run = Analyze($$"""
            public sealed class Hook { public int Hits; }
            public sealed class Repository { public Hook Latest = null!; }
            public static class Helper { public static void Write(Hook h) { h.Hits = 1; } }
            public class HooksController(Repository repository) : ControllerBase
            {
                public void Post(int i)
                {
                    var h = new Hook();
                    repository.Latest = h;
                    {{calls}}
                }
            }
            """ + Startup("services.AddSingleton<Repository>();"));

        var writes = run.Of("Hits").Where(access => access.Operation == AccessOperation.Write && access.Symbol == "Helper.Write(Hook)").ToArray();
        Assert.Equal(2, writes.Length);
        var fresh = Assert.Single(writes, access => access.IsFresh);
        var other = Assert.Single(writes, access => !access.IsFresh);
        Assert.Equal(fresh.Resource.Identity, other.Resource.Identity);
        AssertGuards(fresh, freshGuard);
        AssertGuards(other, otherGuard);
        Assert.Equal(order == "FreshFirst", Array.IndexOf(writes, fresh) < Array.IndexOf(writes, other));
        var reference = ReferencePair(run.Collection.Accesses, run.Execution.Analysis, run.Execution.Heap.Heap);
        Assert.Equal(PairKeys(reference.Pairs, run.Collection.Accesses), PairKeys(run.Pairs.Pairs, run.Collection.Accesses));
        Assert.Equal(reference.Skips.OrderBy(pair => pair.Key), run.Pairs.Skips.OrderBy(pair => pair.Key));
    }

    private static void AssertGuards(Access access, string guard)
    {
        var expected = guard.Split(" && ").Select(part => part.Contains('>') ? "Greater:0" : "Less:10").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, access.Conditions.Select(condition => $"{condition.Relation}:{condition.Value}").Order(StringComparer.Ordinal).ToArray());
        Assert.All(access.Conditions, condition => Assert.Equal("i", condition.Text));
    }

    private static string[] PairKeys(IReadOnlyList<AccessPair> pairs, IReadOnlyList<Access> accesses) =>
        pairs.Select(pair => $"{Array.IndexOf(accesses.ToArray(), pair.First)}:{Array.IndexOf(accesses.ToArray(), pair.Second)}")
             .Order(StringComparer.Ordinal).ToArray();
}
