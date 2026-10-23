using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>A holder's confirmation run collects accesses only in the executions of the trees of the driver's setup, call and
/// enumeration, where value provenance reads them, and leaves its holders and their inputs as a run that collects everything does.</summary>
public sealed class ConfirmationAccessesTests
{
    /// <summary>A holder whose member spawns in the call and whose trigger spawns too: setup, the call and the trigger each have an own
    /// execution, and the call and the trigger a child.</summary>
    private const string SPAWNING_HOLDER = """
        public sealed class Busy { private Action _a; private int _count; public Busy(Action a) { _a = a; } public void Fire() { _a(); _count++; Task.Run(() => Cache.Last = null); } }
        public static class Api { public static Busy Make(Action a) { Task.Run(() => Cache.Last = null); return new Busy(a); } }
        """;

    private const string SPAWNING_HOLDER_ID = "M:Lib.Api.Make(System.Action)";

    /// <summary>A holder of <c>this</c> whose receiver's constructor, run in setup, spawns: a child of setup.</summary>
    private const string SPAWNING_RECEIVER = """
        public sealed class Keeper { private Action _a; public Keeper() { Task.Run(() => Cache.Last = null); } public void Hold(Action a) { _a = a; } public void Fire() { _a(); } }
        """;

    private const string SPAWNING_RECEIVER_ID = "M:Lib.Keeper.Hold(System.Action)";

    /// <summary>A lazy sequence whose enumeration spawns, stored by the driver: the enumeration's own and child executions, and the
    /// escape artefact with its child.</summary>
    private const string LAZY_SPAWNING = "public static class Api { public static IEnumerable<int> Lazy(IEnumerable<int> s, Func<int, bool> p) " +
                                         "{ foreach (var x in s) { Task.Run(() => p(x)); yield return x; } } }";

    private const string LAZY_SPAWNING_ID = "M:Lib.Api.Lazy(System.Collections.Generic.IEnumerable{System.Int32},System.Func{System.Int32,System.Boolean})";

