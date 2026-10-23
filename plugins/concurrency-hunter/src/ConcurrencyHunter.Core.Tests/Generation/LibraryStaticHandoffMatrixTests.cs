using System.Collections.Concurrent;
using System.Text;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>A static field of the library's assembly that code the analysis does not see gets by reference outside setup — the receiver
/// or a <c>ref</c>, <c>in</c> or <c>out</c> argument of a call without a body or of a call without a resolved target — is a possible
/// write into the library's state, as a store the analysis sees into it is (R1): no model, <c>library-state</c>, with
/// <c>library-state-store</c> naming the static storage of the field's type. The driver's own statics, a static's value handed by value
/// and a place that runs only in setup are none; a field of an object the member is given, handed the same ways, hands that object,
/// an input, to the unseen code (<c>unknown-touch</c>). One compiled fixture, one member per cell.</summary>
/// <param name="cache">The class fixture that keeps the matrix's answers for every test of the class.</param>
public sealed class LibraryStaticHandoffMatrixTests(ClassCache cache) : IClassFixture<ClassCache>
{
    private IReadOnlyDictionary<Cell, Result> Results => cache.Get("results", Run);

    /// <summary>Where the code the analysis does not see is called.</summary>
    public enum Site
    {
        /// <summary>A call of an <c>extern</c> member of the fixture, which has no body.</summary>
        UnknownCall,

        /// <summary>An interface member called with a constrained call in a generic method on a struct of the library no reached body
        /// boxes: its implementation is outside the reachable set.</summary>
        UnresolvedTarget
    }

    /// <summary>How the field reaches that code.</summary>
    public enum Channel
    {
        /// <summary>The field is the receiver by reference: a struct's <c>this</c>.</summary>
        Receiver,
        Ref,
        In,
        Out,

        /// <summary>The field's value is an argument by value.</summary>
        ByValue
    }

    /// <summary>The field's type: a struct of the library, <c>int</c>, a class of the library; each holds only an <c>int</c>.</summary>
    public enum FieldType
    {
        Struct,
        Scalar,
        Reference
    }

    /// <summary>Whose the field is.</summary>
    public enum Owner
    {
        /// <summary>A <c>private static</c> field of a type of the library.</summary>
        Library,

        /// <summary>A static of the driver: the member takes a <c>ref</c> parameter, to which the driver passes its own field, and hands
        /// the parameter the same way.</summary>
        Driver,

        /// <summary>A field of the object the member is given as its argument, of a class of the library.</summary>
        Input
    }

    /// <summary>Where the place with the call runs.</summary>
    public enum Where
    {
        /// <summary>In the tree of <c>V_Call</c>: the member calls the method the place is in.</summary>
        Call,

        /// <summary>Only in the tree of setup: the receiver's constructor, which setup runs, calls it.</summary>
        SetupOnly,

        /// <summary>One instance of the method runs in setup and in <c>V_Call</c>: the constructor and the member both call it.</summary>
        SetupAndCall,

        /// <summary>In an unknown execution: the method is a delegate of the library the member hands to an <c>extern</c> method.</summary>
        UnknownExecution,

        /// <summary>In a type initializer: the static constructor of a type of the library the member's call starts calls it.</summary>
        TypeInitializer
    }

    /// <summary>One matrix cell.</summary>
    /// <param name="Site">Where the unseen code is called.</param>
    /// <param name="Channel">How the field reaches it.</param>
    /// <param name="FieldType">The field's type.</param>
    /// <param name="Owner">Whose the field is.</param>
    /// <param name="Where">Where the place runs.</param>
    public sealed record Cell(Site Site, Channel Channel, FieldType FieldType, Owner Owner, Where Where)
    {
        public string Name => $"{Site}/{Channel}/{FieldType}/{Owner}/{Where}";

        public string Type => $"C_{Site}_{Channel}_{FieldType}_{Owner}_{Where}";

