using System.Text;
using System.Text.Json.Nodes;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The shared frameworks' census (ADR 0012 and ADR 0022, amendments of 2026-11-07): how a built-in file names its failed members,
/// which built-in records the census owns, how the owner's regeneration reads the census runs and writes the built-in files, and the census
/// record and its report.</summary>
public sealed class FrameworkCensusTests : IDisposable
{
    private const string NETCORE = "Microsoft.NETCore.App";
    private const string LINQ = "System.Linq";
    private const string COUNT = "M:System.Linq.Enumerable.Count``1(System.Collections.Generic.IEnumerable{``0})~System.Int32";
    private const string ANY = "M:System.Linq.Enumerable.Any``1(System.Collections.Generic.IEnumerable{``0})~System.Boolean";
    private const string FIRST = "M:System.Linq.Enumerable.First``1(System.Collections.Generic.IEnumerable{``0})~``0";
    private const string LAST = "M:System.Linq.Enumerable.Last``1(System.Collections.Generic.IEnumerable{``0})~``0";
    private const string SINGLE = "M:System.Linq.Enumerable.Single``1(System.Collections.Generic.IEnumerable{``0})~``0";
    private const string MAX = "M:System.Linq.Enumerable.Max(System.Collections.Generic.IEnumerable{System.Int32})~System.Int32";
    private const string ARRAY_CLEAR = "M:System.Array.Clear(System.Array)";
    private const string GET_TYPE_INFO = "M:System.Text.Json.JsonSerializerOptions.GetTypeInfo(System.Type)";
    private const string OPTIONS_COPY = "M:System.Text.Json.JsonSerializerOptions.#ctor(System.Text.Json.JsonSerializerOptions)";
    private const string RANGE_8_TO_11 = "\"versions\": {\"minimum\": \"8.0.0.0\", \"maximumExclusive\": \"11.0.0.0\"}";
    private const string RANGE_10 = "\"versions\": {\"minimum\": \"10.0.0.0\", \"maximumExclusive\": \"10.0.0.1\"}";
    private static readonly Version Ten = new(10, 0, 0, 0);

    // One read of the installed frameworks for the whole class: it opens every assembly of both.
    private static readonly Lazy<FrameworkAssemblies> Frameworks = new(() => FrameworkAssemblies.Installed(ImplementationAssemblies.ForThisProcess().DotnetRoot));

