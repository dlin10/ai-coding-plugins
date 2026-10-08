using System.Text;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Whether what a modeled call's task completes with may be an object the analysis does not follow, over every model value form
/// a task's completion can name (R1, R2): a value consumed through the task is unfollowed exactly when its synchronous twin is — the
/// source handed over directly for a value the model names, the same model's synchronous member for an object the call creates, which is
/// followed whatever it is built of, its elements read as the twin's are. The axes are the form and the place that makes the completion,
/// the source (known, opaque, or either), the depth and outer type of the task, and the consumer. One fixture of project-model members,
/// analysed once, with one method per row.</summary>
public sealed class TaskCompletionProvenanceTests(ClassCache cache) : IClassFixture<ClassCache>
{
    private const string FLAG = "Environment.ProcessorCount > 1";

    private const string PREFIX = "body:Fixture:M:Sweep.";

    /// <summary>The model value form a task completes with, and the place that makes the completion.</summary>
    public enum Form
    {
        /// <summary><c>task([arg:x])</c>.</summary>
        Argument,
        /// <summary><c>task([returns:f])</c> of an invoke-now delegate returning the source.</summary>
        Returns,
        /// <summary><c>task([kept:k])</c> after a synchronous call kept the source in k.</summary>
        Kept,
        /// <summary><c>task([kept:k])</c> of a k that a <c>task(new)</c> made, whose <c>keeps.result</c> kept the source.</summary>
        KeptResult,
        /// <summary><c>task(collection(elements(arg:xs)))</c>.</summary>
        Collection,
        /// <summary><c>task(sequence(elements(arg:xs)))</c>.</summary>
        Sequence,
        /// <summary><c>task([completion(arg:t)])</c>.</summary>
        Completion,
        /// <summary>The holder a <c>holder</c> <c>result</c> fate makes, a new object whatever the source, of a member silent about its
        /// result.</summary>
        Holder,
        /// <summary><c>task(new)</c>, beside an argument.</summary>
        New,
        /// <summary><c>task(dictionary(elements(arg:xs),elements(arg:xs)))</c>.</summary>
        Dictionary
    }

    /// <summary>What the source is.</summary>
    public enum Source
    {
        Known,
        Opaque,
        Mixed
    }

    /// <summary>How each level of the task is consumed.</summary>
    public enum Consumer
    {
        Await,
        Result
    }

    /// <summary>The task types around the value: <c>Task</c> or <c>ValueTask</c> outside, one or two levels.</summary>
    private static readonly string[] Shapes = ["T1", "V1", "T2", "V2"];

    private static IEnumerable<(Form Form, Source Source, string Shape, Consumer Consumer)> Combinations() =>
        from form in Enum.GetValues<Form>()
        from source in Enum.GetValues<Source>()
        from shape in Shapes
        from consumer in Enum.GetValues<Consumer>()
        select (form, source, shape, consumer);

