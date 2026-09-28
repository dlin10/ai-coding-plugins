using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Reporting;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The delegates of the collections (R7, ADR 0010): the members of a <c>List</c> that take one and <c>RemoveWhere</c> of a
/// <c>HashSet</c> run it where the call stands, handed what the cells hold, with the accesses of what they do, and the <c>System.Array</c>
/// statics that take one are built-in <c>invoke-now</c> models handed the array's cells — but <c>Sort</c> with a comparison, which stays
/// opaque. Each collection holds objects of a type of its own, so what a delegate is handed names where it came from; a spare object
/// outside them all is what an over-approximation would add.</summary>
public sealed class CollectionDelegateMemberTests
{
    private const string LIST_ITEM = "alloc:State..ctor()#ListItem";
    private const string SET_ITEM = "alloc:State..ctor()#SetItem";
    private const string ARRAY_ITEM = "alloc:State..ctor()#ArrayItem";
    private const string SPARE = "alloc:State..ctor()#Spare";

    [Fact]
    public void List_FindIndex_and_FindLastIndex_overloads_all_run_their_predicate()
    {
        var overloads = ListMethods("FindIndex").Concat(ListMethods("FindLastIndex")).ToArray();
        var run = Run("_ = _state.Items.FindIndex(x => { x.Hits = 1; return true; });\n" +
                      "_ = _state.Items.FindIndex(0, x => { x.Hits = 2; return true; });\n" +
                      "_ = _state.Items.FindIndex(0, 1, x => { x.Hits = 3; return true; });\n" +
                      "_ = _state.Items.FindLastIndex(x => { x.Hits = 4; return true; });\n" +
                      "_ = _state.Items.FindLastIndex(0, x => { x.Hits = 5; return true; });\n" +
                      "_ = _state.Items.FindLastIndex(0, 1, x => { x.Hits = 6; return true; });");

        Assert.Equal(6, overloads.Length);
        Assert.All(overloads, method => Assert.Single(Assert.IsType<IrCollectionCall>(IrLowering.Collections.Of(method)).Factories));
        for (var value = 1; value <= 6; value++)
            Assert.Equal([LIST_ITEM], Written(run, "Hits", $"x.Hits = {value};"));
    }

    [Fact]
    public void List_ForEach_runs_its_action_on_each_element()
    {
        var run = Run("_state.Items.ForEach(x => x.Hits = 1);");

        Assert.Equal([LIST_ITEM], Written(run, "Hits"));
        AssertRunsInTheWorker(run, "Hits");
    }

    [Fact]
    public void List_ForEach_reads_the_structure_and_every_cell()
    {
        var run = Run("_state.Items.ForEach(x => { });");

        Assert.Equal(["cell Read", "structure Read"], OnCollection(run, "Items"));
    }

    [Fact]
    public void List_Sort_with_a_comparison_writes_the_structure_and_every_cell()
    {
        var run = Run("_state.Items.Sort((a, b) => 0);");

        Assert.Equal(["cell Write", "structure Write"], OnCollection(run, "Items"));
    }

    [Fact]
    public void List_Sort_comparison_is_handed_two_elements()
    {
        var run = Run("_state.Items.Sort((a, b) =>\n{\na.Hits = 1;\nb.Value = 2;\nreturn 0;\n});");

        Assert.Equal([LIST_ITEM], Written(run, "Hits"));
        Assert.Equal([LIST_ITEM], Written(run, "Value"));
    }

    [Fact]
    public void List_Sort_without_a_comparison_is_not_in_the_table()
    {
        var sorts = ListMethods("Sort").ToArray();

        Assert.Equal(4, sorts.Length);
        Assert.All(sorts, method => Assert.Equal(method.Parameters is [{ Type.Name: "Comparison" }], IrLowering.Collections.Of(method) is not null));
        Assert.Single(sorts, method => IrLowering.Collections.Of(method) is not null);
    }

    [Fact]
    public void List_RemoveAll_writes_the_structure_and_every_cell()
    {
        var run = Run("_state.Items.RemoveAll(x => false);");

        Assert.Equal(["cell Write", "structure Write"], OnCollection(run, "Items"));
    }

    [Fact]
    public void List_Exists_and_TrueForAll_read_only()
    {
        var run = Run("_ = _state.Items.Exists(x => false);\n_ = _state.Items.TrueForAll(x => true);");

        Assert.Equal(["cell Read", "structure Read"], OnCollection(run, "Items", "Exists"));
        Assert.Equal(["cell Read", "structure Read"], OnCollection(run, "Items", "TrueForAll"));
    }