    [Fact]
    public void Each_execution_role_is_collected_as_its_readers_read_it()
    {
        var holder = Confirmed(SPAWNING_HOLDER, SPAWNING_HOLDER_ID);
        var receiver = Confirmed(SPAWNING_RECEIVER, SPAWNING_RECEIVER_ID);
        var lazy = Trace(LAZY_SPAWNING, LAZY_SPAWNING_ID);
        var unknown = Trace("public static class Api { public static void Run(Action a) { Sink.Take(a); } }", "M:Lib.Api.Run(System.Action)");
        var fire = Assert.Single(holder.Trace.Driver!.Triggers, trigger => trigger.Member == "M:Lib.Busy.Fire").Action;
        var artefact = Single(lazy.Run!, execution => execution.Kind == ExecutionKind.UnknownEnumeration);

        // No row has the artefact in an action's tree: the artefact has no parent and is the root of its own tree, and only an execution
        // another starts inherits its parent's root, so neither it nor an execution of its tree is in the tree of any action.
        Row[] rows =
        [
            new("setup own", holder.Trace, holder.Run, DriverExecutions.Own(DriverSynthesizer.SETUP),
                new DriverExecution(DriverExecutionRole.Setup, DriverSynthesizer.SETUP), DriverSynthesizer.SETUP, true),
            new("setup child", receiver.Trace, receiver.Run, SpawnOf(receiver.Run, DriverSynthesizer.SETUP),
                new DriverExecution(DriverExecutionRole.Setup, DriverSynthesizer.SETUP), DriverSynthesizer.SETUP, true),
            new("call own", holder.Trace, holder.Run, DriverExecutions.Own(DriverSynthesizer.CALL),
                new DriverExecution(DriverExecutionRole.Own, DriverSynthesizer.CALL), DriverSynthesizer.CALL, true),
            new("call child", holder.Trace, holder.Run, SpawnOf(holder.Run, DriverSynthesizer.CALL),
                new DriverExecution(DriverExecutionRole.Child, DriverSynthesizer.CALL), DriverSynthesizer.CALL, true),
            new("enumeration own", lazy, lazy.Run!, DriverExecutions.Own(DriverSynthesizer.ENUMERATE),
                new DriverExecution(DriverExecutionRole.Own, DriverSynthesizer.ENUMERATE), DriverSynthesizer.ENUMERATE, true),
            new("enumeration child", lazy, lazy.Run!, SpawnOf(lazy.Run!, DriverSynthesizer.ENUMERATE),
                new DriverExecution(DriverExecutionRole.Child, DriverSynthesizer.ENUMERATE), DriverSynthesizer.ENUMERATE, true),
            new("trigger own", holder.Trace, holder.Run, DriverExecutions.Own(fire), new DriverExecution(DriverExecutionRole.Own, fire), fire, false),
            // The trigger makes its own holder with the member, then calls Fire: the spawn of each is in its tree.
            new("trigger child", holder.Trace, holder.Run, SpawnOf(holder.Run, fire, "Lib.Busy.Fire()"), new DriverExecution(DriverExecutionRole.Child, fire), fire, false),
            new("artefact", lazy, lazy.Run!, artefact, new DriverExecution(DriverExecutionRole.Artefact, null), null, false),
            new("artefact child", lazy, lazy.Run!, Single(lazy.Run!, execution => execution.Kind == ExecutionKind.Spawn && execution.TreeRootId == artefact),
                new DriverExecution(DriverExecutionRole.Artefact, null), null, false),
            new("elsewhere", unknown, unknown.Run!, Single(unknown.Run!, execution => execution.Kind == ExecutionKind.UnknownDelegateCall),
                new DriverExecution(DriverExecutionRole.Elsewhere, null), null, false)
        ];

        // bounded: one step per row of the table.
        foreach (var row in rows)
        {
            var executions = Executions(row.Trace.Driver!, row.Run);
            var collects = ModelGenerator.ConfirmationCollects(row.Trace.Driver!, row.Run.Executions!);
            var action = ValueProvenance.ActionOf(executions, row.Execution);
            var inSetup = executions.InSetup(row.Execution);

            Assert.True(row.Role == executions.Of(row.Execution), $"{row.Name}: {executions.Of(row.Execution)}");
            Assert.True(row.TreeAction == executions.TreeActionOf(row.Execution), $"{row.Name}: {executions.TreeActionOf(row.Execution)}");
            Assert.True(row.Collected == collects(row.Execution), $"{row.Name}: collected {collects(row.Execution)}");
            Assert.True((row.Role.Role is DriverExecutionRole.Own or DriverExecutionRole.Child ? row.Role.Action : null) == action, $"{row.Name}: {action}");
            Assert.True((row.Role.Role == DriverExecutionRole.Setup) == inSetup, $"{row.Name}: in setup {inSetup}");
            // What the readers read — the call's and the enumeration's executions, and setup's — the filter collects.
            if (action is DriverSynthesizer.CALL or DriverSynthesizer.ENUMERATE || inSetup)
                Assert.True(collects(row.Execution), $"{row.Name}: read but not collected");
        }
    }

    [Fact]
    public void The_confirmation_collects_no_access_in_a_triggers_execution()
    {
        var filtered = Confirmed(SPAWNING_HOLDER, SPAWNING_HOLDER_ID);
        var everything = Confirmed(SPAWNING_HOLDER, SPAWNING_HOLDER_ID, everything: true);
        var triggers = filtered.Trace.Driver!.Triggers.Select(trigger => trigger.Action).ToHashSet(StringComparer.Ordinal);
        bool InTrigger(GenerationTrace trace, ScopeRun run, string execution) =>
            Executions(trace.Driver!, run).TreeActionOf(execution) is { } action && triggers.Contains(action);

        // Without the filter the trigger's tree has accesses of its own, and the executions step keeps them both ways: where the probe fired.
        Assert.Contains(everything.Run.Collection!.Accesses, access => InTrigger(everything.Trace, everything.Run, access.ExecutionId));
        Assert.Contains(filtered.Run.Executions!.Accesses, access => access.Executions.Any(execution => InTrigger(filtered.Trace, filtered.Run, execution)));
        Assert.DoesNotContain(filtered.Run.Collection!.Accesses, access => InTrigger(filtered.Trace, filtered.Run, access.ExecutionId));
        // Where the filter stops: the call's own accesses are collected as without it.
        Assert.Equal(everything.Run.Collection.Accesses.Where(access => !InTrigger(everything.Trace, everything.Run, access.ExecutionId))
                               .Select(access => (access.ExecutionId, access.BodyId, access.OperationId, access.Operation)),
                     filtered.Run.Collection.Accesses.Select(access => (access.ExecutionId, access.BodyId, access.OperationId, access.Operation)));
        Assert.Equal(new ClassifiedFate(FateClassifier.HOLDER, FateClassifier.RESULT), FateOf(filtered.Trace, "a"));
    }

