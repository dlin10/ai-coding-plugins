using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The witness bodies the driver writes for user types.</summary>
public sealed class WitnessTests
{
    private const string SOURCE = """
        using System;
        using System.Threading.Tasks;

        namespace Lib
        {
            public interface IUser
            {
                void Put(object value);
                void M<T>(T value);
                event Action Changed;
            }

            public sealed class Hidden { private Hidden() { } }
            public sealed class Ready { public Ready(string value) { } }
            public class Unbuildable { public Unbuildable(Hidden value) { } }

            public abstract class Base
            {
                public Unbuildable UnbuildableField;
                public virtual void Put(object value) { }
                public virtual void ByRef(ref object value) { }
                public virtual ref object RefValue() => throw null;
                public virtual Task<string> Text() => Task.FromResult("");
                public virtual Ready Address() => null;
                public virtual Span<int> SpanParameter(Span<int> value) => value;
                public virtual unsafe void Pointer(int* value) { }
                public virtual Span<int> SpanResult() => default;
                public virtual Hidden HiddenResult() => null;
                public virtual Unbuildable UnbuildableResult() => null;
                public virtual T GenericResult<T>() => default;
                public virtual void Ping() { }
            }

            public class Target
            {
                public virtual void Run(Action action) => action();
                public virtual void Other(object value) { }
            }

            public static class Api
            {
                public static void Use(Base value, Action done) { value.Ping(); done(); }
                public static void UseInterface(IUser value, Action done) { }
                public static void UseSub(Target value, Action done) { }
            }
        }
        """;

    private static readonly Lazy<Driver> BaseDriver = new(() => Drive("M:Lib.Api.Use(Lib.Base,System.Action)"));
    private static readonly Lazy<Driver> InterfaceDriver = new(() => Drive("M:Lib.Api.UseInterface(Lib.IUser,System.Action)"));

    [Fact]
    public void A_probe_class_override_stores_this_and_its_parameter_as_objects()
    {
        var source = Body(Type(BaseDriver.Value, "Probe_value"), "Put");

        Assert.Contains("Witnessed.W", source);
        Assert.Contains("= this;", source);
        Assert.Contains("= p0;", source);
    }

    [Fact]
    public void A_probes_ToString_stores_this()
    {
        Assert.Contains("= this;", Body(Type(BaseDriver.Value, "Probe_value"), "ToString"));
    }

    [Fact]
    public void A_probes_Equals_stores_this()
    {
        Assert.Contains("= this;", Body(Type(BaseDriver.Value, "Probe_value"), "Equals"));
    }

    [Fact]
    public void A_probes_GetHashCode_stores_this()
    {
        Assert.Contains("= this;", Body(Type(BaseDriver.Value, "Probe_value"), "GetHashCode"));
    }

    [Fact]
    public void A_parameterless_seed_member_stores_this()
    {
        Assert.Contains("= this;", Body(Type(BaseDriver.Value, "Probe_value"), "Ping"));
    }

    [Fact]
    public void A_receiver_subclass_override_is_a_witness_and_the_member_itself_is_not_overridden()
    {
        var driver = Drive("M:Lib.Target.Run(System.Action)");
        var receiver = Type(driver, "Sub_Recv");

        Assert.Contains("= this;", Body(receiver, "Other"));
        Assert.DoesNotContain(receiver.GetMembers(), member => member.Name == "Run");
    }

    [Fact]
    public void An_interface_stub_member_is_a_witness()
    {
        Assert.Contains("= p0;", Body(Type(InterfaceDriver.Value, "Probe_value"), "Lib.IUser.Put"));
    }

    [Fact]
    public void A_generic_interface_method_stores_its_value_as_object_and_compiles()
    {
        var method = Type(InterfaceDriver.Value, "Probe_value").GetMembers().OfType<IMethodSymbol>()
                    .Single(candidate => candidate.Name.EndsWith("M", StringComparison.Ordinal));

        Assert.True(method.IsGenericMethod);
        Assert.False(method.IsExtern);
        Assert.Contains("= p0;", Body(method));
    }

    [Fact]
    public void A_driver_events_add_accessor_stores_its_delegate()
    {
        Assert.Contains("= value;", EventAccessor(InterfaceDriver.Value, MethodKind.EventAdd));
    }

    [Fact]
    public void A_driver_events_remove_accessor_stores_its_delegate()
    {
        Assert.Contains("= value;", EventAccessor(InterfaceDriver.Value, MethodKind.EventRemove));
    }

    [Fact]
    public void A_ref_parameters_value_is_stored()
    {
        Assert.Contains("= p0;", Body(Type(BaseDriver.Value, "Probe_value"), "ByRef"));
    }

