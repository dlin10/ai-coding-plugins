using System.Text;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>What a model's <c>returns:f</c> gives — one run of f's body — read by every form around it, as its synchronous twin reads it
/// (R1, R2, R4): an async body gives back its task, completed with what the body returns; a delegate some alternative of which is code the
/// analysis does not see gives back an unknown value; and an iterator reached through f's task runs where its twin's runs. The axes are
/// the form around <c>returns:</c> and its fate (invoke-now or iterator, the built-in LINQ <c>Select</c> among them), the delegate kind,
/// what f returns, and the consumer. One fixture of project-model members, analysed once, with one method per row.</summary>
public sealed class DelegateReturnsTests(ClassCache cache) : IClassFixture<ClassCache>
{
    private const string FLAG = "Environment.ProcessorCount > 1";

    private const string PREFIX = "body:Fixture:M:Rows.";

    /// <summary>The form around <c>returns:f</c>, its fate, and the member carrying it.</summary>
    public enum Form
    {
        /// <summary><c>task([returns:f])</c>, invoke-now, f a <c>Func&lt;T&gt;</c>.</summary>
        NowValue,
        /// <summary><c>task([completion(returns:f)])</c>, invoke-now.</summary>
        NowCompletion,
        /// <summary><c>[completion(returns:f)]</c>, invoke-now, no task around the result.</summary>
        NowDirect,
        /// <summary><c>sequence(completion(returns:f))</c>, iterator.</summary>
        LazyCompletion,
        /// <summary><c>sequence(returns:f)</c>, iterator: a sequence of f's tasks.</summary>
        LazyTasks,
        /// <summary>The built-in <c>Enumerable.Select</c>, <c>sequence(returns:selector)</c>, over an async selector.</summary>
        Linq,
        /// <summary><c>collection(elements(completion(returns:f)))</c>, invoke-now, f's task completing with an iterator.</summary>
        EnumNow,
        /// <summary><c>task(collection(elements(completion(returns:f))))</c>, invoke-now.</summary>
        EnumNowTask,
        /// <summary><c>sequence(elements(completion(returns:f)))</c>, iterator.</summary>
        EnumLazy,
        /// <summary><c>collection(elements(completion(elements(arg:ts))))</c>: the tasks an argument holds.</summary>
        EnumArg
    }

    /// <summary>What f is.</summary>
    public enum Kind
    {
        /// <summary>A non-async lambda: the value itself, or <c>Task.FromResult</c> of it.</summary>
        Sync,
        /// <summary>An async lambda returning a <c>Task&lt;T&gt;</c>.</summary>
        Async,
        /// <summary>An async lambda returning a <c>ValueTask&lt;T&gt;</c>.</summary>
        AsyncValue,
        /// <summary>A method group of the fixture.</summary>
        Group,
        /// <summary>A delegate an unseen method returns.</summary>
        Unseen,
        /// <summary>A visible async lambda or an unseen delegate.</summary>
        Mixed
    }

    /// <summary>What f's body returns.</summary>
    public enum Source
    {
        Known,
        Opaque,
        Mixed,
        /// <summary>A user iterator, through <c>Task.FromResult</c> or an async body.</summary>
        Iterator
    }

    /// <summary>How the value is taken out.</summary>
    public enum Consumer
    {
        Await,
        Result,
        /// <summary>The call's result itself, or a call enumerating at once.</summary>
        Direct,
        Foreach,
        /// <summary><c>foreach</c> over the tasks, each awaited.</summary>
        ForeachAwait,
        /// <summary><c>Task.WhenAll</c> over the tasks, its first cell.</summary>
        WhenAll
    }

