using System.Collections.Concurrent;
using System.Text;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The fate matrix (task 5 of phase 5d run B1): one source fixture library with one member per cell of three axes — how the
/// member is handed the delegate, the member's kind and result type, what it does with the delegate — compiled once and run through
/// the model generator cell by cell. Each cell's expected answer comes from <see cref="Expected"/>, written from TD-034b's fates and
/// G-5's applicability, never from running the classifier.</summary>
public sealed class FateMatrixTests
{
    private static readonly ClassifiedFate InvokeNow = new(FateClassifier.INVOKE_NOW, null);
    private static readonly ClassifiedFate Iterator = new(FateClassifier.ITERATOR, null);
    private static readonly ClassifiedFate HolderResult = new(FateClassifier.HOLDER, FateClassifier.RESULT);
    private static readonly ClassifiedFate HolderThis = new(FateClassifier.HOLDER, FateClassifier.THIS);
    private static readonly ClassifiedFate Unknown = new(FateClassifier.UNKNOWN_EXECUTION, null);
    private static readonly ClassifiedFate NotRun = new(FateClassifier.NOT_RUN, null);

    private const string IN_TEMPORARY = "the driver hands an in delegate as a temporary the engine cannot name: invoking it is an unresolved " +
                                        "dispatch, and the probe is never followed into the member";

    /// <summary>The shapes a task handing takes: one per result kind and one task result. Every cell costs about 0.19 s of every
    /// suite run, so the member's shape is crossed with <see cref="Handing.Value"/> and <see cref="Handing.Carried"/> in full, and
    /// with a task handing only here.</summary>
    private static readonly HashSet<string> TaskHandingShapes = new(["StaticVoid", "StaticReference", "StaticTaskOfReference", "StaticSequence"],
                                                                    StringComparer.Ordinal);

    /// <summary>The cells whose expected answer the engine cannot reach without changing the engine, by name, with the reason: their
    /// answer may be wider than the table, never narrower, and stays in the matrix so a change of either shows.</summary>
    private static readonly IReadOnlyDictionary<string, string> Wider = Listed(
        (IN_TEMPORARY, [
            "In/Constructor/InvokeNow", "In/Constructor/KeepInResult", "In/Constructor/KeepInThis",
            "In/InstanceReference/InvokeNow", "In/InstanceReference/KeepInResult", "In/InstanceReference/KeepInThis",
            "In/InstanceSequence/InvokeNow", "In/InstanceSequence/KeepInThis", "In/InstanceSequence/LazySequence",
            "In/InstanceTaskOfReference/InvokeNow", "In/InstanceTaskOfReference/KeepInResult", "In/InstanceTaskOfReference/KeepInThis",
            "In/InstanceTaskOfSequence/InvokeNow", "In/InstanceTaskOfSequence/KeepInThis",
            "In/InstanceTaskOfSequence/LazySequence",
            "In/InstanceValueTaskOfReference/InvokeNow", "In/InstanceValueTaskOfReference/KeepInResult", "In/InstanceValueTaskOfReference/KeepInThis",
            "In/InstanceVoid/InvokeNow", "In/InstanceVoid/KeepInThis",
            "In/StaticReference/InvokeNow", "In/StaticReference/KeepInResult",
            "In/StaticSequence/InvokeNow", "In/StaticSequence/LazySequence",
            "In/StaticTaskOfReference/InvokeNow", "In/StaticTaskOfReference/KeepInResult",
            "In/StaticTaskOfSequence/InvokeNow", "In/StaticTaskOfSequence/LazySequence",
            "In/StaticValueTaskOfReference/InvokeNow", "In/StaticValueTaskOfReference/KeepInResult",
            "In/StaticVoid/InvokeNow",
            "In/StaticVoid/DoNothing", "In/StaticReference/DoNothing", "In/StaticTaskOfReference/DoNothing", "In/StaticValueTaskOfReference/DoNothing",
            "In/StaticSequence/DoNothing", "In/StaticTaskOfSequence/DoNothing", "In/InstanceVoid/DoNothing", "In/InstanceReference/DoNothing",
            "In/InstanceTaskOfReference/DoNothing", "In/InstanceValueTaskOfReference/DoNothing", "In/InstanceSequence/DoNothing",
            "In/InstanceTaskOfSequence/DoNothing", "In/Constructor/DoNothing",
            "In/StaticVoid/CompareOnly", "In/StaticReference/CompareOnly", "In/StaticTaskOfReference/CompareOnly", "In/StaticValueTaskOfReference/CompareOnly",
            "In/StaticSequence/CompareOnly", "In/StaticTaskOfSequence/CompareOnly", "In/InstanceVoid/CompareOnly", "In/InstanceReference/CompareOnly",
            "In/InstanceTaskOfReference/CompareOnly", "In/InstanceValueTaskOfReference/CompareOnly", "In/InstanceSequence/CompareOnly",
            "In/InstanceTaskOfSequence/CompareOnly", "In/Constructor/CompareOnly",
            "In/InstanceVoid/RemoveFromThis", "In/InstanceReference/RemoveFromThis", "In/InstanceTaskOfReference/RemoveFromThis",
            "In/InstanceValueTaskOfReference/RemoveFromThis", "In/InstanceSequence/RemoveFromThis", "In/InstanceTaskOfSequence/RemoveFromThis",
            "In/Constructor/RemoveFromThis",
            "In/InstanceVoid/CombineIntoThis", "In/InstanceReference/CombineIntoThis", "In/InstanceTaskOfReference/CombineIntoThis",
            "In/InstanceValueTaskOfReference/CombineIntoThis", "In/InstanceSequence/CombineIntoThis", "In/InstanceTaskOfSequence/CombineIntoThis",
            "In/Constructor/CombineIntoThis"
        ]));

