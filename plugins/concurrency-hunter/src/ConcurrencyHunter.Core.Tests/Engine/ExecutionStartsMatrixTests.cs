using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Every start of an execution is proven on its own (ADR 0035): an execution is one per parent and site, so two method instances
/// of one body that reach its site in one hosted root give it a start each, and every reader of a start reads all of them. Receivers
/// come each from their own <c>new</c>, <c>a</c> allocated before <c>b</c>, so the start the graph keeps first is <c>a</c>'s; the
/// admitted seeder is reached from two separate lambdas.</summary>
public sealed class ExecutionStartsMatrixTests
{
    /// <summary>What starts the execution at the site: the tail of an async call, a <c>Task.Run</c>, or the continuation of an admitted
    /// call's delegate.</summary>
    public enum Kind { Async, TaskRun, Admitted }

    /// <summary>How the root starts the instances: each waited at once, one waited and the other started twice without a wait, or none
    /// waited before both have begun.</summary>
    public enum Starts { BothWait, OneWaitsOtherTwice, NoneWaits }

    /// <summary>One cell of the matrix.</summary>
    /// <param name="Kind">What starts the execution at the site.</param>
    /// <param name="Starts">How the root starts the instances.</param>
    public sealed record Cell(Kind Kind, Starts Starts)
    {
        public override string ToString() => $"{Kind}/{Starts}";
    }

    /// <summary>The cells whose observed pairs are wider than the requirements, with the reason: none.</summary>
    private static readonly Dictionary<string, string> Wider = new(StringComparer.Ordinal);

    /// <summary>The required pairs a cell is known to miss, each with its cause: none.</summary>
    private static readonly Dictionary<string, (string[] Pairs, string Cause)> KnownMisses = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, Lazy<Analyzed>> Runs = new(StringComparer.Ordinal);

    /// <summary>The seeding of the eShop shape, copied from <c>PerPassAdmissionTests</c>: a seeder whose async seeding runs inside an
    /// async retry, and a synchronous migration that retries the seeder it is given.</summary>
    private const string SEEDING = """
        public sealed class SqlException : Exception { }
        public sealed class A { }
        public sealed class B { }
        private static async Task SeedAsync()
        {
            var policy = Policy.Handle<SqlException>().WaitAndRetryAsync(3, retry => TimeSpan.FromSeconds(1), (e, t, r, c) => { });
            await policy.ExecuteAsync(async () =>
            {
                Value = 1;
                await Task.Yield();
                Value = 2;
            });
        }
        private static void Migrate<T>(Action<T> seeder) where T : new()
        {
            try
            {
                var retry = Policy.Handle<SqlException>().WaitAndRetry(new[] { TimeSpan.FromSeconds(1) });
                retry.Execute(() => seeder(new T()));
            }
            catch (Exception) { }
        }
        """;

    private const string ASYNC = """
        public async Task RunAsync() { var t = StepAsync(); await t; }
        private async Task StepAsync() { await Task.Yield(); _sink.Value = 1; }
        """;

    private const string TASK_RUN = "public Task Start() => Task.Run(() => _sink.Value = 1);";

    public static IEnumerable<Cell> AllCells() =>
        from kind in Enum.GetValues<Kind>()
        from starts in Enum.GetValues<Starts>()
        let cell = new Cell(kind, starts)
        where Allowed(cell)
        select cell;

