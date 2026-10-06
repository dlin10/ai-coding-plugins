using ConcurrencyHunter.Analysis;
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

/// <summary>Events in the engine (R1, R2, ADR 0014): a subscription or an unsubscription is a call of the event's add or remove
/// accessor, routed as any call of it is; a field-like event is the delegate field the compiler declares for it, its accessors have
/// the compiler's bodies over that field, and inside its type it is read, written and named by <c>ref</c> as that field. The timer's
/// <c>Elapsed</c> stays its recognizer's, and no event form leaves an unknown operation.</summary>
public sealed class EventLoweringTests
{
    private const string LIBRARY = """
        using System;
        namespace Lib;
        public sealed class Source
        {
            public event Action Changed;
            public static event Action Shared;
            public void Raise() => Changed?.Invoke();
            public static void RaiseShared() => Shared?.Invoke();
        }
        """;

    [Fact]
    public void Library_event_subscription_prints_a_call_of_its_add_accessor_with_the_handler()
    {
        var body = Lower("using System; class C { void M(Lib.Source s, Action h) { s.Changed += h; } }", "C", "M", Library());

        var call = Assert.Single(Operations<IrCallOperation>(body));
        Assert.Equal((IrCallKind.Instance, "body:Lib:M:Lib.Source.add_Changed(System.Action)"), (call.CallKind, call.TargetMethodId));
        Assert.Equal(Parameter(body, "s"), call.ReceiverValue);
        Assert.Equal([Parameter(body, "h")], call.ArgumentValues);
        Assert.Contains($"call Instance result=- method=\"{call.Method}\" receiver=%{call.ReceiverValue} arguments=[%{Parameter(body, "h")}]",
                        IrPrinter.Print(body));
        Assert.Contains("add_Changed", call.Method);
        Assert.Empty(Operations<IrUnknownOperation>(body));
    }

    [Fact]
    public void Library_event_unsubscription_prints_a_call_of_its_remove_accessor_with_the_handler()
    {
        var body = Lower("using System; class C { void M(Lib.Source s, Action h) { s.Changed -= h; } }", "C", "M", Library());

        var call = Assert.Single(Operations<IrCallOperation>(body));
        Assert.Equal((IrCallKind.Instance, "body:Lib:M:Lib.Source.remove_Changed(System.Action)"), (call.CallKind, call.TargetMethodId));
        Assert.Equal(Parameter(body, "s"), call.ReceiverValue);
        Assert.Equal([Parameter(body, "h")], call.ArgumentValues);
        Assert.Contains($"call Instance result=- method=\"{call.Method}\" receiver=%{call.ReceiverValue} arguments=[%{Parameter(body, "h")}]",
                        IrPrinter.Print(body));
        Assert.Contains("remove_Changed", call.Method);
        Assert.Empty(Operations<IrUnknownOperation>(body));
    }

    [Fact]
    public void Static_library_event_subscription_is_a_static_call_without_a_receiver()
    {
        var body = Lower("using System; class C { void M(Action h) { Lib.Source.Shared += h; Lib.Source.Shared -= h; } }", "C", "M", Library());

        var calls = Operations<IrCallOperation>(body);
        Assert.Equal(["body:Lib:M:Lib.Source.add_Shared(System.Action)", "body:Lib:M:Lib.Source.remove_Shared(System.Action)"],
                     calls.Select(call => call.TargetMethodId));
        Assert.All(calls, call => Assert.Equal((IrCallKind.Static, (int?)null), (call.CallKind, call.ReceiverValue)));
        Assert.All(calls, call => Assert.Equal([Parameter(body, "h")], call.ArgumentValues));
        Assert.Empty(Operations<IrUnknownOperation>(body));
    }

    [Fact]
    public void Source_event_with_accessors_of_its_own_is_subscribed_by_calling_its_add_accessor()
    {
        var body = Lower("""
            using System;
            class Source { private Action _handlers; public event Action Changed { add { _handlers += value; } remove { _handlers -= value; } } }
            class C { void M(Source s, Action h) { s.Changed += h; s.Changed -= h; } }
            """, "C", "M");

        var calls = Operations<IrCallOperation>(body);
        Assert.Equal(["body:Fixture:M:Source.add_Changed(System.Action)", "body:Fixture:M:Source.remove_Changed(System.Action)"],
                     calls.Select(call => call.TargetMethodId));
        Assert.All(calls, call => Assert.Equal((IrCallKind.Instance, (int?)Parameter(body, "s")), (call.CallKind, call.ReceiverValue)));
        Assert.Empty(Operations<IrUnknownOperation>(body));
        Assert.Empty(Operations<IrStoreFieldOperation>(body));
    }

