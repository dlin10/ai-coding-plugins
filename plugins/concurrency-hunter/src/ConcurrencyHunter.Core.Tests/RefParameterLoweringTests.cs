using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

/// <summary>A struct's implicit parameterless constructor beside a constructor it declares, a primary one among them, is its
/// default value and runs no initializer: an initializer may read a <c>ref</c> parameter of the declared constructor, which the
/// implicit one does not have, as <c>System.ByReference</c> of CoreLib 8.0 does. A <c>ref</c> parameter bound to a <c>ref</c>
/// field or local, or read after such a binding, lowers in the constructor or method that declares it.</summary>
public sealed class RefParameterLoweringTests
{
    private const string BY_REFERENCE = "readonly ref struct C(ref byte value) { public readonly ref byte Value = ref value; }";

    [Fact]
    public async Task ByReference_implicit_constructor_runs_no_initializer()
    {
        var body = await Lower(BY_REFERENCE, ".ctor", 0);

        Assert.Empty(Operations(body).OfType<IrStoreFieldOperation>());
    }

    [Fact]
    public async Task ByReference_primary_constructor_stores_its_ref_field()
    {
        var body = await Lower(BY_REFERENCE, ".ctor", 1);

        Assert.Contains(Operations(body).OfType<IrStoreFieldOperation>(), store => store.Field.Name == "Value");
    }

    [Fact]
    public async Task Implicit_constructor_beside_a_primary_one_reading_a_ref_parameter_runs_no_initializer()
    {
        var body = await Lower("ref struct C(ref int value) { public int Copy = value; }", ".ctor", 0);

        Assert.Empty(Operations(body).OfType<IrStoreFieldOperation>());
    }

    [Fact]
    public async Task Implicit_constructor_beside_a_primary_one_reading_a_parameter_runs_no_initializer()
    {
        var body = await Lower("struct C(int value) { public int Value = value; }", ".ctor", 0);

        Assert.Empty(Operations(body).OfType<IrStoreFieldOperation>());
        Assert.Empty(Operations(body).OfType<IrLoadFieldOperation>());
    }

    [Fact]
    public async Task Implicit_constructor_beside_an_explicit_one_runs_no_initializer()
    {
        var body = await Lower("struct C { public int Value = 1; public C(int value) { Value = value; } }", ".ctor", 0);

        Assert.Empty(Operations(body).OfType<IrStoreFieldOperation>());
    }

    [Fact]
    public async Task Implicit_constructor_of_a_record_struct_runs_no_initializer()
    {
        var body = await Lower("record struct C(int value) { public int Copy = value; }", ".ctor", 0);

        Assert.Empty(Operations(body).OfType<IrStoreFieldOperation>());
    }

    [Fact]
    public async Task Implicit_constructor_of_a_class_keeps_its_initializers()
    {
        var body = await Lower("class C { public int Value = 1; }", ".ctor", 0);

        Assert.Contains(Operations(body).OfType<IrStoreFieldOperation>(), store => store.Field.Name == "Value");
    }

    [Fact]
    public async Task Constructor_binding_a_ref_parameter_to_a_ref_field_lowers()
    {
        var body = await Lower("ref struct C { public readonly ref byte Value; public C(ref byte value) => Value = ref value; }", ".ctor", 1);

        Assert.Contains(Operations(body).OfType<IrStoreFieldOperation>(), store => store.Field.Name == "Value");
    }

    [Fact]
    public async Task Ref_field_bound_to_a_ref_parameter_then_read_lowers()
    {
        var body = await Lower("ref struct C { public ref int Cell; public C(ref int cell) { Cell = ref cell; Cell = 2; var copy = cell; } }",
                               ".ctor", 1);

        Assert.NotEmpty(Operations(body).OfType<IrLoadReferenceOperation>());
    }

    [Fact]
    public async Task Ref_local_bound_to_a_ref_parameter_lowers()
    {
        var body = await Lower("class C { static int M(ref int value) { ref int local = ref value; local = 1; return local + value; } }");

        Assert.NotEmpty(Operations(body).OfType<IrStoreReferenceOperation>());
    }

    [Fact]
    public async Task Ref_parameter_rebound_then_read_lowers()
    {
        var body = await Lower("class C { static int M(ref int value, ref int other) { value = ref other; value = 2; return value; } }");

        Assert.NotEmpty(Operations(body).OfType<IrReturnOperation>());
    }

    private static IEnumerable<IrOperation> Operations(IrBody body) => body.Blocks.SelectMany(block => block.Operations);

    private static async Task<IrBody> Lower(string source, string name = "M", int? parameters = null)
    {
        var solution = FixtureSolution.Create(("Case.cs", source));
        var compilation = await solution.Projects.Single().GetCompilationAsync() ?? throw new InvalidOperationException();
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var type = compilation.GetTypeByMetadataName("C") ?? throw new InvalidOperationException("Type C was not found.");
        var method = type.GetMembers(name).OfType<IMethodSymbol>().Single(method => parameters is null || method.Parameters.Length == parameters);
        return IrLowering.Lower(method, compilation, @"C:\fixture", CancellationToken.None).Body;
    }
}