    [Fact]
    public void List_Find_hands_out_a_held_element()
    {
        var run = Run("_state.Items.Find(x => true)!.Hits = 1;\n_state.Items.FindLast(x => true)!.Value = 1;");

        Assert.Equal([LIST_ITEM], Written(run, "Hits"));
        Assert.Equal([LIST_ITEM], Written(run, "Value"));
    }

    [Fact]
    public void List_FindIndex_and_FindLastIndex_read_only()
    {
        var run = Run("_ = _state.Items.FindIndex(x => false);\n_ = _state.Items.FindLastIndex(x => false);");

        Assert.Equal(["cell Read", "structure Read"], OnCollection(run, "Items", "FindIndex"));
        Assert.Equal(["cell Read", "structure Read"], OnCollection(run, "Items", "FindLastIndex"));
    }

    [Fact]
    public void List_FindAll_returns_a_list_holding_the_elements()
    {
        var run = Run("var found = _state.Items.FindAll(x => true);\nfound[0].Hits = 1;\nfound.Clear();");

        Assert.Equal([LIST_ITEM], Written(run, "Hits"));
        // The list FindAll returns is one of its own: clearing it writes nothing of the list it was called on.
        Assert.Equal(["cell Read", "structure Read"], OnCollection(run, "Items"));
    }

    [Fact]
    public void List_ConvertAll_returns_a_list_holding_what_the_converter_returned()
    {
        var run = Run("var converted = _state.Items.ConvertAll(x => (Item)_state.Spare);\nconverted[0].Hits = 1;\n_state.Items[0].Value = 2;");

        Assert.Equal([SPARE], Written(run, "Hits"));
        // What the converter returns goes into the new list alone, never into the list it was called on.
        Assert.Equal([LIST_ITEM], Written(run, "Value"));
    }

    [Fact]
    public void HashSet_RemoveWhere_writes_the_structure_and_every_cell()
    {
        var run = Run("_state.Set.RemoveWhere(x => false);");

        Assert.Equal(["cell Write", "structure Write"], OnCollection(run, "Set"));
    }

    [Fact]
    public void Collection_delegate_runs_under_the_callers_lock()
    {
        var run = Run("lock (_state.Gate) { _state.Items.ForEach(x => _state.Count = 1); }", other: "lock (_state.Gate) { _state.Count = 2; }");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.NotEmpty(write.HeldProtection);
        Assert.Empty(run.PairsOn("Count"));
    }

    [Fact]
    public void Collection_delegate_is_not_delegate_to_opaque()
    {
        var run = Run("_state.Items.ForEach(x => { });\n_state.Items.Sort((a, b) => 0);\n_state.Items.RemoveAll(x => false);\n" +
                      "_ = _state.Items.Exists(x => false);\n_ = _state.Items.TrueForAll(x => true);\n_ = _state.Items.Find(x => true);\n" +
                      "_ = _state.Items.FindLast(x => true);\n_ = _state.Items.FindIndex(x => true);\n_ = _state.Items.FindLastIndex(x => true);\n" +
                      "_ = _state.Items.FindAll(x => true);\n_ = _state.Items.ConvertAll(x => x);\n_state.Set.RemoveWhere(x => false);");
        var without = Run("");

        Assert.Equal(without.Counter(CoverageCounters.DELEGATE_TO_OPAQUE), run.Counter(CoverageCounters.DELEGATE_TO_OPAQUE));
        Assert.DoesNotContain(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownDelegateCall);
    }

