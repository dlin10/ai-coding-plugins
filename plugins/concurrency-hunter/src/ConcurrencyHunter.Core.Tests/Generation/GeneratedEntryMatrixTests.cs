using System.Collections.Concurrent;
using System.Text;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Xunit;
using Xunit.Abstractions;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The generated-entry matrix of R4-R7: one compiled fixture, one member per value-kind/action/channel cell, and whole-entry
/// expectations written from the requirements rather than from the generator.</summary>
public sealed class GeneratedEntryMatrixTests(ITestOutputHelper output, ClassCache cache) : IClassFixture<ClassCache>
{
    private Matrix Results => cache.Get("results", Run);

    private static readonly IReadOnlyDictionary<string, string> Wider = Listed(
        ("the heap does not carry the named value through the concrete List<object> initializer, so the fresh collection is refused", [
            "Probe/ReturnInCollection/Call", "Element/ReturnInCollection/Call", "LibraryArg/ReturnInCollection/Call",
            "DelegateResult/ReturnInCollection/Call", "Fresh/ReturnInCollection/Call", "FreshHoldingProbe/ReturnInCollection/Call"
        ]),
        ("the heap loses the delegate-return provenance across the child or iterator execution, so the observed value has no name", [
            "DelegateResult/HandToDelegate/TaskRun", "DelegateResult/KeepOnSealedReceiver/TaskRun",
            "DelegateResult/KeepOnSealedReceiver/Enumeration", "DelegateResult/StoreInArrayArg/TaskRun",
            "DelegateResult/StoreInArrayArg/Enumeration"
        ]),
        ("the keeping chain does not project arg:p through the fresh Box linked into the pre-existing keeper", [
            "FreshHoldingProbe/KeepOnSealedReceiver/Call", "FreshHoldingProbe/KeepOnSubReceiver/Call",
            "FreshHoldingProbe/KeepOnLibraryArg/Call"
        ]),
        ("the child execution's array store loses the captured probe's argument provenance", [
            "Probe/StoreInArrayArg/TaskRun"
        ]));

    /// <summary>The kind and R5 name of the value the member observes.</summary>
    public enum ValueKind
    {
        Probe,
        Element,
        LibraryArg,
        Seed,
        WitnessResult,
        DelegateResult,
        Fresh,
        FreshHoldingProbe,
        PreExisting
    }

    /// <summary>What the member does with the observed value.</summary>
    public enum Action
    {
        Read,
        WriteField,
        KeepOnSealedReceiver,
        KeepOnSubReceiver,
        KeepOnLibraryArg,
        KeepOnFreshResult,
        ReturnWhole,
        ReturnInCollection,
        ReturnInSequence,
        AssignToOut,
        AssignToRef,
        StoreInArrayArg,
        HandToDelegate,
        HandToExtern
    }

    /// <summary>The execution in which the member obtains and acts on the value.</summary>
    public enum Channel
    {
        Call,
        TaskRun,
        Enumeration,
        UnknownExecution
    }

    /// <summary>One matrix cell.</summary>
    /// <param name="ValueKind">The value the member observes.</param>
    /// <param name="Action">What it does with the value.</param>
    /// <param name="Channel">Where it obtains and acts on the value.</param>
    public sealed record Cell(ValueKind ValueKind, Action Action, Channel Channel)
    {
        public string Name => $"{ValueKind}/{Action}/{Channel}";

        public string Class => $"C_{ValueKind}_{Action}_{Channel}";

        public override string ToString() => Name;
    }

    /// <summary>The generated answers of all cells.</summary>
    /// <param name="Answers">The answer by cell.</param>
    private sealed record Matrix(IReadOnlyDictionary<Cell, GeneratedAnswer> Answers);

