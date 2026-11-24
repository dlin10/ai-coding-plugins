using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The timing of a pair and the assumptions its order rests on are a function of the graph and the pair, not of the questions
/// asked before it (#495). The graph keeps the assumption sets the readings of all questions so far took; the assumptions of one pair
/// read it only to tell whether any reading was taken at all and which sets to try as needed, and try any other set as an alternative.
/// Every pair of two executions of one resource, at least one a write, is asked on graphs built and collected anew: forward, in reverse,
/// every other one alone, and one by one on a graph of its own, and gets one answer each time. The rule itself is held on made-up
/// answers of the replays, each pair given by its proofs, with the combinations of withheld sets it asks; and on a fixture, a pair
/// ordered on either of two routes rests on both and asks the replays the rule says.</summary>
public sealed class AssumptionHistoryTests
{
    /// <summary>How many of a scope's questions with assumptions are each asked on a graph of their own.</summary>
    private const int ALONE = 20;

    /// <summary>The assumption set of the thread context and observers, the one a letter <c>C</c> names.</summary>
    private const OrderingAssumption CONTEXT = OrderingAssumption.ThreadContext | OrderingAssumption.Observers;

    /// <summary>The assumption set of thread-pool entry and observers, the one a letter <c>P</c> names.</summary>
    private const OrderingAssumption POOL = OrderingAssumption.ThreadPoolEntry | OrderingAssumption.Observers;

    /// <summary>The assumption set of all three flags, the one a letter <c>B</c> names, and every flag.</summary>
    private const OrderingAssumption BOTH = OrderingAssumption.ThreadPoolEntry | OrderingAssumption.ThreadContext | OrderingAssumption.Observers;

    /// <summary>The assumption sets in the order the rule tries them, a set's place being its bit in a combination.</summary>
    private static readonly OrderingAssumption[] SetsInOrder = [CONTEXT, POOL, BOTH];

    /// <summary>A pair ordered on either of two routes: the worker's own wait for the work, read on its thread context, or a relay on the
    /// thread pool that waits for it, read on thread-pool entry, whose normal return the worker waits for.</summary>
    private static readonly string TwoRoutes = BlockingWaitAssumptionMatrixTests.Worker(
        "var w = Task.Run(First); var relay = Task.Run(() => { try { w.Wait(); } catch (AggregateException) { } }); " +
        "try { w.Wait(); } catch (AggregateException) { } relay.Wait(); Second(); await Task.CompletedTask;");

    /// <summary>The fixtures: waits whose exceptional exit orders a pair on thread-pool entry, on the thread context, on both, or on
    /// either of two routes.</summary>
    private static readonly string[] Fixtures =
    [
        BlockingWaitAssumptionMatrixTests.Worker("for (var attempt = 0; attempt < 3; attempt++) { try { var own = Task.Run(First); own.Wait(); break; } " +
                                                 "catch (AggregateException) { } } Second(); await Task.CompletedTask;"),
        BlockingWaitAssumptionMatrixTests.Worker("var outer = Task.Run(() => { var extra = Task.Factory.StartNew(() => { }); var inner = Task.Run(() => { First(); return 1; }); " +
                                                 "try { inner.Wait(); } catch (AggregateException) { } try { Task.WaitAll(inner, extra); } catch (AggregateException) { } }); " +
                                                 "try { outer.Wait(); } catch (AggregateException) { } Second(); await Task.CompletedTask;"),
        BlockingWaitAssumptionMatrixTests.Worker("var t = Task.Factory.StartNew(First); try { t.Wait(); } catch (AggregateException) { } " +
                                                 "var u = Task.Run(() => { try { t.Wait(); } catch (AggregateException) { } Second(); }); " +
                                                 "try { u.Wait(); } catch (AggregateException) { } After(); await Task.CompletedTask;"),
        TwoRoutes
    ];

    /// <summary>On every fixture, every question is answered alike forward, in reverse, with every other one asked alone, and on a
    /// graph of its own.</summary>
    [Fact]
    public void Fixture_questions_are_answered_alike_whatever_was_asked_before()
    {
        var withAssumptions = Fixtures.Sum(source => AssertAnsweredAlike(Input(Execute(source)), int.MaxValue));

        Assert.True(withAssumptions > 0, "a question whose order rests on assumptions");
    }

    /// <summary>On every demo scope, every question is answered alike forward, in reverse and with every other one asked alone, and the
    /// first 20 questions with assumptions of each scope (<see cref="ALONE"/>), and only those, alike on a graph of their own.</summary>
    [Fact]
    public async Task Demo_questions_are_answered_alike_whatever_was_asked_before() =>
        await ParallelCollectionTests.ForEachDemoScope(input => AssertAnsweredAlike(input, ALONE));

