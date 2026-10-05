using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The generator opens exactly the seed and path declarations its driver must reach, without making the copied library
/// fail to compile.</summary>
public sealed class OpenedFieldsTests
{
    [Fact]
    public void A_private_interface_typed_instance_field_is_writable_from_the_driver_assembly()
    {
        var result = CompileSource("public sealed class Holder { private System.IDisposable _value; }");

        Assert.Equal(Accessibility.Internal, Field(result, "Holder", "_value").DeclaredAccessibility);
        AssertDriverCompiles(result, "public static class Driver { public static void Set(Holder h, System.IDisposable v) { h._value = v; } }");
    }

    [Fact]
    public void A_readonly_seed_field_loses_readonly()
    {
        var result = CompileSource("public sealed class Holder { private readonly object _value = new(); }");

        var field = Field(result, "Holder", "_value");
        Assert.False(field.IsReadOnly);
        Assert.Contains("F:Holder._value", result.OpenedFields);
    }

    [Fact]
    public void A_private_path_field_of_a_sealed_library_class_is_reachable_and_keeps_readonly()
    {
        var result = CompileSource("public sealed class Node { } public sealed class Holder { private readonly Node _node = new(); }");

        var field = Field(result, "Holder", "_node");
        Assert.Equal(Accessibility.Internal, field.DeclaredAccessibility);
        Assert.True(field.IsReadOnly);
    }

    [Fact]
    public void A_path_only_init_auto_property_keeps_init_and_only_its_getter_is_opened()
    {
        var result = CompileSource("public sealed class Node { } public sealed class Holder { private Node P { get; init; } = new(); public Node Q { get; private init; } = new(); }");

        var p = Property(result, "Holder", "P");
        Assert.Equal(Accessibility.Internal, p.GetMethod!.DeclaredAccessibility);
        Assert.True(p.SetMethod!.IsInitOnly);
        var q = Property(result, "Holder", "Q");
        Assert.True(q.SetMethod!.IsInitOnly);
        Assert.Equal(Accessibility.Private, q.SetMethod.DeclaredAccessibility);
        AssertDriverCompiles(result, "public static class Driver { public static Node Get(Holder h) => h.P; }");
    }

    [Fact]
    public void A_seed_typed_init_auto_property_becomes_settable()
    {
        var result = CompileSource("public sealed class Holder { private object P { get; init; } }");

        var setter = Property(result, "Holder", "P").SetMethod!;
        Assert.False(setter.IsInitOnly);
        AssertDriverCompiles(result, "public static class Driver { public static void Set(Holder h, object v) { h.P = v; } }");
    }

    [Fact]
    public void A_field_declared_in_a_struct_is_untouched()
    {
        var result = CompileSource("public struct Box { private object _value; }");

        Assert.Equal(Accessibility.Private, Field(result, "Box", "_value").DeclaredAccessibility);
        Assert.DoesNotContain("F:Box._value", result.OpenedFields);
    }

    [Fact]
    public void A_private_get_only_auto_property_becomes_internal_and_settable()
    {
        var result = CompileSource("public sealed class Holder { private object P { get; } }");

        var property = Property(result, "Holder", "P");
        Assert.Equal(Accessibility.Internal, property.DeclaredAccessibility);
        Assert.Equal(Accessibility.Internal, property.SetMethod!.DeclaredAccessibility);
        Assert.Contains("P:Holder.P", result.OpenedFields);
    }

    [Fact]
    public void A_public_auto_property_with_a_private_setter_is_settable_from_the_driver()
    {
        var result = CompileSource("public sealed class Holder { public object P { get; private set; } }");

        Assert.Equal(Accessibility.Public, Property(result, "Holder", "P").SetMethod!.DeclaredAccessibility);
        AssertDriverCompiles(result, "public static class Driver { public static void Set(Holder h, object v) { h.P = v; } }");
    }

