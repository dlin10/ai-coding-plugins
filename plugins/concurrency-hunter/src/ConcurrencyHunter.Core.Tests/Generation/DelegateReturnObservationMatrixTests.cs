using System.Text;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The generator's answer for a member whose result is what a delegate a known call ran gives back, or what a task completes
/// with that a producer not seen in full may have completed, against its synchronous twin (R6, R7). Three sweeps:
/// <list type="bullet">
/// <item>a model's <c>returns:f</c>, in each form naming it, is what one run of f gives back, for f's own parameter only, exactly as the
/// engine answers it — over the kind of f, what it returns, and a second delegate g beside it;</item>
/// <item>a delegate's parameter is followed back to the input the model's fate hands it, over the input form, its source, the callback
/// and the form naming what the callback returns;</item>
/// <item>an async call with a resolved callee beside an unresolved receiver alternative completes with something no producer the export
/// names, wherever its task travels before it is consumed.</item>
/// </list>
/// Each cell's generated result is exactly its twin's inside <c>task(…)</c>, or no result where the twin has none; a known value
/// through code seen in full is named, and an unseen one never is.</summary>
public sealed class DelegateReturnObservationMatrixTests
{
    private const string DEPENDENCY = "Fixture.Dependency";
    private const string MEMBER = "M:Lib.Cell.Run(Lib.Cargo,System.Boolean)";

    private const string TYPES = """
        public sealed class Cargo { public Cargo Inner; }
        public static class Externals
        {
            public static extern Cargo MakeCargo();
            public static extern Func<Cargo, Cargo> MakeWork();
            public static extern Func<Cargo, Task<Cargo>> MakeTaskWork();
            public static extern Func<object> MakeObjectWork();
            public static extern Maker MakeMaker();
        }
        public static class Source
        {
            public static Cargo Known(Cargo v) => v;
            public static Cargo Opaque(Cargo v) => Externals.MakeCargo();
            public static Cargo Mixed(Cargo v) => Environment.ProcessorCount > 1 ? v : Externals.MakeCargo();
            public static Task<Cargo> KnownTask(Cargo v) => Task.FromResult(v);
            public static Task<Cargo> OpaqueTask(Cargo v) => Task.FromResult(Externals.MakeCargo());
            public static Task<Cargo> MixedTask(Cargo v) => Task.FromResult(Environment.ProcessorCount > 1 ? v : Externals.MakeCargo());
        }
        public class Maker
        {
            public virtual async Task<Cargo> Make(Cargo p) { await Task.Yield(); return p; }
            public virtual Cargo MakeNow(Cargo p) => p;
        }
        public sealed class Slot { public Task<Cargo> Pending; }

        """;

    private static readonly Dictionary<string, string?> Twins = new(StringComparer.Ordinal);

    private static readonly Lazy<(MetadataReference Reference, string Models)> Dependency = new(CompileDependency);

    // ---- rule 1: returns:f ----

    /// <summary>What f is.</summary>
    public enum DelegateKind { Sync, AsyncTask, AsyncValueTask, MethodGroup, Unseen, VisibleOrUnseen }

    /// <summary>What a run of f gives back of the parameter it is handed.</summary>
    public enum Returned { Known, Opaque, Mixed }

    /// <summary>The second delegate g beside f: a visible lambda returning an opaque object, or one the analysis does not see.</summary>
    public enum Second { Known, Unseen }

    /// <summary>The form of the model's result that names what f gives back.</summary>
    public enum Form { Returns, CompletionReturns, TaskReturns, TaskCompletionReturns, SequenceReturns }

    /// <summary>One cell of the <c>returns:f</c> sweep.</summary>
    /// <param name="Delegate">What f is.</param>
    /// <param name="Returned">What a run of f gives back.</param>
    /// <param name="Second">The second delegate.</param>
    /// <param name="Form">The form naming what f gives back.</param>
    public sealed record ReturnsCell(DelegateKind Delegate, Returned Returned, Second Second, Form Form)
    {
        public override string ToString() => $"{Delegate}/{Returned}/{Second}/{Form}";
    }

