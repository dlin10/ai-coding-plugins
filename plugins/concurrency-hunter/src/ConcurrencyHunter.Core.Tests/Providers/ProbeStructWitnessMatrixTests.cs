using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Providers.ProbeStructTests;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>What a probe member's witness stores of each value it receives (R9): member kind × declaring probe × witness source. The
/// receiver of a class probe stores itself, the receiver of a probe struct each probe object the struct holds and never a copy of it; a
/// parameter of the class probe's type stores itself and one of the probe struct's type each probe object it holds, whichever probe
/// declares the member; a setter's <c>value</c> as a parameter of its type. Every witness keeps the field
/// <c>ReadsByConvention</c> gives its member. One driver holds both probes: <c>TC</c> gets a probe class, <c>TS</c> a probe struct,
/// each implementing <c>IMembers&lt;TC, TS&gt;</c> and <c>IComparable</c> of both. A method is <c>IMembers.Method</c>; the member
/// implemented explicitly is <c>IComparable&lt;T&gt;.CompareTo</c>, written by the interface it comes from and read by convention.</summary>
public sealed class ProbeStructWitnessMatrixTests
{
    public enum MemberKind { Method, PropertyGet, PropertySet, IndexerGet, IndexerSet, EventAdd, EventRemove, EqualsObject, GetHashCode, Explicit }

    public enum DeclaringProbe { ClassProbe, ProbeStruct }

    public enum WitnessSource { Receiver, ClassProbeParameter, ProbeStructParameter, SetterValue }

    private const string CLASS_PROBE = "Probe_TC";
    private const string PROBE_STRUCT = "Probe_TS";

    private const string LIBRARY = """
        using System;

        namespace Lib
        {
            public interface IMembers<TC, TS>
            {
                void Method(TC c, TS s);
                TS Property { get; set; }
                TS this[TC key] { get; set; }
                TS this[TS key] { get; set; }
                event Action Changed;
            }

            public interface IOnly<TC>
            {
                void Method(TC c, object o);
                TC Property { get; set; }
                TC this[TC key] { get; set; }
                event Action Changed;
            }

            public static class Api
            {
                public static void Run<TC, TS>(TC c, TS s, Action done)
                    where TC : IMembers<TC, TS>, IComparable<TC>, IComparable<TS>
                    where TS : struct, IMembers<TC, TS>, IComparable<TC>, IComparable<TS>
                    => done();

                public static void Only<TC>(TC c, Action done) where TC : IOnly<TC>, IComparable<TC> => done();
            }
        }
        """;

    private static readonly Lazy<LibraryCompilationResult> Library = new(() => GenerationRuns.Compile(LIBRARY));
    private static readonly Lazy<Driver> Matrix = new(() => Drive("M:Lib.Api.Run``2(``0,``1,System.Action)"));

