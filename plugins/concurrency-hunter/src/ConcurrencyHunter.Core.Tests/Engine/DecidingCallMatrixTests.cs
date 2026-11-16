using System.Text.Json;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Ir;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class DecidingCallMatrixTests
{
    public enum Route { Operation, UnknownDelegate, Join, CurrentPass, Continuation, Timer, Protection }
    public enum Path { SameBody, HelperReturn, ConstructorField, CallerArgument, CapturedVariable }
    public enum Callee { PackageBody, PackageDispatch, PackageNoBody, Framework }
    private sealed record Cell(Route Route, Path Path, Callee Callee);
    private sealed record Source(string Work, string Members, string Extra, string Library, string Member);

    private static IEnumerable<Cell> Cells() =>
        from route in Enum.GetValues<Route>()
        from path in Enum.GetValues<Path>()
        where path == Path.SameBody || route is not (Route.Operation or Route.UnknownDelegate)
        from callee in Enum.GetValues<Callee>()
        select new Cell(route, path, callee);

    [Fact]
    public void Every_value_of_every_axis_occurs_and_the_cells_are_counted()
    {
        var cells = Cells().ToArray();
        Assert.Equal(108, cells.Length);
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<Route>(), cells.Select(cell => cell.Route).Distinct().Order());
        Assert.Equal(Enum.GetValues<Path>(), cells.Select(cell => cell.Path).Distinct().Order());
        Assert.Equal(Enum.GetValues<Callee>(), cells.Select(cell => cell.Callee).Distinct().Order());
        Assert.All(cells.Where(cell => cell.Route is Route.Operation or Route.UnknownDelegate),
                   cell => Assert.Equal(Path.SameBody, cell.Path));
    }

    [Fact]
    public void Every_cell_is_exact()
    {
        foreach (var cell in Cells())
        {
            var source = Fixture(cell);
            var program = new CallSiteTestProgram(source.Work, source.Members, source.Extra, source.Library,
                worker: cell.Route is Route.Join or Route.CurrentPass or Route.Continuation or Route.Timer);
            var run = program.Run();
            var calls = CallSiteTestProgram.Calls(run, source.Member);
            Assert.True(calls.Count == 2, $"{cell}: expected the deciding call and the unused call, got {calls.Count}");
            var deciding = Assert.Single(calls, call => !CallText(program, call).Contains("new object()", StringComparison.Ordinal) &&
                                                      !CallText(program, call).Contains("() => { }", StringComparison.Ordinal));
            var unused = Assert.Single(calls, call => call != deciding);
            var pairs = CallSiteTestProgram.Pairs(run);
            var decisions = pairs.Pairs.SelectMany(pair => pair.DecidingCalls).ToArray();
            var component = cell.Route switch
            {
                Route.Operation => GapComponents.OPERATION,
                Route.Protection => GapComponents.PROTECTION,
                _ => GapComponents.OVERLAP
            };
            Assert.True(decisions.Any(decision => decision.Call == deciding && decision.Check.Component == component),
                        $"{cell}: the {component} check did not name its producing call");
            if (component == GapComponents.OVERLAP)
                Assert.Contains(decisions, decision => decision.Call == deciding && decision.Check.Reason == OverlapReason(cell.Route));
            Assert.DoesNotContain(decisions, decision => decision.Call == unused);
            Assert.All(decisions, decision => Assert.Equal(deciding, decision.Call));

            using var compilations = new CallSiteCompilations();
            // The fixture seam supplies the library's package/framework identity; dispatch and executable bodies are real.
            var targets = program.Selector(compilations, cell.Callee == Callee.Framework ? "framework" : "package")
                                 .Of(run, pairs, [program.Project]);
            var target = Assert.Single(targets);
            Assert.Equal(deciding, target.Call);
            Assert.Equal(cell.Callee == Callee.Framework ? "framework" :
                         cell.Callee == Callee.PackageBody ? null : "no-body", target.Outcome);
            Assert.Equal(cell.Callee == Callee.PackageDispatch, target.Dispatches);
            Assert.Equal(cell.Callee == Callee.PackageBody ? new[] { deciding } : [],
                         targets.Where(target => target.Outcome is null).Select(target => target.Call));
        }
    }

    [Fact]
    public void The_checks_do_not_change_with_the_named_call()
    {
        foreach (var cell in Cells())
        {
            var source = Fixture(cell);
            var program = new CallSiteTestProgram(source.Work, source.Members, source.Extra, source.Library,
                worker: cell.Route is Route.Join or Route.CurrentPass or Route.Continuation or Route.Timer);
            var control = program.Run();
            var selectionOff = CallSiteTestProgram.Pairs(control);
            var run = program.Run();
            var pairs = CallSiteTestProgram.Pairs(run);
            Assert.NotEmpty(pairs.Pairs);
            using var compilations = new CallSiteCompilations();
            var selector = program.Selector(compilations, cell.Callee == Callee.Framework ? "framework" : "package");
            selector.Of(run, pairs, [program.Project]);
            Assert.Equal(selectionOff.Pairs.Select(PairIdentity.Of), pairs.Pairs.Select(PairIdentity.Of));
            Assert.Equal(JsonSerializer.Serialize(selectionOff.Pairs.Select(pair => pair.GapChecks)),
                         JsonSerializer.Serialize(pairs.Pairs.Select(pair => pair.GapChecks)));
            Assert.Equal(selectionOff.Pairs.SelectMany(pair => pair.GapChecks).Select(check => check.Uncertainty),
                         pairs.Pairs.SelectMany(pair => pair.GapChecks).Select(check => check.Uncertainty));
            var before = ConflictFindings.Create(selectionOff.Pairs, control.Collection!.Accesses, CancellationToken.None).Findings;
            var after = ConflictFindings.Create(pairs.Pairs, run.Collection!.Accesses, CancellationToken.None).Findings;
            var unnamed = ConflictFindings.Create(pairs.Pairs.Select(pair => pair with { DecidingCalls = [] }),
                                                 run.Collection.Accesses, CancellationToken.None).Findings;
            Assert.NotEmpty(after);
            Assert.True(JsonSerializer.Serialize(before) == JsonSerializer.Serialize(after),
                        $"{cell}: selection changed the findings, including their fingerprints");
            Assert.True(JsonSerializer.Serialize(unnamed) == JsonSerializer.Serialize(after),
                        $"{cell}: the named call changed the findings, including their fingerprints");
        }
    }

    private static string OverlapReason(Route route) => route switch
    {
        Route.UnknownDelegate => "a side runs in the unknown call of a delegate it was handed, which nothing orders",
        Route.Join => "a task the other side's execution waits for may be its result, so the wait orders nothing",
        Route.CurrentPass => "the current pass's completion handle may be its result, so completion before the next pass is not proven",
        Route.Continuation => "the task a continuation runs after may be its result, so the continuation is ordered after nothing",
        Route.Timer => "the timer whose callback runs a side may be its result, so whether the timer is periodic is not proven",
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };

    private static string CallText(CallSiteTestProgram program, CallIdentity call)
    {
        var text = program.Project.SyntaxTrees.Single().GetText();
        return text.Lines[call.Span.StartLine - 1].ToString()[(call.Span.StartColumn - 1)..(call.Span.EndColumn - 1)];
    }

    private static Source Fixture(Cell cell)
    {
        var type = cell.Route switch
        {
            Route.Operation or Route.UnknownDelegate => "void",
            Route.Timer => "System.Timers.Timer",
            Route.Protection => "object",
            _ => "Task"
        };
        var parameters = cell.Route == Route.UnknownDelegate ? "Action action" : "object owner, Task handle";
        var result = type switch
        {
            "void" => cell.Route == Route.UnknownDelegate ? "action();" : string.Empty,
            "Task" => "return handle;",
            _ => "return new();"
        };
        var declaration = cell.Callee switch
        {
            Callee.PackageDispatch => $"public virtual {type} Decide({parameters}) {{ {result} }}",
            Callee.PackageNoBody => $"[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall)] public static extern {type} Decide({parameters});",
            _ => $"public static {type} Decide({parameters}) {{ {result} }}"
        };
        var library = "using System; using System.Threading.Tasks; public class MatrixApi { " + declaration + " }";
        var receiver = cell.Callee == Callee.PackageDispatch ? "new MatrixApi()" : "MatrixApi";
        var call = receiver + ".Decide(owner, own)";
        var unused = receiver + (cell.Route == Route.UnknownDelegate ? ".Decide(() => { });" : ".Decide(new object(), Task.CompletedTask);");
        var members = string.Empty;
        var extra = string.Empty;
        if (cell.Route is Route.Operation or Route.UnknownDelegate)
            return new(unused + (cell.Route == Route.Operation ? receiver + ".Decide(state, Task.CompletedTask); state.Value++;" :
                                receiver + ".Decide(() => state.Value++);"), members, extra, library, "MatrixApi.Decide");

        var consume = cell.Route switch
        {
            Route.Join => "var handle = token.IsCancellationRequested ? own : value; await handle; state.Value = 2;",
            Route.CurrentPass => "await value;",
            Route.Continuation => "var handle = token.IsCancellationRequested ? own : value; await handle.ContinueWith(_ => state.Value++);",
            Route.Timer => "var timer = token.IsCancellationRequested ? new System.Timers.Timer() : value; timer.Elapsed += (_, _) => state.Value++; timer.Start();",
            _ => "lock (value) { state.Value++; }"
        };
        var async = cell.Route is Route.Join or Route.CurrentPass or Route.Continuation;
        var expression = call.Replace("owner", "state");
        var work = "var value = " + expression + "; " + consume;
        switch (cell.Path)
        {
            case Path.HelperReturn:
                members = $"private {type} Produce(State owner, Task own) => {call};";
                work = "var value = Produce(state, own); " + consume;
                break;
            case Path.ConstructorField:
                extra = $"public sealed class Holder {{ public readonly {type} Value; public Holder(State owner, Task own) {{ Value = {call}; }} }}";
                work = "var holder = new Holder(state, own); var value = holder.Value; " + consume;
                break;
            case Path.CallerArgument:
                members = $"private {(async ? "async Task" : "void")} Consume({type} value, Task own, CancellationToken token) {{ {consume} }}";
                work = (async ? "await " : string.Empty) + $"Consume({expression}, own, " +
                       (cell.Route == Route.Protection ? "CancellationToken.None" : "token") + ");";
                break;
            case Path.CapturedVariable:
                work = $"var value = {expression}; " + (async ? "Func<Task> consume = async () => { " : "Action consume = () => { ") +
                       consume + " }; " + (async ? "await consume();" : "consume();");
                break;
        }
        work = cell.Route == Route.CurrentPass
            ? "for (var pass = 0; pass < 2; pass++) { var own = Task.Run(() => state.Value++); " + work + " }"
            : "var own = " + (cell.Route is Route.Join or Route.Continuation ? "Task.Run(() => state.Value++)" : "Task.CompletedTask") + "; " + work;
        return new(unused + work, members, extra, library, "MatrixApi.Decide");
    }
}
