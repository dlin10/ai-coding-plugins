using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A C# 14 instance compound assignment operator (<c>void operator +=(int)</c>) or instance increment operator
/// (<c>void operator ++()</c>) is a call of that operator on its target (C# 14 feature specification "User Defined Compound Assignment
/// Operators"): on a target of a type known to be a reference type, the target is read once and the operator called on the object it
/// holds, and nothing is stored; on a struct whose result is not used, the operator is called on the variable itself; on a struct whose
/// result is used, the variable is read into a copy, the operator called on the copy and the copy stored back. An operator Roslyn picks
/// that is static keeps the long form <c>x = x op y</c>.</summary>
public sealed class InstanceOperatorLoweringTests
{
    /// <summary>A class whose instance operators update its value in place.</summary>
    private const string METER = """
        public sealed class Meter
        {
            public int Value;
            public void operator +=(int amount) { Value += amount; }
            public void operator ++() { Value++; }
        }

        """;

    /// <summary>A struct whose instance operators update its value in place.</summary>
    private const string STRUCT_METER = """
        public struct Meter
        {
            public int Value;
            public void operator +=(int amount) { Value += amount; }
            public void operator ++() { Value++; }
        }

        """;

    /// <summary>A struct and an extension block whose instance operators update it, by <c>ref</c>.</summary>
    private const string SLOT_OPERATORS = """
        public struct Slot { public int Value; }
        public static class SlotOperators
        {
            extension(ref Slot slot)
            {
                public void operator +=(int amount) { slot.Value += amount; }
                public void operator ++() { slot.Value++; }
            }
        }

        """;

    [Theory]
    [InlineData("_meter += 1;", "op_AdditionAssignment", 1)]
    [InlineData("_meter++;", "op_IncrementAssignment", 0)]
    [InlineData("++_meter;", "op_IncrementAssignment", 0)]
    public async Task Field_of_a_class_calls_the_operator_on_the_object_it_holds_and_stores_nothing(string statement, string name, int arguments)
    {
        var body = await Lower(METER + $"class C {{ Meter _meter = new(); void M() {{ {statement} }} }}");

        var operations = Operations(body);
        var load = Assert.Single(operations.OfType<IrLoadFieldOperation>(), load => load.Field.Name == "_meter");
        var call = Assert.Single(operations.OfType<IrCallOperation>(), call => call.Method.Contains(name, StringComparison.Ordinal));
        Assert.Equal(load.ResultValue, call.ReceiverValue);
        Assert.Equal(arguments, call.ArgumentValues.Count);
        Assert.Null(call.ResultValue);
        Assert.DoesNotContain(operations.OfType<IrStoreFieldOperation>(), store => store.Field.Name == "_meter");
    }

    [Theory]
    [InlineData("_meter += 1;", "op_AdditionAssignment")]
    [InlineData("_meter++;", "op_IncrementAssignment")]
    public async Task Field_of_a_struct_whose_result_is_not_used_calls_the_operator_on_its_storage(string statement, string name)
    {
        var body = await Lower(STRUCT_METER + $"class C {{ Meter _meter; void M() {{ {statement} }} }}");

        var operations = Operations(body);
        var address = Assert.Single(operations.OfType<IrAddressFieldOperation>(), address => address.Field.Name == "_meter");
        var call = Assert.Single(operations.OfType<IrCallOperation>(), call => call.Method.Contains(name, StringComparison.Ordinal));
        Assert.Equal(address.ResultValue, call.ReceiverValue);
        Assert.DoesNotContain(operations.OfType<IrLoadFieldOperation>(), load => load.Field.Name == "_meter");
        Assert.DoesNotContain(operations.OfType<IrStoreFieldOperation>(), store => store.Field.Name == "_meter");
    }

    [Theory]
    [InlineData("var copy = _meter += 1;", "op_AdditionAssignment")]
    [InlineData("var copy = ++_meter;", "op_IncrementAssignment")]
    public async Task Field_of_a_struct_whose_result_is_used_calls_the_operator_on_a_copy_and_stores_the_copy_back(string statement, string name)
    {
        var body = await Lower(STRUCT_METER + $"class C {{ Meter _meter; void M() {{ {statement} }} }}");

        var operations = Operations(body);
        var load = Assert.Single(operations.OfType<IrLoadFieldOperation>(), load => load.Field.Name == "_meter");
        var call = Assert.Single(operations.OfType<IrCallOperation>(), call => call.Method.Contains(name, StringComparison.Ordinal));
        var store = Assert.Single(operations.OfType<IrStoreFieldOperation>(), store => store.Field.Name == "_meter");
        Assert.Equal(load.ResultValue, call.ReceiverValue);
        Assert.Equal(load.ResultValue, store.Value);
        Assert.Equal(load.Id, store.ReadModifyWriteOf);
        Assert.True(Array.IndexOf(operations, call) < Array.IndexOf(operations, store));
        Assert.Empty(operations.OfType<IrAddressFieldOperation>());
    }

