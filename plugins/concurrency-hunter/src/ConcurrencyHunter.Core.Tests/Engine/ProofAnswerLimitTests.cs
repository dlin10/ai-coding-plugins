using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The completion proof of the queried passes keeps its answers up to a limit and forgets them all there, a question reads only
/// the waits its own climbs pass, and pairing lets go of the views a candidate's questions made when the candidate is done (#486). On the
/// folded accesses pairing reads in production: pairing with every question proven anew finds what pairing with every answer kept finds,
/// on fixtures with handles, forwarders, blocking waits, admitted calls, timers and <c>Parallel</c> calls, on the demo and on eShop, at
/// one worker and at four; every kept question, asked again after later ones on a graph that forgets, in the order asked and in the
/// reverse one, gets the branch and the assumptions of the graph that kept it, and the waits its climbs passed are the same either way,
/// as they are for a join of a parent the gap decisions ask about; a scope whose sides have many executions keeps no more answers than the limit; and pairing writes progress lines inside a
/// candidate's search, after the last of which the sides hold the views of found pairs alone.</summary>
public sealed class ProofAnswerLimitTests
{
    private const BindingFlags NON_PUBLIC = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>The limit of the scope of many executions: a few times fewer answers than it asks questions.</summary>
    private const int SMALL_LIMIT = 16;

    /// <summary>The proof's limit by default: the slots a dictionary of its answers grows to on the way past 51 MB.</summary>
    private const int DEFAULT_LIMIT = 672_827;

    /// <summary>The controllers that each start the shared helper's work: every pair of them is a pair of executions asked once.</summary>
    private const int CONTROLLERS = 24;

    /// <summary>The fixtures the proof answers on: a handle in a field, a child waited for twice, two children of one parent, a task
    /// forwarder, a retry loop and a blocking wait, whose readings the replays withhold (R5), a timer disposed of and a <c>Parallel</c>
    /// call, an admitted sequential-awaited call and two async entries of one, whose runs the proof orders through <c>SequentialRuns</c>,
    /// and a helper many controllers start.</summary>
    private static readonly Lazy<EngineRun[]> Runs = new(() =>
    [
        Analyze(BlockingWaitAssumptionMatrixTests.Worker("for (var pass = 0; pass < 2; pass++) { _task = Task.Run(First); await _task; }",
                                                         "private Task? _task;")),
        Analyze(BlockingWaitAssumptionMatrixTests.Worker("for (var pass = 0; pass < 2; pass++) { var own = Task.Run(First); own.Wait(); await own; }")),
        Analyze(BlockingWaitAssumptionMatrixTests.Worker("for (var pass = 0; pass < 2; pass++) { var a = Task.Run(First); var b = Task.Run(Second); " +
                                                         "a.Wait(); b.Wait(); } await Task.CompletedTask;")),
        Analyze(BlockingWaitAssumptionMatrixTests.Worker("for (var pass = 0; pass < 2; pass++) await Forward();",
                                                         "private Task Forward() => DrainAsync(); private async Task DrainAsync() { await Task.Run(First); }")),
        Analyze(BlockingWaitAssumptionMatrixTests.Worker("for (var attempt = 0; attempt < 3; attempt++) { try { var own = Task.Run(First); " +
                                                         "own.Wait(); break; } catch (AggregateException) { } } Second(); await Task.CompletedTask;")),
        Analyze(BlockingWaitAssumptionMatrixTests.Worker("Task.Run(First).Wait(); Second(); await Task.CompletedTask;")),
        Analyze(BlockingWaitAssumptionMatrixTests.Worker("var timer = new Timer(_ => First(), null, 0, 100); await timer.DisposeAsync(); Second();")),
        Analyze(BlockingWaitAssumptionMatrixTests.Worker("Parallel.For(0, 4, _ => First()); Second(); await Task.CompletedTask;")),
        AwaitedRunsFixture.Run(AwaitedRunsFixture.Direct),
        AwaitedRunsFixture.Run("await Helper();\nawait Helper();", members:
                               "private async Task Helper() { await Runs.Lib.Run(async () => { _state.C = 3; await Task.Yield(); _state.C = 1; }); }"),
        Analyze(Many(CONTROLLERS))
    ]);

