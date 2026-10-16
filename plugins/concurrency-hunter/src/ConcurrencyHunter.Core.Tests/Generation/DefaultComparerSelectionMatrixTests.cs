using System.Collections.Concurrent;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The comparer CoreLib's default-comparer factory makes, its mark and its instance (R11): small programs run as
/// <c>CoreLibGenerationSetMatrixTests</c> runs them, under <see cref="DispatchRule.ConstructedTypes"/>, the rule of the only generation where
/// CoreLib's bodies are source. The project <c>Core</c> holds <c>ComparerHelpers</c>, <c>Comparer&lt;T&gt;</c>, <c>EqualityComparer&lt;T&gt;</c>
/// and the comparer types in CoreLib's shapes, each <c>Default</c> filled by its type's initializer as CoreLib fills it, and the values X
/// compared; it is compiled as the assembly <c>System.Private.CoreLib</c>, or as <c>Impostor</c>. The project <c>Probes</c> is the open-world
/// assembly, whose struct implements both generic interfaces as a probe with its witnesses does. The hosted service <c>Worker</c> calls
/// <c>Values.Entry</c>, which compares two values of X through the cell's call of the factory.</summary>
public sealed class DefaultComparerSelectionMatrixTests
{
    private const string CORELIB = "System.Private.CoreLib";
    private const string IMPOSTOR = "Impostor";
    private const string PROBES = "Probes";

    /// <summary>The type the factory is asked for.</summary>
    public enum Compared
    {
        /// <summary>A class implementing <c>IComparable&lt;X&gt;</c> and <c>IEquatable&lt;X&gt;</c>.</summary>
        ClassComparable,

        /// <summary>A struct implementing both generic interfaces.</summary>
        StructComparable,

        /// <summary>A struct implementing neither.</summary>
        StructNeither,

        /// <summary>A <c>Nullable</c> of the comparable struct.</summary>
        NullableOfComparable,

        /// <summary>A <c>Nullable</c> of the struct implementing neither.</summary>
        NullableOfNeither,

        /// <summary>An enum.</summary>
        Enum,

        /// <summary><c>string</c>.</summary>
        String,

        /// <summary>A struct of the open-world assembly implementing both generic interfaces.</summary>
        OpenWorldStruct,

        /// <summary>A class implementing <c>IComparable&lt;object&gt;</c> and <c>IEquatable&lt;object&gt;</c>: assignable to
        /// <c>IComparable&lt;Variant&gt;</c> by the interface's contravariance, never to the invariant <c>IEquatable&lt;Variant&gt;</c>.</summary>
        Variant,

        /// <summary>A class deriving from <c>Base : IComparable&lt;Base&gt;, IEquatable&lt;Base&gt;</c>: assignable to
        /// <c>IComparable&lt;Derived&gt;</c> by contravariance, never to <c>IEquatable&lt;Derived&gt;</c>.</summary>
        Derived,

        /// <summary>A class whose conversion to its comparable argument itself uses covariance.</summary>
        NestedVariant,

        /// <summary>A struct implementing <c>IComparable&lt;object&gt;</c>: a value type converts to <c>object</c> by boxing, no reference
        /// conversion, so variance makes it no <c>IComparable&lt;StructVariant&gt;</c>.</summary>
        StructVariant
    }

    /// <summary>Which factory.</summary>
    public enum Kind
    {
        Ordering,
        Equality
    }

    /// <summary>Where <c>ComparerHelpers</c> is declared.</summary>
    public enum Declaration
    {
        /// <summary>The assembly <c>System.Private.CoreLib</c>.</summary>
        CoreLib,

        /// <summary>A type of the same name and signature in another assembly.</summary>
        Impostor
    }

    /// <summary>What the factory call is handed.</summary>
    public enum Argument
    {
        /// <summary><c>typeof(T)</c> of the containing generic type's parameter, in its initializer, as CoreLib writes it.</summary>
        TypeofParameter,

        /// <summary><c>typeof(X)</c> written at the call.</summary>
        TypeofClosed,

        /// <summary>A <c>Type</c> the calling member receives.</summary>
        NotTypeof
    }

    /// <summary>What the instance holding the call binds.</summary>
    public enum Binding
    {
        /// <summary>Every type parameter.</summary>
        Closed,

        /// <summary>None: an instance of the generic definition that leaves <c>T</c> open, which the merging of every context makes.</summary>
        Open
    }

    /// <summary>A cell of the matrix.</summary>
    /// <param name="Compared">The type the factory is asked for.</param>
    /// <param name="Kind">The factory.</param>
    /// <param name="Declaration">Where the factory is declared.</param>
    /// <param name="Argument">What the call is handed.</param>
    /// <param name="Binding">What the instance binds.</param>
    private sealed record Cell(Compared Compared, Kind Kind, Declaration Declaration, Argument Argument, Binding Binding)
    {
        public override string ToString() => $"{Compared}/{Kind}/{Declaration}/{Argument}/{Binding}";
    }

    /// <summary>What a cell gives at the factory call.</summary>
    /// <param name="Creates">The comparer type the marked call makes from its recorded binding, or null for an unmarked call.</param>
    /// <param name="Marked">Whether the lowering marks the call, so the reachable set reaches the selection's comparer there.</param>
    /// <param name="Reason">The requirement the expectation follows.</param>
    private sealed record Expectation(string? Creates, bool Marked, string Reason);

    /// <summary>The cells whose outcome is known to be wider than the requirement asks: none.</summary>
    private static readonly Cell[] KnownWider = [];

    private static readonly ConcurrentDictionary<string, Lazy<Solution>> Programs = new(StringComparer.Ordinal);

    // ---- the cells ----

    private static IEnumerable<Cell> Cells() =>
        from compared in Enum.GetValues<Compared>()
        from kind in Enum.GetValues<Kind>()
        from declaration in Enum.GetValues<Declaration>()
        from argument in Enum.GetValues<Argument>()
        from binding in Enum.GetValues<Binding>()
        let cell = new Cell(compared, kind, declaration, argument, binding)
        where Allowed(cell)
        select cell;

    /// <summary>Whether a combination is a cell: no substitution touches the argument of <see cref="Argument.TypeofClosed"/> or
    /// <see cref="Argument.NotTypeof"/>, so <see cref="Binding.Closed"/> covers them.</summary>
    /// <param name="cell">The combination.</param>
    private static bool Allowed(Cell cell) => cell.Binding == Binding.Closed || cell.Argument == Argument.TypeofParameter;