    /// <summary>Every cell of the matrix: a probe-typed parameter only where the member takes one, a setter's <c>value</c> only for a
    /// property or indexer setter. <c>Equals(object)</c> takes an <c>object</c>, which stores itself as it always did.</summary>
    public static TheoryData<MemberKind, DeclaringProbe, WitnessSource> Cells()
    {
        var cells = new TheoryData<MemberKind, DeclaringProbe, WitnessSource>();
        foreach (var kind in Enum.GetValues<MemberKind>())
        {
            foreach (var probe in Enum.GetValues<DeclaringProbe>())
            {
                foreach (var source in Enum.GetValues<WitnessSource>().Where(source => Applies(kind, source)))
                    cells.Add(kind, probe, source);
            }
        }

        return cells;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_witnesses_as_expected(MemberKind kind, DeclaringProbe probe, WitnessSource source)
    {
        var member = Member(kind, probe, source);
        var stores = Stores(member);

        (string[] Values, string? Copy) expected = source switch
        {
            WitnessSource.Receiver when probe == DeclaringProbe.ProbeStruct => (new[] { "this.Ref0", "this.Ref1" }, "this"),
            WitnessSource.Receiver => (["this"], null),
            WitnessSource.ClassProbeParameter => ([Parameter(member, CLASS_PROBE)], null),
            WitnessSource.ProbeStructParameter => ([Parameter(member, PROBE_STRUCT) + ".Ref0", Parameter(member, PROBE_STRUCT) + ".Ref1"],
                                                   Parameter(member, PROBE_STRUCT)),
            _ => (["value.Ref0", "value.Ref1"], "value")
        };
        Assert.All(expected.Values, value => Assert.Contains(stores, store => store.Value == value));
        if (expected.Copy is not null)
            Assert.DoesNotContain(stores, store => store.Value == expected.Copy);
        var prefix = ReadsByConvention(kind) ? DriverSynthesizer.READ_WITNESS_FIELD : DriverSynthesizer.WITNESS_FIELD;
        Assert.All(stores, store => Assert.Matches($"^{prefix}[0-9]+$", store.Field));
    }

    [Fact]
    public void No_struct_copy_is_witnessed()
    {
        // Storing a probe struct would box a copy of it, an object no user code holds (R2).
        var driver = Matrix.Value;
        var tree = driver.Compilation.SyntaxTrees.Single();
        var model = driver.Compilation.GetSemanticModel(tree);
        var witnessed = tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
                            .Where(assignment => assignment.Left is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: DriverSynthesizer.WITNESSED_TYPE } })
                            .ToArray();

        Assert.NotEmpty(witnessed);
        Assert.All(witnessed, assignment => Assert.NotEqual(PROBE_STRUCT, model.GetTypeInfo(assignment.Right).Type?.Name));
        Assert.Empty(driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Class_probe_witnesses_are_unchanged()
    {
        // With no probe struct in the driver, every witness of the class probe stores its receiver and each value it gets, itself.
        var probe = Declaration(Drive("M:Lib.Api.Only``1(``0,System.Action)"), CLASS_PROBE);
        var bodies = probe.Members.OfType<MethodDeclarationSyntax>()
                          .Select(method => (Node: (SyntaxNode)method, Values: method.ParameterList.Parameters.Select(parameter => parameter.Identifier.Text)))
                          .Concat(probe.Members.OfType<BasePropertyDeclarationSyntax>().SelectMany(property => property.AccessorList!.Accessors.Select(accessor =>
                              (Node: (SyntaxNode)accessor,
                               Values: ((property as IndexerDeclarationSyntax)?.ParameterList.Parameters.Select(parameter => parameter.Identifier.Text) ?? [])
                                   .Concat(accessor.Keyword.Text is "set" or "add" or "remove" ? ["value"] : Array.Empty<string>())))))
                          .ToArray();

        Assert.Equal(11, bodies.Length);
        Assert.All(bodies, body => Assert.Equal(body.Values.Prepend("this"), Stores(body.Node).Select(store => store.Value)));
    }

    [Fact]
    public void Every_value_of_every_axis_occurs()
    {
        var cells = Cells().Select(row => ((MemberKind)row[0], (DeclaringProbe)row[1], (WitnessSource)row[2])).ToArray();

        Assert.Equal(Enum.GetValues<MemberKind>(), cells.Select(cell => cell.Item1).Distinct().Order());
        Assert.Equal(Enum.GetValues<DeclaringProbe>(), cells.Select(cell => cell.Item2).Distinct().Order());
        Assert.Equal(Enum.GetValues<WitnessSource>(), cells.Select(cell => cell.Item3).Distinct().Order());
        Assert.Equal(40, cells.Length);
    }

    private static bool Applies(MemberKind kind, WitnessSource source) => source switch
    {
        WitnessSource.Receiver => true,
        WitnessSource.SetterValue => kind is MemberKind.PropertySet or MemberKind.IndexerSet,
        _ => kind is MemberKind.Method or MemberKind.IndexerGet or MemberKind.IndexerSet or MemberKind.Explicit
    };

    private static bool ReadsByConvention(MemberKind kind) =>
        kind is MemberKind.PropertyGet or MemberKind.IndexerGet or MemberKind.EqualsObject or MemberKind.GetHashCode or MemberKind.Explicit;

    /// <summary>The body that witnesses a cell's member in its declaring probe: an indexer or <c>CompareTo</c> keyed by the parameter's
    /// probe, by the class probe for the other sources.</summary>
    /// <param name="kind">The member kind.</param>
    /// <param name="probe">The probe declaring the member.</param>
    /// <param name="source">The value witnessed.</param>
    private static SyntaxNode Member(MemberKind kind, DeclaringProbe probe, WitnessSource source)
    {
        var type = Declaration(Matrix.Value, probe == DeclaringProbe.ClassProbe ? CLASS_PROBE : PROBE_STRUCT);
        var key = source == WitnessSource.ProbeStructParameter ? PROBE_STRUCT : CLASS_PROBE;
        var property = type.Members.OfType<PropertyDeclarationSyntax>().SingleOrDefault(candidate => candidate.Identifier.Text == "Property");
        var indexer = type.Members.OfType<IndexerDeclarationSyntax>().SingleOrDefault(candidate => candidate.ParameterList.Parameters.Single().Type!.ToString() == key);
        var @event = type.Members.OfType<EventDeclarationSyntax>().SingleOrDefault();
        return kind switch
        {
            MemberKind.Method => Method(type, "Method", "IMembers"),
            MemberKind.PropertyGet => Accessor(property, "get"),
            MemberKind.PropertySet => Accessor(property, "set"),
            MemberKind.IndexerGet => Accessor(indexer, "get"),
            MemberKind.IndexerSet => Accessor(indexer, "set"),
            MemberKind.EventAdd => Accessor(@event, "add"),
            MemberKind.EventRemove => Accessor(@event, "remove"),
            MemberKind.EqualsObject => Method(type, "Equals", null),
            MemberKind.GetHashCode => Method(type, "GetHashCode", null),
            _ => Method(type, "CompareTo", $"IComparable<{key}>")
        };
    }

    private static MethodDeclarationSyntax Method(TypeDeclarationSyntax type, string name, string? @interface) =>
        type.Members.OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.Text == name &&
                                                                        (@interface is null ? method.ExplicitInterfaceSpecifier is null
                                                                             : method.ExplicitInterfaceSpecifier?.Name.ToString().Contains(@interface, StringComparison.Ordinal) == true));