    [Fact]
    public void A_private_auto_property_of_a_path_type_is_readable()
    {
        var result = CompileSource("public sealed class Node { } public sealed class Holder { private Node P { get; } = new(); }");

        var property = Property(result, "Holder", "P");
        Assert.Equal(Accessibility.Internal, property.GetMethod!.DeclaredAccessibility);
        Assert.Null(property.SetMethod);
        AssertDriverCompiles(result, "public static class Driver { public static Node Get(Holder h) => h.P; }");
    }

    [Fact]
    public void A_string_field_is_untouched()
    {
        var result = CompileSource("public sealed class Holder { private string _value = \"\"; }");

        Assert.Equal(Accessibility.Private, Field(result, "Holder", "_value").DeclaredAccessibility);
        Assert.Empty(result.OpenedFields);
    }

    [Fact]
    public void A_struct_with_references_field_is_untouched()
    {
        var result = CompileSource("public struct Payload { public object Value; } public sealed class Holder { private Payload _value; }");

        Assert.True(SeedableFields.Struct(Field(result, "Holder", "_value").Type));
        Assert.Equal(Accessibility.Private, Field(result, "Holder", "_value").DeclaredAccessibility);
    }

    [Fact]
    public void A_struct_constrained_type_parameter_field_is_not_a_seed_field()
    {
        var result = CompileSource("public sealed class Holder<T> where T : struct { private T _value; }");

        var field = Field(result, "Holder`1", "_value");
        Assert.False(SeedableFields.Seed(field.Type));
        Assert.Equal(Accessibility.Private, field.DeclaredAccessibility);
    }

    [Fact]
    public void A_static_seed_field_is_opened()
    {
        var result = CompileSource("public static class Holder { private static object _value; }");

        var field = Field(result, "Holder", "_value");
        Assert.True(field.IsStatic);
        Assert.Equal(Accessibility.Internal, field.DeclaredAccessibility);
    }

    [Fact]
    public void A_field_of_a_private_nested_type_is_reachable()
    {
        var result = CompileSource("public class Outer { private sealed class Inner { public static object Value; } }");

        var inner = result.Compilation!.GetTypeByMetadataName("Outer+Inner")!;
        Assert.Equal(Accessibility.Internal, inner.DeclaredAccessibility);
        Assert.Contains("F:Outer.Inner.Value", result.OpenedFields);
        AssertDriverCompiles(result, "public static class Driver { public static void Set(object value) { Outer.Inner.Value = value; } }");
    }

    [Fact]
    public void A_get_only_override_whose_added_setter_has_no_base_accessor_is_reverted()
    {
        var result = CompileSource("public abstract class Base { public abstract object P { get; } } public sealed class Holder : Base { public override object P { get; } }");

        Assert.Null(result.Reason);
        var property = Property(result, "Holder", "P");
        Assert.Equal(Accessibility.Public, property.DeclaredAccessibility);
        Assert.Null(property.SetMethod);
        Assert.DoesNotContain("P:Holder.P", result.OpenedFields);
        AssertNoErrors(result);
    }

    [Fact]
    public void An_opening_that_changes_name_binding_and_fails_is_reverted()
    {
        var result = CompileSource("public class Base { private static object Missing; } public sealed class Derived : Base { public int Read() => Missing.Value; } public static class Missing { public static int Value => 1; }");

        Assert.Null(result.Reason);
        Assert.Equal(Accessibility.Private, Field(result, "Base", "Missing").DeclaredAccessibility);
        Assert.DoesNotContain("F:Base.Missing", result.OpenedFields);
        AssertNoErrors(result);
    }

    [Fact]
    public void A_private_dictionary_with_a_seed_key_axis_is_opened()
    {
        var result = CompileSource("public sealed class Holder { private System.Collections.Generic.Dictionary<object, int> _values = new(); }");

        Assert.Equal(Accessibility.Internal, Field(result, "Holder", "_values").DeclaredAccessibility);
        Assert.Contains("F:Holder._values", result.OpenedFields);
    }

