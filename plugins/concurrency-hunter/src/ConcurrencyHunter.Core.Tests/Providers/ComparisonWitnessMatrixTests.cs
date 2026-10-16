using System.Collections.Concurrent;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>Probes that implement the comparison interfaces (R11, <see cref="ComparisonWitnesses"/>): a probe implements
/// <c>IComparable&lt;T&gt;</c> exactly when the member's heap, on any pass, makes an instance of <c>Comparer&lt;T&gt;.Default</c>'s getter for
/// its T, and <c>IEquatable&lt;T&gt;</c> exactly when it makes one of <c>EqualityComparer&lt;T&gt;.Default</c>'s, each by witnesses that read
/// the objects it holds. The fixture library declares both getters as CoreLib does, an auto-property each type's initializer fills, and
/// reaches them from <c>Touch.Reach&lt;T&gt;</c>, directly or through the sealed override <c>Sorter.Sort&lt;T&gt;</c> of an object only a
/// seeded static <c>Api.Hook</c> holds.</summary>
public sealed class ComparisonWitnessMatrixTests
{
    /// <summary>The probe a type parameter gets.</summary>
    public enum ProbeKind
    {
        /// <summary>A probe class: the type parameter is unconstrained.</summary>
        Class,

        /// <summary>A probe struct: the type parameter is constrained to <c>struct</c>.</summary>
        Struct
    }

    /// <summary>Which default getter the probe's closure reaches.</summary>
    public enum Reached
    {
        Neither,
        Ordering,
        Equality,
        Both
    }

    /// <summary>How many type parameters the member binds to probes.</summary>
    public enum Bound
    {
        /// <summary>One, T.</summary>
        One,

        /// <summary>Two: a second, U, whose probe's closure reaches neither default.</summary>
        Two,

        /// <summary>Eighteen probes, past the default context bound, each reaching the requested defaults.</summary>
        Many
    }

    /// <summary>Where the type parameter a probe stands for is declared.</summary>
    public enum Origin
    {
        /// <summary>The member's own (<c>ChooseTypeArguments</c>).</summary>
        MemberParameter,

        /// <summary>A generic static type the driver constructs to seed a static the closure loads (<c>TryConstructStaticType</c>).</summary>
        StaticType
    }

    /// <summary>When the getter is reached.</summary>
    public enum Discovery
    {
        /// <summary>Where the probe first stands: the member's run, or the type initializer of the static type the driver seeds.</summary>
        FirstPass,

        /// <summary>Only by the pass after a static the first pass loaded is seeded: through the object the seeded <c>Api.Hook</c> holds.</summary>
        LaterPass
    }

    /// <summary>A cell of the matrix.</summary>
    /// <param name="Kind">The probe a type parameter gets.</param>
    /// <param name="Reached">Which getter T's closure reaches.</param>
    /// <param name="Bound">How many type parameters are bound to probes.</param>
    /// <param name="Origin">Where they are declared.</param>
    /// <param name="Discovery">When the getter is reached.</param>
    private sealed record Cell(ProbeKind Kind, Reached Reached, Bound Bound, Origin Origin, Discovery Discovery)
    {
        public override string ToString() => $"{Kind}/{Reached}/{Bound}/{Origin}/{Discovery}";
    }

    /// <summary>The cells whose outcome is known to be wider than the requirement asks: none.</summary>
    private static readonly Cell[] KnownWider = [];

    private static readonly ConcurrentDictionary<Cell, Lazy<GenerationTrace>> Traces = new();

    private static IEnumerable<Cell> Cells() =>
        from kind in Enum.GetValues<ProbeKind>()
        from reached in Enum.GetValues<Reached>()
        from bound in Enum.GetValues<Bound>()
        from origin in Enum.GetValues<Origin>()
        from discovery in Enum.GetValues<Discovery>()
        select new Cell(kind, reached, bound, origin, discovery);

