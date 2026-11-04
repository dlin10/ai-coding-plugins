using System.Runtime.InteropServices;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>An error whose id is not <c>CS</c> and digits, such as an <c>[Experimental]</c> or an obsoletion id, is no error of the
/// compiler: it does not make the library unusable, does not make a body extern, does not roll back the opening of a field and does
/// not break a driver, while an error of the compiler decides all of them as before (SPEC TD-034b, «Ошибки не компилятора»).</summary>
public sealed class NonCompilerErrorTests : IDisposable
{
    private const string LIBRARY = "Fixture.Library";
    private const string PREVIEW = "Fixture.Preview";

    private const string PREVIEW_SOURCE = """
        namespace Fixture.Preview
        {
            [System.Diagnostics.CodeAnalysis.Experimental("TEST0001")]
            public class Gadget
            {
                [System.Diagnostics.CodeAnalysis.Experimental("TEST0001")]
                public int Size() { return 1; }
            }

            [System.Obsolete("gone", true, DiagnosticId = "TEST0002")]
            public static class Legacy
            {
                public static int Run() { return 1; }
            }

            [System.Obsolete("gone", true, DiagnosticId = "TEST0003")]
            public class Relic
            {
            }
        }
        """;

    private const string SIGNATURE_SOURCE = """
        namespace Lib
        {
            public static class Api
            {
                public static Fixture.Preview.Gadget Make() { return new Fixture.Preview.Gadget(); }
                public static int Measure(Fixture.Preview.Gadget gadget) { return gadget.Size(); }
            }
        }
        """;

    private const string OBSOLETE_SIGNATURE_SOURCE = """
        namespace Lib
        {
            public static class Api
            {
                public static int Take(Fixture.Preview.Relic relic) { return 1; }
            }
        }
        """;

    private const string EXPERIMENTAL_BODY_SOURCE = """
        namespace Lib
        {
            public static class Api
            {
                public static int Measure() { return new Fixture.Preview.Gadget().Size(); }
                public static int Count() { return 2; }
            }
        }
        """;

    private const string OBSOLETE_BODY_SOURCE = """
        namespace Lib
        {
            public static class Api
            {
                public static int Run() { return Fixture.Preview.Legacy.Run(); }
                public static int Count() { return 2; }
            }
        }
        """;

    private const string BROKEN_BODY_SOURCE = """
        namespace Lib
        {
            public static class Api
            {
                public static int Measure() { return new Fixture.Preview.Gadget().Size(); }
                public static int Broken() { return Undefined.Value; }
            }
        }
        """;

    private const string BROKEN_DECLARATION_SOURCE = """
        namespace Lib
        {
            public static class Api
            {
                public static Fixture.Preview.Gadget Make() { return new Fixture.Preview.Gadget(); }
            }

            public class Broken
            {
                public int Value { private get; }
            }
        }
        """;

    // Opening Base.Missing makes the name in Derived.Read bind to the field instead of the static class: the use of a member of the
    // experimental type is then new in Derived.Read.
    private const string OPENING_EXPERIMENTAL_SOURCE = """
        namespace Lib
        {
            public class Base { private static Fixture.Preview.Gadget Missing; }
            public sealed class Derived : Base { public int Read() => Missing.Size(); }
            public static class Missing { public static int Size() => 1; }
        }
        """;

    // The same opening, whose new binding names a member the experimental type does not have: an error of the compiler.
    private const string OPENING_COMPILER_ERROR_SOURCE = """
        namespace Lib
        {
            public class Base { private static Fixture.Preview.Gadget Missing; }
            public sealed class Derived : Base { public int Read() => Missing.Value; }
            public static class Missing { public static int Value => 1; }
        }
        """;

    private static readonly IReadOnlyList<MetadataReference> PreviewReferences =
        EmittedAssemblies.RuntimeReferences.Append(MetadataReference.CreateFromImage(EmittedAssemblies.Image(PREVIEW, PREVIEW_SOURCE))).ToArray();

    private readonly GenerationInstall _install = new();

    public void Dispose() => _install.Dispose();

    [Theory]
    [InlineData("CS0246", DiagnosticSeverity.Error, true)]
    [InlineData("CS8051", DiagnosticSeverity.Error, true)]
    [InlineData("CS7036", DiagnosticSeverity.Error, true)]
    [InlineData("ASP0029", DiagnosticSeverity.Error, false)]
    [InlineData("SYSLIB5006", DiagnosticSeverity.Error, false)]
    [InlineData("TEST0001", DiagnosticSeverity.Error, false)]
    [InlineData("TEST0002", DiagnosticSeverity.Error, false)]
    [InlineData("CS", DiagnosticSeverity.Error, false)]
    [InlineData("CS12A4", DiagnosticSeverity.Error, false)]
    [InlineData("CSX0001", DiagnosticSeverity.Error, false)]
    [InlineData("cs0246", DiagnosticSeverity.Error, false)]
    [InlineData("CS٣٣", DiagnosticSeverity.Error, false)]
    [InlineData("CS0246", DiagnosticSeverity.Warning, false)]
    [InlineData("CS8600", DiagnosticSeverity.Warning, false)]
    [InlineData("CS0246", DiagnosticSeverity.Info, false)]
    [InlineData("CS0246", DiagnosticSeverity.Hidden, false)]
    public void An_error_of_the_compiler_is_an_error_with_an_id_of_CS_and_digits(string id, DiagnosticSeverity severity, bool expected) =>
        Assert.Equal(expected, LibraryCompilation.IsCompilerError(Diagnostic.Create(new DiagnosticDescriptor(id, "title", "message", "category", severity,
                                                                                                             isEnabledByDefault: true), Location.None)));