    /// <summary>Every allowed start axis is covered, with no wider cells or known misses.</summary>
    [Fact]
    public void There_are_8_cells_covering_every_value_of_every_axis()
    {
        var cells = AllCells().ToArray();
        Assert.Equal(8, cells.Length);
        Assert.Equal(cells.Length, cells.Select(cell => cell.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Enum.GetValues<Kind>(), value => Assert.Contains(cells, cell => cell.Kind == value));
        Assert.All(Enum.GetValues<Starts>(), value => Assert.Contains(cells, cell => cell.Starts == value));
        Assert.DoesNotContain(new Cell(Kind.Admitted, Starts.OneWaitsOtherTwice), cells);
        Assert.Empty(Wider);
        Assert.Empty(KnownMisses);
    }

    /// <summary>Compares every cell's reported pairs with its required set, with no known misses or wider cells. Starts that each wait
    /// keep no pair; a start reached twice without a wait keeps its write paired with itself beside one that waits. Two detached
    /// migrations keep all three pairs of the admitted delegate's synchronous and continuation writes: <c>1-1</c>, <c>1-2</c> and
    /// <c>2-2</c>, because later runs' synchronous parts start in the call's continuation.</summary>
    [Fact]
    public void Every_cell_has_exactly_the_required_pairs()
    {
        var failures = new List<string>();
        foreach (var cell in AllCells())
        {
            var expected = Expected(cell);
            var actual = Observe(cell).ValuePairs();
            if (KnownMisses.TryGetValue(cell.ToString(), out var known))
            {
                foreach (var pair in known.Pairs)
                {
                    if (!expected.Remove(pair))
                        failures.Add($"{cell}: the known miss {pair} is not a required pair.");
                    if (actual.Contains(pair))
                        failures.Add($"{cell}: the known miss {pair} is found now ({known.Cause}); take it off the list.");
                }
            }

            if (Wider.ContainsKey(cell.ToString()))
            {
                if (actual.SetEquals(expected))
                    failures.Add($"{cell}: listed as wider, but it is exact now; take it off the list.");
            }
            else if (!actual.SetEquals(expected))
                failures.Add($"{cell}: expected [{string.Join(", ", expected.Order(StringComparer.Ordinal))}], found [{string.Join(", ", actual.Order(StringComparer.Ordinal))}].");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Executions stay one per parent and site (R1): the site's write runs in one execution, which has a start for every instance
    /// of the body that reaches the site.</summary>
    [Fact]
    public void Every_cell_has_one_execution_with_a_start_per_instance()
    {
        var failures = new List<string>();
        foreach (var cell in AllCells())
        {
            var analyzed = Observe(cell);
            var executions = analyzed.Writes(Site(cell)).Select(access => access.ExecutionId).Distinct(StringComparer.Ordinal).ToArray();
            if (executions.Length != 1)
            {
                failures.Add($"{cell}: the site's write runs in {executions.Length} executions: {string.Join(", ", executions)}.");
                continue;
            }

            var starts = Order(analyzed.Run).StartsOf(executions[0]);
            var heap = analyzed.Run.Execution.Heap.Heap;
            var callers = starts.Select(start => start.CallerInstance).Distinct(StringComparer.Ordinal).ToArray();
            var bodies = callers.Select(caller => heap.Instances[caller].BodyId).Distinct(StringComparer.Ordinal).ToArray();
            var instances = bodies.Length == 1 ? heap.Instances.Values.Count(instance => instance.BodyId == bodies[0]) : 0;
            if (starts.Count < 2 || callers.Length != starts.Count || bodies.Length != 1 || instances != starts.Count)
                failures.Add($"{cell}: {starts.Count} starts from {callers.Length} callers of {bodies.Length} bodies, {instances} instances of the body.");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Every answer read from the starts does not depend on the order the walk visits instances (R6): each cell's executions
    /// rebuilt in reverse and in three seeded orders give the same accesses and pairs.</summary>
    [Fact]
    public void Pairs_do_not_depend_on_walk_order()
    {
        foreach (var cell in AllCells())
        {
            var run = Observe(cell).Run;
            var scope = Scope(run);
            var heap = run.Execution.Heap.Heap;
            var pairs = PairKeys(run.Pairs);
            foreach (var order in new ExecutionWalkOrder[] { new(Reverse: true), new(Seed: 17), new(Seed: 93), new(Seed: 106) })
            {
                var analysis = ExecutionModel.Build(scope, heap, CancellationToken.None, order);
                var collection = InterproceduralAccesses.Collect(new InterproceduralInput(scope, heap, analysis), CancellationToken.None);
                Assert.True(pairs.SequenceEqual(PairKeys(InterproceduralPairing.Pair(collection.Accesses, analysis, heap))), $"{cell}: the pairs differ under {order}.");
            }
        }
    }

    /// <summary>The sequential shapes stay pair-free (R9): two migrations handing one seeder, two receivers each waiting for a nested
    /// <c>Task.Run</c>, a body awaiting its <c>Task.Run</c> and then writing the field behind two receivers, and two receivers on
    /// exclusive branches.</summary>
    [Fact]
    public void The_sequential_shapes_have_no_pair()
    {
        var shapes = new (string Label, Analyzed Analyzed)[]
        {
            ("two-migrations", Admitted("Migrate<A>(a => SeedAsync().Wait()); Migrate<A>(a => SeedAsync().Wait());")),
            ("nested-task-run", Hosted("public void Nest() { Task.Run(() => { _sink.Value = 0; Task.Run(() => _sink.Value = 1).Wait(); }).Wait(); }",
                                       "a.Nest(); b.Nest();")),
            ("awaited-task-run-then-write", Hosted("""
                public async Task RunAsync() { var s = StepAsync(); await s; }
                private async Task StepAsync() { await Task.Run(() => _sink.Value = 1); _sink.Value = 2; }
                """, "await a.RunAsync(); await b.RunAsync();")),
            ("exclusive-branches", Hosted("public void Once() { Task.Run(() => _sink.Value = 1).Wait(); }",
                                          "if (Flag) { a.Once(); } else { b.Once(); }"))
        };

        Assert.All(shapes, shape =>
        {
            Assert.True(shape.Analyzed.Run.Of("Value").Count > 0, $"{shape.Label}: the field's accesses are missing.");
            Assert.True(shape.Analyzed.ValuePairs().Count == 0, $"{shape.Label}: kept [{string.Join(", ", shape.Analyzed.ValuePairs())}].");
        });
    }

    /// <summary>Two detached receivers of one async body share its synchronous access. The synchronous write with the continuation
    /// remains the known miss of issue 408, in a shape without a sequential-awaited record.</summary>
    [Fact]
    public void Two_instances_of_one_body_at_one_operation_give_one_access()
    {
        var analyzed = Hosted("""
            public async Task RunAsync()
            {
                _sink.Value = 0;
                await Task.Yield();
                _sink.Value = 1;
            }
            """, "a.RunAsync(); b.RunAsync();");

        var pairs = analyzed.ValuePairs();
        Assert.False(pairs.Contains("0-1"), "the known miss 0-1 is found now (#408)");
        Assert.Single(analyzed.Writes(0));
        Assert.NotEmpty(analyzed.Writes(1));
        Assert.Equal(new[] { "1-1" }, pairs.Order(StringComparer.Ordinal));
    }

    private const string CHAIN = "public void Chain(Task t) { t.ContinueWith(_ => _sink.Value = 3); }";

    /// <summary>A continuation whose starts chain two different antecedents follows neither: the edge is published only when every start
    /// proves the same antecedent, so both pairs of the continuation's write with the antecedents' writes stay.</summary>
    [Fact]
    public void A_continuation_with_two_antecedents_is_ordered_after_neither()
    {
        var analyzed = Hosted(CHAIN, """
            var first = Task.Run(() => _sink.Value = 1);
            var second = Task.Run(() => _sink.Value = 2);
            a.Chain(first);
            b.Chain(second);
            """);

        Assert.True(analyzed.Pairs(3, 1), "The continuation lost its pair with the first antecedent.");
        Assert.True(analyzed.Pairs(3, 2), "The continuation lost its pair with the second antecedent.");
    }

    /// <summary>A continuation whose starts all chain the same antecedent follows it.</summary>
    [Fact]
    public void A_continuation_with_one_antecedent_is_ordered_after_it()
    {
        var analyzed = Hosted(CHAIN, """
            var first = Task.Run(() => _sink.Value = 1);
            a.Chain(first);
            b.Chain(first);
            """);

        Assert.True(analyzed.Writes(3).Count > 0 && analyzed.Writes(1).Count > 0, "The writes are missing.");
        Assert.False(analyzed.Pairs(3, 1), "The continuation keeps its pair with its one antecedent.");
    }

    /// <summary>A <c>Task.Run</c> whose site two receivers reach with different delegates, <c>a</c>'s async and <c>b</c>'s a non-async one
    /// returning a task, is composite because one start's work is: its waits order nothing, so the work of either start keeps its pair
    /// with the write after both waits.</summary>
    [Fact]
    public void A_spawn_composite_for_one_start_is_composite()
    {
        var analyzed = Hosted("""
            public Func<Task>? Work;
            public Task Start() => Task.Run(Work!);
            """, """
            a.Work = async () =>
            {
                _sink.Value = 1;
                await Task.Yield();
            };
            b.Work = () =>
            {
                _sink.Value = 1;
                return Task.CompletedTask;
            };
            a.Start().Wait();
            b.Start().Wait();
            _sink.Value = 2;
            """);

        var work = analyzed.Writes(1).Select(access => access.Source.StartLine).Distinct().ToArray();
        Assert.Equal(2, work.Length);
        Assert.All(work, line => Assert.True(analyzed.Run.Pairs.Pairs.Any(pair => pair.Resource.Member.Name == "Value" &&
            (pair.First.Source.StartLine == line && analyzed.Value(pair.Second) == 2 || pair.Second.Source.StartLine == line && analyzed.Value(pair.First) == 2)),
            $"The work written on line {line} lost its pair with the write after the waits."));
    }

    private const string GO = "public Task Go() => Task.Run(() => _sink.Value = 1);";

    /// <summary>Starts on exclusive branches of a spawned execution X, each joined in its branch, bound the end of X: the root's write after
    /// its wait for X is ordered after the work of either start.</summary>
    [Fact]
    public void Exclusive_starts_each_joined_in_their_branch_stay_bounded()
    {
        var analyzed = Hosted(GO, "Work(a, b).Wait();\n_sink.Value = 2;",
                              "private Task Work(Runner a, Runner b) => Task.Run(() => { if (Flag) { a.Go().Wait(); } else { b.Go().Wait(); } });");

        Assert.True(analyzed.Writes(1).Count > 0 && analyzed.Writes(2).Count > 0, "The writes are missing.");
        Assert.False(analyzed.Pairs(1, 2), "The work joined on each branch keeps its pair with the root's write.");
    }

    /// <summary>A start on an exclusive branch that is not joined there and passes no other join of the work on its way to the end of X
    /// leaves the work running past it: the pair between <c>b</c>'s work and the root's write after its wait for X stays.</summary>
    [Fact]
    public void Exclusive_starts_one_not_joined_keep_their_pair()
    {
        var analyzed = Hosted(GO, "Work(a, b).Wait();\n_sink.Value = 2;",
                              "private Task Work(Runner a, Runner b) => Task.Run(() => { if (Flag) { a.Go().Wait(); } else { b.Go(); } });");

        Assert.True(analyzed.Pairs(1, 2), "The unjoined branch's work lost its pair with the root's write.");
    }

    private static bool Allowed(Cell cell) => !(cell.Kind == Kind.Admitted && cell.Starts == Starts.OneWaitsOtherTwice);

    /// <summary>The constant the site's write stores: the async tail's and the spawn's only write, the admitted continuation's second.</summary>
    /// <param name="cell">The cell.</param>
    private static int Site(Cell cell) => cell.Kind == Kind.Admitted ? 2 : 1;

    /// <summary>The pairs on the field a cell keeps, each as the constants its two writes store, smaller first; two detached admitted
    /// seedings keep every combination of their synchronous and continuation writes.</summary>
    /// <param name="cell">The cell.</param>
    private static HashSet<string> Expected(Cell cell)
    {
        if (cell.Starts == Starts.BothWait)
            return [];
        // R2: later runs' synchronous parts share the continuation, so two detached seedings keep all three write pairs.
        if (cell.Kind == Kind.Admitted)
            return ["1-1", "1-2", "2-2"];
        // R2, R9: the site's write with itself, from the start that does not wait or from either start before its wait.
        return ["1-1"];
    }

    private static Analyzed Observe(Cell cell) =>
        Runs.GetOrAdd(cell.ToString(), _ => new Lazy<Analyzed>(() => cell.Kind switch
        {
            Kind.Async => Hosted(ASYNC, cell.Starts switch
            {
                Starts.BothWait => "await a.RunAsync(); await b.RunAsync();",
                Starts.OneWaitsOtherTwice => "await a.RunAsync(); _ = b.RunAsync(); _ = b.RunAsync();",
                _ => "var x = a.RunAsync(); var y = b.RunAsync(); await x; await y;"
            }),
            Kind.TaskRun => Hosted(TASK_RUN, cell.Starts switch
            {
                Starts.BothWait => "a.Start().Wait(); b.Start().Wait();",
                Starts.OneWaitsOtherTwice => "a.Start().Wait(); b.Start(); b.Start();",
                _ => "var x = a.Start(); var y = b.Start(); x.Wait(); y.Wait();"
            }),
            _ => Admitted(cell.Starts == Starts.BothWait
                ? "Migrate<A>(a => SeedAsync().Wait()); Migrate<A>(a => SeedAsync().Wait());"
                : "Migrate<A>(a => { var d = SeedAsync(); }); Migrate<A>(a => { var d = SeedAsync(); });")
        })).Value;

    /// <summary>The order graph of a run, read from its execution analysis.</summary>
    /// <param name="run">The analysed program.</param>
    private static HappensBefore Order(EngineRun run) =>
        (HappensBefore)typeof(ExecutionAnalysis).GetField("_order", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                                                .GetValue(run.Execution.Analysis)!;

    /// <summary>The program of a test, with the text of its source file to read the constant of a write from.</summary>
    /// <param name="Run">The analysed program.</param>
    /// <param name="Text">The source file of the program.</param>
    private sealed record Analyzed(EngineRun Run, string Text)
    {
        private static readonly Regex Written = new(@"Value = (\d+)", RegexOptions.CultureInvariant);

        /// <summary>The constant a write of the field stores, read from its line, which holds one write.</summary>
        /// <param name="access">The write.</param>
        internal int Value(Access access)
        {
            var matches = Written.Matches(Text.Split('\n')[access.Source.StartLine - 1]);
            Assert.True(matches.Count == 1, $"Line {access.Source.StartLine} holds {matches.Count} writes.");
            return int.Parse(matches[0].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>The writes of the field that store a constant.</summary>
        /// <param name="value">The constant.</param>
        internal IReadOnlyList<Access> Writes(int value) =>
            Run.Of("Value").Where(access => access.Operation == AccessOperation.Write && Value(access) == value).ToArray();

        /// <summary>The pairs on the field, each as the constants its two writes store, smaller first.</summary>
        internal HashSet<string> ValuePairs() =>
            Run.Pairs.Pairs.Where(pair => pair.First.Resource.Member.Name == "Value" && pair.Second.Resource.Member.Name == "Value")
                           .Select(pair => (Value(pair.First), Value(pair.Second)) is var (x, y) && x <= y ? $"{x}-{y}" : $"{y}-{x}")
                           .ToHashSet(StringComparer.Ordinal);

        /// <summary>Whether a write of one constant keeps a pair with a write of another.</summary>
        /// <param name="first">One constant.</param>
        /// <param name="second">The other.</param>
        internal bool Pairs(int first, int second) => ValuePairs().Contains(first <= second ? $"{first}-{second}" : $"{second}-{first}");
    }

    /// <summary>Analyses a hosted worker whose <c>ExecuteAsync</c> allocates the receivers <c>a</c> then <c>b</c>, both over the worker's
    /// sink, and runs <paramref name="body"/>.</summary>
    /// <param name="runner">The members of the receivers' class, which writes the sink through <c>_sink</c>.</param>
    /// <param name="body">The statements of the worker after the allocations.</param>
    /// <param name="members">Further members of the worker.</param>
    private static Analyzed Hosted(string runner, string body, string members = "")
    {
        var source = Startup("services.AddHostedService<Worker>();") + $$"""

            public sealed class Sink
            {
                public int Value;
            }
            public sealed class Runner
            {
                private readonly Sink _sink;
                public Runner(Sink sink) { _sink = sink; }
            {{runner}}
            }
            public sealed class Worker : BackgroundService
            {
                private static bool Flag;
                private readonly Sink _sink = new Sink();
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var a = new Runner(_sink);
                    var b = new Runner(_sink);
            {{body}}
                    await Task.CompletedTask;
                }
            {{members}}
            }
            """;

        return new Analyzed(Analyze(source), Usings + source);
    }

    /// <summary>Analyses the once-launched worker whose <c>ExecuteAsync</c> runs <paramref name="body"/> beside the seeding members, as
    /// <c>PerPassAdmissionTests</c> builds it over the real Polly 7.2.3 asset.</summary>
    /// <param name="body">The body of the worker's <c>ExecuteAsync</c>.</param>
    private static Analyzed Admitted(string body)
    {
        var source = Usings + "using Polly;\nusing Polly.Retry;\n" + Startup("services.AddHostedService<Worker>();") + $$"""

            public sealed class Worker : BackgroundService
            {
                public static int Value;
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    {{body}}
                    await Task.CompletedTask;
                }
                {{SEEDING}}
            }
            """;
        var options = new FixtureOptions { MetadataReferences = LibraryPackages.BesideStubs };

        return new Analyzed(AnalyzeScope(FixtureSolution.Create(options, ("Case.cs", source)), "scope:Fixture"), source);
    }

    private static ScopeProgram Scope(EngineRun run)
    {
        var heap = run.Execution.Heap;
        var program = heap.Program;
        return new ScopeProgram(program.ScopeId, program.Input.Roots, program.Result, heap.Summaries, program.Input.Program,
                                program.Input.DiIndex, program.Input.InjectionBindings) { MetadataSupertypes = program.MetadataSupertypes };
    }

    private static string Describe(Access access) =>
        $"{access.InstanceId}|{access.OperationId}|{access.ExecutionId}|{access.Resource.Identity}|{access.Operation}";

    private static string PairKey(AccessPair pair) =>
        string.Join(" <> ", new[] { Describe(pair.First), Describe(pair.Second) }.Order(StringComparer.Ordinal)) + "|" + pair.Resource.Identity;

    private static string[] PairKeys(PairAnalysis pairs) => pairs.Pairs.Select(PairKey).Order(StringComparer.Ordinal).ToArray();
}