        public override string ToString() => Name;
    }

    /// <summary>What one cell's generation gave.</summary>
    /// <param name="Answer">The answer.</param>
    /// <param name="StaticStores">The state stores into a static storage region the effect reading recorded.</param>
    /// <param name="Argument">The objects the driver made as the member's argument <c>h</c>.</param>
    /// <param name="PlaceActions">For each instance of the method the place is in, the driver actions whose trees its executions are
    /// in, and the kind of each execution in no action's tree; empty where the place is in the member itself.</param>
    private sealed record Result(GeneratedAnswer Answer, IReadOnlyList<string> StaticStores, IReadOnlySet<string> Argument,
                                 IReadOnlyList<IReadOnlySet<string>> PlaceActions);

    private const string PRELUDE = """
        namespace Lib
        {
            public interface IGo { void Go(); }
            public interface IRun
            {
                void RefS(ref S x); void InS(in S x); void OutS(out S x); void ValS(S x);
                void RefI(ref int x); void InI(in int x); void OutI(out int x); void ValI(int x);
                void RefK(ref K x); void InK(in K x); void OutK(out K x);
            }
            public struct S : IGo { public int Count; public extern void Touch(); public void Go() { Count++; } }
            public sealed class K { public int Count; }
            public sealed class H { public S S; public int I; public K K; }
            public struct R : IRun
            {
                public void RefS(ref S x) { x.Count++; } public void InS(in S x) { } public void OutS(out S x) { x = default; } public void ValS(S x) { }
                public void RefI(ref int x) { x++; } public void InI(in int x) { } public void OutI(out int x) { x = 0; } public void ValI(int x) { }
                public void RefK(ref K x) { x = null; } public void InK(in K x) { } public void OutK(out K x) { x = null; }
            }
            public static class Ext
            {
                public static extern void RefS(ref S x); public static extern void InS(in S x); public static extern void OutS(out S x);
                public static extern void ValS(S x);
                public static extern void RefI(ref int x); public static extern void InI(in int x); public static extern void OutI(out int x);
                public static extern void ValI(int x);
                public static extern void RefK(ref K x); public static extern void InK(in K x); public static extern void OutK(out K x);
                public static extern void Take(System.Action a);
            }
            public static class Calls
            {
                public static void Go<T>(ref T s) where T : struct, IGo { s.Go(); }
                public static void RefS<T>(T r, ref S x) where T : struct, IRun { r.RefS(ref x); }
                public static void InS<T>(T r, in S x) where T : struct, IRun { r.InS(in x); }
                public static void OutS<T>(T r, out S x) where T : struct, IRun { r.OutS(out x); }
                public static void ValS<T>(T r, S x) where T : struct, IRun { r.ValS(x); }
                public static void RefI<T>(T r, ref int x) where T : struct, IRun { r.RefI(ref x); }
                public static void InI<T>(T r, in int x) where T : struct, IRun { r.InI(in x); }
                public static void OutI<T>(T r, out int x) where T : struct, IRun { r.OutI(out x); }
                public static void ValI<T>(T r, int x) where T : struct, IRun { r.ValI(x); }
                public static void RefK<T>(T r, ref K x) where T : struct, IRun { r.RefK(ref x); }
                public static void InK<T>(T r, in K x) where T : struct, IRun { r.InK(in x); }
                public static void OutK<T>(T r, out K x) where T : struct, IRun { r.OutK(out x); }
            }

        """;

