using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Every form that combines or removes delegates lowers to one <see cref="IrCombineDelegatesOperation"/> (R3): the binary
/// operators, their compound forms on every location the lowering models, and <c>Delegate.Combine</c>, <c>Remove</c> and
/// <c>RemoveAll</c> with their operands in parameter order. A user-defined operator, an arithmetic one and a <c>Combine</c> whose
/// delegates the call does not list stay what they were.</summary>
public sealed class DelegateCombinationLoweringTests
{
    [Fact]
    public async Task Binary_add_of_delegates_prints_a_combination_of_both_operands()
    {
        var body = await Lower("using System; class C { Action M(Action a, Action b) => a + b; }");

        var combine = Assert.Single(Operations<IrCombineDelegatesOperation>(body));
        Assert.False(combine.Removes);
        Assert.Equal([Parameter(body, "a"), Parameter(body, "b")], combine.OperandValues.Select(value => Origin(body, value)));
        Assert.Contains($"combine-delegates %{combine.ResultValue} operands=[%{combine.OperandValues[0]},%{combine.OperandValues[1]}]",
                        IrPrinter.Print(body));
        Assert.Empty(Operations<IrComputeOperation>(body));
    }

    [Fact]
    public async Task Binary_subtract_of_delegates_prints_a_removal_whose_first_operand_contributes()
    {
        var body = await Lower("using System; class C { Action M(Action a, Action b) => a - b; }");

        var remove = Assert.Single(Operations<IrCombineDelegatesOperation>(body));
        Assert.True(remove.Removes);
        Assert.Equal([Parameter(body, "a"), Parameter(body, "b")], remove.OperandValues.Select(value => Origin(body, value)));
        Assert.Equal([remove.OperandValues[0]], remove.ContributingOperands);
        Assert.Contains($"remove-delegates %{remove.ResultValue} operands=[%{remove.OperandValues[0]},%{remove.OperandValues[1]}]",
                        IrPrinter.Print(body));
        Assert.Empty(Operations<IrComputeOperation>(body));
    }

    [Fact]
    public async Task Local_compound_add_combines_the_local_and_the_handler_into_its_next_version()
    {
        var body = await Lower("using System; class C { Action M(Action h) { Action local = () => { }; local += h; return local; } }");

        var combine = Assert.Single(Operations<IrCombineDelegatesOperation>(body));
        var create = Assert.Single(Operations<IrCreateDelegateOperation>(body));
        Assert.Equal([create.ResultValue, Parameter(body, "h")], combine.OperandValues.Select(value => Origin(body, value)));
        Assert.Contains(Operations<IrAssignOperation>(body), assign => assign.SourceValue == combine.ResultValue);
        Assert.Empty(Operations<IrComputeOperation>(body));
    }

    [Fact]
    public async Task Field_compound_add_is_a_load_a_combination_and_a_store()
    {
        var body = await Lower("using System; class C { Action _field; void M(Action h) { _field += h; } }");

        var operations = Operations(body);
        var load = Assert.Single(operations.OfType<IrLoadFieldOperation>());
        var combine = Assert.Single(operations.OfType<IrCombineDelegatesOperation>());
        var store = Assert.Single(operations.OfType<IrStoreFieldOperation>());
        Assert.Equal([load.ResultValue, Parameter(body, "h")], combine.OperandValues.Select(value => Origin(body, value)));
        Assert.Equal(combine.ResultValue, store.Value);
        Assert.Equal(load.Id, store.ReadModifyWriteOf);
        Assert.True(Array.IndexOf(operations, load) < Array.IndexOf(operations, combine));
        Assert.True(Array.IndexOf(operations, combine) < Array.IndexOf(operations, store));
        Assert.Empty(operations.OfType<IrComputeOperation>());
    }