    public static IEnumerable<ReturnsCell> ReturnsCells() =>
        from form in Enum.GetValues<Form>()
        from function in Enum.GetValues<DelegateKind>()
        from returned in Enum.GetValues<Returned>()
        from second in Enum.GetValues<Second>()
        select new ReturnsCell(function, returned, second, form);

    public static TheoryData<string> ReturnsCellNames() => new(ReturnsCells().Select(cell => cell.ToString()));

    [Theory]
    [MemberData(nameof(ReturnsCellNames))]
    public void Returns_form_generates_exactly_its_synchronous_twins_result(string name)
    {
        var cell = ReturnsCells().Single(candidate => candidate.ToString() == name);
        var seen = cell.Delegate is not (DelegateKind.Unseen or DelegateKind.VisibleOrUnseen);
        // An unseen g runs code the member's entry cannot say, which leaves no model to name the result in.
        var known = cell is { Returned: Returned.Known, Second: Second.Known } && seen;
        AssertTwin(name, CellSource(cell), TwinSource(cell), known ? "[arg:x]" : null, unseen: cell.Returned != Returned.Known || !seen);
    }

    /// <summary>What a run of f gives back, as an expression of its parameter <c>v</c> and the member's <c>flag</c>.</summary>
    /// <param name="returned">What it gives back.</param>
    private static string Body(Returned returned) => returned switch
    {
        Returned.Known => "v",
        Returned.Opaque => "Externals.MakeCargo()",
        Returned.Mixed => "flag ? v : Externals.MakeCargo()",
        _ => throw new ArgumentOutOfRangeException(nameof(returned), returned, null)
    };

    /// <summary>Whether the form names what the tasks f gives back complete with.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Completes(ReturnsCell cell) => cell.Form is Form.CompletionReturns or Form.TaskCompletionReturns;

