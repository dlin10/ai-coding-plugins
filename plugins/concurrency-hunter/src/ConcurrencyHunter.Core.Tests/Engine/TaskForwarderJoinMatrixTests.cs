using System.Collections.Concurrent;
using System.Text.Json;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class TaskForwarderJoinMatrixTests
{
    public enum Form
    {
        Expression, Block, Local, FirstPassReturn, Chain, Dispatch, Lambda, LocalFunction, ValueTask, AsTask,
        Detached, FaultsBeforeChildJoin, Conditional, Other, LoopCall, TwoTargets, ParameterTask, FieldTask
    }
    public enum Wait { Await, Wait, Result, GetResult, WaitAll, WaitTimeout, WaitToken }
    public enum Place { Once, TwoCalls, Loop, Retry, Record }
    public enum WaitPath { AllPaths, Bypass, ExceptionBeforeWait }
    public enum Parent { Hosted, Http }
    public enum Side { SameWork, ParentBeforeStart, ParentAfterWait }
    public enum Origin { Program, Library }

    public sealed record Cell(Form Form, Wait Wait, Place Place, WaitPath Path, Parent Parent, Side Side, Origin Origin)
    {
        public override string ToString() => $"{Origin}/{Form}/{Wait}/{Place}/{Path}/{Parent}/{Side}";
    }

    private static readonly Dictionary<string, string> Wider = new(StringComparer.Ordinal)
    {
        ["Program/TwoTargets/Await/Once/AllPaths/Hosted/SameWork"] = "The two mutually exclusive delegate targets are read as two spawn sites that can both run.",
        ["Program/TwoTargets/Await/Once/Bypass/Hosted/SameWork"] = "The two mutually exclusive delegate targets are read as two spawn sites that can both run.",
        ["Program/TwoTargets/Await/Once/ExceptionBeforeWait/Hosted/SameWork"] = "The two mutually exclusive delegate targets are read as two spawn sites that can both run."
    };
    private static readonly ConcurrentDictionary<string, Lazy<Observed>> Runs = new(StringComparer.Ordinal);
    private sealed record Observed(bool SameWork, bool ParentBeforeStart, bool ParentAfterWait);

    public static IEnumerable<Cell> AllCells() =>
        from origin in Enum.GetValues<Origin>()
        from form in Enum.GetValues<Form>()
        from wait in Enum.GetValues<Wait>()
        from place in Enum.GetValues<Place>()
        from path in Enum.GetValues<WaitPath>()
        from parent in Enum.GetValues<Parent>()
        from side in Enum.GetValues<Side>()
        let cell = new Cell(form, wait, place, path, parent, side, origin)
        where Allowed(cell)
        select cell;

    public static TheoryData<Form, Wait, Place, WaitPath, Parent, Side, Origin> Cells()
    {
        var result = new TheoryData<Form, Wait, Place, WaitPath, Parent, Side, Origin>();
        foreach (var cell in AllCells())
            result.Add(cell.Form, cell.Wait, cell.Place, cell.Path, cell.Parent, cell.Side, cell.Origin);
        return result;
    }

    [Fact]
    public void There_are_639_cells_covering_every_value_of_every_axis()
    {
        var cells = AllCells().ToArray();
        Assert.Equal(639, cells.Length);
        Assert.Equal(cells.Length, cells.Select(cell => cell.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Enum.GetValues<Form>(), value => Assert.Contains(cells, cell => cell.Form == value));
        Assert.All(Enum.GetValues<Wait>(), value => Assert.Contains(cells, cell => cell.Wait == value));
        Assert.All(Enum.GetValues<Place>(), value => Assert.Contains(cells, cell => cell.Place == value));
        Assert.All(Enum.GetValues<WaitPath>(), value => Assert.Contains(cells, cell => cell.Path == value));
        Assert.All(Enum.GetValues<Parent>(), value => Assert.Contains(cells, cell => cell.Parent == value));
        Assert.All(Enum.GetValues<Side>(), value => Assert.Contains(cells, cell => cell.Side == value));
        Assert.All(Enum.GetValues<Origin>(), value => Assert.Contains(cells, cell => cell.Origin == value));
    }

    [Fact]
    public void Every_wider_cell_names_a_cell_that_expects_no_pair()
    {
        Assert.Equal(3, Wider.Count);
        Assert.All(Enum.GetValues<WaitPath>(), path =>
            Assert.Contains($"Program/TwoTargets/Await/Once/{path}/Hosted/SameWork", Wider.Keys));
        var cells = AllCells().ToDictionary(cell => cell.ToString(), StringComparer.Ordinal);
        Assert.All(Wider, item =>
        {
            Assert.True(cells.TryGetValue(item.Key, out var cell));
            Assert.False(Expected(cell!));
            Assert.False(string.IsNullOrWhiteSpace(item.Value));
        });
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_is_exact_outside_the_wider_list(Form form, Wait wait, Place place, WaitPath path, Parent parent, Side side, Origin origin)
    {
        var cell = new Cell(form, wait, place, path, parent, side, origin);
        var program = Source(cell);
        var observed = Runs.GetOrAdd(program.Source + program.Library, _ => new Lazy<Observed>(() => Observe(program))).Value;
        var pair = side switch
        {
            Side.SameWork => observed.SameWork,
            Side.ParentBeforeStart => observed.ParentBeforeStart,
            _ => observed.ParentAfterWait
        };
        if (Expected(cell))
            Assert.True(pair, $"{cell}: the pair the requirements keep is gone.");
        else if (Wider.ContainsKey(cell.ToString()))
            Assert.True(pair, $"{cell}: listed as wider, but it is exact now; take it off the list.");
        else
            Assert.False(pair, $"{cell}: the pair the requirements remove is still there.");
    }

    private static bool Allowed(Cell cell)
    {
        if (cell.Parent == Parent.Http && (cell.Place != Place.Loop || cell.Side != Side.SameWork))
            return false;
        if (cell.Origin == Origin.Library)
            return cell.Form is Form.Expression or Form.Chain or Form.Dispatch or Form.Lambda or Form.Detached &&
                   cell.Wait == Wait.Await && cell.Path == WaitPath.AllPaths && cell.Parent == Parent.Hosted &&
                   (cell.Place is Place.Once or Place.Loop || cell.Place == Place.Retry && cell.Side == Side.SameWork);
        if (cell.Place == Place.Record)
            return cell.Form is Form.Expression or Form.Chain or Form.Dispatch or Form.Lambda or Form.Conditional or Form.LoopCall &&
                   cell.Wait == Wait.Await && cell.Path == WaitPath.AllPaths && cell.Parent == Parent.Hosted && cell.Side != Side.ParentBeforeStart;
        if (cell.Form is Form.ParameterTask or Form.FieldTask)
            return cell.Wait == Wait.Await && cell.Place == Place.Once && cell.Path == WaitPath.AllPaths && cell.Parent == Parent.Hosted;
        if (cell.Wait == Wait.Await)
            return (cell.Place is Place.Once or Place.Loop || cell.Path == WaitPath.AllPaths) &&
                   (cell.Form != Form.FaultsBeforeChildJoin || cell.Path == WaitPath.AllPaths);
        if (cell.Path != WaitPath.AllPaths || cell.Place is not (Place.Once or Place.Loop))
            return false;
        if (cell.Wait is Wait.WaitTimeout or Wait.WaitToken)
            return cell.Form is Form.Expression or Form.Chain;
        return cell.Form is Form.Expression or Form.Chain or Form.Lambda or Form.Conditional or Form.FaultsBeforeChildJoin;
    }

    private static bool Expected(Cell cell)
    {
        // R2: only a forwarder's own task (or the existing parameter/field proof), waited on every path without a timeout or token,
        // completes the work; detached work and a fault before the child join are left running. HTTP roots overlap themselves.
        var ordered = (cell.Form <= Form.AsTask || cell.Form is Form.ParameterTask or Form.FieldTask) &&
                      cell.Wait is not (Wait.WaitTimeout or Wait.WaitToken) && cell.Path == WaitPath.AllPaths && cell.Parent == Parent.Hosted;
        // R2: one run under Hosted has no second work to pair with, except LoopCall, which starts the work twice in that one run.
        if (cell.Side == Side.SameWork)
            return !ordered && !(cell.Parent == Parent.Hosted && cell.Place == Place.Once && cell.Form != Form.LoopCall);
        // R2: Before precedes both starts in a parent without a loop; a next pass's Before requires the preceding pass's join.
        if (cell.Side == Side.ParentBeforeStart)
            return cell.Place is not (Place.Once or Place.TwoCalls) && !ordered;
        // R2: After follows the work only where the wait proves completion.
        return !ordered;
    }

    private sealed record ProgramText(string Source, string Library, bool Forwarder, Form Form);

    private static ProgramText Source(Cell cell)
    {
        var result = cell.Wait == Wait.Result;
        var task = result ? "Task<int>" : "Task";
        var valueTask = result ? "ValueTask<int>" : "ValueTask";
        var end = result ? "return 1;" : "";
        var completed = result ? "Task.FromResult(0)" : "Task.CompletedTask";
        var library = cell.Origin == Origin.Library;
        var action = library ? "action()" : "WriteAsync()";
        var args = library ? "Func<Task> action" : "";
        var passed = library ? "action" : "";
        var fwd = $"Fwd({passed})";
        var members = $$"""
            {{(library ? "public static" : "private")}} {{task}} Fwd({{args}}) => {{action}};
            """;
        var setup = "";
        var targetDelegate = "Fwd";
        switch (cell.Form)
        {
            case Form.Block: members = $"private {task} Fwd() {{ Idle(); return WriteAsync(); }}"; break;
            case Form.Local: members = $"private {task} Fwd() {{ var t = WriteAsync(); return t; }}"; break;
            case Form.FirstPassReturn: members = $"private {task} Fwd() {{ for (;;) return WriteAsync(); }}"; break;
            case Form.Chain:
                members = $"{(library ? "public static" : "private")} {task} Fwd({args}) => Fwd2({passed}); " +
                          $"{(library ? "private static" : "private")} {task} Fwd2({args}) => {action};";
                break;
            case Form.Dispatch:
                if (library)
                    members = "public abstract class Base { public abstract Task Forward(); } " +
                              "public sealed class Impl(Func<Task> action) : Base { public override Task Forward() => action(); } " +
                              "public static Task Fwd(Func<Task> action) { Base target = new Impl(action); return target.Forward(); }";
                else
                {
                    members = $"public abstract class Base {{ public abstract {task} Forward(); }} " +
                              $"public sealed class Impl(Worker owner) : Base {{ public override {task} Forward() => owner.WriteAsync(); }}";
                    setup = "Base fwd = new Impl(this); "; fwd = "fwd.Forward()"; targetDelegate = "fwd.Forward";
                }
                break;
            case Form.Lambda:
                if (library)
                    members = "public static Task Fwd(Func<Task> action) { Func<Task> fwd = () => action(); return fwd(); }";
                else
                {
                    members = ""; setup = $"Func<{task}> fwd = () => WriteAsync(); "; fwd = "fwd()"; targetDelegate = "fwd";
                }
                break;
            case Form.LocalFunction:
                members = ""; setup = $"{task} Fwd() => WriteAsync(); "; break;
            case Form.ValueTask: members = $"private {valueTask} Fwd() => new {valueTask}(WriteAsync());"; break;
            case Form.AsTask: members = $"private {task} Fwd() => WriteValueAsync().AsTask(); private async {valueTask} WriteValueAsync() {{ await Task.Yield(); Write(); {end} }}"; break;
            case Form.Detached:
                members = $"{(library ? "public static" : "private")} {task} Fwd({args}) => DetachedAsync({passed}); " +
                          $"{(library ? "private static" : "private")} async {task} DetachedAsync({args}) {{ _ = Task.Run({(library ? "action" : "Write")}); await Task.Yield(); {end} }}";
                break;
            case Form.FaultsBeforeChildJoin:
                members = $"private {task} Fwd() => FaultAsync(); private async {task} FaultAsync() {{ var t = Task.Run(Write); if (Flag) throw new InvalidOperationException(); await t; {end} }}";
                break;
            case Form.Conditional: members = $"private {task} Fwd() => Flag ? WriteAsync() : IdleAsync();"; break;
            case Form.Other: members = $"private {task} Fwd() {{ _ = WriteAsync(); return {completed}; }}"; break;
            case Form.LoopCall: members = $"private {task} Fwd() {{ {task} last = {completed}; for (int i = 0; i < 2; i++) last = WriteAsync(); return last; }}"; break;
            case Form.TwoTargets:
                members = $"private {task} Fwd() => WriteAsync(); private {task} Fwd2() => WriteAsync();";
                setup = $"Func<{task}> fwd = Flag ? Fwd : Fwd2; "; fwd = "fwd()"; targetDelegate = "fwd"; break;
            case Form.ParameterTask: members = $"private {task} Pass({task} t) => t;"; fwd = "Pass(WriteAsync())"; break;
            case Form.FieldTask: members = $"private {task}? _known; private {task} Take() => _known!;"; fwd = "Take()"; setup = "_known = WriteAsync(); "; break;
        }
        var wait = cell.Wait switch
        {
            Wait.Await => cell.Form == Form.Chain ? "await own.ConfigureAwait(false);" : "await own;",
            Wait.Wait => "own.Wait();", Wait.Result => "_ = own.Result;",
            Wait.GetResult => "own.GetAwaiter().GetResult();", Wait.WaitAll => "Task.WaitAll(own);",
            Wait.WaitTimeout => "own.Wait(100);", _ => "own.Wait(stoppingToken);"
        };
        var waitPath = cell.Path switch
        {
            WaitPath.AllPaths => wait,
            WaitPath.Bypass => "if (stoppingToken.CanBeCanceled) { " + wait + " }",
            _ => "try { MayThrow(); " + wait + " } catch (InvalidOperationException) { }"
        };
        var startAndWait = "var own = " + fwd + "; " + waitPath;
        var pass = setup + startAndWait;
        if (cell.Form == Form.FaultsBeforeChildJoin)
        {
            startAndWait = "try { " + startAndWait + " } catch (Exception) { }";
            pass = setup + startAndWait;
        }
        var work = "Before(); " + pass + " After();";
        if (cell.Place == Place.TwoCalls)
            work = setup + "Before(); { " + startAndWait + " } { " + startAndWait + " } After();";
        if (cell.Place == Place.Loop)
            work = "for (int pass = 0; pass < 2; pass++) { " + work + " }";
        if (cell.Place == Place.Retry)
        {
            members += $$"""
                {{(library ? "private static" : "private")}} async Task RetryAsync(Func<Task> attempt{{(library ? ", Action Before, Action After" : "")}})
                {
                    for (int pass = 0; pass < 2; pass++)
                    {
                        Before();
                        {{(cell.Form == Form.FaultsBeforeChildJoin ? "try { await attempt(); } catch (Exception) { }" : "await attempt();")}}
                        After();
                    }
                }
                """;
            work = setup + $"await RetryAsync({(library ? "() => " + fwd : cell.Form == Form.ValueTask ? "() => Fwd().AsTask()" : targetDelegate)}{(library ? ", Before, After" : "")});";
        }
        if (cell.Place == Place.Record)
            work = setup + $"Before(); await Runs.Lib.Run({targetDelegate}); After();";
        var libraryText = "";
        if (library)
        {
            libraryText = Usings + "public static class ForwardLibrary { " + members +
                          " public static async Task Entry(Func<Task> action, Action Before, Action After, CancellationToken stoppingToken) { " + work + " } }";
            work = "await ForwardLibrary.Entry(async () => { await Task.Yield(); Write(); }, Before, After, stoppingToken);";
            members = "";
        }
        var common = $$"""
            public static int Value;
            private static bool Flag;
            private void Write() { Value = 1; }
            private void Before() { Value = 2; }
            private void After() { Value = 3; }
            private void Idle() { }
            private async {{task}} WriteAsync() { await Task.Yield(); Write(); {{end}} }
            private async {{task}} IdleAsync() { await Task.Yield(); {{end}} }
            private static void MayThrow() { if (Flag) throw new InvalidOperationException(); }
            """;
        var source = Usings + Startup(cell.Parent == Parent.Hosted ? "services.AddHostedService<Worker>();" : "") +
            (cell.Parent == Parent.Hosted
                ? "public sealed class Worker : BackgroundService { protected override async Task ExecuteAsync(CancellationToken stoppingToken) { "
                : "public sealed class Worker : ControllerBase { [HttpGet] public async Task Get(CancellationToken stoppingToken) { ") +
            work + " } " + common + members + " }";
        return new ProgramText(source, libraryText, cell.Form <= Form.AsTask || cell.Form is Form.Detached or Form.FaultsBeforeChildJoin, cell.Form);
    }

    private static Observed Observe(ProgramText program)
    {
        var librarySource = program.Library.Length == 0
            ? "namespace Runs { public static class Lib { public static System.Threading.Tasks.Task Run(System.Func<System.Threading.Tasks.Task> f) => null!; } }"
            : program.Library;
        var library = ((CSharpCompilation)FixtureSolution.Create(("Library.cs", librarySource)).Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!)
            .WithAssemblyName("Forwarders");
        using var pe = new MemoryStream();
        var emitted = library.Emit(pe);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [MetadataReference.CreateFromImage(pe.ToArray())] }, ("Case.cs", program.Source));
        var compilation = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var table = BuiltInModelReader.Read(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, assemblies = new[] { "Forwarders" }, versions = new { minimum = "0.0.0.0", maximumExclusive = "1.0.0.0" },
            models = new[] { new { member = "M:Runs.Lib.Run(System.Func{System.Threading.Tasks.Task})~System.Threading.Tasks.Task", effects = new { },
                fates = new { f = new Dictionary<string, object> { ["fate"] = "invoke-now", ["runs"] = "sequential-awaited" } } } }
        }));
        var models = new LibraryModels([.. LibraryModels.BuiltIn.Members, .. table.Members], LibraryModels.BuiltIn.ImmutableTypes);
        var run = ScopePipeline.Run("scope:Fixture", [compilation], [(compilation, null)], ROOT_DIRECTORY, ProviderRegistry.BuiltIn, models,
            AnalysisLimits.Default, new(), null, CancellationToken.None,
            reachability: ReachabilityRules.ClassHierarchy with { Admission = program.Library.Length == 0 ? null : new CallSiteAdmission("Forwarders",
                ConcurrencyHunter.Frontend.ProgramIndexBuilder.Build("forwarders", [library], ROOT_DIRECTORY, CancellationToken.None).Methods
                    .Where(method => method.MethodId.Contains(":ForwardLibrary.Entry(", StringComparison.Ordinal)).Select(method => method.MethodId).ToHashSet(StringComparer.Ordinal)) },
            libraryCompilations: program.Library.Length == 0 ? [] : [library]);
        Assert.False(run.Stopped);
        var accesses = run.Collection!.Accesses.Where(access => access.Resource.Member.Name == "Value" && !access.IsConstructionLocal).ToArray();
        Assert.Contains(accesses, access => Is(access.Symbol, "Write"));
        Assert.Contains(accesses, access => Is(access.Symbol, "Before"));
        Assert.Contains(accesses, access => Is(access.Symbol, "After"));
        var pairs = InterproceduralPairing.Pair(run.Collection.Accesses, run.Executions!, run.Heap!).Pairs;
        {
            var spawns = run.Heap!.AsyncSpawns.Where(spawn => spawn.Callees.Any(callee => run.Heap.Instances.TryGetValue(callee, out var target) &&
                run.Reachable.Bodies[target.BodyId].IsAsync)).ToArray();
            // R2 / TD-060a: forwarding adds a proof of join; the async call and its separate tail remain in both layers.
            Assert.NotEmpty(spawns);
            var order = typeof(ExecutionAnalysis).GetField("_order", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(run.Executions);
            if (program.Forwarder)
            {
                var forwarders = run.Heap.Instances.Values.Where(instance => program.Library.Length != 0
                    ? instance.BodyId.StartsWith("body:Forwarders:M:ForwardLibrary.Fwd(", StringComparison.Ordinal)
                    : program.Form switch
                    {
                        Form.Dispatch => instance.BodyId == "body:Fixture:M:Worker.Impl.Forward",
                        Form.Lambda or Form.LocalFunction => instance.BodyId.Contains(")#", StringComparison.Ordinal) &&
                            !run.Reachable.Bodies[instance.BodyId].IsAsync,
                        _ => instance.BodyId == "body:Fixture:M:Worker.Fwd"
                    }).ToArray();
                Assert.NotEmpty(forwarders);
                Assert.All(forwarders, instance => Assert.Contains(run.Executions!.Executions,
                    execution => ((HappensBefore)order!).ForwardedTask(execution.Id, instance.Id) is not null));
            }
            Assert.All(spawns, spawn => Assert.Contains(run.Executions!.Executions, execution => execution.Id.EndsWith($">async:{run.Heap.Instances[spawn.CallerInstance].BodyId}#{spawn.OperationId}", StringComparison.Ordinal) &&
                ((HappensBefore)order!).StartsOf(execution.Id).Any(anchor => anchor.Kind == SpawnAnchorKind.AsyncReturn &&
                    anchor.CallerInstance == spawn.CallerInstance && anchor.OperationId == spawn.OperationId) &&
                run.Executions.Entries.GetValueOrDefault(execution.Id)?.Any(entry => entry.Segment == BodySegment.Tail &&
                    spawn.Callees.Contains(entry.InstanceId)) == true &&
                execution.Origin is { } origin && origin.BodyId == run.Heap.Instances[spawn.CallerInstance].BodyId && origin.OperationId == spawn.OperationId));
        }
        return new Observed(Pairs("Write"), Pairs("Before"), Pairs("After"));

        bool Pairs(string other) => pairs.Any(pair => Is(pair.First.Symbol, "Write") && Is(pair.Second.Symbol, other) ||
                                                    Is(pair.Second.Symbol, "Write") && Is(pair.First.Symbol, other));
        static bool Is(string symbol, string method) => symbol.EndsWith($".{method}()", StringComparison.Ordinal);
    }
}