    private static readonly Lazy<Matrix> Results = new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>How the member is handed the delegate.</summary>
    public enum Handing
    {
        Value,
        In,
        Ref,
        Carried,
        CarriedSetupInvokes,
        CarriedSetupHandsToOpaqueCall,
        TwoCarried,

        /// <summary>The delegate handed as a <c>Task&lt;Action&gt;</c>; its twin is <see cref="Value"/>.</summary>
        TaskOfValue,

        /// <summary>The delegate handed as a <c>ValueTask&lt;Action&gt;</c>; its twin is <see cref="Value"/>.</summary>
        ValueTaskOfValue,

        /// <summary>A <c>Task&lt;Carrier&gt;</c>, the carrier holding the delegate as <see cref="Carried"/>'s does; its twin is
        /// <see cref="Carried"/>.</summary>
        TaskOfCarried,

        /// <summary>A <c>ValueTask&lt;Carrier&gt;</c>; its twin is <see cref="Carried"/>.</summary>
        ValueTaskOfCarried
    }

    /// <summary>What the member does with the delegate.</summary>
    public enum Act
    {
        InvokeNow,
        InvokeBeforeFirstAwait,
        InvokeInTaskRun,
        InvokeInTimerCallback,
        HandToOpaqueCall,
        HandToProbeEquals,
        KeepInResult,
        KeepInThis,
        LazySequence,
        KeepInLibraryStatic,
        StoreIntoOtherArgument,
        StoreIntoOtherSlot,
        AssignToOut,
        KeepInResultAndHandResult,
        KeepInThisAndHandThis,
        KeepInResultAndThis,
        DoNothing,
        InvokeNowAndKeepInResult,
        LazySequenceStartingTaskRun,
        InvokeOneKeepOther,
        KeepInThisBag,
        CompareOnly,
        CombineIntoThis,
        RemoveFromThis
    }

    /// <summary>What a member returns, before any task around it.</summary>
    public enum Result
    {
        None,
        Reference,
        Sequence
    }

    /// <summary>The task a member's result is wrapped in.</summary>
    public enum Wrapper
    {
        None,
        Task,
        ValueTask
    }

    /// <summary>A member's kind and result type.</summary>
    /// <param name="Name">The shape's name.</param>
    /// <param name="Instance">An instance method.</param>
    /// <param name="Constructor">A constructor.</param>
    /// <param name="Result">What the member returns, before any task around it.</param>
    /// <param name="Wrapper">The task around it.</param>
    public sealed record Shape(string Name, bool Instance, bool Constructor, Result Result, Wrapper Wrapper)
    {
        public override string ToString() => Name;
    }