    private readonly string _folder = Directory.CreateTempSubdirectory("ch-census-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_failed_member_reads_as_a_record_that_declares_it_opaque_in_the_files_range()
    {
        var (members, _) = BuiltInModelReader.Read(Bytes($"{{\"schemaVersion\": 1, \"assemblies\": [\"{LINQ}\"], {RANGE_10}, \"models\": [], \"failed\": [\"{COUNT}\"]}}"));

        var failed = Assert.Single(members);
        Assert.Equal((COUNT, true, ModelLayer.BuiltIn), (failed.Id, failed.DeclaredOpaque, failed.Layer));
        Assert.Empty(failed.Effects);
        Assert.Equal(new SupportedAssemblyVersion(LINQ, Ten, new Version(10, 0, 0, 1)), Assert.Single(failed.Assemblies));
    }

    [Theory]
    [InlineData($"\"assemblies\": [\"{LINQ}\"], {RANGE_10}, \"models\": [], \"failed\": [\"T:System.Linq.Enumerable\"]")]
    [InlineData($"\"assemblies\": [\"{LINQ}\"], {RANGE_10}, \"models\": [], \"failed\": [\"{COUNT}\", \"{COUNT}\"]")]
    [InlineData($"\"models\": [], \"failed\": [\"{COUNT}\"]")]
    public void A_failed_member_is_an_exact_member_id_named_once_in_a_file_with_a_range(string body) =>
        Assert.Throws<LibraryModelException>(() => BuiltInModelReader.Read(Bytes($"{{\"schemaVersion\": 1, {body}}}")));

    [Fact]
    public void A_member_both_modelled_and_failed_is_described_twice()
    {
        var modelled = BuiltInModelReader.Read(Bytes(Topical(LINQ, COUNT))).Members;
        var failed = BuiltInModelReader.Read(Bytes($"{{\"schemaVersion\": 1, \"assemblies\": [\"{LINQ}\"], {RANGE_10}, \"models\": [], \"failed\": [\"{COUNT}\"]}}"))
                                      .Members;

        var error = Assert.Throws<LibraryModelException>(() => new LibraryModels(modelled.Concat(failed), []));
        Assert.Contains("described twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_census_owns_a_record_only_when_every_range_at_10_names_a_framework_assembly_that_defines_its_type()
    {
        var frameworks = Frameworks.Value;
        var builtIn = LibraryModels.BuiltIn.Members;

        Assert.True(frameworks.IsCensusEntry(builtIn.First(record => record.Assemblies.Any(range => range.AssemblyName == LINQ))));
        Assert.True(frameworks.IsCensusEntry(builtIn.First(record => record.Assemblies.Any(range => range.AssemblyName == "System.Text.Json"))));
        // System.Runtime forwards System.Array, and CoreLib has a census of its own.
        Assert.False(frameworks.IsCensusEntry(builtIn.First(record => record.Id.StartsWith("M:System.Array.", StringComparison.Ordinal))));
        Assert.False(frameworks.IsCensusEntry(builtIn.First(record => record.Assemblies.Any(range => range.AssemblyName == "Microsoft.EntityFrameworkCore"))));
        Assert.False(frameworks.IsCensusEntry(new LibraryModel(COUNT, [new SupportedAssemblyVersion(LINQ, new Version(8, 0, 0, 0), Ten)], [])));
    }

    [Fact]
    public void The_census_run_reads_the_built_in_models_without_the_census_records()
    {
        string[] owned = ["System.Linq", "System.Linq.Queryable", "System.Text.Json", "System.Net.Http", "Microsoft.Extensions.Http",
                          "Microsoft.Extensions.Logging.Abstractions"];
        var shipped = LibraryModels.BuiltIn;

        var census = LibraryModels.ForCensus(Frameworks.Value);

        Assert.DoesNotContain(census.Members, record => record.Assemblies.Any(range => owned.Contains(range.AssemblyName) && range.Contains(Ten)));
        Assert.Equal(shipped.Members.Count(IsArray), census.Members.Count(IsArray));
        Assert.Equal(shipped.Members.Count(IsEntityFramework), census.Members.Count(IsEntityFramework));
        Assert.Equal(shipped.ImmutableTypes.Count, census.ImmutableTypes.Count);

        static bool IsArray(LibraryModel record) => record.Id.StartsWith("M:System.Array.", StringComparison.Ordinal);
        static bool IsEntityFramework(LibraryModel record) => record.Assemblies.Any(range => range.AssemblyName == "Microsoft.EntityFrameworkCore");
    }

    [Fact]
    public void A_model_replaces_the_owners_census_entry_and_moves_to_its_assemblys_file()
    {
        WriteFile("linq.json", Topical(LINQ, COUNT, ANY));

        var written = Write([Model(COUNT)]);

        Assert.Equal([ANY], Members(Read("linq.json")));
        var census = Read(NETCORE, $"{LINQ}.json");
        Assert.Equal([COUNT], Members(census));
        Assert.Equal("generated", census["models"]![0]!["note"]!.GetValue<string>());
        Assert.Equal(FrameworkCensus.SHIPS_MODEL, Assert.Single(written.Record.Rows).Ships);
        Assert.Empty(written.Problems);
    }

    [Fact]
    public void Answers_that_share_a_file_see_each_others_edits()
    {
        // COUNT stands in two files and leaves both; LAST and SINGLE share a census file that no run had yet, which the first creates.
        WriteFile("linq.json", Topical(LINQ, COUNT));
        WriteFile(Path.Combine(NETCORE, $"{LINQ}.json"), Census(LINQ, [COUNT, MAX], []));

        var written = Write([Model(COUNT), Model(MAX), Model(SINGLE, "System.Collections"), Model(LAST, "System.Collections")]);

        Assert.Empty(Members(Read("linq.json")));
        Assert.Equal([COUNT, MAX], Members(Read(NETCORE, $"{LINQ}.json")));
        Assert.Equal([LAST, SINGLE], Members(Read(NETCORE, "System.Collections.json")));
        Assert.All(written.Record.Rows, row => Assert.Equal(FrameworkCensus.SHIPS_MODEL, row.Ships));
        Assert.Empty(written.Problems);
    }

    [Fact]
    public void A_refusal_keeps_the_model_the_member_had_whoever_wrote_it()
    {
        WriteFile("linq.json", Topical(LINQ, ANY));
        WriteFile(Path.Combine(NETCORE, $"{LINQ}.json"), Census(LINQ, [FIRST], []));

        var written = Write([Refusal(ANY), Refusal(FIRST)]);

        Assert.Equal([ANY], Members(Read("linq.json")));
        Assert.Equal([FIRST], Members(Read(NETCORE, $"{LINQ}.json")));
        Assert.All(written.Record.Rows, row => Assert.Equal((FrameworkCensus.SHIPS_KEPT, true), (row.Ships, row.ModelHash.Length == 12)));
    }

    [Fact]
    public void A_refusal_without_a_model_is_failed_and_each_regeneration_writes_the_failed_list_anew()
    {
        // LAST left the census since the last regeneration: it leaves the failed list; MAX left too and keeps its model.
        WriteFile(Path.Combine(NETCORE, $"{LINQ}.json"), Census(LINQ, [MAX], [SINGLE, LAST]));
        WriteFile(Path.Combine(NETCORE, "System.Text.Json.json"), Census("System.Text.Json", [], [GET_TYPE_INFO + "~System.Text.Json.Serialization.Metadata.JsonTypeInfo"]));

        var written = Write([Refusal(SINGLE)]);

        var census = Read(NETCORE, $"{LINQ}.json");
        Assert.Equal([MAX], Members(census));
        Assert.Equal([SINGLE], census["failed"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.False(File.Exists(Path.Combine(_folder, NETCORE, "System.Text.Json.json")));
        Assert.Equal((FrameworkCensus.SHIPS_FAILED, string.Empty), (Assert.Single(written.Record.Rows).Ships, written.Record.Rows[0].ModelHash));
    }

    [Fact]
    public void A_model_whose_member_an_entry_the_census_does_not_own_describes_is_not_written()
    {
        // The facade System.Runtime names the member too, so the owner's entry is not the census's to replace.
        WriteFile("system.json", $"{{\"schemaVersion\": 1, \"assemblies\": [\"System.Runtime\", \"System.Private.CoreLib\"], {RANGE_8_TO_11}, " +
                                 $"\"models\": [{{\"member\": \"{ARRAY_CLEAR}\", \"effects\": {{}}}}]}}");

        var written = Write([Model(ARRAY_CLEAR, "System.Runtime")]);

        Assert.Equal([ARRAY_CLEAR], Members(Read("system.json")));
        Assert.False(Directory.Exists(Path.Combine(_folder, NETCORE)));
        Assert.Equal(FrameworkCensus.SHIPS_KEPT, Assert.Single(written.Record.Rows).Ships);
        Assert.Contains(written.Problems, problem => problem.StartsWith(ARRAY_CLEAR, StringComparison.Ordinal));
    }

    [Fact]
    public void A_topical_file_no_answer_touches_keeps_its_bytes_and_its_write_time()
    {
        var path = WriteFile("http-client.json", "{\"schemaVersion\": 1, \"note\": \"the owner's 'quotes' <stay>\", \"assemblies\": [\"System.Net.Http\"], " +
                                                 $"{RANGE_8_TO_11}, \"models\": []}}");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var bytes = File.ReadAllBytes(path);

        Write([Model(COUNT)]);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Taking_an_entry_out_of_a_shipped_topical_file_keeps_every_other_byte()
    {
        // The owners escape their files unevenly (raw ' < > ` in some, ' ` in others), so no serializer writes them back unchanged:
        // the writer cuts out an entry's own bytes instead.
        var folder = RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "src", "ConcurrencyHunter.Core", "Providers", "LibraryModels", "BuiltIn");
        foreach (var path in Directory.GetFiles(folder, "*.json").Where(path => JsonNode.Parse(File.ReadAllText(path))!["models"]!.AsArray().Count > 1))
        {
            var bytes = File.ReadAllBytes(path);
            var count = JsonNode.Parse(bytes)!["models"]!.AsArray().Count;
            Assert.Same(bytes, FrameworkCensus.WithoutModels(bytes, new HashSet<int>()));
            foreach (var index in new[] { 0, count / 2, count - 1 })
            {
                var cut = FrameworkCensus.WithoutModels(bytes, new HashSet<int> { index });
                var expected = JsonNode.Parse(bytes)!.AsObject();
                expected["models"]!.AsArray().RemoveAt(index);
                Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(cut)), $"{Path.GetFileName(path)} without entry {index}");
                AssertRemovesOnly(Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(cut), Path.GetFileName(path));
            }

            var empty = JsonNode.Parse(FrameworkCensus.WithoutModels(bytes, Enumerable.Range(0, count).ToHashSet()))!;
            Assert.Empty(empty["models"]!.AsArray());
        }
    }

    /// <summary>Asserts that a text is another with some of its lines taken out and none changed: every line of the shorter one stands in
    /// the longer, in order.</summary>
    /// <param name="original">The longer text.</param>
    /// <param name="cut">The shorter text.</param>
    /// <param name="file">The file the texts are of, for the message.</param>
    private static void AssertRemovesOnly(string original, string cut, string file)
    {
        var lines = original.Split('\n');
        var next = 0;
        foreach (var line in cut.Split('\n'))
        {
            while (next < lines.Length && lines[next] != line)
                next++;
            Assert.True(next < lines.Length, $"{file}: the line '{line.Trim()}' is not one of the original's, in order.");
            next++;
        }
    }

    [Fact]
    public void A_census_record_reads_back_its_own_lines()
    {
        var record = new CensusRecord("abc123", new string('e', 64), new Dictionary<string, string> { [NETCORE] = "10.0.14", ["Microsoft.AspNetCore.App"] = "10.0.14" },
                                      [Row(COUNT, FrameworkCensus.MODEL, FrameworkCensus.SHIPS_MODEL, "0123456789ab"),
                                       new CensusRow(ANY, NETCORE, LINQ, "unknown-touch", ["kept-user-object", "value-unnamed"], FrameworkCensus.SHIPS_FAILED, string.Empty)]);

        var read = CensusRecord.Parse(record.Lines);

        Assert.Equal(record.Lines, read.Lines);
        Assert.Equal(("abc123", "10.0.14"), (read.Commit, read.Patches[NETCORE]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void A_census_record_names_its_engine_and_each_member_once(bool engine, bool twice)
    {
        var lines = new List<string> { "# framework census", "# commit abc123" };
        if (engine)
            lines.Add("# engine " + new string('e', 64));
        lines.Add(Row(COUNT, FrameworkCensus.MODEL, FrameworkCensus.SHIPS_MODEL, "0123456789ab").Line);
        if (twice)
            lines.Add(Row(COUNT, FrameworkCensus.MODEL, FrameworkCensus.SHIPS_MODEL, "0123456789ab").Line);

        Assert.Throws<FormatException>(() => CensusRecord.Parse(lines));
    }

    [Fact]
    public void The_report_names_every_way_a_member_moved()
    {
        var before = Record("10.0.13", Row("M:N.Changed", "model", "model", "aaaaaaaaaaaa"), Row("M:N.GotModel", "timeout", "failed", string.Empty),
                            Row("M:N.Kept", "model", "model", "cccccccccccc"), Row("M:N.Left", "model", "model", "dddddddddddd"));
        var after = Record("10.0.14", Row("M:N.Changed", "model", "model", "bbbbbbbbbbbb"), Row("M:N.GotModel", "model", "model", "eeeeeeeeeeee"),
                           Row("M:N.Joined", "model", "model", "ffffffffffff"), Row("M:N.Kept", "unknown-touch", "kept", "cccccccccccc"),
                           Row("M:N.NewlyFailed", "unknown-touch", "failed", string.Empty));

        Assert.Equal(["Microsoft.NETCore.App: 10.0.13 -> 10.0.14",
                      "joined the census: 2, e.g. M:N.Joined, M:N.NewlyFailed",
                      "left the census: 1, e.g. M:N.Left",
                      "models gained: 2, e.g. M:N.GotModel, M:N.Joined",
                      "models changed: 1, e.g. M:N.Changed",
                      "models kept over a refusal: 1, e.g. M:N.Kept",
                      "newly failed: 1, e.g. M:N.NewlyFailed",
                      "failed members that got a model: 1, e.g. M:N.GotModel",
                      "outcome timeout: 1 -> 0",
                      "outcome unknown-touch: 0 -> 2"],
                     FrameworkCensus.Compare(before, after, 5));
    }

    [Fact]
    public void The_harvest_names_each_member_by_the_runs_lookup_id_and_skips_what_is_no_census_member()
    {
        const string generated = ".concurrency-hunter/models/generated/Microsoft.NETCore.App/10.0";
        WriteFile(Path.Combine("run", generated, "System.Text.Json.json"), Generated("System.Text.Json",
            $"{{\"member\": \"{GET_TYPE_INFO}\", \"model\": {{\"member\": \"{GET_TYPE_INFO}\", \"assemblies\": [\"System.Text.Json\"], {RANGE_10}, \"effects\": {{}}}}}}",
            Refused(OPTIONS_COPY, "value-unnamed", "kept-user-object"),
            Refused("M:System.Text.Json.JsonSerializerOptions.NoSuchMember")));
        WriteFile(Path.Combine("run", generated, "System.Collections.Concurrent.json"), Generated("System.Collections.Concurrent",
            Refused("M:System.Collections.Concurrent.ConcurrentDictionary`2.TryAdd(`0,`1)")));

        var harvest = FrameworkCensus.Harvest([Path.Combine(_folder, "run")], Frameworks.Value);

        var model = Assert.Single(harvest.Answers, answer => answer.Model is not null);
        Assert.Equal(GET_TYPE_INFO + "~System.Text.Json.Serialization.Metadata.JsonTypeInfo", model.LookupId);
        Assert.Equal((FrameworkCensus.MODEL, NETCORE, "System.Text.Json"), (model.Outcome, model.Framework, model.Assembly));
        Assert.Equal(model.LookupId, model.Model!["member"]!.GetValue<string>());
        var refused = Assert.Single(harvest.Answers, answer => answer.Model is null);
        Assert.Equal((OPTIONS_COPY, "unknown-touch"), (refused.LookupId, refused.Outcome));
        Assert.Equal(["kept-user-object", "value-unnamed"], refused.Causes);
        Assert.Contains("NoSuchMember", Assert.Single(harvest.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_census_entry_answers_a_call_bound_to_the_reference_assemblies_and_one_bound_to_the_implementation()
    {
        // Roslyn names GetEnumerator by its nested return type as the assembly writes it: ~System.Buffers.ReadOnlySequence{`0}.Enumerator
        // in the reference assembly a program binds to, ~System.Buffers.ReadOnlySequence`1.Enumerator`0 in the implementation a generation's
        // decompiled code binds to.
        const string generated = ".concurrency-hunter/models/generated/Microsoft.NETCore.App/10.0";
        WriteFile(Path.Combine("run", generated, "System.Memory.json"),
                  Generated("System.Memory", Refused("M:System.Buffers.ReadOnlySequence`1.GetEnumerator", "value-unnamed")));
        var harvest = FrameworkCensus.Harvest([Path.Combine(_folder, "run")], Frameworks.Value);
        Directory.Delete(Path.Combine(_folder, "run"), recursive: true);

        Write(harvest.Answers);

        var models = new LibraryModels(BuiltInModelReader.Read(File.ReadAllBytes(Path.Combine(_folder, NETCORE, "System.Memory.json"))).Members, []);
        var reference = models.Find(GetEnumeratorCall(Frameworks.Value.ReferenceFiles));
        var implementation = models.Find(GetEnumeratorCall(Frameworks.Value.Files));
        Assert.Equal<(LibraryMatchKind?, bool?, LibraryMatchKind?, bool?)>((LibraryMatchKind.Opaque, true, LibraryMatchKind.Opaque, true),
                                                                          (reference?.Kind, reference?.DeclaredOpaque, implementation?.Kind,
                                                                           implementation?.DeclaredOpaque));
    }

    /// <summary>The method a call of <c>ReadOnlySequence&lt;byte&gt;.GetEnumerator</c> binds to in a compilation of the given references.</summary>
    /// <param name="references">The assembly files the call is compiled against.</param>
    private static IMethodSymbol GetEnumeratorCall(IEnumerable<string> references)
    {
        var tree = CSharpSyntaxTree.ParseText("class C { object M(System.Buffers.ReadOnlySequence<byte> s) => s.GetEnumerator(); }");
        var compilation = CSharpCompilation.Create("Caller", [tree], references.Select(file => MetadataReference.CreateFromFile(file)));
        var call = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        return (IMethodSymbol)compilation.GetSemanticModel(tree).GetSymbolInfo(call).Symbol!;
    }

    /// <summary>Writes the census of the given answers into the test's built-in folder, making the planned edits as the census tool does.</summary>
    /// <param name="answers">The regeneration's answers.</param>
    private CensusWrite Write(IReadOnlyList<CensusAnswer> answers)
    {
        var planned = FrameworkCensus.Plan(answers, _folder, Frameworks.Value, "abc123", new string('e', 64));
        foreach (var edit in planned.Edits)
        {
            if (edit.Bytes is null)
            {
                File.Delete(edit.Path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(edit.Path)!);
            File.WriteAllBytes(edit.Path, edit.Bytes);
        }
        return planned;
    }

    /// <summary>Writes a file below the test's folder, creating its folders.</summary>
    /// <param name="relative">The file's path below the folder.</param>
    /// <param name="text">Its text.</param>
    private string WriteFile(string relative, string text)
    {
        var path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>Reads a JSON file below the test's folder.</summary>
    /// <param name="parts">The file's path below the folder, by segment.</param>
    private JsonObject Read(params string[] parts) => JsonNode.Parse(File.ReadAllText(Path.Combine([_folder, .. parts])))!.AsObject();

    /// <summary>The members of a built-in file's models, in their order.</summary>
    /// <param name="file">The file.</param>
    private static string[] Members(JsonObject file) => [.. file["models"]!.AsArray().Select(entry => entry!["member"]!.GetValue<string>())];

    /// <summary>A topical file of an assembly at 8.0.0.0 to 11.0.0.0, with a model with no effects for each member.</summary>
    /// <param name="assembly">The file's assembly.</param>
    /// <param name="members">The members.</param>
    private static string Topical(string assembly, params string[] members) =>
        $"{{\"schemaVersion\": 1, \"assemblies\": [\"{assembly}\"], {RANGE_8_TO_11}, \"models\": [" +
        string.Join(", ", members.Select(member => $"{{\"member\": \"{member}\", \"effects\": {{}}}}")) + "]}";

    /// <summary>A census file of an assembly, with a model with no effects for each modelled member and the failed members.</summary>
    /// <param name="assembly">The file's assembly.</param>
    /// <param name="models">The modelled members.</param>
    /// <param name="failed">The failed members.</param>
    private static string Census(string assembly, string[] models, string[] failed) =>
        $"{{\"schemaVersion\": 1, \"assemblies\": [\"{assembly}\"], {RANGE_10}, \"models\": [" +
        string.Join(", ", models.Select(member => $"{{\"member\": \"{member}\", \"effects\": {{}}}}")) + "], \"failed\": [" +
        string.Join(", ", failed.Select(member => $"\"{member}\"")) + "]}";

    /// <summary>A generated file of a framework assembly with the given entries, of any generator version.</summary>
    /// <param name="assembly">The assembly.</param>
    /// <param name="entries">The entries' JSON.</param>
    private static string Generated(string assembly, params string[] entries) =>
        $"{{\"schemaVersion\": 2, \"generator\": \"another\", \"assembly\": {{\"name\": \"{assembly}\", \"version\": \"10.0.0.0\", \"mvid\": null, " +
        $"\"framework\": \"{NETCORE}\", \"major\": 10}}, \"members\": [{string.Join(", ", entries)}]}}";

    /// <summary>A generated entry that refuses a member with <c>unknown-touch</c> and the given causes.</summary>
    /// <param name="member">The member's declaration id as the generator writes it.</param>
    /// <param name="causes">The codes of the refusal's causes.</param>
    private static string Refused(string member, params string[] causes) =>
        $"{{\"member\": \"{member}\", \"refusal\": {{\"reason\": \"unknown-touch\", \"detail\": \"\", \"causes\": [{string.Join(", ", causes.Select(cause => $"\"{cause}\""))}]}}}}";

    /// <summary>An answer that gives a member of the frameworks a model with no effects, marked so that a test can tell it from the owner's.</summary>
    /// <param name="lookupId">The member's id in the run's form.</param>
    /// <param name="assembly">The member's assembly.</param>
    private static CensusAnswer Model(string lookupId, string assembly = LINQ) =>
        new(lookupId, NETCORE, assembly, FrameworkCensus.MODEL, [],
            new JsonObject
            {
                ["member"] = lookupId,
                ["assemblies"] = new JsonArray(assembly),
                ["versions"] = new JsonObject { ["minimum"] = "10.0.0.0", ["maximumExclusive"] = "10.0.0.1" },
                ["effects"] = new JsonObject(),
                ["note"] = "generated"
            });

    /// <summary>An answer that refuses a member of System.Linq.</summary>
    /// <param name="lookupId">The member's id in the run's form.</param>
    private static CensusAnswer Refusal(string lookupId) => new(lookupId, NETCORE, LINQ, "unknown-touch", ["kept-user-object"], null);

    /// <summary>A census row of a member of System.Linq.</summary>
    /// <param name="member">The member.</param>
    /// <param name="outcome">What the regeneration answered.</param>
    /// <param name="ships">What ships for it.</param>
    /// <param name="hash">The shipped model's hash, or empty.</param>
    private static CensusRow Row(string member, string outcome, string ships, string hash) => new(member, NETCORE, LINQ, outcome, [], ships, hash);

    /// <summary>A census record of the given rows, its NETCore.App at a patch and its ASP.NET Core at 10.0.13.</summary>
    /// <param name="patch">The NETCore.App patch.</param>
    /// <param name="rows">The rows.</param>
    private static CensusRecord Record(string patch, params CensusRow[] rows) =>
        new("abc123", new string('e', 64), new Dictionary<string, string> { [NETCORE] = patch, ["Microsoft.AspNetCore.App"] = "10.0.13" }, rows);

    /// <summary>A text's UTF-8 bytes.</summary>
    /// <param name="text">The text.</param>
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
