using System.Collections.Concurrent;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Where the reachable set counts a value type constructed (R11, ADR 0019): small programs run as
/// <c>CoreLibGenerationSetMatrixTests</c> runs them. The struct <c>Values.S</c> of the project <c>Values</c> overrides <c>GetHashCode</c> and
/// implements <c>IEquatable&lt;S&gt;</c>; the controller action <c>SController.Post</c> makes or takes an S and hands it to a generic helper
/// that calls a member on it. <c>Values</c> is an assembly <see cref="ReachabilityRules.OpenWorldAssemblies"/> names — as the driver's is —,
/// or analysed source like any other.</summary>
public sealed class ConstructedValueTypeMatrixTests
{
    private const string VALUES = "Values";

    /// <summary>Which assembly the value type belongs to.</summary>
    public enum Assembly
    {
        /// <summary>An assembly <see cref="ReachabilityRules.OpenWorldAssemblies"/> names.</summary>
        OpenWorld,

        /// <summary>The analysed source.</summary>
        Other
    }

    /// <summary>How the value comes to exist.</summary>
    public enum Creation
    {
        /// <summary><c>new S(1)</c>.</summary>
        Constructor,

        /// <summary><c>new S { F = o }</c>.</summary>
        Initializer,

        /// <summary><c>default(S)</c>.</summary>
        Default,

        /// <summary>No reached body creates it: the value only arrives as a parameter of the root.</summary>
        Handed
    }

    /// <summary>The call on the value.</summary>
    public enum Call
    {
        /// <summary><c>x.GetHashCode()</c> in <c>Run&lt;T&gt;(T x) where T : struct</c>.</summary>
        ConstrainedObjectMember,

        /// <summary><c>x.Equals(y)</c> in <c>Run&lt;T&gt;(T x, T y) where T : struct, IEquatable&lt;T&gt;</c>.</summary>
        ConstrainedInterface,

        /// <summary><c>object o = x; o.GetHashCode();</c>.</summary>
        Boxed
    }

    /// <summary>The dispatch rule.</summary>
    public enum Dispatch
    {
        ConstructedTypes,
        ClassHierarchy
    }

    /// <summary>A cell of the matrix.</summary>
    /// <param name="Assembly">The value type's assembly.</param>
    /// <param name="Creation">How the value comes to exist.</param>
    /// <param name="Call">The call on it.</param>
    /// <param name="Dispatch">The dispatch rule.</param>
    private sealed record Cell(Assembly Assembly, Creation Creation, Call Call, Dispatch Dispatch)
    {
        public override string ToString() => $"{Assembly}/{Creation}/{Call}/{Dispatch}";
    }

    /// <summary>The cells whose outcome is known to be wider than the requirement asks: none.</summary>
    private static readonly Cell[] KnownWider = [];

    private static readonly ConcurrentDictionary<string, Lazy<Solution>> Programs = new(StringComparer.Ordinal);

    private static IEnumerable<Cell> Cells() =>
        from assembly in Enum.GetValues<Assembly>()
        from creation in Enum.GetValues<Creation>()
        from call in Enum.GetValues<Call>()
        from dispatch in Enum.GetValues<Dispatch>()
        select new Cell(assembly, creation, call, dispatch);

    public static TheoryData<Assembly, Creation, Call, Dispatch> Rows()
    {
        var rows = new TheoryData<Assembly, Creation, Call, Dispatch>();
        foreach (var cell in Cells())
            rows.Add(cell.Assembly, cell.Creation, cell.Call, cell.Dispatch);
        return rows;
    }

