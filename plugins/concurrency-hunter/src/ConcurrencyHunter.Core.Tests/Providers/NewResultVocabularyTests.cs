using System.Text;
using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class NewResultVocabularyTests
{
    [Fact]
    public void New_inside_one_of_is_refused()
    {
        Assert.Null(LibraryResult.Parse("[new]"));
        Assert.Null(LibraryValue.Parse("new"));
        Assert.Throws<LibraryModelException>(() => BuiltInModelReader.Read(Encoding.UTF8.GetBytes(File("Get", "[new]", builtIn: true))));
    }

    [Fact]
    public void New_on_a_void_member_is_refused()
    {
        var (_, rejections) = Resolve(File("Make", "new"));
        Assert.Contains("void", Assert.Single(rejections).Reason);
    }

    [Fact]
    public void Entries_differing_only_in_new_disagree()
    {
        var (models, rejections) = Resolve(File("Get", "new", duplicate: true));
        Assert.Equal(2, rejections.Count);
        Assert.All(rejections, rejection => Assert.Contains("disagree", rejection.Reason));
        Assert.True(Assert.Single(models.Members, member => member.Id.Contains("NewVocabulary.Lib.Get", StringComparison.Ordinal)).DeclaredOpaque);
    }

    [Fact]
    public void New_as_a_result_and_an_output_loads_in_both_layers()
    {
        Assert.Equal("new", LibraryResult.Parse("new")?.ToString());
        var (_, rejections) = Resolve(File("Make", null, output: true));
        Assert.Empty(rejections);
        var member = Assert.Single(BuiltInModelReader.Read(Encoding.UTF8.GetBytes(File("Get", "new", builtIn: true))).Members);
        Assert.Equal("new", member.Result?.ToString());
        var make = Assert.Single(BuiltInModelReader.Read(Encoding.UTF8.GetBytes(File("Make", null, builtIn: true, output: true))).Members);
        Assert.Equal("new", make.Outputs["value"].ToString());
    }

    private static string File(string method, string? result, bool builtIn = false, bool duplicate = false, bool output = false)
    {
        var id = method == "Get" ? "M:NewVocabulary.Lib.Get(System.Object)" : "M:NewVocabulary.Lib.Make(System.Object@)";
        var entry = "{\"member\":\"" + id + "\",\"effects\":{}" + (result is null ? "" : ",\"result\":\"" + result + "\"") +
                    (output ? ",\"outputs\":{\"value\":\"new\"}" : "") + "}";
        return "{\"schemaVersion\":1,\"assemblies\":[\"NewVocabulary\"]," +
            (builtIn ? "\"versions\":{\"minimum\":\"1.0.0.0\",\"maximumExclusive\":\"2.0.0.0\"}," : "") + "\"models\":[" + entry +
            (duplicate ? "," + entry.Replace("\"new\"", "\"[arg:value]\"", StringComparison.Ordinal) : "") + "]}";
    }

    private static (LibraryModels Models, IReadOnlyList<ModelRejection> Rejections) Resolve(string text)
    {
        using var repo = new CellModelRepository(text);
        var source = CSharpCompilation.Create("NewVocabulary", [CSharpSyntaxTree.ParseText("""
            namespace NewVocabulary;
            public static class Lib
            {
                public static object Get(object value) => null!;
                public static void Make(out object value) { value = null!; }
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        Assert.True(source.Emit(bytes).Success);
        var compilation = CSharpCompilation.Create("Check", [], StubAssemblies.PlatformWithout([]).Append(MetadataReference.CreateFromImage(bytes.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var files = ProjectModelFiles.Read(repo.Root);
        return ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(repo.Root, files));
    }
}
