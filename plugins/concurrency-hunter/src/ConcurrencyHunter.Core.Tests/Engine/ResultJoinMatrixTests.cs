using System.Collections.Concurrent;
using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The result-join matrix (R3): one fixture program, analysed once, with one hosted service per cell. Each cell consumes a task
/// through a path, a wrapper and a catch — an await, <c>.Result</c>, <c>GetAwaiter().GetResult()</c> or the configured form of it — and
/// writes <c>Box.Count</c> after; where the producer has a work, that work writes <c>Box.Count</c> before it completes. A cell is ordered
/// when <c>HappensBefore</c> proves the consumer's join and the join comes before the write on every path, and, where there is a work,
/// when the two writes form no pair; the two observations must agree. The oracle is the twin of each producer and path, which consumes
/// the same task with <c>t.Wait()</c> (R3: "as <c>Wait()</c> is").</summary>
public sealed class ResultJoinMatrixTests
{
    private static readonly Lazy<EngineRun> Results = new(() => Analyze(Source()), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentDictionary<string, Observed> Observations = new(StringComparer.Ordinal);

    /// <summary>How the task is consumed.</summary>
    public enum Consumer
    {
        Await,
        Result,
        GetAwaiterGetResult,
        ConfiguredGetResult
    }

    /// <summary>Where the task comes from.</summary>
    public enum Producer
    {
        TaskRunSync,
        TaskRunAsync,
        StartNewUnwrap,
        ContinueWith,
        AsyncCallNotAwaited,
        FromResult,
        Opaque
    }

    /// <summary>Where the task is kept between its producer and its consumer.</summary>
    public enum Path
    {
        Local,
        Field
    }

    /// <summary>What the consumer is applied to.</summary>
    public enum Wrapper
    {
        None,
        ValueTaskOver,
        NonGenericValueTaskOver,
        AsTask,
        WaitAsyncTimeout,
        WaitAsyncToken,
        WhenAnyInner
    }

    /// <summary>Whether the consumption stands in a <c>try</c> whose <c>catch</c> takes every exception, the write after it.</summary>
    public enum Catch
    {
        None,
        CatchAll
    }

    /// <summary>Which tasks the consumed one may be.</summary>
    public enum Alternatives
    {
        One,
        TwoSpawned,
        SpawnedOrOpaque
    }

    /// <summary>A cell; its name is the hosted service that runs it.</summary>
    /// <param name="Consumer">How the task is consumed.</param>
    /// <param name="Producer">Where the task comes from.</param>
    /// <param name="Path">Where the task is kept.</param>
    /// <param name="Wrapper">What the consumer is applied to.</param>
    /// <param name="Catch">Whether a catch-all surrounds the consumption.</param>
    /// <param name="Alternatives">Which tasks the consumed one may be.</param>
    private sealed record Cell(Consumer Consumer, Producer Producer, Path Path, Wrapper Wrapper, Catch Catch, Alternatives Alternatives)
    {
        public string Name => $"C_{Consumer}_{Producer}_{Path}_{Wrapper}_{Catch}_{Alternatives}";
    }

    /// <summary>What a cell or a twin did.</summary>
    /// <param name="Proven">Whether its consumer's join is proven and comes before its write on every path.</param>
    /// <param name="Paired">Whether the work's write and its own form a pair; null where the producer has no work.</param>
    /// <param name="Problem">Why it could not be observed, or null.</param>
    private sealed record Observed(bool Proven, bool? Paired, string? Problem);

    // ---- the cells ----

    private static IEnumerable<Cell> AllCombinations() =>
        from consumer in Enum.GetValues<Consumer>()
        from producer in Enum.GetValues<Producer>()
        from path in Enum.GetValues<Path>()
        from wrapper in Enum.GetValues<Wrapper>()
        from @catch in Enum.GetValues<Catch>()
        from alternatives in Enum.GetValues<Alternatives>()
        select new Cell(consumer, producer, path, wrapper, @catch, alternatives);

    private static IEnumerable<Cell> Cells() => AllCombinations().Where(Allowed);

    /// <summary>The cells the matrix keeps. A non-generic <c>ValueTask</c> has no <c>Result</c>. A catch matters only under a wrapper that
    /// leaves the consumption's own throwing to decide: a proxy is never ordered, and <c>AsTask</c> and <c>WhenAnyInner</c> give back a
    /// <c>Task</c>, which throws only after it completes. A second alternative, like a catch, can only take a proof away, so it is checked
    /// where one exists: over <c>Task.Run</c> in a local, without a catch, presented as a conditional or through <c>WhenAny</c>.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Allowed(Cell cell) =>
        !(cell.Consumer == Consumer.Result && cell.Wrapper == Wrapper.NonGenericValueTaskOver) &&
        (cell.Catch == Catch.None || cell.Wrapper is Wrapper.None or Wrapper.ValueTaskOver or Wrapper.NonGenericValueTaskOver) &&
        (cell.Alternatives == Alternatives.One ||
         cell.Producer == Producer.TaskRunSync && cell.Path == Path.Local && cell.Wrapper is Wrapper.None or Wrapper.WhenAnyInner &&
         cell.Catch == Catch.None);

    /// <summary>The twin of a producer and a path: the same task, consumed by <c>t.Wait()</c>.</summary>
    /// <param name="producer">The producer.</param>
    /// <param name="path">The path.</param>
    private static string Twin(Producer producer, Path path) => $"W_{producer}_{path}";

    private static IEnumerable<(Producer Producer, Path Path)> Twins() =>
        from producer in Enum.GetValues<Producer>()
        from path in Enum.GetValues<Path>()
        select (producer, path);

    private static bool HasWork(Producer producer) => producer is not (Producer.FromResult or Producer.Opaque);

    // ---- the table ----

    /// <summary>Whether a cell is ordered, by R3 over its twin.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Expected(Cell cell)
    {
        // R3: no single proven handle orders nothing.
        if (cell.Alternatives != Alternatives.One)
            return false;
        // R3: a proxy carries no join.
        if (cell.Wrapper is Wrapper.WaitAsyncTimeout or Wrapper.WaitAsyncToken)
            return false;
        // R3: what a ValueTask's Result and GetResult() throw may come before it completes, so a catch-all goes around the join.
        if (cell.Catch == Catch.CatchAll && cell.Wrapper is Wrapper.ValueTaskOver or Wrapper.NonGenericValueTaskOver &&
            cell.Consumer != Consumer.Await)
        {
            return false;
        }

        // R3: a same task carries the join of the task it is, which is ordered as Wait() on it is.
        return Proven(cell.Producer, cell.Path);
    }

    /// <summary>Whether the producer's task at a path is proven: what its twin observes.</summary>
    /// <param name="producer">The producer.</param>
    /// <param name="path">The path.</param>
    private static bool Proven(Producer producer, Path path) => Observe(Twin(producer, path)).Proven;

    // ---- the tests ----

    [Fact]
    public void Every_single_task_cell_without_a_catch_is_ordered_exactly_as_its_twin()
    {
        var failures = Failures(Cells().Where(cell => cell.Alternatives == Alternatives.One && cell.Catch == Catch.None));
        Assert.True(failures.Count == 0, $"{failures.Count} cell failure(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_catch_all_cell_is_ordered_only_where_its_consumption_throws_after_completion()
    {
        var failures = Failures(Cells().Where(cell => cell.Catch == Catch.CatchAll));
        Assert.True(failures.Count == 0, $"{failures.Count} cell failure(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void No_cell_with_a_second_alternative_is_ordered()
    {
        var failures = Failures(Cells().Where(cell => cell.Alternatives != Alternatives.One));
        Assert.True(failures.Count == 0, $"{failures.Count} cell failure(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_twin_observes_its_wait_consistently()
    {
        var failures = new List<string>();
        foreach (var (producer, path) in Twins())
        {
            var name = Twin(producer, path);
            var observed = Observe(name);
            if (observed.Problem is not null)
                failures.Add($"{name}: {observed.Problem}");
            else if (observed.Paired is bool paired && paired == observed.Proven)
                failures.Add($"{name}: the wait is {(observed.Proven ? "" : "not ")}proven, but the writes {(paired ? "" : "do not ")}pair");
            // R3: a task no execution completes has no join to prove.
            else if (!HasWork(producer) && observed.Proven)
                failures.Add($"{name}: proven, but no execution completes the task");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} twin failure(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task The_matrix_has_exactly_the_allowed_cells()
    {
        var cells = Cells().ToArray();
        Assert.Equal(548, cells.Length);
        Assert.Equal(378, cells.Count(cell => cell.Alternatives == Alternatives.One && cell.Catch == Catch.None));
        Assert.Equal(154, cells.Count(cell => cell.Alternatives == Alternatives.One && cell.Catch == Catch.CatchAll));
        Assert.Equal(16, cells.Count(cell => cell.Alternatives != Alternatives.One));
        Assert.All(cells.Where(cell => cell.Alternatives != Alternatives.One), cell => Assert.Equal(Catch.None, cell.Catch));
        Assert.Equal(Enum.GetValues<Consumer>(), cells.Select(cell => cell.Consumer).Distinct().Order());
        Assert.Equal(Enum.GetValues<Producer>(), cells.Select(cell => cell.Producer).Distinct().Order());
        Assert.Equal(Enum.GetValues<Path>(), cells.Select(cell => cell.Path).Distinct().Order());
        Assert.Equal(Enum.GetValues<Wrapper>(), cells.Select(cell => cell.Wrapper).Distinct().Order());
        Assert.Equal(Enum.GetValues<Catch>(), cells.Select(cell => cell.Catch).Distinct().Order());
        Assert.Equal(Enum.GetValues<Alternatives>(), cells.Select(cell => cell.Alternatives).Distinct().Order());
        var compilation = (await FixtureSolution.Create(("Case.cs", Usings + Source())).Projects.Single().GetCompilationAsync())!;
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        // Every cell and every twin is a hosted service of the run, and nothing else is.
        var roots = Results.Value.Execution.Heap.Program.Input.Roots
                           .Select(root => root.Entry.Symbol)
                           .Where(symbol => symbol.EndsWith(".ExecuteAsync(CancellationToken)", StringComparison.Ordinal))
                           .Select(symbol => symbol[..symbol.IndexOf('.', StringComparison.Ordinal)])
                           .Distinct(StringComparer.Ordinal)
                           .Order(StringComparer.Ordinal);
        Assert.Equal(cells.Select(cell => cell.Name).Concat(Twins().Select(twin => Twin(twin.Producer, twin.Path))).Order(StringComparer.Ordinal),
                     roots);
    }

    // ---- observing ----

    private static List<string> Failures(IEnumerable<Cell> cells)
    {
        var failures = new List<string>();
        foreach (var cell in cells)
        {
            var observed = Observe(cell.Name);
            if (observed.Problem is not null)
            {
                failures.Add($"{cell.Name}: {observed.Problem}");
                continue;
            }

            if (observed.Paired is bool paired && paired == observed.Proven)
                failures.Add($"{cell.Name}: the join is {(observed.Proven ? "" : "not ")}proven, but the writes {(paired ? "" : "do not ")}pair");
            var expected = Expected(cell);
            if (observed.Proven != expected)
                failures.Add($"{cell.Name}: {(observed.Proven ? "ordered" : "not ordered")}, expected {(expected ? "ordered" : "not ordered")}" +
                             (observed.Proven ? " (an unsafe narrowing)" : ""));
        }

        return failures;
    }

    private static Observed Observe(string name) => Observations.GetOrAdd(name, ObserveOnce);

    /// <summary>What a cell or a twin did: whether the one join on its consumption line is proven and comes before its write on every
    /// path, and whether its work's write and its own form a pair.</summary>
    /// <param name="name">The hosted service.</param>
    private static Observed ObserveOnce(string name)
    {
        var run = Results.Value;
        var lines = Lines.Value;
        int Line(string kind) => lines.TryGetValue($"{kind}:{name}", out var line) ? line : -1;
        var consumed = Line("C");
        var written = Line("X");
        var worked = Line("W");

        var joins = run.Execution.Heap.Heap.Instances.Values
                       .SelectMany(instance => instance.Summary.Joins.Where(join => join.Provenance.Span.StartLine == consumed)
                                                       .Select(join => (Instance: instance, Join: join)))
                       .ToArray();
        if (joins.Length == 0)
            return new Observed(false, null, "no join on the consumption line");
        if (joins.Select(pair => (pair.Instance.BodyId, pair.Join.OperationId)).Distinct().Count() != 1)
            return new Observed(false, null, $"{joins.Length} joins on the consumption line");

        var writes = run.Accesses("Count").Where(access => access.Operation != AccessOperation.Read && access.Source.StartLine == written).ToArray();
        if (writes.Length == 0)
            return new Observed(false, null, "no write after the consumption");

        var unproven = run.Execution.Analysis.UnprovenJoins.ToHashSet();
        var proven = joins.All(pair => !unproven.Contains(new OperationSite(pair.Instance.BodyId, pair.Join.OperationId))) &&
                     writes.All(write => joins.Any(pair => run.Execution.Analysis.JoinDominates(pair.Instance.Id, pair.Join.OperationId, write)));
        if (worked < 0)
            return new Observed(proven, null, null);

        var work = run.Accesses("Count").Where(access => access.Operation != AccessOperation.Read && access.Source.StartLine == worked).ToArray();
        if (work.Length == 0)
            return new Observed(proven, null, "no write in the work");
        var paired = run.PairsOn("Count").Any(pair => work.Contains(pair.First) && writes.Contains(pair.Second) ||
                                                      work.Contains(pair.Second) && writes.Contains(pair.First));
        return new Observed(proven, paired, null);
    }

    // ---- the fixture ----

    private static readonly Lazy<IReadOnlyDictionary<string, int>> Lines = new(() =>
    {
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        var text = (Usings + Source()).Split('\n');
        for (var index = 0; index < text.Length; index++)
        {
            var start = text[index].IndexOf("/*", StringComparison.Ordinal);
            if (start >= 0 && text[index].IndexOf("*/", start, StringComparison.Ordinal) is var end and > 0 && text[index][start + 2] is 'C' or 'X' or 'W' &&
                text[index][start + 3] == ':')
            {
                lines.Add(text[index][(start + 2)..end], index + 1);
            }
        }

        return lines;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string? _source;

    /// <summary>The program: the box every work and consumer writes, the opaque producer, and one hosted service per cell and twin.</summary>
    private static string Source() => _source ??= Build();

    private static string Build()
    {
        var text = new StringBuilder("""
            public sealed class Box
            {
                public int Count;
            }

            public static class Opaque
            {
                public static extern Task<Box> Make();
            }

            """);
        var registrations = new StringBuilder();
        void Add(string name, string members, string run)
        {
            text.Append($$"""
                public sealed class {{name}} : BackgroundService
                {
                    private readonly Box _box = new Box();
                    private readonly bool _flag = Environment.ProcessorCount > 1;
                    private Task<Box> _t;
                {{members}}
                {{run}}
                }


                """);
            registrations.Append($"services.AddHostedService<{name}>();\n");
        }

        foreach (var cell in Cells())
        {
            var (producerMembers, task) = Produce(cell.Producer, cell.Name);
            Add(cell.Name, producerMembers, Run(cell.Path, task, Alternative(cell.Alternatives), Consume(cell)));
        }

        foreach (var (producer, path) in Twins())
        {
            var name = Twin(producer, path);
            var (producerMembers, task) = Produce(producer, name);
            Add(name, producerMembers, Run(path, task, "", $"t.Wait(); /*C:{name}*/\n_box.Count = 2; /*X:{name}*/"));
        }

        return text + Startup(registrations.ToString());
    }

    /// <summary>The members a producer needs and the expression making the task, whose work writes the box, marked, before it completes.</summary>
    /// <param name="producer">The producer.</param>
    /// <param name="name">The cell or twin, which names the marker.</param>
    private static (string Members, string Task) Produce(Producer producer, string name)
    {
        var write = $"_box.Count = 1; /*W:{name}*/";
        return producer switch
        {
            Producer.TaskRunSync => ("", $"Task.Run(() =>\n{{\n{write}\nreturn _box;\n}})"),
            Producer.TaskRunAsync => ("", $"Task.Run(async () =>\n{{\nawait Task.Yield();\n{write}\nreturn _box;\n}})"),
            Producer.StartNewUnwrap => ("", $"Task.Factory.StartNew(async () =>\n{{\nawait Task.Yield();\n{write}\nreturn _box;\n}}).Unwrap()"),
            Producer.ContinueWith => ("", $"Task.Run(() => {{ }}).ContinueWith(_ =>\n{{\n{write}\nreturn _box;\n}})"),
            // The write stands after the first await, so it is the async call's tail that makes it and not the caller.
            Producer.AsyncCallNotAwaited => ($"private async Task<Box> WorkAsync()\n{{\nawait Task.Yield();\n{write}\nreturn _box;\n}}", "WorkAsync()"),
            Producer.FromResult => ("", "Task.FromResult(_box)"),
            Producer.Opaque => ("", "Opaque.Make()"),
            _ => throw new ArgumentOutOfRangeException(nameof(producer), producer, null)
        };
    }

    /// <summary>The statements making the second task <c>u</c> a cell's consumed task may be instead of <c>t</c>.</summary>
    /// <param name="alternatives">The cell's alternatives.</param>
    private static string Alternative(Alternatives alternatives) => alternatives switch
    {
        Alternatives.One => "",
        Alternatives.TwoSpawned => "var u = Task.Run(() =>\n{\n_box.Count = 3;\nreturn _box;\n});\n",
        Alternatives.SpawnedOrOpaque => "var u = Opaque.Make();\n",
        _ => throw new ArgumentOutOfRangeException(nameof(alternatives), alternatives, null)
    };

    /// <summary>The hosted service's run: the task made into a local and consumed there, or stored in a field by one method and consumed
    /// by another.</summary>
    /// <param name="path">The path.</param>
    /// <param name="task">The expression making the task.</param>
    /// <param name="alternative">The statements making the second task, or none.</param>
    /// <param name="consumption">The statements consuming <c>t</c> and writing the box.</param>
    private static string Run(Path path, string task, string alternative, string consumption) => path switch
    {
        Path.Local => $"protected override async Task ExecuteAsync(CancellationToken stoppingToken)\n{{\nvar t = {task};\n{alternative}{consumption}\n}}",
        Path.Field => $"protected override async Task ExecuteAsync(CancellationToken stoppingToken)\n{{\nStore();\nawait Consume(stoppingToken);\n}}\n" +
                      $"private void Store()\n{{\n_t = {task};\n}}\n" +
                      $"private async Task Consume(CancellationToken stoppingToken)\n{{\nvar t = _t;\n{alternative}{consumption}\n}}",
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, null)
    };

    /// <summary>A cell's consumption of <c>t</c> through its wrapper and catch, marked, then its write of the box, marked. The wrapper is
    /// made before the <c>try</c>, and so is the awaiter of a <c>GetResult()</c> under a catch-all: the catch axis checks the consumption's
    /// own throwing, and a call before the join that may throw would go around it whatever the consumption does.</summary>
    /// <param name="cell">The cell.</param>
    private static string Consume(Cell cell)
    {
        var name = cell.Name;
        var other = cell.Alternatives == Alternatives.One ? "" : ", u";
        var wrap = cell.Wrapper switch
        {
            Wrapper.None => cell.Alternatives == Alternatives.One ? "var w = t;" : "var w = _flag ? t : u;",
            Wrapper.ValueTaskOver => "var w = new ValueTask<Box>(t);",
            Wrapper.NonGenericValueTaskOver => "var w = new ValueTask(t);",
            Wrapper.AsTask => "var w = new ValueTask<Box>(t).AsTask();",
            Wrapper.WaitAsyncTimeout => "var w = t.WaitAsync(TimeSpan.FromSeconds(1));",
            Wrapper.WaitAsyncToken => "var w = t.WaitAsync(stoppingToken);",
            Wrapper.WhenAnyInner => $"var w = await Task.WhenAny(t{other});",
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        var valued = cell.Wrapper != Wrapper.NonGenericValueTaskOver;
        var given = valued ? "var b = " : "";
        var separate = cell.Catch == Catch.CatchAll;
        var (prepare, consume) = cell.Consumer switch
        {
            Consumer.Await => ("", $"{given}await w;"),
            Consumer.Result => ("", "var b = w.Result;"),
            Consumer.GetAwaiterGetResult => separate ? ("var a = w.GetAwaiter();\n", $"{given}a.GetResult();") : ("", $"{given}w.GetAwaiter().GetResult();"),
            Consumer.ConfiguredGetResult => separate
                ? ("var a = w.ConfigureAwait(false).GetAwaiter();\n", $"{given}a.GetResult();")
                : ("", $"{given}w.ConfigureAwait(false).GetAwaiter().GetResult();"),
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        consume += $" /*C:{name}*/";
        var guarded = (cell.Wrapper, cell.Catch) switch
        {
            (_, Catch.CatchAll) => $"try\n{{\n{consume}\n}}\ncatch\n{{\n}}",
            (Wrapper.WaitAsyncTimeout, _) => $"try\n{{\n{consume}\n}}\ncatch (TimeoutException)\n{{\n}}",
            (Wrapper.WaitAsyncToken, _) => $"try\n{{\n{consume}\n}}\ncatch (OperationCanceledException)\n{{\n}}",
            _ => consume
        };
        return $"{wrap}\n{prepare}{guarded}\n_box.Count = 2; /*X:{name}*/";
    }
}