    private static IEnumerable<(Form Form, Kind Kind, Source Source, Consumer Consumer)> Combinations()
    {
        Source[] values = [Source.Known, Source.Opaque, Source.Mixed];
        foreach (var form in Enum.GetValues<Form>())
        {
            var (kinds, consumers) = form switch
            {
                Form.NowValue => ([Kind.Sync, Kind.Group, Kind.Unseen, Kind.Mixed], [Consumer.Await, Consumer.Result]),
                Form.NowCompletion => (Enum.GetValues<Kind>(), [Consumer.Await, Consumer.Result]),
                Form.NowDirect => (Enum.GetValues<Kind>(), [Consumer.Direct]),
                Form.LazyCompletion => (Enum.GetValues<Kind>(), [Consumer.Foreach]),
                Form.LazyTasks or Form.Linq => ([Kind.Sync, Kind.Async, Kind.Group, Kind.Unseen, Kind.Mixed], [Consumer.ForeachAwait, Consumer.WhenAll]),
                Form.EnumNow => ([Kind.Sync, Kind.Async, Kind.Group], [Consumer.Direct]),
                Form.EnumNowTask => ([Kind.Sync, Kind.Async, Kind.Group], [Consumer.Await]),
                Form.EnumLazy => ([Kind.Sync, Kind.Async, Kind.Group], [Consumer.Foreach]),
                Form.EnumArg => (new[] { Kind.Sync }, new[] { Consumer.Direct }),
                _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
            };
            foreach (var kind in kinds)
            foreach (var source in IsEnumeration(form) ? [Source.Iterator] : kind == Kind.Unseen ? [Source.Known] : values)
            foreach (var consumer in consumers)
                yield return (form, kind, source, consumer);
        }
    }

    public static TheoryData<Form, Kind, Source, Consumer> Rows()
    {
        var rows = new TheoryData<Form, Kind, Source, Consumer>();
        foreach (var (form, kind, source, consumer) in Combinations())
            rows.Add(form, kind, source, consumer);
        return rows;
    }

    /// <summary>A value given back through f's task has the objects its twin has — f run directly, or the synchronous member over a
    /// synchronous f returning the same value — and is unfollowed where the twin is, or where f may be a delegate the analysis does not
    /// see; an iterator given back through f's task runs at the row, as its twin's does, and does not escape.</summary>
    /// <param name="form">The form around <c>returns:</c> and its fate.</param>
    /// <param name="kind">What f is.</param>
    /// <param name="source">What f's body returns.</param>
    /// <param name="consumer">How the value is taken out.</param>
    [Theory]
    [MemberData(nameof(Rows))]
    public void Value_given_back_through_a_delegate_task_is_analysed_as_its_twin(Form form, Kind kind, Source source, Consumer consumer)
    {
        var name = Name(form, kind, source, consumer);
        if (IsEnumeration(form))
        {
            // The tasks an array holds keep their iterator in the array's storage, which lets it escape, as the array of its twin keeps
            // the iterator itself (SPEC TD-060b): the two answer alike. Every other row runs both.
            var failure = IteratorFailure(name, "It_" + name)?.Replace("It_", "", StringComparison.Ordinal);
            var twinFailure = IteratorFailure(name, "Tw_" + name)?.Replace("Tw_", "", StringComparison.Ordinal);
            Assert.Equal(twinFailure, failure);
            if (form != Form.EnumArg)
                Assert.Null(failure);
            return;
        }

        var twin = Observed(name, "ObserveSource");
        var consumed = Observed(name, "ObserveConsumed");
        Assert.True(twin.SetEquals(consumed), $"{name}: consumed [{string.Join(", ", consumed.Order())}], twin [{string.Join(", ", twin.Order())}]");
        var handed = Unfollowed(name, "ObserveSource");
        var expected = handed || kind is Kind.Unseen or Kind.Mixed;
        var actual = Unfollowed(name, "ObserveConsumed");
        Assert.True(actual == expected, $"{name}: the consumed value is unfollowed {actual}, its twin {handed}, expected {expected}");
    }

    // ---- observing ----

    private HeapSolution Heap => cache.Get("run", Analyze).Heap;

    private MethodInstance[] Instances(string method) =>
        Heap.Instances.Values.Where(instance => instance.BodyId == PREFIX + method ||
                                                instance.BodyId.StartsWith(PREFIX + method + "#", StringComparison.Ordinal)).ToArray();

    private IEnumerable<(string Instance, CallArgument Argument)> Arguments(string method, string observer)
    {
        var arguments = Instances(method).SelectMany(instance => instance.Summary.OpaqueCalls
                                                                         .Where(call => call.Callee.Contains($"Lib.{observer}", StringComparison.Ordinal))
                                                                         .SelectMany(call => call.Arguments.Select(argument => (instance.Id, argument))))
                                         .ToArray();
        Assert.True(arguments.Length > 0, $"{method}: no call of {observer}");
        return arguments;
    }