    public static TheoryData<Compared, Kind, Declaration, Argument, Binding> Rows()
    {
        var rows = new TheoryData<Compared, Kind, Declaration, Argument, Binding>();
        foreach (var cell in Cells())
            rows.Add(cell.Compared, cell.Kind, cell.Declaration, cell.Argument, cell.Binding);
        return rows;
    }

    public static TheoryData<Compared, Kind> HierarchyRows()
    {
        var rows = new TheoryData<Compared, Kind>();
        foreach (var compared in Enum.GetValues<Compared>())
            foreach (var kind in Enum.GetValues<Kind>())
                rows.Add(compared, kind);
        return rows;
    }

    // ---- the table ----

    /// <summary>What a cell gives, by R11's table: CoreLib's factory with a <c>typeof</c> whose type the instance closes makes exactly the
    /// comparer CoreLib 8.0's selection makes for X. When merging leaves the type open, its recorded closed binding still makes that
    /// comparer and the ordinary factory call runs too; an unmarked call remains the call it is and creates nothing.</summary>
    /// <param name="cell">The cell.</param>
    private static Expectation Expected(Cell cell)
    {
        var marked = cell.Declaration == Declaration.CoreLib && cell.Argument != Argument.NotTypeof;
        if (!marked)
            return new(null, marked, "R11: no closed type a typeof names, or no CoreLib factory");
        return new(Selected(cell.Compared, cell.Kind), true, "R11: CoreLib's selection for X");
    }

    /// <summary>The comparer CoreLib 8.0 makes for X, as a type key. The rows of the variance values were checked against the
    /// .NET 8.0.32 runtime, <c>Comparer&lt;X&gt;.Default</c> and <c>EqualityComparer&lt;X&gt;.Default</c> of the same declarations:
    /// <c>Variant</c> gives <c>GenericComparer&lt;Variant&gt;</c> and <c>ObjectEqualityComparer&lt;Variant&gt;</c>, <c>Derived</c>
    /// <c>GenericComparer&lt;Derived&gt;</c> and <c>ObjectEqualityComparer&lt;Derived&gt;</c>, <c>StructVariant</c>
    /// <c>ObjectComparer&lt;StructVariant&gt;</c> and <c>ObjectEqualityComparer&lt;StructVariant&gt;</c>. <c>NestedVariant</c>, a
    /// <c>List&lt;string&gt;</c> implementing <c>IComparable&lt;IEnumerable&lt;object&gt;&gt;</c> and the corresponding invariant equality
    /// interface, gives <c>GenericComparer&lt;NestedVariant&gt;</c> and <c>ObjectEqualityComparer&lt;NestedVariant&gt;</c>.</summary>
    /// <param name="compared">X.</param>
    /// <param name="kind">The factory.</param>
    private static string Selected(Compared compared, Kind kind)
    {
        var ordering = kind == Kind.Ordering;
        var (comparer, argument) = compared switch
        {
            Compared.ClassComparable or Compared.StructComparable or Compared.String or Compared.OpenWorldStruct =>
                (ordering ? "GenericComparer" : "GenericEqualityComparer", TypeKey(compared)),
            Compared.Variant or Compared.Derived or Compared.NestedVariant => (ordering ? "GenericComparer" : "ObjectEqualityComparer", TypeKey(compared)),
            Compared.StructNeither or Compared.StructVariant => (ordering ? "ObjectComparer" : "ObjectEqualityComparer", TypeKey(compared)),
            Compared.NullableOfComparable => (ordering ? "NullableComparer" : "NullableEqualityComparer", TypeKey(Compared.StructComparable)),
            Compared.NullableOfNeither => (ordering ? "NullableComparer" : "NullableEqualityComparer", TypeKey(Compared.StructNeither)),
            Compared.Enum => (ordering ? "EnumComparer" : "EnumEqualityComparer", TypeKey(compared)),
            _ => throw new ArgumentOutOfRangeException(nameof(compared), compared, null)
        };
        return $"{CORELIB}:System.Collections.Generic.{comparer}<{argument}>";
    }

    /// <summary>X's type key.</summary>
    /// <param name="compared">X.</param>
    private static string TypeKey(Compared compared) => compared switch
    {
        Compared.String => $"{CORELIB}:string",
        Compared.OpenWorldStruct => $"{PROBES}:Probes.OpenWorldStruct",
        Compared.NullableOfComparable => $"{CORELIB}:System.Nullable<{TypeKey(Compared.StructComparable)}>",
        Compared.NullableOfNeither => $"{CORELIB}:System.Nullable<{TypeKey(Compared.StructNeither)}>",
        Compared.Enum => $"{CORELIB}:Values.Color",
        _ => $"{CORELIB}:Values.{compared}"
    };

    // ---- the rows ----

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Every_cell_creates_as_expected(Compared compared, Kind kind, Declaration declaration, Argument argument, Binding binding)
    {
        var cell = new Cell(compared, kind, declaration, argument, binding);
        var failures = await Check(cell, DispatchRule.ConstructedTypes);
        Assert.True(failures.Count == 0, $"{cell} ({Expected(cell).Reason}):\n{string.Join("\n", failures)}");
    }

    [Theory]
    [MemberData(nameof(HierarchyRows))]
    public async Task Marks_reach_the_comparers_under_class_hierarchy_dispatch_too(Compared compared, Kind kind)
    {
        var cell = new Cell(compared, kind, Declaration.CoreLib, Argument.TypeofParameter, Binding.Closed);
        var failures = await Check(cell, DispatchRule.ClassHierarchy);
        Assert.True(failures.Count == 0, $"{cell} under class-hierarchy dispatch:\n{string.Join("\n", failures)}");
    }

