using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class RecipeStorageOpeningMatrixTests
{
    public enum Storage { SeedField, ReadonlyPathField, UnopenedField, GetOnlyAutoProperty, StructWithReferences, StringField, InstanceField,
                          RevertingOpening, InterfaceStaticField, PublicWritableField }
    public enum Copy { Shared, Off }

    public static IEnumerable<object[]> Cells() =>
        from storage in Enum.GetValues<Storage>()
        from copy in Enum.GetValues<Copy>()
        select new object[] { storage, copy };

    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_opens_as_expected(Storage storage, Copy copy)
    {
        var declaration = storage switch
        {
            Storage.SeedField => "private static readonly Base X;",
            Storage.ReadonlyPathField => "private static readonly Holder X;",
            Storage.UnopenedField => "private static readonly Bag X;",
            Storage.GetOnlyAutoProperty => "private static Box X { get; }",
            Storage.StructWithReferences => "private static readonly Pair X;",
            Storage.StringField => "private static readonly string X;",
            Storage.InstanceField => "private readonly Box X;",
            Storage.RevertingOpening => "private static readonly Box Missing;",
            Storage.InterfaceStaticField => "private static Box X;",
            Storage.PublicWritableField => "public static Box X;",
            _ => throw new ArgumentOutOfRangeException(nameof(storage))
        };
        var source = "public class Base { } public sealed class Box { } public sealed class Holder { public System.IDisposable Value; } " +
                     "public sealed class Bag : System.Collections.IEnumerable { public System.Collections.IEnumerator GetEnumerator() => null; } " +
                     "public struct Pair { public Box Value; public void Touch() { Value = null; } } " +
                     (storage == Storage.InterfaceStaticField ? "public interface Api { " : "public class Api { ") + declaration + " } " +
                     (storage == Storage.RevertingOpening ? "public class Derived : Api { public int Read() => Missing.Value; } public static class Missing { public static int Value => 1; }" : "");
        var shared = GenerationRuns.Compile(source);
        Assert.NotNull(shared.Compilation);
        var library = copy == Copy.Shared ? shared : LibraryCompilation.RecipeCopy(shared, new GenerationChecks(CancellationToken.None));
        var name = storage == Storage.RevertingOpening ? "Missing" : "X";
        var symbol = library.Compilation!.GetTypeByMetadataName("Api")!.GetMembers(name).Single();
        var original = shared.Compilation!.GetTypeByMetadataName("Api")!.GetMembers(name).Single();
        Assert.Equal(LibraryFieldOpening.DeclaredReadOnly(original), LibraryFieldOpening.DeclaredReadOnly(symbol));
        if (symbol is IFieldSymbol field)
        {
            Assert.Equal(IrLowering.FieldRef((IFieldSymbol)original).IsReadOnly, IrLowering.FieldRef(field).IsReadOnly);
            var index = ProgramIndexBuilder.Build("s", [library.Compilation], "C:/fixture", CancellationToken.None);
            Assert.Equal(LibraryFieldOpening.DeclaredReadOnly(original), index.Fields.Single(field => field.ContainingTypeKey.EndsWith(":Api", StringComparison.Ordinal) && field.Name == name).IsReadOnly);
        }
        else
        {
            Assert.Equal(IrLowering.PropertyField((IPropertySymbol)original).IsReadOnly, IrLowering.PropertyField((IPropertySymbol)symbol).IsReadOnly);
            if (copy == Copy.Off && ((IPropertySymbol)symbol).SetMethod is { } setter)
                Assert.Null(DriverSynthesizer.FindMember(library.Compilation, setter.GetDocumentationCommentId()!));
        }
        var eligible = storage is not (Storage.StringField or Storage.InstanceField or Storage.RevertingOpening or Storage.InterfaceStaticField);
        var writable = copy == Copy.Off && eligible || storage is Storage.SeedField or Storage.PublicWritableField;
        var target = storage == Storage.InstanceField ? "new Api().X" : "Api." + name;
        var driver = CSharpCompilation.Create(DriverSynthesizer.ASSEMBLY,
            [CSharpSyntaxTree.ParseText("public static class Driver { public static void Set() { " + target + " = default; } }")],
            EmittedAssemblies.RuntimeReferences.Append(library.Compilation.ToMetadataReference()),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Equal(writable, !driver.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        if (storage is Storage.PublicWritableField or Storage.RevertingOpening)
            Assert.DoesNotContain(symbol.GetDocumentationCommentId()!, library.OpenedFields);
        if (copy == Copy.Shared || !eligible)
            Assert.Equal(original.ToDisplayString(), symbol.ToDisplayString());
        if (copy == Copy.Off && eligible && storage != Storage.PublicWritableField)
        {
            Assert.Equal(Accessibility.Internal, symbol.DeclaredAccessibility);
            Assert.Contains(symbol.GetDocumentationCommentId()!, library.OpenedFields);
        }
        if (storage == Storage.StructWithReferences)
        {
            // Lowering must still take a defensive copy for a mutating call on readonly struct storage.
            Assert.Equal(StructCalls(shared), StructCalls(library));
        }
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        var cells = Cells().ToArray();
        Assert.Equal(Enum.GetValues<Storage>(), cells.Select(cell => (Storage)cell[0]).Distinct().Order());
        Assert.Equal(Enum.GetValues<Copy>(), cells.Select(cell => (Copy)cell[1]).Distinct().Order());
    }

    [Fact]
    public void Every_writable_off_storage_gets_its_recipe_value()
    {
        foreach (var storage in Enum.GetValues<Storage>())
        {
            var declaration = storage switch
            {
                Storage.SeedField => "private static readonly Base X;",
                Storage.ReadonlyPathField => "private static readonly Holder X;",
                Storage.UnopenedField => "private static readonly Bag X;",
                Storage.GetOnlyAutoProperty => "private static Box X { get; }",
                Storage.StructWithReferences => "private static readonly Pair X;",
                Storage.StringField => "private static readonly string X;",
                Storage.InstanceField => "private readonly Box X;",
                Storage.RevertingOpening => "private static readonly Box Missing;",
                Storage.InterfaceStaticField => "private static Box X;",
                _ => "public static Box X;"
            };
            var shared = GenerationRuns.Compile("public class Base { } public sealed class Box { } public sealed class Holder { public System.IDisposable Value; } " +
                "public sealed class Bag : System.Collections.IEnumerable { public System.Collections.IEnumerator GetEnumerator() => null; } " +
                "public struct Pair { public Box Value; public Pair(Box value) { Value = value; } } " +
                (storage == Storage.InterfaceStaticField ? "public interface Api { " : "public class Api { ") + declaration +
                " public static void Run(System.Action a) { a(); } } " +
                (storage == Storage.RevertingOpening ? "public class Derived : Api { public int Read() => Missing.Value; } public static class Missing { public static int Value => 1; }" : ""));
            var copy = LibraryCompilation.RecipeCopy(shared, new GenerationChecks(CancellationToken.None));
            var name = storage == Storage.RevertingOpening ? "Missing" : "X";
            var member = DriverSynthesizer.FindMember(copy.Compilation!, "M:Api.Run(System.Action)")!;
            var synthesis = DriverSynthesizer.Synthesize(copy.Compilation!, member, copy.ExternMembers, copy.OpenedFields,
                new HashSet<(string, string)>(), new ComparisonWitnesses(), new GenerationChecks(CancellationToken.None), copy.ReplacedMembers,
                new HashSet<(string, string)> { (GenerationRuns.ASSEMBLY + ":Api", name) });
            Assert.True(synthesis.Driver is not null, $"{storage}: {synthesis.Reason}: {synthesis.Detail}");
            var driver = synthesis.Driver;
            var expected = storage is not (Storage.StringField or Storage.InstanceField or Storage.RevertingOpening or Storage.InterfaceStaticField);
            Assert.True(expected == (driver.RecipeStatements.Count == 1), $"{storage}: {driver.Source}");
            if (storage is Storage.RevertingOpening or Storage.InterfaceStaticField)
                Assert.Single(driver.UnseededRecipeStatics);
            if (storage == Storage.StructWithReferences)
            {
                Assert.Contains("new global::Pair(new global::Box())", Assert.Single(driver.RecipeStatements).Operation.Syntax.ToString());
                Assert.DoesNotContain("default(global::Pair)", driver.Source);
            }
        }
    }

    private static string[] StructCalls(LibraryCompilationResult library)
    {
        // Bind against the library copy, as the driver's compilation does.
        var tree = CSharpSyntaxTree.ParseText("public static class Read { public static void M() { Api.X.Touch(); } }", path: "C:/fixture/Read.cs");
        var compilation = CSharpCompilation.Create(DriverSynthesizer.ASSEMBLY, [tree],
            EmittedAssemblies.RuntimeReferences.Append(library.Compilation!.ToMetadataReference()),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var method = compilation.GetTypeByMetadataName("Read")!.GetMembers("M").OfType<IMethodSymbol>().Single();
        return IrLowering.Lower(method, compilation, "C:/fixture", CancellationToken.None).Body.Blocks.SelectMany(block => block.Operations)
                         .Select(operation => operation.GetType().Name).ToArray();
    }
}