    [Fact]
    public void Base_event_subscription_is_an_instance_call_of_the_base_accessor()
    {
        var body = Lower("""
            using System;
            class B { public virtual event Action E { add { } remove { } } }
            class C : B { public override event Action E { add { } remove { } } void M(Action h) { base.E += h; } }
            """, "C", "M");

        var call = Assert.Single(Operations<IrCallOperation>(body));
        Assert.Equal((IrCallKind.Instance, "body:Fixture:M:B.add_E(System.Action)"), (call.CallKind, call.TargetMethodId));
        Assert.Equal(Receiver(body), call.ReceiverValue);
    }

    [Fact]
    public void Virtual_event_subscription_on_this_is_a_virtual_call()
    {
        var body = Lower("""
            using System;
            class C { public virtual event Action E { add { } remove { } } void M(Action h) { E += h; } }
            """, "C", "M");

        var call = Assert.Single(Operations<IrCallOperation>(body));
        Assert.Equal((IrCallKind.Virtual, "body:Fixture:M:C.add_E(System.Action)"), (call.CallKind, call.TargetMethodId));
        Assert.Equal(Receiver(body), call.ReceiverValue);
    }

    [Fact]
    public void Interface_event_subscription_is_an_interface_call_of_its_accessor()
    {
        var body = Lower("""
            using System;
            interface I { event Action E; }
            class C { void M(I source, Action h) { source.E += h; source.E -= h; } }
            """, "C", "M");

        var calls = Operations<IrCallOperation>(body);
        Assert.Equal(["body:Fixture:M:I.add_E(System.Action)", "body:Fixture:M:I.remove_E(System.Action)"], calls.Select(call => call.TargetMethodId));
        Assert.All(calls, call => Assert.Equal(IrCallKind.Interface, call.CallKind));
        Assert.Empty(Operations<IrLoadFieldOperation>(body));
    }

    [Fact]
    public void Explicit_interface_event_implementation_is_reached_through_the_interface()
    {
        const string SOURCE = """
            using System;
            public interface I { event Action E; }
            public class Impl : I { private Action _h; event Action I.E { add { _h += value; } remove { _h -= value; } } }
            public class C { void M(Impl impl, Action h) { ((I)impl).E += h; } }
            """;
        var body = Lower(SOURCE, "C", "M");

        var call = Assert.Single(Operations<IrCallOperation>(body));
        Assert.Equal((IrCallKind.Interface, "body:Fixture:M:I.add_E(System.Action)"), (call.CallKind, call.TargetMethodId));
        var index = ProgramIndexBuilder.Build("scope:Fixture", [Compile(SOURCE)], ROOT_DIRECTORY, CancellationToken.None);
        var implementation = index.Implementation("Fixture:Impl", call.TargetMethodId!)!;
        Assert.Equal("body:Fixture:M:Impl.I#add_E(System.Action)", implementation.MethodId);
        Assert.True(implementation.HasSourceBody);
    }

    [Fact]
    public void Field_like_event_add_body_is_a_load_a_combination_a_store_and_a_compare_and_swap_on_its_storage()
    {
        var (body, compilation) = LowerWithCompilation("using System; class C { public event Action E; }", "C", "add_E");

        var operations = Operations(body);
        var storage = FieldLikeEvents.FieldRef(Event(compilation, "C", "E"));
        var load = Assert.Single(operations.OfType<IrLoadFieldOperation>());
        var combine = Assert.Single(operations.OfType<IrCombineDelegatesOperation>());
        var store = Assert.Single(operations.OfType<IrStoreFieldOperation>());
        var atomic = Assert.Single(operations.OfType<IrAtomicOperation>());
        Assert.Equal((storage, (int?)Receiver(body)), (load.Field, load.ReceiverValue));
        Assert.False(combine.Removes);
        Assert.Equal([load.ResultValue, Parameter(body, "value")], combine.OperandValues);
        Assert.Equal((storage, (int?)Receiver(body), combine.ResultValue, (int?)load.Id), (store.Field, store.ReceiverValue, store.Value, store.ReadModifyWriteOf));
        Assert.Equal((IrAtomicEffect.CompareAndSwap, (int?)store.Id, (int?)load.ResultValue), (atomic.Effect, atomic.TargetOperationId, atomic.ComparandValue));
        Assert.Equal([load, combine, store, atomic], operations.Where(operation => operation is not IrReturnOperation));
    }