    /// <summary>On every fixture, pairing whose proofs keep one answer at a time finds the pairs, counts and assumptions that pairing
    /// keeping every answer finds, at one worker and at four, and every kept question asked again after later ones gets the branch and
    /// the assumptions of the graph that kept it.</summary>
    [Fact]
    public void Fixtures_pair_and_answer_the_same_when_every_proof_is_proven_anew()
    {
        var seen = new Seen();
        foreach (var run in Runs.Value)
            AssertProvenAnewAsKept(Input(run.Execution), seen);

        Assert.True(seen.Clears > 0, "a proof that forgot its answers");
        Assert.True(seen.Proven > 0, "a question the proof answered yes, asked again");
        Assert.True(seen.Replays > 0, "a replay whose proof was asked");
        Assert.True(seen.WithWaits > 0, "a question whose climbs passed a wait");
    }

    /// <summary>On every demo scope, pairing whose proofs keep one answer at a time finds what pairing keeping every answer finds, at one
    /// worker and at four, and every kept question asked again after later ones gets the branch and the assumptions of the graph that
    /// kept it.</summary>
    [Fact]
    public async Task Demo_pairs_and_answers_the_same_when_every_proof_is_proven_anew()
    {
        var seen = new Seen();
        await ParallelCollectionTests.ForEachDemoScope(input => AssertProvenAnewAsKept(input, seen));

        Assert.True(seen.Clears > 0, "a proof that forgot its answers");
        Assert.True(seen.Hits > 0, "a question answered from the kept answers");
        Assert.True(seen.Proven > 0, "a question the proof answered yes, asked again");
    }

    /// <summary>On every eShop scope, pairing whose proofs keep one answer at a time finds what pairing keeping every answer finds, at
    /// one worker and at four, and every kept question asked again after later ones gets the branch and the assumptions of the graph that
    /// kept it.</summary>
    [RequiresEShopFact]
    public async Task EShop_pairs_and_answers_the_same_when_every_proof_is_proven_anew()
    {
        var seen = new Seen();
        await ParallelCollectionTests.ForEachScope(Path.Combine(Environment.GetEnvironmentVariable(RequiresEShopFactAttribute.VARIABLE)!, "src",
                                                                "eShopOnContainers-ServicesAndWebApps.sln"),
                                                   input => AssertProvenAnewAsKept(input, seen));

        Assert.True(seen.Clears > 0, "a proof that forgot its answers");
        Assert.True(seen.Hits > 0, "a question answered from the kept answers");
        Assert.True(seen.Proven > 0, "a question the proof answered yes, asked again");
    }

    /// <summary>On every fixture, every join of a parent asked whether it would order the passes of a child's work (R9), the question
    /// whose yes the gap decisions read, gets the same answer, and its climbs pass the same waits, on a graph that has been through
    /// collection alone, asked in order, and on the graph that paired, asked in the reverse order: a join, like a question of pairing,
    /// reads only the waits its own climbs pass, whatever was asked before.</summary>
    [Fact]
    public void A_join_of_a_parent_reads_only_the_waits_its_own_climbs_pass()
    {
        var (asked, ordered, withWaits) = (0, 0, 0);
        foreach (var run in Runs.Value)
        {
            var input = Input(run.Execution);
            var fresh = ExecutionModel.Build(input.Scope, input.Heap, CancellationToken.None);
            var folded = InterproceduralAccesses.Collect(input with { Executions = fresh }, CancellationToken.None).Folded;
            var paired = ExecutionModel.Build(input.Scope, input.Heap, CancellationToken.None);
            InterproceduralPairing.Pair(InterproceduralAccesses.Collect(input with { Executions = paired }, CancellationToken.None).Folded,
                                        paired, input.Heap);
            var questions = folded.Where(access => fresh.Execution(access.ExecutionId).ParentId is { } parent && parent != ExecutionModel.STARTUP)
                                  .DistinctBy(access => (access.ExecutionId, access.Root.RootId))
                                  .SelectMany(access => fresh.InstanceExecutions
                                                             .Where(pair => pair.Value.Contains(fresh.Execution(access.ExecutionId).ParentId!))
                                                             .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                                             .SelectMany(pair => input.Heap.Instances[pair.Key].Summary.Joins
                                                                                      .Select(join => (Instance: pair.Key, join.OperationId, Access: access))))
                                  .ToArray();
            var answers = questions.Select(question =>
            {
                var answer = fresh.PassJoinWouldOrder(question.Instance, question.OperationId, question.Access, question.Access);
                return (Answer: answer, Waits: GraphOrNone(fresh) is { } graph ? Waits(graph) : string.Empty);
            }).ToArray();
            for (var index = questions.Length - 1; index >= 0; index--)
            {
                var (instance, operation, access) = questions[index];
                Assert.Equal(answers[index].Answer, paired.PassJoinWouldOrder(instance, operation, access, access));
                Assert.Equal(answers[index].Waits, GraphOrNone(paired) is { } graph ? Waits(graph) : string.Empty);
            }
            asked += questions.Length;
            ordered += answers.Count(answer => answer.Answer);
            withWaits += answers.Count(answer => answer.Waits.Length != 0);
        }

        Assert.True(ordered > 0, "a join that orders the passes");
        Assert.True(withWaits > 0, "a join whose climbs passed a wait");
        Assert.True(asked > ordered, "a join that does not order them");
    }