    [Fact]
    public async Task Field_whose_type_declares_only_a_static_operator_keeps_the_long_form()
    {
        var body = await Lower("""
            public sealed class Plain { public static Plain operator +(Plain plain, int amount) => plain; }
            class C { Plain _plain = new(); void M() { _plain += 1; } }
            """);

        var operations = Operations(body);
        var load = Assert.Single(operations.OfType<IrLoadFieldOperation>(), load => load.Field.Name == "_plain");
        var call = Assert.Single(operations.OfType<IrCallOperation>(), call => call.Method.Contains("op_Addition", StringComparison.Ordinal));
        var store = Assert.Single(operations.OfType<IrStoreFieldOperation>(), store => store.Field.Name == "_plain");
        Assert.Null(call.ReceiverValue);
        Assert.Equal(load.ResultValue, call.ArgumentValues[0]);
        Assert.Equal(call.ResultValue, store.Value);
    }

    [Fact]
    public async Task Operator_only_metadata_declares_is_a_call_on_the_object_too()
    {
        var library = CSharpCompilation.Create("MeterLibrary", [CSharpSyntaxTree.ParseText(METER, new CSharpParseOptions(LanguageVersion.Latest))],
                                               (await Compile(string.Empty)).References,
                                               new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        Assert.True(library.Emit(image).Success);
        var reference = MetadataReference.CreateFromImage(image.ToArray());

        var body = await Lower("class C { Meter _meter = new(); void M() { _meter += 1; } }", reference);

        var operations = Operations(body);
        var load = Assert.Single(operations.OfType<IrLoadFieldOperation>(), load => load.Field.Name == "_meter");
        var call = Assert.Single(operations.OfType<IrCallOperation>(), call => call.Method.Contains("op_AdditionAssignment", StringComparison.Ordinal));
        Assert.Equal(load.ResultValue, call.ReceiverValue);
        Assert.Empty(operations.OfType<IrComputeOperation>());
        Assert.DoesNotContain(operations.OfType<IrStoreFieldOperation>(), store => store.Field.Name == "_meter");
    }

    [Fact]
    public async Task Parameter_updated_in_place_keeps_its_value_for_later_uses()
    {
        var body = await Lower(METER + "class C { void M(Meter meter) { meter += 1; meter++; meter.Value = 0; } }");

        var parameter = body.Parameters.Single(parameter => parameter.Name == "meter").Value;
        var operations = Operations(body);
        var calls = operations.OfType<IrCallOperation>().Where(call => call.Method.Contains("Assignment", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Equal(parameter, Origin(body, call.ReceiverValue!.Value)));
        Assert.Equal(parameter, Origin(body, Assert.Single(operations.OfType<IrStoreFieldOperation>()).ReceiverValue!.Value));
    }

    [Fact]
    public async Task Parameter_updated_in_place_in_a_loop_has_no_version_the_loop_merges()
    {
        var body = await Lower(METER + "class C { void M(Meter meter, bool go) { while (go) { meter += 1; } meter.Value = 0; } }");

        var parameter = body.Parameters.Single(parameter => parameter.Name == "meter").Value;
        var operations = Operations(body);
        Assert.Empty(operations.OfType<IrPhiOperation>());
        Assert.Equal(parameter, Assert.Single(operations.OfType<IrCallOperation>()).ReceiverValue);
        Assert.Equal(parameter, Assert.Single(operations.OfType<IrStoreFieldOperation>()).ReceiverValue);
    }

    /// <summary>An extension block's operator runs as the static method the compiler emits for it, which takes the receiver as its
    /// first argument (issue 511): the object a class-typed target holds, passed by value, and nothing stored.</summary>
    [Fact]
    public async Task Extension_block_operator_is_called_on_the_object_its_target_holds()
    {
        var body = await Lower("""
            public sealed class Plate { public int Value; }
            public static class PlateOperators { extension(Plate plate) { public void operator +=(int amount) { plate.Value += amount; } } }
            class C { Plate _plate = new(); void M() { _plate += 1; } }
            """);

        var operations = Operations(body);
        var load = Assert.Single(operations.OfType<IrLoadFieldOperation>(), load => load.Field.Name == "_plate");
        var call = Assert.Single(operations.OfType<IrCallOperation>(), call => call.Method.Contains("op_AdditionAssignment", StringComparison.Ordinal));
        Assert.Equal(IrCallKind.Static, call.CallKind);
        Assert.Null(call.ReceiverValue);
        Assert.Equal(2, call.ArgumentValues.Count);
        Assert.Equal(load.ResultValue, call.ArgumentValues[0]);
        Assert.DoesNotContain(operations.OfType<IrStoreFieldOperation>(), store => store.Field.Name == "_plate");
    }

    /// <summary>An extension block's operator on a struct takes the receiver by <c>ref</c>, the only form C# allows (CS9322): where the
    /// result is not used, the storage of the target itself, and nothing stored.</summary>
    /// <param name="statement">The statement that calls the operator.</param>
    /// <param name="name">The operator method's metadata name.</param>
    [Theory]
    [InlineData("_slot += 1;", "op_AdditionAssignment")]
    [InlineData("_slot++;", "op_IncrementAssignment")]
    public async Task Extension_block_operator_on_a_struct_passes_the_storage_of_its_target(string statement, string name)
    {
        var body = await Lower(SLOT_OPERATORS + $"class C {{ Slot _slot; void M() {{ {statement} }} }}");

        var operations = Operations(body);
        var address = Assert.Single(operations.OfType<IrAddressFieldOperation>(), address => address.Field.Name == "_slot");
        var call = Assert.Single(operations.OfType<IrCallOperation>(), call => call.Method.Contains(name, StringComparison.Ordinal));
        Assert.Null(call.ReceiverValue);
        Assert.Equal(address.ResultValue, call.ArgumentValues[0]);
        Assert.DoesNotContain(operations.OfType<IrLoadFieldOperation>(), load => load.Field.Name == "_slot");
        Assert.DoesNotContain(operations.OfType<IrStoreFieldOperation>(), store => store.Field.Name == "_slot");
    }

    /// <summary>An extension block's operator on a struct whose result is used is called on the copy C# reads from the target and
    /// assigns back after the call: the copy is the first argument, and the target's store writes it back.</summary>
    [Fact]
    public async Task Extension_block_operator_on_a_struct_whose_result_is_used_is_called_on_the_copy_stored_back()
    {
        var body = await Lower(SLOT_OPERATORS + "class C { Slot _slot; void M() { var copy = _slot += 1; } }");

        var operations = Operations(body);
        var load = Assert.Single(operations.OfType<IrLoadFieldOperation>(), load => load.Field.Name == "_slot");
        var call = Assert.Single(operations.OfType<IrCallOperation>(), call => call.Method.Contains("op_AdditionAssignment", StringComparison.Ordinal));
        var store = Assert.Single(operations.OfType<IrStoreFieldOperation>(), store => store.Field.Name == "_slot");
        Assert.Null(call.ReceiverValue);
        Assert.Equal(load.ResultValue, Origin(body, call.ArgumentValues[0]));
        Assert.Equal(load.ResultValue, store.Value);
        Assert.Equal(load.Id, store.ReadModifyWriteOf);
        Assert.True(Array.IndexOf(operations, call) < Array.IndexOf(operations, store));
        Assert.Empty(operations.OfType<IrAddressFieldOperation>());
    }

    /// <summary>Compiles a source in the fixture's project with the latest language version.</summary>
    /// <param name="source">The source.</param>
    /// <param name="references">Further metadata references.</param>
    private static async Task<Compilation> Compile(string source, params MetadataReference[] references)
    {
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = references }, ("Case.cs", source));
        return await solution.Projects.Single().GetCompilationAsync() ?? throw new InvalidOperationException();
    }

