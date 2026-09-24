using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class IrLoweringTests
{
    [Fact]
    public async Task Static_field_store_has_no_receiver()
    {
        var body = await Lower("class C { static int Value; static void M() { Value = 1; } }");

        var store = Assert.Single(Operations<IrStoreFieldOperation>(body));
        Assert.True(store.Field.IsStatic);
        Assert.Null(store.ReceiverValue);
        Assert.Equal("Value", store.Field.Name);
    }

    [Fact]
    public async Task Instance_field_store_uses_the_receiver()
    {
        var body = await Lower("class C { int Value; void M() { Value = 1; } }");

        var store = Assert.Single(Operations<IrStoreFieldOperation>(body));
        Assert.False(store.Field.IsStatic);
        Assert.NotNull(store.ReceiverValue);
        Assert.Equal(IrValueKind.Receiver, body.Values.Single(value => value.Id == store.ReceiverValue).Kind);
    }

    [Fact]
    public async Task Automatic_property_is_a_field_but_computed_property_is_accessor_calls()
    {
        var body = await Lower("""
            class C
            {
                int Auto { get; set; }
                int Computed { get => 1; set { } }
                void M() { Auto = 1; Computed = 2; var a = Auto; var c = Computed; }
            }
            """);

        Assert.Contains(Operations<IrStoreFieldOperation>(body), operation =>
            operation.Field is { Kind: IrFieldKind.PropertyBackingField, Name: "Auto" });
        Assert.Contains(Operations<IrLoadFieldOperation>(body), operation =>
            operation.Field is { Kind: IrFieldKind.PropertyBackingField, Name: "Auto" });
        Assert.Contains(Operations<IrCallOperation>(body), operation => operation.Method.Contains(".set_Computed(", StringComparison.Ordinal));
        Assert.Contains(Operations<IrCallOperation>(body), operation => operation.Method.Contains(".get_Computed(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Field_keyword_in_a_custom_accessor_stays_an_accessor_call()
    {
        var body = await Lower("""
            class C
            {
                int Value { get => field; set => field = value; }
                void M() { Value = 1; var value = Value; }
            }
            """);

        Assert.DoesNotContain(Operations<IrStoreFieldOperation>(body), operation => operation.Field.Name == "Value");
        Assert.Contains(Operations<IrCallOperation>(body), operation => operation.Method.Contains(".set_Value(", StringComparison.Ordinal));
        Assert.Contains(Operations<IrCallOperation>(body), operation => operation.Method.Contains(".get_Value(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Virtual_automatic_property_stays_accessor_calls()
    {
        var body = await Lower("class C { public virtual int Value { get; set; } void M() { Value = 1; var value = Value; } }");

        Assert.Empty(Operations<IrStoreFieldOperation>(body));
        Assert.All(Operations<IrCallOperation>(body), operation => Assert.Equal(IrCallKind.Virtual, operation.CallKind));
    }

    [Fact]
    public async Task Interface_property_stays_interface_calls()
    {
        var body = await Lower("""
            interface IState { int Value { get; set; } }
            class C { void M(IState state) { state.Value = 1; var value = state.Value; } }
            """);

        Assert.Empty(Operations<IrStoreFieldOperation>(body));
        Assert.All(Operations<IrCallOperation>(body), operation => Assert.Equal(IrCallKind.Interface, operation.CallKind));
    }

    [Fact]
    public async Task Base_call_into_a_source_body_runs_exactly_the_nearest_base_implementation()
    {
        var body = await Lower("""
            class A { public virtual void M() { } public virtual int Value { get => 1; set { } } }
            class B : A { public override void M() { } public override int Value { get => 2; set { } } }
            class C : B { public override void M() { base.M(); base.Value = base.Value; } }
            """);

        var calls = Operations<IrCallOperation>(body);
        Assert.All(calls, call => Assert.Equal(IrCallKind.Instance, call.CallKind));
        Assert.Equal(["body:Fixture:M:B.M", "body:Fixture:M:B.get_Value", "body:Fixture:M:B.set_Value(System.Int32)"],
                     calls.Select(call => call.TargetMethodId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Base_ref_indexer_as_a_reference_runs_exactly_the_nearest_base_accessor()
    {
        var body = await Lower("""
            class A { int _cell; public virtual ref int this[int i] => ref _cell; }
            class B : A { public override ref int this[int i] => ref base[i]; }
            class C : B { public override ref int this[int i] => ref base[i]; void M() { base[0] = 1; var read = base[1]; } }
            """);

        var calls = Operations<IrCallOperation>(body);
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Equal(IrCallKind.Instance, call.CallKind));
        Assert.All(calls, call => Assert.Equal("body:Fixture:M:B.get_Item(System.Int32)", call.TargetMethodId));
    }

    [Fact]
    public async Task Base_method_group_delegate_runs_exactly_the_nearest_base_implementation()
    {
        var body = await Lower("""
            class A { public virtual void M() { } }
            class B : A { public override void M() { } }
            class C : B { public override void M() { System.Action fromBase = base.M; System.Action fromThis = this.M; } }
            """);

        var creations = Operations<IrCreateDelegateOperation>(body);
        var fromBase = Assert.Single(creations, create => create.TargetMethodId == "body:Fixture:M:B.M");
        Assert.True(fromBase.IsNonVirtual);
        Assert.False(Assert.Single(creations, create => create.TargetMethodId == "body:Fixture:M:C.M").IsNonVirtual);
    }

    [Fact]
    public async Task Boxing_conversion_is_recorded()
    {
        var body = await Lower("class C { object M(int value) => value; }");

        Assert.Contains(Operations<IrConvertOperation>(body), operation => operation.ConversionKind == IrConversionKind.Boxing);
    }

    [Fact]
    public async Task Unboxing_conversion_is_recorded()
    {
        var body = await Lower("class C { int M(object value) => (int)value; }");

        Assert.Contains(Operations<IrConvertOperation>(body), operation => operation.ConversionKind == IrConversionKind.Unboxing);
    }

    [Fact]
    public async Task Primary_constructor_parameter_used_by_a_method_is_a_receiver_field()
    {
        var body = await Lower("class C(string owner) { string M(string value) { owner += value; return owner; } }");

        var loads = Operations<IrLoadFieldOperation>(body).ToArray();
        var store = Assert.Single(Operations<IrStoreFieldOperation>(body));
        Assert.Equal(2, loads.Length);
        Assert.All(loads, load => Assert.Equal(IrFieldKind.PrimaryConstructorParameter, load.Field.Kind));
        Assert.Equal(IrFieldKind.PrimaryConstructorParameter, store.Field.Kind);
        Assert.Equal("owner", store.Field.Name);
        Assert.NotNull(store.ReadModifyWriteOf);
        Assert.All(loads, load => Assert.NotNull(load.ReceiverValue));
        Assert.NotNull(store.ReceiverValue);
    }

    [Fact]
    public async Task Compound_field_assignment_is_one_read_modify_write()
    {
        var body = await Lower("class C { int Value; void M() { Value += 2; } }");

        var load = Assert.Single(Operations<IrLoadFieldOperation>(body));
        Assert.Single(Operations<IrComputeOperation>(body));
        var store = Assert.Single(Operations<IrStoreFieldOperation>(body));
        Assert.Equal(load.Id, store.ReadModifyWriteOf);
        Assert.Equal("compound-assignment", store.Provenance.Transformation);
    }

    [Fact]
    public async Task Field_increment_is_one_read_modify_write()
    {
        var body = await Lower("class C { int Value; void M() { Value++; } }");

        var load = Assert.Single(Operations<IrLoadFieldOperation>(body));
        Assert.Single(Operations<IrComputeOperation>(body));
        var store = Assert.Single(Operations<IrStoreFieldOperation>(body));
        Assert.Equal(load.Id, store.ReadModifyWriteOf);
        Assert.Equal("increment", store.Provenance.Transformation);
    }

    [Fact]
    public async Task Deconstruction_stores_targets_left_to_right()
    {
        var body = await Lower("class C { static int Left; static int Right; void M() { (Left, Right) = (1, 2); } }");

        Assert.Equal(["Left", "Right"], Operations<IrStoreFieldOperation>(body).Select(store => store.Field.Name));
    }

    [Fact]
    public async Task Deconstruction_from_a_tuple_local_stores_every_target()
    {
        var body = await Lower("""
            class C
            {
                static int Left;
                static int Right;
                void M((int, int) value) { var pair = value; (Left, Right) = pair; }
            }
            """);

        var stores = Operations<IrStoreFieldOperation>(body);
        Assert.Equal(["Left", "Right"], stores.Select(store => store.Field.Name));
        Assert.NotEqual(stores[0].Value, stores[1].Value);
    }

    [Fact]
    public async Task Deconstruction_into_nested_tuple_targets_stores_every_target()
    {
        var body = await Lower("""
            class C
            {
                static int A;
                static int B;
                static int D;
                void M((int, (int, int)) pair, (int, int) inner) { (A, (B, D)) = pair; (D, (B, A)) = (1, inner); }
            }
            """);

        Assert.Equal(["A", "B", "D", "D", "B", "A"], Operations<IrStoreFieldOperation>(body).Select(store => store.Field.Name));
    }

    [Fact]
    public async Task Deconstruction_declaring_locals_from_a_tuple_parameter_defines_both()
    {
        var body = await Lower("""
            class C
            {
                static int Left;
                static int Right;
                void M((int, int) pair) { var (x, y) = pair; Left = x; Right = y; }
            }
            """);

        var stores = Operations<IrStoreFieldOperation>(body);
        Assert.Equal(["Left", "Right"], stores.Select(store => store.Field.Name));
        Assert.Equal(["x", "y"], stores.Select(store => body.Values.Single(value => value.Id == store.Value).Name));
    }

    [Fact]
    public async Task Ref_field_argument_is_address_taken_without_a_load_or_store()
    {
        var body = await Lower("class C { int Value; static void Take(ref int value) { } void M() { Take(ref Value); } }");

        Assert.Single(Operations<IrAddressFieldOperation>(body));
        Assert.Empty(Operations<IrLoadFieldOperation>(body));
        Assert.Empty(Operations<IrStoreFieldOperation>(body));
    }

    [Fact]
    public async Task Field_passed_to_an_in_parameter_without_keyword_is_address_taken()
    {
        var body = await Lower("class C { int Value; static void Look(in int value) { } void M() { Look(Value); } }");
        var readOnlyRef = await Lower("class C { int Value; static void Look(ref readonly int value) { } void M() { Look(in Value); } }");

        foreach (var lowered in new[] { body, readOnlyRef })
        {
            Assert.Single(Operations<IrAddressFieldOperation>(lowered));
            Assert.Empty(Operations<IrLoadFieldOperation>(lowered));
        }
    }

    [Fact]
    public async Task Null_tests_lower_as_null_comparisons()
    {
        string[] nullTests =
        [
            "class C { string M(string? a) => a ?? \"fallback\"; }",
            "class C { bool M(string? a) => a == null; }",
            "class C { bool M(int? a) => null != a; }",
            "class C { bool M(string? a) => a is null; }",
            "class C { bool M(string? a) => a is not null; }"
        ];
        foreach (var source in nullTests)
        {
            var body = await Lower(source);

            var compare = Assert.Single(Operations<IrCompareOperation>(body), operation => operation.Comparison == IrComparisonKind.Null);
            Assert.Null(compare.RightValue);
            Assert.Null(compare.Type);
            Assert.NotEqual(IrValueKind.Constant, body.Values.Single(value => value.Id == compare.LeftValue).Kind);
            Assert.DoesNotContain(Operations<IrUnknownOperation>(body), operation => operation.Reason == "unsupported");
            Assert.Empty(IrValidator.Validate(body));
        }

        var userDefined = await Lower("""
            class P
            {
                public static bool operator ==(P? left, P? right) => true;
                public static bool operator !=(P? left, P? right) => false;
                public override bool Equals(object? other) => true;
                public override int GetHashCode() => 0;
            }
            class C { bool M(P? a) => a == null; }
            """);
        Assert.DoesNotContain(Operations<IrCompareOperation>(userDefined), operation => operation.Comparison == IrComparisonKind.Null);
        Assert.True(Operations<IrCompareOperation>(userDefined).Any(operation => operation.Comparison == IrComparisonKind.Equality) ||
                    Operations<IrCallOperation>(userDefined).Length != 0);
        Assert.Empty(IrValidator.Validate(userDefined));
    }

    [Fact]
    public async Task Nameof_is_a_constant()
    {
        var body = await Lower("class C { int Value; string M() => nameof(Value); }");

        Assert.Contains(body.Values, value => value is { Kind: IrValueKind.Constant, Name: "Value" });
        Assert.Empty(Operations<IrUnknownOperation>(body));
    }

    [Fact]
    public async Task Object_creation_is_allocate_then_constructor_call()
    {
        var body = await Lower("class C { object M() => new object(); }");
        var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
        var allocate = Assert.Single(operations.OfType<IrAllocateOperation>());
        var constructor = Assert.Single(operations.OfType<IrCallOperation>(),
            call => call.CallKind == IrCallKind.Constructor);

        Assert.True(Array.IndexOf(operations, allocate) < Array.IndexOf(operations, constructor));
        Assert.Equal(allocate.ResultValue, constructor.ReceiverValue);
    }

    [Fact]
    public async Task If_else_has_a_join_with_both_explicit_predecessors()
    {
        var body = await Lower("""
            class C
            {
                int M(bool condition)
                {
                    int value;
                    if (condition) value = 1; else value = 2;
                    return value;
                }
            }
            """);

        Assert.Equal(Enumerable.Range(0, body.Blocks.Count), body.Blocks.Select(block => block.Ordinal));
        Assert.Contains(body.Blocks, block => block.Predecessors.Distinct().Count() == 2);
    }

    [Fact]
    public async Task Try_finally_regions_and_branch_finally_path_are_copied()
    {
        var body = await Lower("""
            class C
            {
                static int Value;
                void M() { try { Value = 1; } finally { Value = 2; } }
            }
            """);

        Assert.Contains(body.Regions, region => region.Kind == IrRegionKind.TryAndFinally);
        Assert.Contains(body.Regions, region => region.Kind == IrRegionKind.Finally);
        Assert.Contains(body.Blocks.SelectMany(Branches), branch => branch.FinallyRegions.Count != 0);
    }

    [Fact]
    public async Task Await_is_recorded()
    {
        var body = await Lower("""
            using System.Threading.Tasks;
            class C { async Task<int> M(Task<int> value) => await value; }
            """);

        Assert.Single(Operations<IrAwaitOperation>(body));
    }

    [Fact]
    public async Task Yield_return_is_printed()
    {
        var body = await Lower("""
            using System.Collections.Generic;
            class C { IEnumerable<int> M() { yield return 1; } }
            """);

        var yielded = Assert.Single(Operations<IrYieldOperation>(body));
        Assert.NotNull(yielded.Value);
        Assert.Contains($"operation {yielded.Id} yield value=%{yielded.Value} |", IrPrinter.Print(body));
    }

    [Fact]
    public async Task Provenance_is_relative_slash_separated_and_one_based()
    {
        var body = await Lower("""
            class C
            {
                int Value;
                void M() { Value = 1; }
            }
            """);
        var provenance = Assert.Single(Operations<IrStoreFieldOperation>(body)).Provenance;

        Assert.Equal("Case.cs", provenance.Span.Path);
        Assert.True(provenance.Span.StartLine >= 1);
        Assert.True(provenance.Span.StartColumn >= 1);
        Assert.Equal("C.M()", provenance.ContainingSymbol);
    }

    [Fact]
    public async Task Unsupported_operation_still_lowers_its_child_field_load()
    {
        var body = await Lower("class C { static int Value; string M() => $\"value={Value}\"; }");
        var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();

        var load = Assert.Single(operations.OfType<IrLoadFieldOperation>());
        Assert.Equal("Value", load.Field.Name);
        var consumer = Assert.Single(operations.OfType<IrUnknownOperation>(), unknown => unknown.OperandValues.Contains(load.ResultValue));
        Assert.Equal("unsupported", consumer.Reason);
        Assert.True(Array.IndexOf(operations, load) < Array.IndexOf(operations, consumer));
        Assert.Empty(IrValidator.Validate(body));
    }

    [Fact]
    public async Task Unsupported_operation_is_unknown()
    {
        var body = await Lower("class C { string M(int value) => $\"value={value}\"; }");

        Assert.Contains(Operations<IrUnknownOperation>(body), operation => operation.Reason == "unsupported");
    }

    [Fact]
    public async Task Method_without_a_source_body_throws()
    {
        var solution = FixtureSolution.Create(("Case.cs", "class C { void M() { } }"));
        var compilation = await solution.Projects.Single().GetCompilationAsync() ?? throw new InvalidOperationException();
        var method = compilation.GetSpecialType(SpecialType.System_String).GetMembers("ToString").OfType<IMethodSymbol>().First();

        Assert.Throws<ArgumentException>(() => IrLowering.Lower(method, compilation, @"C:\fixture", CancellationToken.None));
    }

    private static async Task<IrBody> Lower(string source)
    {
        var solution = FixtureSolution.Create(("Case.cs", source));
        var compilation = await solution.Projects.Single().GetCompilationAsync() ?? throw new InvalidOperationException();
        var type = compilation.GetTypeByMetadataName("C") ?? throw new InvalidOperationException("Type C was not found.");
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        return IrLowering.Lower(method, compilation, @"C:\fixture", CancellationToken.None).Body;
    }

    private static IrOperation[] Operations(IrBody body) =>
        body.Blocks.SelectMany(block => block.Operations).ToArray();

    private static T[] Operations<T>(IrBody body) where T : IrOperation =>
        Operations(body).OfType<T>().ToArray();

    private static IEnumerable<IrBranch> Branches(IrBlock block)
    {
        if (block.ConditionalBranch is not null)
            yield return block.ConditionalBranch;
        if (block.FallThroughBranch is not null)
            yield return block.FallThroughBranch;
    }
}