    public static TheoryData<ProbeKind, Reached, Bound, Origin, Discovery> Rows()
    {
        var rows = new TheoryData<ProbeKind, Reached, Bound, Origin, Discovery>();
        foreach (var cell in Cells())
            rows.Add(cell.Kind, cell.Reached, cell.Bound, cell.Origin, cell.Discovery);
        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Every_cell_declares_as_expected(ProbeKind kind, Reached reached, Bound bound, Origin origin, Discovery discovery)
    {
        var cell = new Cell(kind, reached, bound, origin, discovery);
        var driver = Driver(cell);
        var ordering = reached is Reached.Ordering or Reached.Both;
        var equality = reached is Reached.Equality or Reached.Both;
        if (bound == Bound.Many)
        {
            var run = Traces[cell].Value.Run!;
            foreach (var parameter in new[] { "T" }.Concat(Enumerable.Range(1, 17).Select(i => "T" + i)))
            {
                var probe = Probe(driver, cell, parameter);
                var key = DriverSynthesizer.ASSEMBLY + ":" + probe;
                var recordedOrdering = run.Reachable.DefaultComparerTypes.Contains((ConcurrencyHunter.Ir.IrComparerKind.Ordering, key));
                var recordedEquality = run.Reachable.DefaultComparerTypes.Contains((ConcurrencyHunter.Ir.IrComparerKind.Equality, key));
                AssertDeclares(driver, probe, kind, recordedOrdering, recordedEquality, cell);
            }
            if (ordering || equality)
            {
                // A concrete string/int binding of Store's original code consumes one slot in the static-origin fixture.
                var recorded = origin == Origin.MemberParameter ? 17 : 16;
                Assert.Equal(recorded, driver.ProbeTypes.Where(pair => pair.Key.ContainingSymbol.Name ==
                    (origin == Origin.StaticType ? "Store" : "Run")).Count(pair => Type(driver, pair.Value).Interfaces.Any(Comparison)));
                Assert.DoesNotContain(Type(driver, Probe(driver, cell, "T17")).Interfaces, Comparison);
                Assert.Contains(run.Reachable.OpenDefaultComparerCalls, mark => mark.BodyId.Contains(ordering ? "Comparer`1.#cctor" : "EqualityComparer`1.#cctor", StringComparison.Ordinal));
                Assert.Contains(run.Heap!.Instances.Values, instance => instance.BodyId.Contains(ordering ? "ComparerHelpers.CreateDefaultComparer" : "ComparerHelpers.CreateDefaultEqualityComparer", StringComparison.Ordinal));
            }
        }
        else
            AssertDeclares(driver, Probe(driver, cell, "T"), kind, ordering, equality, cell);
        if (bound == Bound.Two)
            AssertDeclares(driver, Probe(driver, cell, "U"), kind, false, false, cell);
    }

    /// <summary>The name of the probe that stands for a type parameter of the member, or of the static type <c>Store</c>.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="cell">The cell.</param>
    /// <param name="parameter">The type parameter's name.</param>
    private static string Probe(Driver driver, Cell cell, string parameter) =>
        driver.ProbeTypes.Single(pair => pair.Key.Name == parameter &&
                                         pair.Key.ContainingSymbol.Name == (cell.Origin == Origin.StaticType ? "Store" : "Run")).Value;

    [Theory]
    [InlineData(ProbeKind.Class, Origin.MemberParameter)]
    [InlineData(ProbeKind.Struct, Origin.MemberParameter)]
    [InlineData(ProbeKind.Class, Origin.StaticType)]
    [InlineData(ProbeKind.Struct, Origin.StaticType)]
    public void No_unrelated_probe_gains_an_interface(ProbeKind kind, Origin origin)
    {
        // The second probe of Bound Two implements neither, whatever the first's closure reaches and on whichever pass.
        foreach (var discovery in Enum.GetValues<Discovery>())
        {
            var cell = new Cell(kind, Reached.Both, Bound.Two, origin, discovery);
            var driver = Driver(cell);
            AssertDeclares(driver, Probe(driver, cell, "U"), kind, false, false, cell);
            Assert.Equal(2, Type(driver, Probe(driver, cell, "T")).Interfaces.Count(Comparison));
            // Nor does a probe the driver declares for a type parameter of no reached default, the fixture's own comparers' among them.
            Assert.All(driver.ProbeTypes.Values.Where(name => name != Probe(driver, cell, "T")).Distinct(),
                       name => Assert.DoesNotContain(Type(driver, name).Interfaces, Comparison));
        }
    }

    [Theory]
    [InlineData(ProbeKind.Class, Origin.MemberParameter)]
    [InlineData(ProbeKind.Struct, Origin.MemberParameter)]
    [InlineData(ProbeKind.Class, Origin.StaticType)]
    [InlineData(ProbeKind.Struct, Origin.StaticType)]
    public void A_need_found_on_a_later_pass_is_declared(ProbeKind kind, Origin origin)
    {
        // The getter is reached only through the object Api.Hook holds, which only a pass after the first seeds: the final driver seeds it,
        // and its probe implements both interfaces all the same.
        var cell = new Cell(kind, Reached.Both, Bound.One, origin, Discovery.LaterPass);
        var driver = Driver(cell);
        Assert.Contains(driver.SeedStatements, seed => seed.Path.EndsWith("Lib.Api.Hook", StringComparison.Ordinal));
        AssertDeclares(driver, Probe(driver, cell, "T"), kind, true, true, cell);
    }

    [Fact]
    public void Every_value_of_every_axis_occurs()
    {
        var cells = Cells().ToArray();
        Assert.Empty(KnownWider);
        Assert.Equal(96, cells.Length);
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.All(Enum.GetValues<ProbeKind>(), value => Assert.Contains(cells, cell => cell.Kind == value));
        Assert.All(Enum.GetValues<Reached>(), value => Assert.Contains(cells, cell => cell.Reached == value));
        Assert.All(Enum.GetValues<Bound>(), value => Assert.Contains(cells, cell => cell.Bound == value));
        Assert.All(Enum.GetValues<Origin>(), value => Assert.Contains(cells, cell => cell.Origin == value));
        Assert.All(Enum.GetValues<Discovery>(), value => Assert.Contains(cells, cell => cell.Discovery == value));
    }

    // ---- the check ----

    /// <summary>Asserts a probe implements exactly the comparison interfaces expected, each by witnesses reading what it holds: a probe
    /// struct its <c>Ref0</c> and <c>Ref1</c>, a probe class itself, on the receiver and on the argument.</summary>
    /// <param name="driver">The final driver.</param>
    /// <param name="name">The probe's name.</param>
    /// <param name="kind">Whether it is a probe struct.</param>
    /// <param name="ordering">Whether it implements <c>IComparable&lt;T&gt;</c>.</param>
    /// <param name="equality">Whether it implements <c>IEquatable&lt;T&gt;</c>.</param>
    /// <param name="cell">The cell, for the message.</param>
    private static void AssertDeclares(Driver driver, string name, ProbeKind kind, bool ordering, bool equality, Cell cell)
    {
        var probe = Type(driver, name);
        Assert.Equal(kind == ProbeKind.Struct ? TypeKind.Struct : TypeKind.Class, probe.TypeKind);
        Assert.True(ordering == probe.Interfaces.Any(@interface => @interface.ToDisplayString() == $"System.IComparable<{name}>"),
                    $"{cell}: {name} implements {string.Join(", ", probe.Interfaces.Select(@interface => @interface.ToDisplayString()))}");
        Assert.True(equality == probe.Interfaces.Any(@interface => @interface.ToDisplayString() == $"System.IEquatable<{name}>"),
                    $"{cell}: {name} implements {string.Join(", ", probe.Interfaces.Select(@interface => @interface.ToDisplayString()))}");
        var read = kind == ProbeKind.Struct ? new[] { "this.Ref0", "this.Ref1", "p0.Ref0", "p0.Ref1" } : ["this", "p0"];
        foreach (var (wanted, member) in new[] { (ordering, "CompareTo"), (equality, "Equals") })
        {
            if (!wanted)
                continue;
            var witness = ProbeStructTests.Declaration(driver, name).Members.OfType<MethodDeclarationSyntax>()
                                          .Single(method => method.Identifier.Text == member && method.ExplicitInterfaceSpecifier is not null);
            var stores = ProbeStructTests.Stores(witness);
            Assert.Equal(read, stores.Select(store => store.Value));
            Assert.All(stores, store => Assert.StartsWith(DriverSynthesizer.READ_WITNESS_FIELD, store.Field));
        }
    }

    private static bool Comparison(INamedTypeSymbol @interface) =>
        @interface.OriginalDefinition.ToDisplayString() is "System.IComparable<T>" or "System.IEquatable<T>";

    private static INamedTypeSymbol Type(Driver driver, string name) =>
        Assert.IsAssignableFrom<INamedTypeSymbol>(driver.Compilation.GetTypeByMetadataName(name));

    /// <summary>The final driver of a cell's generation, which compiles and stops.</summary>
    /// <param name="cell">The cell.</param>
    private static Driver Driver(Cell cell)
    {
        var trace = Traces.GetOrAdd(cell, key => new Lazy<GenerationTrace>(() => TraceOf(Library(key), Member(key), "System.Private.CoreLib"))).Value;
        Assert.True(trace.Driver is not null, $"{cell}: {trace.Answer.Reason}: {trace.Answer.Detail}");
        Assert.Empty(trace.Driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return trace.Driver;
    }

    private static string Member(Cell cell) => cell.Origin == Origin.StaticType
        ? "M:Lib.Api.Run(System.Object)"
        : cell.Bound == Bound.Many ? "M:Lib.Api.Run``18(" + string.Join(",", Enumerable.Range(0, 18).Select(i => "``" + i)) + ")"
        : cell.Bound == Bound.Two ? "M:Lib.Api.Run``2(``0,``1)" : "M:Lib.Api.Run``1(``0)";

    /// <summary>The fixture library of a cell.</summary>
    /// <param name="cell">The cell.</param>
    private static string Library(Cell cell)
    {
        var reach = cell.Reached switch
        {
            Reached.Neither => "",
            Reached.Ordering => "_ = Comparer<T>.Default;",
            Reached.Equality => "_ = EqualityComparer<T>.Default;",
            Reached.Both => "_ = Comparer<T>.Default; _ = EqualityComparer<T>.Default;",
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        var constraint = cell.Kind == ProbeKind.Struct ? "struct" : "notnull";
        var two = cell.Bound == Bound.Two;
        var names = cell.Bound == Bound.Many ? new[] { "T" }.Concat(Enumerable.Range(1, 17).Select(i => "T" + i)).ToArray() : two ? ["T", "U"] : new[] { "T" };
        var parameters = "<" + string.Join(", ", names) + ">";
        var constraints = string.Join(" ", names.Select(name => $"where {name} : {constraint}"));
        var start = cell.Discovery == Discovery.FirstPass ? "Touch.Reach<T>();" : "Api.Hook.Sort<T>();";
        if (cell.Bound == Bound.Many)
            start = string.Join(" ", names.Select(name => start.Replace("<T>", "<" + name + ">")));
        var closed = cell.Kind == ProbeKind.Struct ? two ? "<int, int>" : "<int>" : two ? "<string, string>" : "<string>";
        if (cell.Bound == Bound.Many)
            closed = "<" + string.Join(", ", names.Select(_ => cell.Kind == ProbeKind.Struct ? "int" : "string")) + ">";
        var args = string.Join(", ", names.Select((name, i) => name + " x" + i));
        var (api, store) = cell.Origin == Origin.MemberParameter
            ? ($"public static void Run{parameters}({args}) {constraints} {{ {start} }}", "")
            : ($"public static void Run(object x) {{ _ = Store{closed}.Value; }}",
               $"public static class Store{parameters} {constraints} {{ public static object Value; static Store() {{ {start} }} }}");
        var source = $$"""
            using System;
            using System.Collections.Generic;

            // Internal, so the driver, which sees the runtime's types of these names too, never names them.
            namespace System.Collections.Generic
            {
                internal abstract class Comparer<T> { public static Comparer<T> Default { get; } = null!; public abstract int Compare(T x, T y); }
                internal abstract class EqualityComparer<T> { public static EqualityComparer<T> Default { get; } = null!; public abstract bool Equals(T x, T y); }
            }

            namespace Lib
            {
                public static class Touch { public static void Reach<T>() { {{reach}} } }
                public abstract class Base { public abstract void Sort<T>(); }
                public abstract class Sorter : Base { public sealed override void Sort<T>() => Touch.Reach<T>(); }
                {{store}}
                public static class Api
                {
                    public static Sorter Hook;
                    {{api}}
                }
            }
            """;
        return source.Replace("public static Comparer<T> Default { get; } = null!;", "public static Comparer<T> Default { get; } = (Comparer<T>)ComparerHelpers.CreateDefaultComparer(typeof(T));")
                     .Replace("public static EqualityComparer<T> Default { get; } = null!;", "public static EqualityComparer<T> Default { get; } = (EqualityComparer<T>)ComparerHelpers.CreateDefaultEqualityComparer(typeof(T));")
                     .Replace("internal abstract class Comparer<T>", """
                         internal static class ComparerHelpers
                         {
                             internal static object CreateDefaultComparer(Type t) => null!;
                             internal static object CreateDefaultEqualityComparer(Type t) => null!;
                         }
                         internal sealed class GenericComparer<T> : Comparer<T> where T : IComparable<T> { public override int Compare(T x, T y) => 0; }
                         internal sealed class ObjectComparer<T> : Comparer<T> { public override int Compare(T x, T y) => 0; }
                         internal sealed class GenericEqualityComparer<T> : EqualityComparer<T> where T : IEquatable<T> { public override bool Equals(T x, T y) => true; }
                         internal sealed class ObjectEqualityComparer<T> : EqualityComparer<T> { public override bool Equals(T x, T y) => true; }
                         internal abstract class Comparer<T>
                         """);
    }
}
