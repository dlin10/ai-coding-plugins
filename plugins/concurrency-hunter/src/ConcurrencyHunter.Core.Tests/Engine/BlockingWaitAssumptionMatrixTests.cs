using System.Collections.Concurrent;
using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The exceptional exit of a blocking wait for a task under the environment assumptions (R3, R4; ADR 0030), over every form of
/// wait, source of the task, process scope and handler: a write after the <c>try</c> is ordered after the work exactly where the wait's
/// exception comes only after the task completed. Each scope is one program, analysed once, with one hosted service per cell; a normal
/// return and an <c>await</c> keep today's order under every scope and source.</summary>
public sealed class BlockingWaitAssumptionMatrixTests
{
    public enum Form
    {
        Wait, Result, GetResultTask, GetResultTaskT, ConfiguredGetResult, WaitAllArray, WaitAllParams, ValueTaskResult, WaitTimeout,
        WaitToken, Await
    }

    public enum Source { TaskRun, AsyncCall, StartNew, StartNewDefault, StartNewCurrent, StartNewOwn, ContinueWithOwn, OwnFactory }

    public enum Scope
    {
        None, SetContext, SetContextUnreached, SetContextDeadBranch, WinFormsRun, WpfDispatcherRun, WpfApplicationRun, ThreadInterrupt
    }

    public enum Handler { CatchException, CatchAggregate, NormalReturn }

    public sealed record Cell(Form Form, Source Source, Scope Scope, Handler Handler)
    {
        public override string ToString() => $"{Form}/{Source}/{Scope}/{Handler}";

        /// <summary>The hosted service running the cell in its scope's program.</summary>
        public string Name => $"C_{Form}_{Source}_{Handler}";
    }

    /// <summary>The cells whose expected answer — no pair — the analysis cannot reach without what the run does not build, each by name
    /// with the reason; a name that names no such cell fails <see cref="Every_wider_cell_names_a_cell_that_expects_no_pair"/>.</summary>
    private static readonly Dictionary<string, string> Wider = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<Scope, Lazy<(EngineRun Run, IReadOnlyDictionary<string, int> Lines)>> Runs = new();

    public static IEnumerable<Cell> AllCells() =>
        from form in Enum.GetValues<Form>()
        from source in Enum.GetValues<Source>()
        from scope in Enum.GetValues<Scope>()
        from handler in Enum.GetValues<Handler>()
        let cell = new Cell(form, source, scope, handler)
        where Allowed(cell)
        select cell;

    public static TheoryData<Form, Source, Scope, Handler> Cells()
    {
        var cells = new TheoryData<Form, Source, Scope, Handler>();
        foreach (var cell in AllCells())
            cells.Add(cell.Form, cell.Source, cell.Scope, cell.Handler);
        return cells;
    }