    [Fact]
    public void A_ref_return_uses_a_WitnessedRef_field()
    {
        Assert.Contains("return ref WitnessedRef<global::System.Object>.Value;", Body(Type(BaseDriver.Value, "Probe_value"), "RefValue"));
    }

    [Fact]
    public void A_Task_of_string_return_is_completed_with_a_string()
    {
        Assert.Contains("Task.FromResult<global::System.String>(\"s\")", Body(Type(BaseDriver.Value, "Probe_value"), "Text"));
    }

    [Fact]
    public void A_sealed_library_class_return_uses_the_recipe()
    {
        Assert.Contains("new global::Lib.Ready(\"s\")", Body(Type(BaseDriver.Value, "Probe_value"), "Address"));
    }

    [Fact]
    public void A_member_with_a_ref_like_parameter_stays_extern()
    {
        Assert.True(Method(Type(BaseDriver.Value, "Probe_value"), "SpanParameter").IsExtern);
    }

    [Fact]
    public void A_member_with_a_pointer_parameter_stays_extern()
    {
        Assert.True(Method(Type(BaseDriver.Value, "Probe_value"), "Pointer").IsExtern);
    }

    [Fact]
    public void A_member_with_a_ref_like_return_stays_extern()
    {
        Assert.True(Method(Type(BaseDriver.Value, "Probe_value"), "SpanResult").IsExtern);
    }

    [Fact]
    public void A_member_returning_a_class_no_seed_can_derive_from_stays_extern()
    {
        Assert.True(Method(Type(BaseDriver.Value, "Probe_value"), "HiddenResult").IsExtern);
    }

    [Fact]
    public void A_member_returning_a_seed_whose_constructor_cannot_be_built_stays_extern()
    {
        Assert.True(Method(Type(BaseDriver.Value, "Probe_value"), "UnbuildableResult").IsExtern);
    }

    [Fact]
    public void An_unbuildable_seed_is_left_unseeded_without_refusing_the_driver()
    {
        Assert.Contains(BaseDriver.Value.Unseeded, path => path.Contains("UnbuildableField", StringComparison.Ordinal));
    }

    [Fact]
    public void A_member_returning_a_method_type_parameter_stays_extern()
    {
        Assert.True(Method(Type(BaseDriver.Value, "Probe_value"), "GenericResult").IsExtern);
    }