    /// <summary>The value a value was converted or copied from, followed back to the first one that is neither.</summary>
    /// <param name="body">The body the value is in.</param>
    /// <param name="value">The value.</param>
    private static int Origin(IrBody body, int value)
    {
        while (Operations(body).FirstOrDefault(operation => operation.DefinedValues.Contains(value)) is IrConvertOperation or IrAssignOperation)
        {
            value = Operations(body).First(operation => operation.DefinedValues.Contains(value)) switch
            {
                IrConvertOperation convert => convert.OperandValue,
                IrAssignOperation assign => assign.SourceValue,
                _ => value
            };
        }

        return value;
    }

    /// <summary>Lowers <c>C.M</c> of a source compiled with the latest language version.</summary>
    /// <param name="source">The source, which declares <c>C</c> with one method <c>M</c>.</param>
    /// <param name="references">Further metadata references.</param>
    private static async Task<IrBody> Lower(string source, params MetadataReference[] references)
    {
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = references }, ("Case.cs", source));
        var compilation = await solution.Projects.Single().GetCompilationAsync() ?? throw new InvalidOperationException();
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var type = compilation.GetTypeByMetadataName("C") ?? throw new InvalidOperationException("Type C was not found.");
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        return IrLowering.Lower(method, compilation, ROOT_DIRECTORY, CancellationToken.None).Body;
    }

    /// <summary>The operations of a body, block by block.</summary>
    /// <param name="body">The body.</param>
    private static IrOperation[] Operations(IrBody body) => body.Blocks.SelectMany(block => block.Operations).ToArray();
}