    [Fact]
    public void There_are_495_cells_covering_every_value_of_every_axis()
    {
        var cells = AllCells().ToArray();

        Assert.Equal(495, cells.Length);
        Assert.Equal(cells.Length, cells.Select(cell => cell.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Enum.GetValues<Form>(), form => Assert.Contains(cells, cell => cell.Form == form));
        Assert.All(Enum.GetValues<Source>(), source => Assert.Contains(cells, cell => cell.Source == source));
        Assert.All(Enum.GetValues<Scope>(), scope => Assert.Contains(cells, cell => cell.Scope == scope));
        Assert.All(Enum.GetValues<Handler>(), handler => Assert.Contains(cells, cell => cell.Handler == handler));
        Assert.Contains(cells, cell => !PairExpected(cell));
        Assert.Contains(cells, PairExpected);
    }

    /// <summary>The scope's installer stands where the scope says: in a body the analysis reaches, on a branch that never runs in one, or
    /// in a body it never reaches.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="reached">Whether the analysis reaches the installer's body.</param>
    [Theory]
    [InlineData(Scope.SetContext, true)]
    [InlineData(Scope.SetContextDeadBranch, true)]
    [InlineData(Scope.SetContextUnreached, false)]
    public void The_installer_body_is_reached_as_the_scope_says(Scope scope, bool reached)
    {
        var run = Program(scope).Run;
        var bodies = run.Execution.Heap.Program.Result.ReachedBodies.Keys.Where(body => body.Contains("Install", StringComparison.Ordinal)).ToArray();

        Assert.Equal(reached, bodies.Length != 0);
    }

    [Fact]
    public void Every_wider_cell_names_a_cell_that_expects_no_pair()
    {
        var cells = AllCells().ToDictionary(cell => cell.ToString(), StringComparer.Ordinal);

        Assert.All(Wider, wider =>
        {
            Assert.True(cells.TryGetValue(wider.Key, out var cell), $"{wider.Key} names no cell.");
            Assert.False(PairExpected(cell!), $"{wider.Key} expects a pair, so it cannot be wider.");
            Assert.False(string.IsNullOrWhiteSpace(wider.Value));
        });
    }

    /// <summary>No cell is narrower than its expectation; a cell outside <see cref="Wider"/> answers exactly, and one inside it keeps the
    /// pair it is listed for.</summary>
    /// <param name="form">How the task is waited for.</param>
    /// <param name="source">Where the task comes from.</param>
    /// <param name="scope">What the process scope's visible code installs.</param>
    /// <param name="handler">How the wait's exception is handled before the write after it.</param>
    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_is_exact_outside_the_wider_list(Form form, Source source, Scope scope, Handler handler)
    {
        var cell = new Cell(form, source, scope, handler);
        var pair = Paired(cell);

        if (PairExpected(cell))
            Assert.True(pair, $"{cell}: the pair the requirements keep is gone.");
        else if (Wider.ContainsKey(cell.ToString()))
            Assert.True(pair, $"{cell}: listed as wider, but it is exact now; take it off the list.");
        else
            Assert.False(pair, $"{cell}: the pair the requirements remove is still there.");
    }

    /// <summary>Which values of the axes make a cell. The two exceptions of R4 are independent conditions — a context installed in the
    /// scope, a task on a scheduler the program names — so the source varies only in a scope without an installer, and the scope only
    /// over <c>Task.Run</c>, whose task runs on the default scheduler; the form and the handler vary everywhere.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Allowed(Cell cell) => cell.Scope == Scope.None || cell.Source == Source.TaskRun;

    /// <summary>Whether the requirements keep the pair of the work's write and the write after the wait.</summary>
    /// <param name="cell">The cell.</param>
    private static bool PairExpected(Cell cell)
    {
        // R2: a wait with a timeout or a token may return or throw before the work completes.
        if (cell.Form is Form.WaitTimeout or Form.WaitToken)
            return true;
        // R3: a normal return and an await are read as today, whatever the scope and the source.
        if (cell.Handler == Handler.NormalReturn || cell.Form == Form.Await)
            return false;
        // R3: what a ValueTask's Result throws may come before it completes.
        if (cell.Form == Form.ValueTaskResult)
            return true;
        // R4(b): a task created with a scheduler argument other than TaskScheduler.Default, or by a StartNew without one on a separate
        // factory in a scope that constructs a factory on such a scheduler, may fault its wait before it completes.
        if (cell.Source is Source.StartNewCurrent or Source.StartNewOwn or Source.ContinueWithOwn or Source.OwnFactory)
            return true;
        // R4(a): a reachable installer anywhere in the process scope, on a branch that never runs included (D-0022), lets the wait end
        // early; one in a body the analysis does not reach does not.
        if (cell.Scope is not (Scope.None or Scope.SetContextUnreached))
            return true;
        // R3: the wait's exception comes only after the task completed, so the path through the handler is ordered as the normal return.
        return false;
    }

    /// <summary>Whether the work's write and the write after the wait form a pair in the cell's scope program.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Paired(Cell cell)
    {
        var (run, lines) = Program(cell.Scope);
        int Line(string kind) => lines.TryGetValue($"{kind}:{cell.Name}", out var line) ? line : -1;
        var work = run.Accesses("Count").Where(access => access.Operation != AccessOperation.Read && access.Source.StartLine == Line("W")).ToArray();
        var after = run.Accesses("Count").Where(access => access.Operation != AccessOperation.Read && access.Source.StartLine == Line("X")).ToArray();
        Assert.True(work.Length != 0, $"{cell}: no write in the work.");
        Assert.True(after.Length != 0, $"{cell}: no write after the wait.");
        return run.PairsOn("Count").Any(pair => work.Contains(pair.First) && after.Contains(pair.Second) ||
                                                work.Contains(pair.Second) && after.Contains(pair.First));
    }

    private static (EngineRun Run, IReadOnlyDictionary<string, int> Lines) Program(Scope scope) =>
        Runs.GetOrAdd(scope, key => new Lazy<(EngineRun, IReadOnlyDictionary<string, int>)>(() => Build(key))).Value;

    // ---- the programs ----

    private static (EngineRun Run, IReadOnlyDictionary<string, int> Lines) Build(Scope scope)
    {
        var text = new StringBuilder(SCHEDULER);
        var registrations = new StringBuilder();
        foreach (var cell in AllCells().Where(cell => cell.Scope == scope))
        {
            text.Append(Service(cell));
            registrations.Append($"services.AddHostedService<{cell.Name}>();\n");
        }

        var (installer, registration) = Installer(scope);
        text.Append(installer);
        registrations.Append(registration);
        var source = text + Startup(registrations.ToString());
        var desktop = scope is Scope.WinFormsRun or Scope.WpfDispatcherRun or Scope.WpfApplicationRun ? DesktopReferences() : [];
        var options = new FixtureOptions
        {
            MetadataReferences = desktop.Select(reference => reference.Reference).ToArray(),
            OmittedPlatformAssemblies = desktop.Select(reference => reference.Name).ToArray()
        };
        var run = AnalyzeScope(FixtureSolution.Create(options, ("Case.cs", Usings + source)), "scope:Fixture");
        return (run, Markers(Usings + source));
    }

    /// <summary>The line of each <c>/*W:name*/</c> and <c>/*X:name*/</c> marker.</summary>
    /// <param name="text">The program.</param>
    private static Dictionary<string, int> Markers(string text)
    {
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        var split = text.Split('\n');
        for (var index = 0; index < split.Length; index++)
        {
            var start = split[index].IndexOf("/*", StringComparison.Ordinal);
            if (start >= 0 && split[index].IndexOf("*/", start, StringComparison.Ordinal) is var end and > 0 && split[index][start + 2] is 'W' or 'X' &&
                split[index][start + 3] == ':')
            {
                lines.Add(split[index][(start + 2)..end], index + 1);
            }
        }

        return lines;
    }

    /// <summary>The scheduler a program names, the box every work and wait writes, and the opaque producer.</summary>
    private const string SCHEDULER = """
        public sealed class OwnScheduler : TaskScheduler
        {
            protected override System.Collections.Generic.IEnumerable<Task>? GetScheduledTasks() => null;
            protected override void QueueTask(Task task) => TryExecuteTask(task);
            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => TryExecuteTask(task);
        }

        public static class Schedulers
        {
            public static readonly TaskScheduler Own = new OwnScheduler();
        }

        public sealed class Box
        {
            public int Count;
        }

        public static class Opaque
        {
            public static extern Task Make();
        }

        """;

    /// <summary>The code a scope adds and its registration: a hosted service installing a context, one doing so on a branch that never
    /// runs, or a class nothing calls.</summary>
    /// <param name="scope">The scope.</param>
    private static (string Code, string Registration) Installer(Scope scope)
    {
        string Hosted(string body) => $$"""
            public sealed class Installer : BackgroundService
            {
                private static readonly bool Never = Environment.ProcessorCount < 0;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    {{body}}
                    return Task.CompletedTask;
                }
            }

            """;
        const string REGISTRATION = "services.AddHostedService<Installer>();\n";
        const string SET = "SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());";
        return scope switch
        {
            Scope.None => ("", ""),
            Scope.SetContext => (Hosted(SET), REGISTRATION),
            Scope.SetContextDeadBranch => (Hosted($"if (Never) {SET}"), REGISTRATION),
            Scope.SetContextUnreached => ($"public static class Installer\n{{\npublic static void Install() => {SET}\n}}\n", ""),
            Scope.WinFormsRun => (Hosted("System.Windows.Forms.Application.Run(new System.Windows.Forms.Form());"), REGISTRATION),
            Scope.WpfDispatcherRun => (Hosted("System.Windows.Threading.Dispatcher.Run();"), REGISTRATION),
            Scope.WpfApplicationRun => (Hosted("new System.Windows.Application().Run();"), REGISTRATION),
            Scope.ThreadInterrupt => (Hosted("Thread.CurrentThread.Interrupt();"), REGISTRATION),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
        };
    }

    /// <summary>A cell's hosted service: it makes the task, prepares what the wait reads before the <c>try</c> — a call before the
    /// join that may throw would go around it whatever the wait does — waits under its handler and writes the box after.</summary>
    /// <param name="cell">The cell.</param>
    private static string Service(Cell cell)
    {
        var valued = cell.Form != Form.GetResultTask;
        var (members, task) = Produce(cell.Source, valued, cell.Name);
        var (prepare, wait) = cell.Form switch
        {
            Form.Wait => ("", "t.Wait();"),
            Form.Result => ("", "_ = t.Result;"),
            Form.GetResultTask or Form.GetResultTaskT => ("var a = t.GetAwaiter();", "a.GetResult();"),
            Form.ConfiguredGetResult => ("var a = t.ConfigureAwait(false).GetAwaiter();", "a.GetResult();"),
            Form.WaitAllArray => ("", "Task.WaitAll(new[] { t });"),
            Form.WaitAllParams => ("", "Task.WaitAll(t);"),
            Form.ValueTaskResult => ("var v = new ValueTask<int>(t);", "_ = v.Result;"),
            Form.WaitTimeout => ("", "t.Wait(100);"),
            Form.WaitToken => ("", "t.Wait(stoppingToken);"),
            Form.Await => ("", "await t;"),
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        var guarded = cell.Handler switch
        {
            Handler.CatchException => $"try\n{{\n{wait}\n}}\ncatch (Exception)\n{{\n}}",
            Handler.CatchAggregate => $"try\n{{\n{wait}\n}}\ncatch (AggregateException)\n{{\n}}",
            _ => wait
        };
        return $$"""
            public sealed class {{cell.Name}} : BackgroundService
            {
                private readonly Box _box = new Box();
            {{members}}
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var t = {{task}};
                    {{prepare}}
                    {{guarded}}
                    _box.Count = 2; /*X:{{cell.Name}}*/
                    await Task.CompletedTask;
                }
            }


            """;
    }

    /// <summary>The members a source needs and the expression making its task, a <c>Task&lt;int&gt;</c> or a <c>Task</c>, whose work
    /// writes the box, marked, before it completes.</summary>
    /// <param name="source">The source.</param>
    /// <param name="valued">Whether the task completes with a value.</param>
    /// <param name="name">The cell, which names the marker.</param>
    private static (string Members, string Task) Produce(Source source, bool valued, string name)
    {
        var body = valued ? $"{{\n_box.Count = 1; /*W:{name}*/\nreturn 1;\n}}" : $"{{\n_box.Count = 1; /*W:{name}*/\n}}";
        const string OPTIONS = "CancellationToken.None, TaskCreationOptions.None";
        return source switch
        {
            Source.TaskRun => ("", $"Task.Run(() =>\n{body})"),
            // The write stands after the first await, so it is the async call's tail that makes it and not the caller.
            Source.AsyncCall => (valued
                ? $"private async Task<int> WorkAsync()\n{{\nawait Task.Yield();\n_box.Count = 1; /*W:{name}*/\nreturn 1;\n}}"
                : $"private async Task WorkAsync()\n{{\nawait Task.Yield();\n_box.Count = 1; /*W:{name}*/\n}}", "WorkAsync()"),
            Source.StartNew => ("", $"Task.Factory.StartNew(() =>\n{body})"),
            Source.StartNewDefault => ("", $"Task.Factory.StartNew(() =>\n{body}, {OPTIONS}, TaskScheduler.Default)"),
            Source.StartNewCurrent => ("", $"Task.Factory.StartNew(() =>\n{body}, {OPTIONS}, TaskScheduler.Current)"),
            Source.StartNewOwn => ("", $"Task.Factory.StartNew(() =>\n{body}, {OPTIONS}, Schedulers.Own)"),
            Source.ContinueWithOwn => ("", $"Task.Run(() => {{ }}).ContinueWith(_ =>\n{body}, Schedulers.Own)"),
            Source.OwnFactory => ("", $"new TaskFactory(Schedulers.Own).StartNew(() =>\n{body})"),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
        };
    }

    /// <summary>The reference assemblies of the Windows desktop pack of the installed .NET, by name, for the scopes that run a UI
    /// message loop; each replaces a platform assembly of the same name, such as the <c>WindowsBase</c> facade.</summary>
    private static IReadOnlyList<(string Name, MetadataReference Reference)> DesktopReferences()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var packs = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", "packs", "Microsoft.WindowsDesktop.App.Ref"));
        var major = Path.GetFileName(runtime).Split('.')[0] + ".";
        var version = Directory.Exists(packs)
            ? Directory.GetDirectories(packs).Select(Path.GetFileName).OfType<string>()
                       .Where(name => name.StartsWith(major, StringComparison.Ordinal) && Version.TryParse(name, out _))
                       .OrderByDescending(Version.Parse).FirstOrDefault()
            : null;
        var directory = version is null ? null : Path.Combine(packs, version, "ref", "net" + major + "0");
        Assert.True(directory is not null && Directory.Exists(directory),
                    $"No Microsoft.WindowsDesktop.App.Ref {major}x reference pack under {packs}.");
        return Directory.GetFiles(directory!, "*.dll")
                        .Select(path => (Path.GetFileNameWithoutExtension(path), (MetadataReference)MetadataReference.CreateFromFile(path)))
                        .ToArray();
    }

    // ---- outside the axes ----

    [Fact]
    public void Wait_all_of_two_ordinary_tasks_caught_orders_both()
    {
        var run = Analyze(Worker("var a = Task.Run(() => First()); var b = Task.Run(() => Second()); " +
                                 "try { Task.WaitAll(a, b); } catch (AggregateException) { } After();"));

        Assert.False(Pairs(run, "First", "After"));
        Assert.False(Pairs(run, "Second", "After"));
    }

    /// <summary>A task on a scheduler the program names lets the wait fault before it completes, and then before the other tasks of the
    /// same wait complete either (D-0020), wherever it stands among them.</summary>
    /// <param name="ownFirst">Whether the task on the program's scheduler is the first the wait lists.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Wait_all_with_a_task_on_a_program_scheduler_caught_orders_neither(bool ownFirst)
    {
        var tasks = ownFirst ? "own, plain" : "plain, own";
        var run = Analyze(SCHEDULER + Worker("var own = Task.Factory.StartNew(() => First(), CancellationToken.None, TaskCreationOptions.None, Schedulers.Own); " +
                                             $"var plain = Task.Run(() => Second()); try {{ Task.WaitAll({tasks}); }} catch (AggregateException) {{ }} After();"));

        Assert.True(Pairs(run, "First", "After"));
        Assert.True(Pairs(run, "Second", "After"));
    }

    /// <summary>A task whose identity is not proven may run on any scheduler, so the wait's fault orders none of its tasks (D-0020).</summary>
    [Fact]
    public void Wait_all_of_a_partly_proven_set_caught_orders_nothing()
    {
        var run = Analyze(SCHEDULER + Worker("var a = Task.Run(() => First()); var b = Opaque.Make(); " +
                                             "try { Task.WaitAll(a, b); } catch (AggregateException) { } After();"));

        Assert.True(Pairs(run, "First", "After"));
    }

    /// <summary>The fault of a <c>Wait()</c> on the task of a <c>Task.WhenAll</c> called inside the <c>try</c> comes only after every
    /// member completed, so it orders each of two ordinary tasks — through the group's members, not through a <c>Task.WaitAll</c>
    /// listing, and with the group's own call inside the <c>try</c>.</summary>
    [Fact]
    public void When_all_wait_of_two_ordinary_tasks_caught_orders_both()
    {
        var run = Analyze(Worker("var a = Task.Run(() => First()); var b = Task.Run(() => Second()); " +
                                 "try { Task.WhenAll(a, b).Wait(); } catch (AggregateException) { } After();"));

        Assert.False(Pairs(run, "First", "After"));
        Assert.False(Pairs(run, "Second", "After"));
    }

    /// <summary>A member on a scheduler the program names lets the group's <c>Wait()</c> fault before it completes, and then before the
    /// other members complete either (D-0020), wherever it stands among them.</summary>
    /// <param name="ownFirst">Whether the task on the program's scheduler is the first the group lists.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void When_all_wait_with_a_task_on_a_program_scheduler_caught_orders_neither(bool ownFirst)
    {
        var tasks = ownFirst ? "own, plain" : "plain, own";
        var run = Analyze(SCHEDULER + Worker("var own = Task.Factory.StartNew(() => First(), CancellationToken.None, TaskCreationOptions.None, Schedulers.Own); " +
                                             $"var plain = Task.Run(() => Second()); try {{ Task.WhenAll({tasks}).Wait(); }} catch (AggregateException) {{ }} After();"));

        Assert.True(Pairs(run, "First", "After"));
        Assert.True(Pairs(run, "Second", "After"));
    }

    /// <summary>A member whose identity is not proven may run on any scheduler, so the group's <c>Wait()</c> fault orders none of its
    /// members (D-0020).</summary>
    [Fact]
    public void When_all_wait_of_a_partly_proven_set_caught_orders_nothing()
    {
        var run = Analyze(SCHEDULER + Worker("var a = Task.Run(() => First()); var b = Opaque.Make(); " +
                                             "try { Task.WhenAll(a, b).Wait(); } catch (AggregateException) { } After();"));

        Assert.True(Pairs(run, "First", "After"));
    }

    public enum Fed { WaitAllParams, WaitAllArray, Wait, Result, GetResult, ConfiguredGetResult, WhenAllWait, Await }

    /// <summary>A program call computing what a wait is handed may throw before the wait, whichever task its value is proven to be: inside
    /// the <c>try</c> its path through the handler keeps the pair; called before the <c>try</c>, its exception does not reach the handler,
    /// and the wait's own fault comes only after the work completed. An <c>await</c> answers as it always has: its fault comes after its
    /// task completed, and the call feeding it inside the <c>try</c> may throw first.</summary>
    /// <param name="form">How the forwarded task is waited for.</param>
    /// <param name="inside">Whether the call forwarding the task stands inside the <c>try</c>.</param>
    [Theory]
    [MemberData(nameof(FedCases))]
    public void A_program_call_feeding_the_wait_inside_the_try_keeps_the_pair(Fed form, bool inside)
    {
        var handle = inside ? "Forward(t)" : "f";
        var prepare = inside ? "" : "var f = Forward(t); ";
        var run = Analyze(Worker($"var t = Task.Run(() => {{ First(); return 1; }}); {prepare}try {{ {FedWait(form, handle)} }} catch (Exception) {{ }} After();",
                                 FORWARD));

        Assert.Equal(inside, Pairs(run, "First", "After"));
    }

    public static TheoryData<Fed, bool> FedCases()
    {
        var cases = new TheoryData<Fed, bool>();
        foreach (var form in Enum.GetValues<Fed>())
        {
            cases.Add(form, true);
            cases.Add(form, false);
        }

        return cases;
    }

    /// <summary>The exception of a program call forwarding the pass's task inside the <c>try</c> leads through the handler to the next
    /// pass without the wait, so the work of one pass overlaps that of the next; forwarded before the <c>try</c>, the exception leaves the
    /// loop, and the wait's own fault comes only after the work completed.</summary>
    /// <param name="inside">Whether the call forwarding the task stands inside the <c>try</c>.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_program_call_feeding_wait_all_inside_the_try_keeps_the_next_pass_pair(bool inside)
    {
        var wait = inside ? "try { Task.WaitAll(Forward(t)); } catch (Exception) { }" : "var f = Forward(t); try { Task.WaitAll(f); } catch (Exception) { }";
        var run = Analyze(Worker($"for (var pass = 0; pass < 2; pass++) {{ var t = Task.Run(() => {{ First(); return 1; }}); {wait} }} After();", FORWARD));

        Assert.Equal(inside, Pairs(run, "First", "First"));
    }

    /// <summary>A helper's wait orders its caller's handler path only when no exception leaves the helper before the wait: one that
    /// forwards the task through a program call before waiting may throw first, one or two calls up, and so may a loop's next pass reached
    /// through the handler; a helper that throws only after its wait, a task forwarded before the <c>try</c> and a helper's normal return
    /// stay ordered.</summary>
    /// <param name="shape">A short name of the case, which tells the cases apart and names a failing one.</param>
    /// <param name="body">The worker's body around the helper call.</param>
    /// <param name="first">The method whose write pairs with the after-write, or a second run of itself.</param>
    /// <param name="paired">Whether the pair is kept.</param>
    [Theory]
    [InlineData("helper", "var t = Task.Run(() => { First(); return 1; }); try { Helper(t); } catch (Exception) { } After();", "After", true)]
    [InlineData("helper-twice", "var t = Task.Run(() => { First(); return 1; }); try { Outer(t); } catch (Exception) { } After();", "After", true)]
    [InlineData("helper-next-pass", "for (var pass = 0; pass < 2; pass++) { var t = Task.Run(() => { First(); return 1; }); try { Helper(t); } catch (Exception) { } } After();", "First", true)]
    [InlineData("late", "var t = Task.Run(() => { First(); return 1; }); try { Late(t); } catch (Exception) { } After();", "After", false)]
    [InlineData("forwarded-before", "var t = Task.Run(() => { First(); return 1; }); var f = Forward(t); try { Plain(f); } catch (Exception) { } After();", "After", false)]
    [InlineData("normal-return", "var t = Task.Run(() => { First(); return 1; }); Helper(t); After();", "After", false)]
    [InlineData("plain-next-pass", "for (var pass = 0; pass < 2; pass++) { var t = Task.Run(() => { First(); return 1; }); Plain(t); } After();", "First", false)]
    public void A_helper_forwarding_its_task_before_the_wait_keeps_the_handler_pair(string shape, string body, string first, bool paired)
    {
        const string HELPERS = """
            private static void Helper(Task<int> t) { Forward(t).Wait(); }
            private static void Outer(Task<int> t) { Helper(t); }
            private static void Late(Task<int> t) { t.Wait(); Forward(t); }
            private static void Plain(Task<int> t) { t.Wait(); }
            """;
        var run = Analyze(Worker(body, FORWARD + HELPERS));

        Assert.True(paired == Pairs(run, "First", first), $"{shape}: expected the pair {(paired ? "kept" : "removed")}.");
    }

    /// <summary>What computes the handle or the listing inside the <c>try</c> decides whether the handler path goes around the wait: a
    /// local read, a listing made for the call and converted to <c>Task[]</c>, and the call of an async method — whose exception faults
    /// the task it returns, which the wait then throws — cannot throw first; a field read may. A <c>Task.Run</c> written in the listing
    /// starts the work only once it returned, so nothing it throws precedes the work.</summary>
    /// <param name="handle">A short name of what computes the handle, which tells the cases apart and names a failing one.</param>
    /// <param name="body">The worker's body.</param>
    /// <param name="paired">Whether the work's write and the write after the <c>try</c> form a pair.</param>
    [Theory]
    [InlineData("local", "var t = Task.Run(() => { First(); return 1; }); try { t.Wait(); } catch (Exception) { } After();", false)]
    [InlineData("listing", "var t = Task.Run(() => { First(); return 1; }); try { Task.WaitAll(new Task<int>[] { t }); } catch (Exception) { } After();", false)]
    [InlineData("async-call", "try { WorkAsync().Wait(); } catch (Exception) { } After();", false)]
    [InlineData("run-in-listing", "try { Task.WaitAll(Task.Run(() => { First(); return 1; })); } catch (Exception) { } After();", false)]
    [InlineData("field", "_task = Task.Run(() => { First(); return 1; }); try { _task.Wait(); } catch (Exception) { } After();", true)]
    public void What_computes_the_handle_inside_the_try_decides_the_handler_pair(string handle, string body, bool paired)
    {
        const string MEMBERS = """
            private Task<int> _task = Task.FromResult(0);
            private async Task WorkAsync() { await Task.Yield(); First(); }
            """;
        var run = Analyze(Worker(body, MEMBERS));

        Assert.True(paired == Pairs(run, "First", "After"), $"{handle}: expected the pair {(paired ? "kept" : "removed")}.");
    }

    public enum Cast { None, Downcast }

    public enum Waited { WaitTask, WaitTaskOfT, ResultOfT, GetResultTask, GetResultTaskOfT, ConfiguredTask, ConfiguredTaskOfT, WaitAllParams, WaitAllArray, WhenAllWait, Await }

    public enum Side { After, NextPass }

    /// <summary>A conversion of the handle inside the <c>try</c> is part of the wait only when it cannot throw: an implicit one — the
    /// <c>Task&lt;T&gt;</c> to <c>Task</c> the non-generic forms and the listings make — leaves the wait's own fault after the work
    /// completed, so the work is ordered before the access after the <c>catch</c> and the next pass. An explicit downcast throws
    /// <c>InvalidCastException</c> here before the wait is called, with the work still running, so its path through the handler keeps
    /// both pairs, in every form of wait, directly and in a helper. An <c>await</c> answers as it always has.</summary>
    /// <param name="cast">Whether the handle is downcast to a type the task is not.</param>
    /// <param name="form">How the handle is waited for.</param>
    /// <param name="placement">Whether the wait stands in the <c>try</c> or in a helper called there.</param>
    /// <param name="side">Whether the pair is with the access after the <c>catch</c> or with the next pass's work.</param>
    [Theory]
    [MemberData(nameof(CastCases))]
    public void A_conversion_that_may_throw_before_the_wait_keeps_the_pair(Cast cast, Waited form, Placement placement, Side side)
    {
        var handle = cast == Cast.Downcast ? "((Task<string>)(object)t)" : "t";
        var wait = form switch
        {
            Waited.WaitTask => $"((Task){handle}).Wait();",
            Waited.WaitTaskOfT => $"{handle}.Wait();",
            Waited.ResultOfT => $"_ = {handle}.Result;",
            Waited.GetResultTask => $"((Task){handle}).GetAwaiter().GetResult();",
            Waited.GetResultTaskOfT => $"{handle}.GetAwaiter().GetResult();",
            Waited.ConfiguredTask => $"((Task){handle}).ConfigureAwait(false).GetAwaiter().GetResult();",
            Waited.ConfiguredTaskOfT => $"{handle}.ConfigureAwait(false).GetAwaiter().GetResult();",
            Waited.WaitAllParams => $"Task.WaitAll({handle});",
            Waited.WaitAllArray => $"Task.WaitAll(new Task[] {{ {handle} }});",
            Waited.WhenAllWait => $"Task.WhenAll({handle}).Wait();",
            _ => $"await {handle};"
        };
        var helper = placement == Placement.Helper ? $"private static void Waiting(Task<int> t) {{ {wait} }}" : "";
        var inTry = placement == Placement.Helper ? "Waiting(t);" : wait;
        var start = "var t = Task.Run(() => { First(); return 1; });";
        var body = side == Side.After
            ? $"{start} try {{ {inTry} }} catch (Exception) {{ }} After();"
            : $"for (var pass = 0; pass < 2; pass++) {{ {start} try {{ {inTry} }} catch (Exception) {{ }} }} After();";
        var run = Analyze(Worker(body, helper));

        // A group's task is another task than the pass's own, so Wait() on it is no join of the pass (R2).
        var paired = cast == Cast.Downcast || form == Waited.WhenAllWait && side == Side.NextPass;
        Assert.Equal(paired, Pairs(run, "First", side == Side.After ? "After" : "First"));
    }

    public static TheoryData<Cast, Waited, Placement, Side> CastCases()
    {
        var cases = new TheoryData<Cast, Waited, Placement, Side>();
        foreach (var cast in Enum.GetValues<Cast>())
        {
            foreach (var form in Enum.GetValues<Waited>())
            {
                foreach (var placement in new[] { Placement.Direct, Placement.Helper })
                {
                    // An async helper's own fault is its task's, which the await in the try then throws: another question.
                    if (form == Waited.Await && placement == Placement.Helper)
                        continue;
                    foreach (var side in Enum.GetValues<Side>())
                        cases.Add(cast, form, placement, side);
                }
            }
        }

        return cases;
    }

    /// <summary>The other operations that may throw before a wait keep their path through the handler: a user-defined conversion the
    /// program declares, <c>ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)</c> on a <c>Task&lt;T&gt;</c>, which throws
    /// <c>ArgumentOutOfRangeException</c>, a <c>Task.WhenAll</c> handed a collection it does not list, and a store into the listed
    /// array after its initializer — out of its range, or of a <c>Task</c> into an array of <c>Task&lt;string&gt;</c>; an array held in
    /// a local is no listing the call makes, so its stores are never the wait's. The stores of the initializer of an array made for the
    /// call, converting each task to <c>Task</c>, cannot throw and keep the work ordered.</summary>
    /// <param name="shape">A short name of the case, which tells the cases apart and names a failing one.</param>
    /// <param name="inTry">What the <c>try</c> runs on the pass's task <c>t</c>.</param>
    /// <param name="paired">Whether the pairs are kept.</param>
    [Theory]
    [InlineData("user-defined", "((Task<int>)new Wrap(t)).Wait();", true)]
    [InlineData("suppress-throwing", "t.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();", true)]
    [InlineData("when-all-unlisted", "Task.WhenAll(new System.Collections.Generic.List<Task> { t }).Wait();", true)]
    [InlineData("store-out-of-range", "var all = new Task[] { t }; all[1] = t; Task.WaitAll(all);", true)]
    [InlineData("store-covariant", "Task[] all = new Task<string>[1]; all[0] = t; Task.WaitAll(all);", true)]
    [InlineData("initializer", "Task.WaitAll(new Task[] { t });", false)]
    [InlineData("configure-await-false", "t.ConfigureAwait(false).GetAwaiter().GetResult();", false)]
    public void Another_operation_that_may_throw_before_the_wait_keeps_the_pair(string shape, string inTry, bool paired)
    {
        const string WRAP = """
            private sealed class Wrap(Task<int> task)
            {
                public static explicit operator Task<int>(Wrap wrap)
                {
                    if (Environment.TickCount % 2 == 0)
                        throw new InvalidOperationException();
                    return wrap._task;
                }

                private readonly Task<int> _task = task;
            }

            """;
        var start = "var t = Task.Run(() => { First(); return 1; });";
        var after = Analyze(Worker($"{start} try {{ {inTry} }} catch (Exception) {{ }} After();", WRAP));
        var next = Analyze(Worker($"for (var pass = 0; pass < 2; pass++) {{ {start} try {{ {inTry} }} catch (Exception) {{ }} }} After();", WRAP));

        Assert.True(paired == Pairs(after, "First", "After"), $"{shape}: expected the pair after the catch {(paired ? "kept" : "removed")}.");
        Assert.True(paired == Pairs(next, "First", "First"), $"{shape}: expected the next pass's pair {(paired ? "kept" : "removed")}.");
    }

    public enum Listed { Array, CovariantCast, Downcast, RoundTrip, Collection, Params, SpanCast }

    public enum Group { WaitAll, WhenAllWait, WhenAllResult, WhenAllGetResult, WhenAllConfiguredGetResult, WhenAllAwait }

    /// <summary>What gives the listing a <c>Task.WaitAll</c> or a <c>Task.WhenAll</c> is handed is part of the wait only when it cannot
    /// throw, as for a handle: the array created for the call, its implicit covariant conversion, a collection expression, the
    /// <c>params</c> listing and the implicit conversion of any of them to the <c>ReadOnlySpan&lt;Task&gt;</c> the call takes leave the
    /// wait's own fault after the work completed. An explicit conversion of the listing may throw before the wait — the downcast to
    /// <c>Task&lt;string&gt;[]</c> throws <c>InvalidCastException</c> with the work still running — so its path through the handler keeps
    /// the pair, in every form of blocking wait, directly and in a helper; so does a cast to <c>ReadOnlySpan&lt;Task&gt;</c>, an explicit
    /// conversion recorded as no kind that is proven not to throw. A group's task is another task than the pass's own, so a wait on it
    /// keeps the next pass's pair (R2); an <c>await</c> answers as it always has — the <c>Task.WhenAll</c> call it waits on, no blocking
    /// wait, may throw before it.</summary>
    /// <param name="listed">What gives the listing.</param>
    /// <param name="form">How the listing is waited for.</param>
    /// <param name="placement">Whether the wait stands in the <c>try</c> or in a helper called there.</param>
    /// <param name="side">Whether the pair is with the access after the <c>catch</c> or with the next pass's work.</param>
    [Theory]
    [MemberData(nameof(ListingCases))]
    public void A_listing_that_may_throw_before_the_wait_keeps_the_pair(Listed listed, Group form, Placement placement, Side side)
    {
        var listing = listed switch
        {
            Listed.Array => "new Task<int>[] { t }",
            Listed.CovariantCast => "(Task[])new Task<int>[] { t }",
            Listed.Downcast => "(Task<string>[])(object)new Task<int>[] { t }",
            Listed.RoundTrip => "(Task<int>[])(object)new Task<int>[] { t }",
            Listed.Collection => "[t]",
            Listed.Params => "t",
            _ => "(ReadOnlySpan<Task>)new Task[] { t }"
        };
        var wait = form switch
        {
            Group.WaitAll => $"Task.WaitAll({listing});",
            Group.WhenAllWait => $"Task.WhenAll({listing}).Wait();",
            Group.WhenAllResult => $"_ = Task.WhenAll({listing}).Result;",
            Group.WhenAllGetResult => $"Task.WhenAll({listing}).GetAwaiter().GetResult();",
            Group.WhenAllConfiguredGetResult => $"Task.WhenAll({listing}).ConfigureAwait(false).GetAwaiter().GetResult();",
            _ => $"await Task.WhenAll({listing});"
        };
        var helper = placement == Placement.Helper ? $"private static void Waiting(Task<int> t) {{ {wait} }}" : "";
        var inTry = placement == Placement.Helper ? "Waiting(t);" : wait;
        var start = "var t = Task.Run(() => { First(); return 1; });";
        var body = side == Side.After
            ? $"{start} try {{ {inTry} }} catch (Exception) {{ }} After();"
            : $"for (var pass = 0; pass < 2; pass++) {{ {start} try {{ {inTry} }} catch (Exception) {{ }} }} After();";
        var run = Analyze(Worker(body, helper));

        var paired = listed is Listed.Downcast or Listed.RoundTrip or Listed.SpanCast || form == Group.WhenAllAwait ||
                     form != Group.WaitAll && side == Side.NextPass;
        Assert.Equal(paired, Pairs(run, "First", side == Side.After ? "After" : "First"));
    }

    /// <summary>The other operations that may throw on the way to a group's wait keep their path through the handler, as they do for a
    /// handle: <c>ConfigureAwait(ConfigureAwaitOptions)</c> on the group's task, a listing held in a field or returned by a program
    /// call, which no call makes for itself, and a listed task read out of an array.</summary>
    /// <param name="shape">A short name of the case, which tells the cases apart and names a failing one.</param>
    /// <param name="inTry">What the <c>try</c> runs on the pass's task <c>t</c>.</param>
    [Theory]
    [InlineData("group-options", "Task.WhenAll(new Task<int>[] { t }).ConfigureAwait(ConfigureAwaitOptions.None).GetAwaiter().GetResult();")]
    [InlineData("listing-field", "_all = new Task<int>[] { t }; Task.WhenAll(_all).Wait();")]
    [InlineData("listing-call", "Task.WhenAll(Tasks(t)).Wait();")]
    [InlineData("listing-call-wait-all", "Task.WaitAll(Tasks(t));")]
    [InlineData("element-read", "var all = new[] { t }; Task.WaitAll(new Task[] { all[0] });")]
    public void Another_operation_giving_a_group_or_its_listing_keeps_the_pair(string shape, string inTry)
    {
        const string MEMBERS = """
            private Task<int>[] _all = [];
            private static Task<int>[] Tasks(Task<int> t) => new[] { t };
            """;
        var start = "var t = Task.Run(() => { First(); return 1; });";
        var after = Analyze(Worker($"{start} try {{ {inTry} }} catch (Exception) {{ }} After();", MEMBERS));
        var next = Analyze(Worker($"for (var pass = 0; pass < 2; pass++) {{ {start} try {{ {inTry} }} catch (Exception) {{ }} }} After();", MEMBERS));

        Assert.True(Pairs(after, "First", "After"), $"{shape}: expected the pair after the catch kept.");
        Assert.True(Pairs(next, "First", "First"), $"{shape}: expected the next pass's pair kept.");
    }

    public static TheoryData<Listed, Group, Placement, Side> ListingCases()
    {
        var cases = new TheoryData<Listed, Group, Placement, Side>();
        foreach (var listed in Enum.GetValues<Listed>())
        {
            foreach (var form in Enum.GetValues<Group>())
            {
                // A group of tasks that are not Task<T> has no result.
                if (form == Group.WhenAllResult && listed is Listed.CovariantCast or Listed.SpanCast)
                    continue;
                foreach (var placement in new[] { Placement.Direct, Placement.Helper })
                {
                    // An async helper's own fault is its task's, which the await in the try then throws: another question.
                    if (form == Group.WhenAllAwait && placement == Placement.Helper)
                        continue;
                    foreach (var side in Enum.GetValues<Side>())
                        cases.Add(listed, form, placement, side);
                }
            }
        }

        return cases;
    }

    /// <summary>A pass that waits for its own work inside a <c>try</c> whose handler leads to the next pass waits on every path to it
    /// when the only exception before the next pass is the wait's own, which comes after the work completed (R1, R3) — directly, or
    /// through a helper one or two calls up whose only exceptional exit is its wait's, in every form of wait for a task. The pair stays
    /// where an exception may come first: a helper computing the handle through a program call before its wait, such a call inside the
    /// <c>try</c>, a wait with a timeout, a task on a scheduler the program names, or a context the process scope may install. A
    /// <c>Wait()</c> on a <c>Task.WhenAll</c> group is no join of the pass's own handle — the group's task is another task — so it keeps
    /// the pair with or without a <c>try</c>, here as in a loop that only waits.</summary>
    /// <param name="wait">The wait of the pass on its task <c>t</c>.</param>
    /// <param name="paired">Whether the work of one pass pairs with that of the next.</param>
    /// <param name="start">How the pass starts its task, or null for <c>Task.Run</c>.</param>
    /// <param name="before">What the worker runs before its loop.</param>
    [Theory]
    [InlineData("t.Wait();", false)]
    [InlineData("Plain(t);", false)]
    [InlineData("Twice(t);", false)]
    [InlineData("Result(t);", false)]
    [InlineData("Get(t);", false)]
    [InlineData("All(t);", false)]
    [InlineData("WhenAllWait(t);", true)]
    [InlineData("Task.WhenAll(t).Wait();", true)]
    [InlineData("Early(t);", true)]
    [InlineData("Task.WaitAll(Forward(t));", true)]
    [InlineData("Timeout(t);", true)]
    [InlineData("Plain(t);", true, "Task.Factory.StartNew(() => { First(); return 1; }, CancellationToken.None, TaskCreationOptions.None, Schedulers.Own)")]
    [InlineData("Plain(t);", true, null, "if (Environment.ProcessorCount < 0) Install();")]
    public void A_pass_waiting_its_work_in_a_try_has_no_self_pair(string wait, bool paired, string? start = null, string before = "")
    {
        var task = start ?? "Task.Run(() => { First(); return 1; })";
        var run = Analyze(SCHEDULER + Worker($"{before} for (var pass = 0; pass < 2; pass++) {{ var t = {task}; try {{ {wait} }} catch (Exception) {{ }} }} After();",
                                             FORWARD + PASS_HELPERS));

        Assert.Equal(paired, Pairs(run, "First", "First"));
    }

    /// <summary>A pass waiting for its own work through a helper inside a <c>try</c> orders the work before the parent's access after
    /// the <c>catch</c> and before its access in the next pass ahead of the start (R1, R3); a helper that may throw before its wait
    /// keeps both pairs.</summary>
    /// <param name="helper">The helper the pass waits through.</param>
    /// <param name="paired">Whether the work pairs with the parent's accesses.</param>
    [Theory]
    [InlineData("Plain", false)]
    [InlineData("Early", true)]
    public void A_pass_waiting_its_work_through_a_helper_in_a_try_orders_the_parent_after_and_next(string helper, bool paired)
    {
        var run = Analyze(Worker($"for (var pass = 0; pass < 2; pass++) {{ Second(); var t = Task.Run(() => {{ First(); return 1; }}); try {{ {helper}(t); }} catch (Exception) {{ }} After(); }}",
                                 FORWARD + PASS_HELPERS));

        Assert.Equal(paired, Pairs(run, "First", "After"));
        Assert.Equal(paired, Pairs(run, "First", "Second"));
    }

    /// <summary>The requests of an HTTP action overlap one another, so its passes are no passes one after another, however each waits
    /// (R2).</summary>
    [Fact]
    public void A_pass_of_an_http_action_waiting_its_work_through_a_helper_in_a_try_keeps_the_self_pair()
    {
        var run = Analyze(Startup() + $$"""

            public sealed class LoopController : ControllerBase
            {
                public static int Value;
                private void First() { Value = 1; }
                {{FORWARD}}
                {{PASS_HELPERS}}
                public async Task Post(CancellationToken stoppingToken)
                {
                    for (var pass = 0; pass < 2; pass++) { var t = Task.Run(() => { First(); return 1; }); try { Plain(t); } catch (Exception) { } }
                    await Task.CompletedTask;
                }
            }
            """);

        Assert.True(Pairs(run, "First", "First"));
    }

    public enum Leave { Throw, Helper, None, EarlyReturn, CaughtThenJoin }

    public enum InnerJoin { Wait, Result, GetResult, WaitAll, Await }

    /// <summary>The fault of a blocking wait for a task comes after that task completed, not after the work the task started: a task
    /// that may leave with an exception before it joins a child — by a <c>throw</c> of its own or a helper's — leaves the child running,
    /// so the child's write keeps its pair with the write after the waiter's <c>catch</c>, in every form of the waiter's wait and of the
    /// task's join, one level of nesting deeper too. Without the exception before the join, with a handler inside the task that catches
    /// it and then joins, and on the waiter's normal return the child is ordered; an early return skips the join on a normal path, which
    /// keeps the pair as it always has.</summary>
    /// <param name="waiter">How the waiter waits for the outer task.</param>
    /// <param name="leave">How the outer task may leave before its join of the child.</param>
    /// <param name="join">How the outer task joins the child.</param>
    /// <param name="paired">Whether the child's write pairs with the write after the waiter's <c>catch</c>.</param>
    [Theory]
    [InlineData(Fed.Wait, Leave.Throw, InnerJoin.Wait, true)]
    [InlineData(Fed.Result, Leave.Throw, InnerJoin.Wait, true)]
    [InlineData(Fed.GetResult, Leave.Throw, InnerJoin.Wait, true)]
    [InlineData(Fed.ConfiguredGetResult, Leave.Throw, InnerJoin.Wait, true)]
    [InlineData(Fed.WaitAllParams, Leave.Throw, InnerJoin.Wait, true)]
    [InlineData(Fed.WaitAllArray, Leave.Throw, InnerJoin.Wait, true)]
    [InlineData(Fed.WhenAllWait, Leave.Throw, InnerJoin.Wait, true)]
    [InlineData(Fed.Wait, Leave.Helper, InnerJoin.Wait, true)]
    [InlineData(Fed.Result, Leave.Helper, InnerJoin.Wait, true)]
    [InlineData(Fed.GetResult, Leave.Helper, InnerJoin.Wait, true)]
    [InlineData(Fed.ConfiguredGetResult, Leave.Helper, InnerJoin.Wait, true)]
    [InlineData(Fed.WaitAllParams, Leave.Helper, InnerJoin.Wait, true)]
    [InlineData(Fed.WhenAllWait, Leave.Helper, InnerJoin.Wait, true)]
    [InlineData(Fed.Wait, Leave.Throw, InnerJoin.Result, true)]
    [InlineData(Fed.Wait, Leave.Throw, InnerJoin.GetResult, true)]
    [InlineData(Fed.Wait, Leave.Throw, InnerJoin.WaitAll, true)]
    [InlineData(Fed.Wait, Leave.Throw, InnerJoin.Await, true)]
    [InlineData(Fed.Result, Leave.Helper, InnerJoin.Await, true)]
    [InlineData(Fed.Wait, Leave.None, InnerJoin.Wait, false)]
    [InlineData(Fed.Result, Leave.None, InnerJoin.Wait, false)]
    [InlineData(Fed.GetResult, Leave.None, InnerJoin.Result, false)]
    [InlineData(Fed.WaitAllParams, Leave.None, InnerJoin.WaitAll, false)]
    [InlineData(Fed.Wait, Leave.CaughtThenJoin, InnerJoin.Wait, false)]
    [InlineData(Fed.Result, Leave.CaughtThenJoin, InnerJoin.GetResult, false)]
    [InlineData(Fed.Wait, Leave.EarlyReturn, InnerJoin.Wait, true)]
    public void A_task_leaving_before_its_child_join_keeps_the_child_pair_past_the_catch(Fed waiter, Leave leave, InnerJoin join, bool paired)
    {
        var run = Analyze(Worker($"var outer = {Nested(leave, join)}; try {{ {FedWait(waiter, "outer")} }} catch (Exception) {{ }} After();", FAIL));

        Assert.Equal(paired, Pairs(run, "First", "After"));
    }

    /// <summary>The waiter's normal return follows the outer task's normal return, which joined the child, so a throw before the join
    /// orders the child before the write after an uncaught wait as it always has.</summary>
    /// <param name="leave">How the outer task may leave before its join of the child.</param>
    [Theory]
    [InlineData(Leave.Throw)]
    [InlineData(Leave.Helper)]
    [InlineData(Leave.None)]
    public void A_task_leaving_before_its_child_join_orders_the_child_before_an_uncaught_wait(Leave leave)
    {
        var run = Analyze(Worker($"var outer = {Nested(leave, InnerJoin.Wait)}; outer.Wait(); After();", FAIL));

        Assert.False(Pairs(run, "First", "After"));
    }

    /// <summary>Two levels deep, the middle task's exception before it joins the child faults the outer task's join of it, which then
    /// leaves the child running past the waiter's <c>catch</c>; without it the child is ordered.</summary>
    /// <param name="leave">How the middle task may leave before its join of the child.</param>
    /// <param name="paired">Whether the child's write pairs with the write after the waiter's <c>catch</c>.</param>
    [Theory]
    [InlineData(Leave.Throw, true)]
    [InlineData(Leave.Helper, true)]
    [InlineData(Leave.None, false)]
    public void A_middle_task_leaving_before_its_child_join_keeps_the_child_pair_past_the_catch(Leave leave, bool paired)
    {
        var middle = Nested(leave, InnerJoin.Wait);
        var run = Analyze(Worker($"var outer = Task.Run(() => {{ var middle = {middle}; middle.Wait(); return 1; }}); try {{ outer.Wait(); }} catch (Exception) {{ }} After();",
                                 FAIL));

        Assert.Equal(paired, Pairs(run, "First", "After"));
    }

    /// <summary>A pass whose task may leave before it joins its child leaves the child running into the next pass through the handler;
    /// without the exception before the join the child is the pass's own completed work.</summary>
    /// <param name="leave">How the pass's task may leave before its join of the child.</param>
    /// <param name="paired">Whether the child of one pass pairs with that of the next.</param>
    [Theory]
    [InlineData(Leave.Throw, true)]
    [InlineData(Leave.Helper, true)]
    [InlineData(Leave.None, false)]
    public void A_pass_task_leaving_before_its_child_join_keeps_the_next_pass_pair(Leave leave, bool paired)
    {
        var run = Analyze(Worker($"for (var pass = 0; pass < 2; pass++) {{ var outer = {Nested(leave, InnerJoin.Wait)}; try {{ outer.Wait(); }} catch (Exception) {{ }} }} After();",
                                 FAIL));

        Assert.Equal(paired, Pairs(run, "First", "First"));
    }

    /// <summary>Two levels below a pass, the middle task's exception before it joins the child faults the pass's task through its join
    /// of the middle one, so the child runs on into the next pass and past the loop through the handler; without it the child completes
    /// with the pass.</summary>
    /// <param name="leave">How the middle task may leave before its join of the child.</param>
    /// <param name="paired">Whether the child pairs with the child of the next pass and with the write after the loop.</param>
    [Theory]
    [InlineData(Leave.Throw, true)]
    [InlineData(Leave.Helper, true)]
    [InlineData(Leave.None, false)]
    public void A_pass_middle_task_leaving_before_its_child_join_keeps_the_next_pass_pair(Leave leave, bool paired)
    {
        var middle = Nested(leave, InnerJoin.Wait);
        var run = Analyze(Worker($"for (var pass = 0; pass < 2; pass++) {{ var outer = Task.Run(() => {{ var middle = {middle}; middle.Wait(); return 1; }}); " +
                                 "try { outer.Wait(); } catch (Exception) { } } After();", FAIL));

        Assert.Equal(paired, Pairs(run, "First", "First"));
        Assert.Equal(paired, Pairs(run, "First", "After"));
    }

    /// <summary>The requests of an HTTP action overlap one another, so the child keeps its pair with the action's write after the
    /// <c>catch</c> whether or not the outer task may leave before joining it (R2).</summary>
    /// <param name="leave">How the outer task may leave before its join of the child.</param>
    [Theory]
    [InlineData(Leave.Throw)]
    [InlineData(Leave.None)]
    public void A_task_of_an_http_action_leaving_before_its_child_join_keeps_the_child_pair(Leave leave)
    {
        var run = Analyze(Startup() + $$"""

            public sealed class LoopController : ControllerBase
            {
                public static int Value;
                private void First() { Value = 1; }
                private void After() { Value = 3; }
                {{FAIL}}
                public async Task Post(CancellationToken stoppingToken)
                {
                    var outer = {{Nested(leave, InnerJoin.Wait)}}; try { outer.Wait(); } catch (Exception) { } After();
                    await Task.CompletedTask;
                }
            }
            """);

        Assert.True(Pairs(run, "First", "After"));
    }

    /// <summary>An <c>await</c> of the outer task reads its fault as it always has: after the task completed, with what the task's end
    /// carries; the blocking forms' exception does not decide it.</summary>
    /// <param name="leave">How the outer task may leave before its join of the child.</param>
    /// <param name="paired">Whether the child's write pairs with the write after the <c>catch</c>.</param>
    [Theory]
    [InlineData(Leave.Throw, false)]
    [InlineData(Leave.None, false)]
    public void An_await_of_a_task_leaving_before_its_child_join_reads_as_before(Leave leave, bool paired)
    {
        var run = Analyze(Worker($"var outer = {Nested(leave, InnerJoin.Wait)}; try {{ await outer; }} catch (Exception) {{ }} After();", FAIL));

        Assert.Equal(paired, Pairs(run, "First", "After"));
    }

    /// <summary>A <c>Task.Run</c> whose task starts a child writing in <c>First</c>, may leave as given, then joins the child as given and
    /// completes with a value.</summary>
    /// <param name="leave">How the task may leave before its join of the child.</param>
    /// <param name="join">How the task joins the child.</param>
    private static string Nested(Leave leave, InnerJoin join)
    {
        var before = leave switch
        {
            Leave.Throw => "if (Environment.TickCount % 2 == 0) throw new InvalidOperationException();",
            Leave.Helper => "Fail();",
            Leave.EarlyReturn => "if (Environment.TickCount % 2 == 0) return 0;",
            Leave.CaughtThenJoin => "try { Fail(); } catch (Exception) { }",
            _ => ""
        };
        var wait = join switch
        {
            InnerJoin.Wait => "inner.Wait();",
            InnerJoin.Result => "_ = inner.Result;",
            InnerJoin.GetResult => "inner.GetAwaiter().GetResult();",
            InnerJoin.WaitAll => "Task.WaitAll(inner);",
            _ => "await inner;"
        };
        var async = join == InnerJoin.Await ? "async " : "";
        return $"Task.Run({async}() => {{ var inner = Task.Run(() => {{ First(); return 1; }}); {before} {wait} return 1; }})";
    }

    /// <summary>A program call that may throw.</summary>
    private const string FAIL = """
        private static void Fail()
        {
            if (Environment.TickCount % 2 == 0)
                throw new InvalidOperationException();
        }

        """;

    /// <summary>The helpers a pass waits for its task through: in each form of wait, one call further up, after computing the handle
    /// through a program call, with a timeout; and a call installing a context.</summary>
    private const string PASS_HELPERS = """
        private static void Plain(Task<int> t) { t.Wait(); }
        private static void Twice(Task<int> t) { Plain(t); }
        private static void Result(Task<int> t) { _ = t.Result; }
        private static void Get(Task<int> t) { t.GetAwaiter().GetResult(); }
        private static void All(Task<int> t) { Task.WaitAll(t); }
        private static void WhenAllWait(Task<int> t) { Task.WhenAll(t).Wait(); }
        private static void Early(Task<int> t) { Forward(t).Wait(); }
        private static void Timeout(Task<int> t) { t.Wait(100); }
        private static void Install() => SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        """;

    /// <summary>The wait of a form on a forwarded task.</summary>
    /// <param name="form">The form.</param>
    /// <param name="handle">The expression of the task.</param>
    private static string FedWait(Fed form, string handle) => form switch
    {
        Fed.WaitAllParams => $"Task.WaitAll({handle});",
        Fed.WaitAllArray => $"Task.WaitAll(new[] {{ {handle} }});",
        Fed.Wait => $"{handle}.Wait();",
        Fed.Result => $"_ = {handle}.Result;",
        Fed.GetResult => $"{handle}.GetAwaiter().GetResult();",
        Fed.ConfiguredGetResult => $"{handle}.ConfigureAwait(false).GetAwaiter().GetResult();",
        Fed.WhenAllWait => $"Task.WhenAll({handle}).Wait();",
        _ => $"await {handle};"
    };

    /// <summary>A program call that hands back the task it is given, or throws first.</summary>
    private const string FORWARD = """
        private static Task<int> Forward(Task<int> task)
        {
            if (Environment.TickCount % 2 == 0)
                throw new InvalidOperationException();
            return task;
        }

        """;

    public enum Factory { TaskFactory, TaskFactoryOfInt }

    public enum Scheduler { None, Default, Current, Own }

    /// <summary>A <c>StartNew</c> on a separate factory obeys D-0023: an explicit scheduler argument decides by itself whatever the
    /// factory is; without one, the task runs on the program's scheduler where the scope constructs a factory on a scheduler other than
    /// <c>TaskScheduler.Default</c> — this one or another — and otherwise on the thread's, which the thread-context assumption makes the
    /// default (R4(b)).</summary>
    /// <param name="factory">The factory type.</param>
    /// <param name="constructed">The scheduler the factory is constructed with; <see cref="Scheduler.Current"/> does not occur.</param>
    /// <param name="argument">The scheduler argument of the <c>StartNew</c>.</param>
    /// <param name="another">Whether the scope reaches another construction of a factory on the program's scheduler.</param>
    [Theory]
    [MemberData(nameof(FactoryCases))]
    public void Start_new_on_a_factory_obeys_the_scheduler_rule(Factory factory, Scheduler constructed, Scheduler argument, bool another)
    {
        var type = factory == Factory.TaskFactory ? "TaskFactory" : "TaskFactory<int>";
        var work = factory == Factory.TaskFactory ? "() => { First(); }" : "() => { First(); return 1; }";
        var create = constructed switch
        {
            Scheduler.None => $"new {type}()",
            Scheduler.Default => $"new {type}(TaskScheduler.Default)",
            _ => $"new {type}(Schedulers.Own)"
        };
        var start = argument switch
        {
            Scheduler.None => $"factory.StartNew({work})",
            Scheduler.Default => $"factory.StartNew({work}, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default)",
            Scheduler.Current => $"factory.StartNew({work}, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current)",
            _ => $"factory.StartNew({work}, CancellationToken.None, TaskCreationOptions.None, Schedulers.Own)"
        };
        var other = another ? "_ = new TaskFactory(Schedulers.Own); " : "";
        var run = Analyze(SCHEDULER + Worker($"{other}var factory = {create}; var t = {start}; try {{ t.Wait(); }} catch (AggregateException) {{ }} After();"));

        var program = argument switch
        {
            Scheduler.Default => false,
            Scheduler.None => constructed == Scheduler.Own || another,
            _ => true
        };
        Assert.Equal(program, Pairs(run, "First", "After"));
    }

    public static TheoryData<Factory, Scheduler, Scheduler, bool> FactoryCases()
    {
        var cases = new TheoryData<Factory, Scheduler, Scheduler, bool>();
        foreach (var factory in Enum.GetValues<Factory>())
        {
            foreach (var constructed in new[] { Scheduler.None, Scheduler.Default, Scheduler.Own })
            {
                foreach (var argument in Enum.GetValues<Scheduler>())
                {
                    cases.Add(factory, constructed, argument, false);
                    cases.Add(factory, constructed, argument, true);
                }
            }
        }

        return cases;
    }

    public enum Read { Direct, Cast, CastThroughObject, Current, New }

    public enum Named { TaskFactory, TaskFactoryOfInt, StartNewArgument, ContinueWithArgument }

    /// <summary>A conversion of a scheduler leaves it the scheduler it was: a read of <c>TaskScheduler.Default</c> cast to
    /// <c>TaskScheduler</c>, directly or through <c>object</c>, is the default scheduler wherever it is named — handed to either factory
    /// constructor, whose <c>StartNew</c> without a scheduler then runs on the thread's scheduler, or to <c>StartNew</c> or
    /// <c>ContinueWith</c> — and the wait's fault orders the work before the write after the <c>catch</c> as the plain read does (R4(b)).
    /// <c>TaskScheduler.Current</c> and a scheduler the program makes stay the program's.</summary>
    /// <param name="named">Where the scheduler is named.</param>
    /// <param name="read">How the scheduler is read.</param>
    [Theory]
    [MemberData(nameof(ReadCases))]
    public void A_cast_of_the_default_scheduler_names_the_default_scheduler(Named named, Read read)
    {
        var scheduler = read switch
        {
            Read.Direct => "TaskScheduler.Default",
            Read.Cast => "(TaskScheduler)TaskScheduler.Default",
            Read.CastThroughObject => "(TaskScheduler)(object)TaskScheduler.Default",
            Read.Current => "TaskScheduler.Current",
            _ => "Schedulers.Own"
        };
        var start = named switch
        {
            Named.TaskFactory => $"var factory = new TaskFactory({scheduler}); var t = factory.StartNew(() => {{ First(); }});",
            Named.TaskFactoryOfInt => $"var factory = new TaskFactory<int>({scheduler}); var t = factory.StartNew(() => {{ First(); return 1; }});",
            Named.StartNewArgument => $"var t = Task.Factory.StartNew(() => {{ First(); }}, CancellationToken.None, TaskCreationOptions.None, {scheduler});",
            _ => $"var p = Task.Run(() => 0); var t = p.ContinueWith(_ => {{ First(); }}, CancellationToken.None, TaskContinuationOptions.None, {scheduler});"
        };
        var run = Analyze(SCHEDULER + Worker($"{start} try {{ t.Wait(); }} catch (AggregateException) {{ }} After();"));

        Assert.Equal(read is Read.Current or Read.New, Pairs(run, "First", "After"));
    }

    public static TheoryData<Named, Read> ReadCases()
    {
        var cases = new TheoryData<Named, Read>();
        foreach (var named in Enum.GetValues<Named>())
        {
            foreach (var read in Enum.GetValues<Read>())
                cases.Add(named, read);
        }

        return cases;
    }

    public enum Waits { One, FirstFaults, SecondFaults, WaitAll }

    public enum Placement { Direct, Helper, HelperOfHelper, Nested, Pass, PassHelper }

    public enum Blocking { Wait, Result, GetResult, ConfiguredGetResult, WhenAllWait }

    /// <summary>The exception of one wait comes after that wait's own tasks completed, and only theirs (R2, R3): the path it opens through
    /// the handler has waited for what that wait and the waits before it waited for, never for a later wait it skips. Two tasks <c>a</c>
    /// (writing in <c>First</c>) and <c>b</c> (in <c>Second</c>) are waited one after the other — either of them may fault —, together in
    /// one <c>Task.WaitAll</c> or <c>Task.WhenAll</c>, or only <c>a</c> is; directly in the <c>try</c>, through a helper one or two calls
    /// up, inside a task the worker waits for in the <c>try</c>, or in a pass of a loop; in every blocking form, with or without a program
    /// call that may throw before the first wait. Past the <c>catch</c>, the first wait's work is ordered and the skipped one's pairs; on
    /// the next pass, the first wait's work has no self-pair and the skipped one's keeps it. Where the answer is wider than that, the
    /// reason is what bounds it, not the skipped wait: a task that may leave while its work runs leaves it running (a spawn stands between
    /// <c>a</c>'s start and its join inside the nested task), a <c>WhenAll</c> group's task is no pass's own handle, a <c>Task.WaitAll</c>
    /// of two tasks is no join on one handle, and a helper's call site carries one exceptional exit for every task it waits for, so a
    /// helper waiting for two groups orders neither past the <c>catch</c>; a per-task exit of one call site is not built.</summary>
    /// <param name="waits">What is waited for, and which task may fault.</param>
    /// <param name="placement">Where the waits stand.</param>
    /// <param name="form">The form of each wait.</param>
    /// <param name="early">Whether a program call that may throw comes before the first wait.</param>
    [Theory]
    [MemberData(nameof(SkippedCases))]
    public void A_wait_skipped_by_an_earlier_waits_exception_keeps_its_pair(Waits waits, Placement placement, Blocking form, bool early)
    {
        var run = Analyze(Worker(Skipped(waits, placement, form, early, out var members), members + FAIL));
        var pass = placement is Placement.Pass or Placement.PassHelper;
        bool[] expected;
        if (pass)
            expected = early || form == Blocking.WhenAllWait || waits == Waits.WaitAll ? [true, true] : [false, true];
        else if (early || placement == Placement.Nested)
            expected = [true, true];
        else if (waits == Waits.WaitAll)
            expected = [false, false];
        else if (waits == Waits.One)
            expected = [false, true];
        else
            expected = [placement != Placement.Direct && form == Blocking.WhenAllWait, true];

        bool[] actual = pass ? [Pairs(run, "First", "First"), Pairs(run, "Second", "Second")]
                             : [Pairs(run, "First", "After"), Pairs(run, "Second", "After")];
        Assert.Equal(expected, actual);
    }

    public static TheoryData<Waits, Placement, Blocking, bool> SkippedCases()
    {
        var cases = new TheoryData<Waits, Placement, Blocking, bool>();
        foreach (var waits in Enum.GetValues<Waits>())
        {
            foreach (var placement in Enum.GetValues<Placement>())
            {
                foreach (var form in Enum.GetValues<Blocking>())
                {
                    cases.Add(waits, placement, form, false);
                    cases.Add(waits, placement, form, true);
                }
            }
        }

        return cases;
    }

    /// <summary>The worker's body of a cell of <see cref="A_wait_skipped_by_an_earlier_waits_exception_keeps_its_pair"/>.</summary>
    /// <param name="waits">What is waited for, and which task may fault.</param>
    /// <param name="placement">Where the waits stand.</param>
    /// <param name="form">The form of each wait.</param>
    /// <param name="early">Whether a program call that may throw comes before the first wait.</param>
    /// <param name="members">Receives the helpers the body calls.</param>
    private static string Skipped(Waits waits, Placement placement, Blocking form, bool early, out string members)
    {
        var tasks = $"var a = Task.Run(() => {{ {(waits == Waits.FirstFaults ? "Fail(); " : "")}First(); return 1; }}); " +
                    $"var b = Task.Run(() => {{ {(waits == Waits.SecondFaults ? "Fail(); " : "")}Second(); return 2; }});";
        var group = form switch
        {
            Blocking.Wait => "Task.WaitAll(a, b);",
            Blocking.Result => "_ = Task.WhenAll(a, b).Result;",
            Blocking.GetResult => "Task.WhenAll(a, b).GetAwaiter().GetResult();",
            Blocking.ConfiguredGetResult => "Task.WhenAll(a, b).ConfigureAwait(false).GetAwaiter().GetResult();",
            _ => "Task.WhenAll(a, b).Wait();"
        };
        var wait = (early ? "Fail(); " : "") + waits switch
        {
            Waits.One => Single(form, "a"),
            Waits.WaitAll => group,
            _ => $"{Single(form, "a")} {Single(form, "b")}"
        };
        members = $$"""
            private static void Both(Task<int> a, Task<int> b) { {{wait}} }
            private static void Outer(Task<int> a, Task<int> b) { Both(a, b); }

            """;
        return placement switch
        {
            Placement.Direct => $"{tasks} try {{ {wait} }} catch (Exception) {{ }} After();",
            Placement.Helper => $"{tasks} try {{ Both(a, b); }} catch (Exception) {{ }} After();",
            Placement.HelperOfHelper => $"{tasks} try {{ Outer(a, b); }} catch (Exception) {{ }} After();",
            Placement.Nested => $"var x = Task.Run(() => {{ {tasks} Both(a, b); return 0; }}); try {{ {Single(form, "x")} }} catch (Exception) {{ }} After();",
            Placement.Pass => $"for (var pass = 0; pass < 2; pass++) {{ {tasks} try {{ {wait} }} catch (Exception) {{ }} }} After();",
            _ => $"for (var pass = 0; pass < 2; pass++) {{ {tasks} try {{ Both(a, b); }} catch (Exception) {{ }} }} After();"
        };

        static string Single(Blocking form, string handle) => form switch
        {
            Blocking.Wait => $"{handle}.Wait();",
            Blocking.Result => $"_ = {handle}.Result;",
            Blocking.GetResult => $"{handle}.GetAwaiter().GetResult();",
            Blocking.ConfiguredGetResult => $"{handle}.ConfigureAwait(false).GetAwaiter().GetResult();",
            _ => $"Task.WhenAll({handle}).Wait();"
        };
    }

    /// <summary>The review's probes as written: a task whose lambda only throws and a task writing in <c>First</c>, waited one after the
    /// other by a helper — the write after the <c>catch</c> pairs with the second task's write, and so does that write of the next pass
    /// when the helper stands in a loop —, and the same two waits inside a task the worker waits for in the <c>try</c>. The same waits
    /// written directly in the <c>try</c> keep the pair too; waited the other way round, the writing task's wait comes first and orders
    /// its write.</summary>
    /// <param name="shape">A short name of the case, which tells the cases apart and names a failing one.</param>
    /// <param name="body">The worker's body.</param>
    /// <param name="second">The method whose write pairs with <c>First</c>'s, or <c>First</c> itself for the next pass.</param>
    /// <param name="paired">Whether the pair is kept.</param>
    [Theory]
    [InlineData("helper", "var a = Task.Run(() => { throw new Exception(); }); var b = Task.Run(() => First()); try { Both(a, b); } catch (Exception) { } After();", "After", true)]
    [InlineData("helper-next-pass", "for (var pass = 0; pass < 2; pass++) { var a = Task.Run(() => { throw new Exception(); }); var b = Task.Run(() => First()); try { Both(a, b); } catch (Exception) { } }", "First", true)]
    [InlineData("nested", "var x = Task.Run(() => { var a = Task.Run(() => { throw new Exception(); }); var b = Task.Run(() => First()); a.Wait(); b.Wait(); }); try { x.Wait(); } catch (Exception) { } After();", "After", true)]
    [InlineData("direct", "var a = Task.Run(() => { throw new Exception(); }); var b = Task.Run(() => First()); try { a.Wait(); b.Wait(); } catch (Exception) { } After();", "After", true)]
    [InlineData("direct-writer-first", "var a = Task.Run(() => { throw new Exception(); }); var b = Task.Run(() => First()); try { b.Wait(); a.Wait(); } catch (Exception) { } After();", "After", false)]
    public void A_helper_waiting_two_tasks_keeps_the_pair_of_the_one_its_first_wait_skips(string shape, string body, string second, bool paired)
    {
        const string BOTH = """
            private static void Both(Task a, Task b) { a.Wait(); b.Wait(); }

            """;
        var run = Analyze(Worker(body, BOTH));

        Assert.True(paired == Pairs(run, "First", second), $"{shape}: expected the pair {(paired ? "kept" : "removed")}.");
    }

    /// <summary>Whether a write of one method and a write of another form a pair.</summary>
    /// <param name="run">The analysed program.</param>
    /// <param name="first">The method writing first.</param>
    /// <param name="second">The method writing second.</param>
    private static bool Pairs(EngineRun run, string first, string second)
    {
        Assert.Contains(run.Of("Value"), access => Is(access.Symbol, first));
        Assert.Contains(run.Of("Value"), access => Is(access.Symbol, second));
        return run.PairsOn("Value").Any(pair => Is(pair.First.Symbol, first) && Is(pair.Second.Symbol, second) ||
                                                Is(pair.First.Symbol, second) && Is(pair.Second.Symbol, first));

        static bool Is(string symbol, string method) => symbol.EndsWith($".{method}()", StringComparison.Ordinal);
    }

    private static string Worker(string body, string members = "") => Startup("services.AddHostedService<Worker>();") + $$"""

        public sealed class Worker : BackgroundService
        {
            public static int Value;
            private void First() { Value = 1; }
            private void Second() { Value = 2; }
            private void After() { Value = 3; }
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
                await Task.CompletedTask;
            }
            {{members}}
        }
        """;
}