    [Fact]
    public void Every_cell_gets_its_expected_answer()
    {
        var failures = Cells().Select(cell => (Cell: cell, Failure: Check(cell, Results[cell])))
                              .Where(pair => pair.Failure is not null)
                              .Select(pair => $"{pair.Cell.Name}: {pair.Failure}; got {Show(Results[pair.Cell])}")
                              .ToArray();

        Assert.True(failures.Length == 0, $"{failures.Length} cell(s) off the table:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        var cells = Cells().ToArray();
        var missing = Missing(cells, cell => cell.Site).Concat(Missing(cells, cell => cell.Channel)).Concat(Missing(cells, cell => cell.FieldType))
                                                       .Concat(Missing(cells, cell => cell.Owner)).Concat(Missing(cells, cell => cell.Where))
                                                       .ToArray();

        Assert.True(missing.Length == 0, "No cell has " + string.Join(", ", missing));
        Assert.Equal(Product().Count(cell => Excluded(cell) is null), cells.Length);
    }

    /// <summary>Why an answer is not the cell's, or <c>null</c> when it is (R1): a library static handed by reference where the place
    /// runs outside setup is library state, named by the static storage of its type; an argument's field handed the same way hands the
    /// argument; in every other cell no store into a static storage is recorded.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="result">Its generation.</param>
    private static string? Check(Cell cell, Result result)
    {
        if (WhereFailure(cell, result) is { } where)
            return where;
        var answer = result.Answer;
        var region = $"static:{ASSEMBLY}:Lib.{cell.Type}";
        var handedByReference = cell.Channel != Channel.ByValue && cell.Where != Where.SetupOnly;
        if (cell.Owner == Owner.Library && handedByReference)
        {
            if (answer.Model is not null || answer.ModelReason != ModelReasons.LIBRARY_STATE)
                return "expected no model and library-state";
            return answer.Causes.Any(cause => cause.Code == ModelCauses.LIBRARY_STATE_STORE && cause.Detail.StartsWith($"{region} in ", StringComparison.Ordinal))
                ? null
                : $"expected the cause {ModelCauses.LIBRARY_STATE_STORE} naming {region}";
        }
        if (cell.Owner == Owner.Input && handedByReference)
        {
            if (answer.Model is not null || answer.ModelReason != ModelReasons.UNKNOWN_TOUCH)
                return "expected no model and unknown-touch";
            return answer.Causes.Any(cause => cause.Code == ModelCauses.PROBE_HANDED_TO_UNSEEN &&
                                              result.Argument.Any(argument => cause.Detail.StartsWith(argument, StringComparison.Ordinal)))
                ? null
                : $"expected the cause {ModelCauses.PROBE_HANDED_TO_UNSEEN} naming the argument {string.Join(", ", result.Argument)}";
        }
        if (result.StaticStores.Count != 0)
            return $"expected no store into a static storage, found {string.Join(", ", result.StaticStores)}";
        return answer.Causes.Any(cause => cause.Code == ModelCauses.LIBRARY_STATE_STORE && cause.Detail.StartsWith("static:", StringComparison.Ordinal))
            ? $"expected no cause {ModelCauses.LIBRARY_STATE_STORE} naming a static storage"
            : null;
    }

    /// <summary>Why the place does not run where the cell says, or <c>null</c> when it does: in <c>V_Call</c> only, in setup only, in
    /// one instance in both, only in an unknown call of a delegate, or only in a type initializer.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="result">Its generation.</param>
    private static string? WhereFailure(Cell cell, Result result)
    {
        if (cell.Owner != Owner.Library)
            return null;
        var actions = result.PlaceActions;
        var runs = cell.Where switch
        {
            Where.Call => actions.Count != 0 && actions.All(set => set.SetEquals([DriverSynthesizer.CALL])),
            Where.SetupOnly => actions.Count != 0 && actions.All(set => set.SetEquals([DriverSynthesizer.SETUP])),
            Where.UnknownExecution => actions.Count != 0 && actions.All(set => set.SetEquals([nameof(ExecutionKind.UnknownDelegateCall)])),
            Where.TypeInitializer => actions.Count != 0 && actions.All(set => set.SetEquals([nameof(ExecutionKind.TypeInitializer)])),
            _ => actions.Any(set => set.Contains(DriverSynthesizer.SETUP) && set.Contains(DriverSynthesizer.CALL))
        };
        return runs ? null : $"the place runs in {string.Join("; ", actions.Select(set => string.Join("+", set.Order(StringComparer.Ordinal))))}";
    }

    /// <summary>Why a cell of the product is no cell, or <c>null</c> when it is one.</summary>
    /// <param name="cell">The cell.</param>
    private static string? Excluded(Cell cell)
    {
        if (cell.Channel == Channel.Receiver && cell.FieldType != FieldType.Struct)
            return "a receiver by reference is a struct's this; an object of a class is handed by the value of its reference";
        if (cell.Channel == Channel.ByValue && cell.FieldType == FieldType.Reference)
            return "the object a field holds, handed by the value of its reference, is outside the rule";
        if (cell.Owner != Owner.Library && cell.Where != Where.Call)
            return "the member's argument is not the constructor's";
        return null;
    }

    private static IEnumerable<string> Missing<T>(IReadOnlyList<Cell> cells, Func<Cell, T> axis) where T : struct, Enum =>
        Enum.GetValues<T>().Where(value => cells.All(cell => !axis(cell).Equals(value))).Select(value => $"{typeof(T).Name} {value}");

    private static IEnumerable<Cell> Product() =>
        from site in Enum.GetValues<Site>()
        from channel in Enum.GetValues<Channel>()
        from fieldType in Enum.GetValues<FieldType>()
        from owner in Enum.GetValues<Owner>()
        from runs in Enum.GetValues<Where>()
        select new Cell(site, channel, fieldType, owner, runs);

    private static IEnumerable<Cell> Cells() => Product().Where(cell => Excluded(cell) is null);

    /// <summary>Compiles the fixture once and generates every cell in parallel, as the model generator is used per member.</summary>
    private static IReadOnlyDictionary<Cell, Result> Run()
    {
        var cells = Cells().ToArray();
        var library = Compile(Source(cells));
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        var results = new ConcurrentDictionary<Cell, Result>();
        Parallel.ForEach(cells, CellParallelism, cell =>
        {
            var member = library.Compilation!.GetTypeByMetadataName($"Lib.{cell.Type}")!.GetMembers("M").OfType<IMethodSymbol>().Single();
            var trace = ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", member.GetDocumentationCommentId()!, null, null), library,
                                             CancellationToken.None);
            results[cell] = Summarize(cell, trace);
        });
        return results;
    }