    public static TheoryData<Form, Source, string, Consumer> Rows()
    {
        var rows = new TheoryData<Form, Source, string, Consumer>();
        foreach (var (form, source, shape, consumer) in Combinations())
            rows.Add(form, source, shape, consumer);
        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Completion_value_is_unfollowed_exactly_when_its_source_is(Form form, Source source, string shape, Consumer consumer)
    {
        var name = Name(form, source, shape, consumer);
        var handed = Unfollowed(name, "ObserveSource");
        Assert.True(handed == (source != Source.Known), $"{name}: the source handed over directly is unfollowed {handed}");
        var twin = Unfollowed(name, "ObserveTwin");
        // An object the call creates is followed whatever it is built of, as its synchronous twin is.
        if (Creates(form))
            Assert.False(twin, $"{name}: the synchronous twin's created object is unfollowed");
        var consumed = Unfollowed(name, "ObserveConsumed");
        // A holder is a new object the call makes, whatever the source; its member's model is silent about the result, which leaves the
        // value unknown beside the holder (R4), so it is unfollowed whatever the source.
        var expected = form == Form.Holder || twin;
        Assert.True(consumed == expected, $"{name}: the consumed value is unfollowed {consumed}, its twin {expected}");
        if (HasElements(form))
        {
            var twinElement = Unfollowed(name, "ObserveTwinElement");
            var element = Unfollowed(name, "ObserveElement");
            Assert.True(element == twinElement, $"{name}: an element of the consumed value is unfollowed {element}, the twin's {twinElement}");
        }
    }

    /// <summary>Whether the form's call creates the object its task completes with.</summary>
    /// <param name="form">The form.</param>
    private static bool Creates(Form form) => form is Form.Collection or Form.Sequence or Form.Dictionary or Form.New;

    /// <summary>Whether the object the form's task completes with has elements a row reads.</summary>
    /// <param name="form">The form.</param>
    private static bool HasElements(Form form) => form is Form.Collection or Form.Sequence or Form.Dictionary;

    // ---- observing ----

    /// <summary>Whether the value handed to the observing call may be an object the heap does not follow, as the engine's
    /// <c>Unfollowed</c> answers it from the argument's unknown sources, source calls and completions.</summary>
    /// <param name="method">The row's method.</param>
    /// <param name="observer">The observing library member.</param>
    private bool Unfollowed(string method, string observer)
    {
        var heap = cache.Get("run", Analyze).Heap;
        var arguments = heap.Instances.Values
                            .Where(instance => instance.BodyId == PREFIX + method)
                            .SelectMany(instance => instance.Summary.OpaqueCalls
                                                            .Where(call => call.Callee.Contains($"Lib.{observer}(", StringComparison.Ordinal))
                                                            .SelectMany(call => call.Arguments.Select(argument => (instance.Id, Argument: argument))))
                            .ToArray();
        Assert.True(arguments.Length > 0, $"{method}: no call of {observer}");
        return arguments.Any(item => item.Argument.UnknownSources.Any(unknown => unknown is not (UnknownSource.Null or UnknownSource.FieldBeforeWrite or UnknownSource.SourceCall)) ||
                                     item.Argument.SourceCalls.Any(call => heap.UnfollowedCallResults.Contains((item.Id, call))) ||
                                     item.Argument.Completions.Any(completion => heap.UnfollowedCompletions.Contains((item.Id, completion))));
    }

    // ---- the library ----

    private static string Wrap(string type, string shape) => shape switch
    {
        "T1" => $"Task<{type}>",
        "V1" => $"ValueTask<{type}>",
        "T2" => $"Task<Task<{type}>>",
        "V2" => $"ValueTask<Task<{type}>>",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
    };

    private static string WrapResult(string result, string shape) => shape.EndsWith('1') ? $"task({result})" : $"task(task({result}))";

    private static (string Source, IReadOnlyList<(string Member, string Decision)> Entries) Library()
    {
        var text = new StringBuilder("""
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;

            namespace TaskLib
            {
                public sealed class Keeper { }

                public abstract class Runner { public abstract void Go(object value); }

                public static class Lib
                {
                    public static void ObserveSource(object value) { }
                    public static void ObserveTwin(object value) { }
                    public static void ObserveConsumed(object value) { }
                    public static void ObserveTwinElement(object value) { }
                    public static void ObserveElement(object value) { }
                    public static void Keep<T>(Keeper k, T x) { }
                    public static List<T> CollectionSync<T>(T[] xs) => default!;
                    public static IEnumerable<T> SequenceSync<T>(T[] xs) => default!;
                    public static Dictionary<T, T> DictionarySync<T>(T[] xs) => default!;
                    public static Keeper NewSync<T>(T x) => default!;

            """);
        var entries = new List<(string Member, string Decision)>
        {
            ("ObserveSource", ""),
            ("ObserveTwin", ""),
            ("ObserveConsumed", ""),
            ("ObserveTwinElement", ""),
            ("ObserveElement", ""),
            ("Keep", "\"keeps\":{\"k\":[\"arg:x\"]}"),
            ("CollectionSync", "\"result\":\"collection(elements(arg:xs))\""),
            ("SequenceSync", "\"result\":\"sequence(elements(arg:xs))\""),
            ("DictionarySync", "\"result\":\"dictionary(elements(arg:xs),elements(arg:xs))\""),
            ("NewSync", "\"result\":\"new\"")
        };
        foreach (var shape in Shapes)
        {
            text.Append($"        public static {Wrap("T", shape)} Argument{shape}<T>(T x) => default!;\n");
            entries.Add(($"Argument{shape}", $"\"result\":\"{WrapResult("[arg:x]", shape)}\""));
            text.Append($"        public static {Wrap("T", shape)} Returns{shape}<T>(Func<T> f) => default!;\n");
            entries.Add(($"Returns{shape}", $"\"result\":\"{WrapResult("[returns:f]", shape)}\",\"fates\":{{\"f\":{{\"fate\":\"invoke-now\",\"inputs\":[]}}}}"));
            text.Append($"        public static {Wrap("object", shape)} Kept{shape}(Keeper k) => default!;\n");
            entries.Add(($"Kept{shape}", $"\"result\":\"{WrapResult("[kept:k]", shape)}\""));
            text.Append($"        public static {Wrap("Keeper", shape)} Made{shape}<T>(T x) => default!;\n");
            entries.Add(($"Made{shape}", $"\"result\":\"{WrapResult("new", shape)}\",\"keeps\":{{\"result\":[\"arg:x\"]}}"));
            text.Append($"        public static {Wrap("List<T>", shape)} Collection{shape}<T>(T[] xs) => default!;\n");
            entries.Add(($"Collection{shape}", $"\"result\":\"{WrapResult("collection(elements(arg:xs))", shape)}\""));
            text.Append($"        public static {Wrap("IEnumerable<T>", shape)} Sequence{shape}<T>(T[] xs) => default!;\n");
            entries.Add(($"Sequence{shape}", $"\"result\":\"{WrapResult("sequence(elements(arg:xs))", shape)}\""));
            text.Append($"        public static {Wrap("T", shape)} Completion{shape}<T>(Task<T> t) => default!;\n");
            entries.Add(($"Completion{shape}", $"\"result\":\"{WrapResult("[completion(arg:t)]", shape)}\""));
            text.Append($"        public static {Wrap("Runner", shape)} Holder{shape}(Action<object> action) => default!;\n");
            entries.Add(($"Holder{shape}", "\"fates\":{\"action\":{\"fate\":\"holder\",\"holder\":\"result\",\"inputs\":[[\"holder-arg:0\"]]}}"));
            text.Append($"        public static {Wrap("Keeper", shape)} New{shape}<T>(T x) => default!;\n");
            entries.Add(($"New{shape}", $"\"result\":\"{WrapResult("new", shape)}\""));
            text.Append($"        public static {Wrap("Dictionary<T, T>", shape)} Dictionary{shape}<T>(T[] xs) => default!;\n");
            entries.Add(($"Dictionary{shape}", $"\"result\":\"{WrapResult("dictionary(elements(arg:xs),elements(arg:xs))", shape)}\""));
        }

        text.Append("    }\n}\n");
        return (text.ToString(), entries);
    }

    // ---- the fixture ----

    private static string Name(Form form, Source source, string shape, Consumer consumer) => $"{form}_{source}_{shape}_{consumer}";

    private static string Value(Source source, bool array) => (source, array) switch
    {
        (Source.Known, false) => "Boxes.Shared",
        (Source.Opaque, false) => "Externals.MakeBox()",
        (Source.Mixed, false) => $"({FLAG} ? Boxes.Shared : Externals.MakeBox())",
        (Source.Known, true) => "new Box[] { Boxes.Shared }",
        (Source.Opaque, true) => "Externals.MakeBoxes()",
        (Source.Mixed, true) => $"({FLAG} ? new Box[] {{ Boxes.Shared }} : Externals.MakeBoxes())",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
    };

    /// <summary>A read of each element of a value of the form's type, each handed to the observer.</summary>
    /// <param name="form">The form.</param>
    /// <param name="value">The value.</param>
    /// <param name="observer">The observing library member.</param>
    private static string ReadElements(Form form, string value, string observer) => form switch
    {
        Form.Collection => $"TaskLib.Lib.{observer}({value}[0]);\n",
        Form.Sequence => $"foreach (var e in {value}) TaskLib.Lib.{observer}(e);\n",
        Form.Dictionary => $"foreach (var e in {value}.Values) TaskLib.Lib.{observer}(e);\n",
        _ => ""
    };

    /// <summary>The body of one row: the source, observed as handed over directly; the synchronous twin, observed with its elements;
    /// the call making the task; each level consumed; and the consumed value observed with its elements.</summary>
    /// <param name="form">The form and place.</param>
    /// <param name="source">The source.</param>
    /// <param name="shape">The task types around the value.</param>
    /// <param name="consumer">How each level is consumed.</param>
    private static string Body(Form form, Source source, string shape, Consumer consumer)
    {
        string Consume(string task) => consumer == Consumer.Await ? $"await {task}" : $"{task}.Result";
        var array = form is Form.Collection or Form.Sequence or Form.Dictionary;
        var body = new StringBuilder($"var x = {Value(source, array)};\nTaskLib.Lib.ObserveSource(x);\n");
        // The twin is the same value with the task taken out: the source handed over directly, or the model's synchronous member.
        var twin = form switch
        {
            Form.Collection => "TaskLib.Lib.CollectionSync<Box>(x)",
            Form.Sequence => "TaskLib.Lib.SequenceSync<Box>(x)",
            Form.Dictionary => "TaskLib.Lib.DictionarySync<Box>(x)",
            Form.New => "TaskLib.Lib.NewSync<Box>(x)",
            _ => "x"
        };
        body.Append($"var twin = {twin};\nTaskLib.Lib.ObserveTwin(twin);\n").Append(ReadElements(form, "twin", "ObserveTwinElement"));
        var call = form switch
        {
            Form.Argument => $"TaskLib.Lib.Argument{shape}<Box>(x)",
            Form.Returns => $"TaskLib.Lib.Returns{shape}<Box>(() => {Value(source, false)})",
            Form.Kept => $"TaskLib.Lib.Kept{shape}(k)",
            Form.KeptResult => $"TaskLib.Lib.Made{shape}<Box>(x)",
            Form.Collection => $"TaskLib.Lib.Collection{shape}<Box>(x)",
            Form.Sequence => $"TaskLib.Lib.Sequence{shape}<Box>(x)",
            Form.Completion => $"TaskLib.Lib.Completion{shape}<Box>(Task.FromResult<Box>(x))",
            Form.Holder => $"TaskLib.Lib.Holder{shape}(value => ((Box)value).Count = 1)",
            Form.New => $"TaskLib.Lib.New{shape}<Box>(x)",
            Form.Dictionary => $"TaskLib.Lib.Dictionary{shape}<Box>(x)",
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
        };
        if (form == Form.Kept)
            body.Append("var k = new TaskLib.Keeper();\nTaskLib.Lib.Keep<Box>(k, x);\n");
        body.Append($"var t = {call};\n");
        var last = "t";
        if (shape.EndsWith('2'))
        {
            body.Append($"var inner = {Consume("t")};\n");
            last = "inner";
        }

        body.Append($"var consumed = {Consume(last)};\n");
        // The keeper a task(new) made is read through a task of its own.
        if (form == Form.KeptResult)
            body.Append($"var read = {Consume("TaskLib.Lib.KeptT1(consumed)")};\nTaskLib.Lib.ObserveConsumed(read);\n");
        else
            body.Append("TaskLib.Lib.ObserveConsumed(consumed);\n").Append(ReadElements(form, "consumed", "ObserveElement"));
        return body.ToString();
    }

    private static string FixtureSource()
    {
        var text = new StringBuilder("""
            public class Box { public int Count; public virtual void Touch() { } }
            public static class Boxes { public static readonly Box Shared = new Box(); }

            public static class Externals
            {
                public static extern Box MakeBox();
                public static extern Box[] MakeBoxes();
            }

            public sealed class Sweep
            {

            """);
        var names = new List<string>();
        foreach (var (form, source, shape, consumer) in Combinations())
        {
            var name = Name(form, source, shape, consumer);
            names.Add(name);
            text.Append($"public async Task {name}()\n{{\n{Body(form, source, shape, consumer)}}}\n\n");
        }

        text.Append("}\n\npublic sealed class SweepController : ControllerBase\n{\n");
        foreach (var name in names)
            text.Append($"    public async Task {name}() => await new Sweep().{name}();\n");
        text.Append("}\n");
        return text.ToString();
    }

    private static HeapRun Analyze()
    {
        var (librarySource, entries) = Library();
        var library = TaskModelFixture.Library(librarySource);
        return TaskModelFixture.Solve(FixtureSource(), library, entries.Select(entry => (TaskModelFixture.Id(library.Compilation, "Lib", entry.Member), entry.Decision)));
    }
}