    [Fact]
    public void Field_like_event_remove_body_removes_the_handler_from_its_storage_with_a_compare_and_swap()
    {
        var (body, compilation) = LowerWithCompilation("using System; class C { public static event Action E; }", "C", "remove_E");

        var storage = FieldLikeEvents.FieldRef(Event(compilation, "C", "E"));
        Assert.True(storage.IsStatic);
        var load = Assert.Single(Operations<IrLoadFieldOperation>(body));
        var remove = Assert.Single(Operations<IrCombineDelegatesOperation>(body));
        var store = Assert.Single(Operations<IrStoreFieldOperation>(body));
        var atomic = Assert.Single(Operations<IrAtomicOperation>(body));
        Assert.Equal((storage, (int?)null), (load.Field, load.ReceiverValue));
        Assert.True(remove.Removes);
        Assert.Equal([load.ResultValue, Parameter(body, "value")], remove.OperandValues);
        Assert.Equal((storage, (int?)null, remove.ResultValue), (store.Field, store.ReceiverValue, store.Value));
        Assert.Equal((IrAtomicEffect.CompareAndSwap, (int?)store.Id, (int?)load.ResultValue), (atomic.Effect, atomic.TargetOperationId, atomic.ComparandValue));
    }

    [Fact]
    public void Field_like_event_subscription_writes_its_storage_as_an_atomic_read_modify_write()
    {
        var run = Analyze("""
            public sealed class Hub { public event Action Changed; }
            public class HubController(Hub hub) : ControllerBase
            {
                private readonly Hub _hub = hub;
                public void Subscribe() => _hub.Changed += () => { };
                public void Unsubscribe() => _hub.Changed -= () => { };
            }
            """ + Startup("services.AddSingleton<Hub>();"));

        var accesses = run.Of("Changed");
        Assert.Equal(2, accesses.Count(access => access.Operation == AccessOperation.AtomicReadModifyWrite));
        Assert.DoesNotContain(accesses, access => access.Operation is AccessOperation.Write or AccessOperation.ReadModifyWrite);
    }

    [Fact]
    public void Conditional_invoke_of_a_field_like_event_loads_its_storage_and_calls_the_delegate()
    {
        var (body, compilation) = LowerWithCompilation("using System; class C { public event Action E; void M() { E?.Invoke(); } }", "C", "M");

        var load = Assert.Single(Operations<IrLoadFieldOperation>(body));
        Assert.Equal((FieldLikeEvents.FieldRef(Event(compilation, "C", "E")), (int?)Receiver(body)), (load.Field, load.ReceiverValue));
        var call = Assert.Single(Operations<IrCallOperation>(body));
        Assert.Equal(IrCallKind.Delegate, call.CallKind);
        Assert.Empty(Operations<IrUnknownOperation>(body));
    }

    [Fact]
    public void Raising_a_field_like_event_directly_loads_its_storage_and_calls_the_delegate()
    {
        var (body, compilation) = LowerWithCompilation("using System; class C { public static event Action<int> E; static void M() { E(1); } }", "C", "M");

        var load = Assert.Single(Operations<IrLoadFieldOperation>(body));
        Assert.Equal(FieldLikeEvents.FieldRef(Event(compilation, "C", "E")), load.Field);
        var call = Assert.Single(Operations<IrCallOperation>(body));
        Assert.Equal((IrCallKind.Delegate, (int?)load.ResultValue), (call.CallKind, call.ReceiverValue));
        Assert.Empty(Operations<IrUnknownOperation>(body));
    }

