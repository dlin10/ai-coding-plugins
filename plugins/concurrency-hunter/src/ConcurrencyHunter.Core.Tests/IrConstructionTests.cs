using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class IrConstructionTests
{
    [Fact]
    public async Task Initializers_run_before_the_explicit_constructor_body()
    {
        var (body, _) = await LowerConstructor("class C { int _a = 1; object _b = new object(); public C() { _a = 2; } }");

        Assert.Equal("body:Fixture:M:C.#ctor", body.BodyId);
        Assert.Equal("C..ctor()", body.OwnerSymbol);
        Assert.Equal(["store:_a", "store:_b", "call:object..ctor()", "store:_a"], Steps(body));
        var stores = Operations<IrStoreFieldOperation>(body);
        Assert.Equal(["1", "2"], new[] { stores[0], stores[2] }.Select(store => Value(body, store.Value).Name));
        Assert.Empty(IrValidator.Validate(body));
    }

    [Fact]
    public async Task Implicit_constructor_has_a_body_with_its_initializers()
    {
        var (body, _) = await LowerConstructor("class C { int _a = 1; string B { get; } = \"b\"; }");

        Assert.Equal("body:Fixture:M:C.#ctor", body.BodyId);
        Assert.Equal(["store:_a", "store:B", "call:object..ctor()"], Steps(body));
        Assert.Equal(IrFieldKind.PropertyBackingField, Operations<IrStoreFieldOperation>(body)[1].Field.Kind);
        Assert.Equal(IrBlockKind.Entry, body.Blocks[0].Kind);
        Assert.Equal(IrBlockKind.Exit, body.Blocks[^1].Kind);
    }

    [Fact]
    public async Task This_chaining_skips_initializers()
    {
        var (body, _) = await LowerConstructor("class C { int _a = 1; public C() : this(2) { } public C(int a) { _a = a; } }",
                                               constructor => constructor.Parameters.Length == 0);

        Assert.Equal(["call:C..ctor(int)"], Steps(body));
        var call = Assert.Single(Operations<IrCallOperation>(body));
        Assert.Equal(IrCallKind.Constructor, call.CallKind);
        Assert.Equal(IrValueKind.Receiver, Value(body, call.ReceiverValue!.Value).Kind);
    }

    [Fact]
    public async Task Base_call_comes_after_initializers()
    {
        var (body, _) = await LowerConstructor("""
            class B { public B(int x) { } }
            class C : B { int _a = 1; public C() : base(5) { _a = 2; } }
            """);

        Assert.Equal(["store:_a", "call:B..ctor(int)", "store:_a"], Steps(body));
        var call = Assert.Single(Operations<IrCallOperation>(body));
        Assert.Equal(IrValueKind.Receiver, Value(body, call.ReceiverValue!.Value).Kind);
        Assert.Equal("body:Fixture:M:B.#ctor(System.Int32)", call.TargetMethodId);
    }

    [Fact]
    public async Task Type_initializer_body_comes_from_static_initializers_only()
    {
        var (body, _) = await LowerTypeInitializer("class C { static int A = 1; static object B = new object(); int _instance = 3; }");

        Assert.Equal("body:Fixture:M:C.#cctor", body.BodyId);
        Assert.Equal("C..cctor()", body.OwnerSymbol);
        Assert.Equal(["store:A", "store:B"], Steps(body));
        Assert.All(Operations<IrStoreFieldOperation>(body), store => Assert.True(store.Field.IsStatic));
        Assert.DoesNotContain(body.Values, value => value.Kind == IrValueKind.Receiver);
    }

    [Fact]
    public async Task Declared_static_constructor_runs_after_static_initializers()
    {
        var (body, _) = await LowerTypeInitializer("class C { static int A = 1; static C() { A = 2; } }");

        Assert.Equal(["store:A", "store:A"], Steps(body));
        Assert.Equal(["1", "2"], Operations<IrStoreFieldOperation>(body).Select(store => Value(body, store.Value).Name));
        Assert.Empty(IrValidator.Validate(body));
    }

    [Fact]
    public async Task Array_creation_is_an_allocation_with_element_stores()
    {
        var body = (await LowerMethod("class C { object M() { var array = new int[] { 1, 2 }; return array; } }")).Body;

        var allocate = Assert.Single(Operations<IrAllocateOperation>(body));
        Assert.Equal("int[]", allocate.AllocatedType);
        Assert.EndsWith(":int[]", allocate.AllocatedTypeKey, StringComparison.Ordinal);
        var stores = Operations<IrStoreElementOperation>(body);
        Assert.Equal(2, stores.Length);
        Assert.All(stores, store => Assert.Equal(allocate.ResultValue, store.ReceiverValue));
        Assert.Equal(["0", "1"], stores.Select(store => Value(body, Assert.Single(store.IndexValues)).Name));
        Assert.Equal(["1", "2"], stores.Select(store => Value(body, store.Value).Name));
        Assert.DoesNotContain(Operations<IrUnknownOperation>(body), unknown => unknown.Reason == "unsupported");
    }

    [Fact]
    public async Task Out_argument_defines_a_new_local_version_after_the_call()
    {
        var body = (await LowerMethod("""
            class C
            {
                static void Take(out int value) { value = 1; }
                static void Use(int value) { }
                void M() { int x = 0; Take(out x); Use(x); }
            }
            """)).Body;

        var calls = Operations<IrCallOperation>(body);
        var take = Assert.Single(calls, call => call.Method == "C.Take(int)");
        var defined = Assert.Single(take.RefResults);
        Assert.Equal(0, defined.Key);
        Assert.Equal(("x", IrValueKind.Local, 2), (Value(body, defined.Value).Name, Value(body, defined.Value).Kind, Value(body, defined.Value).SsaVersion));
        Assert.Contains(defined.Value, take.DefinedValues);
        Assert.Equal(defined.Value, Assert.Single(Assert.Single(calls, call => call.Method == "C.Use(int)").ArgumentValues));
        Assert.Contains(Operations<IrUnknownOperation>(body), unknown => unknown.Reason == "address-taken");
    }

    [Fact]
    public async Task Ref_parameter_final_value_is_readable_at_return()
    {
        var body = (await LowerMethod("class C { int M(ref int value, bool flag) { if (flag) value = 1; else value = 2; return 0; } }")).Body;

        var parameter = Assert.Single(body.Parameters, candidate => candidate.Name == "value");
        Assert.Equal((IrRefKind.Ref, 0, 0), (parameter.RefKind, parameter.Ordinal, Value(body, parameter.Value).SsaVersion));
        Assert.Equal(IrRefKind.None, body.Parameters[1].RefKind);
        var returnBlock = Assert.Single(body.Blocks, block => block.Operations.OfType<IrReturnOperation>().Any());
        var final = ValueAtEnd(body, returnBlock, Value(body, parameter.Value).SymbolKey!);
        var phi = Assert.IsType<IrPhiOperation>(Definition(body, final));
        Assert.Equal([1, 2], phi.Inputs.Select(input => Value(body, input.Value).SsaVersion).Order());
    }

    [Fact]
    public async Task Call_and_delegate_target_ids_name_source_metadata_and_generic_methods()
    {
        var body = (await LowerMethod("""
            class C
            {
                void Source() { }
                static T Generic<T>(T value) => value;
                void M()
                {
                    Source();
                    var text = 1.ToString();
                    Generic<string>("x");
                    System.Action source = Source;
                    System.Func<string, string> generic = Generic<string>;
                    System.Func<string> metadata = "x".ToUpperInvariant;
                }
            }
            """)).Body;

        var calls = Operations<IrCallOperation>(body);
        Assert.Equal("body:Fixture:M:C.Source", calls[0].TargetMethodId);
        Assert.EndsWith(":M:System.Int32.ToString", calls[1].TargetMethodId, StringComparison.Ordinal);
        Assert.StartsWith("body:System.", calls[1].TargetMethodId, StringComparison.Ordinal);
        Assert.Equal("body:Fixture:M:C.Generic``1(``0)", calls[2].TargetMethodId);
        Assert.EndsWith(":string", Assert.Single(calls[2].TargetMethodTypeArgumentKeys), StringComparison.Ordinal);
        Assert.Equal("Fixture:C", calls[0].TargetContainingTypeKey);
        var creates = Operations<IrCreateDelegateOperation>(body);
        Assert.Equal("body:Fixture:M:C.Source", creates[0].TargetMethodId);
        Assert.Equal("body:Fixture:M:C.Generic``1(``0)", creates[1].TargetMethodId);
        Assert.EndsWith(":string", Assert.Single(creates[1].TargetMethodTypeArgumentKeys), StringComparison.Ordinal);
        Assert.EndsWith(":M:System.String.ToUpperInvariant", creates[2].TargetMethodId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Named_and_reordered_arguments_map_to_their_parameter_ordinals()
    {
        var body = (await LowerMethod("""
            class C
            {
                static void Take(int first, string second, bool third = false) { }
                void M() { Take(second: "s", first: 1); Take(1, third: true, second: "x"); }
            }
            """)).Body;

        var calls = Operations<IrCallOperation>(body);
        Assert.Equal([1, 0, 2], calls[0].ArgumentParameterOrdinals);
        Assert.Equal("s", Value(body, calls[0].ArgumentValues[0]).Name);
        Assert.Equal([0, 2, 1], calls[1].ArgumentParameterOrdinals);
        Assert.Equal("x", Value(body, calls[1].ArgumentValues[2]).Name);
    }

    [Fact]
    public async Task Allocation_site_ordinals_count_initializers_and_lambdas_in_source_order()
    {
        var (body, nested) = await LowerConstructor("""
            class C
            {
                object _a = new object();
                public C()
                {
                    var b = new object();
                    System.Func<object> f = () => new object();
                    var c = new object();
                    var builder = new System.Text.StringBuilder();
                }
            }
            """);

        Assert.Equal([1, 2, 4], Operations<IrAllocateOperation>(body).Where(allocate => allocate.AllocatedType == "object").Select(allocate => allocate.SiteOrdinal));
        Assert.Equal(1, Assert.Single(Operations<IrAllocateOperation>(body), allocate => allocate.AllocatedType == "System.Text.StringBuilder").SiteOrdinal);
        Assert.Equal(3, Assert.Single(Operations<IrAllocateOperation>(Assert.Single(nested))).SiteOrdinal);
        Assert.Equal(1, Assert.Single(Operations<IrCreateDelegateOperation>(body)).SiteOrdinal);
    }

    [Fact]
    public async Task Lambda_capture_carries_the_symbol_key_of_the_outer_local()
    {
        var lowered = await LowerMethod("""
            class C
            {
                void M()
                {
                    var local = 1;
                    System.Action action = () => System.GC.KeepAlive(local);
                    System.GC.KeepAlive(local);
                }
            }
            """);

        var outer = Assert.Single(lowered.Body.Values, value => value is { Name: "local", Kind: IrValueKind.Local });
        Assert.StartsWith(lowered.Body.BodyId + "|local|", outer.SymbolKey, StringComparison.Ordinal);
        var nested = Assert.Single(lowered.NestedBodies);
        var capture = Assert.Single(Operations<IrCaptureOperation>(nested));
        Assert.Equal(outer.SymbolKey, capture.SymbolKey);
        Assert.Equal(outer.SymbolKey, Value(nested, capture.Value).SymbolKey);
        Assert.Equal([outer.SymbolKey!], Assert.Single(Operations<IrCreateDelegateOperation>(lowered.Body)).CapturedSymbolKeys);
        Assert.Equal("this", Assert.Single(lowered.Body.Values, value => value.Kind == IrValueKind.Receiver).SymbolKey);
    }

    [Fact]
    public async Task Call_and_method_group_inside_a_generic_type_record_constructed_type_and_method_type_arguments()
    {
        var body = (await LowerMethod("""
            class Order { }
            class Invoice { }
            class Cache<T>
            {
                public void Put<U>() { }
                void M()
                {
                    new Cache<Order>().Put<Invoice>();
                    System.Action put = new Cache<Order>().Put<Invoice>;
                }
            }
            """, "Cache`1")).Body;

        var call = Assert.Single(Operations<IrCallOperation>(body), operation => operation.CallKind != IrCallKind.Constructor);
        var create = Assert.Single(Operations<IrCreateDelegateOperation>(body));
        foreach (var (containing, arguments, id) in new[]
                 {
                     (call.TargetContainingTypeKey, call.TargetMethodTypeArgumentKeys, call.TargetMethodId),
                     (create.TargetContainingTypeKey, create.TargetMethodTypeArgumentKeys, create.TargetMethodId)
                 })
        {
            Assert.Equal("Fixture:Cache<Fixture:Order>", containing);
            Assert.Equal(["Fixture:Invoice"], arguments);
            Assert.Equal("body:Fixture:M:Cache`1.Put``1", id);
        }
    }

    [Fact]
    public async Task Expanded_params_call_passes_one_allocated_array_with_element_stores()
    {
        var body = (await LowerMethod("class C { static void Take(params int[] values) { } void M() { Take(1, 2); } }")).Body;

        var call = Assert.Single(Operations<IrCallOperation>(body));
        var allocate = Assert.Single(Operations<IrAllocateOperation>(body));
        Assert.Equal([allocate.ResultValue], call.ArgumentValues);
        Assert.Equal([0], call.ArgumentParameterOrdinals);
        var stores = Operations<IrStoreElementOperation>(body);
        Assert.All(stores, store => Assert.Equal(allocate.ResultValue, store.ReceiverValue));
        Assert.Equal(["1", "2"], stores.Select(store => Value(body, store.Value).Name));
    }

    [Fact]
    public async Task Auto_property_synthesized_getter_and_setter_use_its_backing_field()
    {
        var compilation = await Compile("class C { public int Value { get; set; } }");
        var property = (IPropertySymbol)compilation.GetTypeByMetadataName("C")!.GetMembers("Value").Single();

        var getter = IrLowering.Lower(property.GetMethod!, compilation, @"C:\fixture", CancellationToken.None).Body;
        var setter = IrLowering.Lower(property.SetMethod!, compilation, @"C:\fixture", CancellationToken.None).Body;

        Assert.Equal("body:Fixture:M:C.get_Value", getter.BodyId);
        var load = Assert.Single(Operations<IrLoadFieldOperation>(getter));
        Assert.Equal(("Value", IrFieldKind.PropertyBackingField), (load.Field.Name, load.Field.Kind));
        Assert.Equal(IrValueKind.Receiver, Value(getter, load.ReceiverValue!.Value).Kind);
        Assert.Equal(load.ResultValue, Assert.Single(Operations<IrReturnOperation>(getter)).Value);
        var store = Assert.Single(Operations<IrStoreFieldOperation>(setter));
        Assert.Equal(("Value", IrFieldKind.PropertyBackingField), (store.Field.Name, store.Field.Kind));
        Assert.Equal(Assert.Single(setter.Parameters).Value, store.Value);
        Assert.Empty(IrValidator.Validate(getter));
    }

    [Fact]
    public async Task Primary_constructor_parameter_used_only_inside_an_initializer_lambda_is_stored()
    {
        var (body, nested) = await LowerConstructor(
            "class Service { public void Use() { } } class C(Service service) { System.Action Run = () => service.Use(); }");

        var stored = Assert.Single(Operations<IrStoreFieldOperation>(body), store => store.Field.Kind == IrFieldKind.PrimaryConstructorParameter);
        Assert.Equal("service", stored.Field.Name);
        Assert.Equal(Assert.Single(body.Parameters).Value, stored.Value);
        var load = Assert.Single(Operations<IrLoadFieldOperation>(Assert.Single(nested)));
        Assert.Equal(("service", IrFieldKind.PrimaryConstructorParameter), (load.Field.Name, load.Field.Kind));
    }

    [Fact]
    public async Task Primary_constructor_stores_captured_parameters_before_initializers()
    {
        var (body, _) = await LowerConstructor("class C(int count, string name) { int _doubled = count * 2; public string Name() => name; }");

        Assert.Equal(["store:name", "store:_doubled", "call:object..ctor()"], Steps(body));
        var stored = Operations<IrStoreFieldOperation>(body)[0];
        Assert.Equal(IrFieldKind.PrimaryConstructorParameter, stored.Field.Kind);
        Assert.Equal(body.Parameters[1].Value, stored.Value);
        var doubled = Operations<IrStoreFieldOperation>(body)[1];
        Assert.IsType<IrComputeOperation>(Definition(body, doubled.Value));
    }

    [Fact]
    public async Task Constructor_lambdas_are_numbered_across_initializers_and_body()
    {
        const string SOURCE = """
            class C
            {
                System.Func<int> _first = () => 1;
                System.Func<int> _second = () => 2;
                public C() { System.Func<int> third = () => 3; }
            }
            """;
        var (body, nested) = await LowerConstructor(SOURCE);
        var compilation = await Compile(SOURCE);
        var constructor = compilation.GetTypeByMetadataName("C")!.InstanceConstructors.Single();

        var ids = nested.Select(candidate => candidate.BodyId).ToArray();
        Assert.Equal([body.BodyId + "#lambda1", body.BodyId + "#lambda2", body.BodyId + "#lambda3"], ids);
        Assert.Equal(ids, Operations<IrCreateDelegateOperation>(body).Select(create => create.TargetBodyId));
        Assert.Equal(ids, IrLowering.NestedBodyIds(constructor, compilation, CancellationToken.None).Values.Order(StringComparer.Ordinal));
    }

    private static async Task<(IrBody Body, IReadOnlyList<IrBody> Nested)> LowerConstructor(string source,
                                                                                             Func<IMethodSymbol, bool>? select = null)
    {
        var compilation = await Compile(source);
        var constructor = compilation.GetTypeByMetadataName("C")!.InstanceConstructors.Single(select ?? (_ => true));
        var lowered = IrLowering.Lower(constructor, compilation, @"C:\fixture", CancellationToken.None);
        return (lowered.Body, lowered.NestedBodies);
    }

    private static async Task<(IrBody Body, IReadOnlyList<IrBody> Nested)> LowerTypeInitializer(string source)
    {
        var compilation = await Compile(source);
        var initializer = compilation.GetTypeByMetadataName("C")!.StaticConstructors.Single();
        var lowered = IrLowering.Lower(initializer, compilation, @"C:\fixture", CancellationToken.None);
        return (lowered.Body, lowered.NestedBodies);
    }

    private static async Task<IrLoweredMethod> LowerMethod(string source, string typeName = "C")
    {
        var compilation = await Compile(source);
        var method = compilation.GetTypeByMetadataName(typeName)!.GetMembers("M").OfType<IMethodSymbol>().Single();
        return IrLowering.Lower(method, compilation, @"C:\fixture", CancellationToken.None);
    }

    private static async Task<Compilation> Compile(string source) =>
        await FixtureSolution.Create(("Case.cs", source)).Projects.Single().GetCompilationAsync() ?? throw new InvalidOperationException();

    /// <summary>The field stores and calls of a body in order, as <c>store:field</c> and <c>call:method</c>, leaving out the
    /// constructor calls of <c>new</c> expressions.</summary>
    private static string[] Steps(IrBody body) =>
        body.Blocks.SelectMany(block => block.Operations)
            .Select(operation => operation switch
            {
                IrStoreFieldOperation store => "store:" + store.Field.Name,
                IrCallOperation call when call.Provenance.Transformation != "object-creation" => "call:" + call.Method,
                _ => null
            })
            .OfType<string>()
            .ToArray();

    private static T[] Operations<T>(IrBody body) where T : IrOperation =>
        body.Blocks.SelectMany(block => block.Operations).OfType<T>().ToArray();

    private static IrValue Value(IrBody body, int id) => body.Values.Single(value => value.Id == id);

    private static IrOperation? Definition(IrBody body, int value) =>
        body.Blocks.SelectMany(block => block.Operations).FirstOrDefault(operation => operation.DefinedValues.Contains(value));

    /// <summary>The version of a variable when <paramref name="block"/> ends: its last definition there, else its phi there, else
    /// its version when a flow predecessor ends, else its incoming value.</summary>
    private static int ValueAtEnd(IrBody body, IrBlock block, string symbolKey)
    {
        var defined = block.Operations.SelectMany(operation => operation.DefinedValues)
                           .LastOrDefault(value => Value(body, value).SymbolKey == symbolKey, -1);
        if (defined >= 0)
            return defined;
        if (block.FlowPredecessors.Count == 0)
            return body.Values.First(value => value.SymbolKey == symbolKey && value.SsaVersion == 0).Id;
        return ValueAtEnd(body, body.Blocks[block.FlowPredecessors[0].BlockOrdinal], symbolKey);
    }
}