    /// <summary>A dictionary of the proof's answers filled to the default limit has the 672,827 slots it grows to on its way, a 72-byte
    /// entry and a 4-byte bucket each, 51.1 MB, and not the 1,395,263 of the next step.</summary>
    [Fact]
    public void A_memo_full_to_the_default_limit_stays_at_its_smaller_size()
    {
        var memo = new Dictionary<(string SourceExecution, string DestinationExecution, HappensBefore.PointKey Source, HappensBefore.PointKey Destination,
                                   string? Creator), bool>();
        for (var answer = 0; answer < DEFAULT_LIMIT; answer++)
            memo.Add(($"e{answer}", "f", new HappensBefore.PointKey("i", answer, false), new HappensBefore.PointKey("j", answer, false), null), true);

        var entries = (Array)memo.GetType().GetField("_entries", NON_PUBLIC)!.GetValue(memo)!;
        var entry = memo.GetType().GetNestedType("Entry", BindingFlags.NonPublic)!.MakeGenericType(memo.GetType().GetGenericArguments());
        Assert.Equal(DEFAULT_LIMIT, entries.Length);
        Assert.Equal(72, (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(entry).Invoke(null, null)!);
    }

    /// <summary>A helper started by many controllers runs in an execution per controller, and pairing asks about every pair of them: the
    /// proof keeps at most 672,827 answers by default; limited, it keeps no more than the limit while it is asked several times as many
    /// questions, counts every question as answered from the kept ones or proven and kept since the last time it forgot them, and pairing
    /// finds what it finds keeping every answer.</summary>
    [Fact]
    public void Sides_of_many_executions_keep_no_more_answers_than_the_limit()
    {
        var input = Input(Runs.Value[^1].Execution);
        var kept = PairAnew(input, null, 1);
        var limited = PairAnew(input, SMALL_LIMIT, 1);

        Assert.Equal(DEFAULT_LIMIT, Assert.IsType<HappensBefore>(kept.Order).ProofAnswerLimit);
        Assert.Equal(kept.Pairs, limited.Pairs);
        var order = Assert.IsType<HappensBefore>(limited.Order);
        var counters = order.Counters;
        Assert.True(counters.PassProofs > 4 * SMALL_LIMIT, $"{counters.PassProofs} questions");
        Assert.True(counters.PassProofClears > 0);
        Assert.All(Graphs(order), graph => Assert.InRange(Answers(graph)?.Count ?? 0, 0, SMALL_LIMIT));
        Assert.Equal(counters.PassProofs - counters.PassProofHits,
                     counters.PassProofClears * SMALL_LIMIT + Graphs(order).Sum(graph => (long)(Answers(graph)?.Count ?? 0)));
        Assert.Contains($"passProofs={counters.PassProofs} passProofHits={counters.PassProofHits} passProofClears={counters.PassProofClears}",
                        counters.Since(new TimingCounters()));
        Assert.Equal(0, kept.Order!.Counters.PassProofClears);
        Assert.True(Answers(kept.Order)!.Count > SMALL_LIMIT);
    }

    /// <summary>A side makes one view of an execution for the questions of the candidate being considered, the same object at every
    /// question; a found pair's view is that one, kept; when the candidate is done the side lets go of the rest, and the next candidate
    /// makes its views anew, equal field for field.</summary>
    [Fact]
    public void A_side_makes_one_view_of_an_execution_for_a_candidate_keeps_the_found_ones_and_lets_the_rest_go()
    {
        var folded = InterproceduralAccesses.Collect(Input(Runs.Value[0].Execution), CancellationToken.None).Folded
                                            .First(access => access.Executions.Count != 0);
        var execution = folded.Executions[0];
        var access = folded with
        {
            Executions = [execution, execution with { ExecutionId = execution.ExecutionId + "/1", Position = execution.Position + 1 },
                          execution with { ExecutionId = execution.ExecutionId + "/2", Position = execution.Position + 2 }]
        };
        var sideType = typeof(InterproceduralPairing).GetNestedType("PairingSide", BindingFlags.NonPublic)!;
        var side = Activator.CreateInstance(sideType, NON_PUBLIC, null, [access, 0], null)!;

        var asked = Asked(0);
        Assert.Same(asked, Asked(0));
        Assert.Equal(access.Executions[0].ExecutionId, asked.ExecutionId);
        var witness = Asked(2);
        Assert.Equal(access.Executions[2].ExecutionId, witness.ExecutionId);
        Assert.Equal(2, Held());
        Assert.Same(witness, View(2));
        Assert.Equal(2, Held());
        Forget();
        Assert.Equal(1, Held());
        Assert.Same(witness, Asked(2));
        var again = Asked(0);
        Assert.NotSame(asked, again);
        Assert.Equal(Side(asked), Side(again));
        Assert.Equal(2, Held());

        Access Asked(int at) => (Access)sideType.GetMethod("Asked", NON_PUBLIC)!.Invoke(side, [at])!;
        Access View(int at) => (Access)sideType.GetMethod("View", NON_PUBLIC)!.Invoke(side, [at])!;
        void Forget() => sideType.GetMethod("Forget", NON_PUBLIC)!.Invoke(side, null);
        int Held() => (int)sideType.GetProperty("Held", NON_PUBLIC)!.GetValue(side)!;
    }

    /// <summary>On every demo scope, pairing with a progress line due at every chance writes lines while it searches one candidate: at
    /// the pairs of executions an orientation looks at and rejects unasked, and as each witness moves to the found pairs, some of them
    /// counting the witnesses that candidate has gathered and not yet moved; while a candidate is searched its sides hold the views its
    /// questions made, and after the last candidate they hold the views of found pairs alone.</summary>
    [Fact]
    public async Task Demo_pairing_writes_progress_inside_a_candidate_and_ends_holding_the_views_of_found_pairs_alone()
    {
        var (inside, pending, released, looked, moved) = (0, 0, 0, 0, 0);
        await ParallelCollectionTests.ForEachDemoScope(input =>
        {
            var executions = ExecutionModel.Build(input.Scope, input.Heap, CancellationToken.None);
            var collection = InterproceduralAccesses.Collect(input with { Executions = executions }, CancellationToken.None);
            var lines = new List<string>();
            var pairs = InterproceduralPairing.Pair(collection.Folded, executions, input.Heap, StringComparer.Ordinal,
                                                    new InterproceduralPairing.PairingProgress(TimeSpan.Zero, (_, details) => lines.Add(details[0])));
            if (lines.Count == 0)
                return;

            var progress = lines.Select(Progress).ToArray();
            inside += progress.Zip(progress.Skip(1))
                              .Count(step => step.First.Considered == step.Second.Considered && step.Second.ExecutionPairs > step.First.ExecutionPairs);
            pending += progress.Count(line => line.Pending > 0);
            // A pair of executions looked at writes a line before it is asked, so a pair an orientation rejects unasked writes one that
            // asked nothing more and found nothing more than the line before it.
            looked += progress.Zip(progress.Skip(1)).Count(step => step.Second == step.First);
            // A witness moving to the found pairs writes a line with one witness more and one pending witness fewer.
            moved += progress.Zip(progress.Skip(1))
                             .Count(step => step.First.Considered == step.Second.Considered && step.Second.ExecutionPairs == step.First.ExecutionPairs &&
                                            step.Second.Witnesses == step.First.Witnesses + 1 && step.Second.Pending == step.First.Pending - 1);
            var folded = collection.Folded.ToHashSet(ReferenceEqualityComparer.Instance);
            var foundViews = pairs.Pairs.SelectMany(pair => new[] { pair.First, pair.Second }).Where(side => !folded.Contains(side))
                                  .Distinct(ReferenceEqualityComparer.Instance).Count();
            // The last line comes after the last candidate.
            Assert.Equal(pairs.Pairs.Select(pair => pair.Candidate).Distinct().Count(), progress[^1].Candidates);
            Assert.Equal(pairs.Pairs.Count, progress[^1].Witnesses);
            Assert.Equal(foundViews, progress[^1].Held);
            released += progress.Max(line => line.Held) > foundViews ? 1 : 0;
        });

        Assert.True(inside > 0, "a line written inside a candidate's search");
        Assert.True(pending > 0, "a line counting the witnesses a candidate has gathered");
        Assert.True(released > 0, "a scope whose sides held views of questions they let go of");
        Assert.True(looked > 0, "a line written at a pair of executions an orientation rejected unasked");
        Assert.True(moved > 0, "a line written as a witness moved to the found pairs");
    }

    /// <summary>Pairs a scope on a model built anew from its input, with the proof's limit given and the collection on the workers
    /// given, three times: every answer kept at one worker, one answer at a time at one worker and at four. The three find the same, the
    /// counters of the first account for every question, and every question the first kept is asked again on the second.</summary>
    /// <param name="input">The scope's input, after the pipeline has collected it once.</param>
    /// <param name="seen">What the checks met.</param>
    private static void AssertProvenAnewAsKept(InterproceduralInput input, Seen seen)
    {
        var kept = PairAnew(input, null, 1);
        var anew = PairAnew(input, 1, 1);
        Assert.Equal(kept.Pairs, anew.Pairs);
        Assert.Equal(kept.Pairs, PairAnew(input, 1, 4).Pairs);
        if (kept.Order is not { } order || anew.Order is not { } limited || kept.Template is not { } template)
            return;

        var counters = order.Counters;
        Assert.Equal(0, counters.PassProofClears);
        Assert.Equal(counters.PassProofs - counters.PassProofHits, Graphs(order).Sum(graph => (long)(Answers(graph)?.Count ?? 0)));
        Assert.All(Graphs(limited), graph => Assert.InRange(Answers(graph)?.Count ?? 0, 0, 1));
        seen.Clears += limited.Counters.PassProofClears;
        seen.Hits += counters.PassProofHits;
        seen.Replays += Graphs(order).Skip(1).Count(graph => Answers(graph) is { Count: > 0 });
        // A graph that has been through collection, as pairing's graph has, and asked no question of pairing.
        var collected = ExecutionModel.Build(input.Scope, input.Heap, CancellationToken.None);
        InterproceduralAccesses.Collect(input with { Executions = collected }, CancellationToken.None);
        var (proven, withWaits) = AskAgain(order, limited, Assert.IsType<HappensBefore>(GraphOrNone(collected)), template);
        seen.Proven += proven;
        seen.WithWaits += withWaits;
    }

    /// <summary>Asks every question the graph that kept every answer kept: first, in the order asked, of a graph that has been through
    /// collection and no pairing, then, in the reverse order, of the graph that paired keeping one answer at a time, so that each is
    /// proven anew after every question of its pairing and after later ones. Each graph gives the branch that orders the question and the assumptions it rests
    /// on that the kept graph gives, and the waits its climbs passed are the same on both: its own, whatever was asked before.</summary>
    /// <param name="kept">The graph that kept every answer, after its pairing.</param>
    /// <param name="limited">The graph that keeps one answer at a time, after its pairing.</param>
    /// <param name="fresh">A graph of the same scope that has been through collection and asked no question of pairing.</param>
    /// <param name="template">An access of the scope, which the questions copy with their executions and points.</param>
    /// <returns>How many of the questions were answered yes, and how many passed some wait.</returns>
    private static (int Proven, int WithWaits) AskAgain(HappensBefore kept, HappensBefore limited, HappensBefore fresh, Access template)
    {
        if (Answers(kept) is not { Count: > 0 } answers)
            return (0, 0);
        var questions = answers.Keys.Cast<ITuple>().Zip(answers.Values.Cast<bool>(), (key, answer) => (Key: key, Answer: answer)).ToArray();
        var waits = new Dictionary<ITuple, string>();
        var (proven, withWaits) = (0, 0);
        foreach (var (key, answer) in questions)
        {
            Ask(fresh, key, answer);
            var own = Waits(fresh);
            waits.Add(key, own);
            withWaits += own.Length == 0 ? 0 : 1;
            proven += answer ? 1 : 0;
        }
        foreach (var (key, answer) in questions.Reverse())
        {
            Ask(limited, key, answer);
            Assert.Equal(waits[key], Waits(limited));
        }
        return (proven, withWaits);

        void Ask(HappensBefore graph, ITuple key, bool answer)
        {
            // A question the proof was asked is one no path ordered, so the graph asks its proof.
            var first = Question(template, (string)key[0]!, (HappensBefore.PointKey)key[2]!);
            var second = Question(template, (string)key[1]!, (HappensBefore.PointKey)key[3]!);
            var creator = (string?)key[4];
            Assert.Equal(answer ? TimingBranch.PassCompletion : (TimingBranch?)null, graph.OrderedBy(first, second, creator));
            Assert.Equal(kept.Assumed(first, second, creator), graph.Assumed(first, second, creator));
        }
    }

    /// <summary>The waits a graph's proof holds as passed by the climbs, by start, as text in a fixed order.</summary>
    /// <param name="graph">The graph.</param>
    private static string Waits(HappensBefore graph)
    {
        var starts = (IDictionary)Field(Field(graph, "_passCompletion")!, "_completions")!;
        return string.Join("; ", starts.Keys.Cast<object>()
                                       .Select(start => $"{start}: " + string.Join(",", ((IDictionary)starts[start]!).Keys.Cast<object>()
                                                                                         .Select(wait => $"{wait}={((IDictionary)starts[start]!)[wait]}")
                                                                                         .Order(StringComparer.Ordinal)))
                                       .Order(StringComparer.Ordinal));
    }

    /// <summary>An access of an execution at a point, as a question about a pair reads it: the proof reads the execution, the instance
    /// and the operation, and the path search the same.</summary>
    /// <param name="template">An access of the scope.</param>
    /// <param name="execution">The execution.</param>
    /// <param name="point">The point.</param>
    private static Access Question(Access template, string execution, HappensBefore.PointKey point) =>
        template with { ExecutionId = execution, InstanceId = point.Instance, OperationId = point.Operation, Executions = [] };

    /// <summary>Builds a scope's model anew from its input, limits its proof's kept answers where a limit is given, collects on the
    /// workers given and pairs the folded accesses, as the analysis does.</summary>
    /// <param name="input">The scope's input.</param>
    /// <param name="limit">The proof's limit, or null for the default, which no scope here reaches.</param>
    /// <param name="workers">The collection's workers.</param>
    private static Paired PairAnew(InterproceduralInput input, int? limit, int workers)
    {
        var executions = ExecutionModel.Build(input.Scope, input.Heap, CancellationToken.None);
        var order = GraphOrNone(executions);
        if (order is not null && limit is { } answers)
            order.ProofAnswerLimit = answers;
        var collection = InterproceduralAccesses.Collect(input with { Executions = executions, Workers = workers }, CancellationToken.None);
        return new Paired(Describe(InterproceduralPairing.Pair(collection.Folded, executions, input.Heap)), order, collection.Folded.FirstOrDefault());
    }

    /// <summary>What pairing found, in its order: each pair with both sides as their execution has them and the candidate it is a
    /// witness of, then the counts, the skips and the assumptions.</summary>
    /// <param name="analysis">The pairing.</param>
    private static string[] Describe(PairAnalysis analysis) =>
    [
        .. analysis.Pairs.Select(pair => $"{Side(pair.First)} | {Side(pair.Second)} | {pair.Protection} {pair.Resource.Identity} " +
                                         $"[{string.Join(",", pair.Uncertainties)}] candidate={pair.Candidate}"),
        $"comparisons={analysis.Comparisons} suppressed={analysis.Suppressed} candidates={analysis.Candidates} cartesian={analysis.CartesianBound}",
        .. analysis.Skips.OrderBy(skip => skip.Key, StringComparer.Ordinal).Select(skip => $"skip {skip.Key}={skip.Value}"),
        .. analysis.Assumed.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"assumed {item.Key}={item.Value}")
    ];

