using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The fold's census (ADR 0042, step 1) counts each access an execution is the first to make under the first reason, in a
/// fixed order, that tells it from every access folded before it, and apart from that whether its region is one of an invocation of a
/// root.</summary>
public sealed class FoldCensusTests
{
    private const string SCOPE = "scope:Fixture";
    private const string INSTANCE = "instance:a";
    private const string REGION = "region:r";

    [Fact]
    public void Each_new_access_counts_under_the_first_reason_that_tells_it_from_what_was_folded_before()
    {
        var census = new InterproceduralAccesses.FoldCensus(new TextNumbers(), new Dictionary<string, long>(StringComparer.Ordinal) { ["second"] = 7 });
        var first = Write(INSTANCE, 1, REGION, "Value");
        var firstCounters = Count(census, "first", [first]);

        Assert.Equal(1, firstCounters.Firsts);
        Assert.Equal(1, firstCounters.NewInstance);
        Assert.Equal(0, firstCounters.ModelAccesses);

        var scoped = Write(INSTANCE, 3, $"di|S|S@Scoped|{WholeProgram.INVOCATION_SCOPE}root:R", "Value");
        var fromConstruction = Write(INSTANCE, 2, "region:s", "Value");
        var counters = Count(census, "second",
                             [
                                 fromConstruction,
                                 Write(INSTANCE, 2, REGION, "Other"),
                                 first with { Operation = AccessOperation.Read },
                                 first with { Conditions = [new PathPredicate("canon|subject", PathRelation.Equal, "1", "subject == 1")] },
                                 first with { HeldProtection = ["lock"], HeldProtectionIds = ["scope|lock"] },
                                 first with { IsConstructionLocal = true },
                                 first with { IsFresh = true },
                                 scoped,
                                 Write("instance:b", 1, REGION, "Value")
                             ],
                             new HashSet<Access>([fromConstruction], ReferenceEqualityComparer.Instance));

        Assert.Equal(9, counters.Firsts);
        Assert.Equal(1, counters.FirstsFromConstructions);
        Assert.Equal(1, counters.NewInstance);
        Assert.Equal(2, counters.NewRegion);
        Assert.Equal(1, counters.NewSite);
        Assert.Equal(1, counters.NewOperation);
        Assert.Equal(1, counters.NewGuards);
        Assert.Equal(1, counters.NewHeld);
        Assert.Equal(1, counters.NewLocal);
        Assert.Equal(1, counters.NewOther);
        Assert.Equal(1, counters.NewRootScoped);
        Assert.Equal(7, counters.ModelAccesses);
        Assert.Equal(counters.Firsts, counters.NewInstance + counters.NewRegion + counters.NewSite + counters.NewOperation +
                                      counters.NewGuards + counters.NewHeld + counters.NewLocal + counters.NewOther);
    }

    /// <summary>One lock held for reading and then for writing, with one display and one id, is another held protection as the fold
    /// tells protections apart, not something else.</summary>
    [Fact]
    public void A_lock_held_in_another_mode_is_another_held_protection()
    {
        var census = new InterproceduralAccesses.FoldCensus(new TextNumbers(), new Dictionary<string, long>(StringComparer.Ordinal));
        var read = Write(INSTANCE, 1, REGION, "Value") with
        {
            HeldProtection = ["gate"],
            HeldProtectionIds = ["scope|gate"],
            HeldProtections = new Dictionary<string, IReadOnlyList<HeldProtection>>(StringComparer.Ordinal)
            {
                ["scope|gate"] = [new HeldProtection(IrSynchronizationPrimitive.ReaderWriterLockSlim, IrLockMode.Read, true)]
            }
        };
        Count(census, "first", [read]);

        var counters = Count(census, "second",
                             [
                                 read with
                                 {
                                     HeldProtections = new Dictionary<string, IReadOnlyList<HeldProtection>>(StringComparer.Ordinal)
                                     {
                                         ["scope|gate"] = [new HeldProtection(IrSynchronizationPrimitive.ReaderWriterLockSlim, IrLockMode.Write, true)]
                                     }
                                 }
                             ]);

        Assert.Equal(1, counters.Firsts);
        Assert.Equal(1, counters.NewHeld);
        Assert.Equal(0, counters.NewOther);
    }

    /// <summary>A new access is told apart from what was folded before it, accesses of its own execution folded before it among them:
    /// two new accesses of one instance in one execution make one new instance.</summary>
    [Fact]
    public void An_execution_counts_its_own_earlier_new_accesses_as_folded_before()
    {
        var census = new InterproceduralAccesses.FoldCensus(new TextNumbers(), new Dictionary<string, long>(StringComparer.Ordinal));

        var counters = Count(census, "first", [Write(INSTANCE, 1, REGION, "Value"), Write(INSTANCE, 1, REGION, "Other")]);

        Assert.Equal(2, counters.Firsts);
        Assert.Equal(1, counters.NewInstance);
        Assert.Equal(1, counters.NewSite);
    }

    /// <summary>Gives the census one execution's new accesses and returns what it counted.</summary>
    /// <param name="census">The census.</param>
    /// <param name="execution">The execution's id.</param>
    /// <param name="firsts">The accesses the execution is the first to make, in order.</param>
    /// <param name="fromConstructions">Those of them a construction entry's visits made.</param>
    private static CollectionCounters Count(InterproceduralAccesses.FoldCensus census, string execution, Access[] firsts,
                                            IReadOnlySet<Access>? fromConstructions = null)
    {
        census.Firsts.AddRange(firsts);
        var counters = new CollectionCounters();
        census.Count(execution, counters, fromConstructions);
        Assert.Empty(census.Firsts);
        return counters;
    }

    /// <summary>A write of an instance at an operation to a field of a region.</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="operation">The operation.</param>
    /// <param name="region">The region.</param>
    /// <param name="member">The field.</param>
    private static Access Write(string instance, int operation, string region, string member) =>
        new Access(new AccessResource("Fixture", SCOPE, region, [member], new MemberKey("Fixture.State", member, IrFieldKind.Field), region),
                   AccessOperation.Write, ROOT, "S.Write", SPAN, [], [], [], [], [])
        {
            InstanceId = instance,
            BodyId = "body:write",
            OperationId = operation,
            ExecutionId = "execution"
        };

    private static readonly InvocationPolicy POLICY = new(Multiplicity.Repeated, SelfOverlap.MayOverlap, SCOPE);
    private static readonly AccessRoot ROOT = new("root", "S.Root", "root", "test", "test", POLICY, SCOPE);
    private static readonly Analysis.SourceSpan SPAN = new("Case.cs", 1, 1, 1, 2);
}
