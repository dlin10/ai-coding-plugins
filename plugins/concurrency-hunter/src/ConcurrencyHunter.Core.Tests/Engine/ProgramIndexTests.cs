using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ProgramIndexTests
{
    [Fact]
    public void Override_lookup_goes_through_two_levels()
    {
        var index = Index("""
            public class A { public virtual void Run() { } }
            public class B : A { public override void Run() { } }
            public class C : B { }
            public class D : C { public override void Run() { } }
            """);

        const string RUN = "body:Fixture:M:A.Run";
        Assert.Equal("body:Fixture:M:B.Run", index.Implementation("Fixture:C", RUN)!.MethodId);
        Assert.Equal("body:Fixture:M:D.Run", index.Implementation("Fixture:D", RUN)!.MethodId);
        Assert.Equal(RUN, index.Implementation("Fixture:A", RUN)!.MethodId);
        Assert.Equal("body:Fixture:M:B.Run", index.Method("body:Fixture:M:D.Run")!.OverriddenMethodId);
    }

    [Fact]
    public void Explicit_and_implicit_interface_implementations_resolve()
    {
        var index = Index("""
            public interface IRun { void Run(); void Stop(); }
            public class Runner : IRun { public void Run() { } void IRun.Stop() { } }
            public class Derived : Runner { }
            """);

        Assert.Equal("body:Fixture:M:Runner.Run", index.Implementation("Fixture:Derived", "body:Fixture:M:IRun.Run")!.MethodId);
        var stop = index.Implementation("Fixture:Runner", "body:Fixture:M:IRun.Stop")!;
        Assert.Equal("body:Fixture:M:Runner.IRun#Stop", stop.MethodId);
        Assert.Equal(["body:Fixture:M:IRun.Stop"], stop.ImplementedInterfaceMethodIds);
        Assert.Equal(["Fixture:IRun"], index.Type("Fixture:Runner")!.InterfaceKeys);
        Assert.True(index.Type("Fixture:IRun")!.IsInterface);
    }

    [Fact]
    public void Interface_member_resolves_to_the_override_of_its_implementation()
    {
        var index = Index("""
            public interface I { void M(); }
            public class Base : I { public virtual void M() { } }
            public sealed class Derived : Base { public override void M() { } }
            """);

        Assert.Equal("body:Fixture:M:Derived.M",
                     index.Implementation("Fixture:Derived", "body:Fixture:M:I.M", "Fixture:I")!.MethodId);
    }

    [Fact]
    public void New_tables_leave_the_type_and_method_tables_as_they_were()
    {
        var (index, compilation) = IndexAndCompilation("""
            public interface I<T> { void M(T value); }
            public class C<T> : I<T> { public virtual void M(T value) { } }
            public sealed class D : C<int> { public override void M(int value) { } }
            public class Use { public object Make() => new System.Collections.Generic.List<int>(); }
            """);
        var sourceTypes = new[] { "I`1", "C`1", "D", "Use" }
            .Select(name => compilation.GetTypeByMetadataName(name)!)
            .ToArray();
        var expectedTypes = sourceTypes.SelectMany(type => type.AllInterfaces.Select(@interface => @interface.OriginalDefinition)
                                         .Concat(type.BaseType is null ? [] : [type.BaseType.OriginalDefinition])
                                         .Append(type))
                                       .Select(SymbolNames.TypeKey)
                                       .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expectedTypes.Order(StringComparer.Ordinal), index.Types.Select(type => type.TypeKey).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(index.Types, type => type.TypeKey.Contains("List", StringComparison.Ordinal));
        var implemented = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var type in sourceTypes.Where(type => type.TypeKind != TypeKind.Interface))
        {
            foreach (var @interface in type.AllInterfaces)
            {
                foreach (var member in @interface.GetMembers().OfType<IMethodSymbol>())
                {
                    if (type.FindImplementationForInterfaceMember(member) is not IMethodSymbol implementation)
                        continue;
                    var implementationId = IrLowering.RootBodyId(implementation.OriginalDefinition);
                    if (!implemented.TryGetValue(implementationId, out var members))
                        implemented.Add(implementationId, members = new HashSet<string>(StringComparer.Ordinal));
                    members.Add(IrLowering.RootBodyId(member.OriginalDefinition));
                }
            }
        }
        foreach (var method in index.Methods)
            Assert.Equal(implemented.GetValueOrDefault(method.MethodId)?.Order(StringComparer.Ordinal).ToArray() ?? [],
                         method.ImplementedInterfaceMethodIds.Order(StringComparer.Ordinal));

        Assert.Contains(index.InterfaceMappings, mapping => mapping.TypeDefinitionKey == "Fixture:C<Fixture:T>" &&
                                                          mapping.InterfaceTypeKey == "Fixture:I<Fixture:T>" &&
                                                          mapping.InterfaceMemberId == "body:Fixture:M:I`1.M(`0)" &&
                                                          mapping.ImplementationMethodId == "body:Fixture:M:C`1.M(`0)");
    }

    [Fact]
    public void Abstract_method_has_no_body_and_no_implementation_of_its_own()
    {
        var index = Index("public abstract class Job { public abstract void Run(); public void Log() { } }");

        var run = index.Method("body:Fixture:M:Job.Run")!;
        Assert.True(run.IsAbstract);
        Assert.False(run.HasSourceBody);
        Assert.Empty(run.NestedBodyIds);
        Assert.Null(index.Implementation("Fixture:Job", run.MethodId));
        Assert.True(index.Method("body:Fixture:M:Job.Log")!.HasSourceBody);
        Assert.True(index.Type("Fixture:Job")!.IsAbstract);
    }

    [Fact]
    public void Metadata_base_type_is_indexed_with_its_methods()
    {
        var (index, compilation) = IndexAndCompilation("public class Failure : System.Exception { }");

        var exception = compilation.GetTypeByMetadataName("System.Exception")!;
        var exceptionKey = SymbolNames.TypeKey(exception);
        Assert.Equal(exceptionKey, index.Type("Fixture:Failure")!.BaseTypeKey);
        var toString = index.Method(IrLowering.RootBodyId(exception.GetMembers("ToString").OfType<IMethodSymbol>().Single()))!;
        Assert.False(toString.HasSourceBody);
        Assert.Equal(exceptionKey, toString.ContainingTypeKey);
        var objectToString = IrLowering.RootBodyId(compilation.GetSpecialType(SpecialType.System_Object).GetMembers("ToString").OfType<IMethodSymbol>().Single());
        Assert.Equal(toString.MethodId, index.Implementation("Fixture:Failure", objectToString)!.MethodId);
        Assert.True(index.Method("body:Fixture:M:Failure.#ctor")!.HasSourceBody);
    }

    [Fact]
    public void Generic_type_and_its_methods_are_keyed_on_the_original_definition()
    {
        var index = Index("public class Store<T> { public void Put(T item) { } public T? Last; }");

        var store = index.Type("Fixture:Store<Fixture:T>")!;
        Assert.Equal(["Fixture:T"], store.TypeParameterKeys);
        var put = index.Method("body:Fixture:M:Store`1.Put(`0)")!;
        Assert.Equal("Fixture:Store<Fixture:T>", put.ContainingTypeKey);
        Assert.Equal([new ProgramParameter("item", "Fixture:T", IrRefKind.None)], put.Parameters);
        Assert.Contains(index.Fields, field => field is { ContainingTypeKey: "Fixture:Store<Fixture:T>", Name: "Last", FieldTypeKey: "Fixture:T" });
    }

    [Fact]
    public void Delegate_compatibility_compares_parameters_ref_kinds_and_return()
    {
        var index = Index("""
            public delegate int Transform(string text);
            public delegate void Fill(out int value);
            public static class Handlers
            {
                public static int Length(string text) => text.Length;
                public static void Ignore(string text) { }
                public static int Two(string text, int extra) => extra;
                public static void Set(out int value) => value = 1;
                public static void SetByRef(ref int value) { }
            }
            """);

        Assert.True(index.Type("Fixture:Transform")!.IsDelegate);
        Assert.True(index.IsDelegateCompatible("Fixture:Transform", "body:Fixture:M:Handlers.Length(System.String)"));
        Assert.False(index.IsDelegateCompatible("Fixture:Transform", "body:Fixture:M:Handlers.Ignore(System.String)"));
        Assert.False(index.IsDelegateCompatible("Fixture:Transform", "body:Fixture:M:Handlers.Two(System.String,System.Int32)"));
        Assert.True(index.IsDelegateCompatible("Fixture:Fill", "body:Fixture:M:Handlers.Set(System.Int32@)"));
        Assert.False(index.IsDelegateCompatible("Fixture:Handlers", "body:Fixture:M:Handlers.Set(System.Int32@)"));
        Assert.Equal(IrRefKind.Out, Assert.Single(index.Method("body:Fixture:M:Handlers.Set(System.Int32@)")!.Parameters).RefKind);
    }

    [Fact]
    public void Fields_carry_static_and_readonly_flags()
    {
        var index = Index("""
            public class Settings
            {
                public static int Count;
                public static readonly object Gate = new();
                private readonly string _name = "";
                public int Value;
                public const int Limit = 3;
            }
            """);

        var fields = index.Fields.Where(field => field.ContainingTypeKey == "Fixture:Settings")
                          .ToDictionary(field => field.Name, field => (field.IsStatic, field.IsReadOnly));
        Assert.Equal((true, false), fields["Count"]);
        Assert.Equal((true, true), fields["Gate"]);
        Assert.Equal((false, true), fields["_name"]);
        Assert.Equal((false, false), fields["Value"]);
    }

    [Fact]
    public void Same_named_types_in_two_assemblies_are_kept_apart()
    {
        const string LIBRARY = "namespace Shared { public class Gate { public void Run() { } } }";
        var solution = FixtureSolution.CreateProjects(("Alpha", "Alpha.cs", LIBRARY), ("Beta", "Beta.cs", LIBRARY));
        var compilations = solution.Projects.Select(project => project.GetCompilationAsync().GetAwaiter().GetResult()!).ToArray();

        var index = ProgramIndexBuilder.Build("scope:Fixture", compilations, @"C:\fixture", CancellationToken.None);

        Assert.Equal("Alpha", index.Type("Alpha:Shared.Gate")!.Assembly);
        Assert.Equal("Beta", index.Type("Beta:Shared.Gate")!.Assembly);
        Assert.Equal("Beta:Shared.Gate", index.Method("body:Beta:M:Shared.Gate.Run")!.ContainingTypeKey);
        Assert.Equal("Alpha:Shared.Gate", index.Method("body:Alpha:M:Shared.Gate.Run")!.ContainingTypeKey);
        Assert.Null(index.Type("Shared.Gate"));
    }

    [Fact]
    public void Method_of_a_closed_generic_type_resolves_through_its_definition()
    {
        var index = Index("""
            public class Order { }
            public class Invoice { }
            public class Store<T> { public void Put(T item) { } }
            public class Use { private readonly Store<Order> _store = new(); }
            """);

        var closed = Assert.Single(index.ClosedGenericTypes, type => type.TypeKey == "Fixture:Store<Fixture:Order>");
        Assert.Equal("Fixture:Store<Fixture:T>", closed.OriginalDefinitionKey);
        Assert.Equal(["Fixture:Order"], closed.TypeArgumentKeys);
        const string PUT = "body:Fixture:M:Store`1.Put(`0)";
        Assert.Contains(index.MethodsOf("Fixture:Store<Fixture:Order>"), method => method.MethodId == PUT);
        Assert.Equal(PUT, index.Implementation("Fixture:Store<Fixture:Order>", PUT)!.MethodId);
        Assert.Equal(PUT, index.Implementation("Fixture:Store<Fixture:Invoice>", PUT)!.MethodId);
        Assert.Single(index.Constructors("Fixture:Store<Fixture:Order>"));
    }

    [Fact]
    public void Nested_body_ids_are_listed_per_member()
    {
        var index = Index("""
            public class Worker
            {
                private readonly int _seed = Compute(() => 1);
                public int Run()
                {
                    System.Func<int> first = () => 2;
                    int Local() => 3;
                    return first() + Local();
                }
                private static int Compute(System.Func<int> factory) => factory();
            }
            """);

        Assert.Equal(["body:Fixture:M:Worker.Run#lambda1", "body:Fixture:M:Worker.Run#local:Local"],
                     index.Method("body:Fixture:M:Worker.Run")!.NestedBodyIds);
        Assert.Equal(["body:Fixture:M:Worker.#ctor#lambda1"], index.Method("body:Fixture:M:Worker.#ctor")!.NestedBodyIds);
        Assert.Empty(index.Method("body:Fixture:M:Worker.Compute(System.Func{System.Int32})")!.NestedBodyIds);
    }

    [Fact]
    public void Base_type_is_projected_through_a_generic_middle_type()
    {
        var (index, compilation) = IndexAndCompilation("""
            public class Order { }
            public class Base<T> { }
            public class Middle<T> : Base<System.Collections.Generic.List<T>> { }
            public class Derived : Middle<Order> { }
            """);

        var middle = compilation.GetTypeByMetadataName("Derived")!.BaseType!;
        Assert.Equal(SymbolNames.TypeKey(middle.BaseType!), index.ConstructedBase("Fixture:Derived", "Fixture:Base<Fixture:T>"));
        Assert.Equal("Fixture:Middle<Fixture:Order>", index.ConstructedBase("Fixture:Derived", "Fixture:Middle<Fixture:T>"));
        Assert.Equal("Fixture:Middle<Fixture:Order>", index.Type("Fixture:Derived")!.BaseTypeKey);
        Assert.Null(index.ConstructedBase("Fixture:Derived", "Fixture:Order"));
    }

    [Fact]
    public void Implicit_type_initializer_exists_only_for_static_initializers()
    {
        var index = Index("""
            public class Primed { private static readonly object Gate = new(); }
            public class Plain { private static int _count; }
            public class Auto { public int Value { get; set; } public abstract class Nested { public abstract int Size { get; } } }
            """);

        var initializer = index.Method("body:Fixture:M:Primed.#cctor")!;
        Assert.Equal((ProgramMethodKind.TypeInitializer, true), (initializer.Kind, initializer.HasSourceBody));
        Assert.DoesNotContain(index.MethodsOf("Fixture:Plain"), method => method.Kind == ProgramMethodKind.TypeInitializer);
        var getter = index.Method("body:Fixture:M:Auto.get_Value")!;
        Assert.Equal((ProgramMethodKind.Accessor, true), (getter.Kind, getter.HasSourceBody));
        Assert.False(index.Method("body:Fixture:M:Auto.Nested.get_Size")!.HasSourceBody);
    }

    [Fact]
    public void Instance_fields_carry_their_type_keys_in_walk_order()
    {
        var index = Index("""
            public class Item { }
            public class Base { public Item BaseField; }
            public class Store(Item captured) : Base
            {
                public Item Field;
                public Item Property { get; set; }
                public Item Read() => captured;
            }
            """);
        var fields = index.InstanceFieldsOf("Fixture:Store")!;
        Assert.Equal(new[] { "Field", "Property", "captured", "BaseField" }, fields.Select(field => field.Name));
        Assert.All(fields, field => Assert.Equal("Fixture:Item", field.FieldTypeKey));
        Assert.All(fields, field => Assert.Equal("Item", field.Type));
    }

    [Fact]
    public void Inherited_instance_field_type_key_is_substituted_through_a_generic_base()
    {
        var index = Index("""
            public class Item { }
            public class Base<T> { public T Field; public T Property { get; set; } }
            public class Middle<T> : Base<T> { }
            public class Derived : Middle<Item> { }
            """);
        var fields = index.InstanceFieldsOf("Fixture:Derived")!;
        Assert.Equal(new[] { "Field", "Property" }, fields.Select(field => field.Name));
        Assert.All(fields, field => Assert.Equal("Fixture:Item", field.FieldTypeKey));
        Assert.All(fields, field => Assert.Equal("Base<Fixture:Item>", field.ContainingTypeId));
        Assert.All(fields, field => Assert.Equal("T", field.Type));
    }

    [Fact]
    public void Field_like_event_storage_is_an_instance_field_and_a_static_ones_is_static()
    {
        var index = Index("""
            public class Item { }
            public class Hub
            {
                public event System.Action<Item> Changed;
                public static event System.Action Shared;
                public event System.Action Custom { add { } remove { } }
            }
            """);

        Assert.Contains(new ProgramField("Fixture:Hub", "Changed", false, false, "System.Private.CoreLib:System.Action<Fixture:Item>"), index.Fields);
        Assert.Contains(new ProgramField("Fixture:Hub", "Shared", true, false, "System.Private.CoreLib:System.Action"), index.Fields);
        Assert.DoesNotContain(index.Fields, field => field.Name == "Custom");
        var storage = Assert.Single(index.InstanceFieldsOf("Fixture:Hub")!);
        Assert.Equal(("Changed", IrFieldKind.Field, false), (storage.Name, storage.Kind, storage.IsStatic));
    }

    [Fact]
    public void Field_like_event_accessors_have_a_source_body_and_abstract_or_extern_ones_do_not()
    {
        var index = Index("""
            public interface ISource { event System.Action Changed; }
            public class Hub : ISource
            {
                public event System.Action Changed;
                public static extern event System.Action External;
            }
            """);

        Assert.True(index.Method("body:Fixture:M:Hub.add_Changed(System.Action)")!.HasSourceBody);
        Assert.True(index.Method("body:Fixture:M:Hub.remove_Changed(System.Action)")!.HasSourceBody);
        Assert.False(index.Method("body:Fixture:M:ISource.add_Changed(System.Action)")!.HasSourceBody);
        Assert.False(index.Method("body:Fixture:M:Hub.add_External(System.Action)")!.HasSourceBody);
        Assert.DoesNotContain(index.Fields, field => field.Name == "External");
    }

    [Fact]
    public void Field_like_event_initializer_alone_gives_its_type_a_static_constructor_body()
    {
        var index = Index("public static class Hub { public static event System.Action Shared = () => { }; }");

        var initializer = index.Method("body:Fixture:M:Hub.#cctor")!;
        Assert.Equal((ProgramMethodKind.TypeInitializer, true), (initializer.Kind, initializer.HasSourceBody));
    }

    private static ProgramIndex Index(string source) => IndexAndCompilation(source).Index;

    private static (ProgramIndex Index, Compilation Compilation) IndexAndCompilation(string source)
    {
        var compilation = FixtureSolution.Create(("Case.cs", source)).Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        return (ProgramIndexBuilder.Build("scope:Fixture", [compilation], @"C:\fixture", CancellationToken.None), compilation);
    }
}