    [Fact]
    public async Task Field_compound_subtract_is_a_load_a_removal_and_a_store()
    {
        var body = await Lower("using System; class C { static Action _field; void M(Action h) { _field -= h; } }");

        var load = Assert.Single(Operations<IrLoadFieldOperation>(body));
        var remove = Assert.Single(Operations<IrCombineDelegatesOperation>(body));
        Assert.True(remove.Removes);
        Assert.Equal(load.ResultValue, remove.OperandValues[0]);
        Assert.Equal(remove.ResultValue, Assert.Single(Operations<IrStoreFieldOperation>(body)).Value);
    }

    [Fact]
    public async Task Element_ref_local_and_property_compound_adds_store_the_combination_where_they_read()
    {
        var body = await Lower("""
            using System;
            class C
            {
                Action[] _cells = new Action[2];
                Action _slot;
                Action Auto { get; set; }
                Action Long { get => _slot; set => _slot = value; }
                void M(Action h)
                {
                    _cells[0] += h;
                    ref Action cell = ref _slot;
                    cell += h;
                    Auto += h;
                    Long += h;
                }
            }
            """);

        var operations = Operations(body);
        var combinations = operations.OfType<IrCombineDelegatesOperation>().ToArray();
        Assert.Equal(4, combinations.Length);
        Assert.All(combinations, combine => Assert.Equal(Parameter(body, "h"), Origin(body, combine.OperandValues[1])));
        var element = Assert.Single(operations.OfType<IrStoreElementOperation>(), store => combinations.Any(combine => combine.ResultValue == store.Value));
        Assert.Equal(Assert.Single(operations.OfType<IrLoadElementOperation>()).ResultValue,
                     combinations.Single(combine => combine.ResultValue == element.Value).OperandValues[0]);
        var reference = Assert.Single(operations.OfType<IrStoreReferenceOperation>());
        Assert.Contains(combinations, combine => combine.ResultValue == reference.Value);
        Assert.Contains(operations.OfType<IrStoreFieldOperation>(), store => combinations.Any(combine => combine.ResultValue == store.Value));
        Assert.Contains(operations.OfType<IrCallOperation>(), call => call.Method.Contains("set_Long", StringComparison.Ordinal) &&
                                                                      combinations.Any(combine => call.ArgumentValues.Contains(combine.ResultValue)));
        Assert.Empty(operations.OfType<IrComputeOperation>());
    }

    [Fact]
    public async Task Combine_of_two_delegates_is_a_combination_and_no_call()
    {
        var body = await Lower("using System; class C { Delegate M(Action a, Action b) => Delegate.Combine(a, b); }");

        var combine = Assert.Single(Operations<IrCombineDelegatesOperation>(body));
        Assert.False(combine.Removes);
        Assert.Equal([Parameter(body, "a"), Parameter(body, "b")], combine.OperandValues.Select(value => Origin(body, value)));
        Assert.Contains($"combine-delegates %{combine.ResultValue} operands=", IrPrinter.Print(body));
        Assert.Empty(Operations<IrCallOperation>(body));
    }