    [Fact]
    public void A_Sub_argument_object_has_the_probe_object_role()
    {
        var trace = TraceOf(SOURCE, "M:Lib.Api.UseSub(Lib.Target,System.Action)");
        var reachability = new HeapReachability(trace.Run!.Heap!);
        var executions = new DriverExecutions(trace.Driver!, trace.Run.Executions!, new HashSet<string>(StringComparer.Ordinal));
        var allocations = new Allocations(trace.Driver!, trace.Run.Heap!, trace.Run.Executions!, executions, reachability);

        Assert.Equal(DriverRole.ProbeObject, allocations.Of(Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_value_Call"))).Role);
    }

    [Fact]
    public void A_witness_receipt_widens_without_making_Witnessed_a_holder()
    {
        var trace = TraceOf(SOURCE.Replace("value.Ping(); done();", "value.Put(done);"), "M:Lib.Api.Use(Lib.Base,System.Action)");

        Assert.Equal(FateClassifier.UNKNOWN_EXECUTION, FateOf(trace, "done").Fate);
        Assert.DoesNotContain(FateClassifier.HOLDER, trace.Answer.Classified!.Values.Select(fate => fate.Fate));
    }

    [Fact]
    public void A_delegate_seed_receiving_an_argument_records_a_deep_read()
    {
        var trace = Trace("public sealed class Options { public Action<object> Use; } " +
                          "public sealed class Value { } public static class Api { public static void Run(Options options, Value value) => options.Use(value); }",
                          "M:Lib.Api.Run(Lib.Options,Lib.Value)");

        Assert.NotNull(trace.Answer.Model);
        Assert.True(new EffectReader(trace.Driver!, trace.Run!).HasEffect("value", EffectReader.READS_DEEP));
        Assert.True(new EffectReader(trace.Driver!, trace.Run!).HasEffect("options", EffectReader.READS_DEEP));
    }

    [Fact]
    public void A_delegate_seed_receiving_a_probe_delegate_widens_its_fate()
    {
        var trace = Trace("public sealed class Options { public Action<Action> Use; } " +
                          "public static class Api { public static void Run(Options options, Action done) => options.Use(done); }",
                          "M:Lib.Api.Run(Lib.Options,System.Action)");

        Assert.Equal(FateClassifier.UNKNOWN_EXECUTION, FateOf(trace, "done").Fate);
    }

    [Fact]
    public void A_parameterless_delegate_seed_witnesses_its_own_object()
    {
        var trace = Trace("public sealed class Options { public Action Run; } " +
                          "public static class Api { public static void Run(Options options) => options.Run(); }", "M:Lib.Api.Run(Lib.Options)");

        Assert.NotNull(trace.Answer.Model);
        Assert.True(new EffectReader(trace.Driver!, trace.Run!).HasEffect("options", EffectReader.READS_DEEP));
    }

    [Fact]
    public void A_delegate_seed_with_an_unbuildable_return_keeps_its_invoke_extern()
    {
        var trace = Trace("public sealed class Hidden { private Hidden() { } } public sealed class Options { public Func<object, Hidden> Make; } " +
                          "public static class Api { public static void Run(Options options, object value) { _ = options.Make(value); } }",
                          "M:Lib.Api.Run(Lib.Options,System.Object)");

        Assert.NotNull(trace.Driver);
        Assert.Null(trace.Answer.Model);
        Assert.Equal(GenerationReasons.UNKNOWN_TOUCH, trace.Answer.ModelReason);
        Assert.Contains(trace.Driver.Compilation.GlobalNamespace.GetTypeMembers(), type => type.Name.StartsWith("Seed_", StringComparison.Ordinal) &&
            type.GetMembers("Invoke").OfType<IMethodSymbol>().Any(method => method.IsExtern));
    }

    [Theory]
    [InlineData("public abstract class User { public abstract Hidden Value { get; } }", "Lib.User")]
    [InlineData("public interface IUser { Hidden Value { get; } }", "Lib.IUser")]
    [InlineData("public abstract class User { public abstract Hidden this[object key] { get; } }", "Lib.User")]
    public void An_unbuildable_property_return_stays_an_unknown_call(string declaration, string type)
    {
        var indexed = declaration.Contains("this[", StringComparison.Ordinal);
        var trace = Trace("public sealed class Hidden { private Hidden() { } } " + declaration +
                          $" public static class Api {{ public static void Run({type} user) {{ _ = user{(indexed ? "[user]" : ".Value")}; }} }}",
                          $"M:Lib.Api.Run({type})");
        Assert.NotNull(trace.Driver);
        var property = trace.Driver.Compilation.GetTypeByMetadataName("Probe_user")!.GetMembers().OfType<IPropertySymbol>().Single();

        Assert.True(property.GetMethod!.IsExtern);
        Assert.Null(trace.Answer.Model);
        Assert.Equal(GenerationReasons.UNKNOWN_TOUCH, trace.Answer.ModelReason);
    }

    [Fact]
    public void A_buildable_property_getter_remains_a_witness()
    {
        var trace = Trace("public abstract class User { public abstract string Value { get; } } " +
                          "public static class Api { public static void Run(User user) { _ = user.Value; } }", "M:Lib.Api.Run(Lib.User)");
        var property = trace.Driver!.Compilation.GetTypeByMetadataName("Probe_user")!.GetMembers().OfType<IPropertySymbol>().Single();

        Assert.False(property.GetMethod!.IsExtern);
        Assert.True(new EffectReader(trace.Driver, trace.Run!).HasEffect("user", EffectReader.READS_DEEP));
    }

    private static Driver Drive(string memberId)
    {
        var library = Compile(SOURCE);
        Assert.NotNull(library.Compilation);
        var synthesis = DriverSynthesizer.Synthesize(library.Compilation!, DriverSynthesizer.FindMember(library.Compilation!, memberId)!,
                                                     library.ExternMembers.ToHashSet(StringComparer.Ordinal), CancellationToken.None);
        Assert.True(synthesis.Driver is not null, synthesis.Detail);
        return synthesis.Driver;
    }

    private static INamedTypeSymbol Type(Driver driver, string name) => driver.Compilation.GetTypeByMetadataName(name)!;

    private static IMethodSymbol Method(INamedTypeSymbol type, string name) =>
        type.GetMembers().OfType<IMethodSymbol>().Single(method => method.Name.EndsWith(name, StringComparison.Ordinal));

    private static string Body(INamedTypeSymbol type, string name) => Body(Method(type, name));

    private static string Body(IMethodSymbol method) =>
        ((MethodDeclarationSyntax)method.DeclaringSyntaxReferences.Single().GetSyntax()).Body!.ToString();

    private static string EventAccessor(Driver driver, MethodKind kind)
    {
        var @event = Type(driver, "Probe_value").GetMembers().OfType<IEventSymbol>().Single();
        var accessor = kind == MethodKind.EventAdd ? @event.AddMethod! : @event.RemoveMethod!;
        return ((AccessorDeclarationSyntax)accessor.DeclaringSyntaxReferences.Single().GetSyntax()).Body!.ToString();
    }
}