    /// <summary>f as the cell hands it: giving back the value, or for a completion form a task completing with it.</summary>
    /// <param name="cell">The cell.</param>
    private static string Function(ReturnsCell cell)
    {
        var body = Body(cell.Returned);
        var completes = Completes(cell);
        return cell.Delegate switch
        {
            DelegateKind.Sync => completes ? $"(Func<Cargo, Task<Cargo>>)(v => Task.FromResult({body}))" : $"(Func<Cargo, Cargo>)(v => {body})",
            DelegateKind.AsyncTask => $"(Func<Cargo, Task<Cargo>>)(async v => {{ await Task.Yield(); return {body}; }})",
            DelegateKind.AsyncValueTask => $"(Func<Cargo, ValueTask<Cargo>>)(async v => {{ await Task.Yield(); return {body}; }})",
            DelegateKind.MethodGroup => completes ? $"(Func<Cargo, Task<Cargo>>)Source.{cell.Returned}Task" : $"(Func<Cargo, Cargo>)Source.{cell.Returned}",
            DelegateKind.Unseen => completes ? "Externals.MakeTaskWork()" : "Externals.MakeWork()",
            DelegateKind.VisibleOrUnseen => completes
                ? $"(flag ? (Func<Cargo, Task<Cargo>>)(v => Task.FromResult({body})) : Externals.MakeTaskWork())"
                : $"(flag ? (Func<Cargo, Cargo>)(v => {body}) : Externals.MakeWork())",
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
    }

    private static string SecondFunction(Second second) =>
        second == Second.Known ? "(Func<object>)(() => Externals.MakeCargo())" : "Externals.MakeObjectWork()";

    /// <summary>The cell's member: the known call handed <c>x</c>, f and g, and every task it gives consumed by <c>await</c>, the
    /// sequence's first element taken.</summary>
    /// <param name="cell">The cell.</param>
    private static string CellSource(ReturnsCell cell)
    {
        var arguments = $"x, {Function(cell)}, {SecondFunction(cell.Second)}";
        var asyncRun = cell.Delegate is DelegateKind.AsyncTask or DelegateKind.AsyncValueTask && !Completes(cell);
        var valueTask = cell.Delegate == DelegateKind.AsyncValueTask ? "Value" : "";
        var run = asyncRun ? "await " : "";
        var statements = cell.Form switch
        {
            Form.Returns => $"return {run}Dependency.Calls.Returns({arguments});",
            Form.CompletionReturns => $"return Dependency.Calls.CompletionReturns{valueTask}({arguments});",
            Form.TaskReturns => $"return {run}await Dependency.Calls.TaskReturns({arguments});",
            Form.TaskCompletionReturns => $"return await Dependency.Calls.TaskCompletionReturns{valueTask}({arguments});",
            Form.SequenceReturns => $"foreach (var item in Dependency.Calls.SequenceReturns({arguments})) return {run}item; return null!;",
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        return $"public static class Cell {{ public static async Task<Cargo> Run(Cargo x, bool flag) {{ {statements} }} }}";
    }

    /// <summary>The twin: the same code with the task taken out — f giving back its value directly, through the same unseen
    /// alternative, the same g, and the synchronous form.</summary>
    /// <param name="cell">The cell.</param>
    private static string TwinSource(ReturnsCell cell)
    {
        var body = Body(cell.Returned);
        var f = cell.Delegate switch
        {
            DelegateKind.Sync or DelegateKind.AsyncTask or DelegateKind.AsyncValueTask => $"(Func<Cargo, Cargo>)(v => {body})",
            DelegateKind.MethodGroup => $"(Func<Cargo, Cargo>)Source.{cell.Returned}",
            DelegateKind.Unseen => "Externals.MakeWork()",
            DelegateKind.VisibleOrUnseen => $"(flag ? (Func<Cargo, Cargo>)(v => {body}) : Externals.MakeWork())",
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        var arguments = $"x, {f}, {SecondFunction(cell.Second)}";
        var statements = cell.Form == Form.SequenceReturns
            ? $"foreach (var item in Dependency.Calls.SequenceReturns({arguments})) return item; return null!;"
            : $"return Dependency.Calls.Returns({arguments});";
        return $"public static class Cell {{ public static Cargo Run(Cargo x, bool flag) {{ {statements} }} }}";
    }

    // ---- rule 2: a delegate parameter followed to the fate input ----

    /// <summary>What the model's fate hands the callback.</summary>
    public enum Input { Argument, Receiver, Completion, New, Kept, Elements }

    /// <summary>What the callback gives back of its parameter.</summary>
    public enum Callback { Identity, Field }

    /// <summary>The form of the model's result naming what the callback gives back.</summary>
    public enum Route { Returns, CompletionReturns, TaskReturns }

    /// <summary>One cell of the fate-input sweep.</summary>
    /// <param name="Input">The fate's input.</param>
    /// <param name="Returned">Where the input's value comes from.</param>
    /// <param name="Callback">The callback.</param>
    /// <param name="Route">The form naming what the callback gives back.</param>
    public sealed record InputCell(Input Input, Returned Returned, Callback Callback, Route Route)
    {
        public override string ToString() => $"{Input}/{Returned}/{Callback}/{Route}";
    }

    public static IEnumerable<InputCell> InputCells() =>
        from route in Enum.GetValues<Route>()
        from input in Enum.GetValues<Input>()
        from returned in Enum.GetValues<Returned>()
        from callback in Enum.GetValues<Callback>()
        select new InputCell(input, returned, callback, route);

    public static TheoryData<string> InputCellNames() => new(InputCells().Select(cell => cell.ToString()));

    [Theory]
    [MemberData(nameof(InputCellNames))]
    public void Fate_input_reaches_the_callback_as_its_synchronous_twins_argument(string name)
    {
        var cell = InputCells().Single(candidate => candidate.ToString() == name);
        // The twin reads an argument or a completion value as the value itself; an element, a kept value or a field it reads as the
        // synchronous code reads it, which this sweep compares against and does not judge.
        var plain = cell is { Callback: Callback.Identity, Input: Input.Argument or Input.Completion };
        AssertTwin(name, CellSource(cell), TwinSource(cell), plain && cell.Returned == Returned.Known ? "[arg:x]" : null,
                   unseen: plain && cell.Returned != Returned.Known);
    }

    /// <summary>The value the input stands for, as an expression of the member's <c>x</c> and <c>flag</c>.</summary>
    /// <param name="returned">Where it comes from.</param>
    private static string Value(Returned returned) => returned switch
    {
        Returned.Known => "x",
        Returned.Opaque => "Externals.MakeCargo()",
        Returned.Mixed => "flag ? x : Externals.MakeCargo()",
        _ => throw new ArgumentOutOfRangeException(nameof(returned), returned, null)
    };

    /// <summary>The callback's parameter type for an input.</summary>
    /// <param name="input">The input.</param>
    private static string ParameterType(Input input) => input switch
    {
        Input.Receiver => "Dependency.Wrap<Cargo>",
        Input.New => "Dependency.Made",
        Input.Kept => "object",
        _ => "Cargo"
    };

    /// <summary>The callback's body over its parameter <c>v</c>.</summary>
    /// <param name="cell">The cell.</param>
    private static string CallbackBody(InputCell cell) => (cell.Callback, cell.Input) switch
    {
        (Callback.Identity, _) => "v",
        (Callback.Field, Input.Receiver) => "v.Value",
        (Callback.Field, Input.New) => "v.Value",
        (Callback.Field, Input.Kept) => "((Cargo)v).Inner",
        _ => "v.Inner"
    };

    /// <summary>The statements that make the input's object, and the argument handing it to the known call.</summary>
    /// <param name="cell">The cell.</param>
    private static (string Prefix, string Argument) Handed(InputCell cell)
    {
        var value = Value(cell.Returned);
        return cell.Input switch
        {
            Input.Argument => ("", value),
            Input.Receiver => ($"var w = new Dependency.Wrap<Cargo>(); w.Value = {value}; ", ""),
            Input.Completion => ("", $"Task.FromResult<Cargo>({value})"),
            Input.New => ("", ""),
            Input.Kept => ($"var k = new Dependency.Keeper(); Dependency.Calls.Keep<Cargo>(k, {value}); ", "k"),
            Input.Elements => ($"var a = new Cargo[] {{ {value} }}; ", "a"),
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
    }

    private static string CellSource(InputCell cell)
    {
        var (prefix, argument) = Handed(cell);
        var type = ParameterType(cell.Input);
        var body = CallbackBody(cell);
        var callback = cell.Route == Route.CompletionReturns
            ? $"(Func<{type}, Task<object>>)(v => Task.FromResult<object>({body}))"
            : $"(Func<{type}, object>)(v => {body})";
        var call = cell.Input == Input.Receiver
            ? $"w.{cell.Route}({callback})"
            : $"Dependency.Calls.Hand{cell.Input}{cell.Route}({(argument.Length == 0 ? "" : argument + ", ")}{callback})";
        var returned = cell.Route == Route.TaskReturns ? $"await {call}" : call;
        return $"public static class Cell {{ public static async Task<object> Run(Cargo x, bool flag) {{ {prefix}return {returned}; }} }}";
    }

    /// <summary>The twin: the same code with the delegate's run taken out of the call — the callback invoked directly on what the input
    /// stands for, read the same way: the array's element, what the keeper keeps through a model naming it.</summary>
    /// <param name="cell">The cell.</param>
    private static string TwinSource(InputCell cell)
    {
        var value = Value(cell.Returned);
        var (prefix, handed) = cell.Input switch
        {
            Input.Receiver => ($"var w = new Dependency.Wrap<Cargo>(); w.Value = {value}; ", "w"),
            Input.New => ("", "new Dependency.Made()"),
            Input.Kept => ($"var k = new Dependency.Keeper(); Dependency.Calls.Keep<Cargo>(k, {value}); ", "Dependency.Calls.ReadKept(k)"),
            Input.Elements => ($"var a = new Cargo[] {{ {value} }}; ", "a[0]"),
            _ => ("", $"({value})")
        };
        var type = ParameterType(cell.Input);
        return $"public static class Cell {{ public static object Run(Cargo x, bool flag) {{ {prefix}Func<{type}, object> f = v => {CallbackBody(cell)}; " +
               $"return f({handed}); }} }}";
    }

    // ---- an async call beside an unseen receiver ----

    /// <summary>Where the task travels before it is consumed.</summary>
    public enum Travel { Direct, Local, Field, Collection }

    /// <summary>The receiver of the async call: a known one, or a known one beside one the analysis does not see.</summary>
    public enum Receiver { Known, KnownOrUnseen }

    /// <summary>How the member consumes the task.</summary>
    public enum Consumer { Await, Result }

    /// <summary>One cell of the async-call sweep.</summary>
    /// <param name="Receiver">The receiver.</param>
    /// <param name="Travel">Where the task travels.</param>
    /// <param name="Consumer">How it is consumed.</param>
    public sealed record AsyncCell(Receiver Receiver, Travel Travel, Consumer Consumer)
    {
        public override string ToString() => $"{Receiver}/{Travel}/{Consumer}";
    }

    public static IEnumerable<AsyncCell> AsyncCells() =>
        from receiver in Enum.GetValues<Receiver>()
        from travel in Enum.GetValues<Travel>()
        from consumer in Enum.GetValues<Consumer>()
        select new AsyncCell(receiver, travel, consumer);

    public static TheoryData<string> AsyncCellNames() => new(AsyncCells().Select(cell => cell.ToString()));

    [Theory]
    [MemberData(nameof(AsyncCellNames))]
    public void Async_call_task_is_observed_as_its_synchronous_twin_wherever_it_travels(string name)
    {
        var cell = AsyncCells().Single(candidate => candidate.ToString() == name);
        var receiver = cell.Receiver == Receiver.Known ? "new Maker()" : "(flag ? new Maker() : Externals.MakeMaker())";
        var call = $"{receiver}.Make(x)";
        var (prefix, task) = cell.Travel switch
        {
            Travel.Direct => ("", call),
            Travel.Local => ($"var t = {call}; ", "t"),
            Travel.Field => ($"var slot = new Slot(); slot.Pending = {call}; ", "slot.Pending"),
            Travel.Collection => ($"var list = new List<Task<Cargo>> {{ {call} }}; ", "list[0]"),
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        var consumed = cell.Consumer == Consumer.Await ? $"await {task}" : $"{task}.Result";
        var source = $"public static class Cell {{ public static async Task<Cargo> Run(Cargo x, bool flag) {{ {prefix}return {consumed}; }} }}";
        var twin = $"public static class Cell {{ public static Cargo Run(Cargo x, bool flag) {{ return {receiver}.MakeNow(x); }} }}";
        AssertTwin(name, source, twin, cell.Receiver == Receiver.Known ? "[arg:x]" : null, unseen: cell.Receiver == Receiver.KnownOrUnseen);
    }

    // ---- the oracle ----

    /// <summary>Asserts a cell generates exactly its twin's result inside <c>task(…)</c>, and the twin the known answer where there is
    /// one: <c>[arg:x]</c> for a known value through code seen in full, nothing naming <c>x</c> for one that may be unseen.</summary>
    /// <param name="name">The cell's name.</param>
    /// <param name="cell">The cell's member class.</param>
    /// <param name="twinSource">The twin's member class.</param>
    /// <param name="known">The twin's result where it is known, or null.</param>
    /// <param name="unseen">Whether the value may be one the analysis does not see, so that no result may name <c>x</c>.</param>
    private static void AssertTwin(string name, string cell, string twinSource, string? known, bool unseen)
    {
        var twin = TwinResult(twinSource);
        if (known is not null)
            Assert.True(known == twin, $"{name}: the twin gives {twin ?? "no result"}, not {known} ({Explain(Generate(twinSource))})");
        if (unseen)
            Assert.True(!(twin ?? "").Contains("arg:x", StringComparison.Ordinal), $"{name}: the twin names x: {twin}");
        var expected = twin is null ? null : $"task({twin})";
        var trace = Generate(cell);
        var result = trace.Answer.Model?.Result?.ToString();
        Assert.True(expected == result, $"{name}: the twin gives {expected ?? "no result"}, the cell {result ?? "no result"} ({Explain(trace)})");
    }

    /// <summary>Why a trace's answer is what it is.</summary>
    /// <param name="trace">The trace.</param>
    private static string Explain(GenerationTrace trace) => $"{trace.Answer.Reason ?? trace.Answer.ModelReason}: {trace.Answer.Detail}";

    private static string? TwinResult(string source)
    {
        lock (Twins)
        {
            if (!Twins.TryGetValue(source, out var result))
                Twins[source] = result = Generate(source).Answer.Model?.Result?.ToString();
            return result;
        }
    }

    /// <summary>The generator's trace for <see cref="MEMBER"/> of a library of the prelude, <see cref="TYPES"/> and the member's class,
    /// over the dependency and its project models.</summary>
    /// <param name="cell">The member's class.</param>
    private static GenerationTrace Generate(string cell)
    {
        var source = GenerationRuns.PRELUDE + TYPES + cell + "\n}\n";
        var library = LibraryCompilation.CompileTrees(GenerationRuns.ASSEMBLY,
                                                      [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), $"{GenerationRuns.ASSEMBLY}/Library.cs")],
                                                      [.. EmittedAssemblies.RuntimeReferences, Dependency.Value.Reference], CancellationToken.None);
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        var trace = ModelGenerator.Trace(new GenerationRequest(GenerationRuns.ASSEMBLY, "1.0", MEMBER, null, null), library, CancellationToken.None,
                                         DependencyModels(library.Compilation));
        Assert.True(trace.Answer.Reason is null, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        return trace;
    }

    // ---- the dependency ----

    /// <summary>The dependency's members and their project-model decisions.</summary>
    private static (string Source, IReadOnlyList<(string Type, string Member, string Decision)> Entries) DependencySource()
    {
        const string INVOKE_NOW = "\"fate\":\"invoke-now\"";
        var text = new StringBuilder("""
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            namespace Dependency
            {
                public sealed class Keeper { }
                public sealed class Made { public object Value; }
                public sealed class Wrap<T>
                {
                    public T Value;
                    public object Returns(Func<Wrap<T>, object> f) => default!;
                    public object CompletionReturns(Func<Wrap<T>, Task<object>> f) => default!;
                    public Task<object> TaskReturns(Func<Wrap<T>, object> f) => default!;
                }
                public static class Calls
                {
                    public static R Returns<T, R>(T input, Func<T, R> f, Func<object> g) => default!;
                    public static R CompletionReturns<T, R>(T input, Func<T, Task<R>> f, Func<object> g) => default!;
                    public static R CompletionReturnsValue<T, R>(T input, Func<T, ValueTask<R>> f, Func<object> g) => default!;
                    public static Task<R> TaskReturns<T, R>(T input, Func<T, R> f, Func<object> g) => default!;
                    public static Task<R> TaskCompletionReturns<T, R>(T input, Func<T, Task<R>> f, Func<object> g) => default!;
                    public static Task<R> TaskCompletionReturnsValue<T, R>(T input, Func<T, ValueTask<R>> f, Func<object> g) => default!;
                    public static IEnumerable<R> SequenceReturns<T, R>(T input, Func<T, R> f, Func<object> g) => default!;
                    public static void Keep<T>(Keeper k, T v) { }
                    public static object ReadKept(Keeper k) => default!;

            """);
        var fates = $"\"fates\":{{\"f\":{{{INVOKE_NOW},\"inputs\":[[\"arg:input\"]]}},\"g\":{{{INVOKE_NOW},\"inputs\":[]}}}}";
        var entries = new List<(string Type, string Member, string Decision)>
        {
            ("Calls", "Returns", $"\"result\":\"[returns:f]\",{fates}"),
            ("Calls", "CompletionReturns", $"\"result\":\"[completion(returns:f)]\",{fates}"),
            ("Calls", "CompletionReturnsValue", $"\"result\":\"[completion(returns:f)]\",{fates}"),
            ("Calls", "TaskReturns", $"\"result\":\"task([returns:f])\",{fates}"),
            ("Calls", "TaskCompletionReturns", $"\"result\":\"task([completion(returns:f)])\",{fates}"),
            ("Calls", "TaskCompletionReturnsValue", $"\"result\":\"task([completion(returns:f)])\",{fates}"),
            ("Calls", "SequenceReturns", $"\"result\":\"sequence(returns:f)\",{fates}"),
            ("Calls", "Keep", "\"keeps\":{\"k\":[\"arg:v\"]}"),
            ("Calls", "ReadKept", "\"result\":\"[kept:k]\"")
        };
        foreach (var input in Enum.GetValues<Input>())
        {
            var (parameter, form) = input switch
            {
                Input.Argument => ("T input, ", "arg:input"),
                Input.Receiver => ("", "this"),
                Input.Completion => ("Task<T> input, ", "completion(arg:input)"),
                Input.New => ("", "new"),
                Input.Kept => ("Keeper input, ", "kept:input"),
                Input.Elements => ("T[] input, ", "elements(arg:input)"),
                _ => throw new ArgumentOutOfRangeException(nameof(input), input, null)
            };
            var type = input switch
            {
                Input.New => "Made",
                Input.Kept => "object",
                _ => "T"
            };
            var generic = input is Input.New or Input.Kept ? "" : "<T>";
            var decision = $"\"fates\":{{\"f\":{{{INVOKE_NOW},\"inputs\":[[\"{form}\"]]}}}}";
            foreach (var route in Enum.GetValues<Route>())
            {
                var result = route switch
                {
                    Route.Returns => "[returns:f]",
                    Route.CompletionReturns => "[completion(returns:f)]",
                    _ => "task([returns:f])"
                };
                entries.Add((input == Input.Receiver ? "Wrap`1" : "Calls", input == Input.Receiver ? route.ToString() : $"Hand{input}{route}",
                             $"\"result\":\"{result}\",{decision}"));
                if (input == Input.Receiver)
                    continue;
                var signature = route switch
                {
                    Route.Returns => $"object Hand{input}{route}{generic}({parameter}Func<{type}, object> f)",
                    Route.CompletionReturns => $"object Hand{input}{route}{generic}({parameter}Func<{type}, Task<object>> f)",
                    _ => $"Task<object> Hand{input}{route}{generic}({parameter}Func<{type}, object> f)"
                };
                text.Append($"        public static {signature} => default!;\n");
            }
        }

        text.Append("    }\n}\n");
        return (text.ToString(), entries);
    }

    private static (MetadataReference Reference, string Models) CompileDependency()
    {
        var (source, entries) = DependencySource();
        var compilation = CSharpCompilation.Create(DEPENDENCY, [CSharpSyntaxTree.ParseText(source)], EmittedAssemblies.RuntimeReferences,
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var models = "{\"schemaVersion\":1,\"assemblies\":[\"" + DEPENDENCY + "\"],\"models\":[" +
                     string.Join(",", entries.Select(entry =>
                     {
                         var member = compilation.GetTypeByMetadataName($"Dependency.{entry.Type}")!.GetMembers(entry.Member).Single();
                         return "{\"member\":\"" + DocumentationCommentId.CreateDeclarationId(member) + "\",\"effects\":{}," + entry.Decision + "}";
                     })) + "]}";
        return (MetadataReference.CreateFromImage(stream.ToArray()), models);
    }

    /// <summary>The built-in models and the dependency's project models.</summary>
    /// <param name="compilation">The library's compilation, which references the dependency.</param>
    private static LibraryModels DependencyModels(Compilation compilation)
    {
        var root = Directory.CreateTempSubdirectory("ch-delegate-returns-matrix-").FullName;
        try
        {
            var folder = Path.Combine(root, ".concurrency-hunter", "models");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "dependency.json"), Dependency.Value.Models, new UTF8Encoding(false));
            var files = ProjectModelFiles.Read(root);
            var (resolved, rejections) = ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(root, files));
            Assert.True(rejections.Count == 0, string.Join("\n", rejections));
            return resolved;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
