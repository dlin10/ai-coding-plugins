using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests;

/// <summary>A struct of the program kept in heap storage — a field, a static field, a cell, a <c>ref</c> or <c>in</c> reference to one, a
/// <c>ref</c> return — is lowered as the address of that storage wherever an operation reaches through it: the receiver of a field access
/// and of a member call, a property or an event accessor among them (R1). A defensive copy, a method-group delegate's box and a by-value
/// use take its value (R2), and <c>new</c> and <c>default</c> make a value that is no allocation region.</summary>
public sealed class StructPlaceLoweringTests
{
    private const string STATE = """
        public struct State
        {
            public int Count;
            public void Increment() { Count++; }
            public readonly int Peek() => Count;
            public int Twice { get => Count * 2; set => Count = value / 2; }
            public event System.Action? Changed { add { Count++; } remove { Count--; } }
        }
        """;

    private const string FIELDS = """
            private State _state;
            private static State s_state;
            private readonly State[] _states = new State[2];
            private readonly State _ro;
        """;

    [Fact]
    public async Task Field_access_through_a_struct_field_is_on_its_address()
    {
        var body = await Lower("void M() { _state.Count++; }");

        var address = Assert.Single(Operations(body).OfType<IrAddressFieldOperation>());
        Assert.Equal("_state", address.Field.Name);
        Assert.True(Value(body, address.ResultValue).IsReference);
        Assert.Equal("Fixture:State", Value(body, address.ResultValue).StructTypeKey);
        Assert.All(Operations(body).OfType<IrLoadFieldOperation>(), load => Assert.Equal(address.ResultValue, load.ReceiverValue));
        Assert.Equal(address.ResultValue, Assert.Single(Operations(body).OfType<IrStoreFieldOperation>()).ReceiverValue);
        Assert.DoesNotContain(Operations(body).OfType<IrLoadFieldOperation>(), load => load.Field.Name == "_state");
    }

    [Fact]
    public async Task Field_access_through_a_static_struct_field_is_on_its_address()
    {
        var body = await Lower("void M() { s_state.Count = 1; }");

        var address = Assert.Single(Operations(body).OfType<IrAddressFieldOperation>());
        Assert.Equal("s_state", address.Field.Name);
        Assert.Null(address.ReceiverValue);
        Assert.Equal(address.ResultValue, Assert.Single(Operations(body).OfType<IrStoreFieldOperation>()).ReceiverValue);
    }

    [Fact]
    public async Task Field_access_through_a_struct_cell_is_on_the_cell_address()
    {
        var body = await Lower("void M(int i) { _states[i].Count++; }");

        var address = Assert.Single(Operations(body).OfType<IrAddressElementOperation>());
        Assert.True(Value(body, address.ResultValue).IsReference);
        Assert.Empty(Operations(body).OfType<IrLoadElementOperation>());
        Assert.Contains(Operations(body).OfType<IrLoadFieldOperation>(), load => load.Field.Name == "Count" && load.ReceiverValue == address.ResultValue);
    }

    [Fact]
    public async Task Field_access_through_a_ref_local_is_on_the_reference()
    {
        var body = await Lower("void M() { ref var s = ref _state; s.Count++; }");

        var store = Assert.Single(Operations(body).OfType<IrStoreFieldOperation>());
        Assert.True(Value(body, store.ReceiverValue!.Value).IsReference);
        Assert.Empty(Operations(body).OfType<IrLoadReferenceOperation>());
    }

    [Fact]
    public async Task Field_access_through_a_ref_parameter_is_on_the_reference()
    {
        var body = await Lower("static void M(ref State s) { s.Count++; }");

        var store = Assert.Single(Operations(body).OfType<IrStoreFieldOperation>());
        Assert.Equal(body.Parameters.Single().Value, store.ReceiverValue);
        Assert.True(Value(body, body.Parameters.Single().Value).IsReference);
        Assert.Empty(Operations(body).OfType<IrLoadReferenceOperation>());
    }

    [Fact]
    public async Task Field_read_through_an_in_parameter_is_on_the_reference()
    {
        var body = await Lower("static int M(in State s) => s.Count;");

        var load = Assert.Single(Operations(body).OfType<IrLoadFieldOperation>());
        Assert.Equal(body.Parameters.Single().Value, load.ReceiverValue);
        Assert.Empty(Operations(body).OfType<IrLoadReferenceOperation>());
    }

    [Fact]
    public async Task Field_access_through_a_ref_return_is_on_the_returned_reference()
    {
        var body = await Lower("ref State Get() => ref _state; void M() { Get().Count++; }");

        var call = Assert.Single(Operations(body).OfType<IrCallOperation>());
        Assert.True(Value(body, call.ResultValue!.Value).IsReference);
        Assert.Equal(call.ResultValue, Assert.Single(Operations(body).OfType<IrStoreFieldOperation>()).ReceiverValue);
        Assert.Empty(Operations(body).OfType<IrLoadReferenceOperation>());
    }