    /// <summary>The objects the value handed to the observing call may be.</summary>
    /// <param name="method">The row's method.</param>
    /// <param name="observer">The observing library member.</param>
    private HashSet<string> Observed(string method, string observer) =>
        Arguments(method, observer).SelectMany(item => Heap.Resolve(item.Instance, item.Argument.Values)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether the value handed to the observing call may be an object the heap does not follow, as the engine's
    /// <c>Unfollowed</c> answers it from the argument's unknown sources, source calls and completions.</summary>
    /// <param name="method">The row's method.</param>
    /// <param name="observer">The observing library member.</param>
    private bool Unfollowed(string method, string observer) =>
        Arguments(method, observer).Any(item => item.Argument.UnknownSources.Any(unknown => unknown is not (UnknownSource.Null or UnknownSource.FieldBeforeWrite or UnknownSource.SourceCall)) ||
                                                item.Argument.SourceCalls.Any(call => Heap.UnfollowedCallResults.Contains((item.Instance, call))) ||
                                                item.Argument.Completions.Any(completion => Heap.UnfollowedCompletions.Contains((item.Instance, completion))));

    /// <summary>Why an iterator of the fixture does not run at the row, made there or by its lambdas and enumerated by them, without
    /// escaping; null where it does.</summary>
    /// <param name="method">The row's method.</param>
    /// <param name="iterator">The iterator method's name.</param>
    private string? IteratorFailure(string method, string iterator)
    {
        var callers = Instances(method).Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal);
        var made = Heap.IteratorObjects.Where(item => Heap.Instances[item.Creation.CalleeInstance].BodyId == PREFIX + iterator).ToArray();
        if (made.Length == 0)
            return $"{iterator} is never made";
        if (made.Any(item => Heap.UnknownIterators.Contains(item.RegionId)))
            return $"{iterator} escapes";
        return made.Any(item => Heap.ExecutionEdges.Any(edge => edge.CalleeInstance == item.Creation.CalleeInstance && callers.Contains(edge.CallerInstance)))
            ? null
            : $"{iterator} is not enumerated at the row";
    }

    // ---- the library ----

