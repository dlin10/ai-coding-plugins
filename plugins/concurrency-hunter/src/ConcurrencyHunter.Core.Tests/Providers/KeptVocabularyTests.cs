using System.Text;
using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class KeptVocabularyTests
{
    [Fact]
    public void Kept_result_is_refused() => Refused("\"result\":\"[kept:result]\"", "kept:result is not allowed");

    [Fact]
    public void Result_keeper_without_new_is_refused() => Refused("\"keeps\":{\"result\":[\"arg:value\"]}", "new");

    [Fact]
    public void Keeper_naming_a_missing_parameter_is_refused() => Refused("\"keeps\":{\"missing\":[\"arg:value\"]}", "parameter");

    [Fact]
    public void This_keeper_on_a_static_member_is_refused() => Refused("\"keeps\":{\"this\":[\"arg:value\"]}", "instance");

    [Fact]
    public void Entries_differing_only_in_keeps_disagree()
    {
        var entry = Entry("\"keeps\":{\"cache\":[\"arg:value\"]}");
        var (models, rejections) = Resolve(File(entry + "," + Entry("\"keeps\":{\"cache\":[\"arg:wrong\"]}")));
        Assert.Equal(2, rejections.Count);
        Assert.All(rejections, rejection => Assert.Contains("disagree", rejection.Reason));
        Assert.True(Assert.Single(models.Members, member => member.Id.StartsWith("M:KeptVocabulary.Lib.", StringComparison.Ordinal)).DeclaredOpaque);
    }

    [Fact]
    public void Built_in_entry_with_keeps_and_kept_loads_and_reaches_the_match()
    {
        var file = File(Entry("\"keeps\":{\"cache\":[\"arg:value\"]},\"result\":\"[kept:cache]\""), builtIn: true);
        var member = Assert.Single(BuiltInModelReader.Read(Encoding.UTF8.GetBytes(file)).Members);
        Assert.Equal("[kept:cache]", member.Result!.ToString());
        var compilation = CreateCompilation();
        var method = Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(member.Id, compilation).OfType<IMethodSymbol>());
        var match = new LibraryModels([member], []).Find(method);
        Assert.Equal("[kept:cache]", match!.Result!.ToString());
        Assert.Single(match.Keeps["cache"]);
        Assert.Null(LibraryVocabulary.Member(member.Result, member.Fates, method, compilation, member.Effects, member.Stores, member.Outputs, member.Keeps));
    }

    [Fact]
    public void Typed_leaf_beside_kept_is_still_checked()
    {
        foreach (var result in new[] { "[kept:cache,arg:wrong]", "[arg:wrong,kept:cache]" })
            Refused("\"outputs\":{\"output\":\"" + result + "\"}", "convert");
    }

    private static void Refused(string forms, string rule)
    {
        var (_, rejections) = Resolve(File(Entry(forms)));
        Assert.Contains(rule, Assert.Single(rejections).Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static string Entry(string forms) => "{\"member\":\"M:KeptVocabulary.Lib.Use(KeptVocabulary.Cache,KeptVocabulary.Profile,KeptVocabulary.Wrong,KeptVocabulary.Profile@)~KeptVocabulary.Profile\",\"effects\":{}," + forms + "}";

    private static string File(string entries, bool builtIn = false) => "{\"schemaVersion\":1,\"assemblies\":[\"KeptVocabulary\"]," +
        (builtIn ? "\"versions\":{\"minimum\":\"0.0.0.0\",\"maximumExclusive\":\"2.0.0.0\"}," : "") + "\"models\":[" + entries + "]}";

    private static (LibraryModels Models, IReadOnlyList<ModelRejection> Rejections) Resolve(string text)
    {
        using var repo = new CellModelRepository(text);
        var compilation = CreateCompilation();
        var files = ProjectModelFiles.Read(repo.Root);
        return ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(repo.Root, files));
    }

    private static Compilation CreateCompilation()
    {
        var source = CSharpCompilation.Create("KeptVocabulary", [CSharpSyntaxTree.ParseText("""
            namespace KeptVocabulary;
            public class Cache { }
            public class Profile { }
            public class Wrong { }
            public static class Lib
            {
                public static Profile Use(Cache cache, Profile value, Wrong wrong, out Profile output) { output = null!; return null!; }
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        Assert.True(source.Emit(bytes).Success);
        var compilation = CSharpCompilation.Create("Check", [], StubAssemblies.PlatformWithout([]).Append(MetadataReference.CreateFromImage(bytes.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation;
    }
}