    /// <summary>What a test reads of a trace, so the matrix keeps no engine run.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="trace">Its generation trace.</param>
    private static Result Summarize(Cell cell, GenerationTrace trace)
    {
        if (trace is not { Driver: { } driver, Run: { Heap: { } heap, Executions: { } analysis } })
            return new Result(trace.Answer, [], new HashSet<string>(), []);
        var statics = trace.StateStores.Where(store => heap.Regions.TryGetValue(store.Region, out var held) && held.Kind == HeapRegionKind.Static)
                           .Select(store => store.Region).ToArray();
        var argument = Slot(trace, DriverSynthesizer.DRIVER_TYPE, $"Arg_h_{DriverSynthesizer.CALL_VARIANT}");
        var executions = new DriverExecutions(driver, analysis, new HashSet<string>(StringComparer.Ordinal));
        var place = $":M:Lib.{cell.Type}.Hand";
        var actions = heap.Instances.Values.Where(instance => instance.BodyId.Contains(place, StringComparison.Ordinal))
                          .Select(instance => (IReadOnlySet<string>)(analysis.InstanceExecutions.GetValueOrDefault(instance.Id) ?? new HashSet<string>())
                                                                    .Select(execution => executions.TreeActionOf(execution) ??
                                                                                         analysis.Execution(execution).Kind.ToString())
                                                                    .ToHashSet(StringComparer.Ordinal))
                          .ToArray();
        return new Result(trace.Answer, statics, argument, actions);
    }