    [Fact]
    public async Task Member_call_on_a_struct_field_is_on_its_address()
    {
        var body = await Lower("void M() { _state.Increment(); }");

        var address = Assert.Single(Operations(body).OfType<IrAddressFieldOperation>());
        Assert.Equal(address.ResultValue, Assert.Single(Operations(body).OfType<IrCallOperation>()).ReceiverValue);
        Assert.Empty(Operations(body).OfType<IrLoadFieldOperation>());
    }

    [Fact]
    public async Task Property_accessors_on_a_struct_field_are_on_its_address()
    {
        var body = await Lower("int M() { _state.Twice = 4; return _state.Twice; }");

        var addresses = Operations(body).OfType<IrAddressFieldOperation>().Select(address => address.ResultValue).ToHashSet();
        var calls = Operations(body).OfType<IrCallOperation>().ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Contains(call.ReceiverValue!.Value, addresses));
    }

    [Fact]
    public async Task Event_accessor_on_a_struct_field_is_on_its_address()
    {
        var body = await Lower("void M(System.Action handler) { _state.Changed += handler; }");

        var address = Assert.Single(Operations(body).OfType<IrAddressFieldOperation>());
        var accessor = Assert.Single(Operations(body).OfType<IrCallOperation>());
        Assert.Contains("add_Changed", accessor.Method, StringComparison.Ordinal);
        Assert.Equal(address.ResultValue, accessor.ReceiverValue);
    }

    [Fact]
    public async Task Method_group_of_a_struct_field_boxes_a_copy()
    {
        var body = await Lower("System.Action M() => _state.Increment;");

        var copy = Assert.Single(Operations(body).OfType<IrLoadFieldOperation>());
        Assert.Equal("_state", copy.Field.Name);
        var box = Assert.Single(Operations(body).OfType<IrBoxOperation>());
        Assert.Equal(copy.ResultValue, box.OperandValue);
        Assert.Equal("Fixture:State", box.BoxedTypeKey);
        Assert.Equal(box.ResultValue, Assert.Single(Operations(body).OfType<IrCreateDelegateOperation>()).ReceiverValue);
        Assert.Empty(Operations(body).OfType<IrAddressFieldOperation>());
    }

    [Fact]
    public async Task Member_call_on_read_only_storage_takes_a_defensive_copy_unless_the_member_is_readonly()
    {
        var copied = await Lower("void M() { _ro.Increment(); }");
        var load = Assert.Single(Operations(copied).OfType<IrLoadFieldOperation>());
        Assert.Equal(load.ResultValue, Assert.Single(Operations(copied).OfType<IrCallOperation>()).ReceiverValue);
        Assert.Empty(Operations(copied).OfType<IrAddressFieldOperation>());

        var direct = await Lower("int M() => _ro.Peek();");
        var address = Assert.Single(Operations(direct).OfType<IrAddressFieldOperation>());
        Assert.Equal(address.ResultValue, Assert.Single(Operations(direct).OfType<IrCallOperation>()).ReceiverValue);

        // In its own constructor a readonly field is written in place, so no copy is taken there either.
        var constructing = await Lower("public C() { _ro.Increment(); }", ".ctor");
        Assert.Contains(Operations(constructing).OfType<IrAddressFieldOperation>(), field => field.Field.Name == "_ro");
    }

    [Fact]
    public async Task New_and_default_of_a_struct_make_a_value_and_no_allocation_region()
    {
        var body = await Lower("void M() { var s = new State(); State d = default; s.Count = d.Count; }");

        Assert.Empty(Operations(body).OfType<IrUnknownOperation>());
        Assert.Contains(body.Values, value => value is { Kind: IrValueKind.Constant, Name: "default", StructTypeKey: "Fixture:State" });
        Assert.Empty(Operations(body).OfType<IrAddressFieldOperation>());

        var run = Solve(Usings + STATE + """
            public sealed class Holder { public State Kept; }
            public class StateController : ControllerBase
            {
                public void Post([FromServices] Holder holder) { var s = new State(); State d = default; s.Count = 1; holder.Kept = s; holder.Kept = d; }
            }
            """ + Startup("services.AddSingleton<Holder>();"));
        Assert.DoesNotContain(run.Heap.Regions.Values, region => region.Kind == HeapRegionKind.Allocation && region.TypeKey == "Fixture:State");
        Assert.Contains(run.Heap.Regions.Values, region => region.Kind == HeapRegionKind.StructCopy && region.TypeKey == "Fixture:State");
    }

    [Fact]
    public async Task A_struct_held_by_value_is_its_value_wherever_an_operation_reaches_through_it()
    {
        var body = await Lower("void M(State p) { var local = _state; local.Count++; p.Increment(); }");

        // Copying the field reads it whole; the local and the by-value parameter are their own copies.
        Assert.Contains(Operations(body).OfType<IrLoadFieldOperation>(), load => load.Field.Name == "_state");
        Assert.Empty(Operations(body).OfType<IrAddressFieldOperation>());
        Assert.All(Operations(body).OfType<IrStoreFieldOperation>(), store => Assert.False(Value(body, store.ReceiverValue!.Value).IsReference));
        Assert.Equal(body.Parameters.Single().Value, Assert.Single(Operations(body).OfType<IrCallOperation>()).ReceiverValue);
    }

    [Theory]
    [InlineData("C GetHolder() => this; int GetCount() => 1; void M() { GetHolder()._state = new State { Count = GetCount() }; }",
                new[] { "GetHolder", "GetCount" })]
    [InlineData("State[] GetStates() => _states; int GetIndex() => 0; int GetCount() => 1; " +
                "void M() { GetStates()[GetIndex()] = new State { Count = GetCount() }; }",
                new[] { "GetStates", "GetIndex", "GetCount" })]
    [InlineData("ref State GetRef() => ref _state; int GetCount() => 1; void M() { GetRef() = new State { Count = GetCount() }; }",
                new[] { "GetRef", "GetCount" })]
    [InlineData("C GetHolder() => this; int GetCount() => 1; State Kept { get => _state; set => _state = value; } " +
                "void M() { GetHolder().Kept = new State { Count = GetCount() }; }",
                new[] { "GetHolder", "GetCount", "set_Kept" })]
    [InlineData("int GetCount() => 1; void M() { s_state = new State { Count = GetCount() }; }", new[] { "GetCount" })]
    public async Task A_struct_stored_whole_evaluates_its_target_before_the_value_and_reads_nothing(string members, string[] calls)
    {
        // C# evaluates an assignment's target, its receiver and its indices, before the value, and an object initializer is part of the
        // value; the store writes the storage whole without reading it first (R2).
        var body = await Lower(members);

        var called = Operations(body).OfType<IrCallOperation>().Select(call => call.Method)
                                     .Select(method => calls.FirstOrDefault(name => method.Contains($".{name}(", StringComparison.Ordinal)))
                                     .OfType<string>().ToArray();
        Assert.Equal(calls, called);
        Assert.DoesNotContain(Operations(body).OfType<IrLoadFieldOperation>(), load => load.Field.Name is "_state" or "s_state");
        Assert.Empty(Operations(body).OfType<IrLoadElementOperation>());
        Assert.Empty(Operations(body).OfType<IrLoadReferenceOperation>());
    }

    [Theory]
    [InlineData("Steps.Target(box, holder).Kept = new State { Count = Steps.Start(box) };", false)]
    [InlineData("var count = Steps.Start(box); Steps.Target(box, holder).Kept = new State { Count = count };", true)]
    public void The_target_of_a_whole_struct_store_runs_before_the_work_its_value_starts(string store, bool pairs)
    {
        // The target's receiver writes the box before the initializer starts the work on it, which follows that write; started first,
        // the work meets it (R2, R5).
        var run = Analyze(Usings + STATE + $$"""
            public sealed class Box { public int N; }
            public sealed class Holder { public State Kept; }
            public static class Steps
            {
                public static Holder Target(Box box, Holder holder) { box.N = 1; return holder; }
                public static int Start(Box box) { Task.Run(() => { box.N++; }); return 1; }
            }
            public class StateController : ControllerBase
            {
                public void Post([FromServices] Holder holder)
                {
                    var box = new Box();
                    {{store}}
                }
            }
            """ + Startup("services.AddSingleton<Holder>();"));

        Assert.NotEmpty(run.Accesses("N"));
        Assert.Equal(pairs, run.PairsOn("N").Count != 0);
    }

    [Fact]
    public async Task A_struct_of_a_library_is_no_struct_the_analysis_follows()
    {
        var body = await Lower("System.Collections.Generic.KeyValuePair<int, string> _pair; int M() => _pair.Key;");

        Assert.All(body.Values, value => Assert.Null(value.StructTypeKey));
        Assert.Empty(Operations(body).OfType<IrAddressFieldOperation>());
    }

    private static IEnumerable<IrOperation> Operations(IrBody body) => body.Blocks.SelectMany(block => block.Operations);

    private static IrValue Value(IrBody body, int id) => body.Values.Single(value => value.Id == id);

    private static async Task<IrBody> Lower(string members, string name = "M")
    {
        var solution = FixtureSolution.Create(("Case.cs", $$"""
            {{STATE}}
            public class C
            {
            {{FIELDS}}
                {{members}}
            }
            """));
        var compilation = await solution.Projects.Single().GetCompilationAsync() ?? throw new InvalidOperationException();
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var type = compilation.GetTypeByMetadataName("C") ?? throw new InvalidOperationException("Type C was not found.");
        var method = type.GetMembers(name).OfType<IMethodSymbol>().Single(method => !method.IsImplicitlyDeclared);
        return IrLowering.Lower(method, compilation, @"C:\fixture", CancellationToken.None).Body;
    }
}