    /// <summary>One cell of the matrix.</summary>
    /// <param name="Handing">How the member is handed the delegate.</param>
    /// <param name="Shape">The member's kind and result type.</param>
    /// <param name="Act">What the member does with it.</param>
    public sealed record Cell(Handing Handing, Shape Shape, Act Act)
    {
        public string Name => $"{Handing}/{Shape.Name}/{Act}";

        public string Class => $"C_{Handing}_{Shape.Name}_{Act}";

        public string Parameter => Handing is Handing.Value or Handing.In or Handing.Ref or Handing.TaskOfValue or Handing.ValueTaskOfValue ? "a" : "c";

        public override string ToString() => Name;
    }

    /// <summary>The answers of every cell, by cell.</summary>
    /// <param name="Answers">The answers.</param>
    private sealed record Matrix(IReadOnlyDictionary<Cell, GeneratedAnswer> Answers);

    private static readonly Shape[] Shapes =
    [
        new("StaticVoid", false, false, Result.None, Wrapper.None),
        new("StaticReference", false, false, Result.Reference, Wrapper.None),
        new("StaticTaskOfReference", false, false, Result.Reference, Wrapper.Task),
        new("StaticValueTaskOfReference", false, false, Result.Reference, Wrapper.ValueTask),
        new("StaticSequence", false, false, Result.Sequence, Wrapper.None),
        new("StaticTaskOfSequence", false, false, Result.Sequence, Wrapper.Task),
        new("InstanceVoid", true, false, Result.None, Wrapper.None),
        new("InstanceReference", true, false, Result.Reference, Wrapper.None),
        new("InstanceTaskOfReference", true, false, Result.Reference, Wrapper.Task),
        new("InstanceValueTaskOfReference", true, false, Result.Reference, Wrapper.ValueTask),
        new("InstanceSequence", true, false, Result.Sequence, Wrapper.None),
        new("InstanceTaskOfSequence", true, false, Result.Sequence, Wrapper.Task),
        new("Constructor", false, true, Result.None, Wrapper.None)
    ];

