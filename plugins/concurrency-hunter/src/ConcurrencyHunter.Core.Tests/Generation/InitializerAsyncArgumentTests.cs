using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

public sealed class InitializerAsyncArgumentTests
{
    [Theory]
    [InlineData("list", false, false)]
    [InlineData("list", false, true)]
    [InlineData("array", false, false)]
    [InlineData("array", false, true)]
    [InlineData("list", true, false)]
    [InlineData("list", true, true)]
    [InlineData("array", true, false)]
    [InlineData("array", true, true)]
    public void Modeled_async_callbacks_are_possible_work_also_after_the_automatic_switch(string kind, bool automatic, bool lambda)
    {
        var call = kind == "list" ? "new List<int> { 1 }.ForEach(Callback);" : "Array.ForEach(new[] { 1 }, Callback);";
        if (lambda)
            call = call.Replace("Callback", "async value => { await Task.Yield(); State.Value++; }");
        var source = PRELUDE + $$"""
            public static class State { public static int Value; }
            public static class Api
            {
                static Api() { {{call}} {{(automatic ? "Huge.All();" : "")}} }
                private static async void Callback(int value) { await Task.Yield(); State.Value++; }
                public static void Run(Action a) => a();
            }
            """ + (automatic ? Huge() : "") + "}";
        var library = Compile(source);
        var request = new GenerationRequest(ASSEMBLY, "1.0", "M:Lib.Api.Run(System.Action)", null, null);
        var off = ModelGenerator.Trace(request, library, CancellationToken.None,
                                       initializers: automatic ? null : TypeInitializerTreatment.Off);
        Assert.Equal("off", off.Answer.Generation.Mode);
        if (automatic)
            Assert.True(off.Answer.Generation.Attempts[0].ReachedBodies > 1500);
        Assert.Equal(ModelReasons.INCOMPLETE, off.Answer.ModelReason);
        Assert.Contains(off.Answer.Causes, cause => cause.Code == ModelCauses.INITIALIZER_WORK_NOT_RUN);
        Assert.Null(off.Answer.Model);
        if (!automatic)
        {
            foreach (var treatment in new[] { TypeInitializerTreatment.Run, TypeInitializerTreatment.Inert })
            {
                var twin = ModelGenerator.Trace(request, library, CancellationToken.None, initializers: treatment);
                Assert.Contains(twin.Run!.Executions!.Executions, execution => execution.ParentId is { } parent &&
                    twin.Run.Executions.Executions.Any(owner => owner.Id == parent && owner.Kind == ExecutionKind.TypeInitializer));
            }
        }
    }

    [Theory]
    [InlineData("extern", "Async", true)]
    [InlineData("extern", "async () => { await Task.Yield(); }", true)]
    [InlineData("extern", "() => { }", false)]
    [InlineData("extern", "Items", false)]
    [InlineData("idle", "Async", false)]
    [InlineData("idle", "Items", false)]
    [InlineData("idle", "() => { }", false)]
    [InlineData("invoke", "Async", true)]
    [InlineData("awaited", "Async", false)]
    public void Only_unseen_or_invoked_async_arguments_may_start_work(string callee, string argument, bool work)
    {
        var parameter = argument == "Items" ? "Func<IAsyncEnumerable<int>>" : "Func<Task>";
        if (argument == "() => { }")
            parameter = "Action";
        var body = callee switch { "invoke" => "{ _ = callback(); }", "awaited" => "{ await callback(); }", _ => "{ }" };
        var source = PRELUDE + $$"""
            public static class Api
            {
                static Api() { {{(callee == "awaited" ? "_ = Helper();" : "Take(" + argument + ");")}} }
                private static async Task Helper() { await Take(Async); }
                private static async Task Async() { await Task.Yield(); }
                private static async IAsyncEnumerable<int> Items() { await Task.Yield(); yield return 1; }
                private static {{(callee == "extern" ? "extern void" : callee == "awaited" ? "async Task" : "void")}} Take({{parameter}} callback) {{(callee == "extern" ? ";" : body)}}
                public static void Run(Action a) => a();
            }
            }
            """;
        // Only the awaited fixture needs the Task-returning overload used by Helper.
        if (callee != "awaited")
            source = source.Replace("private static async Task Helper() { await Take(Async); }", "");
        var library = Compile(source);
        var request = new GenerationRequest(ASSEMBLY, "1.0", "M:Lib.Api.Run(System.Action)", null, null);
        var trace = ModelGenerator.Trace(request, library, CancellationToken.None, initializers: TypeInitializerTreatment.Off);
        var running = ModelGenerator.Trace(request, library, CancellationToken.None, initializers: TypeInitializerTreatment.Run);
        var initializer = running.Run!.Executions!.Executions.Single(execution => execution.Kind == ExecutionKind.TypeInitializer &&
            execution.Id.Contains("Api", StringComparison.Ordinal));
        var starts = running.Run.Executions.Executions.Any(execution => execution.ParentId == initializer.Id &&
            execution.Kind is ExecutionKind.Spawn or ExecutionKind.TimerCallback);
        // The initializer's own unawaited call of Helper is a start, even though Helper immediately awaits Take.
        var expected = work || callee == "awaited";
        // Known-wider (D-0025): an extern may invoke the async argument, though Run cannot observe it doing so.
        var knownWider = callee == "extern" && work;
        Assert.Equal(knownWider, expected && !starts);
        Assert.Equal(expected, trace.Answer.Causes.Any(cause => cause.Code == ModelCauses.INITIALIZER_WORK_NOT_RUN));
        if (expected)
            Assert.Null(trace.Answer.Model);
        else
            Assert.NotNull(trace.Answer.Model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Awaiting_an_unseen_call_does_not_await_its_async_argument(bool asyncArgument)
    {
        var library = Compile(PRELUDE + $$"""
            public static class Api
            {
                static Api() { _ = Helper(); }
                private static async Task Helper() { await Take(Argument); }
                private static extern Task Take(Func<Task> callback);
                private static {{(asyncArgument ? "async Task Argument() { await Task.Yield(); }" : "Task Argument() => Task.CompletedTask;")}}
                public static void Run(Action a) => a();
            }
            }
            """);
        var request = new GenerationRequest(ASSEMBLY, "1.0", "M:Lib.Api.Run(System.Action)", null, null);
        var inert = ModelGenerator.Trace(request, library, CancellationToken.None, initializers: TypeInitializerTreatment.Inert);
        var reached = ModelGenerator.InitializerReachedBodies([inert.Run!], new GenerationChecks(CancellationToken.None));
        var helper = Assert.Single(reached, body => body.Body.BodyId.EndsWith("M:Lib.Api.Helper", StringComparison.Ordinal));
        var call = Assert.Single(helper.Body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>(),
                                 call => call.Method.Contains("Take", StringComparison.Ordinal));
        Assert.True(call.IsAwaitedImmediately);
        Assert.Equal(asyncArgument, InitializerWork.Starts(helper, call));
        var off = ModelGenerator.Trace(request, library, CancellationToken.None, initializers: TypeInitializerTreatment.Off);
        Assert.Null(off.Answer.Model);
        Assert.Contains(off.Answer.Causes, cause => cause.Code == ModelCauses.INITIALIZER_WORK_NOT_RUN);
    }

    private static string Huge() => "public static class Huge { public static void All() { " +
        string.Join(" ", Enumerable.Range(0, 1501).Select(i => $"M{i}();")) + " } " +
        string.Join(" ", Enumerable.Range(0, 1501).Select(i => $"private static void M{i}() {{ }}")) + " }";
}
