using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class MethodSummaryTests
{
    [Fact]
    public void Static_store_and_load_are_accesses_without_a_base()
    {
        var summary = Summarize("class C { static object? Value; void M() { Value = Value; } }");

        Assert.Equal([SummaryAccessKind.Load, SummaryAccessKind.Store], summary.Accesses.Select(access => access.Kind));
        Assert.All(summary.Accesses, access => Assert.Equal(("Value", true), (access.Field.Name, access.Field.IsStatic)));
        Assert.All(summary.Accesses, access => Assert.Empty(access.Bases));
        var load = summary.Accesses[0];
        Assert.Equal([new StaticFieldValue(load.Field)], load.Values);
        Assert.Equal([new LoadDependency(load.OperationId)], summary.Accesses[1].Dependencies);
        Assert.Single(summary.Stores);
    }

    [Fact]
    public void Auto_property_store_is_keyed_on_its_backing_field()
    {
        var summary = Summarize("class C { public int Count { get; set; } void M() { Count = 1; } }");

        var store = Assert.Single(summary.Accesses);
        Assert.Equal((SummaryAccessKind.Store, "Count", IrFieldKind.PropertyBackingField), (store.Kind, store.Field.Name, store.Field.Kind));
        Assert.Equal([ThisValue.Instance], store.Bases);
        Assert.Empty(store.Dependencies);
        Assert.Empty(summary.Stores);
    }

    [Fact]
    public void Compound_assignment_store_depends_on_its_load()
    {
        var summary = Summarize("class C { int _total; void M(int amount) { _total += amount; } }");

        var load = Assert.Single(summary.Accesses, access => access.Kind == SummaryAccessKind.Load);
        var store = Assert.Single(summary.Accesses, access => access.Kind == SummaryAccessKind.Store);
        Assert.Equal(load.OperationId, store.ReadModifyWriteOf);
        Assert.Equal(new HashSet<ValueDependency> { new LoadDependency(load.OperationId), new ParameterDependency(0) }, store.Dependencies);
    }

    [Fact]
    public void Auto_property_incremented_by_assignment_depends_on_its_load()
    {
        var summary = Summarize("class C { public int Views { get; set; } void M() { Views = Views + 1; } }");

        var load = Assert.Single(summary.Accesses, access => access.Kind == SummaryAccessKind.Load);
        var store = Assert.Single(summary.Accesses, access => access.Kind == SummaryAccessKind.Store);
        Assert.Null(store.ReadModifyWriteOf);
        Assert.Equal([new LoadDependency(load.OperationId)], store.Dependencies);
    }

    [Fact]
    public void Read_compute_write_over_three_statements_depends_on_the_load()
    {
        var summary = Summarize("class C { int _split; void M(int amount) { var current = _split; var next = current + amount; _split = next; } }");

        var load = Assert.Single(summary.Accesses, access => access.Kind == SummaryAccessKind.Load);
        var store = Assert.Single(summary.Accesses, access => access.Kind == SummaryAccessKind.Store);
        Assert.Contains(new LoadDependency(load.OperationId), store.Dependencies);
    }

    [Fact]
    public void Store_under_a_condition_on_the_read_with_an_independent_value_has_no_dependency()
    {
        var summary = Summarize("class C { int _level; void M() { if (_level > 0) { _level = 5; } } }");

        var store = Assert.Single(summary.Accesses, access => access.Kind == SummaryAccessKind.Store);
        Assert.Empty(store.Dependencies);
    }

    [Fact]
    public void Getter_return_depends_on_its_load_and_setter_store_on_its_parameter()
    {
        const string SOURCE = "class C { int _level; int GetLevel() => _level; void SetLevel(int level) => _level = level; }";
        var getter = Summarize(SOURCE, "GetLevel");
        var setter = Summarize(SOURCE, "SetLevel");

        var load = Assert.Single(getter.Accesses);
        Assert.Equal([new LoadDependency(load.OperationId)], Assert.Single(getter.Returns).Dependencies);
        Assert.Equal([new ParameterDependency(0)], Assert.Single(setter.Accesses).Dependencies);
    }

    [Fact]
    public void Out_parameter_final_value_is_recorded()
    {
        var summary = Summarize("class C { void M(bool flag, out object memo) { if (flag) memo = new object(); else memo = new C(); } }");

        var final = Assert.Single(summary.RefParameters);
        Assert.Equal(1, final.Ordinal);
        var sites = final.Values.OfType<AllocationValue>().Select(value => value.Site).ToArray();
        Assert.Equal(2, sites.Length);
        Assert.Contains(sites, site => site.TypeKey == "Fixture:C");
        Assert.All(sites, site => Assert.Equal("body:Fixture:M:C.M(System.Boolean,System.Object@)", site.BodyId));
    }

    [Fact]
    public void Lambda_captures_this_and_every_version_of_a_local()
    {
        var summary = Summarize("""
            class C
            {
                int _field;
                void M()
                {
                    object local = new object();
                    System.Action action = () => { System.GC.KeepAlive(local); _field = 1; };
                    local = new C();
                }
            }
            """);

        var created = Assert.Single(summary.Delegates).Delegate;
        Assert.EndsWith("#lambda1", created.Target, StringComparison.Ordinal);
        Assert.Equal([ThisValue.Instance], created.CapturedValues["this"]);
        var local = Assert.Single(created.CapturedValues, pair => pair.Key != "this");
        Assert.Contains("|local|", local.Key, StringComparison.Ordinal);
        Assert.Equal(2, local.Value.OfType<AllocationValue>().Count());
    }

    [Fact]
    public void Captured_variable_dependency_is_recorded_on_both_sides()
    {
        const string SOURCE = """
            class C
            {
                int _count;
                void M()
                {
                    var seen = _count;
                    System.Action action = () => _count = seen + 1;
                }
            }
            """;
        var (outer, nested) = SummarizeWithNested(SOURCE);

        var key = Assert.Single(outer.Delegates).Delegate.CapturedValues.Keys.Single(candidate => candidate != "this");
        var store = Assert.Single(nested.Accesses, access => access.Kind == SummaryAccessKind.Store);
        Assert.Equal([new CapturedDependency(key)], store.Dependencies);
        var load = Assert.Single(outer.Accesses, access => access.Kind == SummaryAccessKind.Load);
        Assert.Contains(new LoadDependency(load.OperationId), Assert.Single(outer.Variables, variable => variable.SymbolKey == key).Dependencies);
    }

    [Fact]
    public void Constructor_storing_this_into_a_static_is_a_transfer_of_this()
    {
        var summary = Summarize("class C { static C? Last; public string? Status; public C() { Last = this; Status = \"starting\"; } }", ".ctor");

        var transfer = Assert.Single(summary.Stores, store => store.Field.Name == "Last");
        Assert.True(transfer.Field.IsStatic);
        Assert.Equal([ThisValue.Instance], transfer.Values);
        Assert.Contains(summary.Accesses, access => access is { Kind: SummaryAccessKind.Store, Field.Name: "Status" });
    }

    [Fact]
    public void Thirteen_segment_path_collapses_to_a_wildcard_at_the_default_limit_only()
    {
        var levels = string.Concat(Enumerable.Range(1, 11).Select(level => $"class Level{level} {{ public Level{level + 1} Next {{ get; }} = new(); }}\n"));
        var source = levels + """
            class Level12 { public string? Value { get; set; } }
            class C
            {
                public Level1 First { get; } = new();
                void M(string value) => First.Next.Next.Next.Next.Next.Next.Next.Next.Next.Next.Next.Value = value;
            }
            """;

        var collapsed = Assert.Single(Summarize(source).Accesses, access => access is { Kind: SummaryAccessKind.Store, Field.Name: "Value" });
        var kept = Assert.Single(Summarize(source, limits: new AnalysisLimits(MaxAccessPathDepth: 20)).Accesses,
                                 access => access is { Kind: SummaryAccessKind.Store, Field.Name: "Value" });

        var wildcard = Assert.IsType<PathValue>(Assert.Single(collapsed.Bases));
        Assert.True(wildcard.IsWildcard);
        Assert.Equal(ThisValue.Instance, wildcard.Base);
        var path = Assert.IsType<PathValue>(Assert.Single(kept.Bases));
        Assert.False(path.IsWildcard);
        Assert.Equal(12, path.Segments.Count);
    }

    [Fact]
    public void Element_store_is_an_element_operation_carrying_its_value()
    {
        var summary = Summarize("class C { void M(object[] items, object item) { items[0] = item; } }");

        var element = Assert.Single(summary.Elements);
        Assert.Equal(ElementOperationKind.Store, element.Kind);
        Assert.Equal([new ParameterValue(0)], element.Arrays);
        Assert.Equal([new ParameterValue(1)], element.Values);
        Assert.Empty(summary.Accesses);
    }

    [Fact]
    public void Spawn_call_is_a_spawn_event_whose_work_is_not_a_delegate_of_the_opaque_call()
    {
        var summary = Summarize("""
            class C
            {
                int _field;
                void M(int[] items)
                {
                    System.Threading.Tasks.Task.Run(() => _field = 1);
                    System.GC.KeepAlive(System.Linq.Enumerable.Select(items, item => item));
                }
            }
            """);

        var run = Assert.Single(summary.OpaqueCalls, call => call.Callee.StartsWith("System.Threading.Tasks.Task.Run", StringComparison.Ordinal));
        Assert.Empty(run.Delegates);
        Assert.DoesNotContain(summary.Calls, call => call.OperationId == run.OperationId);
        var spawn = Assert.Single(summary.Spawns);
        Assert.Equal((IrSpawnKind.TaskRun, run.OperationId), (spawn.Kind, spawn.CallOperationId));
        Assert.EndsWith("#lambda1", Assert.IsType<DelegateCreationValue>(Assert.Single(Assert.Single(spawn.Work).Values)).Target, StringComparison.Ordinal);
        Assert.Equal([new CallResultValue(run.OperationId)], spawn.Handle!.Values);
        Assert.Empty(spawn.Handle.UnknownSources);
        var select = Assert.Single(summary.OpaqueCalls, call => call.Callee.StartsWith("System.Linq.Enumerable.Select", StringComparison.Ordinal));
        Assert.EndsWith("#lambda2", Assert.Single(select.Delegates).Target, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_call_binds_named_arguments_to_their_parameters()
    {
        var summary = Summarize("class C { static void Take(int count, object item) { } void M(object item) { Take(item: item, count: 1); } }");

        var call = Assert.Single(summary.Calls);
        Assert.Equal("body:Fixture:M:C.Take(System.Int32,System.Object)", call.Target);
        Assert.Equal([new ParameterValue(0)], Assert.Single(call.Arguments, argument => argument.ParameterOrdinal == 1).Values);
        Assert.Empty(Assert.Single(call.Arguments, argument => argument.ParameterOrdinal == 0).Values);
    }

    [Fact]
    public void Store_under_a_lock_carries_the_lock_values()
    {
        var summary = Summarize("class C { static readonly object Gate = new(); int _next; void M() { lock (Gate) { _next = 1; } } }");

        var store = Assert.Single(summary.Accesses, access => access.Kind == SummaryAccessKind.Store);
        var held = Assert.Single(store.HeldLocks);
        Assert.Equal("Gate", Assert.IsType<StaticFieldValue>(Assert.Single(held.Values)).Field.Name);
    }

    private static MethodSummary Summarize(string source, string member = "M", AnalysisLimits? limits = null)
    {
        var (compilation, index) = Compile(source);
        var method = compilation.GetTypeByMetadataName("C")!.GetMembers(member).OfType<IMethodSymbol>().Single();
        var body = IrLowering.Lower(method, compilation, EngineFixture.ROOT_DIRECTORY, CancellationToken.None).Body;
        return MethodSummaryBuilder.Build(body, index, limits ?? AnalysisLimits.Default);
    }

    private static (MethodSummary Outer, MethodSummary Nested) SummarizeWithNested(string source)
    {
        var (compilation, index) = Compile(source);
        var method = compilation.GetTypeByMetadataName("C")!.GetMembers("M").OfType<IMethodSymbol>().Single();
        var lowered = IrLowering.Lower(method, compilation, EngineFixture.ROOT_DIRECTORY, CancellationToken.None);
        return (MethodSummaryBuilder.Build(lowered.Body, index, AnalysisLimits.Default),
                MethodSummaryBuilder.Build(Assert.Single(lowered.NestedBodies), index, AnalysisLimits.Default));
    }

    private static (Compilation Compilation, CallGraph.ProgramIndex Index) Compile(string source)
    {
        var compilation = FixtureSolution.Create(("Case.cs", source)).Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        return (compilation, ProgramIndexBuilder.Build("scope:Fixture", [compilation], EngineFixture.ROOT_DIRECTORY, CancellationToken.None));
    }
}