    private const string PRELUDE = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Lib
        {
            public static class Sink { public static extern void Take(object o); }
            public static class Cache { public static Action A; public static Action B; }
            public sealed class Box { public Action A; public Action B; }
            public sealed class Res
            {
                private readonly Action _a;
                private readonly Action _b;
                public Res(Action a, Action b) { _a = a; _b = b; }
                public void Fire() { _a(); if (_b != null) _b(); }
            }
            public sealed class SeqRes : IEnumerable<string>
            {
                private readonly Action _a;
                private readonly Action _b;
                public SeqRes(Action a, Action b) { _a = a; _b = b; }
                public void Fire() { _a(); if (_b != null) _b(); }
                public IEnumerator<string> GetEnumerator() { yield break; }
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }
            public sealed class Carrier { public Action A; public object OtherA; public Carrier(Action a) { A = a; } }
            public sealed class InvokingCarrier { public Action A; public object OtherA; public InvokingCarrier(Action a) { A = a; a(); } }
            public sealed class LeakingCarrier { public Action A; public object OtherA; public LeakingCarrier(Action a) { A = a; Sink.Take(a); } }
            public sealed class Pair
            {
                public Action A;
                public Action B;
                public object OtherA;
                public object OtherB;
                public Pair(Action a, Action b) { A = a; B = b; }
            }

        """;

    [Fact]
    public void Every_cell_has_no_unsafe_narrowing()
    {
        var unsafeCells = Results.Value.Answers.Where(pair => !IsSafe(Classified(pair.Value, pair.Key), Expected(pair.Key)))
                                 .Select(pair => $"{pair.Key.Name}: expected {Show(Expected(pair.Key))}, got {Show(Classified(pair.Value, pair.Key))}")
                                 .Order(StringComparer.Ordinal).ToArray();

        Assert.True(unsafeCells.Length == 0, $"{unsafeCells.Length} unsafe narrowing(s):\n" + string.Join("\n", unsafeCells));
    }

    [Fact]
    public void Every_cell_gets_its_expected_fate()
    {
        var failures = new List<string>();
        foreach (var (cell, answer) in Results.Value.Answers.OrderBy(pair => pair.Key.Name, StringComparer.Ordinal))
        {
            var expected = Expected(cell);
            var actual = Classified(answer, cell);
            if (Wider.TryGetValue(cell.Name, out var reason))
            {
                if (actual == expected)
                    failures.Add($"{cell.Name}: listed as wider ({reason}) but got the expected {Show(expected)}");
                else if (!IsSafe(actual, expected))
                    failures.Add($"{cell.Name}: listed as wider ({reason}) but got {Show(actual)}, narrower than {Show(expected)}");
            }
            else if (actual != expected)
                failures.Add($"{cell.Name}: expected {Show(expected)}, got {Show(actual)}");
        }

        foreach (var listed in Wider.Keys.Where(name => Results.Value.Answers.Keys.All(cell => cell.Name != name)))
            failures.Add($"{listed}: listed as wider but is no cell of the matrix");
        Assert.True(failures.Count == 0, $"{failures.Count} cell(s) off the table:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void The_matrix_covers_every_axis_value()
    {
        var ran = Results.Value.Answers.Where(pair => pair.Value.Classified is not null).Select(pair => pair.Key).ToArray();
        var missing = Enum.GetValues<Handing>().Where(handing => ran.All(cell => cell.Handing != handing)).Select(handing => $"handing {handing}")
                          .Concat(Shapes.Where(shape => ran.All(cell => cell.Shape != shape)).Select(shape => $"shape {shape.Name}"))
                          .Concat(Enum.GetValues<Act>().Where(act => ran.All(cell => cell.Act != act)).Select(act => $"act {act}"))
                          .ToArray();

        Assert.True(missing.Length == 0, "No cell that ran has " + string.Join(", ", missing));
        var refused = Results.Value.Answers.Where(pair => pair.Value.Classified is null)
                             .Select(pair => $"{pair.Key.Name}: {pair.Value.Reason} {pair.Value.Detail}").ToArray();
        Assert.True(refused.Length == 0, $"{refused.Length} cell(s) got no classification:\n" + string.Join("\n", refused));
    }

    [Fact]
    public void Every_task_handing_cell_has_an_admitted_twin()
    {
        var cells = Cells().ToHashSet();
        var orphans = cells.Where(cell => Twin(cell) is { } twin && !cells.Contains(twin)).Select(cell => cell.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Contains(cells, cell => Twin(cell) is not null);
        Assert.True(orphans.Length == 0, $"{orphans.Length} task handing cell(s) without an admitted twin:\n" + string.Join("\n", orphans));
    }

    // ---- the table ----

    /// <summary>The cell's expected answer, from TD-034b's fates and G-5's applicability: <c>invoke-now</c> for a delegate run in the
    /// call; <c>holder</c> <c>result</c> for one kept in the returned or constructed object that is no sequence — a sequence's
    /// triggers can only call <c>GetEnumerator</c>, which never runs it (R7) — <c>holder</c> <c>this</c> for one kept in
    /// the receiver of an instance method — combined into a field of it with <c>+=</c> included — that a trigger runs it from;
    /// <c>iterator</c> for a lazy sequence that runs it when enumerated; <c>not-run</c> for one the member ignores, only compares or only
    /// removes from a field with <c>-=</c> (R7); <c>unknown-execution</c> for every other cell — one kept where no member of the class
    /// runs it included (R7) — for a fate the member's kind does not admit, and for a probe that setup's construction ran or handed to
    /// a call the engine cannot follow. A task handing expects what its twin does (R6).</summary>
    /// <param name="cell">The cell.</param>
    private static ClassifiedFate Expected(Cell cell)
    {
        if (Twin(cell) is { } twin)
            return Expected(twin);
        if (cell.Handing is Handing.CarriedSetupInvokes or Handing.CarriedSetupHandsToOpaqueCall)
            return Unknown;
        var fate = cell.Act switch
        {
            Act.InvokeNow or Act.InvokeBeforeFirstAwait => InvokeNow,
            // R7: a holder needs a trigger that runs the delegate in its own execution; a sequence's static type offers only
            // GetEnumerator, which never runs it.
            Act.KeepInResult => cell.Shape.Result == Result.Sequence ? Unknown : HolderResult,
            Act.KeepInThis or Act.CombineIntoThis => cell.Shape.Constructor ? HolderResult : HolderThis,
            Act.DoNothing or Act.CompareOnly or Act.RemoveFromThis => NotRun,
            Act.LazySequence => Iterator,
            _ => Unknown
        };
        return Admits(cell.Shape, fate) ? fate : Unknown;
    }

    /// <summary>Whether a member of the shape admits the fate (G-5's applicability): <c>holder</c> <c>this</c> an instance method that
    /// is not a constructor, <c>holder</c> <c>result</c> a constructor or a reference result, <c>iterator</c> a sequence result — the
    /// result being what a task completes with.</summary>
    /// <param name="shape">The member's kind and result type.</param>
    /// <param name="fate">The fate.</param>
    private static bool Admits(Shape shape, ClassifiedFate fate) => fate switch
    {
        { Fate: FateClassifier.HOLDER, Holder: FateClassifier.THIS } => shape.Instance,
        { Fate: FateClassifier.HOLDER, Holder: FateClassifier.RESULT } => shape.Constructor || shape.Result != Result.None,
        { Fate: FateClassifier.ITERATOR } => shape.Result == Result.Sequence,
        _ => true
    };

    /// <summary>G-7's comparator, as <see cref="EntryComparator.FateIsSafe"/> decides it.</summary>
    /// <param name="actual">The classified fate.</param>
    /// <param name="expected">The expected fate.</param>
    private static bool IsSafe(ClassifiedFate actual, ClassifiedFate expected) =>
        EntryComparator.FateIsSafe(expected.Fate, expected.Holder, actual.Fate, actual.Holder);

    /// <summary>The cell's classified fate; a cell with no classification counts as <c>unknown-execution</c>.</summary>
    /// <param name="answer">The answer.</param>
    /// <param name="cell">The cell.</param>
    private static ClassifiedFate Classified(GeneratedAnswer answer, Cell cell) =>
        answer.Classified?.GetValueOrDefault(cell.Parameter) ?? Unknown;

    private static string Show(ClassifiedFate fate) => fate.Holder is null ? fate.Fate : $"{fate.Fate} {fate.Holder}";

    /// <summary>A task handing's twin: the same cell with the task taken out, the delegate or the carrier handed directly; <c>null</c>
    /// for any other handing.</summary>
    /// <param name="cell">The cell.</param>
    private static Cell? Twin(Cell cell) => cell.Handing switch
    {
        Handing.TaskOfValue or Handing.ValueTaskOfValue => cell with { Handing = Handing.Value },
        Handing.TaskOfCarried or Handing.ValueTaskOfCarried => cell with { Handing = Handing.Carried },
        _ => null
    };

    private static Dictionary<string, string> Listed(params (string Reason, string[] Cells)[] groups) =>
        groups.SelectMany(group => group.Cells.Select(cell => (cell, group.Reason))).ToDictionary(pair => pair.cell, pair => pair.Reason, StringComparer.Ordinal);

    // ---- the cells ----

    /// <summary>Every combination C# allows: an <c>await</c> needs an <c>async</c> member, which takes no <c>in</c>, <c>ref</c> or
    /// <c>out</c> parameter; a member with no result keeps nothing in one; a static member has no <c>this</c>; a lazy sequence needs a
    /// sequence result; another slot of the value handed exists only for a carried handing, and two probes treated differently only for
    /// two carried probes. A constructor's <c>this</c> is the object it constructs, so keeping in the result and in <c>this</c> are both
    /// keeping in it there, and keeping in both is one keep; a constructor returns no lazy sequence. A static member has no receiver
    /// field to keep in, combine into or remove from. A task handing takes exactly the cells its twin takes, among
    /// <see cref="TaskHandingShapes"/>.</summary>
    private static IEnumerable<Cell> Cells() =>
        from handing in Enum.GetValues<Handing>()
        from shape in Shapes
        from act in Enum.GetValues<Act>()
        let cell = new Cell(handing, shape, act)
        where Allowed(cell)
        select cell;

    private static bool Allowed(Cell cell)
    {
        if (Twin(cell) is { } twin)
            return TaskHandingShapes.Contains(cell.Shape.Name) && Allowed(twin);
        var shape = cell.Shape;
        var hasResult = shape.Constructor || shape.Result != Result.None;
        return cell.Act switch
        {
            Act.InvokeBeforeFirstAwait => IsAsync(cell),
            Act.KeepInResult or Act.KeepInResultAndHandResult or Act.InvokeNowAndKeepInResult => hasResult,
            Act.KeepInThis or Act.KeepInThisAndHandThis => shape.Instance || shape.Constructor,
            // A static member has no receiver field (R7).
            Act.KeepInThisBag or Act.CombineIntoThis or Act.RemoveFromThis => shape.Instance || shape.Constructor,
            Act.KeepInResultAndThis => shape.Instance && shape.Result != Result.None,
            Act.LazySequence or Act.LazySequenceStartingTaskRun => shape.Result == Result.Sequence,
            Act.StoreIntoOtherSlot => cell.Handing is not (Handing.Value or Handing.In or Handing.Ref),
            Act.InvokeOneKeepOther => cell.Handing == Handing.TwoCarried && hasResult,
            _ => true
        };
    }

    /// <summary>Whether the cell's member is <c>async</c>: a task result, unless an <c>in</c>, <c>ref</c> or <c>out</c> parameter
    /// forbids it.</summary>
    /// <param name="cell">The cell.</param>
    private static bool IsAsync(Cell cell) =>
        cell.Shape.Wrapper != Wrapper.None && cell.Handing is not (Handing.In or Handing.Ref) && cell.Act != Act.AssignToOut;

    /// <summary>The library: the prelude and one class per cell.</summary>
    /// <param name="cells">The cells.</param>
    private static string Source(IEnumerable<Cell> cells)
    {
        var text = new StringBuilder(PRELUDE);
        foreach (var cell in cells)
            text.Append(Declaration(cell)).Append('\n');
        return text.Append("}\n").ToString();
    }

    /// <summary>The class of one cell: its member, the fields it keeps the delegate in, and a member that invokes them.</summary>
    /// <param name="cell">The cell.</param>
    private static string Declaration(Cell cell)
    {
        var shape = cell.Shape;
        var delegates = cell.Handing == Handing.TwoCarried ? new[] { "d", "e" } : ["d"];
        var kept = new SortedSet<int>();
        var body = new List<string>();
        // A task handing reads the delegate or the carrier out of the task: by await where its twin is async, by .Result where not.
        var read = IsAsync(cell) ? "await " : "";
        var completed = IsAsync(cell) ? "" : ".Result";
        var carrier = cell.Handing is Handing.TaskOfCarried or Handing.ValueTaskOfCarried ? "carrier" : "c";
        body.AddRange(cell.Handing switch
        {
            Handing.TwoCarried => ["var d = c.A;", "var e = c.B;"],
            Handing.Value or Handing.In or Handing.Ref => ["var d = a;"],
            Handing.TaskOfValue or Handing.ValueTaskOfValue => [$"var d = {read}a{completed};"],
            Handing.TaskOfCarried or Handing.ValueTaskOfCarried => [$"var carrier = {read}c{completed};", "var d = carrier.A;"],
            _ => new[] { "var d = c.A;" }
        });

        string? result = null;
        string? helper = null;
        var iterator = false;
        var fire = true;
        string? combined = null;
        string Holder(IEnumerable<string> held)
        {
            var values = held.ToList();
            while (values.Count < 2)
                values.Add("null");
            return $"new {(shape.Result == Result.Sequence ? "SeqRes" : "Res")}({string.Join(", ", values)})";
        }

        void Keep(IEnumerable<string> held)
        {
            foreach (var (value, index) in held.Select((value, index) => (value, index)))
            {
                kept.Add(index);
                body.Add($"_{index} = {value};");
            }
        }

        void Each(Func<string, string> statement) => body.AddRange(delegates.Select(statement));

        void Lazy(Func<string, string> perElement)
        {
            var loop = $"for (var i = 0; i < 2; i++) {{ {string.Concat(delegates.Select(perElement))} yield return \"x\"; }}";
            if (shape.Wrapper == Wrapper.None && cell.Handing is not (Handing.In or Handing.Ref))
            {
                iterator = true;
                body.Add(loop);
                return;
            }

            helper = loop;
            result = $"Lazy({string.Join(", ", delegates)})";
        }

        switch (cell.Act)
        {
            case Act.InvokeNow:
                Each(d => $"{d}();");
                break;
            case Act.InvokeBeforeFirstAwait:
                Each(d => $"{d}();");
                body.Add("await Task.Delay(1);");
                break;
            case Act.InvokeInTaskRun:
                Each(d => $"Task.Run({d});");
                break;
            case Act.InvokeInTimerCallback:
                Each(d => $"new Timer(_ => {d}(), null, 1, 1000);");
                break;
            case Act.HandToOpaqueCall:
                Each(d => $"Sink.Take({d});");
                break;
            case Act.HandToProbeEquals:
                Each(d => $"o.Equals({d});");
                break;
            case Act.KeepInResult:
                if (shape.Constructor)
                    Keep(delegates);
                else
                    result = Holder(delegates);
                break;
            case Act.KeepInThis:
                Keep(delegates);
                break;
            case Act.LazySequence:
                Lazy(d => $"{d}(); ");
                break;
            case Act.KeepInLibraryStatic:
                body.AddRange(delegates.Select((d, index) => $"Cache.{(index == 0 ? "A" : "B")} = {d};"));
                break;
            case Act.StoreIntoOtherArgument:
                body.AddRange(delegates.Select((d, index) => $"b.{(index == 0 ? "A" : "B")} = {d};"));
                break;
            case Act.StoreIntoOtherSlot:
                body.AddRange(delegates.Select((d, index) => $"{carrier}.Other{(index == 0 ? "A" : "B")} = {d};"));
                break;
            case Act.AssignToOut:
                body.AddRange(delegates.Select((d, index) => $"k{index} = {d};"));
                break;
            case Act.KeepInResultAndHandResult:
                if (shape.Constructor)
                {
                    Keep(delegates);
                    body.Add("Sink.Take(this);");
                }
                else
                {
                    body.Add($"var h = {Holder(delegates)};");
                    body.Add("Sink.Take(h);");
                    result = "h";
                }

                break;
            case Act.KeepInThisAndHandThis:
                Keep(delegates);
                body.Add("Sink.Take(this);");
                break;
            case Act.KeepInResultAndThis:
                Keep(delegates);
                result = Holder(delegates);
                break;
            case Act.DoNothing:
                break;
            case Act.InvokeNowAndKeepInResult:
                Each(d => $"{d}();");
                if (shape.Constructor)
                    Keep(delegates);
                else
                    result = Holder(delegates);
                break;
            case Act.LazySequenceStartingTaskRun:
                Lazy(d => $"Task.Run({d}); ");
                break;
            case Act.InvokeOneKeepOther:
                body.Add("d();");
                if (shape.Constructor)
                {
                    kept.Add(1);
                    body.Add("_1 = e;");
                }
                else
                    result = Holder(["e"]);

                break;
            case Act.KeepInThisBag:
                Keep(delegates);
                fire = false;
                break;
            case Act.CompareOnly:
                Each(d => shape.Result == Result.None ? $"if ({d} is null) return;" : $"if ({d} is null) {{ }}");
                break;
            case Act.CombineIntoThis:
                Each(d => $"_h += {d};");
                combined = "        public void Fire() { _h?.Invoke(); }\n";
                break;
            case Act.RemoveFromThis:
                Each(d => $"_h -= {d};");
                combined = "";
                break;
        }

        var parameters = new List<string>
        {
            cell.Handing switch
            {
                Handing.Value => "Action a",
                Handing.In => "in Action a",
                Handing.Ref => "ref Action a",
                Handing.Carried => "Carrier c",
                Handing.CarriedSetupInvokes => "InvokingCarrier c",
                Handing.CarriedSetupHandsToOpaqueCall => "LeakingCarrier c",
                Handing.TaskOfValue => "Task<Action> a",
                Handing.ValueTaskOfValue => "ValueTask<Action> a",
                Handing.TaskOfCarried => "Task<Carrier> c",
                Handing.ValueTaskOfCarried => "ValueTask<Carrier> c",
                _ => "Pair c"
            }
        };
        if (cell.Act == Act.HandToProbeEquals)
            parameters.Add("object o");
        if (cell.Act == Act.StoreIntoOtherArgument)
            parameters.Add("Box b");
        if (cell.Act == Act.AssignToOut)
            parameters.AddRange(delegates.Select((_, index) => $"out Action k{index}"));

        var isAsync = IsAsync(cell);
        if (!iterator && !shape.Constructor && shape.Result != Result.None)
        {
            var value = result ?? "null";
            var resultType = shape.Result == Result.Sequence ? "IEnumerable<string>" : "Res";
            body.Add(shape.Wrapper switch
            {
                Wrapper.None => $"return {value};",
                _ when isAsync => $"return {value};",
                Wrapper.Task => $"return Task.FromResult<{resultType}>({value});",
                _ => $"return new ValueTask<{resultType}>(({resultType}){value});"
            });
        }

        var returned = shape.Result switch
        {
            Result.None => "void",
            Result.Reference => "Res",
            _ => "IEnumerable<string>"
        };
        if (shape.Wrapper != Wrapper.None)
            returned = $"{shape.Wrapper}<{returned}>";
        var signature = shape.Constructor ? $"public {cell.Class}" : $"public {(shape.Instance ? "" : "static ")}{(isAsync ? "async " : "")}{returned} M";
        var text = new StringBuilder();
        text.Append("    public ").Append(shape.Instance || shape.Constructor ? "sealed " : "static ").Append("class ").Append(cell.Class).Append('\n');
        text.Append("    {\n");
        foreach (var index in kept)
            text.Append("        private Action _").Append(index).Append(";\n");
        if (combined is not null)
            text.Append("        private Action _h;\n");
        text.Append("        ").Append(signature).Append('(').Append(string.Join(", ", parameters)).Append(")\n");
        text.Append("        {\n");
        foreach (var statement in body)
            text.Append("            ").Append(statement).Append('\n');
        text.Append("        }\n");
        if (kept.Count > 0 && fire)
            text.Append("        public void Fire() { ").Append(string.Concat(kept.Select(index => $"_{index}(); "))).Append("}\n");
        text.Append(combined ?? "");
        if (helper is not null)
        {
            text.Append("        private static IEnumerable<string> Lazy(").Append(string.Join(", ", delegates.Select(d => $"Action {d}"))).Append(")\n");
            text.Append("        {\n            ").Append(helper).Append("\n        }\n");
        }

        return text.Append("    }\n").ToString();
    }

    // ---- the run ----

    /// <summary>Compiles the library once and runs the generator for every cell's member, the cells in parallel.</summary>
    private static Matrix Run()
    {
        var cells = Cells().ToArray();
        var library = Compile(Source(cells));
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        Assert.Equal(0, library.ExternBodies);
        var answers = new ConcurrentDictionary<Cell, GeneratedAnswer>();
        Parallel.ForEach(cells, cell =>
        {
            var request = new GenerationRequest(ASSEMBLY, "1.0", MemberId(library, cell), null, null);
            answers[cell] = ModelGenerator.Trace(request, library, CancellationToken.None).Answer;
        });
        return new Matrix(answers);
    }

    /// <summary>The declaration id of a cell's member: its constructor, or its method <c>M</c>.</summary>
    /// <param name="library">The compiled library.</param>
    /// <param name="cell">The cell.</param>
    private static string MemberId(LibraryCompilationResult library, Cell cell)
    {
        var type = library.Compilation!.GetTypeByMetadataName($"Lib.{cell.Class}")!;
        var member = cell.Shape.Constructor ? type.InstanceConstructors.Single() : type.GetMembers("M").OfType<IMethodSymbol>().Single();
        return member.GetDocumentationCommentId()!;
    }
}