    [Theory]
    [InlineData("public sealed class Receiver { private Action _callback; public Receiver(Action callback) { _callback = callback; } public void Fire() => _callback(); public void Quiet() { } } " +
                "public static class Api { public static Receiver Keep(Action callback) => new Receiver(callback); }", "M:Lib.Api.Keep(System.Action)")]
    [InlineData("public sealed class Result { private readonly Action<object> _take; public Result(Action<object> take) { _take = take; } public void Fire(object value) { _take(value); } } " +
                "public static class Api { public static Result Make(Action<object> take) => new Result(take); }", "M:Lib.Api.Make(System.Action{System.Object})")]
    [InlineData("public sealed class Args { } public sealed class Result { private readonly Action<Args> _take; public Result(Action<Args> take) { _take = take; } public void Fire() { _take(new Args()); } } " +
                "public static class Api { public static Result Make(Action<Args> take) => new Result(take); }", "M:Lib.Api.Make(System.Action{Lib.Args})")]
    [InlineData("public sealed class Result { private readonly object _value; private readonly Action _done; public Result(object value, Action done) { _value = value; _done = done; } public void Fire(object other) { Cache.Last = other; _done(); } } " +
                "public static class Api { public static Result Make(object value, Action done) => new Result(value, done); }", "M:Lib.Api.Make(System.Object,System.Action)")]
    [InlineData("public sealed class Made { } public sealed class Result { private readonly Made _value = new Made(); private readonly Action<Made> _take; public Result(Action<Made> take) { _take = take; } public void Fire() { _take(_value); } } " +
                "public static class Api { public static Result Make(Action<Made> take) => new Result(take); }", "M:Lib.Api.Make(System.Action{Lib.Made})")]
    [InlineData("", "M:Lib.Holder.#ctor(System.Action)")]
    [InlineData("public sealed class Narrow { public Narrow() { } public void N() { } } " +
                "public sealed class Keeper { private Action _a; public Keeper() { } public Narrow Hold(Action a) { _a = a; return new Narrow(); } public void Fire() { _a(); } }", "M:Lib.Keeper.Hold(System.Action)")]
    [InlineData("public sealed class Bus { private Action _a; public void On(Action a) { _a = a; } public void Fire() => _a(); }", "M:Lib.Bus.On(System.Action)")]
    [InlineData("public sealed class Parser { private readonly Func<int> _f; public Parser(Func<int> f) { _f = f; } public int Parse() => _f(); } " +
                "public sealed class Codec { private readonly Parser _p; public Codec(Parser p) { _p = p; } public int Read() => _p.Parse(); } " +
                "public static class Api { public static Codec For(int tag, Parser parser) => new Codec(parser); }", "M:Lib.Api.For(System.Int32,Lib.Parser)")]
    [InlineData(SPAWNING_HOLDER, SPAWNING_HOLDER_ID)]
    public void Holder_inputs_are_the_same_as_with_every_execution_collected(string types, string memberId)
    {
        var filtered = Confirmed(types, memberId);
        var everything = Confirmed(types, memberId, everything: true);
        var filteredValues = Values(filtered.Trace);
        var everythingValues = Values(everything.Trace);

        Assert.Contains(everything.Trace.Answer.Classified!.Values, fate => fate.Fate == FateClassifier.HOLDER);
        Assert.Equal(Text(everything.Trace.Answer.Classified!), Text(filtered.Trace.Answer.Classified!));
        Assert.Equal(Text(everythingValues.HolderTriggers), Text(filteredValues.HolderTriggers));
        Assert.Equal(Text(everythingValues.Inputs), Text(filteredValues.Inputs));
        Assert.Equal(everythingValues.Causes, filteredValues.Causes);
        Assert.Equal(everything.Trace.Answer.Causes, filtered.Trace.Answer.Causes);
        Assert.Equal(everything.Trace.Answer.ModelReason, filtered.Trace.Answer.ModelReason);
    }