    /// <summary>One side of a pair as its execution has it.</summary>
    /// <param name="access">The side.</param>
    private static string Side(Access access) =>
        $"{access.ExecutionId} {access.BodyId}#{access.OperationId} {access.InstanceId} {access.Operation} root={access.Root.RootId} " +
        $"path=[{string.Join("/", access.CallPath)}] flow=[{string.Join("/", access.CodeFlow)}] reads=[{string.Join("/", access.ReadSources)}]";

    /// <summary>The counts of one <c>pairing-progress</c> line.</summary>
    /// <param name="line">The line.</param>
    private static ProgressLine Progress(string line)
    {
        var match = Regex.Match(line, @"considered=(\d+) .*executionPairs=(\d+) candidates=(\d+) witnesses=(\d+) pending=(\d+) .*heldViews=(\d+)");
        Assert.True(match.Success, line);
        return new ProgressLine(long.Parse(match.Groups[1].Value), long.Parse(match.Groups[2].Value), long.Parse(match.Groups[3].Value),
                                long.Parse(match.Groups[4].Value), long.Parse(match.Groups[5].Value), long.Parse(match.Groups[6].Value));
    }

    /// <summary>A graph and the replays it built.</summary>
    /// <param name="order">The graph.</param>
    private static HappensBefore[] Graphs(HappensBefore order) => [order, .. ((Dictionary<string, HappensBefore>)Field(order, "_replays")!).Values];

