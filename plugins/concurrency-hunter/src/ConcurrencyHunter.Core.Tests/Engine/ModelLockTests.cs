using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ModelLockTests
{
    private const string SAVE = "M:Tiny.Store.Save(*)";
    private const string SAVE_INT = "M:Tiny.Store.Save(System.Int32)";
    private const string SAVE_STRING = "M:Tiny.Store.Save(System.String)";
    private const string LOCK = ".concurrency-hunter/models/models.lock.json";

    [Fact]
    public void Pattern_names_every_overload_of_the_member()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE)));
        var run = Resolve(repo);
        Assert.Equal(3, Members(repo, SAVE, "1.0.0.0").Length);
        Assert.Empty(run.Rejections);
    }

    [Fact]
    public void Pattern_matches_any_generic_arity()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE)));
        Resolve(repo);
        Assert.Contains(Members(repo, SAVE, "1.0.0.0"), member => member.Contains("Save``1", StringComparison.Ordinal));
    }

    [Fact]
    public void Pattern_names_constructors_and_getters()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("M:Tiny.Store.#ctor(*)") + "," + Entry("M:Tiny.Store.get_Name(*)")));
        Resolve(repo);
        Assert.Equal(2, Members(repo, "M:Tiny.Store.#ctor(*)", "1.0.0.0").Length);
        Assert.Single(Members(repo, "M:Tiny.Store.get_Name(*)", "1.0.0.0"));
    }

    [Fact]
    public void Pattern_does_not_match_inherited_members()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("M:Tiny.Child.Save(*)")));
        var run = Resolve(repo);
        Assert.Empty(Members(repo, "M:Tiny.Child.Save(*)", "1.0.0.0"));
        Assert.Single(run.Rejections);
    }

    [Fact]
    public void Pattern_matching_nothing_is_rejected_and_locked_empty()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("M:Tiny.Store.Missing(*)")));
        var run = Resolve(repo);
        Assert.Empty(Members(repo, "M:Tiny.Store.Missing(*)", "1.0.0.0"));
        Assert.Contains(run.Rejections, rejection => rejection.Diagnostic.Contains("names no member", StringComparison.Ordinal));
    }

    [Fact]
    public void Pattern_and_exact_entry_that_disagree_leave_the_member_opaque()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE) + "," + Entry(SAVE_INT, "\"opaque\": true")));
        var run = Resolve(repo);
        Assert.Equal(2, run.Rejections.Count);
        Assert.True(run.Models.Find(Method(run.Compilation, SAVE_INT))?.DeclaredOpaque);
    }

    [Fact]
    public void First_run_writes_the_rows_of_each_version_met()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE)));
        var files = ProjectModelFiles.Read(repo.Root);
        var modelLock = ModelLock.Read(repo.Root, files);
        ProjectModelResolver.Resolve(files, [Compilation(1)], modelLock);
        ProjectModelResolver.Resolve(files, [Compilation(2)], modelLock);
        modelLock.Write();
        Assert.Equal(2, Rows(repo).Length);
    }

    [Fact]
    public void Locked_row_decides_although_the_version_has_more_overloads()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE)));
        repo.Lock(LockJson(Row(SAVE, "1.0.0.0", SAVE_INT)));
        var run = Resolve(repo);
        Assert.Equal([SAVE_INT], Members(repo, SAVE, "1.0.0.0"));
        Assert.Equal(ModelLayer.Project, run.Models.Find(Method(run.Compilation, SAVE_INT))?.Layer);
        Assert.Null(run.Models.Find(Method(run.Compilation, SAVE_STRING)));
    }

    [Fact]
    public void Locked_member_the_version_lacks_is_rejected_alone()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE)));
        repo.Lock(LockJson(Row(SAVE, "1.0.0.0", SAVE_INT, "M:Tiny.Store.Save(System.Boolean)")));
        var run = Resolve(repo);
        Assert.Single(run.Rejections);
        Assert.Contains("System.Boolean", run.Rejections[0].Diagnostic);
        Assert.Equal(ModelLayer.Project, run.Models.Find(Method(run.Compilation, SAVE_INT))?.Layer);
    }

    [Fact]
    public void New_version_adds_rows_and_keeps_the_old_ones()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE)));
        Resolve(repo, 1);
        var old = Members(repo, SAVE, "1.0.0.0");
        Resolve(repo, 2);
        Assert.Equal(old, Members(repo, SAVE, "1.0.0.0"));
        Assert.NotEmpty(Members(repo, SAVE, "2.0.0.0"));
    }

    [Fact]
    public void Rows_of_a_removed_pattern_are_dropped()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE)));
        repo.Lock(LockJson(Row(SAVE, "1.0.0.0", SAVE_INT), Row("M:Tiny.Store.Missing(*)", "1.0.0.0")));
        Resolve(repo);
        Assert.Single(Rows(repo));
    }

    [Fact]
    public void Rows_of_a_rejected_pattern_entry_are_kept()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE, "\"opaque\": false")));
        repo.Lock(LockJson(Row(SAVE, "1.0.0.0", SAVE_INT)));
        var run = Resolve(repo);
        Assert.Single(run.Rejections);
        Assert.Single(Rows(repo));
    }

    [Fact]
    public void No_row_is_dropped_while_a_project_file_is_rejected_whole()
    {
        using var repo = new Repository();
        repo.Model("not JSON", "bad.json");
        repo.Model(FileModel(Entry(SAVE)));
        repo.Lock(LockJson(Row("M:Tiny.Store.Missing(*)", "1.0.0.0")));
        Resolve(repo);
        Assert.Equal(2, Rows(repo).Length);
    }

    [Fact]
    public void Unchanged_lock_is_not_rewritten()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE)));
        Resolve(repo);
        var before = DateTime.UtcNow.AddMinutes(-10);
        File.SetLastWriteTimeUtc(repo.LockPath, before);
        Resolve(repo);
        Assert.Equal(before, File.GetLastWriteTimeUtc(repo.LockPath));
    }

    [Fact]
    public void Lock_with_a_bom_is_read_and_rewritten_once_without_it()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE)));
        repo.LockBytes(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(LockJson(Row(SAVE, "1.0.0.0", SAVE_INT)))).ToArray());
        Resolve(repo);
        Assert.False(File.ReadAllBytes(repo.LockPath).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Equal([SAVE_INT], Members(repo, SAVE, "1.0.0.0"));
    }

    [Fact]
    public void Lock_is_sorted_without_bom_with_lf_and_a_final_newline()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("M:Tiny.Store.get_Name(*)") + "," + Entry(SAVE)));
        Resolve(repo);
        var bytes = File.ReadAllBytes(repo.LockPath);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.EndsWith("\n", text);
        Assert.DoesNotContain("\r", text);
        Assert.True(text.IndexOf(SAVE, StringComparison.Ordinal) < text.IndexOf("get_Name(*)", StringComparison.Ordinal));
    }

    [Fact]
    public void Unwritable_lock_keeps_the_resolution_in_memory_and_says_so()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE)));
        Directory.CreateDirectory(repo.LockPath);
        var run = Resolve(repo);
        Assert.Equal(ModelLayer.Project, run.Models.Find(Method(run.Compilation, SAVE_INT))?.Layer);
        Assert.Contains(run.LockDiagnostics, diagnostic => diagnostic.Contains("cannot write lock", StringComparison.Ordinal));
    }

    [Fact]
    public void Failed_replace_keeps_the_previous_lock_whole()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE)));
        var old = LockJson(Row(SAVE, "1.0.0.0", SAVE_INT));
        repo.Lock(old);
        var files = ProjectModelFiles.Read(repo.Root);
        var modelLock = ModelLock.Read(repo.Root, files);
        ProjectModelResolver.Resolve(files, [Compilation(2)], modelLock);
        using (var held = new FileStream(repo.LockPath, FileMode.Open, FileAccess.Read, FileShare.None))
            modelLock.Write();
        Assert.Equal(old, File.ReadAllText(repo.LockPath));
        Assert.Empty(Directory.GetFiles(repo.Models, "*.tmp"));
        Assert.Contains(modelLock.Diagnostics, diagnostic => diagnostic.Contains("cannot write lock", StringComparison.Ordinal));
    }

    [Fact]
    public void No_pattern_and_no_lock_writes_nothing()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE_INT)));
        Resolve(repo);
        Assert.False(File.Exists(repo.LockPath));
    }

    [Fact]
    public void Overload_missing_a_named_parameter_is_rejected_alone()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE, "\"effects\": {\"x\": [\"reads-deep\"]}")));
        var run = Resolve(repo);
        Assert.Equal(2, run.Rejections.Count);
        Assert.Equal(ModelLayer.Project, run.Models.Find(Method(run.Compilation, SAVE_INT))?.Layer);
    }

    [Fact]
    public void Overload_taking_a_delegate_is_rejected_alone()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("M:Tiny.Store.Invoke(*)")));
        var run = Resolve(repo);
        Assert.Single(run.Rejections);
        Assert.Contains("delegate-typed", run.Rejections[0].Reason);
        Assert.Equal(2, Members(repo, "M:Tiny.Store.Invoke(*)", "1.0.0.0").Length);
    }

    [Theory]
    [MemberData(nameof(UnreadableLocks))]
    public void Unreadable_locks(string name, byte[] bytes)
    {
        Assert.NotEmpty(name);
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE)));
        repo.LockBytes(bytes);
        var run = Resolve(repo);
        Assert.Contains(run.LockDiagnostics, diagnostic => diagnostic.StartsWith("library-models: " + LOCK + ": cannot read lock:", StringComparison.Ordinal));
        Assert.Equal(3, Members(repo, SAVE, "1.0.0.0").Length);
        Assert.DoesNotContain("cannot read lock", File.ReadAllText(repo.LockPath));
    }

    [Fact]
    public void Lock_without_read_attributes_is_reported_unreadable()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var repo = new Repository();
        repo.Model(FileModel(Entry(SAVE)));
        repo.Lock(LockJson(Row(SAVE, "1.0.0.0")));
        var file = new FileInfo(repo.LockPath);
        var modified = file.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadAttributes | FileSystemRights.ReadData,
                                            AccessControlType.Deny);
        modified.AddAccessRule(rule);
        file.SetAccessControl(modified);
        try
        {
            var run = Resolve(repo);
            Assert.Contains(run.LockDiagnostics, diagnostic => diagnostic.StartsWith("library-models: " + LOCK + ": cannot read lock:", StringComparison.Ordinal));
        }
        finally
        {
            modified.RemoveAccessRuleSpecific(rule);
            file.SetAccessControl(modified);
        }
    }

    public static IEnumerable<object[]> UnreadableLocks()
    {
        static object[] DataRow(string name, string json) => [name, Encoding.UTF8.GetBytes(json)];
        yield return DataRow("not JSON", "no");
        yield return ["UTF-16", Encoding.Unicode.GetBytes(LockJson())];
        yield return ["invalid UTF-8", new byte[] { 0xFF }];
        yield return DataRow("trailing comma", "{\"schemaVersion\":1,\"patterns\":[],}");
        yield return DataRow("array", "[]");
        yield return DataRow("no patterns", "{\"schemaVersion\":1}");
        yield return DataRow("schema 2", "{\"schemaVersion\":2,\"patterns\":[]}");
        yield return DataRow("unknown top property", "{\"schemaVersion\":1,\"patterns\":[],\"other\":1}");
        yield return DataRow("repeated top property", "{\"schemaVersion\":1,\"schemaVersion\":1,\"patterns\":[]}");
        yield return DataRow("unknown row property", LockJson(Row(SAVE, "1.0.0.0", SAVE_INT).TrimEnd('}') + ",\"other\":1}"));
        yield return DataRow("repeated row property", LockJson(Row(SAVE, "1.0.0.0", SAVE_INT).TrimEnd('}') + ",\"version\":\"1.0.0.0\"}"));
        yield return DataRow("bad pattern", LockJson(Row("bad", "1.0.0.0")));
        yield return DataRow("nested pattern parenthesis", LockJson(Row("M:Tiny.Store.Save((*)", "1.0.0.0")));
        yield return DataRow("bad assembly", LockJson(Row(SAVE, "1.0.0.0").Replace("\"Tiny\"", "\"\"")));
        yield return DataRow("no version", LockJson(Row(SAVE, "1.0.0.0").Replace("\"version\":\"1.0.0.0\",", "")));
        yield return DataRow("short version", LockJson(Row(SAVE, "1.0")));
        yield return DataRow("bad members", LockJson(Row(SAVE, "1.0.0.0").Replace("\"members\":[]", "\"members\":1")));
        yield return DataRow("wrong member", LockJson(Row(SAVE, "1.0.0.0", "M:Tiny.Other.Save(System.Int32)")));
        yield return DataRow("empty member return type", LockJson(Row(SAVE, "1.0.0.0", "M:Tiny.Store.Save~")));
        yield return DataRow("trailing member parameter comma", LockJson(Row(SAVE, "1.0.0.0", "M:Tiny.Store.Save(System.Int32,)")));
        yield return DataRow("duplicate row", LockJson(Row(SAVE, "1.0.0.0"), Row(SAVE, "1.0.0.0")));
    }

    private static (LibraryModels Models, IReadOnlyList<ModelRejection> Rejections, IReadOnlyList<string> LockDiagnostics,
                    Compilation Compilation) Resolve(Repository repo, int version = 1)
    {
        var compilation = Compilation(version);
        var files = ProjectModelFiles.Read(repo.Root);
        var modelLock = ModelLock.Read(repo.Root, files);
        var (models, rejections) = ProjectModelResolver.Resolve(files, [compilation], modelLock);
        modelLock.Write();
        return (models, rejections, modelLock.Diagnostics, compilation);
    }

    private static Compilation Compilation(int version)
    {
        var library = Library(version);
        return FixtureSolution.Create(new FixtureOptions { MetadataReferences = [library] },
                                      ("Case.cs", "public class Client { public void Work() { Tiny.Store.Save(1); } }") )
                              .Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
    }

    private static MetadataReference Library(int version)
    {
        var source = $$"""
            using System;
            using System.Reflection;
            [assembly: AssemblyVersion("{{version}}.0.0.0")]
            namespace Tiny
            {
                public class Store
                {
                    public Store() { }
                    public Store(int x) { }
                    public string Name => "";
                    public static void Save(int x) { }
                    public static void Save(string text) { }
                    public static void Save<T>(T item) { }
                    public static void Invoke(Action callback) { }
                    public static void Invoke(int x) { }
                }
                public class Child : Store { }
            }
            """;
        var compilation = CSharpCompilation.Create("Tiny", [CSharpSyntaxTree.ParseText(source)],
                                                    StubAssemblies.PlatformWithout([]),
                                                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        var emit = compilation.Emit(bytes);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return MetadataReference.CreateFromImage(bytes.ToArray());
    }

    private static IMethodSymbol Method(Compilation compilation, string id) =>
        Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation).OfType<IMethodSymbol>());

    private static string Entry(string member, string decision = "\"effects\": {}") =>
        "{\"member\":\"" + member + "\"," + decision + "}";

    private static string FileModel(string entries) =>
        "{\"schemaVersion\":1,\"assemblies\":[\"Tiny\"],\"models\":[" + entries + "]}";

    private static string Row(string pattern, string version, params string[] members) =>
        "{\"pattern\":\"" + pattern + "\",\"assembly\":\"Tiny\",\"version\":\"" + version +
        "\",\"members\":[" + string.Join(",", members.Select(member => "\"" + member + "\"")) + "]}";

    private static string LockJson(params string[] rows) =>
        "{\"schemaVersion\":1,\"patterns\":[" + string.Join(",", rows) + "]}";

    private static JsonElement[] Rows(Repository repo)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(repo.LockPath));
        return document.RootElement.GetProperty("patterns").EnumerateArray().Select(row => row.Clone()).ToArray();
    }

    private static string[] Members(Repository repo, string pattern, string version) =>
        Assert.Single(Rows(repo), row => row.GetProperty("pattern").GetString() == pattern &&
                                         row.GetProperty("version").GetString() == version)
              .GetProperty("members").EnumerateArray().Select(member => member.GetString()!).ToArray();

    private sealed class Repository : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("ch-lock-").FullName;
        public string Models => Path.Combine(Root, ".concurrency-hunter", "models");
        public string LockPath => Path.Combine(Models, "models.lock.json");

        public void Model(string text, string name = "model.json")
        {
            Directory.CreateDirectory(Models);
            File.WriteAllText(Path.Combine(Models, name), text, new UTF8Encoding(false));
        }

        public void Lock(string text) => LockBytes(new UTF8Encoding(false).GetBytes(text));

        public void LockBytes(byte[] bytes)
        {
            Directory.CreateDirectory(Models);
            File.WriteAllBytes(LockPath, bytes);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