    [Fact]
    public void A_library_whose_public_signature_uses_an_experimental_type_compiles_by_both_routes()
    {
        var assembly = Lay(SIGNATURE_SOURCE);

        var recompiled = Recompiled(assembly);
        var compiled = LibraryCompilation.Compile(assembly, CancellationToken.None);

        foreach (var result in new[] { recompiled, compiled })
        {
            Assert.True(result.Reason is null, $"{result.Reason}: {string.Join(", ", result.Errors)}");
            Assert.Empty(result.Errors);
            Assert.Empty(result.ExternMembers);
            Assert.Equal(0, result.ExternBodies);
            Assert.Contains(result.Compilation!.GetDiagnostics(), diagnostic => diagnostic.Id == "TEST0001" && diagnostic.Severity == DiagnosticSeverity.Error);
        }
    }

    [Fact]
    public void A_body_that_uses_an_experimental_member_stays_a_body()
    {
        var result = CompileSource(EXPERIMENTAL_BODY_SOURCE);

        Assert.True(result.Reason is null, $"{result.Reason}: {string.Join(", ", result.Errors)}");
        Assert.DoesNotContain("M:Lib.Api.Measure", result.ExternMembers);
        Assert.Empty(result.ExternMembers);
        Assert.Equal(0, result.ExternBodies);
        Assert.Contains(result.Compilation!.GetDiagnostics(), diagnostic => diagnostic.Id == "TEST0001");
        Assert.False(Method(result, "Measure").IsExtern);
    }