    /// <summary>The answers a graph's proof keeps, or null before the graph is asked.</summary>
    /// <param name="graph">The graph or a replay.</param>
    private static IDictionary? Answers(HappensBefore graph) =>
        Field(graph, "_passCompletion") is { } proof ? (IDictionary)Field(proof, "_proofs")! : null;

    /// <summary>The analysis's happens-before graph, or null for a scope without one.</summary>
    /// <param name="analysis">The analysis.</param>
    private static HappensBefore? GraphOrNone(ExecutionAnalysis analysis) =>
        (HappensBefore?)typeof(ExecutionAnalysis).GetField("_order", NON_PUBLIC)!.GetValue(analysis);

    /// <summary>A private instance field's value.</summary>
    /// <param name="target">The object holding the field.</param>
    /// <param name="name">The field.</param>
    private static object? Field(object target, string name) => target.GetType().GetField(name, NON_PUBLIC)!.GetValue(target);

    /// <summary>A helper many controllers start with <c>Task.Run</c> and wait for, which reads and writes one static field: its two
    /// accesses have an execution per controller.</summary>
    /// <param name="controllers">How many controllers start it.</param>
    private static string Many(int controllers) => Startup() + """

        public static class Shared
        {
            public static int Value;
            public static void Touch() { Value = Value + 1; }
        }
        """ + string.Concat(Enumerable.Range(0, controllers).Select(index => $$"""

        public sealed class C{{index}}Controller : ControllerBase
        {
            public async Task Get() { await Task.Run(Shared.Touch); }
        }
        """));