    [Fact]
    public void Assigning_null_to_a_field_like_event_stores_into_its_storage()
    {
        var (body, compilation) = LowerWithCompilation("using System; class C { public event Action E; void M() { E = null; } }", "C", "M");

        var store = Assert.Single(Operations<IrStoreFieldOperation>(body));
        Assert.Equal((FieldLikeEvents.FieldRef(Event(compilation, "C", "E")), (int?)Receiver(body), (int?)null),
                     (store.Field, store.ReceiverValue, store.ReadModifyWriteOf));
        Assert.Empty(Operations<IrCallOperation>(body));
        Assert.Empty(Operations<IrUnknownOperation>(body));
    }

    [Fact]
    public void Copying_a_field_like_event_into_a_local_loads_its_storage()
    {
        var (body, compilation) = LowerWithCompilation("using System; class C { public event Action E; Action M() { var h = E; return h; } }", "C", "M");

        var load = Assert.Single(Operations<IrLoadFieldOperation>(body));
        Assert.Equal(FieldLikeEvents.FieldRef(Event(compilation, "C", "E")), load.Field);
        Assert.Contains(Operations<IrAssignOperation>(body), assign => assign.SourceValue == load.ResultValue);
        Assert.Empty(Operations<IrUnknownOperation>(body));
    }

    [Fact]
    public void Interlocked_compare_exchange_on_a_ref_field_like_event_is_a_compare_and_swap_on_its_storage()
    {
        var (body, compilation) = LowerWithCompilation("""
            using System; using System.Threading;
            class C { public event Action E; void M(Action h) { Interlocked.CompareExchange(ref E, h, null); } }
            """, "C", "M");

        var store = Assert.Single(Operations<IrStoreFieldOperation>(body));
        Assert.Equal((FieldLikeEvents.FieldRef(Event(compilation, "C", "E")), Parameter(body, "h")), (store.Field, store.Value));
        var atomic = Assert.Single(Operations<IrAtomicOperation>(body));
        Assert.Equal((IrAtomicEffect.CompareAndSwap, (int?)store.Id), (atomic.Effect, atomic.TargetOperationId));
        Assert.NotNull(atomic.ComparandValue);
        Assert.Empty(Operations<IrCallOperation>(body));
        Assert.Empty(Operations<IrUnknownOperation>(body));
    }

    [Fact]
    public void Interlocked_exchange_on_a_ref_field_like_event_is_an_atomic_store_into_its_storage()
    {
        var (body, compilation) = LowerWithCompilation("""
            using System; using System.Threading;
            class C { public static event Action E; static Action M() => Interlocked.Exchange(ref E, null); }
            """, "C", "M");

        var store = Assert.Single(Operations<IrStoreFieldOperation>(body));
        Assert.Equal((FieldLikeEvents.FieldRef(Event(compilation, "C", "E")), (int?)null), (store.Field, store.ReceiverValue));
        var atomic = Assert.Single(Operations<IrAtomicOperation>(body));
        Assert.Equal(store.Id, atomic.TargetOperationId);
        Assert.NotNull(atomic.ResultValue);
        Assert.Empty(Operations<IrCallOperation>(body));
        Assert.Empty(Operations<IrUnknownOperation>(body));
    }

    [Fact]
    public void Instance_field_like_event_initializer_stores_into_its_storage_in_the_constructor()
    {
        var (body, compilation) = LowerWithCompilation("using System; class C { public event Action E = () => { }; }", "C", ".ctor");

        var create = Assert.Single(Operations<IrCreateDelegateOperation>(body));
        var store = Assert.Single(Operations<IrStoreFieldOperation>(body));
        Assert.Equal((FieldLikeEvents.FieldRef(Event(compilation, "C", "E")), (int?)Receiver(body)), (store.Field, store.ReceiverValue));
        Assert.Equal(create.ResultValue, Origin(body, store.Value));
        Assert.Empty(Operations<IrUnknownOperation>(body));
    }

    [Fact]
    public void Static_field_like_event_initializer_stores_into_its_storage_in_the_type_initializer()
    {
        var (body, compilation) = LowerWithCompilation("using System; static class C { public static event Action E = () => { }; }", "C", ".cctor");

        var create = Assert.Single(Operations<IrCreateDelegateOperation>(body));
        var store = Assert.Single(Operations<IrStoreFieldOperation>(body));
        Assert.Equal((FieldLikeEvents.FieldRef(Event(compilation, "C", "E")), (int?)null), (store.Field, store.ReceiverValue));
        Assert.Equal(create.ResultValue, Origin(body, store.Value));
    }