    /// <summary>A closed binding makes its comparer regardless of how deeply its type arguments nest.</summary>
    /// <param name="layers">How many layers wrap the comparable type.</param>
    /// <param name="kind">The factory.</param>
    [Theory]
    [InlineData(8, Kind.Ordering)]
    [InlineData(8, Kind.Equality)]
    [InlineData(5, Kind.Ordering)]
    [InlineData(5, Kind.Equality)]
    public async Task A_deep_closed_binding_makes_its_comparer(int layers, Kind kind)
    {
        var cell = new Cell(Compared.ClassComparable, kind, Declaration.CoreLib, Argument.TypeofParameter, Binding.Closed);
        var (solution, core) = await Compilations(cell, layers);
        var run = await Run(solution, cell, DispatchRule.ConstructedTypes);
        Assert.False(run.Stopped);
        var heap = run.Heap!;
        var x = Layered(layers, $"{CORELIB}:Values.");
        var generic = kind == Kind.Ordering ? "GenericComparer" : "GenericEqualityComparer";
        var factoryBody = IrLowering.RootBodyId(Factory(core, kind));
        var site = Calls(core).Single(call => call.Target.OriginalDefinition.Equals(Factory(core, kind), SymbolEqualityComparer.Default) &&
                                             call.Caller.ContainingType.Name == KindType(kind));
        var siteBody = IrLowering.RootBodyId(site.Caller);
        var siteInstances = heap.Instances.Values.Where(instance => instance.BodyId == siteBody).ToArray();
        Assert.NotEmpty(siteInstances);
        var created = heap.Regions.Values.Where(region => region.SiteBodyId == siteBody && region.SiteOperationId is not null).Select(region => region.TypeKey).ToArray();
        var factoryRuns = heap.Edges.Any(edge => siteInstances.Any(instance => instance.Id == edge.CallerInstance) &&
                                                 heap.Instances[edge.CalleeInstance].BodyId == factoryBody);
        var reachedBySite = run.Reachable.ReachedBodies.GetValueOrDefault(factoryBody)?.StartsWith($"call:{siteBody}:", StringComparison.Ordinal) == true;
        var constructor = IrLowering.RootBodyId(core.Assembly.GetTypeByMetadataName($"System.Collections.Generic.{generic}`1")!.InstanceConstructors.Single());

        Assert.Contains((kind == Kind.Ordering ? IrComparerKind.Ordering : IrComparerKind.Equality, x), run.Reachable.DefaultComparerTypes);
        Assert.Equal(new[] { $"{CORELIB}:System.Collections.Generic.{generic}<{x}>" }, created);
        Assert.Contains(constructor, run.Reachable.ReachedBodies.Keys);
        Assert.Contains(heap.Instances.Values, instance => instance.BodyId == constructor);
        Assert.False(reachedBySite);
        Assert.False(factoryRuns);
    }

    /// <summary>Closed binding traverses arrays of any rank and arrays inside generic arguments.</summary>
    /// <param name="template">The type template over the caller's parameter.</param>
    /// <param name="kind">The factory.</param>
    [Theory]
    [InlineData("ArrayVariant<T[]>", Kind.Ordering)]
    [InlineData("ArrayVariant<T[,]>", Kind.Ordering)]
    [InlineData("ArrayVariant<T[][,]>", Kind.Ordering)]
    [InlineData("ArrayVariant<T>[]", Kind.Ordering)]
    [InlineData("ArrayVariant<T[]>", Kind.Equality)]
    [InlineData("ArrayVariant<T>[]", Kind.Equality)]
    [InlineData("Layer<T?>", Kind.Ordering)]
    [InlineData("Layer<(T, int)>", Kind.Ordering)]
    [InlineData("T[]", Kind.Ordering)]
    [InlineData("T[,]", Kind.Ordering)]
    [InlineData("Layer<T[]>", Kind.Ordering)]
    [InlineData("Layer<T>", Kind.Ordering)]
    [InlineData("Layer<T[,]>", Kind.Ordering)]
    [InlineData("T[,][]", Kind.Ordering)]
    [InlineData("T[]", Kind.Equality)]
    [InlineData("T[,]", Kind.Equality)]
    [InlineData("Layer<T[]>", Kind.Equality)]
    [InlineData("Layer<T>", Kind.Equality)]
    [InlineData("Layer<T[,]>", Kind.Equality)]
    [InlineData("T[,][]", Kind.Equality)]
    public async Task Templates_keep_every_source_parameter(string template, Kind kind)
    {
        var cell = new Cell(Compared.ClassComparable, kind, Declaration.CoreLib, Argument.TypeofParameter, Binding.Closed);
        var (source, worker) = Sources(cell);
        if (template.Contains("?", StringComparison.Ordinal))
            worker = worker.Replace("new Values.ClassComparable()", "new Values.StructComparable()");
        var type = KindType(kind);
        var member = kind == Kind.Ordering ? "Compare" : "Equals";
        source = source.Replace($"public static {(kind == Kind.Ordering ? "int" : "bool")} Run<T>(T x, T y) => {type}<T>.Default.{member}(x, y);",
                                $"public static object Run<T>(T x, T y){(template.Contains("?", StringComparison.Ordinal) ? " where T : struct" : "")} => {type}<{template}>.Default;");
        var solution = FixtureSolution.CreateProjects(new FixtureOptions
        {
            ProjectAssemblyNames = new Dictionary<string, string> { ["Core"] = CORELIB },
            ProjectReferences = [("Fixture", "Core"), ("Fixture", PROBES), ("Core", PROBES)]
        }, (PROBES, "Probes.cs", PROBES_SOURCE), ("Core", "Core.cs", source), ("Fixture", "Case.cs", worker));
        var run = await Run(solution, cell, DispatchRule.ConstructedTypes);
        Assert.False(run.Stopped);
        // C# writes the outer rank first; the type key appends each outer rank to its element's key.
        var argument = TypeKey(template.Contains("?", StringComparison.Ordinal) ? Compared.StructComparable : Compared.ClassComparable);
        var ordered = template switch { "T[,][]" => "T[][,]", "ArrayVariant<T[][,]>" => "ArrayVariant<T[,][]>", _ => template };
        var closed = ordered.Replace("T", "$").Replace("$?", "System.Private.CoreLib:System.Nullable<$>")
                            .Replace("($, int)", "System.Private.CoreLib:System.ValueTuple<$, System.Private.CoreLib:int>")
                            .Replace("$", argument).Replace("Layer<", CORELIB + ":Values.Layer<")
                            .Replace("ArrayVariant<", CORELIB + ":Values.ArrayVariant<");
        var selected = template.StartsWith("ArrayVariant<", StringComparison.Ordinal) && !template.EndsWith("[]", StringComparison.Ordinal)
            ? kind == Kind.Ordering ? "GenericComparer" : "ObjectEqualityComparer"
            : template.StartsWith("Layer", StringComparison.Ordinal)
            ? kind == Kind.Ordering ? "GenericComparer" : "GenericEqualityComparer"
            : kind == Kind.Ordering ? "ObjectComparer" : "ObjectEqualityComparer";
        Assert.Contains((kind == Kind.Ordering ? IrComparerKind.Ordering : IrComparerKind.Equality, closed), run.Reachable.DefaultComparerTypes);
        Assert.Equal(selected.StartsWith("Generic", StringComparison.Ordinal), run.Program.ImplementsSelfInterface!(closed,
                     kind == Kind.Ordering ? "System.IComparable`1" : "System.IEquatable`1"));
        Assert.Contains(run.Heap.Regions.Values, region => region.TypeKey == $"{CORELIB}:System.Collections.Generic.{selected}<{closed}>");
    }