    private static AccessorDeclarationSyntax Accessor(BasePropertyDeclarationSyntax? member, string keyword) =>
        Assert.IsAssignableFrom<BasePropertyDeclarationSyntax>(member).AccessorList!.Accessors.Single(accessor => accessor.Keyword.Text == keyword);

    /// <summary>The name of the one parameter of a probe's type that a member, or the indexer of an accessor, takes.</summary>
    /// <param name="member">The method, or the indexer accessor.</param>
    /// <param name="type">The probe's name.</param>
    private static string Parameter(SyntaxNode member, string type)
    {
        var parameters = member switch
        {
            MethodDeclarationSyntax method => method.ParameterList.Parameters,
            AccessorDeclarationSyntax { Parent.Parent: IndexerDeclarationSyntax indexer } => indexer.ParameterList.Parameters,
            _ => default
        };
        return parameters.Single(parameter => parameter.Type!.ToString() == type).Identifier.Text;
    }

    private static Driver Drive(string id)
    {
        var library = Library.Value;
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        var member = Assert.IsAssignableFrom<ISymbol>(DriverSynthesizer.FindMember(library.Compilation, id));
        var synthesis = DriverSynthesizer.Synthesize(library.Compilation, member, library.ExternMembers, library.OpenedFields, CancellationToken.None);
        Assert.True(synthesis.Driver is not null, $"{synthesis.Reason}: {synthesis.Detail}");
        return synthesis.Driver;
    }
}