    /// <summary>On every eShop scope, every question is answered alike forward, in reverse and with every other one asked alone, and the
    /// first 20 questions with assumptions of each scope (<see cref="ALONE"/>), and only those, alike on a graph of their own.</summary>
    [RequiresEShopFact]
    public async Task EShop_questions_are_answered_alike_whatever_was_asked_before() =>
        await ParallelCollectionTests.ForEachScope(Path.Combine(Environment.GetEnvironmentVariable(RequiresEShopFactAttribute.VARIABLE)!, "src",
                                                                "eShopOnContainers-ServicesAndWebApps.sln"),
                                                   input => AssertAnsweredAlike(input, ALONE));

    /// <summary>A pair ordered on either of two routes on different assumption sets — the pair is ordered while <see cref="CONTEXT"/> or
    /// <see cref="POOL"/> is left, whatever <see cref="BOTH"/> is — rests on both, thread-pool entry among them. Its question asks the
    /// graph once and the replays five times: all three sets withheld, each of the two that the pair's own decision read, then the two
    /// combinations that leave one of them alone, after which the union holds every flag and the third set is not tried.</summary>
    [Fact]
    public void A_pair_ordered_on_either_of_two_routes_rests_on_both_and_asks_no_more_than_that_takes()
    {
        var run = Analyze(TwoRoutes);
        var first = Assert.Single(run.Of("Value"), access => access.Operation == AccessOperation.Write && access.Source.StartLine == Line(TwoRoutes, "Value = 1"));
        var second = Assert.Single(run.Of("Value"), access => access.Operation == AccessOperation.Write && access.Source.StartLine == Line(TwoRoutes, "Value = 2"));
        var analysis = run.Execution.Analysis;
        var counters = Assert.IsType<TimingCounters>(analysis.TimingCounters);
        var before = counters.Copy();

        Assert.Equal(PairTiming.Ordered, analysis.Timing(first, second, out var assumed));

        string[] all = [OrderingAssumptions.OBSERVERS, OrderingAssumptions.THREAD_CONTEXT, OrderingAssumptions.THREAD_POOL_ENTRY];
        Assert.Equal(all, assumed.Order(StringComparer.Ordinal));
        Assert.Equal((6L, 1L), (counters.OrderQueries - before.OrderQueries, counters.AssumptionQueries - before.AssumptionQueries));
    }

    /// <summary>A pair ordered with every set withheld rests on none, and nothing more is asked.</summary>
    [Fact]
    public void A_pair_ordered_without_any_set_rests_on_none() => AssertRule("CPB", [""], OrderingAssumption.None, "CPB");

    /// <summary>A pair every two sets order and no set alone needs none of them, and no set completes another: every proof needs two and
    /// each set has a proof without it, so the pair rests on all three. Once neither <c>C</c> nor <c>P</c> orders it alone, <c>B</c>, which
    /// holds every flag, is not asked, as either answer gives every flag.</summary>
    [Fact]
    public void A_pair_every_two_sets_order_and_none_alone_rests_on_all_three() =>
        AssertRule("CPB", ["CP", "CB", "PB"], BOTH, "CPB", "C", "P", "B", "PB", "CB");

    /// <summary>A pair that needs one set, which does not order it alone, rests on all three without an alternative asked: each other set
    /// has a proof without it and so completes the needed one, and the needed set with either holds every flag.</summary>
    [Fact]
    public void A_pair_needing_one_set_that_does_not_order_it_alone_rests_on_all_three() =>
        AssertRule("CPB", ["CP", "CB"], BOTH, "CPB", "C", "P", "B", "PB");

    /// <summary>A pair one set orders alone, and only that set, rests on exactly that set's flags: it is needed and orders the pair
    /// without the others.</summary>
    /// <param name="proof">The one set that orders the pair, as its letter.</param>
    /// <param name="others">The combination that withholds the two others, as its letters.</param>
    [Theory]
    [InlineData("C", "PB")]
    [InlineData("P", "CB")]
    public void A_pair_only_one_set_orders_rests_on_exactly_its_flags(string proof, string others) =>
        AssertRule("CPB", [proof], Set(proof[0]), "CPB", "C", "P", "B", others);

    /// <summary>A pair one set orders alone and the other two order together rests on the one alone: none is needed, and neither of the
    /// two orders the pair without the other, so neither is an alternative.</summary>
    [Fact]
    public void A_pair_one_set_orders_alone_and_two_together_rests_on_the_one() =>
        AssertRule("CPB", ["C", "PB"], CONTEXT, "CPB", "C", "P", "B", "PB", "CB", "CP");

    /// <summary>A pair that needs every set rests on all three once two of them are found needed: the third is not asked, and every
    /// question withholds at least one set.</summary>
    [Fact]
    public void A_pair_needing_every_set_rests_on_all_three_once_two_are_needed() => AssertRule("CPB", ["CPB"], BOTH, "CPB", "C", "P");

