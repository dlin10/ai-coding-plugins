using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

public sealed class InitializerWorkStartMatrixTests(ClassCache cache) : IClassFixture<ClassCache>
{
    private enum Work { TaskRun, ThreadCreateOnly, ThreadStart, ParallelFor, TimerCreateDisabled, TimerCreateEnabled, TimerChange,
                        TimersTimerSubscribeOnly, TimersTimerStart, AsyncCall, AsyncCallAwaitedLater, AsyncIteratorCreate, AsyncCallAwaited,
                        TimersTimerEnabledFalse, TimersTimerEnabledUnknown, TimersTimerAutoReset, TimersTimerStop, TimersTimerDispose }
    private enum Site { Initializer, Helper }
    private sealed record Cell(Work Work, Site Site);
    private sealed record Result(GeneratedAnswer Answer, bool RunningStartsWork, string[] Starts);

    private IReadOnlyDictionary<Cell, Result> Results => cache.Get("results", Run);

    private static IEnumerable<Cell> Cells() =>
        (from work in Enum.GetValues<Work>() from site in Enum.GetValues<Site>() select new Cell(work, site))
        .Where(cell => Allowed(cell) is null);

    private static string? Allowed(Cell cell) => cell.Site == Site.Initializer &&
        (cell.Work is Work.AsyncCallAwaitedLater or Work.AsyncCallAwaited || cell.Work >= Work.TimersTimerEnabledFalse)
            ? "A type initializer cannot await and has no timer or flag parameter."
            : null;

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        Assert.Equal(Enum.GetValues<Work>(), Cells().Select(cell => cell.Work).Distinct().Order());
        Assert.Equal(Enum.GetValues<Site>(), Cells().Select(cell => cell.Site).Distinct().Order());
    }

    [Fact]
    public void Every_cell_gets_its_expected_cause()
    {
        foreach (var (cell, result) in Results)
        {
            var expected = cell.Work is not (Work.ThreadCreateOnly or Work.AsyncIteratorCreate or Work.TimersTimerEnabledFalse or
                                             Work.TimersTimerAutoReset or Work.TimersTimerStop or Work.TimersTimerDispose);
            Assert.True(expected == result.Answer.Causes.Any(cause => cause.Code == ModelCauses.INITIALIZER_WORK_NOT_RUN),
                        $"{cell}: {result.Answer.ModelReason}: {string.Join("; ", result.Answer.Causes)}");
            Assert.Equal(ExpectedStarts(cell).Order(), result.Starts.Order());
            if (expected)
            {
                Assert.Null(result.Answer.Model);
                Assert.Equal(ModelReasons.INCOMPLETE, result.Answer.ModelReason);
                Assert.Contains("Api.#cctor", Assert.Single(result.Answer.Causes, cause => cause.Code == ModelCauses.INITIALIZER_WORK_NOT_RUN).Detail);
            }
        }
    }

    [Fact]
    public void Off_refuses_wherever_running_initializers_start_work()
    {
        foreach (var (cell, result) in Results)
        {
            var refuses = result.Answer.Causes.Any(cause => cause.Code == ModelCauses.INITIALIZER_WORK_NOT_RUN);
            Assert.True(!result.RunningStartsWork || refuses, cell.ToString());
            Assert.Equal(cell.Work is Work.TimerCreateDisabled or Work.TimersTimerSubscribeOnly or Work.TimersTimerEnabledUnknown,
                         refuses && !result.RunningStartsWork);
        }
    }

    private static IReadOnlyDictionary<Cell, Result> Run()
    {
        var results = new Dictionary<Cell, Result>();
        foreach (var cell in Cells())
        {
            var library = LibraryCompilation.CompileTrees(ASSEMBLY,
                [CSharpSyntaxTree.ParseText(Source(cell), new CSharpParseOptions(LanguageVersion.Preview), ASSEMBLY + "/Library.cs")],
                EmittedAssemblies.RuntimeReferences.Concat(new[] { typeof(Parallel).Assembly, typeof(System.Timers.Timer).Assembly, typeof(System.ComponentModel.Component).Assembly }
                    .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))).DistinctBy(reference => reference.Display).ToArray(),
                CancellationToken.None);
            Assert.NotNull(library.Compilation);
            Assert.True(!library.ExternMembers.Any(id => !id.Contains("Sink.Take", StringComparison.Ordinal)), cell.ToString());
            var request = new GenerationRequest(ASSEMBLY, "1.0", "M:Lib.Api.Run(System.Action)", null, null);
            var off = ModelGenerator.Trace(request, library, CancellationToken.None, initializers: TypeInitializerTreatment.Off);
            var running = ModelGenerator.Trace(request, library, CancellationToken.None, initializers: TypeInitializerTreatment.Run);
            var inert = ModelGenerator.Trace(request, library, CancellationToken.None, initializers: TypeInitializerTreatment.Inert);
            Assert.True(inert.Run is not null, $"{cell}: {inert.Answer.Reason}: {inert.Answer.Detail}");
            var reached = ModelGenerator.InitializerReachedBodies([inert.Run], new GenerationChecks(CancellationToken.None));
            var starts = reached.SelectMany(body => body.Body.Blocks.SelectMany(block => block.Operations)
                .Where(operation => InitializerWork.Starts(body, operation))
                .Select(operation => (body.Body.BodyId, operation.Id, Name(body.Body, operation)))).Distinct().ToArray();
            var initializer = running.Run!.Executions!.Executions.Single(execution => execution.Kind == ExecutionKind.TypeInitializer &&
                                                                                      execution.Id.Contains("Api", StringComparison.Ordinal));
            var work = running.Run.Executions.Executions.Any(execution => execution.ParentId == initializer.Id &&
                execution.Kind is ExecutionKind.Spawn or ExecutionKind.TimerCallback);
            results.Add(cell, new Result(off.Answer, work, starts.Select(start => start.Item3).ToArray()));
        }
        return results;
    }

    private static IEnumerable<string> ExpectedStarts(Cell cell)
    {
        if (cell.Work is Work.AsyncCallAwaited or Work.AsyncCallAwaitedLater)
        {
            yield return "Api.#cctor:async";
            if (cell.Work == Work.AsyncCallAwaitedLater)
                yield return "Api.Helper:async";
            yield break;
        }
        var prefix = cell.Site == Site.Initializer ? "Api.#cctor:" : "Api.Helper:";
        switch (cell.Work)
        {
            case Work.TaskRun: case Work.ThreadStart: case Work.ParallelFor:
                yield return prefix + "spawn";
                break;
            case Work.TimerCreateDisabled: case Work.TimerCreateEnabled:
                yield return prefix + "Create";
                break;
            case Work.TimerChange:
                yield return prefix + "Create";
                yield return prefix + "Change";
                break;
            case Work.TimersTimerSubscribeOnly: case Work.TimersTimerStart:
                yield return prefix + "ElapsedSubscribe";
                if (cell.Work == Work.TimersTimerStart)
                    yield return prefix + "Start";
                break;
            case Work.TimersTimerEnabledUnknown:
                yield return prefix + "SetEnabled";
                break;
            case Work.AsyncCall:
                yield return prefix + "async";
                break;
        }
    }

    private static string Name(IrBody body, IrOperation operation) =>
        (body.BodyId.Contains("#cctor", StringComparison.Ordinal) ? "Api.#cctor:" : "Api.Helper:") +
        (operation is IrTimerOperation timer ? timer.Action.ToString() : operation is IrSpawnOperation ? "spawn" : "async");

    private static string Source(Cell cell)
    {
        var statement = cell.Work switch
        {
            Work.TaskRun => "_ = Task.Run(() => State.Count++);",
            Work.ThreadCreateOnly => "_ = new Thread(() => State.Count++);",
            Work.ThreadStart => "new Thread(() => State.Count++).Start();",
            Work.ParallelFor => "Parallel.For(0, 2, i => State.Count++);",
            Work.TimerCreateDisabled => "_ = new Timer(_ => State.Count++, null, Timeout.Infinite, Timeout.Infinite);",
            Work.TimerCreateEnabled => "_ = new Timer(_ => State.Count++, null, 0, 1);",
            Work.TimerChange => "var timer = new Timer(_ => State.Count++, null, Timeout.Infinite, Timeout.Infinite); timer.Change(0, Timeout.Infinite);",
            Work.TimersTimerSubscribeOnly => "var timer = new System.Timers.Timer(); timer.Elapsed += (_, _) => State.Count++;",
            Work.TimersTimerStart => "var timer = new System.Timers.Timer(); timer.Elapsed += (_, _) => State.Count++; timer.Start();",
            Work.AsyncCall => "_ = Later();",
            Work.AsyncCallAwaitedLater => "var task = Later(); State.Count++; await task;",
            Work.AsyncIteratorCreate => "_ = Items();",
            Work.AsyncCallAwaited => "await Later();",
            Work.TimersTimerEnabledFalse => "timer.Enabled = false;",
            Work.TimersTimerEnabledUnknown => "timer.Enabled = flag;",
            Work.TimersTimerAutoReset => "timer.AutoReset = true;",
            Work.TimersTimerStop => "timer.Stop();",
            Work.TimersTimerDispose => "timer.Dispose();",
            _ => throw new ArgumentOutOfRangeException()
        };
        var async = cell.Work is Work.AsyncCallAwaited or Work.AsyncCallAwaitedLater;
        var parameter = cell.Work >= Work.TimersTimerEnabledFalse;
        var helper = parameter ? "Helper(new System.Timers.Timer(), DateTime.Now.Ticks > 0);" : "_ = Helper();";
        return PRELUDE + $$"""
            public static class State { public static int Count; }
            public static class Api
            {
                static Api() { {{(cell.Site == Site.Initializer ? statement : helper)}} }
                private static {{(async ? "async Task" : "object")}} Helper({{(parameter ? "System.Timers.Timer timer, bool flag" : "")}})
                {
                    {{(cell.Site == Site.Helper ? statement : "")}}
                    {{(async ? "" : "return null;")}}
                }
                private static async Task Later() { await Task.Yield(); State.Count++; }
                private static async IAsyncEnumerable<int> Items() { await Task.Yield(); yield return 1; }
                public static void Run(Action a) => a();
            }
            }
            """;
    }
}