    [Fact]
    public void Field_ref_of_a_field_like_event_equals_the_field_ref_of_its_initializers_backing_field()
    {
        var compilation = Compile("""
            using System;
            public struct S { public event Action E = () => { }; public S() { } }
            public class G<T> { public static event Func<T> E = () => default; }
            """);

        foreach (var declarator in compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            var model = compilation.GetSemanticModel(declarator.SyntaxTree);
            var initializer = Assert.IsAssignableFrom<IFieldInitializerOperation>(model.GetOperation(declarator.Initializer!));
            var @event = Assert.IsAssignableFrom<IEventSymbol>(model.GetDeclaredSymbol(declarator));
            Assert.True(FieldLikeEvents.Is(@event));
            Assert.Equal(IrLowering.FieldRef(Assert.Single(initializer.InitializedFields)), FieldLikeEvents.FieldRef(@event));
        }
    }

    [Fact]
    public void Timer_elapsed_subscription_is_one_timer_operation_and_no_unknown_or_accessor_call()
    {
        var body = Lower("""
            using System;
            class C { void M() { var timer = new System.Timers.Timer(1000); timer.Elapsed += (sender, e) => { }; } }
            """, "C", "M");

        var subscription = Assert.Single(Operations<IrTimerOperation>(body));
        Assert.Equal(IrTimerAction.ElapsedSubscribe, subscription.Action);
        Assert.NotNull(subscription.CallbackValue);
        Assert.DoesNotContain(Operations<IrCallOperation>(body), call => call.Method.Contains("Elapsed", StringComparison.Ordinal));
        Assert.Empty(Operations<IrUnknownOperation>(body));
    }

    [Fact]
    public void Timer_elapsed_unsubscription_records_nothing()
    {
        var body = Lower("""
            using System;
            class C { void M(System.Timers.ElapsedEventHandler h) { var timer = new System.Timers.Timer(1000); timer.Elapsed -= h; } }
            """, "C", "M");

        Assert.Empty(Operations<IrTimerOperation>(body));
        Assert.DoesNotContain(Operations<IrCallOperation>(body), call => call.Method.Contains("Elapsed", StringComparison.Ordinal));
        Assert.Empty(Operations<IrUnknownOperation>(body));
    }

    private static IrBody Lower(string source, string type, string member, MetadataReference? library = null) =>
        LowerWithCompilation(source, type, member, library).Body;

    private static (IrBody Body, Compilation Compilation) LowerWithCompilation(string source, string type, string member,
                                                                               MetadataReference? library = null)
    {
        var compilation = Compile(source, library);
        var method = (compilation.GetTypeByMetadataName(type) ?? throw new InvalidOperationException($"Type {type} was not found."))
                     .GetMembers(member).OfType<IMethodSymbol>().Single();
        return (IrLowering.Lower(method, compilation, ROOT_DIRECTORY, CancellationToken.None).Body, compilation);
    }

    private static Compilation Compile(string source, MetadataReference? library = null)
    {
        var options = new FixtureOptions { MetadataReferences = library is null ? [] : [library] };
        var compilation = FixtureSolution.Create(options, ("Case.cs", source)).Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return compilation;
    }

    /// <summary>The library whose events have no accessor bodies in the run.</summary>
    private static MetadataReference Library()
    {
        var compilation = CSharpCompilation.Create("Lib", [CSharpSyntaxTree.ParseText(LIBRARY)], StubAssemblies.PlatformWithout([]),
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        var emit = compilation.Emit(bytes);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return MetadataReference.CreateFromImage(bytes.ToArray());
    }

    private static IEventSymbol Event(Compilation compilation, string type, string name) =>
        compilation.GetTypeByMetadataName(type)!.GetMembers(name).OfType<IEventSymbol>().Single();

    private static int Parameter(IrBody body, string name) => body.Parameters.Single(parameter => parameter.Name == name).Value;

    private static int Receiver(IrBody body) => body.Values.Single(value => value.Kind == IrValueKind.Receiver).Id;

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

    private static IrOperation[] Operations(IrBody body) => body.Blocks.SelectMany(block => block.Operations).ToArray();

    private static T[] Operations<T>(IrBody body) where T : IrOperation => Operations(body).OfType<T>().ToArray();
}