    [Fact]
    public void A_field_of_a_private_protected_nested_type_is_reachable()
    {
        var result = CompileSource("public class Outer { private protected class Inner { public static object Value; } }");

        Assert.Equal(Accessibility.Internal, result.Compilation!.GetTypeByMetadataName("Outer+Inner")!.DeclaredAccessibility);
        AssertDriverCompiles(result, "public static class Driver { public static void Set(object value) { Outer.Inner.Value = value; } }");
    }

    [Fact]
    public void An_init_auto_property_is_changed_to_a_setter()
    {
        var result = CompileSource("public sealed class Holder { private object P { get; init; } }");

        Assert.False(Property(result, "Holder", "P").SetMethod!.IsInitOnly);
        AssertDriverCompiles(result, "public static class Driver { public static void Set(Holder h, object value) { h.P = value; } }");
    }

    [Fact]
    public void A_field_like_event_is_untouched()
    {
        var result = CompileSource("public sealed class Holder { private event System.Action Changed; }");

        var @event = result.Compilation!.GetTypeByMetadataName("Holder")!.GetMembers("Changed").OfType<IEventSymbol>().Single();
        Assert.Equal(Accessibility.Private, @event.DeclaredAccessibility);
        Assert.Empty(result.OpenedFields);
    }

    [Fact]
    public void Installed_System_Linq_has_the_same_body_counts_and_no_new_errors_with_field_opening()
    {
        var resolution = ImplementationAssemblies.ForThisProcess().Resolve("System.Linq", Environment.Version.Major.ToString(), null, null);
        Assert.True(resolution.Reason is null, resolution.Detail);
        var module = LibraryCompilation.Decompile(resolution.Assembly!, CancellationToken.None);
        var references = resolution.Assembly!.References.Select(reference => MetadataReference.CreateFromFile(reference)).ToArray();

        var closed = LibraryCompilation.CompileTrees("System.Linq", module.Trees, references, CancellationToken.None, openFields: false);
        var opened = LibraryCompilation.CompileTrees("System.Linq", module.Trees, references, CancellationToken.None, openFields: true);

        Assert.Null(closed.Reason);
        Assert.Null(opened.Reason);
        Assert.Equal(closed.Bodies, opened.Bodies);
        Assert.Equal(closed.ExternBodies, opened.ExternBodies);
        Assert.Empty(closed.OpenedFields);
        Assert.All(opened.Compilation!.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                   diagnostic => Assert.Contains(diagnostic.Id, LibraryCompilation.BenignDeclarationErrors));
    }

    private static LibraryCompilationResult CompileSource(string source, bool openFields = true) =>
        LibraryCompilation.CompileTrees("Fixture.Library",
                                        [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), "Fixture.Library/Library.cs")],
                                        EmittedAssemblies.RuntimeReferences, CancellationToken.None, openFields: openFields);

    private static IFieldSymbol Field(LibraryCompilationResult result, string type, string name) =>
        result.Compilation!.GetTypeByMetadataName(type)!.GetMembers(name).OfType<IFieldSymbol>().Single();

    private static IPropertySymbol Property(LibraryCompilationResult result, string type, string name) =>
        result.Compilation!.GetTypeByMetadataName(type)!.GetMembers(name).OfType<IPropertySymbol>().Single();

    private static void AssertDriverCompiles(LibraryCompilationResult library, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), "ConcurrencyHunter.ModelDriver/Driver.cs");
        var driver = CSharpCompilation.Create(DriverSynthesizer.ASSEMBLY, [tree],
                                              EmittedAssemblies.RuntimeReferences.Append(library.Compilation!.ToMetadataReference()),
                                              new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(driver.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    private static void AssertNoErrors(LibraryCompilationResult result) =>
        Assert.Empty(result.Compilation!.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
}