    private static string Source(IEnumerable<Cell> cells)
    {
        var source = new StringBuilder(PRELUDE);
        // bounded: one declaration per cell of the finite matrix.
        foreach (var cell in cells)
            source.Append(Declaration(cell));
        return source.Append("}\n").ToString();
    }

    private static string Declaration(Cell cell)
    {
        var type = cell.FieldType switch
        {
            FieldType.Struct => "S",
            FieldType.Scalar => "int",
            _ => "K"
        };
        var source = new StringBuilder().Append("    public sealed class ").Append(cell.Type).Append("\n    {\n");
        switch (cell.Owner)
        {
            case Owner.Library when cell.Where == Where.UnknownExecution:
                source.Append("        private static ").Append(type).Append(" s_f;\n");
                // A static method: its delegate captures nothing the unseen code could be handed besides the call it makes.
                source.Append("        private static void Hand() { ").Append(Handoff(cell, "s_f")).Append(" }\n");
                source.Append("        public void M() { Ext.Take(Hand); }\n");
                break;
            case Owner.Library when cell.Where == Where.TypeInitializer:
                source.Append("        private static ").Append(type).Append(" s_f;\n");
                source.Append("        private static void Hand() { ").Append(Handoff(cell, "s_f")).Append(" }\n");
                source.Append("        private static class Init { static Init() { Hand(); } public static void Run() { } }\n");
                source.Append("        public void M() { Init.Run(); }\n");
                break;
            case Owner.Library:
                source.Append("        private static ").Append(type).Append(" s_f;\n");
                if (cell.Where != Where.Call)
                    source.Append("        public ").Append(cell.Type).Append("() { Hand(); }\n");
                // An instance method: the constructor and the member call it on the one receiver, in one context.
                source.Append("        private void Hand() { ").Append(Handoff(cell, "s_f")).Append(" }\n");
                source.Append("        public void M() { ").Append(cell.Where == Where.SetupOnly ? "" : "Hand();").Append(" }\n");
                break;
            case Owner.Driver:
                source.Append("        public void M(ref ").Append(type).Append(" x) { ").Append(Handoff(cell, "x")).Append(" }\n");
                break;
            default:
                var field = cell.FieldType switch
                {
                    FieldType.Struct => "h.S",
                    FieldType.Scalar => "h.I",
                    _ => "h.K"
                };
                source.Append("        public void M(H h) { ").Append(Handoff(cell, field)).Append(" }\n");
                break;
        }
        return source.Append("    }\n").ToString();
    }

    /// <summary>The statement handing a place to the unseen code of a cell's site by its channel.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="place">The field, an expression that is a variable.</param>
    private static string Handoff(Cell cell, string place)
    {
        var suffix = cell.FieldType switch
        {
            FieldType.Struct => "S",
            FieldType.Scalar => "I",
            _ => "K"
        };
        var (member, argument) = cell.Channel switch
        {
            Channel.Ref => ($"Ref{suffix}", $"ref {place}"),
            Channel.In => ($"In{suffix}", $"in {place}"),
            Channel.Out => ($"Out{suffix}", $"out {place}"),
            Channel.ByValue => ($"Val{suffix}", place),
            _ => ("", place)
        };
        return (cell.Site, cell.Channel) switch
        {
            (Site.UnknownCall, Channel.Receiver) => $"{place}.Touch();",
            (Site.UnknownCall, _) => $"Ext.{member}({argument});",
            (_, Channel.Receiver) => $"Calls.Go(ref {place});",
            _ => $"Calls.{member}(new R(), {argument});"
        };
    }

    private static string Show(Result result) => result.Answer.Model is { } model
        ? ModelEntryWriter.Write(model).ToJsonString()
        : $"{result.Answer.ModelReason ?? result.Answer.Reason}: {result.Answer.Detail} [{string.Join("; ", result.Answer.Causes)}]" +
          (result.StaticStores.Count == 0 ? "" : $" static stores {string.Join(", ", result.StaticStores)}");
}
