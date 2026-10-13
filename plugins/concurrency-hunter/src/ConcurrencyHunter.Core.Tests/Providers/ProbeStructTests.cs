using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>A type parameter constrained to <c>struct</c>, not <c>unmanaged</c> and not <c>Enum</c>, gets a probe struct of its own (SPEC
/// TD-034b, R9): fields that hold probe objects, members that witness the objects it holds where a class probe witnesses itself, and
/// its interface constraints met as a class probe meets them. The member is no longer <c>driver-not-synthesized</c> for it.</summary>
public sealed class ProbeStructTests
{
    [Theory]
    [InlineData("public static class Api { public static bool Same<T>(T x, object y) where T : struct => x.Equals(y); }",
                "M:Lib.Api.Same``1(``0,System.Object)", "x")]
    [InlineData("public sealed class StructComparer<T> where T : struct, IComparable<T> { public int Compare(T x, T y) => x.CompareTo(y); }",
                "M:Lib.StructComparer`1.Compare(`0,`0)", "x")]
    public void A_struct_constrained_type_parameter_gets_a_probe_struct_that_holds_probe_objects(string types, string id, string argument)
    {
        // A comparer and Object.Equals on a user's struct: it compares itself on what it holds, the user's objects.
        var trace = Trace(types, id);
        var driver = Assert.IsType<Driver>(trace.Driver);

        var probe = Type(driver, "Probe_T");
        Assert.Equal(TypeKind.Struct, probe.TypeKind);
        Assert.All(new[] { "Ref0", "Ref1" }, field => Assert.Equal(SpecialType.System_Object, probe.GetMembers(field).OfType<IFieldSymbol>().Single().Type.SpecialType));
        Assert.Equal(TypeKind.Class, Type(driver, "Probe_T_Object").TypeKind);
        Assert.Contains($"Arg_{argument}_Call = ", driver.Source);
        Assert.Contains("new Probe_T() { Ref0 = new Probe_T_Object() { Ref0 = new Probe_T_Object() } }", driver.Source);
        Assert.NotEqual(GenerationReasons.DRIVER_NOT_SYNTHESIZED, trace.Answer.Reason);
    }

    [Fact]
    public void Its_Equals_and_GetHashCode_witness_the_objects_it_holds_and_the_call_reads_them_as_a_class_probes()
    {
        var trace = Trace("public static class Api { public static bool Same<T>(T x, object y) where T : struct => x.Equals(y); }",
                          "M:Lib.Api.Same``1(``0,System.Object)");
        var probe = Declaration(trace.Driver!, "Probe_T");

        foreach (var name in new[] { "Equals", "GetHashCode" })
        {
            var stores = Stores(probe.Members.OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.Text == name));
            Assert.Contains(stores, store => store.Value == "this.Ref0");
            Assert.Contains(stores, store => store.Value == "this.Ref1");
            Assert.DoesNotContain(stores, store => store.Value == "this");
            Assert.All(stores, store => Assert.StartsWith(DriverSynthesizer.READ_WITNESS_FIELD, store.Field));
        }

        // The user's Equals runs on what x holds, as it runs on a class probe.
        var twin = Trace("public static class Api { public static bool Same<T>(T x, object y) where T : class => x.Equals(y); }",
                         "M:Lib.Api.Same``1(``0,System.Object)");
        Assert.Equal(Shape(twin), Shape(trace));
        Assert.NotEqual(Shape(Trace("public static class Api { public static bool Same<T>(T x, object y) where T : unmanaged => x.Equals(y); }",
                                    "M:Lib.Api.Same``1(``0,System.Object)")), Shape(trace));
    }

    [Fact]
    public void An_interface_constraint_is_met_by_an_explicit_witness_implementation()
    {
        var trace = Trace("public interface IShape<T> { object Area(T other); } " +
                          "public static class Api { public static object Measure<T>(T x, T y) where T : struct, IShape<T>, IComparable<T> => x.Area(y); }",
                          "M:Lib.Api.Measure``1(``0,``0)");
        var driver = Assert.IsType<Driver>(trace.Driver);

        var probe = Type(driver, "Probe_T");
        Assert.Contains(probe.AllInterfaces, @interface => @interface.ToDisplayString() == "Lib.IShape<Probe_T>");
        Assert.Contains(probe.AllInterfaces, @interface => @interface.ToDisplayString() == "System.IComparable<Probe_T>");
        var area = Declaration(driver, "Probe_T").Members.OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.Text == "Area");
        Assert.NotNull(area.ExplicitInterfaceSpecifier);
        Assert.Equal(["this.Ref0", "this.Ref1", "p0.Ref0", "p0.Ref1"], Stores(area).Select(store => store.Value));
        Assert.Empty(driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData("public static class Api { public static int Size<T>(T value, Action done) where T : unmanaged { done(); return 4; } }",
                "M:Lib.Api.Size``1(``0,System.Action)", null)]
    [InlineData("public static class Api { public static string Name<T>(T value, Action done) where T : struct, Enum { done(); return null; } }",
                "M:Lib.Api.Name``1(``0,System.Action)", "no type argument meets the constraints of T")]
    public void Unmanaged_and_Enum_keep_the_recipes_choice(string types, string id, string? refusal)
    {
        // An unmanaged struct holds no object, and an enum runs no member of its own: no probe struct stands for either.
        var trace = Trace(types, id);

        if (refusal is null)
        {
            Assert.Null(Assert.IsType<Driver>(trace.Driver).Compilation.GetTypeByMetadataName("Probe_T"));
            Assert.Contains("<global::System.Int32>(Arg_value_Call", trace.Driver.Source);
        }
        else
        {
            Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, trace.Answer.Reason);
            Assert.Contains(refusal, trace.Answer.Detail);
        }
    }

    /// <summary>The witness stores of a member body: each statement <c>Witnessed.&lt;field&gt; = &lt;value&gt;;</c>, in order.</summary>
    /// <param name="member">The member, or one of its accessors.</param>
    internal static IReadOnlyList<(string Field, string Value)> Stores(SyntaxNode member) =>
        member.DescendantNodes().OfType<AssignmentExpressionSyntax>()
              .Where(assignment => assignment.Left is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: DriverSynthesizer.WITNESSED_TYPE } })
              .Select(assignment => (((MemberAccessExpressionSyntax)assignment.Left).Name.Identifier.Text, assignment.Right.ToString()))
              .ToArray();

    /// <summary>A driver type's declaration.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="name">The type's name.</param>
    internal static TypeDeclarationSyntax Declaration(Driver driver, string name) =>
        driver.Compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().Single(type => type.Identifier.Text == name);

    private static INamedTypeSymbol Type(Driver driver, string name) =>
        Assert.IsAssignableFrom<INamedTypeSymbol>(driver.Compilation.GetTypeByMetadataName(name));

    /// <summary>An answer without what names its member: the entry, or the reason.</summary>
    /// <param name="trace">The trace.</param>
    private static string Shape(GenerationTrace trace) => trace.Answer.Model is { } model
        ? ModelEntryWriter.Write(model with { Id = "M" }).ToJsonString()
        : trace.Answer.ModelReason ?? trace.Answer.Reason ?? "none";
}