    [Fact]
    public void A_body_that_uses_a_member_obsolete_as_an_error_with_an_id_of_its_own_stays_a_body()
    {
        var result = CompileSource(OBSOLETE_BODY_SOURCE);

        Assert.True(result.Reason is null, $"{result.Reason}: {string.Join(", ", result.Errors)}");
        Assert.Empty(result.ExternMembers);
        Assert.Equal(0, result.ExternBodies);
        Assert.Contains(result.Compilation!.GetDiagnostics(), diagnostic => diagnostic.Id == "TEST0002" && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.False(Method(result, "Run").IsExtern);
    }

    [Fact]
    public void A_body_with_an_error_of_the_compiler_is_still_made_extern_beside_a_body_with_an_experimental_use()
    {
        var result = CompileSource(BROKEN_BODY_SOURCE);

        Assert.True(result.Reason is null, $"{result.Reason}: {string.Join(", ", result.Errors)}");
        Assert.Equal("M:Lib.Api.Broken", Assert.Single(result.ExternMembers));
        Assert.Equal(1, result.ExternBodies);
        Assert.True(Method(result, "Broken").IsExtern);
        Assert.False(Method(result, "Measure").IsExtern);
    }

    [Fact]
    public void An_error_of_the_compiler_still_refuses_the_library_and_Errors_names_it_without_the_experimental_one()
    {
        var result = CompileSource(BROKEN_DECLARATION_SOURCE);

        Assert.Equal(GenerationReasons.LIBRARY_DOES_NOT_COMPILE, result.Reason);
        Assert.Null(result.Compilation);
        Assert.Contains("CS0276", result.Errors);
        Assert.DoesNotContain("TEST0001", result.Errors);
        Assert.All(result.Errors, id => Assert.Matches("^CS[0-9]+$", id));
    }

    [Fact]
    public void Opening_a_field_is_not_rolled_back_by_an_error_that_is_not_the_compilers()
    {
        var result = CompileSource(OPENING_EXPERIMENTAL_SOURCE);

        Assert.True(result.Reason is null, $"{result.Reason}: {string.Join(", ", result.Errors)}");
        Assert.Contains("F:Lib.Base.Missing", result.OpenedFields);
        Assert.Equal(Accessibility.Internal, result.Compilation!.GetTypeByMetadataName("Lib.Base")!.GetMembers("Missing").OfType<IFieldSymbol>().Single().DeclaredAccessibility);
        Assert.Contains(result.Compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "TEST0001" && diagnostic.Location.SourceTree is { } tree &&
            tree.GetRoot().FindNode(diagnostic.Location.SourceSpan).AncestorsAndSelf().OfType<MethodDeclarationSyntax>().Any(method => method.Identifier.ValueText == "Read"));
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), LibraryCompilation.IsCompilerError);
    }

    [Fact]
    public void Opening_a_field_is_still_rolled_back_by_an_error_of_the_compiler_beside_an_experimental_type()
    {
        var result = CompileSource(OPENING_COMPILER_ERROR_SOURCE);

        Assert.True(result.Reason is null, $"{result.Reason}: {string.Join(", ", result.Errors)}");
        Assert.DoesNotContain("F:Lib.Base.Missing", result.OpenedFields);
        Assert.Equal(Accessibility.Private, result.Compilation!.GetTypeByMetadataName("Lib.Base")!.GetMembers("Missing").OfType<IFieldSymbol>().Single().DeclaredAccessibility);
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), LibraryCompilation.IsCompilerError);
    }

    [Fact]
    public void The_driver_of_a_member_that_takes_an_experimental_type_is_synthesized_and_compiles()
    {
        var driver = Drive("M:Lib.Api.Measure(Fixture.Preview.Gadget)", CompileSource(SIGNATURE_SOURCE));

        // The driver names the experimental type, and its own `#pragma warning disable` leaves nothing of it among the errors.
        Assert.Contains("global::Fixture.Preview.Gadget", driver.Source);
        Assert.Empty(driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        // `[Obsolete(..., true, DiagnosticId = ...)]` reports an error the driver's pragma does not cover, so this driver's own compilation
        // does carry an error that is not the compiler's, and Synthesize must still build the driver: it keeps only the compiler's errors.
        var obsolete = Drive("M:Lib.Api.Take(Fixture.Preview.Relic)", CompileSource(OBSOLETE_SIGNATURE_SOURCE));
        var diagnostics = obsolete.Compilation.GetDiagnostics();
        Assert.Contains("global::Fixture.Preview.Relic", obsolete.Source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "TEST0003" && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, LibraryCompilation.IsCompilerError);
    }

    /// <summary>A fixture library compiled from source against the runtime and the preview assembly, as the decompiler's output would be.</summary>
    /// <param name="source">The source.</param>
    private static LibraryCompilationResult CompileSource(string source) =>
        LibraryCompilation.CompileTrees(LIBRARY, [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), $"{LIBRARY}/Library.cs")],
                                        PreviewReferences, CancellationToken.None);

    /// <summary>A library emitted into the install with <c>TEST0001</c> suppressed, as the resolver would describe it, against the
    /// running runtime and the preview assembly.</summary>
    /// <param name="source">The library's source.</param>
    private ImplementationAssembly Lay(string source)
    {
        var preview = EmittedAssemblies.Image(PREVIEW, PREVIEW_SOURCE);
        var previewPath = EmittedAssemblies.Write(Path.Combine(_install.Root, "preview", PREVIEW + ".dll"), PREVIEW, PREVIEW_SOURCE);
        var path = EmittedAssemblies.Write(Path.Combine(_install.Root, "library", LIBRARY + ".dll"), LIBRARY, source, references: [preview],
                                           suppressedDiagnostics: ["TEST0001"]);
        var runtime = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        return new ImplementationAssembly(LIBRARY, path, null, "1.0.0", $"net{Environment.Version.Major}.0", runtime,
                                          EmittedAssemblies.RUNTIME_ASSEMBLIES.Select(reference => Path.Combine(runtime, reference + ".dll"))
                                                           .Append(previewPath).ToArray(),
                                          [], "0.0.0.0", "", Guid.Empty);
    }

    /// <summary>The decompiled assembly compiled by the route a generation takes, without the cache.</summary>
    /// <param name="assembly">The assembly.</param>
    private static LibraryCompilationResult Recompiled(ImplementationAssembly assembly) =>
        LibraryCompilation.CompileModule(assembly.AssemblyName, LibraryCompilation.Decompile(assembly, CancellationToken.None),
                                         assembly.References.Select(reference => (MetadataReference)MetadataReference.CreateFromFile(reference)).ToArray(),
                                         CancellationToken.None);

    /// <summary>The driver of a member, which must be synthesized.</summary>
    /// <param name="id">The member's documentation id.</param>
    /// <param name="library">The compiled library.</param>
    private static Driver Drive(string id, LibraryCompilationResult library)
    {
        Assert.True(library.Reason is null && library.Compilation is not null, string.Join(", ", library.Errors));
        var trace = ModelGenerator.Trace(new GenerationRequest(LIBRARY, "1.0", id, null, null), library, CancellationToken.None);
        Assert.True(trace.Driver is not null, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        return trace.Driver;
    }

    /// <summary>A method of <c>Lib.Api</c> in the compiled library.</summary>
    /// <param name="result">The compiled library.</param>
    /// <param name="name">The method's name.</param>
    private static IMethodSymbol Method(LibraryCompilationResult result, string name) =>
        result.Compilation!.GetTypeByMetadataName("Lib.Api")!.GetMembers(name).OfType<IMethodSymbol>().Single();
}