    /// <summary>The trace of one member of a library of the prelude and <paramref name="types"/>, with its confirmation run, which
    /// finished.</summary>
    /// <param name="types">Declarations inside namespace <c>Lib</c>.</param>
    /// <param name="memberId">The member's declaration id.</param>
    /// <param name="everything">Whether the confirmation run collects the accesses of every execution.</param>
    private static (GenerationTrace Trace, ScopeRun Run) Confirmed(string types, string memberId, bool everything = false)
    {
        var library = Compile(PRELUDE + types + "\n}\n");
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        var trace = ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", memberId, null, null), library, CancellationToken.None,
                                         confirmationCollectsEverything: everything);
        Assert.True(trace.Confirmation is { Stopped: false }, $"no confirmation run: {trace.Answer.Reason}: {trace.Answer.Detail}");
        return (trace, trace.Confirmation);
    }

    /// <summary>One execution of the role table.</summary>
    /// <param name="Name">The row's name.</param>
    /// <param name="Trace">The trace whose driver the run is of.</param>
    /// <param name="Run">The run the execution is in.</param>
    /// <param name="Execution">The execution.</param>
    /// <param name="Role">Its role, with the action <see cref="DriverExecutions.Of"/> gives.</param>
    /// <param name="TreeAction">What <see cref="DriverExecutions.TreeActionOf"/> answers.</param>
    /// <param name="Collected">Whether the confirmation run collects its accesses.</param>
    private sealed record Row(string Name, GenerationTrace Trace, ScopeRun Run, string Execution, DriverExecution Role, string? TreeAction, bool Collected);

    private static DriverExecutions Executions(Driver driver, ScopeRun run)
    {
        var reachability = new HeapReachability(run.Heap!);
        var result = reachability.StaticField(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.KEEP_TYPE, "R"));
        return new DriverExecutions(driver, run.Executions!, reachability.From(result is null ? [] : reachability.Targets(result)));
    }

    private static ValueProvenance Values(GenerationTrace trace) =>
        new(trace.Driver!, trace.Run!, trace.Answer.Classified!, trace.Confirmation);

    /// <summary>The one spawn whose parent is an action's own execution, made in a body when one is named.</summary>
    /// <param name="run">The run.</param>
    /// <param name="action">The action.</param>
    /// <param name="symbol">The symbol of the body whose site starts the spawn, or <c>null</c> for any.</param>
    private static string SpawnOf(ScopeRun run, string action, string? symbol = null) =>
        Single(run, execution => execution.Kind == ExecutionKind.Spawn && execution.ParentId == DriverExecutions.Own(action) &&
                                 (symbol is null || execution.Origin?.Symbol == symbol));

    private static string Single(ScopeRun run, Func<ExecutionInstance, bool> predicate) =>
        Assert.Single(run.Executions!.Executions, execution => predicate(execution)).Id;

    private static string[] Text<T>(IReadOnlyDictionary<string, T> values) =>
        values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}: {Text(pair.Value)}").ToArray();

    private static string Text(object? value) => value switch
    {
        string text => text,
        System.Collections.IEnumerable items => "[" + string.Join(", ", items.Cast<object?>().Select(Text)) + "]",
        _ => value?.ToString() ?? "null"
    };
}