    private const string PRELUDE = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        namespace Lib
        {
            public static class Sink
            {
                public static extern void Take(object value);
                public static extern void Run(Action action);
            }

            public static class Choice { public static bool Value; }
            public class Item { public object Value; }
            public sealed class Leaf { public int Value; }
            public sealed class Box { public object Value; }
            public interface IConv { bool Ready(); Item Make(); }
            public sealed class Lib { public IConv Conv; public object Kept; }

        """;

    [Fact]
    public void Every_cell_has_no_unsafe_narrowing()
    {
        var failures = Results.Answers.Select(pair => (pair.Key, Comparison: Compare(pair.Key, pair.Value)))
                              .Where(pair => pair.Comparison.IsUnsafeNarrowing)
                              .Select(pair => $"{pair.Key.Name}: {pair.Comparison.Detail}; got {Show(pair.Key, Results.Answers[pair.Key])}")
                              .Order(StringComparer.Ordinal).ToArray();

        Assert.True(failures.Length == 0, $"{failures.Length} unsafe narrowing(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_cell_gets_its_expected_answer()
    {
        var failures = new List<string>();
        foreach (var (cell, answer) in Results.Answers.OrderBy(pair => pair.Key.Name, StringComparer.Ordinal))
        {
            var comparison = Compare(cell, answer);
            if (Wider.TryGetValue(cell.Name, out var reason))
            {
                if (comparison.IsExact)
                    failures.Add($"{cell.Name}: listed as wider ({reason}) but got the expected answer");
                else if (comparison.IsUnsafeNarrowing)
                    failures.Add($"{cell.Name}: listed as wider ({reason}) but is unsafe: {comparison.Detail}");
            }
            else if (!comparison.IsExact)
                failures.Add($"{cell.Name}: {comparison.Detail}; got {Show(cell, answer)}");
        }

        foreach (var listed in Wider.Keys.Where(name => Results.Answers.Keys.All(cell => cell.Name != name)))
            failures.Add($"{listed}: listed as wider but is no cell of the matrix");
        Assert.True(failures.Count == 0, $"{failures.Count} cell(s) off the table:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_listed_reason_is_reported_with_its_cells()
    {
        // Task 9: every listed reason reaches the run's results on a passing run too, not only in a failure message.
        var reported = Wider.GroupBy(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                            .Select(group => $"wider ({group.Count()}): {group.Key}: {string.Join(", ", group.Order(StringComparer.Ordinal))}")
                            .ToArray();
        foreach (var line in reported)
            output.WriteLine(line);

        Assert.Equal(Wider.Values.Distinct(StringComparer.Ordinal).Count(), reported.Length);
        Assert.All(Wider.Keys, name => Assert.Contains(reported, line => line.Contains(name, StringComparison.Ordinal)));
    }

    [Fact]
    public void The_matrix_covers_every_axis_value()
    {
        var classified = Results.Answers.Where(pair => pair.Value.Classified is not null).Select(pair => pair.Key).ToArray();
        var missing = Enum.GetValues<ValueKind>().Where(value => classified.All(cell => cell.ValueKind != value)).Select(value => $"value {value}")
                          .Concat(Enum.GetValues<Action>().Where(action => classified.All(cell => cell.Action != action)).Select(action => $"action {action}"))
                          .Concat(Enum.GetValues<Channel>().Where(channel => classified.All(cell => cell.Channel != channel)).Select(channel => $"channel {channel}"))
                          .ToArray();
        var refused = Results.Answers.Where(pair => pair.Value.Classified is null)
                             .Select(pair => $"{pair.Key.Name}: {pair.Value.Reason} {pair.Value.Detail}").ToArray();

        Assert.True(missing.Length == 0, "No classified cell has " + string.Join(", ", missing));
        Assert.True(refused.Length == 0, $"{refused.Length} cell(s) got no classification:\n" + string.Join("\n", refused));
    }

    private static EntryComparison Compare(Cell cell, GeneratedAnswer answer) =>
        EntryComparator.Compare(answer, Expected(cell), requireSameModelReason: true);

    /// <summary>The whole expected answer. R4 supplies effects, R5 supplies names and whole-place <c>new</c>, R6 rejects writes to
    /// pre-existing library state, and R7 rejects touches outside the call and enumeration.</summary>
    /// <param name="cell">The matrix cell.</param>
    private static EntryTruth Expected(Cell cell)
    {
        var expected = new ExpectedModel();
        if (cell.ValueKind == ValueKind.Element)
            expected.Effects.Add(LibraryEffect.DeepReadOf("items"));
        if (cell.ValueKind == ValueKind.DelegateResult)
            expected.Fates.Add(Fate("g", cell.Channel switch
            {
                Channel.Call => LibraryFateKind.InvokeNow,
                Channel.Enumeration => LibraryFateKind.Iterator,
                _ => LibraryFateKind.UnknownExecution
            }));
        if (cell.Channel == Channel.Enumeration)
            expected.Result = Result("sequence(arg:p)");

        var reason = cell.Channel switch
        {
            Channel.Call => ExpectedCall(cell, expected),
            Channel.TaskRun or Channel.Enumeration => ExpectedReachedExecution(cell, expected),
            Channel.UnknownExecution => ExpectedUnknownExecution(cell, expected),
            _ => throw new InvalidOperationException($"Unknown channel {cell.Channel}.")
        };
        return reason is null ? EntryTruth.Entry(expected.Build()) : EntryTruth.NoEntry(reason);
    }

    /// <summary>R4-R7 for actions in the member's own call: effects name probe reads and writes, R5 names only whole fresh places,
    /// and R6 permits pre-existing writes only along a keeper that keeps a value in this call.</summary>
    /// <param name="cell">The call cell.</param>
    /// <param name="expected">The answer being built.</param>
    private static string? ExpectedCall(Cell cell, ExpectedModel expected)
    {
        var value = Name(cell.ValueKind);
        switch (cell.Action)
        {
            case Action.Read:
                return cell.ValueKind switch
                {
                    ValueKind.Probe => AddEffect(expected, LibraryEffect.DeepReadOf("p")),
                    ValueKind.Seed => AddEffect(expected, LibraryEffect.DeepReadOf("lib")),
                    ValueKind.WitnessResult or ValueKind.DelegateResult => ModelReasons.VOCABULARY,
                    _ => null
                };
            case Action.WriteField:
                return cell.ValueKind switch
                {
                    ValueKind.Probe => AddEffect(expected, LibraryEffect.WriteOf("p")),
                    ValueKind.Element or ValueKind.WitnessResult or ValueKind.DelegateResult => ModelReasons.VOCABULARY,
                    ValueKind.LibraryArg or ValueKind.PreExisting => ModelReasons.LIBRARY_STATE,
                    _ => null
                };
            case Action.KeepOnSealedReceiver:
            case Action.KeepOnSubReceiver:
                return Keep(expected, "this", cell.ValueKind, value);
            case Action.KeepOnLibraryArg:
                return Keep(expected, "other", cell.ValueKind, value);
            case Action.KeepOnFreshResult:
                return FreshResult(expected, cell.ValueKind, value);
            case Action.ReturnWhole:
                return WholeResult(expected, cell.ValueKind, value, collection: false);
            case Action.ReturnInCollection:
                return WholeResult(expected, cell.ValueKind, value, collection: true);
            case Action.AssignToOut:
                return Output(expected, "o", cell.ValueKind, value, includeOld: false);
            case Action.AssignToRef:
                return Output(expected, "r", cell.ValueKind, value, includeOld: true);
            case Action.StoreInArrayArg:
                return Store(expected, cell.ValueKind, value);
            case Action.HandToDelegate:
                return Input(expected, cell.ValueKind, value, LibraryFateKind.InvokeNow);
            case Action.HandToExtern:
                return Extern(expected, cell.ValueKind);
            default:
                throw new InvalidOperationException($"Action {cell.Action} is not a call action.");
        }
    }

    /// <summary>R4 and R7 for a reached spawn or enumeration: the action is attributed to that execution, while an unnameable value
    /// remains vocabulary and a delegate handed there has that channel's fate.</summary>
    /// <param name="cell">The reached-execution cell.</param>
    /// <param name="expected">The answer being built.</param>
    private static string? ExpectedReachedExecution(Cell cell, ExpectedModel expected)
    {
        var value = Name(cell.ValueKind);
        return cell.Action switch
        {
            Action.Read => cell.ValueKind switch
            {
                ValueKind.Probe => AddEffect(expected, LibraryEffect.DeepReadOf("p")),
                ValueKind.Seed => AddEffect(expected, LibraryEffect.DeepReadOf("lib")),
                _ => ModelReasons.VOCABULARY
            },
            Action.KeepOnSealedReceiver => cell.ValueKind switch
            {
                ValueKind.Probe => AddValues(expected.Keeps, "this", "arg:p"),
                ValueKind.Seed => ModelReasons.VOCABULARY,
                _ => AddValues(expected.Keeps, "this", value!)
            },
            Action.StoreInArrayArg => cell.ValueKind switch
            {
                ValueKind.Probe => StoreNamed(expected, "arg:p"),
                ValueKind.Seed => ModelReasons.VOCABULARY,
                _ => StoreNamed(expected, value!)
            },
            Action.HandToDelegate => cell.ValueKind switch
            {
                ValueKind.Probe => InputNamed(expected, "arg:p", FateFor(cell.Channel)),
                ValueKind.Seed => ModelReasons.VOCABULARY,
                _ => InputNamed(expected, value!, FateFor(cell.Channel))
            },
            Action.HandToExtern => cell.ValueKind switch
            {
                ValueKind.Seed => AddEffect(expected, LibraryEffect.DeepReadOf("lib")),
                _ => ModelReasons.UNKNOWN_TOUCH
            },
            Action.ReturnInSequence => cell.ValueKind switch
            {
                ValueKind.Probe => null,
                ValueKind.Seed => ModelReasons.VOCABULARY,
                _ => SetSequenceResult(expected, "arg:p", value!)
            },
            _ => throw new InvalidOperationException($"Action {cell.Action} is not a reached-execution action.")
        };
    }

    /// <summary>R6-R7 for an unknown execution: captured probes make the whole member unknown-touch; a receiver write that keeps
    /// nothing is library-state; and the seed's witness can only establish a deep read of <c>lib</c>.</summary>
    /// <param name="cell">The unknown-execution cell.</param>
    /// <param name="expected">The answer being built.</param>
    private static string? ExpectedUnknownExecution(Cell cell, ExpectedModel expected)
    {
        if (cell.ValueKind is ValueKind.Probe or ValueKind.DelegateResult)
            return ModelReasons.UNKNOWN_TOUCH;
        return cell.Action switch
        {
            Action.Read or Action.HandToExtern => AddEffect(expected, LibraryEffect.DeepReadOf("lib")),
            Action.KeepOnSealedReceiver => ModelReasons.LIBRARY_STATE,
            Action.StoreInArrayArg or Action.HandToDelegate => ModelReasons.UNKNOWN_TOUCH,
            _ => throw new InvalidOperationException($"Action {cell.Action} is not an unknown-execution action.")
        };
    }

    private static string? Keep(ExpectedModel expected, string keeper, ValueKind kind, string? value) => kind switch
    {
        ValueKind.Seed or ValueKind.WitnessResult => ModelReasons.VOCABULARY,
        ValueKind.Fresh or ValueKind.PreExisting => ModelReasons.LIBRARY_STATE,
        ValueKind.FreshHoldingProbe => AddValues(expected.Keeps, keeper, "arg:p"),
        _ => AddValues(expected.Keeps, keeper, value!)
    };

    private static string? FreshResult(ExpectedModel expected, ValueKind kind, string? value)
    {
        expected.Result = Result("new");
        return kind switch
        {
            ValueKind.Seed or ValueKind.WitnessResult or ValueKind.PreExisting => ModelReasons.VOCABULARY,
            ValueKind.Fresh => null,
            ValueKind.FreshHoldingProbe => AddValues(expected.Keeps, "result", "arg:p"),
            _ => AddValues(expected.Keeps, "result", value!)
        };
    }

    private static string? WholeResult(ExpectedModel expected, ValueKind kind, string? value, bool collection)
    {
        if (kind is ValueKind.Seed or ValueKind.WitnessResult or ValueKind.PreExisting)
            return ModelReasons.VOCABULARY;
        if (kind == ValueKind.Fresh)
            expected.Result = Result("new");
        else if (kind == ValueKind.FreshHoldingProbe)
        {
            expected.Result = Result("new");
            AddValues(expected.Keeps, "result", "arg:p");
        }
        else
            expected.Result = Result(collection ? $"collection({value})" : $"[{value}]");
        return null;
    }

    private static string? Output(ExpectedModel expected, string parameter, ValueKind kind, string? value, bool includeOld)
    {
        if (kind is ValueKind.Seed or ValueKind.WitnessResult or ValueKind.FreshHoldingProbe or ValueKind.PreExisting ||
            includeOld && kind == ValueKind.Fresh)
            return ModelReasons.VOCABULARY;
        expected.Outputs[parameter] = kind == ValueKind.Fresh ? Result("new") :
                                      Result(includeOld ? $"[{value},arg:r]" : $"[{value}]");
        return null;
    }

    private static string? Store(ExpectedModel expected, ValueKind kind, string? value) => kind switch
    {
        ValueKind.Seed or ValueKind.WitnessResult or ValueKind.Fresh or ValueKind.FreshHoldingProbe or ValueKind.PreExisting => ModelReasons.VOCABULARY,
        _ => StoreNamed(expected, value!)
    };

    private static string? StoreNamed(ExpectedModel expected, string value)
    {
        expected.Effects.Add(LibraryEffect.WriteCellsOf("cells"));
        return AddValues(expected.Stores, "cells", value);
    }

    private static string? Input(ExpectedModel expected, ValueKind kind, string? value, LibraryFateKind fate) => kind switch
    {
        ValueKind.Seed or ValueKind.WitnessResult or ValueKind.FreshHoldingProbe or ValueKind.PreExisting => ModelReasons.VOCABULARY,
        ValueKind.Fresh => InputNamed(expected, "new", fate),
        _ => InputNamed(expected, value!, fate)
    };

    private static string? InputNamed(ExpectedModel expected, string value, LibraryFateKind fate)
    {
        expected.Fates.Add(Fate("f", fate, value));
        return null;
    }

    private static string? Extern(ExpectedModel expected, ValueKind kind) => kind switch
    {
        ValueKind.Probe or ValueKind.Element or ValueKind.DelegateResult or ValueKind.FreshHoldingProbe => ModelReasons.UNKNOWN_TOUCH,
        ValueKind.LibraryArg or ValueKind.Seed => AddEffect(expected, LibraryEffect.DeepReadOf("lib")),
        ValueKind.WitnessResult => ModelReasons.VOCABULARY,
        _ => null
    };

    private static LibraryFateKind FateFor(Channel channel) => channel switch
    {
        Channel.TaskRun => LibraryFateKind.UnknownExecution,
        Channel.Enumeration => LibraryFateKind.Iterator,
        _ => throw new InvalidOperationException($"Channel {channel} gives no reached-execution fate.")
    };

    private static string? AddEffect(ExpectedModel expected, LibraryEffect effect)
    {
        expected.Effects.Add(effect);
        return null;
    }

    private static string? AddValues(Dictionary<string, IReadOnlyList<LibraryValue>> places, string place, params string[] values)
    {
        places[place] = values.Select(Value).ToArray();
        return null;
    }

    private static string? SetSequenceResult(ExpectedModel expected, params string[] values)
    {
        expected.Result = Result($"sequence({string.Join(',', values)})");
        return null;
    }

    private static string? Name(ValueKind kind) => kind switch
    {
        ValueKind.Probe => "arg:p",
        ValueKind.Element => "elements(arg:items)",
        ValueKind.LibraryArg => "arg:lib",
        ValueKind.DelegateResult => "returns:g",
        _ => null
    };

    private static LibraryFate Fate(string parameter, LibraryFateKind kind, params string[] inputs) =>
        new(parameter, kind, null, inputs.Length == 0 ? null : [inputs.Select(Value).ToArray()]);

    private static LibraryValue Value(string text) => LibraryValue.Parse(text)!;

    private static LibraryResult Result(string text) => LibraryResult.Parse(text)!;

    private static IEnumerable<Cell> Cells() =>
        from channel in Enum.GetValues<Channel>()
        from value in Enum.GetValues<ValueKind>()
        from action in Enum.GetValues<Action>()
        let cell = new Cell(value, action, channel)
        where Allowed(cell)
        select cell;

    private static bool Allowed(Cell cell)
    {
        if (cell.Channel == Channel.Call)
            return cell.Action != Action.ReturnInSequence && !(cell.ValueKind == ValueKind.Seed && cell.Action == Action.WriteField);
        if (cell.ValueKind is not (ValueKind.Probe or ValueKind.Seed or ValueKind.DelegateResult))
            return false;
        return cell.Action is Action.Read or Action.KeepOnSealedReceiver or Action.StoreInArrayArg or Action.HandToDelegate or Action.HandToExtern ||
               cell.Channel == Channel.Enumeration && cell.Action == Action.ReturnInSequence;
    }

    /// <summary>Compiles the fixture once and generates every cell in parallel, as the model generator is used per member.</summary>
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

    private static string Source(IEnumerable<Cell> cells)
    {
        var source = new StringBuilder(PRELUDE);
        foreach (var cell in cells)
            source.Append(Declaration(cell));
        return source.Append("}\n").ToString();
    }

    private static string Declaration(Cell cell)
    {
        var parameters = new List<string> { "Item p" };
        parameters.AddRange(cell.ValueKind switch
        {
            ValueKind.Element => ["Item[] items"],
            ValueKind.LibraryArg or ValueKind.Seed or ValueKind.WitnessResult => ["Lib lib"],
            ValueKind.DelegateResult => ["Func<Item> g"],
            _ => []
        });
        parameters.AddRange(cell.Action switch
        {
            Action.KeepOnLibraryArg => ["Lib other"],
            Action.AssignToOut => ["out object o"],
            Action.AssignToRef => ["ref object r"],
            Action.StoreInArrayArg => ["object[] cells"],
            Action.HandToDelegate => ["Action<object> f"],
            _ => []
        });

        var body = new List<string>();
        var action = ActionBody(cell);
        if (cell.Channel == Channel.Call)
            body.AddRange(action);
        else if (cell.Channel == Channel.TaskRun)
            body.Add("Task.Run(() => { " + string.Join(' ', action) + " });");
        else if (cell.Channel == Channel.UnknownExecution)
            body.Add("Sink.Run(() => { " + string.Join(' ', action) + " });");
        else
        {
            body.AddRange(action);
            body.Add("yield return p;");
        }

        var returns = cell.Channel == Channel.Enumeration ? "IEnumerable<object>" : cell.Action switch
        {
            Action.KeepOnFreshResult => "Box",
            Action.ReturnWhole => "object",
            Action.ReturnInCollection => "List<object>",
            _ => "void"
        };
        var open = cell.Action == Action.KeepOnSubReceiver ? "class" : "sealed class";
        var source = new StringBuilder().Append("    public ").Append(open).Append(' ').Append(cell.Class).Append("\n    {\n");
        if (cell.Action == Action.KeepOnSealedReceiver)
            source.Append("        private object _kept;\n");
        if (cell.Action == Action.KeepOnSubReceiver)
            source.Append("        public object Kept;\n");
        if (cell.ValueKind == ValueKind.PreExisting)
            source.Append("        private readonly Leaf _pre = new Leaf();\n");
        source.Append("        public ").Append(returns).Append(" M(").Append(string.Join(", ", parameters)).Append(")\n        {\n");
        foreach (var statement in body)
            source.Append("            ").Append(statement).Append('\n');
        return source.Append("        }\n    }\n").ToString();
    }

    private static IReadOnlyList<string> ActionBody(Cell cell)
    {
        var body = new List<string> { ValueDeclaration(cell.ValueKind) };
        body.AddRange(cell.Action switch
        {
            Action.Read => [Read(cell.ValueKind)],
            Action.WriteField => [Write(cell.ValueKind)],
            Action.KeepOnSealedReceiver => ["_kept = value;"],
            Action.KeepOnSubReceiver => ["Kept = value;"],
            Action.KeepOnLibraryArg => ["other.Kept = value;"],
            Action.KeepOnFreshResult => ["var result = new Box();", "result.Value = value;", "return result;"],
            Action.ReturnWhole => ["return value;"],
            Action.ReturnInCollection => ["return new List<object> { value };"],
            Action.ReturnInSequence => ["yield return value;"],
            Action.AssignToOut => ["o = value;"],
            Action.AssignToRef => ["if (Choice.Value) r = value;"],
            Action.StoreInArrayArg => ["cells[0] = value;"],
            Action.HandToDelegate => ["f(value);"],
            Action.HandToExtern => ["Sink.Take(value);"],
            _ => throw new InvalidOperationException($"Unknown action {cell.Action}.")
        });
        return body;
    }

    private static string ValueDeclaration(ValueKind kind) => kind switch
    {
        ValueKind.Probe => "var value = p;",
        ValueKind.Element => "var value = items[0];",
        ValueKind.LibraryArg => "var value = lib;",
        ValueKind.Seed => "var value = lib.Conv;",
        ValueKind.WitnessResult => "var value = lib.Conv.Make();",
        ValueKind.DelegateResult => "var value = g();",
        ValueKind.Fresh => "var value = new Leaf();",
        ValueKind.FreshHoldingProbe => "var value = new Box { Value = p };",
        ValueKind.PreExisting => "var value = _pre;",
        _ => throw new InvalidOperationException($"Unknown value kind {kind}.")
    };

    private static string Read(ValueKind kind) => kind switch
    {
        ValueKind.LibraryArg => "_ = value.Conv;",
        ValueKind.Seed => "_ = value.Ready();",
        _ => "_ = value.Value;"
    };

    private static string Write(ValueKind kind) => kind switch
    {
        ValueKind.LibraryArg => "value.Conv = null;",
        ValueKind.Fresh or ValueKind.PreExisting => "value.Value++;",
        _ => "value.Value = null;"
    };

    private static string MemberId(LibraryCompilationResult library, Cell cell) =>
        library.Compilation!.GetTypeByMetadataName($"Lib.{cell.Class}")!.GetMembers("M").OfType<IMethodSymbol>().Single().GetDocumentationCommentId()!;

    private static string Show(Cell cell, GeneratedAnswer answer) => answer.Model is { } model
        ? ModelEntryWriter.Write(model).ToJsonString()
        : answer.ModelReason ?? answer.Reason ?? $"no answer for {cell.Name}";

    private static Dictionary<string, string> Listed(params (string Reason, string[] Cells)[] groups) =>
        groups.SelectMany(group => group.Cells.Select(cell => (cell, group.Reason)))
              .ToDictionary(pair => pair.cell, pair => pair.Reason, StringComparer.Ordinal);

    private sealed class ExpectedModel
    {
        internal List<LibraryEffect> Effects { get; } = [];
        internal LibraryResult? Result { get; set; }
        internal List<LibraryFate> Fates { get; } = [];
        internal Dictionary<string, IReadOnlyList<LibraryValue>> Keeps { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, IReadOnlyList<LibraryValue>> Stores { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, LibraryResult> Outputs { get; } = new(StringComparer.Ordinal);

        internal LibraryModel Build() => new("M:Lib.Cell.M", [], Effects)
        {
            Result = Result,
            Fates = Fates,
            Keeps = Keeps,
            Stores = Stores,
            Outputs = Outputs
        };
    }
}