    [Fact]
    public void Every_Array_static_with_a_delegate_is_modelled_or_excluded()
    {
        var statics = Type("System.Array").GetMembers().OfType<IMethodSymbol>()
                                         .Where(method => method is { DeclaredAccessibility: Accessibility.Public, IsStatic: true } &&
                                                          method.Parameters.Any(parameter => parameter.Type.TypeKind == TypeKind.Delegate) &&
                                                          !method.Parameters.Any(parameter => parameter.Type.OriginalDefinition.Name == "IComparer"))
                                         .ToArray();
        var modelled = LibraryModels.BuiltIn.Members.Select(member => member.Id).ToHashSet(StringComparer.Ordinal);
        var sort = Assert.Single(statics, method => method.Name == "Sort");

        Assert.Equal(["ConvertAll", "Exists", "Find", "FindAll", "FindIndex", "FindIndex", "FindIndex", "FindLast", "FindLastIndex", "FindLastIndex",
                      "FindLastIndex", "ForEach", "Sort", "TrueForAll"],
                     statics.Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.Equal("M:System.Array.Sort``1(``0[],System.Comparison{``0})", DocumentationCommentId.CreateDeclarationId(sort));
        Assert.DoesNotContain(DocumentationCommentId.CreateDeclarationId(sort)!, modelled);
        Assert.All(statics.Where(method => method.Name != "Sort"), method => Assert.Contains(Id(method), modelled));
    }

    [Fact]
    public void Array_Sort_with_a_comparison_stays_opaque()
    {
        var run = Run("System.Array.Sort(_state.Cells, (a, b) => { _state.Count = 1; return 0; });");
        var without = Run("");

        Assert.Equal(1, run.Counter(CoverageCounters.OPAQUE_CALL) - without.Counter(CoverageCounters.OPAQUE_CALL));
        Assert.Equal(1, run.Counter(CoverageCounters.DELEGATE_TO_OPAQUE) - without.Counter(CoverageCounters.DELEGATE_TO_OPAQUE));
        Assert.Contains(Worker(run, "Count"), access => KindOf(run, access) == ExecutionKind.UnknownDelegateCall);
    }

    /// <summary>One row per member of R7 that takes a delegate: the delegate runs in the worker's own execution, handed what the cells
    /// hold, and a write through what the call returns lands where R7 says — nothing for a member returning no object.</summary>
    [Theory]
    [InlineData("ForEach", "_state.Items.ForEach(x => { x.Hits = 1; });", LIST_ITEM, "")]
    [InlineData("Sort", "_state.Items.Sort((x, y) => { x.Hits = 1; return 0; });", LIST_ITEM, "")]
    [InlineData("RemoveAll", "_state.Items.RemoveAll(x => { x.Hits = 1; return false; });", LIST_ITEM, "")]
    [InlineData("Exists", "_ = _state.Items.Exists(x => { x.Hits = 1; return false; });", LIST_ITEM, "")]
    [InlineData("TrueForAll", "_ = _state.Items.TrueForAll(x => { x.Hits = 1; return true; });", LIST_ITEM, "")]
    [InlineData("Find", "_state.Items.Find(x => { x.Hits = 1; return true; })!.Value = 9;", LIST_ITEM, LIST_ITEM)]
    [InlineData("FindLast", "_state.Items.FindLast(x => { x.Hits = 1; return true; })!.Value = 9;", LIST_ITEM, LIST_ITEM)]
    [InlineData("FindIndex", "_ = _state.Items.FindIndex(x => { x.Hits = 1; return true; });", LIST_ITEM, "")]
    [InlineData("FindLastIndex", "_ = _state.Items.FindLastIndex(x => { x.Hits = 1; return true; });", LIST_ITEM, "")]
    [InlineData("FindAll", "_state.Items.FindAll(x => { x.Hits = 1; return true; })[0].Value = 9;", LIST_ITEM, LIST_ITEM)]
    [InlineData("ConvertAll", "_state.Items.ConvertAll(x => { x.Hits = 1; return (Item)_state.Spare; })[0].Value = 9;", LIST_ITEM, SPARE)]
    [InlineData("RemoveWhere", "_state.Set.RemoveWhere(x => { x.Hits = 1; return false; });", SET_ITEM, "")]
    public void List_member_runs_its_delegate(string member, string statement, string handed, string result) =>
        AssertRunsItsDelegate(member, statement, handed, result);

    /// <summary>One row per modelled <c>System.Array</c> static: the same three checks over the array's cells.</summary>
    [Theory]
    [InlineData("ForEach", "System.Array.ForEach(_state.Cells, x => { x.Hits = 1; });", "")]
    [InlineData("Exists", "_ = System.Array.Exists(_state.Cells, x => { x.Hits = 1; return false; });", "")]
    [InlineData("TrueForAll", "_ = System.Array.TrueForAll(_state.Cells, x => { x.Hits = 1; return true; });", "")]
    [InlineData("Find", "System.Array.Find(_state.Cells, x => { x.Hits = 1; return true; })!.Value = 9;", ARRAY_ITEM)]
    [InlineData("FindLast", "System.Array.FindLast(_state.Cells, x => { x.Hits = 1; return true; })!.Value = 9;", ARRAY_ITEM)]
    [InlineData("FindIndex", "_ = System.Array.FindIndex(_state.Cells, x => { x.Hits = 1; return true; });", "")]
    [InlineData("FindLastIndex", "_ = System.Array.FindLastIndex(_state.Cells, x => { x.Hits = 1; return true; });", "")]
    [InlineData("FindAll", "System.Array.FindAll(_state.Cells, x => { x.Hits = 1; return true; })[0].Value = 9;", ARRAY_ITEM)]
    [InlineData("ConvertAll", "System.Array.ConvertAll(_state.Cells, x => { x.Hits = 1; return (Item)_state.Spare; })[0].Value = 9;", SPARE)]
    public void Array_static_runs_its_delegate(string member, string statement, string result) =>
        AssertRunsItsDelegate(member, statement, ARRAY_ITEM, result);

    // ---- helpers ----

    private static void AssertRunsItsDelegate(string member, string statement, string handed, string result)
    {
        Assert.Contains($".{member}(", statement, StringComparison.Ordinal);
        var run = Run(statement);

        Assert.Equal([handed], Written(run, "Hits"));
        AssertRunsInTheWorker(run, "Hits");
        Assert.Equal(result.Length == 0 ? [] : [result], Written(run, "Value"));
        Assert.DoesNotContain(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownDelegateCall);
    }

    private static void AssertRunsInTheWorker(EngineRun run, string member)
    {
        var accesses = Worker(run, member);
        Assert.NotEmpty(accesses);
        Assert.All(accesses, access => Assert.Equal(ExecutionKind.Root, KindOf(run, access)));
    }

    private static ExecutionKind KindOf(EngineRun run, Access access) => run.Execution.Analysis.Execution(access.ExecutionId).Kind;

    private static IReadOnlyList<Access> Worker(EngineRun run, string member) =>
        run.Of(member).Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal)).ToArray();

    /// <summary>The objects the worker writes a member of, at the line holding <paramref name="at"/> where one is given.</summary>
    private static string[] Written(EngineRun run, string member, string? at = null) =>
        Worker(run, member).Where(access => access.Operation == AccessOperation.Write && (at is null || access.Source.StartLine == Line(at)))
                           .Select(access => access.Resource.Region)
                           .Distinct()
                           .Order(StringComparer.Ordinal)
                           .ToArray();

    /// <summary>What the worker does to the collection in <paramref name="field"/>, on the line calling <paramref name="call"/> where one
    /// is given: its structure and its cells, each with its operation.</summary>
    private static string[] OnCollection(EngineRun run, string field, string? call = null) =>
        run.Collection.Accesses.Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) && access.Resource.CollectionId is not null &&
                                                access.Resource.AccessPath[0] == field &&
                                                (call is null || access.Source.StartLine == Line($".{call}(")))
                               .Select(access => $"{(access.Resource.Selector is null ? "structure" : "cell")} {access.Operation}")
                               .Distinct()
                               .Order(StringComparer.Ordinal)
                               .ToArray();

    /// <summary>The text of the case file the last run analysed.</summary>
    private static string? _text;

    private static int Line(string at) =>
        Array.FindIndex(_text!.Split('\n'), line => line.Contains(at, StringComparison.Ordinal)) + 1 is var found and > 0
            ? found
            : throw new InvalidOperationException($"no line holds '{at}'");

    private static readonly Lazy<Compilation> Probe = new(() =>
        FixtureSolution.Create(("Case.cs", "public sealed class Placeholder { }")).Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!);

    private static INamedTypeSymbol Type(string metadataName) => Probe.Value.GetTypeByMetadataName(metadataName)!;

    private static IEnumerable<IMethodSymbol> ListMethods(string name) =>
        Type("System.Collections.Generic.List`1").GetMembers(name).OfType<IMethodSymbol>().Where(method => method.DeclaredAccessibility == Accessibility.Public);

    private static string Id(IMethodSymbol method) => DocumentationCommentId.CreateDeclarationId(method)!;

    private static EngineRun Run(string work, string other = "")
    {
        _text = Usings + Source(work, other);
        return AnalyzeScope(FixtureSolution.Create(new FixtureOptions(), ("Case.cs", _text)), "scope:Fixture");
    }

    /// <summary>A singleton holding a list, a set and an array, each filled at construction with an object of a type of its own, a spare
    /// object outside them all and a lock; a worker doing <paramref name="work"/> on it and a reader doing <paramref name="other"/>.</summary>
    private static string Source(string work, string other) => $$"""
        using System.Collections.Generic;

        public class Item { public int Hits; public int Value; }
        public sealed class ListItem : Item { }
        public sealed class SetItem : Item { }
        public sealed class ArrayItem : Item { }
        public sealed class Spare : Item { }

        public sealed class State
        {
            public readonly List<Item> Items = new();
            public readonly HashSet<Item> Set = new();
            public readonly Item[] Cells = new Item[1];
            public readonly Spare Spare = new Spare();
            public readonly object Gate = new();
            public int Count;

            public State()
            {
                Items.Add(new ListItem());
                Set.Add(new SetItem());
                Cells[0] = new ArrayItem();
            }
        }

        public sealed class Worker : BackgroundService
        {
            private readonly State _state;
            public Worker(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }
        }

        public sealed class Reader : BackgroundService
        {
            private readonly State _state;
            public Reader(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{other}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>(); services.AddHostedService<Reader>();");
}
