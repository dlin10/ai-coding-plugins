using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class SeedTests
{
    private const string SOURCE = """
        using System;
        using System.Collections;
        using System.Collections.Generic;

        namespace Lib
        {
            public interface IUser { object Echo(object value); }
            public interface IStaticUser { static abstract object Make(); }
            public class UserBase { public UserBase() { } public virtual object Echo(object value) => value; }
            public class Locked { private Locked() { } public object Value; }
            public sealed class Options { public object Value; }
            public sealed class L4 { public object Seed; }
            public sealed class L3 { public object Seed; public L4 Next = new(); }
            public sealed class L2 { public object Seed; public L3 Next = new(); }
            public sealed class L1 { public object Seed; public L2 Next = new(); }
            public class Receiver { public L1 Root = new(); public void Run() { } }
            public struct Nested { public object Value; }
            public struct OuterStruct { public Nested Nested; }
            public class BaseFields { public object Same; }
            public sealed class DerivedFields : BaseFields { public new object Same; }
            public class WitnessBase { public virtual object Make() => null; }
            public sealed class Mystery<T> : IEnumerable<T>
            {
                public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Array.Empty<T>()).GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }
            public sealed class All
            {
                public object Direct;
                public IUser Interface;
                public UserBase Base;
                public Func<object, object> Delegate;
                public object[] One;
                public object[,] Two;
                public List<object> List;
                public Queue<object> Queue;
                public Dictionary<string, object> StringKeys;
                public Dictionary<object, object> ObjectKeys;
                public IEnumerable<object> Objects;
                public IEnumerable<int> Numbers;
                public Mystery<object> Mystery;
                public List<Options> OptionList;
                public Options[] OptionArray;
                public IEnumerable<Options> OptionSequence;
                public Locked Locked;
                public System.Delegate AnyDelegate;
                public IStaticUser StaticUser;
                public OuterStruct Struct;
                public DerivedFields Hidden;
                private object Secret { get; }
            }
            public static class Statics
            {
                public static L1 Root = new();
            }
            public static class GenericStatics<T>
            {
                public static object Value;
            }
            public static class ImpossibleStatics<T> where T : struct, IUser
            {
                public static object Value;
            }
            public static class Api
            {
                public static void Use(All value) { _ = Statics.Root; _ = GenericStatics<object>.Value; }
                public static void UseArg(L1 value) { }
                public static void UseStruct(OuterStruct value) { }
                public static void UseArray(Options[] value) { }
                public static void UseWitness(WitnessBase value) { }
            }
        }
        """;

    private const string CLOSED_SOURCE = """
        namespace Lib
        {
            public sealed class Closed
            {
                private object _direct;
                private Inner _inner = new();
                private sealed class Inner { public object Value; }
            }
            public static class Api { public static void Use(Closed value) { } }
        }
        """;

    private static readonly Lazy<LibraryCompilationResult> Library = new(() => Compile(SOURCE));
    private static readonly Lazy<LibraryCompilationResult> ClosedLibrary = new(() => Compile(CLOSED_SOURCE, openFields: false));

    [Fact]
    public void An_arguments_opened_field_gets_a_seed_in_V_Setup()
    {
        Assert.Contains(".Direct = new Seed_", Setup(Use()));
    }

    [Fact]
    public void The_receivers_field_at_depth_3_is_seeded()
    {
        Assert.Contains(Drive("M:Lib.Receiver.Run").SeedStatements,
                        seed => seed.Path == "this/F:Lib.Receiver.Root/F:Lib.L1.Next/F:Lib.L2.Seed");
    }

    [Fact]
    public void The_first_receiver_field_past_depth_3_is_unseeded()
    {
        Assert.Contains("this/F:Lib.Receiver.Root/F:Lib.L1.Next/F:Lib.L2.Next/F:Lib.L3.Seed",
                        Drive("M:Lib.Receiver.Run").Unseeded);
    }

    [Fact]
    public void An_arguments_field_at_depth_2_is_seeded()
    {
        Assert.Contains(Drive("M:Lib.Api.UseArg(Lib.L1)").SeedStatements, seed => seed.Path.EndsWith("F:Lib.L2.Seed", StringComparison.Ordinal));
    }

    [Fact]
    public void The_first_argument_field_past_depth_2_is_unseeded()
    {
        Assert.Contains(Drive("M:Lib.Api.UseArg(Lib.L1)").Unseeded, path => path.EndsWith("F:Lib.L3.Seed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_static_field_at_depth_2_below_a_static_is_seeded()
    {
        Assert.Contains(Use().SeedStatements, seed => seed.Path == "static/F:Lib.Statics.Root/F:Lib.L1.Next/F:Lib.L2.Seed");
    }

    [Fact]
    public void The_first_static_field_past_depth_2_is_unseeded()
    {
        Assert.Contains("static/F:Lib.Statics.Root/F:Lib.L1.Next/F:Lib.L2.Next/F:Lib.L3.Seed", Use().Unseeded);
    }

    [Fact]
    public void A_static_field_of_a_generic_type_uses_the_drivers_construction()
    {
        Assert.Contains("global::Lib.GenericStatics<Probe_T>.Value", Setup(Use()));
    }

    [Fact]
    public void A_one_dimensional_array_field_holds_a_seed()
    {
        Assert.Contains(".One = new global::System.Object[] { new Seed_", Setup(Use()));
    }

    [Fact]
    public void A_rank_2_array_field_has_one_seeded_cell()
    {
        Assert.Contains(".Two = new global::System.Object[,] { { new Seed_", Setup(Use()));
    }

    [Fact]
    public void A_List_field_holds_a_seed_without_a_List_subclass()
    {
        var source = Use().Source;
        Assert.Contains("new global::System.Collections.Generic.List<global::System.Object>()", source);
        Assert.DoesNotContain(" : global::System.Collections.Generic.List", source);
    }

    [Fact]
    public void A_Queue_field_has_a_seed_enqueued()
    {
        Assert.Contains(".Enqueue(new Seed_", Setup(Use()));
    }

    [Fact]
    public void A_dictionary_field_gets_a_seed_value()
    {
        Assert.Contains("SeedContainer_", Setup(Use()));
        Assert.Contains("[\"s\"] = new Seed_", Setup(Use()));
    }

    [Fact]
    public void A_dictionary_key_gets_a_seed_when_it_can_hold_a_user_object()
    {
        Assert.Contains("[new Seed_", Setup(Use()));
    }

    [Fact]
    public void A_dictionary_key_that_cannot_hold_a_user_object_gets_the_recipe_value()
    {
        Assert.Contains("[\"s\"]", Setup(Use()));
    }

    [Fact]
    public void IEnumerable_of_object_gets_a_seed_implementation_and_a_list()
    {
        var source = Use().Source;
        Assert.Contains("new global::System.Collections.Generic.List<global::System.Object>()", source);
        Assert.Contains(" : global::System.Collections.Generic.IEnumerable<global::System.Object>", source);
    }

    [Fact]
    public void IEnumerable_of_int_gets_a_seed_implementation()
    {
        Assert.Contains(" : global::System.Collections.Generic.IEnumerable<global::System.Int32>", Use().Source);
    }

    [Fact]
    public void A_collection_outside_the_table_is_unseeded()
    {
        Assert.Contains("arg:value/F:Lib.All.Mystery", Use().Unseeded);
    }

    [Fact]
    public void A_sealed_library_field_is_reached_through_to_its_path_field()
    {
        Assert.Contains(Drive("M:Lib.Api.UseArg(Lib.L1)").SeedStatements, seed => seed.Path.Contains("F:Lib.L1.Next/F:Lib.L2.Seed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_struct_field_lists_its_seed_typed_reference_fields()
    {
        Assert.Contains("arg:value/F:Lib.All.Struct/F:Lib.OuterStruct.Nested/F:Lib.Nested.Value", Use().Unseeded);
    }

    [Fact]
    public void A_field_of_a_class_with_only_private_constructors_is_unseeded()
    {
        Assert.Contains("arg:value/F:Lib.All.Locked", Use().Unseeded);
    }

    [Fact]
    public void A_System_Delegate_field_is_unseeded()
    {
        Assert.Contains("arg:value/F:Lib.All.AnyDelegate", Use().Unseeded);
    }

    [Fact]
    public void An_interface_with_a_static_abstract_member_is_unseeded()
    {
        Assert.Contains("arg:value/F:Lib.All.StaticUser", Use().Unseeded);
    }

    [Fact]
    public void A_generic_type_with_no_valid_construction_has_unseeded_statics()
    {
        var library = Library.Value;
        var field = library.Compilation!.GetTypeByMetadataName("Lib.ImpossibleStatics`1")!.GetMembers("Value").Single();
        var member = DriverSynthesizer.FindMember(library.Compilation, "M:Lib.Api.Use(Lib.All)")!;
        var synthesis = DriverSynthesizer.Synthesize(library.Compilation, member, library.ExternMembers, library.OpenedFields,
                                                     new HashSet<(string Type, string Name)> { SeedableFields.StaticStorage(field) },
                                                     CancellationToken.None);

        Assert.Contains("static/F:Lib.ImpossibleStatics`1.Value", Assert.IsType<Driver>(synthesis.Driver).Unseeded);
    }

    [Fact]
    public void A_static_field_no_reached_body_loads_is_neither_seeded_nor_listed_unseeded()
    {
        var driver = Drive("M:Lib.Api.UseArg(Lib.L1)");

        Assert.DoesNotContain(driver.SeedStatements, seed => seed.Path.StartsWith("static/", StringComparison.Ordinal));
        Assert.DoesNotContain(driver.Unseeded, path => path.StartsWith("static/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_static_field_the_member_loads_is_seeded()
    {
        Assert.Contains(Use().SeedStatements, seed => seed.Path == "static/F:Lib.GenericStatics`1.Value");
    }

    [Fact]
    public void A_static_field_loaded_through_a_ref_is_seeded()
    {
        var library = Compile("""
            namespace Lib
            {
                public static class Api
                {
                    public static object Value;
                    public static object Read(object input) { ref object slot = ref Value; return slot; }
                }
            }
            """);

        Assert.Contains(Drive("M:Lib.Api.Read(System.Object)", library).SeedStatements, seed => seed.Path == "static/F:Lib.Api.Value");
    }

    [Fact]
    public void A_loaded_static_auto_property_is_seeded()
    {
        var library = Compile("""
            namespace Lib
            {
                public static class Api
                {
                    public static object Value { get; set; }
                    public static object Read(object input) => Value;
                }
            }
            """);

        Assert.Contains(Drive("M:Lib.Api.Read(System.Object)", library).SeedStatements, seed => seed.Path == "static/P:Lib.Api.Value");
    }

    [Fact]
    public void A_static_load_through_a_ref_return_is_seeded()
    {
        var library = Compile("""
            namespace Lib
            {
                public static class Api
                {
                    public static object Value;
                    private static ref object Slot() => ref Value;
                    public static object Read(object input) => Slot();
                }
            }
            """);

        Assert.Contains(Drive("M:Lib.Api.Read(System.Object)", library).SeedStatements, seed => seed.Path == "static/F:Lib.Api.Value");
    }

    [Fact]
    public void A_static_load_through_a_ref_parameter_is_seeded()
    {
        var library = Compile("""
            namespace Lib
            {
                public static class Api
                {
                    public static object Value;
                    private static object Load(ref object slot) => slot;
                    public static object Read(object input) => Load(ref Value);
                }
            }
            """);

        Assert.Contains(Drive("M:Lib.Api.Read(System.Object)", library).SeedStatements, seed => seed.Path == "static/F:Lib.Api.Value");
    }

    [Fact]
    public void A_static_field_that_is_only_written_through_a_ref_gets_no_seed()
    {
        var library = Compile("""
            namespace Lib
            {
                public static class Api
                {
                    public static object Value;
                    private static void Store(ref object slot, object input) { slot = input; }
                    public static void Write(object input) { Store(ref Value, input); }
                }
            }
            """);
        var driver = Drive("M:Lib.Api.Write(System.Object)", library);

        Assert.DoesNotContain(driver.SeedStatements, seed => seed.Path == "static/F:Lib.Api.Value");
        Assert.DoesNotContain("static/F:Lib.Api.Value", driver.Unseeded);
    }

    [Fact]
    public void A_static_field_that_is_only_written_gets_no_seed()
    {
        var library = Compile("""
            namespace Lib
            {
                public static class Api
                {
                    public static object Value;
                    public static void Write(object input) { Value = input; }
                }
            }
            """);
        var driver = Drive("M:Lib.Api.Write(System.Object)", library);

        Assert.DoesNotContain(driver.SeedStatements, seed => seed.Path == "static/F:Lib.Api.Value");
        Assert.DoesNotContain("static/F:Lib.Api.Value", driver.Unseeded);
    }

    [Fact]
    public void A_static_load_reached_only_after_seeding_gets_a_seed_on_the_next_iteration()
    {
        var library = Compile("""
            namespace Lib
            {
                public class Hook { public Hook() { _ = Later.Value; } public virtual void Run() { } }
                public static class Later { public static object Value; }
                public static class Api
                {
                    public static Hook Callback;
                    public static void Use(object value) { Callback.Run(); }
                }
            }
            """);
        var driver = Drive("M:Lib.Api.Use(System.Object)", library);

        Assert.Contains(driver.SeedStatements, seed => seed.Path == "static/F:Lib.Api.Callback");
        Assert.Contains(driver.SeedStatements, seed => seed.Path == "static/F:Lib.Later.Value");
    }

    [Fact]
    public void No_seed_statement_is_in_call_enumeration_or_trigger_actions()
    {
        var driver = Use();
        Assert.All(driver.SeedStatements, seed => Assert.Equal(DriverSynthesizer.SETUP, EnclosingMethod(seed.Operation)));
    }

    [Fact]
    public void A_witness_returning_a_seedable_type_returns_a_seed()
    {
        Assert.Contains("override global::System.Object Make() {", Drive("M:Lib.Api.UseWitness(Lib.WitnessBase)").Source);
        Assert.Contains("return new Seed_", Drive("M:Lib.Api.UseWitness(Lib.WitnessBase)").Source);
    }

    [Fact]
    public void A_List_of_path_elements_is_filled_enumerated_and_seeded()
    {
        var setup = Setup(Use());
        Assert.Contains(".Add(new global::Lib.Options())", setup);
        Assert.Contains("foreach (global::Lib.Options", setup);
        Assert.Contains(Use().SeedStatements, seed => seed.Path.EndsWith("F:Lib.Options.Value", StringComparison.Ordinal));
    }

    [Fact]
    public void An_array_of_path_elements_is_enumerated_and_seeded()
    {
        var setup = Setup(Use());
        Assert.Contains(".OptionArray = new global::Lib.Options[] { new global::Lib.Options() }", setup);
        Assert.Contains("foreach (global::Lib.Options", setup);
    }

    [Fact]
    public void An_interface_sequence_of_path_elements_uses_its_seed_container_for_recursion()
    {
        Assert.Contains(Use().SeedStatements, seed => seed.Path.Contains("F:Lib.All.OptionSequence/F:Lib.Options.Value", StringComparison.Ordinal));
    }

    [Fact]
    public void An_array_arguments_elements_get_seeds_in_their_fields()
    {
        Assert.Contains(Drive("M:Lib.Api.UseArray(Lib.Options[])").SeedStatements,
                        seed => seed.Path == "arg:value/F:Lib.Options.Value");
    }

    [Fact]
    public void A_hidden_base_field_is_seeded_through_its_declaring_type()
    {
        var setup = Setup(Use());
        Assert.Contains("((global::Lib.BaseFields)", setup);
        Assert.Contains("((global::Lib.DerivedFields)", setup);
    }

    [Fact]
    public void A_private_get_only_auto_property_gets_a_seed_through_its_added_setter()
    {
        Assert.Contains(".Secret = new Seed_", Setup(Use()));
    }

    [Fact]
    public void A_reverted_seed_field_and_fields_below_a_reverted_path_are_unseeded()
    {
        var driver = Drive("M:Lib.Api.Use(Lib.Closed)", ClosedLibrary.Value);

        Assert.Contains("arg:value/F:Lib.Closed._direct", driver.Unseeded);
        Assert.Contains("arg:value/F:Lib.Closed._inner/F:Lib.Closed.Inner.Value", driver.Unseeded);
    }

    [Fact]
    public void Every_seed_statement_carries_its_field_path_and_assignment()
    {
        Assert.All(Use().SeedStatements, seed =>
        {
            Assert.True(seed.Path.StartsWith("arg:", StringComparison.Ordinal) || seed.Path.StartsWith("this", StringComparison.Ordinal) ||
                        seed.Path.StartsWith("static", StringComparison.Ordinal), seed.Path);
            Assert.IsAssignableFrom<ISimpleAssignmentOperation>(seed.Operation);
        });
    }

    [Fact]
    public void The_driver_with_every_seed_kind_compiles()
    {
        Assert.Empty(Use().Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData("public readonly List<Options> Items = new() { new Options() };", "F:Lib.Container.Items")]
    [InlineData("public readonly Options[] Items = new[] { new Options() };", "F:Lib.Container.Items")]
    [InlineData("public List<Options> Items { get; } = new() { new Options() };", "P:Lib.Container.Items")]
    [InlineData("public Options[] Items { get; } = new[] { new Options() };", "P:Lib.Container.Items")]
    public void A_read_only_container_path_seeds_existing_elements_without_writing_the_path(string declaration, string id)
    {
        var library = Compile("using System.Collections.Generic; namespace Lib { public sealed class Options { public object Value; } " +
                              $"public sealed class Container {{ {declaration} }} public static class Api {{ public static void Run(Container value) {{ }} }} }}",
                              openFields: false);
        var driver = Drive("M:Lib.Api.Run(Lib.Container)", library);

        Assert.Contains(driver.SeedStatements, seed => seed.Path == $"arg:value/{id}/F:Lib.Options.Value");
        Assert.DoesNotContain(driver.SeedStatements, seed => seed.Path == $"arg:value/{id}");
        Assert.DoesNotContain(driver.Unseeded, path => path.EndsWith("F:Lib.Options.Value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("public readonly List<Options> Items = new();", "F:Lib.Container.Items", ".Items.Add(")]
    [InlineData("public List<Options> Items { get; } = new();", "P:Lib.Container.Items", ".Items.Add(")]
    [InlineData("public readonly Dictionary<string, Options> Items = new();", "F:Lib.Container.Items", ".Items[\"s\"] = ")]
    [InlineData("public readonly Options[] Items = new Options[1];", "F:Lib.Container.Items", ".Items[0] = ")]
    public void An_empty_read_only_container_path_gets_a_recipe_built_element_that_carries_the_seeds(string declaration, string id, string added)
    {
        var library = Compile("using System.Collections.Generic; namespace Lib { public sealed class Options { public object Value; } " +
                              $"public sealed class Container {{ {declaration} }} public static class Api {{ public static void Run(Container value) {{ }} }} }}",
                              openFields: false);
        var driver = Drive("M:Lib.Api.Run(Lib.Container)", library);

        Assert.Contains(added, Setup(driver));
        Assert.Contains(driver.SeedStatements, seed => seed.Path == $"arg:value/{id}/F:Lib.Options.Value");
        Assert.DoesNotContain(driver.Unseeded, path => path.EndsWith("F:Lib.Options.Value", StringComparison.Ordinal));
        Assert.Empty(driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData("public readonly Options[] Items = new Options[1];", "F:Lib.Container.Items")]
    [InlineData("public readonly List<Options> Items = new();", "F:Lib.Container.Items")]
    [InlineData("public static readonly Options[] Items = new Options[1];", null)]
    public void A_read_only_container_whose_element_the_driver_cannot_build_records_the_seeds_below_it(string declaration, string? id)
    {
        var call = id is null ? "_ = Container.Items[0].Value;" : "";
        var library = Compile("using System.Collections.Generic; namespace Lib { public sealed class Options { private Options() { } public object Value; } " +
                              $"public sealed class Container {{ {declaration} }} public static class Api {{ public static void Run(Container value) {{ {call} }} }} }}",
                              openFields: false);
        var driver = Drive("M:Lib.Api.Run(Lib.Container)", library);

        Assert.Contains((id is null ? "static/F:Lib.Container.Items" : $"arg:value/{id}") + "/F:Lib.Options.Value", driver.Unseeded);
        Assert.Empty(driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData("public static readonly Root Value = new();", "F:Lib.State.Value")]
    [InlineData("public static Root Value { get; } = new();", "P:Lib.State.Value")]
    public void A_loaded_read_only_static_path_seeds_below_it_without_writing_it(string declaration, string id)
    {
        var library = Compile("namespace Lib { public sealed class Root { public object Item; } " +
                              $"public static class State {{ {declaration} }} public static class Api {{ public static void Run(object value) {{ _ = State.Value.Item; }} }} }}",
                              openFields: false);
        var driver = Drive("M:Lib.Api.Run(System.Object)", library);

        Assert.Contains(driver.SeedStatements, seed => seed.Path == $"static/{id}/F:Lib.Root.Item");
        Assert.DoesNotContain(driver.SeedStatements, seed => seed.Path == $"static/{id}");
    }

    [Fact]
    public void A_seeded_static_root_retains_two_steps_below_the_static_and_records_the_boundary()
    {
        var library = Compile("namespace Lib { public sealed class Leaf { public object Item; public Leaf Next = new(); } " +
                              "public class Root { public Leaf Child = new(); } public static class State { public static Root Value = new(); } " +
                              "public static class Api { public static void Run(object value) { _ = State.Value.Child.Item; } } }");
        var driver = Drive("M:Lib.Api.Run(System.Object)", library);

        Assert.Contains(driver.SeedStatements, seed => seed.Path == "static/F:Lib.State.Value");
        Assert.Contains(driver.SeedStatements, seed => seed.Path == "static/F:Lib.State.Value/F:Lib.Root.Child/F:Lib.Leaf.Item");
        Assert.Contains("static/F:Lib.State.Value/F:Lib.Root.Child/F:Lib.Leaf.Next/F:Lib.Leaf.Item", driver.Unseeded);
    }

    [Theory]
    [InlineData("public object Value { get; set; }", "P:Lib.Part.Value")]
    [InlineData("public Nested Value { get; set; }", "P:Lib.Part.Value/P:Lib.Nested.Item")]
    public void A_struct_auto_property_lists_the_unseeded_reference_storage(string declaration, string path)
    {
        var library = Compile($"namespace Lib {{ public struct Nested {{ public object Item {{ get; set; }} }} public struct Part {{ {declaration} }} " +
                              "public static class Api { public static void Run(Part value, object input) { } } }");
        var driver = Drive("M:Lib.Api.Run(Lib.Part,System.Object)", library);

        Assert.Contains("arg:value/" + path, driver.Unseeded);
    }

    [Theory]
    [InlineData("object[][,]", ".Values = new global::System.Object[][,] { new global::System.Object[,] { { new Seed_")]
    [InlineData("object[,][]", ".Values = new global::System.Object[,][] { { new global::System.Object[] { new Seed_")]
    public void A_nested_array_field_builds_every_level_with_its_own_rank(string type, string expected)
    {
        var library = Compile($"namespace Lib {{ public sealed class Holder {{ public {type} Values; }} " +
                              "public static class Api { public static void Run(Holder holder) { } } }");
        var driver = Drive("M:Lib.Api.Run(Lib.Holder)", library);

        Assert.Contains(expected, Setup(driver));
        Assert.Empty(driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    private static Driver Use() => Drive("M:Lib.Api.Use(Lib.All)");

    private static string Setup(Driver driver) => driver.Compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
                                                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                                                        .Single(method => method.Identifier.Text == DriverSynthesizer.SETUP).Body!.ToString();

    private static string EnclosingMethod(IOperation operation) => operation.Syntax.Ancestors()
        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single().Identifier.Text;

    private static Driver Drive(string id, LibraryCompilationResult? result = null)
    {
        var library = result ?? Library.Value;
        Assert.NotNull(library.Compilation);
        var trace = ModelGenerator.Trace(new GenerationRequest("Fixture.Library", "1.0", id, null, null), library, CancellationToken.None);
        Assert.True(trace.Driver is not null, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        return trace.Driver;
    }

    private static LibraryCompilationResult Compile(string source, bool openFields = true) =>
        LibraryCompilation.CompileTrees("Fixture.Library",
                                        [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview),
                                                                    "Fixture.Library/Library.cs")],
                                        EmittedAssemblies.RuntimeReferences, CancellationToken.None, openFields: openFields);
}
