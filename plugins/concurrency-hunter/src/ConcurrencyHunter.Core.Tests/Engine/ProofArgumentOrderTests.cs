using System.Collections;
using System.Reflection;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The completion proof of the queried passes answers a question the same whichever of its two accesses comes first (#490): its
/// disjuncts are the same either way round, and each reads only the waits its own climbs pass, forgotten before every disjunct, so that a
/// disjunct that fails leaves no wait for the next. Every pair of two executions of one resource, at least one a write, is asked both ways
/// round on a graph whose proof keeps one answer at a time, so that every question is proven anew, on timing fixtures, every demo scope and
/// every eShop scope; the path search is symmetric by construction, so an answer apart would be the proof's. The questions go to
/// <see cref="HappensBefore.OrderedBy"/> directly, past pairing and the filters it asks before the timing, and a question a path orders
/// never reaches the proof.</summary>
public sealed class ProofArgumentOrderTests
{
    private const BindingFlags NON_PUBLIC = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Hosted work that passes twice: each pass starts outer work that starts two pieces of work and awaits both. Both climbs of
    /// a question about the outer work and a piece, or about the two pieces, pass the outer start.</summary>
    private static readonly string SharedOuterStart = BlockingWaitAssumptionMatrixTests.Worker(
        "for (var pass = 0; pass < 2; pass++) { var outer = Task.Run(async () => { var a = Task.Run(First); var b = Task.Run(Second); await a; await b; After(); }); " +
        "await outer; }");

    /// <summary>Hosted work that passes twice: each pass starts outer work that starts one piece of work, catches the await of it and
    /// calls <c>Second</c> inline, and the pass catches the await of the outer work: the waits the climbs record read a fault as
    /// completion or not.</summary>
    private static readonly string OuterCatches = BlockingWaitAssumptionMatrixTests.Worker(
        "for (var pass = 0; pass < 2; pass++) { var outer = Task.Run(async () => { var a = Task.Run(First); try { await a; } catch (Exception) { } Second(); }); " +
        "try { await outer; } catch (Exception) { } }");

    /// <summary>Timing fixtures the proof answers on: a handle in a field, a child waited for twice, two children of one parent, a task
    /// forwarder, a retry loop, a timer, a <c>Parallel</c> call, an admitted sequential-awaited call and two async entries of one.</summary>
    private static readonly Func<EngineRun>[] Fixtures =
    [
        () => Analyze(BlockingWaitAssumptionMatrixTests.Worker("for (var pass = 0; pass < 2; pass++) { _task = Task.Run(First); await _task; }", "private Task? _task;")),
        () => Analyze(BlockingWaitAssumptionMatrixTests.Worker("for (var pass = 0; pass < 2; pass++) { var own = Task.Run(First); own.Wait(); await own; }")),
        () => Analyze(BlockingWaitAssumptionMatrixTests.Worker("for (var pass = 0; pass < 2; pass++) { var a = Task.Run(First); var b = Task.Run(Second); a.Wait(); b.Wait(); } " +
                                                               "await Task.CompletedTask;")),
        () => Analyze(BlockingWaitAssumptionMatrixTests.Worker("for (var pass = 0; pass < 2; pass++) await Forward();",
                                                               "private Task Forward() => DrainAsync(); private async Task DrainAsync() { await Task.Run(First); }")),
        () => Analyze(BlockingWaitAssumptionMatrixTests.Worker("for (var attempt = 0; attempt < 3; attempt++) { try { var own = Task.Run(First); own.Wait(); break; } " +
                                                               "catch (AggregateException) { } } Second(); await Task.CompletedTask;")),
        () => Analyze(BlockingWaitAssumptionMatrixTests.Worker("var timer = new Timer(_ => First(), null, 0, 100); await timer.DisposeAsync(); Second();")),
        () => Analyze(BlockingWaitAssumptionMatrixTests.Worker("Parallel.For(0, 4, _ => First()); Second(); await Task.CompletedTask;")),
        () => AwaitedRunsFixture.Run(AwaitedRunsFixture.Direct),
        () => AwaitedRunsFixture.Run("await Helper();\nawait Helper();", members:
                                     "private async Task Helper() { await Runs.Lib.Run(async () => { _state.C = 3; await Task.Yield(); _state.C = 1; }); }")
    ];

    /// <summary>On every fixture, every question gets one answer either way round.</summary>
    [Fact]
    public void Fixture_questions_are_proven_alike_either_way_round() =>
        Assert.True(Fixtures.Sum(run => AssertAlikeEitherWayRound(Input(run().Execution))) > 0, "a question the proof orders");

    /// <summary>A question about the outer work and a piece it starts, or about two pieces one outer start starts, where the disjuncts
    /// climb through that start, gets one answer either way round.</summary>
    /// <param name="name">The fixture.</param>
    [Theory]
    [InlineData(nameof(SharedOuterStart))]
    [InlineData(nameof(OuterCatches))]
    public void A_question_whose_disjuncts_climb_through_one_outer_start_is_proven_alike_either_way_round(string name) =>
        AssertAlikeEitherWayRound(Input(Analyze(name == nameof(SharedOuterStart) ? SharedOuterStart : OuterCatches).Execution));

    /// <summary>A question about the two pieces one outer start of <see cref="SharedOuterStart"/> starts, asked of a graph built anew so
    /// that the proof has no answer for it, is not ordered and leaves the proof no waits. Its disjuncts that climb through the outer start
    /// record waits there; the last one, whose execution's start is no async return, refuses before it climbs; and the waits are forgotten
    /// before every disjunct, so the last leaves the table as it found it, empty.</summary>
    [Fact]
    public void A_question_whose_last_disjunct_refuses_before_climbing_leaves_the_proof_no_waits()
    {
        var input = Input(Analyze(SharedOuterStart).Execution);
        var analysis = ExecutionModel.Build(input.Scope, input.Heap, CancellationToken.None);
        var views = InterproceduralAccesses.Collect(input with { Executions = analysis }, CancellationToken.None).Accesses
                                           .Where(view => view.Resource.Member.Name == "Value" && view.Operation == AccessOperation.Write).ToArray();
        var lines = (Usings + SharedOuterStart).Split('\n');
        var firsts = views.Where(view => view.Source.StartLine == Array.FindIndex(lines, line => line.Contains("Value = 1", StringComparison.Ordinal)) + 1).ToArray();
        var seconds = views.Where(view => view.Source.StartLine == Array.FindIndex(lines, line => line.Contains("Value = 2", StringComparison.Ordinal)) + 1).ToArray();
        var order = Assert.IsType<HappensBefore>(typeof(ExecutionAnalysis).GetField("_order", NON_PUBLIC)!.GetValue(analysis));
        Assert.NotEmpty(firsts);
        Assert.NotEmpty(seconds);

        foreach (var (first, second) in firsts.SelectMany(first => seconds.Select(second => (first, second))))
        {
            Assert.Null(order.OrderedBy(first, second));
            Assert.Empty((IDictionary)Field(Field(order, "_passCompletion")!, "_completions")!);
        }
    }

    /// <summary>On every demo scope, every question gets one answer either way round, and the proof orders some.</summary>
    [Fact]
    public async Task Demo_questions_are_proven_alike_either_way_round()
    {
        var proven = 0;
        await ParallelCollectionTests.ForEachDemoScope(input => Interlocked.Add(ref proven, AssertAlikeEitherWayRound(input)));
        Assert.True(proven > 0, "a question the proof orders");
    }

    /// <summary>On every eShop scope, every question gets one answer either way round, and the proof orders some.</summary>
    [RequiresEShopFact]
    public async Task EShop_questions_are_proven_alike_either_way_round()
    {
        var proven = 0;
        await ParallelCollectionTests.ForEachScope(Path.Combine(Environment.GetEnvironmentVariable(RequiresEShopFactAttribute.VARIABLE)!, "src",
                                                                "eShopOnContainers-ServicesAndWebApps.sln"),
                                                   input => Interlocked.Add(ref proven, AssertAlikeEitherWayRound(input)));
        Assert.True(proven > 0, "a question the proof orders");
    }

    /// <summary>Builds and collects a scope's model anew, has its proof keep one answer at a time, asks every question both ways round
    /// and asserts one answer.</summary>
    /// <param name="input">The scope's input.</param>
    /// <returns>How many questions the proof orders; none on a scope without a happens-before graph.</returns>
    private static int AssertAlikeEitherWayRound(InterproceduralInput input)
    {
        var analysis = ExecutionModel.Build(input.Scope, input.Heap, CancellationToken.None);
        var views = InterproceduralAccesses.Collect(input with { Executions = analysis }, CancellationToken.None).Accesses;
        if (typeof(ExecutionAnalysis).GetField("_order", NON_PUBLIC)!.GetValue(analysis) is not HappensBefore order)
            return 0;
        order.ProofAnswerLimit = 1;
        var creatorOf = typeof(ExecutionAnalysis).GetMethod("TreeCreator", NON_PUBLIC)!;
        var proven = 0;
        foreach (var group in views.GroupBy(view => view.Resource.Identity, StringComparer.Ordinal))
        {
            var members = group.ToArray();
            for (var one = 0; one < members.Length; one++)
            {
                for (var other = one; other < members.Length; other++)
                {
                    var (first, second) = (members[one], members[other]);
                    if (first.Operation == AccessOperation.Read && second.Operation == AccessOperation.Read)
                        continue;
                    var creator = (string?)creatorOf.Invoke(analysis, [first, second]);
                    var forward = order.OrderedBy(first, second, creator);
                    Assert.True(forward == order.OrderedBy(second, first, creator), $"{first.ExecutionId} | {second.ExecutionId}");
                    proven += forward == TimingBranch.PassCompletion ? 1 : 0;
                }
            }
        }

        return proven;
    }

    /// <summary>A private field's value.</summary>
    /// <param name="target">The object.</param>
    /// <param name="name">The field.</param>
    private static object? Field(object target, string name) => target.GetType().GetField(name, NON_PUBLIC)!.GetValue(target);
}