    /// <summary>Whether S's member the call reaches is reached and runs (R11): always under class-hierarchy dispatch and through a box; a
    /// constrained call under constructed types exactly where S is an open-world assembly's and a reached body creates it, never for a value
    /// type of any other assembly (ADR 0019).</summary>
    /// <param name="cell">The cell.</param>
    private static bool Expected(Cell cell) =>
        cell.Dispatch == Dispatch.ClassHierarchy || cell.Call == Call.Boxed ||
        cell.Assembly == Assembly.OpenWorld && cell.Creation != Creation.Handed;

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Every_cell_reaches_as_expected(Assembly assembly, Creation creation, Call call, Dispatch dispatch)
    {
        var cell = new Cell(assembly, creation, call, dispatch);
        var (run, compilation) = await Run(Program(Source(cell.Creation, cell.Call)), cell.Dispatch, cell.Assembly == Assembly.OpenWorld);
        Assert.False(run.Stopped);
        var member = Member(compilation, cell.Call);
        var expected = Expected(cell);
        Assert.True(expected == run.Reachable.ReachedBodies.ContainsKey(member), $"{cell}: {member} is {(expected ? "not " : "")}reached");
        Assert.True(expected == run.Heap!.Instances.Values.Any(instance => instance.BodyId == member), $"{cell}: {member} does {(expected ? "not " : "")}run");
    }

    [Fact]
    public async Task Only_an_open_world_value_type_is_constructed_where_it_is_created()
    {
        // Two value types created alike in one run, one of the open-world assembly and one of the analysed source: a constrained call on each
        // reaches only the open-world one's override.
        var solution = Program(Source(Creation.Constructor, Call.ConstrainedObjectMember, alsoLocal: true));
        var (run, compilation) = await Run(solution, Dispatch.ConstructedTypes, openWorld: true);
        Assert.False(run.Stopped);
        Assert.Contains(Member(compilation, Call.ConstrainedObjectMember), run.Reachable.ReachedBodies.Keys);
        var local = run.Program.Methods.Single(method => method.MethodId.EndsWith(":M:Local.GetHashCode", StringComparison.Ordinal)).MethodId;
        Assert.DoesNotContain(local, run.Reachable.ReachedBodies.Keys);

        // Under class-hierarchy dispatch both are, as today.
        var (hierarchy, _) = await Run(solution, Dispatch.ClassHierarchy, openWorld: true);
        Assert.Contains(local, hierarchy.Reachable.ReachedBodies.Keys);
    }

    [Fact]
    public void Every_value_of_every_axis_occurs()
    {
        var cells = Cells().ToArray();
        Assert.Empty(KnownWider);
        Assert.Equal(48, cells.Length);
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.All(Enum.GetValues<Assembly>(), value => Assert.Contains(cells, cell => cell.Assembly == value));
        Assert.All(Enum.GetValues<Creation>(), value => Assert.Contains(cells, cell => cell.Creation == value));
        Assert.All(Enum.GetValues<Call>(), value => Assert.Contains(cells, cell => cell.Call == value));
        Assert.All(Enum.GetValues<Dispatch>(), value => Assert.Contains(cells, cell => cell.Dispatch == value));
        // Both outcomes occur under constructed types.
        Assert.Contains(cells, cell => cell.Dispatch == Dispatch.ConstructedTypes && Expected(cell));
        Assert.Contains(cells, cell => cell.Dispatch == Dispatch.ConstructedTypes && !Expected(cell));
    }

    /// <summary>The body of S's member the call reaches.</summary>
    /// <param name="values">The compilation of <c>Values</c>.</param>
    /// <param name="call">The call.</param>
    private static string Member(Compilation values, Call call)
    {
        var type = values.GetTypeByMetadataName("Values.S")!;
        var method = call == Call.ConstrainedInterface
            ? type.GetMembers("Equals").OfType<IMethodSymbol>().Single(candidate => candidate.Parameters is [{ Type.Name: "S" }])
            : type.GetMembers("GetHashCode").OfType<IMethodSymbol>().Single();
        return IrLowering.RootBodyId(method);
    }

    private static Solution Program((string Values, string Case) source) =>
        Programs.GetOrAdd(source.Values + "\n" + source.Case, _ => new Lazy<Solution>(() => FixtureSolution.CreateProjects(
                              new FixtureOptions { ProjectReferences = [("Fixture", VALUES)] },
                              (VALUES, "Values.cs", source.Values), ("Fixture", "Case.cs", source.Case)))).Value;