    /// <summary>What one pairing found, the graph it asked and an access of its scope.</summary>
    /// <param name="Pairs">What pairing found (<see cref="Describe"/>).</param>
    /// <param name="Order">The graph, or null for a scope without one.</param>
    /// <param name="Template">An access of the scope, or null for a scope without accesses.</param>
    private sealed record Paired(string[] Pairs, HappensBefore? Order, Access? Template);

    /// <summary>The counts of a <c>pairing-progress</c> line a test reads.</summary>
    /// <param name="Considered">The candidate pairs considered whole.</param>
    /// <param name="ExecutionPairs">The pairs of executions asked about.</param>
    /// <param name="Candidates">The candidates numbered (ADR 0042).</param>
    /// <param name="Witnesses">The witnesses moved to the found pairs.</param>
    /// <param name="Pending">The witnesses the candidate being searched has gathered and not yet moved to the found pairs.</param>
    /// <param name="Held">The views the sides hold.</param>
    private sealed record ProgressLine(long Considered, long ExecutionPairs, long Candidates, long Witnesses, long Pending, long Held);

    /// <summary>What the checks met across scopes, so that a test shows it checked something.</summary>
    private sealed class Seen
    {
        /// <summary>The times a limited proof forgot its answers.</summary>
        internal long Clears;

        /// <summary>The questions answered from the kept answers where every answer was kept.</summary>
        internal long Hits;

        /// <summary>The replays whose proof kept answers.</summary>
        internal int Replays;

        /// <summary>The questions asked again whose kept answer was yes.</summary>
        internal int Proven;

        /// <summary>The questions asked again whose climbs passed some wait.</summary>
        internal int WithWaits;
    }
}
