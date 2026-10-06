using System.Text;
using System.Text.Json;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>An accessor is named by its <c>M:</c> declaration id as a method is (R5): both readers accept a getter, a setter, an init
/// setter, an indexer's accessors and an event's add and remove accessors, exactly and through a member pattern, and refuse a
/// <c>P:</c> or <c>E:</c> id; the model lock records an accessor by its own name.</summary>
public sealed class AccessorModelIdTests
{
    private const string GET_VALUE = "M:Acc.Box.get_Value";
    private const string SET_VALUE = "M:Acc.Box.set_Value(System.Object)";
    private const string SET_INIT = "M:Acc.Box.set_Init(System.Object)";
    // An indexer getter's declaration id carries its return type, as DocumentationCommentId writes it.
    private const string GET_ITEM = "M:Acc.Box.get_Item(System.Object)~System.Object";
    private const string SET_ITEM = "M:Acc.Box.set_Item(System.Object,System.Object)";
    private const string ADD_CHANGED = "M:Acc.Box.add_Changed(System.Action)";
    private const string REMOVE_CHANGED = "M:Acc.Box.remove_Changed(System.Action)";
    private const string PROPERTY = "P:Acc.Box.Value";
    private const string EVENT = "E:Acc.Box.Changed";

    [Theory]
    [MemberData(nameof(Ids))]
    public void Reader_accepts_an_accessor_id_and_refuses_a_property_or_event_id(string reader, string id, bool accepted)
    {
        var member = reader == "pattern" ? Pattern(id) : id;
        var file = Encoding.UTF8.GetBytes(FileModel(Entry(member)));

        if (reader == "built-in")
        {
            var error = Record.Exception(() => BuiltInModelReader.Read(file));
            Assert.Equal(accepted, error is null);
            if (error is not null)
                Assert.IsType<LibraryModelException>(error);
            return;
        }
        var files = ProjectModelFiles.Read("model.json", file);
        Assert.Equal(accepted, files.Rejections.Count == 0);
        Assert.Equal(accepted ? 1 : 0, files.Entries.Count);
        if (!accepted)
            Assert.Contains("member must be an M: declaration id or a member pattern", Assert.Single(files.Rejections).Reason);
    }

    public static IEnumerable<object[]> Ids()
    {
        string[] accessors = [GET_VALUE, SET_VALUE, SET_INIT, GET_ITEM, SET_ITEM, ADD_CHANGED, REMOVE_CHANGED];
        foreach (var reader in new[] { "built-in", "project", "pattern" })
        {
            foreach (var id in accessors)
                yield return [reader, id, true];
            yield return [reader, PROPERTY, false];
            yield return [reader, EVENT, false];
        }
    }