    /// <summary>X nested in <c>Layer&lt;…&gt;</c> <paramref name="layers"/> times around <c>ClassComparable</c>.</summary>
    /// <param name="layers">How many layers wrap it.</param>
    /// <param name="prefix">What each type name is prefixed with: a namespace, or a type key's assembly and namespace.</param>
    private static string Layered(int layers, string prefix) =>
        layers == 0 ? $"{prefix}ClassComparable" : $"{prefix}Layer<{Layered(layers - 1, prefix)}>";

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Only_corelibs_factory_with_a_typeof_is_marked(Compared compared, Kind kind, Declaration declaration, Argument argument, Binding binding)
    {
        var cell = new Cell(compared, kind, declaration, argument, binding);
        var (_, core) = await Compilations(cell);
        var factory = Factory(core, kind);
        var calls = Calls(core).Where(call => call.Target.OriginalDefinition.Equals(factory, SymbolEqualityComparer.Default)).ToArray();
        Assert.NotEmpty(calls);
        foreach (var (method, _, operation) in calls)
        {
            var lowered = IrLowering.Lower(method, core, ROOT_DIRECTORY, CancellationToken.None);
            var call = lowered.NestedBodies.Prepend(lowered.Body).SelectMany(body => body.Blocks).SelectMany(block => block.Operations)
                              .OfType<IrCallOperation>().Single(candidate => candidate.TargetMethodId == IrLowering.RootBodyId(factory));
            var typeOf = operation.Arguments.Single().Value is Microsoft.CodeAnalysis.Operations.ITypeOfOperation given ? given.TypeOperand : null;
            Assert.Equal(declaration == Declaration.CoreLib && typeOf is not null
                             ? new IrDefaultComparer(kind == Kind.Ordering ? IrComparerKind.Ordering : IrComparerKind.Equality, SymbolNames.TypeKey(typeOf!))
                             : null,
                         call.DefaultComparer);
        }
    }

    [Fact]
    public async Task No_other_member_is_claimed()
    {
        foreach (var declaration in Enum.GetValues<Declaration>())
        {
            var (_, core) = await Compilations(new Cell(Compared.ClassComparable, Kind.Ordering, declaration, Argument.TypeofParameter, Binding.Closed));
            var helpers = core.Assembly.GetTypeByMetadataName("System.Collections.Generic.ComparerHelpers")!;
            var members = helpers.GetMembers().OfType<IMethodSymbol>().Where(method => method.MethodKind == MethodKind.Ordinary).ToArray();
            Assert.Equal(3, members.Length);
            foreach (var member in members)
            {
                var claimed = declaration == Declaration.CoreLib && member.Name is "CreateDefaultComparer" or "CreateDefaultEqualityComparer";
                Assert.True(claimed == EngineClaims.Of(member) is not null, $"{declaration} {member.Name}: {EngineClaims.Of(member)}");
            }
        }
    }

    // ---- the facts ----