    /// <summary>A pair ordered on either of two sets rests on both though the questions so far read only one: an alternative is tried
    /// whether or not a question read it, and a set no question read is never tried as needed.</summary>
    [Fact]
    public void A_pair_ordered_on_either_of_two_sets_rests_on_both_though_only_one_was_read() =>
        AssertRule("C", ["C", "P"], BOTH, "CPB", "C", "PB", "CB");

    /// <summary>Every monotone order — a pair ordered with some sets withheld is ordered with fewer withheld: 20 tables over the eight
    /// combinations — with every set of taken sets that holds the sets the pair needs, 104 cases, gets the answer of the rule as first
    /// written (<see cref="RuleAsWritten"/>), with at most seven questions, none withholding no set and none asked twice: what the rule
    /// does not ask could not have changed its answer.</summary>
    [Fact]
    public void Every_monotone_order_with_every_taken_set_gets_the_rule_as_written_asking_each_combination_once()
    {
        var cases = 0;
        for (var table = 0; table < 256; table++)
        {
            var ordered = Enumerable.Range(0, 8).Select(withheld => (table >> withheld & 1) == 1).ToArray();
            if (Enumerable.Range(0, 8).Any(withheld => ordered[withheld] && Enumerable.Range(0, 8).Any(fewer => (fewer & withheld) == fewer && !ordered[fewer])))
                continue;
            var needed = Enumerable.Range(0, 3).Where(set => !ordered[1 << set]).Sum(set => 1 << set);
            foreach (var taken in Enumerable.Range(0, 8).Where(taken => (taken & needed) == needed))
            {
                var asked = new List<int>();
                var answer = HappensBefore.Assumed(Sets(taken).ToHashSet(), withheld =>
                {
                    asked.Add(withheld.Sum(set => 1 << Array.IndexOf(SetsInOrder, set)));
                    return ordered[asked[^1]];
                });

                Assert.Equal(RuleAsWritten(taken, ordered), answer);
                Assert.InRange(asked.Count, 1, 7);
                Assert.DoesNotContain(0, asked);
                Assert.Equal(asked.Count, asked.Distinct().Count());
                cases++;
            }
        }

        Assert.Equal(104, cases);
    }

    /// <summary>The sets of a combination.</summary>
    /// <param name="combination">The combination, a bit per set in <see cref="SetsInOrder"/>.</param>
    private static IEnumerable<OrderingAssumption> Sets(int combination) => SetsInOrder.Where((_, place) => (combination >> place & 1) == 1);

    /// <summary>The rule as first written, asked of every combination at once: the needed sets among the taken ones; their union where
    /// they order the pair alone; otherwise with each other set that orders it together with them alone; where nothing qualifies, every
    /// flag; and nothing where the pair is ordered with every set withheld.</summary>
    /// <param name="taken">The taken sets, a bit per set in <see cref="SetsInOrder"/>.</param>
    /// <param name="ordered">Whether the pair is ordered with each combination withheld, by its bits.</param>
    private static OrderingAssumption RuleAsWritten(int taken, bool[] ordered)
    {
        if (ordered[7])
            return OrderingAssumption.None;
        var needed = Enumerable.Range(0, 3).Where(set => (taken >> set & 1) == 1 && !ordered[1 << set]).Sum(set => 1 << set);
        if (needed != 0 && ordered[7 & ~needed])
            return Sets(needed).Aggregate(OrderingAssumption.None, (union, set) => union | set);
        var completing = Enumerable.Range(0, 3).Where(set => (needed >> set & 1) == 0 && ordered[7 & ~(1 << set) & ~needed]).Sum(set => 1 << set);
        return needed + completing == 0 ? BOTH : Sets(needed | completing).Aggregate(OrderingAssumption.None, (union, set) => union | set);
    }

    /// <summary>Asks every question of a scope on graphs built and collected anew — forward, in reverse, every other one, and the first
    /// ones with assumptions each on a graph of its own — and asserts that each gets one answer.</summary>
    /// <param name="input">The scope's input.</param>
    /// <param name="alone">How many questions with assumptions to ask each on a graph of its own.</param>
    /// <returns>How many questions have assumptions.</returns>
    private static int AssertAnsweredAlike(InterproceduralInput input, int alone)
    {
        var questions = Questions(Fresh(input).Collection.Accesses);
        var all = Enumerable.Range(0, questions.Count).ToArray();
        var forward = Ask(Fresh(input).Analysis, questions, all);
        Assert.Equal(forward, Ask(Fresh(input).Analysis, questions, all.Reverse().ToArray()));
        var odd = Ask(Fresh(input).Analysis, questions, all.Where(index => index % 2 == 1).ToArray());
        Assert.All(odd, answer => Assert.Equal(forward[answer.Key], answer.Value));
        var withAssumptions = all.Where(index => forward[index].Assumed.Length != 0).ToArray();
        foreach (var index in withAssumptions.Take(alone))
            Assert.Equal(forward[index], Ask(Fresh(input).Analysis, questions, [index])[index]);
        return withAssumptions.Length;
    }