    [Fact]
    public void A_project_entry_for_a_library_setter_resolves_to_a_known_project_model()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SET_VALUE, "\"effects\": {\"value\": [\"reads-deep\"]}")));
        var run = Resolve(repo);

        Assert.Empty(run.Rejections);
        var match = Assert.IsType<LibraryMatch>(run.Models.Find(Method(run.Compilation, SET_VALUE)));
        Assert.Equal(LibraryMatchKind.Known, match.Kind);
        Assert.Equal(ModelLayer.Project, match.Layer);
        Assert.False(match.DeclaredOpaque);
        Assert.Equal([LibraryEffect.DeepReadOf("value")], match.Effects);
    }

    [Fact]
    public void A_lock_row_for_a_setter_pattern_is_written_and_read_back()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("M:Acc.Box.set_Value(*)")));
        Resolve(repo);
        Assert.Equal([SET_VALUE], Members(repo, "M:Acc.Box.set_Value(*)"));
        var written = File.ReadAllBytes(repo.LockPath);

        var again = Resolve(repo);
        Assert.Empty(again.LockDiagnostics);
        Assert.Empty(again.Rejections);
        Assert.Equal(written, File.ReadAllBytes(repo.LockPath));
        Assert.Equal(ModelLayer.Project, again.Models.Find(Method(again.Compilation, SET_VALUE))?.Layer);
    }

    [Fact]
    public void A_get_Item_pattern_expands_to_the_indexer_getter()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("M:Acc.Box.get_Item(*)")));
        var run = Resolve(repo);

        Assert.Empty(run.Rejections);
        Assert.Equal([GET_ITEM], Members(repo, "M:Acc.Box.get_Item(*)"));
    }

    [Fact]
    public void A_set_Item_pattern_expands_to_the_indexer_setter()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("M:Acc.Box.set_Item(*)")));
        var run = Resolve(repo);

        Assert.Empty(run.Rejections);
        Assert.Equal([SET_ITEM], Members(repo, "M:Acc.Box.set_Item(*)"));
    }

    [Fact]
    public void A_pattern_over_a_renamed_indexer_expands_to_its_get_Cells_accessor()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("M:Acc.Grid.get_Cells(*)")));
        var run = Resolve(repo);

        Assert.Empty(run.Rejections);
        Assert.Equal(["M:Acc.Grid.get_Cells(System.Int32)~System.Object"], Members(repo, "M:Acc.Grid.get_Cells(*)"));
    }

    [Fact]
    public void An_add_pattern_expands_to_the_events_add_accessor()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("M:Acc.Box.add_Changed(*)", "\"effects\": {}, \"fates\": {\"value\": {\"fate\": \"not-run\"}}")));
        var run = Resolve(repo);

        Assert.Empty(run.Rejections);
        Assert.Equal([ADD_CHANGED], Members(repo, "M:Acc.Box.add_Changed(*)"));
    }

    [Fact]
    public void A_setter_entry_of_a_source_property_is_rejected_for_its_body_in_the_run()
    {
        using var repo = new Repository();
        repo.Model("{\"schemaVersion\":1,\"assemblies\":[\"Fixture\"],\"models\":[" + Entry("M:Client.set_Value(System.Int32)") + "]}");
        var files = ProjectModelFiles.Read(repo.Root);
        var compilation = Compilation();
        var (_, rejections) = ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(repo.Root, files));

        var rejection = Assert.Single(rejections);
        Assert.Contains("set_Value", rejection.Diagnostic);
        Assert.Contains("member has a body in the run.", rejection.Reason);
    }

    [Fact]
    public void A_not_run_entry_of_a_source_field_like_events_add_accessor_is_rejected_for_its_body_in_the_run()
    {
        using var repo = new Repository();
        repo.Model("{\"schemaVersion\":1,\"assemblies\":[\"Fixture\"],\"models\":[" +
                   Entry("M:Client.add_Changed(System.Action)", "\"effects\": {}, \"fates\": {\"value\": {\"fate\": \"not-run\"}}") + "]}");
        var files = ProjectModelFiles.Read(repo.Root);
        var (_, rejections) = ProjectModelResolver.Resolve(files, [Compilation()], ModelLock.Read(repo.Root, files));

        var rejection = Assert.Single(rejections);
        Assert.Contains("add_Changed", rejection.Diagnostic);
        Assert.Contains("member has a body in the run.", rejection.Reason);
    }

    /// <summary>The member pattern of an id: its name with <c>(*)</c> in place of the parameter list.</summary>
    /// <param name="id">A declaration id.</param>
    private static string Pattern(string id) => (id.Contains('(') ? id[..id.IndexOf('(')] : id) + "(*)";

    private static (LibraryModels Models, IReadOnlyList<ModelRejection> Rejections, IReadOnlyList<string> LockDiagnostics,
                    Compilation Compilation) Resolve(Repository repo)
    {
        var compilation = Compilation();
        var files = ProjectModelFiles.Read(repo.Root);
        var modelLock = ModelLock.Read(repo.Root, files);
        var (models, rejections) = ProjectModelResolver.Resolve(files, [compilation], modelLock);
        modelLock.Write();
        return (models, rejections, modelLock.Diagnostics, compilation);
    }

    /// <summary>A client project, whose assembly is <c>Fixture</c>, referencing the accessor library.</summary>
    private static Compilation Compilation() =>
        FixtureSolution.Create(new FixtureOptions { MetadataReferences = [Library.Value] },
                               ("Case.cs", "public class Client { public int Value { get; set; } public event System.Action Changed; public void Work(Acc.Box box) => box.Value = 1; }"))
                       .Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;

    private static readonly Lazy<MetadataReference> Library = new(() =>
    {
        const string SOURCE = """
            using System;
            using System.Runtime.CompilerServices;
            namespace Acc
            {
                public class Box
                {
                    public object Value { get; set; }
                    public object Init { get; init; }
                    public object this[object key] { get => null; set { } }
                    public event Action Changed;
                }
                public class Grid
                {
                    [IndexerName("Cells")]
                    public object this[int row] { get => null; set { } }
                }
            }
            """;
        var compilation = CSharpCompilation.Create("Acc", [CSharpSyntaxTree.ParseText(SOURCE)], StubAssemblies.PlatformWithout([]),
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        var emit = compilation.Emit(bytes);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return MetadataReference.CreateFromImage(bytes.ToArray());
    });

    private static IMethodSymbol Method(Compilation compilation, string id) =>
        Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation).OfType<IMethodSymbol>());

    private static string Entry(string member, string decision = "\"effects\": {}") =>
        "{\"member\":\"" + member + "\"," + decision + "}";

    private static string FileModel(string entries) =>
        "{\"schemaVersion\":1,\"assemblies\":[\"Acc\"],\"versions\":{\"minimum\":\"0.0.0.0\",\"maximumExclusive\":\"100.0.0.0\"}," +
        "\"models\":[" + entries + "]}";

    private static string[] Members(Repository repo, string pattern)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(repo.LockPath));
        return Assert.Single(document.RootElement.GetProperty("patterns").EnumerateArray(), row => row.GetProperty("pattern").GetString() == pattern)
                     .GetProperty("members").EnumerateArray().Select(member => member.GetString()!).ToArray();
    }

    private sealed class Repository : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("ch-accessor-").FullName;
        public string Models => Path.Combine(Root, ".concurrency-hunter", "models");
        public string LockPath => Path.Combine(Models, "models.lock.json");

        public void Model(string text)
        {
            Directory.CreateDirectory(Models);
            File.WriteAllText(Path.Combine(Models, "model.json"), text, new UTF8Encoding(false));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