    private const string LIBRARY = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        namespace TaskLib
        {
            public static class Lib
            {
                public static void ObserveSource(object value) { }
                public static void ObserveConsumed(object value) { }
                public static Task<T> NowValue<T>(Func<T> f) => null!;
                public static Task<T> NowCompletion<T>(Func<Task<T>> f) => null!;
                public static Task<T> NowCompletionV<T>(Func<ValueTask<T>> f) => null!;
                public static T NowDirect<T>(Func<Task<T>> f) => default!;
                public static T NowDirectV<T>(Func<ValueTask<T>> f) => default!;
                public static IEnumerable<T> LazyCompletion<T>(Func<Task<T>> f) => null!;
                public static IEnumerable<T> LazyCompletionV<T>(Func<ValueTask<T>> f) => null!;
                public static IEnumerable<Task<T>> LazyTasks<T>(Func<Task<T>> f) => null!;
                public static IEnumerable<T> TwinLazy<T>(Func<T> f) => null!;
                public static List<T> EnumNow<T>(Func<Task<IEnumerable<T>>> f) => null!;
                public static Task<List<T>> EnumNowTask<T>(Func<Task<IEnumerable<T>>> f) => null!;
                public static IEnumerable<T> EnumLazy<T>(Func<Task<IEnumerable<T>>> f) => null!;
                public static List<T> EnumArg<T>(IEnumerable<Task<IEnumerable<T>>> ts) => null!;
                public static List<T> TwinEnumArg<T>(IEnumerable<IEnumerable<T>> xs) => null!;
                public static List<T> TwinEnumNow<T>(Func<IEnumerable<T>> f) => null!;
                public static IEnumerable<T> TwinEnumLazy<T>(Func<IEnumerable<T>> f) => null!;
            }
        }
        """;

    private const string NOW = ",\"fates\":{\"f\":{\"fate\":\"invoke-now\",\"inputs\":[]}}";

    private const string LAZY = ",\"fates\":{\"f\":{\"fate\":\"iterator\",\"inputs\":[]}}";

    private static readonly (string Member, string Decision)[] Entries =
    [
        ("ObserveSource", ""),
        ("ObserveConsumed", ""),
        ("NowValue", "\"result\":\"task([returns:f])\"" + NOW),
        ("NowCompletion", "\"result\":\"task([completion(returns:f)])\"" + NOW),
        ("NowCompletionV", "\"result\":\"task([completion(returns:f)])\"" + NOW),
        ("NowDirect", "\"result\":\"[completion(returns:f)]\"" + NOW),
        ("NowDirectV", "\"result\":\"[completion(returns:f)]\"" + NOW),
        ("LazyCompletion", "\"result\":\"sequence(completion(returns:f))\"" + LAZY),
        ("LazyCompletionV", "\"result\":\"sequence(completion(returns:f))\"" + LAZY),
        ("LazyTasks", "\"result\":\"sequence(returns:f)\"" + LAZY),
        ("TwinLazy", "\"result\":\"sequence(returns:f)\"" + LAZY),
        ("EnumNow", "\"result\":\"collection(elements(completion(returns:f)))\"" + NOW),
        ("EnumNowTask", "\"result\":\"task(collection(elements(completion(returns:f))))\"" + NOW),
        ("EnumLazy", "\"result\":\"sequence(elements(completion(returns:f)))\"" + LAZY),
        ("EnumArg", "\"result\":\"collection(elements(completion(elements(arg:ts))))\""),
        ("TwinEnumArg", "\"result\":\"collection(elements(elements(arg:xs)))\""),
        ("TwinEnumNow", "\"result\":\"collection(elements(returns:f))\"" + NOW),
        ("TwinEnumLazy", "\"result\":\"sequence(elements(returns:f))\"" + LAZY)
    ];

    // ---- the fixture ----

    private static bool IsEnumeration(Form form) => form is Form.EnumNow or Form.EnumNowTask or Form.EnumLazy or Form.EnumArg;

    private static string Name(Form form, Kind kind, Source source, Consumer consumer) => $"{form}_{kind}_{source}_{consumer}";

    private static string Value(Source source) => source switch
    {
        Source.Known => "Boxes.Shared",
        Source.Opaque => "Externals.MakeBox()",
        Source.Mixed => $"({FLAG} ? Boxes.Shared : Externals.MakeBox())",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
    };

    /// <summary>The members of one row: its method, and the method group or iterators it names.</summary>
    /// <param name="form">The form around <c>returns:</c> and its fate.</param>
    /// <param name="kind">What f is.</param>
    /// <param name="source">What f's body returns.</param>
    /// <param name="consumer">How the value is taken out.</param>
    private static string Members(Form form, Kind kind, Source source, Consumer consumer)
    {
        var name = Name(form, kind, source, consumer);
        var text = new StringBuilder();
        if (IsEnumeration(form))
        {
            text.Append($"private static IEnumerable<Box> It_{name}() {{ yield return Boxes.Shared; }}\n");
            text.Append($"private static IEnumerable<Box> Tw_{name}() {{ yield return Boxes.Shared; }}\n");
            text.Append($"private static async Task<IEnumerable<Box>> G_{name}() {{ await Task.Yield(); return It_{name}(); }}\n");
            var f = kind switch
            {
                Kind.Sync => $"() => Task.FromResult<IEnumerable<Box>>(It_{name}())",
                Kind.Async => $"async () => {{ await Task.Yield(); return It_{name}(); }}",
                Kind.Group => $"G_{name}",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
            var body = form switch
            {
                Form.EnumNow => $"GC.KeepAlive(TaskLib.Lib.EnumNow<Box>(f));\nGC.KeepAlive(TaskLib.Lib.TwinEnumNow<Box>(g));\n",
                Form.EnumNowTask => $"GC.KeepAlive(await TaskLib.Lib.EnumNowTask<Box>(f));\nGC.KeepAlive(TaskLib.Lib.TwinEnumNow<Box>(g));\n",
                Form.EnumLazy => "foreach (var item in TaskLib.Lib.EnumLazy<Box>(f)) item.Touch();\n" +
                                 "foreach (var item in TaskLib.Lib.TwinEnumLazy<Box>(g)) item.Touch();\n",
                Form.EnumArg => $"GC.KeepAlive(TaskLib.Lib.EnumArg<Box>(new[] {{ Task.FromResult<IEnumerable<Box>>(It_{name}()) }}));\n" +
                                $"GC.KeepAlive(TaskLib.Lib.TwinEnumArg<Box>(new[] {{ Tw_{name}() }}));\n",
                _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
            };
            text.Append($"public async Task {name}()\n{{\nFunc<Task<IEnumerable<Box>>> f = {f};\nFunc<IEnumerable<Box>> g = () => Tw_{name}();\n{body}}}\n\n");
            return text.ToString();
        }

        var x = Value(source);
        var selector = form == Form.Linq;
        var parameter = selector ? "i" : "()";
        var parameterType = selector ? "int, " : "";
        // f, and the synchronous g its twin runs, returning the same value.
        string type, fValue, gValue;
        var gType = $"Func<{parameterType}Box>";
        var unseenSync = selector ? "Externals.MakeSyncSelector()" : "Externals.MakeFunc()";
        gValue = kind switch
        {
            Kind.Unseen => unseenSync,
            Kind.Mixed => $"{FLAG} ? ({gType})({parameter} => {x}) : {unseenSync}",
            _ => $"{parameter} => {x}"
        };
        if (form == Form.NowValue)
        {
            type = "Func<Box>";
            text.Append($"private static Box G_{name}() => {x};\n");
            fValue = kind switch
            {
                Kind.Sync => $"() => {x}",
                Kind.Group => $"G_{name}",
                Kind.Unseen => "Externals.MakeFunc()",
                Kind.Mixed => $"{FLAG} ? (Func<Box>)(() => {x}) : Externals.MakeFunc()",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
        }
        else
        {
            var task = kind == Kind.AsyncValue ? "ValueTask" : "Task";
            type = $"Func<{parameterType}{task}<Box>>";
            var groupParameter = selector ? "int i" : "";
            text.Append($"private static async Task<Box> G_{name}({groupParameter}) {{ await Task.Yield(); return {x}; }}\n");
            var unseen = selector ? "Externals.MakeSelector()" : kind == Kind.AsyncValue ? "Externals.MakeValueWork()" : "Externals.MakeWork()";
            fValue = kind switch
            {
                Kind.Sync => $"{parameter} => Task.FromResult<Box>({x})",
                Kind.Async or Kind.AsyncValue => $"async {parameter} => {{ await Task.Yield(); return {x}; }}",
                Kind.Group => $"G_{name}",
                Kind.Unseen => unseen,
                Kind.Mixed => $"{FLAG} ? ({type})(async {parameter} => {{ await Task.Yield(); return {x}; }}) : {unseen}",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
        }

        var v = kind == Kind.AsyncValue ? "V" : "";
        var route = (form, consumer) switch
        {
            (Form.NowValue, Consumer.Await) => "var consumed = await TaskLib.Lib.NowValue<Box>(f);\nTaskLib.Lib.ObserveConsumed(consumed);\n",
            (Form.NowValue, Consumer.Result) => "var consumed = TaskLib.Lib.NowValue<Box>(f).Result;\nTaskLib.Lib.ObserveConsumed(consumed);\n",
            (Form.NowCompletion, Consumer.Await) => $"var consumed = await TaskLib.Lib.NowCompletion{v}<Box>(f);\nTaskLib.Lib.ObserveConsumed(consumed);\n",
            (Form.NowCompletion, Consumer.Result) => $"var consumed = TaskLib.Lib.NowCompletion{v}<Box>(f).Result;\nTaskLib.Lib.ObserveConsumed(consumed);\n",
            (Form.NowDirect, _) => $"var consumed = TaskLib.Lib.NowDirect{v}<Box>(f);\nTaskLib.Lib.ObserveConsumed(consumed);\n",
            (Form.LazyCompletion, _) => $"foreach (var consumed in TaskLib.Lib.LazyCompletion{v}<Box>(f)) TaskLib.Lib.ObserveConsumed(consumed);\n",
            (Form.LazyTasks, Consumer.ForeachAwait) => "foreach (var t in TaskLib.Lib.LazyTasks<Box>(f)) { var consumed = await t; TaskLib.Lib.ObserveConsumed(consumed); }\n",
            (Form.LazyTasks, Consumer.WhenAll) => "var all = await Task.WhenAll(TaskLib.Lib.LazyTasks<Box>(f));\nvar consumed = all[0];\nTaskLib.Lib.ObserveConsumed(consumed);\n",
            (Form.Linq, Consumer.ForeachAwait) => "foreach (var t in System.Linq.Enumerable.Select(new[] { 1 }, f)) { var consumed = await t; TaskLib.Lib.ObserveConsumed(consumed); }\n",
            (Form.Linq, Consumer.WhenAll) => "var all = await Task.WhenAll(System.Linq.Enumerable.Select(new[] { 1 }, f));\nvar consumed = all[0];\nTaskLib.Lib.ObserveConsumed(consumed);\n",
            _ => throw new ArgumentOutOfRangeException(nameof(consumer), consumer, null)
        };
        // The twin: f run directly where the route takes one value of one task, else the synchronous member over g.
        var twin = (form, consumer) switch
        {
            (Form.NowValue, _) => "var source = f();\nTaskLib.Lib.ObserveSource(source);\n",
            (Form.NowCompletion or Form.NowDirect, _) => "var source = await f();\nTaskLib.Lib.ObserveSource(source);\n",
            (Form.LazyCompletion, _) or (Form.LazyTasks, Consumer.ForeachAwait) =>
                "foreach (var source in TaskLib.Lib.TwinLazy<Box>(g)) TaskLib.Lib.ObserveSource(source);\n",
            (Form.LazyTasks, Consumer.WhenAll) => "var source = System.Linq.Enumerable.ToArray(TaskLib.Lib.TwinLazy<Box>(g))[0];\nTaskLib.Lib.ObserveSource(source);\n",
            (Form.Linq, Consumer.ForeachAwait) => "foreach (var source in System.Linq.Enumerable.Select(new[] { 1 }, g)) TaskLib.Lib.ObserveSource(source);\n",
            (Form.Linq, Consumer.WhenAll) => "var source = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(new[] { 1 }, g))[0];\nTaskLib.Lib.ObserveSource(source);\n",
            _ => throw new ArgumentOutOfRangeException(nameof(consumer), consumer, null)
        };
        text.Append($"public async Task {name}()\n{{\n{type} f = {fValue};\n{gType} g = {gValue};\n{twin}{route}}}\n\n");
        return text.ToString();
    }

    private static string FixtureSource()
    {
        var text = new StringBuilder("""
            public class Box { public int Count; public virtual void Touch() { } }
            public static class Boxes { public static readonly Box Shared = new Box(); }

            public static class Externals
            {
                public static extern Box MakeBox();
                public static extern Func<Box> MakeFunc();
                public static extern Func<Task<Box>> MakeWork();
                public static extern Func<ValueTask<Box>> MakeValueWork();
                public static extern Func<int, Task<Box>> MakeSelector();
                public static extern Func<int, Box> MakeSyncSelector();
            }

            public sealed class Rows
            {

            """);
        var names = new List<string>();
        foreach (var (form, kind, source, consumer) in Combinations())
        {
            names.Add(Name(form, kind, source, consumer));
            text.Append(Members(form, kind, source, consumer));
        }

        text.Append("}\n\npublic sealed class RowsController : ControllerBase\n{\n");
        foreach (var name in names)
            text.Append($"    public async Task {name}() => await new Rows().{name}();\n");
        text.Append("}\n");
        return text.ToString();
    }

    private static HeapRun Analyze()
    {
        var library = TaskModelFixture.Library(LIBRARY);
        return TaskModelFixture.Solve(FixtureSource(), library, Entries.Select(entry => (TaskModelFixture.Id(library.Compilation, "Lib", entry.Member), entry.Decision)));
    }
}
