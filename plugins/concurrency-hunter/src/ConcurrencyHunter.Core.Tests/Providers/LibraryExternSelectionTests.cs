using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class LibraryExternSelectionTests
{
    private const string SOURCE = """
        using System;
        public class Box
        {
            public virtual int Compute() => 1;
            public virtual int Value { get => 1; set { Compute(); } }
            public virtual int this[int i] { get => i; set { Compute(); } }
            public virtual event Action Changed { add { Compute(); } remove { Compute(); } }
            public int Plain() => 2;
            public int plain() => 3;
        }
        """;

    [Fact]
    public void Selected_members_compile_without_a_body()
    {
        using var install = new GenerationInstall();
        var assembly = Assembly(install);
        var selected = new HashSet<string>(StringComparer.Ordinal) { "M:Box.Compute", "P:Box.Value", "P:Box.Item(System.Int32)", "E:Box.Changed" };
        var result = LibraryCompilation.Compile(assembly, selected, CancellationToken.None);
        Assert.Null(result.Reason);
        Assert.Empty(result.Compilation!.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var box = result.Compilation.GetTypeByMetadataName("Box")!;
        foreach (var member in box.GetMembers().Where(member => selected.Contains(member.GetDocumentationCommentId() ?? "")))
        {
            var syntax = Assert.Single(member.DeclaringSyntaxReferences).GetSyntax();
            Assert.Empty(syntax.DescendantNodes().OfType<BlockSyntax>());
            Assert.Empty(syntax.DescendantNodes().OfType<ArrowExpressionClauseSyntax>());
        }
        Assert.Contains("M:Box.Compute", result.ExternMembers);
    }

    [Fact]
    public void Property_selected_through_one_accessor_loses_every_accessor_body()
    {
        using var install = new GenerationInstall();
        var assembly = Assembly(install);
        var baseline = LibraryCompilation.Compile(assembly, CancellationToken.None).Compilation!;
        var property = (IPropertySymbol)baseline.GetTypeByMetadataName("Box")!.GetMembers("Value").Single();
        var selected = new HashSet<string>(StringComparer.Ordinal) { property.GetMethod!.AssociatedSymbol!.GetDocumentationCommentId()! };
        var result = LibraryCompilation.Compile(assembly, selected, CancellationToken.None);
        Assert.Null(result.Reason);
        var rewritten = (IPropertySymbol)result.Compilation!.GetTypeByMetadataName("Box")!.GetMembers("Value").Single();
        var syntax = (PropertyDeclarationSyntax)Assert.Single(rewritten.DeclaringSyntaxReferences).GetSyntax();
        Assert.Equal(2, syntax.AccessorList!.Accessors.Count);
        Assert.All(syntax.AccessorList.Accessors, accessor => { Assert.Null(accessor.Body); Assert.Null(accessor.ExpressionBody); });
    }

    [Fact]
    public void Unselected_members_keep_their_bodies()
    {
        using var install = new GenerationInstall();
        var assembly = Assembly(install);
        var result = LibraryCompilation.Compile(assembly, new HashSet<string>(StringComparer.Ordinal) { "M:Box.Compute" }, CancellationToken.None);
        Assert.Null(result.Reason);
        var plain = result.Compilation!.GetTypeByMetadataName("Box")!.GetMembers("Plain").Single();
        var syntax = (MethodDeclarationSyntax)Assert.Single(plain.DeclaringSyntaxReferences).GetSyntax();
        Assert.True(syntax.Body is not null || syntax.ExpressionBody is not null);
        Assert.DoesNotContain("M:Box.Plain", result.ExternMembers);
    }

    [Fact]
    public void Selection_is_part_of_the_cache_key()
    {
        using var install = new GenerationInstall();
        var assembly = Assembly(install);
        var baseline = LibraryCompilation.Compile(assembly, CancellationToken.None);
        var selected = new HashSet<string>(StringComparer.Ordinal) { "M:Box.Compute", "P:Box.Value" };
        var first = LibraryCompilation.Compile(assembly, selected, CancellationToken.None);
        var reversed = new HashSet<string>(StringComparer.Ordinal) { "P:Box.Value", "M:Box.Compute" };
        Assert.Same(first, LibraryCompilation.Compile(assembly, reversed, CancellationToken.None));
        Assert.NotSame(baseline, first);
        Assert.NotSame(first, LibraryCompilation.Compile(assembly, new HashSet<string>(StringComparer.Ordinal) { "M:Box.Compute" }, CancellationToken.None));
        Assert.Same(baseline, LibraryCompilation.Compile(assembly, CancellationToken.None));
        var upper = LibraryCompilation.Compile(assembly, new HashSet<string>(StringComparer.Ordinal) { "M:Box.Plain" }, CancellationToken.None);
        var lower = LibraryCompilation.Compile(assembly, new HashSet<string>(StringComparer.Ordinal) { "M:Box.plain" }, CancellationToken.None);
        Assert.NotSame(upper, lower);
        Assert.Contains("M:Box.Plain", upper.ExternMembers);
        Assert.DoesNotContain("M:Box.plain", upper.ExternMembers);
        Assert.Contains("M:Box.plain", lower.ExternMembers);
    }

    private static ImplementationAssembly Assembly(GenerationInstall install)
    {
        var framework = install.SharedFramework("8.0.1", runtime: true);
        var path = EmittedAssemblies.Write(Path.Combine(install.Root, "Fixture.Extern.dll"), "Fixture.Extern", SOURCE);
        return new ImplementationAssembly("Fixture.Extern", path, null, "8.0.1", "net8.0", framework,
                                           EmittedAssemblies.RUNTIME_ASSEMBLIES.Select(name => Path.Combine(framework, name + ".dll")).ToArray(),
                                           [], "1.0.0.0", "1.0.0.0", Guid.Empty);
    }
}