    /// <summary>Every pair of two executions of one resource, at least one a write, each pair once.</summary>
    /// <param name="views">Every execution of the scope's accesses.</param>
    private static List<(Access First, Access Second)> Questions(IEnumerable<Access> views)
    {
        var questions = new List<(Access First, Access Second)>();
        foreach (var group in views.GroupBy(view => view.Resource.Identity, StringComparer.Ordinal))
        {
            var members = group.ToArray();
            for (var one = 0; one < members.Length; one++)
            {
                for (var other = one; other < members.Length; other++)
                {
                    if (members[one].Operation != AccessOperation.Read || members[other].Operation != AccessOperation.Read)
                        questions.Add((members[one], members[other]));
                }
            }
        }

        return questions;
    }

    /// <summary>Asks the questions given, in the order given, of one analysis.</summary>
    /// <param name="analysis">The analysis.</param>
    /// <param name="questions">Every question.</param>
    /// <param name="order">The places of the questions to ask, in order.</param>
    private static Dictionary<int, Answer> Ask(ExecutionAnalysis analysis, List<(Access First, Access Second)> questions, int[] order)
    {
        var answers = new Dictionary<int, Answer>();
        foreach (var index in order)
        {
            var (first, second) = questions[index];
            var timing = analysis.Timing(first, second, out var assumed);
            answers.Add(index, new Answer(timing, string.Join("+", assumed.Order(StringComparer.Ordinal))));
        }

        return answers;
    }

    /// <summary>A scope's model built anew and collected, as pairing's graph is.</summary>
    /// <param name="input">The scope's input.</param>
    private static (ExecutionAnalysis Analysis, InterproceduralCollection Collection) Fresh(InterproceduralInput input)
    {
        var analysis = ExecutionModel.Build(input.Scope, input.Heap, CancellationToken.None);
        return (analysis, InterproceduralAccesses.Collect(input with { Executions = analysis }, CancellationToken.None));
    }

    /// <summary>The line of a fixture's source, with the usings before it, that holds a text.</summary>
    /// <param name="source">The fixture's source.</param>
    /// <param name="text">The text.</param>
    private static int Line(string source, string text) => Array.FindIndex((Usings + source).Split('\n'), line => line.Contains(text, StringComparison.Ordinal)) + 1;

    /// <summary>Asserts what the rule of <see cref="HappensBefore.Assumed(IReadOnlySet{OrderingAssumption}, Func{IReadOnlyList{OrderingAssumption}, bool})"/>
    /// answers for a pair given by its proofs, and which combinations of withheld sets it asks, in order. Each set is a letter:
    /// <c>C</c> for <see cref="CONTEXT"/>, <c>P</c> for <see cref="POOL"/>, <c>B</c> for <see cref="BOTH"/>.</summary>
    /// <param name="taken">The sets the questions so far read.</param>
    /// <param name="proofs">The pair's proofs, each the sets it reads: the pair is ordered with some sets withheld when a proof reads none
    /// of them.</param>
    /// <param name="expected">The assumptions the pair rests on.</param>
    /// <param name="asked">The combinations withheld, in the order asked, each its sets in the order of <c>C</c>, <c>P</c>, <c>B</c>.</param>
    private static void AssertRule(string taken, string[] proofs, OrderingAssumption expected, params string[] asked)
    {
        var questions = new List<string>();

        var assumed = HappensBefore.Assumed(taken.Select(Set).ToHashSet(), withheld =>
        {
            var letters = string.Concat(withheld.Select(Letter));
            questions.Add(letters);
            return proofs.Any(proof => !proof.Any(letters.Contains));
        });

        Assert.Equal(asked, questions);
        Assert.Equal(expected, assumed);
    }

    /// <summary>The assumption set a letter names.</summary>
    /// <param name="letter">The letter.</param>
    private static OrderingAssumption Set(char letter) => letter switch { 'C' => CONTEXT, 'P' => POOL, _ => BOTH };

    /// <summary>The letter that names an assumption set.</summary>
    /// <param name="set">The set.</param>
    private static char Letter(OrderingAssumption set) => set switch { CONTEXT => 'C', POOL => 'P', _ => 'B' };

    /// <summary>A question's answer: its timing and the assumptions its order rests on.</summary>
    /// <param name="Timing">The timing.</param>
    /// <param name="Assumed">The assumptions, joined in order.</param>
    private readonly record struct Answer(PairTiming Timing, string Assumed);
}