    /// <summary>The source of <c>Values</c> and of the program.</summary>
    /// <param name="creation">How the value comes to exist.</param>
    /// <param name="call">The call on it.</param>
    /// <param name="alsoLocal">Whether the program makes and calls a value type of its own too.</param>
    private static (string Values, string Case) Source(Creation creation, Call call, bool alsoLocal = false)
    {
        const string VALUES_SOURCE = """
            using System;
            namespace Values
            {
                public static class Seen { public static int Hash; public static int Same; }
                public struct S : IEquatable<S>
                {
                    public object F;
                    public S(int i) { F = null!; }
                    public override int GetHashCode() { Seen.Hash = 1; return 0; }
                    public bool Equals(S other) { Seen.Same = 1; return true; }
                    public override bool Equals(object? obj) => false;
                }
            }
            """;
        var parameter = creation == Creation.Handed ? "Values.S s" : "";
        var make = creation switch
        {
            Creation.Constructor => "var s = new Values.S(1);",
            Creation.Initializer => "var s = new Values.S { F = new object() };",
            Creation.Default => "var s = default(Values.S);",
            Creation.Handed => "",
            _ => throw new ArgumentOutOfRangeException(nameof(creation), creation, null)
        };
        var run = call switch
        {
            Call.ConstrainedObjectMember => "Calls.Member(s);",
            Call.ConstrainedInterface => "Calls.Interface(s, s);",
            Call.Boxed => "Calls.Boxed(s);",
            _ => throw new ArgumentOutOfRangeException(nameof(call), call, null)
        };
        var local = alsoLocal ? "var l = new Local(1); Calls.Member(l);" : "";
        var program = Usings + $$"""
            public struct Local
            {
                public object F;
                public Local(int i) { F = null!; }
                public override int GetHashCode() { Values.Seen.Hash = 2; return 0; }
            }

            public static class Calls
            {
                public static int Member<T>(T x) where T : struct => x.GetHashCode();
                public static bool Interface<T>(T x, T y) where T : struct, IEquatable<T> => x.Equals(y);
                public static int Boxed<T>(T x) where T : struct { object o = x; return o.GetHashCode(); }
            }

            [ApiController]
            public sealed class SController : ControllerBase
            {
                [HttpPost("/s")]
                public int Post({{parameter}})
                {
                    {{make}}
                    {{run}}
                    {{local}}
                    return 0;
                }
            }
            """ + Startup();
        return (VALUES_SOURCE, program);
    }

    /// <summary>Runs the program's one scope through the pipeline under the dispatch rule.</summary>
    /// <param name="solution">The program.</param>
    /// <param name="dispatch">The dispatch rule.</param>
    /// <param name="openWorld">Whether <c>Values</c> is an open-world assembly.</param>
    private static async Task<(ScopeRun Run, Compilation Values)> Run(Solution solution, Dispatch dispatch, bool openWorld)
    {
        var scoped = Assert.Single(ProcessScopes.Discover(solution, ROOT_DIRECTORY).Scopes);
        var projectFiles = new List<(Compilation Compilation, string? ProjectFilePath)>();
        foreach (var project in scoped.Projects)
            projectFiles.Add(((await project.GetCompilationAsync())!, project.FilePath));
        var compilations = projectFiles.Select(project => project.Compilation).ToArray();
        var rules = new ReachabilityRules(dispatch == Dispatch.ConstructedTypes ? DispatchRule.ConstructedTypes : DispatchRule.ClassHierarchy,
                                          new HashSet<string>(StringComparer.Ordinal))
        {
            OpenWorldAssemblies = openWorld ? new HashSet<string>(StringComparer.Ordinal) { VALUES } : new HashSet<string>(StringComparer.Ordinal)
        };
        var run = ScopePipeline.Run(scoped.Scope.Id, compilations, projectFiles, ROOT_DIRECTORY, ProviderRegistry.BuiltIn, LibraryModels.BuiltIn,
                                    AnalysisLimits.Default, new(), null, CancellationToken.None, reachability: rules);
        return (run, compilations.Single(compilation => compilation.AssemblyName == VALUES));
    }
}