    [Fact]
    public async Task Combine_of_three_listed_delegates_binds_the_params_collection_and_has_three_operands()
    {
        const string SOURCE = "using System; class C { Delegate M(Action a, Action b, Action c) => Delegate.Combine(a, b, c); }";
        Assert.Equal(ArgumentKind.ParamCollection, await CombineArgumentKind(SOURCE, LanguageVersion.Latest));

        var body = await Lower(SOURCE);

        var combine = Assert.Single(Operations<IrCombineDelegatesOperation>(body));
        Assert.Equal([Parameter(body, "a"), Parameter(body, "b"), Parameter(body, "c")],
                     combine.OperandValues.Select(value => Origin(body, value)));
        Assert.DoesNotContain(Operations<IrCallOperation>(body), call => call.Method.Contains("Combine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Combine_of_three_listed_delegates_in_CSharp12_binds_the_params_array_and_has_three_operands()
    {
        const string SOURCE = "using System; class C { Delegate M(Action a, Action b, Action c) => Delegate.Combine(a, b, c); }";
        Assert.Equal(ArgumentKind.ParamArray, await CombineArgumentKind(SOURCE, LanguageVersion.CSharp12));

        var body = await Lower(SOURCE, LanguageVersion.CSharp12);

        var combine = Assert.Single(Operations<IrCombineDelegatesOperation>(body));
        Assert.Equal([Parameter(body, "a"), Parameter(body, "b"), Parameter(body, "c")],
                     combine.OperandValues.Select(value => Origin(body, value)));
        Assert.Equal(3, Operations<IrStoreElementOperation>(body).Length);
        Assert.Empty(Operations<IrCallOperation>(body));
    }

    [Fact]
    public async Task Combine_of_a_collection_expression_without_a_spread_has_its_two_elements_as_operands()
    {
        var body = await Lower("using System; class C { Delegate M(Action a, Action b) => Delegate.Combine([a, b]); }");

        var combine = Assert.Single(Operations<IrCombineDelegatesOperation>(body));
        Assert.Equal([Parameter(body, "a"), Parameter(body, "b")], combine.OperandValues.Select(value => Origin(body, value)));
        Assert.Empty(Operations<IrCallOperation>(body));
    }

    [Fact]
    public async Task Combine_of_an_existing_array_loads_its_cells_and_combines_the_loaded_value()
    {
        var body = await Lower("using System; class C { Delegate M(Delegate[] array) => Delegate.Combine(array); }");

        var operations = Operations(body);
        var load = Assert.Single(operations.OfType<IrLoadElementOperation>());
        var combine = Assert.Single(operations.OfType<IrCombineDelegatesOperation>());
        Assert.Equal(Parameter(body, "array"), Origin(body, load.ReceiverValue));
        Assert.False(load.NamesOneCell);
        Assert.Equal([load.ResultValue], combine.OperandValues);
        Assert.True(Array.IndexOf(operations, load) < Array.IndexOf(operations, combine));
        Assert.Empty(operations.OfType<IrCallOperation>());
        Assert.Empty(operations.OfType<IrUnknownOperation>());
    }

    [Fact]
    public async Task Remove_of_two_delegates_is_a_removal_with_source_first()
    {
        var body = await Lower("using System; class C { Delegate M(Action a, Action b) => Delegate.Remove(a, b); }");

        var remove = Assert.Single(Operations<IrCombineDelegatesOperation>(body));
        Assert.True(remove.Removes);
        Assert.Equal([Parameter(body, "a"), Parameter(body, "b")], remove.OperandValues.Select(value => Origin(body, value)));
        Assert.Contains($"remove-delegates %{remove.ResultValue} operands=", IrPrinter.Print(body));
        Assert.Empty(Operations<IrCallOperation>(body));
    }

    [Fact]
    public async Task Remove_with_named_arguments_in_reverse_order_keeps_source_as_its_first_operand()
    {
        var body = await Lower("using System; class C { Delegate M(Action a, Action b) => Delegate.Remove(value: b, source: a); }");

        var remove = Assert.Single(Operations<IrCombineDelegatesOperation>(body));
        Assert.Equal([Parameter(body, "a"), Parameter(body, "b")], remove.OperandValues.Select(value => Origin(body, value)));
        Assert.Equal([remove.OperandValues[0]], remove.ContributingOperands);
    }

    [Fact]
    public async Task RemoveAll_of_two_delegates_is_a_removal()
    {
        var body = await Lower("using System; class C { Delegate M(Action a, Action b) => Delegate.RemoveAll(a, b); }");

        Assert.True(Assert.Single(Operations<IrCombineDelegatesOperation>(body)).Removes);
        Assert.Empty(Operations<IrCallOperation>(body));
    }

    [Fact]
    public async Task Combine_of_a_spread_stays_a_call()
    {
        var body = await Lower("using System; class C { Delegate M(Delegate[] array) => Delegate.Combine([.. array]); }");

        Assert.Empty(Operations<IrCombineDelegatesOperation>(body));
        Assert.Contains(Operations<IrCallOperation>(body), call => call.Method.Contains("Combine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task User_defined_add_returning_a_delegate_and_its_compound_form_stay_computes()
    {
        var body = await Lower("""
            using System;
            class Bag
            {
                public static Action operator +(Bag bag, Action action) => action;
                public static Action operator +(Action action, Bag bag) => action;
            }
            class C
            {
                Action M(Bag bag, Action h)
                {
                    var first = bag + h;
                    Action local = h;
                    local += bag;
                    return first + local;
                }
            }
            """);

        // The two user-defined additions stay computes; only the predefined `first + local` is a combination.
        Assert.Equal(["Add", "Add"], Operations<IrComputeOperation>(body).Select(compute => compute.Operator));
        Assert.Single(Operations<IrCombineDelegatesOperation>(body));
    }

    [Fact]
    public async Task User_defined_subtract_returning_a_delegate_and_its_compound_form_stay_computes()
    {
        var body = await Lower("""
            using System;
            class Bag
            {
                public static Action operator -(Action action, Bag bag) => action;
            }
            class C
            {
                Action M(Bag bag, Action h)
                {
                    var removed = h - bag;
                    Action local = h;
                    local -= bag;
                    return removed;
                }
            }
            """);

        Assert.Empty(Operations<IrCombineDelegatesOperation>(body));
        Assert.Equal(["Subtract", "Subtract"], Operations<IrComputeOperation>(body).Select(compute => compute.Operator));
    }

    [Fact]
    public async Task Integer_add_stays_a_compute()
    {
        var body = await Lower("class C { int M(int x, int y) => x + y; }");

        Assert.Empty(Operations<IrCombineDelegatesOperation>(body));
        Assert.Equal("Add", Assert.Single(Operations<IrComputeOperation>(body)).Operator);
    }

    private static async Task<ArgumentKind> CombineArgumentKind(string source, LanguageVersion version)
    {
        var compilation = await Compile(source, version);
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var operation = (IInvocationOperation)model.GetOperation(invocation)!;
        Assert.NotNull(DelegateCombination.Of(operation));
        return Assert.Single(operation.Arguments).ArgumentKind;
    }

    private static async Task<Compilation> Compile(string source, LanguageVersion version)
    {
        var solution = FixtureSolution.Create(("Case.cs", source));
        var project = solution.Projects.Single();
        project = project.WithParseOptions(((CSharpParseOptions)project.ParseOptions!).WithLanguageVersion(version));
        return await project.GetCompilationAsync() ?? throw new InvalidOperationException();
    }

    private static async Task<IrBody> Lower(string source, LanguageVersion version = LanguageVersion.Latest)
    {
        var compilation = await Compile(source, version);
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var type = compilation.GetTypeByMetadataName("C") ?? throw new InvalidOperationException("Type C was not found.");
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        return IrLowering.Lower(method, compilation, ROOT_DIRECTORY, CancellationToken.None).Body;
    }

    private static int Parameter(IrBody body, string name) => body.Parameters.Single(parameter => parameter.Name == name).Value;

    /// <summary>The value a value was converted or copied from, followed back to the first one that is neither.</summary>
    /// <param name="body">The body the value is in.</param>
    /// <param name="value">The value.</param>
    private static int Origin(IrBody body, int value)
    {
        while (Operations(body).FirstOrDefault(operation => operation.DefinedValues.Contains(value)) is { } definition)
        {
            value = definition switch
            {
                IrConvertOperation convert => convert.OperandValue,
                IrAssignOperation assign => assign.SourceValue,
                _ => value
            };
            if (definition is not (IrConvertOperation or IrAssignOperation))
                break;
        }

        return value;
    }

    private static IrOperation[] Operations(IrBody body) =>
        body.Blocks.SelectMany(block => block.Operations).ToArray();

    private static T[] Operations<T>(IrBody body) where T : IrOperation =>
        Operations(body).OfType<T>().ToArray();
}