    /// <summary>Receiver contexts can merge with only one closed type binding. The merged call still makes that binding's comparer,
    /// and calls the factory only if reachability also reached it for an open binding.</summary>
    /// <param name="kind">The factory.</param>
    /// <param name="receivers">The number of separately allocated receivers, at or above the context limit.</param>
    /// <param name="dispatch">The reachable set's dispatch rule.</param>
    [Theory]
    [InlineData(Kind.Ordering, 16, DispatchRule.ConstructedTypes)]
    [InlineData(Kind.Ordering, 17, DispatchRule.ConstructedTypes)]
    [InlineData(Kind.Equality, 16, DispatchRule.ConstructedTypes)]
    [InlineData(Kind.Equality, 17, DispatchRule.ConstructedTypes)]
    [InlineData(Kind.Ordering, 17, DispatchRule.ClassHierarchy)]
    [InlineData(Kind.Equality, 17, DispatchRule.ClassHierarchy)]
    public async Task Receiver_merging_keeps_the_recorded_closed_comparer(Kind kind, int receivers, DispatchRule dispatch)
    {
        var cell = new Cell(Compared.ClassComparable, kind, Declaration.CoreLib, Argument.TypeofParameter, Binding.Closed);
        var (source, worker) = Sources(cell);
        var factoryName = kind == Kind.Ordering ? "CreateDefaultComparer" : "CreateDefaultEqualityComparer";
        source = source.Replace("public static class Entry", $$"""
            public sealed class Factory<T>
            {
                public object Make() => ComparerHelpers.{{factoryName}}(typeof(T));
            }
            public static class Entry
            """);
        worker = worker.Replace("Values.Entry.Run(new Values.ClassComparable(), new Values.ClassComparable());",
                                string.Join("\n", Enumerable.Repeat("new Values.Factory<Values.ClassComparable>().Make();", receivers)));
        var solution = FixtureSolution.CreateProjects(new FixtureOptions
        {
            ProjectAssemblyNames = new Dictionary<string, string> { ["Core"] = CORELIB },
            ProjectReferences = [("Fixture", "Core"), ("Fixture", PROBES), ("Core", PROBES)]
        }, (PROBES, "Probes.cs", PROBES_SOURCE), ("Core", "Core.cs", source), ("Fixture", "Case.cs", worker));
        var core = (await solution.Projects.Single(project => project.Name == "Core").GetCompilationAsync())!;
        var run = await Run(solution, cell, dispatch);
        Assert.False(run.Stopped);
        var factoryBody = IrLowering.RootBodyId(Factory(core, kind));
        var make = core.Assembly.GetTypeByMetadataName("Values.Factory`1")!.GetMembers("Make").OfType<IMethodSymbol>().Single();
        var makeBody = IrLowering.RootBodyId(make);
        var instances = run.Heap.Instances.Values.Where(instance => instance.BodyId == makeBody).ToArray();
        Assert.Equal(receivers > AnalysisLimits.Default.MaxContextsPerMethod, instances.Any(instance => instance.IsMerged));
        Assert.DoesNotContain(factoryBody, run.Reachable.ReachedBodies.Keys);
        foreach (var instance in instances)
        {
            Assert.Contains(run.Heap.Regions.Values, region => region.SiteBodyId == makeBody && region.Context == instance.Context &&
                                                             region.TypeKey == Selected(cell.Compared, kind));
            Assert.Contains(run.Heap.Edges, edge => edge.CallerInstance == instance.Id &&
                                                   run.Heap.Instances[edge.CalleeInstance].BodyId.Contains(".#ctor", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(run.Heap.Edges, edge => run.Heap.Instances[edge.CalleeInstance].BodyId == factoryBody);
    }

    /// <summary>Two marks of one kind keep only their own closed bindings when their receiver contexts merge.</summary>
    /// <param name="kind">The factory.</param>
    [Theory]
    [InlineData(Kind.Ordering)]
    [InlineData(Kind.Equality)]
    public async Task Merged_marks_do_not_borrow_each_others_bindings(Kind kind)
    {
        var cell = new Cell(Compared.ClassComparable, kind, Declaration.CoreLib, Argument.TypeofParameter, Binding.Closed);
        var (source, worker) = Sources(cell);
        var factoryName = kind == Kind.Ordering ? "CreateDefaultComparer" : "CreateDefaultEqualityComparer";
        source = source.Replace("public static class Entry", $$"""
            public sealed class FirstFactory<T> { public object Make() => ComparerHelpers.{{factoryName}}(typeof(T)); }
            public sealed class SecondFactory<U> { public object Make() => ComparerHelpers.{{factoryName}}(typeof(U)); }
            public static class Entry
            """);
        worker = worker.Replace("Values.Entry.Run(new Values.ClassComparable(), new Values.ClassComparable());",
                                string.Join("\n", Enumerable.Repeat("new Values.FirstFactory<Values.ClassComparable>().Make(); new Values.SecondFactory<Values.StructNeither>().Make();", 17)));
        var solution = FixtureSolution.CreateProjects(new FixtureOptions
        {
            ProjectAssemblyNames = new Dictionary<string, string> { ["Core"] = CORELIB },
            ProjectReferences = [("Fixture", "Core"), ("Fixture", PROBES), ("Core", PROBES)]
        }, (PROBES, "Probes.cs", PROBES_SOURCE), ("Core", "Core.cs", source), ("Fixture", "Case.cs", worker));
        var core = (await solution.Projects.Single(project => project.Name == "Core").GetCompilationAsync())!;
        var run = await Run(solution, cell, DispatchRule.ConstructedTypes);
        Assert.False(run.Stopped);
        foreach (var (name, compared) in new[] { ("FirstFactory`1", Compared.ClassComparable), ("SecondFactory`1", Compared.StructNeither) })
        {
            var body = IrLowering.RootBodyId(core.Assembly.GetTypeByMetadataName("Values." + name)!.GetMembers("Make").OfType<IMethodSymbol>().Single());
            var merged = Assert.Single(run.Heap.Instances.Values, instance => instance.BodyId == body && instance.IsMerged);
            var created = run.Heap.Regions.Values.Where(region => region.SiteBodyId == body && region.Context == merged.Context).Select(region => region.TypeKey).ToArray();
            Assert.Equal(new[] { Selected(compared, kind) }, created);
        }
    }

    /// <summary>A mark with no closed binding keeps the factory and borrows no selection from other marks.</summary>
    /// <param name="kind">The factory.</param>
    [Theory]
    [InlineData(Kind.Ordering)]
    [InlineData(Kind.Equality)]
    public async Task An_unbound_mark_creates_nothing_and_keeps_the_factory(Kind kind)
    {
        var cell = new Cell(Compared.ClassComparable, kind, Declaration.CoreLib, Argument.TypeofParameter, Binding.Closed);
        var (source, _) = Sources(cell);
        var factoryName = kind == Kind.Ordering ? "CreateDefaultComparer" : "CreateDefaultEqualityComparer";
        source = source.Replace("public static class Entry", $"public static class UnboundFactory {{ public static object Make<T>() => System.Collections.Generic.ComparerHelpers.{factoryName}(typeof(T)); }} public static class Entry");
        var worker = """
            public static class ModelDriver
            {
                public static void V_Call<T>() { Values.UnboundFactory.Make<T>(); }
            }
            """;
        var solution = FixtureSolution.CreateProjects(new FixtureOptions
        {
            ProjectAssemblyNames = new Dictionary<string, string> { ["Core"] = CORELIB, ["Fixture"] = DriverSynthesizer.ASSEMBLY },
            ProjectReferences = [("Fixture", "Core"), ("Fixture", PROBES), ("Core", PROBES)]
        }, (PROBES, "Probes.cs", PROBES_SOURCE), ("Core", "Core.cs", source), ("Fixture", "Case.cs", worker));
        var compilations = new List<Compilation>();
        foreach (var project in solution.Projects)
            compilations.Add((await project.GetCompilationAsync())!);
        var run = ScopePipeline.Run("unbound-mark", compilations, [], ROOT_DIRECTORY, new ProviderRegistry([new DriverRootProvider()]),
                                    LibraryModels.BuiltIn, AnalysisLimits.Default, new(), null, CancellationToken.None);
        Assert.False(run.Stopped);
        Assert.Empty(run.Reachable.DefaultComparerTypes);
        Assert.Single(run.Reachable.OpenDefaultComparerCalls);
        var core = compilations.Single(compilation => compilation.AssemblyName == CORELIB);
        var factory = IrLowering.RootBodyId(Factory(core, kind));
        Assert.Contains(factory, run.Reachable.ReachedBodies.Keys);
        Assert.Contains(run.Heap.Instances.Values, instance => instance.BodyId == factory);
    }

    /// <summary>Recursive widening terminates by the context-count limit, retaining its ordinary factory for open bindings.</summary>
    /// <param name="kind">The factory.</param>
    [Theory]
    [InlineData(Kind.Ordering)]
    [InlineData(Kind.Equality)]
    public async Task Recursive_widening_counts_as_open_and_reaches_the_factory(Kind kind)
    {
        var cell = new Cell(Compared.ClassComparable, kind, Declaration.CoreLib, Argument.TypeofParameter, Binding.Closed);
        var (source, worker) = Sources(cell);
        var factoryName = kind == Kind.Ordering ? "CreateDefaultComparer" : "CreateDefaultEqualityComparer";
        source = source.Replace("public static class Entry", $$"""
            public static class Widen { public static object Make<T>() { Make<Layer<T>>(); return ComparerHelpers.{{factoryName}}(typeof(T)); } }
            public static class Entry
            """);
        worker = worker.Replace("Values.Entry.Run(new Values.ClassComparable(), new Values.ClassComparable());", "Values.Widen.Make<Values.ClassComparable>();");
        var solution = FixtureSolution.CreateProjects(new FixtureOptions
        {
            ProjectAssemblyNames = new Dictionary<string, string> { ["Core"] = CORELIB },
            ProjectReferences = [("Fixture", "Core"), ("Fixture", PROBES), ("Core", PROBES)]
        }, (PROBES, "Probes.cs", PROBES_SOURCE), ("Core", "Core.cs", source), ("Fixture", "Case.cs", worker));
        var core = (await solution.Projects.Single(project => project.Name == "Core").GetCompilationAsync())!;
        var run = await Run(solution, cell, DispatchRule.ConstructedTypes, AnalysisLimits.Default with { MaxContextsPerMethod = 2 });
        Assert.False(run.Stopped);
        var body = IrLowering.RootBodyId(core.Assembly.GetTypeByMetadataName("Values.Widen")!.GetMembers("Make").OfType<IMethodSymbol>().Single());
        Assert.Contains(run.Reachable.OpenDefaultComparerCalls, mark => mark.BodyId == body);
        Assert.InRange(run.Reachable.DefaultComparerTypes.Count, 1, 3);
        Assert.Contains(IrLowering.RootBodyId(Factory(core, kind)), run.Reachable.ReachedBodies.Keys);
    }

    [Fact]
    public void Every_value_of_every_axis_occurs()
    {
        var cells = Cells().ToArray();
        Assert.Empty(KnownWider);
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.Equal(192, cells.Length);
        Assert.All(Enum.GetValues<Compared>(), value => Assert.Contains(cells, cell => cell.Compared == value));
        Assert.All(Enum.GetValues<Kind>(), value => Assert.Contains(cells, cell => cell.Kind == value));
        Assert.All(Enum.GetValues<Declaration>(), value => Assert.Contains(cells, cell => cell.Declaration == value));
        Assert.All(Enum.GetValues<Argument>(), value => Assert.Contains(cells, cell => cell.Argument == value));
        Assert.All(Enum.GetValues<Binding>(), value => Assert.Contains(cells, cell => cell.Binding == value));
        Assert.All(cells, cell => Assert.False(string.IsNullOrEmpty(Expected(cell).Reason)));
    }

    // ---- the check ----

    /// <summary>The ways a cell's run departs from its expectation.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="dispatch">The dispatch rule of the run.</param>
    private static async Task<List<string>> Check(Cell cell, DispatchRule dispatch)
    {
        var expected = Expected(cell);
        var (solution, core) = await Compilations(cell);
        var run = await Run(solution, cell, dispatch);
        var failures = new List<string>();
        void Fail(bool holds, string what)
        {
            if (!holds)
                failures.Add(what);
        }

        Assert.False(run.Stopped);
        var heap = run.Heap!;
        var factory = Factory(core, cell.Kind);
        var factoryBody = IrLowering.RootBodyId(factory);
        // The call the cell makes: the factory's call in the initializer of the type's Default, or in Entry.
        var site = Calls(core).Single(call => call.Target.OriginalDefinition.Equals(factory, SymbolEqualityComparer.Default) &&
                                             call.Caller.ContainingType.Name == (cell.Argument == Argument.TypeofParameter ? KindType(cell.Kind) : "Entry"));
        var siteBody = IrLowering.RootBodyId(site.Caller);
        var siteBodies = heap.Instances.Values.Where(instance => instance.BodyId == siteBody).ToArray();
        Fail(siteBodies.Length != 0, $"{siteBody} has no instance");
        var siteOperations = run.Reachable.Bodies.TryGetValue(siteBody, out var lowered)
            ? lowered.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>().Where(call => call.TargetMethodId == factoryBody).Select(call => call.Id).ToHashSet()
            : [];
        Fail(siteOperations.Count == 1, $"{siteBody} holds {siteOperations.Count} calls of the factory");
        var created = heap.Regions.Values.Where(region => region.SiteBodyId == siteBody && region.SiteOperationId is int operation && siteOperations.Contains(operation))
                          .ToArray();
        var factoryRuns = heap.Edges.Any(edge => siteBodies.Any(instance => instance.Id == edge.CallerInstance) && siteOperations.Contains(edge.OperationId) &&
                                                 heap.Instances[edge.CalleeInstance].BodyId == factoryBody);

        if (expected.Creates is { } comparer)
        {
            Fail(created.Select(region => region.TypeKey).SequenceEqual([comparer]),
                 $"the call creates {string.Join(", ", created.Select(region => region.TypeKey))}, not {comparer}");
            Fail(factoryRuns == (cell.Binding == Binding.Open), "the factory call disagrees with whether its binding can stay open");
            var definition = run.Program.Type(comparer)!;
            var constructor = IrLowering.RootBodyId(core.Assembly.GetTypeByMetadataName(MetadataName(definition))!.InstanceConstructors.Single());
            Fail(heap.Instances.Values.Any(instance => instance.BodyId == constructor && created.Any(region => instance.Receivers.Contains(region.Identity))),
                 $"{constructor} does not run on the comparer");
            var member = IrLowering.RootBodyId(core.Assembly.GetTypeByMetadataName(MetadataName(definition))!
                                                   .GetMembers(cell.Kind == Kind.Ordering ? "Compare" : "Equals").OfType<IMethodSymbol>()
                                                   .Single(method => method.Parameters.Length == 2));
            Fail(heap.Instances.Values.Any(instance => instance.BodyId == member && created.Any(region => instance.Receivers.Contains(region.Identity))),
                 $"a call on the comparer does not dispatch to {member}");
        }
        else
        {
            Fail(created.Length == 0, $"the call creates {string.Join(", ", created.Select(region => region.TypeKey))}");
            Fail(factoryRuns, "the call is not the ordinary call of the factory's body");
        }

        // At a mark the set reaches the constructor of exactly the comparer the selection gives for the closed type the mark's type is bound
        // to, and counts it constructed: its override of the comparison is reached, which constructed-types dispatch reaches only for a type
        // it counts constructed. No other comparer of the kind is reached, and the factory's body only where the type can stay open.
        var selected = Selected(cell.Compared, cell.Kind);
        var selectedDefinition = selected[..selected.IndexOf('<')].Split('.')[^1];
        foreach (var definition in Comparers(cell.Kind))
        {
            var type = core.Assembly.GetTypeByMetadataName($"System.Collections.Generic.{definition}`1")!;
            var constructor = IrLowering.RootBodyId(type.InstanceConstructors.Single());
            var compare = IrLowering.RootBodyId(type.GetMembers(cell.Kind == Kind.Ordering ? "Compare" : "Equals").OfType<IMethodSymbol>()
                                                    .Single(method => method.Parameters.Length == 2));
            var reaches = expected.Marked && definition == selectedDefinition;
            Fail(run.Reachable.ReachedBodies.ContainsKey(constructor) == reaches,
                 reaches ? $"{constructor} is not reached at the mark" : $"{constructor} is reached, not the selection's {selectedDefinition}");
            if (dispatch == DispatchRule.ConstructedTypes)
                Fail(run.Reachable.ReachedBodies.ContainsKey(compare) == reaches,
                     reaches ? $"{compare} is not reached: {definition} is not counted constructed" : $"{compare} is reached: {definition} is counted constructed");
        }

        // The cell's own call reaches the factory's body where it is no mark, and at a mark only where the type can stay open: a typeof(T)
        // the merging of every context leaves unbound (Binding.Open). Bound to one closed type within the heap's limit, T stays closed and
        // the factory's body is reached by no call; the walker reaches the cell's call first, so the reason the body is reached for says
        // whether the cell's call did.
        var factoryReached = !expected.Marked || cell is { Argument: Argument.TypeofParameter, Binding: Binding.Open };
        var reachedBySite = run.Reachable.ReachedBodies.GetValueOrDefault(factoryBody)?.StartsWith($"call:{siteBody}:", StringComparison.Ordinal) == true;
        Fail(reachedBySite == factoryReached,
             factoryReached ? $"{factoryBody} is not reached by {siteBody}, where the type can stay open"
                 : $"{factoryBody} is reached by {siteBody}, at a mark whose type is closed");

        return failures;
    }

    /// <summary>The comparer types the factory of a kind can make.</summary>
    /// <param name="kind">The factory.</param>
    private static string[] Comparers(Kind kind) => kind == Kind.Ordering
        ? ["GenericComparer", "NullableComparer", "EnumComparer", "ObjectComparer"]
        : ["GenericEqualityComparer", "NullableEqualityComparer", "EnumEqualityComparer", "ObjectEqualityComparer"];

    private static string KindType(Kind kind) => kind == Kind.Ordering ? "Comparer" : "EqualityComparer";

    private static string MetadataName(ProgramType type) =>
        FieldSlot.WithoutTypeArguments(type.TypeKey[(type.TypeKey.IndexOf(':') + 1)..]) + "`" + type.TypeParameterKeys.Count;

    private static IMethodSymbol Factory(Compilation core, Kind kind) =>
        core.Assembly.GetTypeByMetadataName("System.Collections.Generic.ComparerHelpers")!
            .GetMembers(kind == Kind.Ordering ? "CreateDefaultComparer" : "CreateDefaultEqualityComparer").OfType<IMethodSymbol>().Single();

    /// <summary>Every call in <c>Core</c>'s source, with the member it stands in.</summary>
    /// <param name="core">The compilation of <c>Core</c>.</param>
    private static IEnumerable<(IMethodSymbol Caller, IMethodSymbol Target, Microsoft.CodeAnalysis.Operations.IInvocationOperation Operation)> Calls(Compilation core)
    {
        foreach (var tree in core.SyntaxTrees)
        {
            var model = core.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
            {
                if (model.GetOperation(node) is not Microsoft.CodeAnalysis.Operations.IInvocationOperation invocation)
                    continue;
                var caller = model.GetEnclosingSymbol(node.SpanStart) switch
                {
                    IMethodSymbol method => method,
                    // A static property's initializer stands in its type's static constructor.
                    IPropertySymbol or IFieldSymbol => model.GetEnclosingSymbol(node.SpanStart)!.ContainingType.StaticConstructors.Single(),
                    var other => throw new InvalidOperationException($"{node} stands in {other}")
                };
                yield return (caller, invocation.TargetMethod, invocation);
            }
        }
    }

    // ---- the program ----

    private static async Task<(Solution Solution, Compilation Core)> Compilations(Cell cell, int layers = 0)
    {
        var solution = Program(cell, layers);
        var core = (await solution.Projects.Single(project => project.Name == "Core").GetCompilationAsync())!;
        return (solution, core);
    }

    private static Solution Program(Cell cell, int layers = 0)
    {
        var (core, worker) = Sources(cell, layers);
        var assembly = cell.Declaration == Declaration.CoreLib ? CORELIB : IMPOSTOR;
        return Programs.GetOrAdd($"{assembly}\n{core}\n{worker}", _ => new Lazy<Solution>(() => FixtureSolution.CreateProjects(
                                     new FixtureOptions
                                     {
                                         ProjectAssemblyNames = new Dictionary<string, string> { ["Core"] = assembly },
                                         ProjectReferences = [("Fixture", "Core"), ("Fixture", PROBES), ("Core", PROBES)]
                                     },
                                     (PROBES, "Probes.cs", PROBES_SOURCE), ("Core", "Core.cs", core), ("Fixture", "Case.cs", worker)))).Value;
    }

    /// <summary>The open-world assembly's struct, which implements both generic interfaces as a probe with its witnesses does.</summary>
    private const string PROBES_SOURCE = """
        using System;
        namespace Probes
        {
            public struct OpenWorldStruct : IComparable<OpenWorldStruct>, IEquatable<OpenWorldStruct>
            {
                public object Ref0;
                public int CompareTo(OpenWorldStruct other) => 0;
                public bool Equals(OpenWorldStruct other) => true;
            }
        }
        """;

    /// <summary>The source of <c>Core</c> and of the program for a cell.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="layers">How many <c>Layer&lt;…&gt;</c> wrap <c>ClassComparable</c> in X, in place of the cell's X; none for the cell's own.</param>
    private static (string Core, string Worker) Sources(Cell cell, int layers = 0)
    {
        var x = layers > 0 ? Layered(layers, "") : cell.Compared switch
        {
            Compared.String => "string",
            Compared.OpenWorldStruct => "Probes.OpenWorldStruct",
            Compared.NullableOfComparable => "StructComparable?",
            Compared.NullableOfNeither => "StructNeither?",
            Compared.Enum => "Color",
            _ => cell.Compared.ToString()
        };
        var value = layers > 0 ? $"new {Layered(layers, "Values.")}()" : cell.Compared switch
        {
            Compared.ClassComparable => "new Values.ClassComparable()",
            Compared.String => "\"a\"",
            Compared.OpenWorldStruct => "new Probes.OpenWorldStruct()",
            Compared.NullableOfComparable => "(Values.StructComparable?)new Values.StructComparable()",
            Compared.NullableOfNeither => "(Values.StructNeither?)new Values.StructNeither()",
            Compared.Enum => "Values.Color.Red",
            _ => $"new Values.{cell.Compared}()"
        };
        var (type, factory, member, result) = cell.Kind == Kind.Ordering
            ? ("Comparer", "CreateDefaultComparer", "Compare", "int")
            : ("EqualityComparer", "CreateDefaultEqualityComparer", "Equals", "bool");
        var (entry, call) = cell.Argument switch
        {
            Argument.TypeofParameter => ($"public static {result} Run<T>(T x, T y) => {type}<T>.Default.{member}(x, y);",
                                         $"Values.Entry.Run({value}, {value});"),
            Argument.TypeofClosed => ($"public static {result} Run({x} x, {x} y) => (({type}<{x}>)ComparerHelpers.{factory}(typeof({x}))).{member}(x, y);",
                                      $"Values.Entry.Run({value}, {value});"),
            Argument.NotTypeof => ($"public static {result} Run(Type type, {x} x, {x} y) => (({type}<{x}>)ComparerHelpers.{factory}(type)).{member}(x, y);",
                                   $"Values.Entry.Run(typeof({(cell.Compared == Compared.String || cell.Compared == Compared.OpenWorldStruct ? x : "Values." + x)}), {value}, {value});"),
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        var core = $$"""
            using System;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;

            namespace System.Collections.Generic
            {
                internal static class ComparerHelpers
                {
                    internal static object CreateDefaultComparer(Type type) => CreateInstanceForAnotherGenericParameter(type);
                    internal static object CreateDefaultEqualityComparer(Type type) => CreateInstanceForAnotherGenericParameter(type);
                    [MethodImpl(MethodImplOptions.InternalCall)]
                    internal static extern object CreateInstanceForAnotherGenericParameter(Type type);
                }

                public abstract class Comparer<T>
                {
                    public static Comparer<T> Default { get; } = (Comparer<T>)ComparerHelpers.CreateDefaultComparer(typeof(T));
                    public abstract int Compare(T x, T y);
                }

                public sealed class GenericComparer<T> : Comparer<T> where T : IComparable<T> { public override int Compare(T x, T y) => 1; }
                public sealed class NullableComparer<T> : Comparer<T?> where T : struct { public override int Compare(T? x, T? y) => 2; }
                public sealed class EnumComparer<T> : Comparer<T> where T : struct, Enum { public override int Compare(T x, T y) => 3; }
                public sealed class ObjectComparer<T> : Comparer<T> { public override int Compare(T x, T y) => 4; }

                public abstract class EqualityComparer<T>
                {
                    public static EqualityComparer<T> Default { get; } = (EqualityComparer<T>)ComparerHelpers.CreateDefaultEqualityComparer(typeof(T));
                    public abstract bool Equals(T x, T y);
                }

                public sealed class GenericEqualityComparer<T> : EqualityComparer<T> where T : IEquatable<T> { public override bool Equals(T x, T y) => true; }
                public sealed class NullableEqualityComparer<T> : EqualityComparer<T?> where T : struct { public override bool Equals(T? x, T? y) => true; }
                public sealed class EnumEqualityComparer<T> : EqualityComparer<T> where T : struct, Enum { public override bool Equals(T x, T y) => true; }
                public sealed class ObjectEqualityComparer<T> : EqualityComparer<T> { public override bool Equals(T x, T y) => true; }
            }

            namespace Values
            {
                public sealed class ClassComparable : IComparable<ClassComparable>, IEquatable<ClassComparable>
                {
                    public int CompareTo(ClassComparable? other) => 0;
                    public bool Equals(ClassComparable? other) => true;
                }

                public struct StructComparable : IComparable<StructComparable>, IEquatable<StructComparable>
                {
                    public object Ref0;
                    public int CompareTo(StructComparable other) => 0;
                    public bool Equals(StructComparable other) => true;
                }

                public struct StructNeither { public object Ref0; }

                public sealed class Variant : IComparable<object>, IEquatable<object>
                {
                    public int CompareTo(object? other) => 0;
                    public override bool Equals(object? other) => true;
                    public override int GetHashCode() => 0;
                }

                public class Base : IComparable<Base>, IEquatable<Base>
                {
                    public int CompareTo(Base? other) => 0;
                    public bool Equals(Base? other) => true;
                }

                public sealed class Derived : Base { }

                public sealed class NestedVariant : List<string>, IComparable<IEnumerable<object>>, IEquatable<IEnumerable<object>>
                {
                    public int CompareTo(IEnumerable<object>? other) => 0;
                    public bool Equals(IEnumerable<object>? other) => true;
                }

                public sealed class ArrayVariant<A> : List<A>, IComparable<IEnumerable<object>>, IEquatable<IEnumerable<object>>
                {
                    public int CompareTo(IEnumerable<object>? other) => 0;
                    public bool Equals(IEnumerable<object>? other) => true;
                }

                public struct StructVariant : IComparable<object>
                {
                    public object Ref0;
                    public int CompareTo(object? other) => 0;
                }

                public sealed class Layer<T> : IComparable<Layer<T>>, IEquatable<Layer<T>>
                {
                    public int CompareTo(Layer<T>? other) => 0;
                    public bool Equals(Layer<T>? other) => true;
                }

                public enum Color { Red }

                public static class Entry
                {
                    {{entry}}
                }
            }
            """;
        var worker = Usings + $$"""
            public sealed class Worker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    {{call}}
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddHostedService<Worker>();");
        return (core, worker);
    }

    /// <summary>Runs the program's one scope through the pipeline under the dispatch rule, every context merged for an open binding.</summary>
    /// <param name="solution">The program.</param>
    /// <param name="cell">The cell.</param>
    /// <param name="dispatch">The dispatch rule.</param>
    /// <param name="overrideLimits">The limits for a recursive-widening row, or null for the cell's limits.</param>
    private static async Task<ScopeRun> Run(Solution solution, Cell cell, DispatchRule dispatch, AnalysisLimits? overrideLimits = null)
    {
        var scoped = Assert.Single(ProcessScopes.Discover(solution, ROOT_DIRECTORY).Scopes);
        var projectFiles = new List<(Compilation Compilation, string? ProjectFilePath)>();
        foreach (var project in scoped.Projects)
            projectFiles.Add(((await project.GetCompilationAsync())!, project.FilePath));
        var compilations = projectFiles.Select(project => project.Compilation).ToArray();
        var rules = new ReachabilityRules(dispatch, new HashSet<string>(StringComparer.Ordinal))
        {
            OpenWorldAssemblies = new HashSet<string>(StringComparer.Ordinal) { PROBES }
        };
        var limits = overrideLimits ?? (cell.Binding == Binding.Open ? AnalysisLimits.Default with { MaxContextsPerMethod = 0 } : AnalysisLimits.Default);
        return ScopePipeline.Run(scoped.Scope.Id, compilations, projectFiles, ROOT_DIRECTORY, ProviderRegistry.BuiltIn, LibraryModels.BuiltIn,
                                 limits, new(), null, CancellationToken.None, reachability: rules);
    }
}
