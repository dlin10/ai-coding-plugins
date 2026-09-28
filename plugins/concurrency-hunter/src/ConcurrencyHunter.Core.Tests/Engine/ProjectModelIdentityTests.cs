using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ProjectModelIdentityTests
{
    private const string JSON_MEMBER = "M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes``1(``0,System.Text.Json.JsonSerializerOptions)~System.Byte[]";
    private const string TINY_MEMBER = "M:Shared.Api.Run(System.Int32)";

    [Fact]
    public void Project_opaque_range_does_not_override_other_referenced_version()
    {
        var nine = JsonCompilation(9);
        var ten = JsonCompilation(10);
        var entry = Entry(JSON_MEMBER, ["System.Text.Json"], (VersionOf(9), VersionOf(10)), [], opaque: true);

        var (models, rejections) = Resolve([entry], nine, ten);

        Assert.Empty(rejections);
        Assert.Equal((LibraryMatchKind.Opaque, ModelLayer.Project), KindAndLayer(models.Find(Method(nine, JSON_MEMBER))));
        Assert.Equal((LibraryMatchKind.Known, ModelLayer.BuiltIn), KindAndLayer(models.Find(Method(ten, JSON_MEMBER))));
    }

    [Fact]
    public void Same_id_in_two_assemblies_keeps_distinct_effects()
    {
        var first = TinyCompilation("TinyFirst", 1, "value");
        var second = TinyCompilation("TinySecond", 1, "value");
        var entries = new[]
        {
            Entry(TINY_MEMBER, ["TinyFirst"], null, [LibraryEffect.DeepReadOf("value")]),
            Entry(TINY_MEMBER, ["TinySecond"], null, [LibraryEffect.WriteOf("value")])
        };

        var (models, rejections) = Resolve(entries, first, second);

        Assert.Empty(rejections);
        Assert.Equal([LibraryEffect.DeepReadOf("value")], models.Find(Method(first, TINY_MEMBER))?.Effects);
        Assert.Equal([LibraryEffect.WriteOf("value")], models.Find(Method(second, TINY_MEMBER))?.Effects);
    }

    [Fact]
    public void Same_id_in_two_versions_keeps_each_project_range()
    {
        var first = TinyCompilation("Tiny", 1, "value");
        var second = TinyCompilation("Tiny", 2, "value");
        var entries = new[]
        {
            Entry(TINY_MEMBER, ["Tiny"], (VersionOf(1), VersionOf(2)), []),
            Entry(TINY_MEMBER, ["Tiny"], (VersionOf(2), VersionOf(3)), [])
        };

        var (models, rejections) = Resolve(entries, first, second);

        Assert.Empty(rejections);
        Assert.Equal((LibraryMatchKind.Known, ModelLayer.Project), KindAndLayer(models.Find(Method(first, TINY_MEMBER))));
        Assert.Equal((LibraryMatchKind.Known, ModelLayer.Project), KindAndLayer(models.Find(Method(second, TINY_MEMBER))));
    }

    [Fact]
    public void Exact_entry_checks_each_assembly_parameter()
    {
        var first = TinyCompilation("TinyFirst", 1, "x");
        var second = TinyCompilation("TinySecond", 1, "y");
        var entry = Entry(TINY_MEMBER, ["TinyFirst", "TinySecond"], null, [LibraryEffect.DeepReadOf("x")]);

        var (models, rejections) = Resolve([entry], first, second);

        Assert.Single(rejections, rejection => rejection.Reason.Contains("missing parameter", StringComparison.Ordinal));
        Assert.Equal((LibraryMatchKind.Known, ModelLayer.Project), KindAndLayer(models.Find(Method(first, TINY_MEMBER))));
        Assert.Null(models.Find(Method(second, TINY_MEMBER)));
    }

    private static ProjectModelEntry Entry(string id, IReadOnlyList<string> assemblies, (Version Minimum, Version Maximum)? versions,
                                           IReadOnlyList<LibraryEffect> effects, bool opaque = false) =>
        new(".concurrency-hunter/models/model.json", 0, id, assemblies, versions, effects, opaque);

    private static (LibraryModels Models, IReadOnlyList<ModelRejection> Rejections) Resolve(
        IReadOnlyList<ProjectModelEntry> entries, params Compilation[] compilations)
    {
        using var folder = new TemporaryDirectory();
        var files = new ProjectModelFiles(entries, [], new HashSet<string>(StringComparer.Ordinal), false);
        return ProjectModelResolver.Resolve(files, compilations, ModelLock.Read(folder.Path, files));
    }

    private static (LibraryMatchKind Kind, ModelLayer Layer)? KindAndLayer(LibraryMatch? match) =>
        match is null ? null : (match.Kind, match.Layer);

    private static Version VersionOf(int major) => new(major, 0, 0, 0);

    private static IMethodSymbol Method(Compilation compilation, string id) =>
        Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation).OfType<IMethodSymbol>());

    private static Compilation JsonCompilation(int major) => LibraryCompilation("System.Text.Json", major, """
        namespace System.Text.Json
        {
            public sealed class JsonSerializerOptions { }
            public static class JsonSerializer
            {
                public static byte[] SerializeToUtf8Bytes<T>(T value, JsonSerializerOptions options = null) => [];
            }
        }
        """, "public class Client { public void Call() { System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(1); } }");

    private static Compilation TinyCompilation(string assemblyName, int major, string parameter)
    {
        return LibraryCompilation(assemblyName, major,
                                  $"namespace Shared {{ public static class Api {{ public static void Run(int {parameter}) {{ }} }} }}",
                                  "public class Client { public void Call() { Shared.Api.Run(1); } }");
    }

    private static Compilation LibraryCompilation(string assemblyName, int major, string source, string client)
    {
        var references = StubAssemblies.PlatformWithout([assemblyName]).ToArray();
        var version = $"[assembly: System.Reflection.AssemblyVersion(\"{major}.0.0.0\")]";
        var library = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(version + source)], references,
                                               new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        var emit = library.Emit(bytes);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        var compilation = CSharpCompilation.Create("Client", [CSharpSyntaxTree.ParseText(client)],
                                                   references.Append(MetadataReference.CreateFromImage(bytes.ToArray())),
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return compilation;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("ch-project-identity-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
