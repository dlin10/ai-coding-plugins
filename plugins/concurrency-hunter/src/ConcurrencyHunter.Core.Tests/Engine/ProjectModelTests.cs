using System.Text;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Reporting;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ProjectModelTests
{
    private const string SERIALIZE = "M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes``1(``0,System.Text.Json.JsonSerializerOptions)~System.Byte[]";
    private const string WORK = "System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_state);";
    private const string MODEL_PATH = ".concurrency-hunter/models/model.json";

    [Fact]
    public async Task Project_entry_overrides_the_built_in_effects()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"effects\": {\"value\": [\"writes-arg\"]}")));
        var result = await Run(repo);

        Assert.Equal(1, Counter(result, CoverageCounters.KNOWN_CALL_PROJECT));
        Assert.Contains(result.Findings, finding => finding.Resource.AccessPath.SequenceEqual(["Count"]));
    }

    [Fact]
    public async Task Project_opaque_entry_makes_a_built_in_member_opaque_with_its_gap()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true")));
        var result = await Run(repo);

        AssertOpaque(result);
    }

    [Fact]
    public async Task Project_opaque_entry_overrides_the_immutable_type_rule()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true", "M:System.String.Concat(System.String,System.String)~System.String", "System.Private.CoreLib")));
        var result = await Run(repo, "_ = string.Concat(_state.Text, _state.Text);");

        Assert.Equal(1, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Equal(0, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
    }

    [Fact]
    public async Task Project_entry_describes_a_member_no_built_in_model_covers()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"effects\": {}", "M:System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(System.Object)~System.Int32", "System.Private.CoreLib")));
        var result = await Run(repo, "System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_state);");

        Assert.Equal(1, Counter(result, CoverageCounters.KNOWN_CALL_PROJECT));
    }

    [Fact]
    public async Task Empty_effects_make_a_known_call_without_access()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"effects\": {}")));
        var result = await Run(repo);

        Assert.Equal(1, Counter(result, CoverageCounters.KNOWN_CALL_PROJECT));
        Assert.DoesNotContain(result.Findings, finding => finding.Resource.AccessPath.SequenceEqual(["Count"]));
    }

    [Fact]
    public async Task Parameter_with_two_effect_kinds_gets_both()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"effects\": {\"value\": [\"reads-deep\", \"writes-arg\"]}")));
        var result = await Run(repo);

        Assert.Equal(1, Counter(result, CoverageCounters.KNOWN_CALL_PROJECT));
        Assert.Contains(result.Findings, finding => finding.Resource.AccessPath.SequenceEqual(["Count"]));
    }

    [Fact]
    public async Task Entry_without_the_return_type_suffix_matches()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true", SERIALIZE.Split('~')[0])));

        AssertOpaque(await Run(repo));
    }

    [Fact]
    public async Task Conversion_operator_without_its_return_type_is_rejected()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true", "M:System.String.op_Implicit(System.String)")));

        AssertRejected(await Run(repo), "op_Implicit");
    }

    [Fact]
    public async Task File_versions_are_the_default_of_its_entries()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true"), versions: Range(8, 11)));

        AssertOpaque(await Run(repo));
    }

    [Fact]
    public async Task Entry_assemblies_and_versions_override_the_file_defaults()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"assemblies\": [\"System.Text.Json\"], \"versions\": " + Range(8, 11) + ", \"opaque\": true"),
                             assemblies: "[\"System.Runtime\"]", versions: Range(11, 12)));

        AssertOpaque(await Run(repo));
    }

    [Fact]
    public async Task Entry_without_versions_applies_to_every_version()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true")));

        AssertOpaque(await Run(repo));
    }

    [Fact]
    public async Task Entry_outside_its_versions_is_rejected()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"versions\": " + Range(11, 12) + ", \"opaque\": true")));

        AssertRejected(await Run(repo), SERIALIZE);
    }

    [Fact]
    public async Task Entry_of_another_assembly_is_rejected()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"assemblies\": [\"System.Runtime\"], \"opaque\": true")));

        AssertRejected(await Run(repo), SERIALIZE);
    }

    [Fact]
    public async Task Id_resolving_to_nothing_is_rejected()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true", "M:System.Text.Json.JsonSerializer.NoSuchMethod(System.Object)")));

        AssertRejected(await Run(repo), "NoSuchMethod");
    }

    [Fact]
    public async Task Setter_entry_is_rejected()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"assemblies\": [\"Fixture\"], \"opaque\": true", "M:State.set_Value(System.Int32)")));

        AssertRejected(await Run(repo), "set_Value");
    }

    [Fact]
    public async Task Member_taking_a_delegate_is_rejected()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"assemblies\": [\"System.Linq\"], \"opaque\": true",
                                   "M:System.Linq.Enumerable.Any``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})~System.Boolean")));

        AssertRejected(await Run(repo), "Enumerable.Any");
    }

    [Fact]
    public async Task Member_of_a_recognized_type_is_rejected()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"assemblies\": [\"System.Collections\"], \"opaque\": true",
                                   "M:System.Collections.Generic.List`1.Add(`0)")));

        AssertRejected(await Run(repo), "List`1.Add");
    }

    [Fact]
    public async Task Member_with_a_body_in_the_run_is_rejected()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"assemblies\": [\"Fixture\"], \"opaque\": true", "M:State.Touch")));

        AssertRejected(await Run(repo, "_state.Touch();"), "State.Touch");
    }

    [Fact]
    public async Task Effect_naming_a_missing_parameter_is_rejected()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"effects\": {\"missing\": [\"writes-arg\"]}")));

        AssertRejected(await Run(repo), SERIALIZE);
    }

    [Fact]
    public async Task Model_file_with_a_bom_applies()
    {
        using var repo = new Repository();
        repo.Model("\uFEFF" + FileModel(Entry("\"opaque\": true")));

        AssertOpaque(await Run(repo));
    }

    [Fact]
    public async Task Unreadable_file_is_rejected_and_the_run_continues()
    {
        using var repo = new Repository();
        var blocked = repo.Model(FileModel(Entry("\"opaque\": true")), "blocked.json");
        repo.Model(FileModel(Entry("\"opaque\": true")), "good.json");
        using var held = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await Run(repo);

        Assert.Equal(1, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.Equal(1, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Contains(Assert.Single(result.Coverage).Diagnostics, diagnostic => diagnostic.Contains("blocked.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unlistable_models_subfolder_is_rejected_and_the_run_continues()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true")), "good.json");
        var blocked = Path.Combine(repo.Models, "blocked");
        Directory.CreateDirectory(blocked);
        using var denied = DenyListing(blocked);
        var result = await Run(repo);

        Assert.Equal(1, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.Equal(1, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Contains(Assert.Single(result.Coverage).Diagnostics, diagnostic => diagnostic.Contains("models/blocked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unlistable_models_folder_is_rejected_and_nothing_is_read_or_written()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true")));
        var lockPath = Path.Combine(repo.Models, "models.lock.json");
        File.WriteAllText(lockPath, "keep me");
        using var denied = DenyListing(repo.Models);
        var result = await Run(repo);

        Assert.Equal(1, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.Equal(0, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Contains(Assert.Single(result.Coverage).Diagnostics, diagnostic => diagnostic.Contains(".concurrency-hunter/models:", StringComparison.Ordinal));
        denied.Dispose();
        Assert.Equal("keep me", File.ReadAllText(lockPath));
    }

    [Fact]
    public async Task Directory_link_under_models_is_not_followed()
    {
        using var repo = new Repository();
        var outside = Path.Combine(repo.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "model.json"), FileModel(Entry("\"opaque\": true")));
        var link = Path.Combine(repo.Models, "linked");
        Directory.CreateDirectory(repo.Models);
        Junction(link, outside);
        try
        {
            var result = await Run(repo);
            Assert.Equal(0, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
            Assert.Equal(0, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task Model_file_in_a_nested_folder_applies()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true")), "nested/child/model.json");

        AssertOpaque(await Run(repo));
    }

    [Fact]
    public async Task Hidden_model_file_applies()
    {
        using var repo = new Repository();
        var path = repo.Model(FileModel(Entry("\"opaque\": true")), ".hidden.json");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);

        AssertOpaque(await Run(repo));
    }

    [Fact]
    public async Task Generated_ai_and_lock_are_skipped_whatever_their_case()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true")), "GeNeRaTeD/model.json");
        repo.Model(FileModel(Entry("\"opaque\": true")), "AI/model.json");
        repo.Model("not JSON", "MoDeLs.LoCk.JsOn");
        var result = await Run(repo);

        Assert.Equal(0, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Equal(0, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
    }

    [Fact]
    public async Task No_models_folder_reads_and_writes_nothing()
    {
        using var repo = new Repository();
        var result = await Run(repo);

        Assert.Equal(0, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.False(Directory.Exists(repo.Models));
    }

    [Fact]
    public async Task Analysis_reads_the_models_of_a_repository_root_above_the_solution()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true")));
        var solution = Directory.CreateDirectory(Path.Combine(repo.Root, "src", "app")).FullName;

        AssertOpaque(await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Source(WORK))), solution, CancellationToken.None));
    }

    [Fact]
    public async Task Analysis_uses_the_nearest_folder_holding_the_directory()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true")));
        var near = Path.Combine(repo.Root, "near");
        var nearModels = Path.Combine(near, ".concurrency-hunter", "models");
        Directory.CreateDirectory(nearModels);
        File.WriteAllText(Path.Combine(nearModels, "near.json"), FileModel(Entry("\"effects\": {}")));
        var solution = Directory.CreateDirectory(Path.Combine(near, "src")).FullName;
        var result = await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Source(WORK))), solution, CancellationToken.None);

        Assert.Equal(1, Counter(result, CoverageCounters.KNOWN_CALL_PROJECT));
        Assert.Equal(0, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
    }

    [Fact]
    public async Task Two_disagreeing_entries_leave_the_member_opaque()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true")));
        repo.Model(FileModel(Entry("\"effects\": {}")), "other.json");
        var result = await Run(repo);

        Assert.Equal(1, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Equal(2, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
    }

    [Fact]
    public async Task Three_entries_with_one_disagreeing_reject_all_three()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true")));
        repo.Model(FileModel(Entry("\"opaque\": true")), "same.json");
        repo.Model(FileModel(Entry("\"effects\": {}")), "different.json");
        var result = await Run(repo);

        Assert.Equal(1, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Equal(3, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.Equal(3, Assert.Single(result.Coverage).Diagnostics.Count(diagnostic => diagnostic.StartsWith("library-models:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Entries_deciding_alike_in_another_order_count_once()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"effects\": {\"value\": [\"reads-deep\", \"writes-arg\"]}")));
        repo.Model(FileModel(Entry("\"effects\": {\"value\": [\"writes-arg\", \"reads-deep\"]}")), "other.json");
        var result = await Run(repo);

        Assert.Equal(1, Counter(result, CoverageCounters.KNOWN_CALL_PROJECT));
        Assert.Equal(0, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
    }

    [Fact]
    public async Task Entry_for_an_assembly_the_scope_does_not_reference_is_ignored()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"assemblies\": [\"Missing.Assembly\"], \"opaque\": true")));
        var result = await Run(repo);

        Assert.Equal(0, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.Equal(0, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
    }

    [Fact]
    public async Task Malformed_entry_for_an_assembly_the_scope_does_not_reference_is_still_rejected()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"assemblies\": [\"Missing.Assembly\"], \"opaque\": false")));

        AssertRejected(await Run(repo), SERIALIZE);
    }

    [Fact]
    public async Task Known_call_counters_split_by_layer()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"effects\": {}")));
        var result = await Run(repo, WORK + " _ = _state.Text.Length;");

        Assert.Equal(Counter(result, CoverageCounters.KNOWN_CALL_BUILT_IN) + Counter(result, CoverageCounters.KNOWN_CALL_PROJECT),
                     Counter(result, CoverageCounters.KNOWN_CALL));
        Assert.True(Counter(result, CoverageCounters.KNOWN_CALL_BUILT_IN) > 0);
        Assert.Equal(1, Counter(result, CoverageCounters.KNOWN_CALL_PROJECT));
    }

    [Fact]
    public async Task Declared_opaque_call_hands_its_delegate_to_an_unknown_execution()
    {
        using var repo = new Repository();
        repo.Model(FileModel(Entry("\"opaque\": true")));
        const string WORK_WITH_DELEGATE = "System.Text.Json.JsonSerializer.SerializeToUtf8Bytes((Action)(() => _state.Count++));";
        var result = await Run(repo, WORK_WITH_DELEGATE);
        using var noModel = new Repository();
        var known = await Run(noModel, WORK_WITH_DELEGATE);

        Assert.Equal(1, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Equal(1, Counter(result, CoverageCounters.DELEGATE_TO_OPAQUE));
        Assert.Equal(Counter(known, CoverageCounters.REACHABLE_BODIES) + 1, Counter(result, CoverageCounters.REACHABLE_BODIES));
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Count"]) &&
                                                  access.PathRoot.RootKind == "unknown-delegate-call");
    }

    [Fact]
    public async Task File_rejection_is_counted_and_named_in_every_scope()
    {
        using var repo = new Repository();
        repo.Model("not JSON");
        var options = new FixtureOptions { ProjectOutputKinds = new Dictionary<string, Microsoft.CodeAnalysis.OutputKind>
        {
            ["A"] = Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
            ["B"] = Microsoft.CodeAnalysis.OutputKind.ConsoleApplication
        } };
        var source = Source(WORK) + "public static class Program { public static void Main() { } }";
        var solution = FixtureSolution.CreateProjects(options, ("A", "Case.cs", source), ("B", "Case.cs", source));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, repo.Root, CancellationToken.None);

        Assert.Equal(2, result.Coverage.Count);
        Assert.All(result.Coverage, scope =>
        {
            Assert.Equal(1, scope.Skips.GetValueOrDefault(CoverageCounters.MODEL_ENTRY_REJECTED));
            Assert.Single(scope.Diagnostics, diagnostic => diagnostic.StartsWith("library-models: " + MODEL_PATH + ": ", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Report_explains_the_layer_counters()
    {
        using var repo = new Repository();
        var result = await Run(repo);
        var lines = ReportRenderer.Render(ReportingTestData.CreateReport(result)).ReportMarkdown.Split('\n');
        var meanings = new Dictionary<string, string>
        {
            [CoverageCounters.KNOWN_CALL_BUILT_IN] = "known calls a built-in library model describes",
            [CoverageCounters.KNOWN_CALL_PROJECT] = "known calls a project library model describes",
            [CoverageCounters.OPAQUE_BY_PROJECT] = "opaque calls whose member a project model declares opaque",
            [CoverageCounters.MODEL_ENTRY_REJECTED] = "project model files, entries and pattern overloads that were rejected; the library-models diagnostics name each and why"
        };
        foreach (var (name, meaning) in meanings)
            Assert.Contains(lines, line => line.StartsWith("  - " + name + " ", StringComparison.Ordinal) && line.EndsWith(": " + meaning, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Project_immutable_types_are_rejected()
    {
        using var repo = new Repository();
        repo.Model("{\"schemaVersion\":1,\"models\":[],\"immutableTypes\":[{\"type\":\"T:System.String\"}]}");
        var result = await Run(repo);

        AssertRejected(result, "immutableTypes[0]");
    }

    [Theory]
    [MemberData(nameof(RejectedFileCases))]
    public async Task Rejected_project_files(string name, byte[] bytes)
    {
        Assert.NotEmpty(name);
        using var repo = new Repository();
        repo.ModelBytes(bytes);
        var result = await Run(repo);
        var scope = Assert.Single(result.Coverage);

        Assert.Equal(1, scope.Skips.GetValueOrDefault(CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.Equal(0, scope.Skips.GetValueOrDefault(CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Single(scope.Diagnostics, diagnostic => diagnostic.StartsWith("library-models: " + MODEL_PATH + ": ", StringComparison.Ordinal) &&
                                                       !diagnostic.Contains(SERIALIZE, StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> RejectedFileCases()
    {
        static object[] Row(string name, string text) => [name, Encoding.UTF8.GetBytes(text)];
        yield return Row("not JSON", "not JSON");
        yield return ["UTF-16", Encoding.Unicode.GetBytes(FileModel(Entry("\"opaque\": true")))];
        yield return ["invalid UTF-8", new byte[] { 0xFF, 0xFE, 0x80 }];
        yield return Row("comment", "{/*comment*/\"schemaVersion\":1,\"models\":[]}");
        yield return Row("trailing comma", "{\"schemaVersion\":1,\"models\":[],}");
        yield return Row("array", "[]");
        yield return Row("null", "null");
        yield return Row("string", "\"text\"");
        yield return Row("unknown top property", "{\"schemaVersion\":1,\"models\":[],\"extra\":1}");
        yield return Row("repeated top property", "{\"schemaVersion\":1,\"schemaVersion\":1,\"models\":[]}");
        yield return Row("schemaVersion 2", "{\"schemaVersion\":2,\"models\":[]}");
        yield return Row("missing schemaVersion", "{\"models\":[]}");
        yield return Row("missing models", "{\"schemaVersion\":1}");
        yield return Row("models not array", "{\"schemaVersion\":1,\"models\":{}}");
        yield return Row("immutableTypes not array", "{\"schemaVersion\":1,\"models\":[],\"immutableTypes\":{}}");
        yield return Row("note not string", "{\"schemaVersion\":1,\"models\":[],\"note\":4}");
        yield return Row("assemblies malformed", "{\"schemaVersion\":1,\"models\":[],\"assemblies\":[\"\"]}");
        yield return Row("versions malformed", "{\"schemaVersion\":1,\"models\":[],\"versions\":{\"minimum\":\"bad\",\"maximumExclusive\":\"11.0.0.0\"}}");
        yield return Row("two-part version", "{\"schemaVersion\":1,\"models\":[],\"versions\":{\"minimum\":\"8.0\",\"maximumExclusive\":\"11.0.0.0\"}}");
        yield return Row("three-part version", "{\"schemaVersion\":1,\"models\":[],\"versions\":{\"minimum\":\"8.0.0\",\"maximumExclusive\":\"11.0.0.0\"}}");
    }

    [Theory]
    [MemberData(nameof(RejectedEntryCases))]
    public async Task Rejected_project_entries(string name, string invalid)
    {
        Assert.NotEmpty(name);
        using var repo = new Repository();
        var valid = Entry("\"assemblies\": [\"System.Text.Json\"], \"opaque\": true");
        repo.Model("{\"schemaVersion\":1,\"models\":[" + invalid + "," + valid + "]}");
        var result = await Run(repo);
        var scope = Assert.Single(result.Coverage);

        Assert.Equal(1, scope.Skips.GetValueOrDefault(CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.Equal(1, scope.Skips.GetValueOrDefault(CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Single(scope.Diagnostics, diagnostic => diagnostic.StartsWith("library-models: " + MODEL_PATH + ": ", StringComparison.Ordinal) &&
                                                       (diagnostic.Contains(SERIALIZE, StringComparison.Ordinal) ||
                                                        diagnostic.Contains("models[0]", StringComparison.Ordinal) ||
                                                        diagnostic.Contains("not-an-id", StringComparison.Ordinal) ||
                                                        diagnostic.Contains("M:A.B.Run~", StringComparison.Ordinal) ||
                                                        diagnostic.Contains("M:A.B.Run((System.Int32)", StringComparison.Ordinal) ||
                                                        diagnostic.Contains("M:A.B.Name((*)", StringComparison.Ordinal)));
    }

    public static IEnumerable<object[]> RejectedEntryCases()
    {
        static object[] Row(string name, string text) => [name, text];
        static string Bad(string properties) => "{\"member\":\"" + SERIALIZE + "\",\"assemblies\":[\"System.Text.Json\"]," + properties + "}";
        yield return Row("not object", "null");
        yield return Row("unknown property", Bad("\"opaque\":true,\"extra\":1"));
        yield return Row("repeated property", Bad("\"opaque\":true,\"opaque\":true"));
        yield return Row("member not string", "{\"member\":4,\"assemblies\":[\"System.Text.Json\"],\"opaque\":true}");
        yield return Row("assemblies not array", "{\"member\":\"" + SERIALIZE + "\",\"assemblies\":\"System.Text.Json\",\"opaque\":true}");
        yield return Row("version malformed", Bad("\"versions\":{\"minimum\":\"bad\",\"maximumExclusive\":\"11.0.0.0\"},\"opaque\":true"));
        yield return Row("two-part version", Bad("\"versions\":{\"minimum\":\"8.0\",\"maximumExclusive\":\"11.0.0.0\"},\"opaque\":true"));
        yield return Row("three-part version", Bad("\"versions\":{\"minimum\":\"8.0.0\",\"maximumExclusive\":\"11.0.0.0\"},\"opaque\":true"));
        yield return Row("repeated version property", Bad("\"versions\":{\"minimum\":\"8.0.0.0\",\"minimum\":\"8.0.0.0\",\"maximumExclusive\":\"11.0.0.0\"},\"opaque\":true"));
        yield return Row("inverted version", Bad("\"versions\":{\"minimum\":\"11.0.0.0\",\"maximumExclusive\":\"8.0.0.0\"},\"opaque\":true"));
        yield return Row("effects not object", Bad("\"effects\":[]"));
        yield return Row("repeated parameter", Bad("\"effects\":{\"value\":[\"reads-deep\"],\"value\":[\"writes-arg\"]}"));
        yield return Row("effect value not array", Bad("\"effects\":{\"value\":\"reads-deep\"}"));
        yield return Row("empty kinds", Bad("\"effects\":{\"value\":[]}"));
        yield return Row("repeated kind", Bad("\"effects\":{\"value\":[\"reads-deep\",\"reads-deep\"]}"));
        yield return Row("unknown kind", Bad("\"effects\":{\"value\":[\"other\"]}"));
        yield return Row("opaque false", Bad("\"opaque\":false"));
        yield return Row("opaque and effects", Bad("\"opaque\":true,\"effects\":{}"));
        yield return Row("neither", Bad("\"note\":\"none\""));
        yield return Row("note not string", Bad("\"opaque\":true,\"note\":8"));
        yield return Row("id not M", "{\"member\":\"not-an-id\",\"assemblies\":[\"System.Text.Json\"],\"opaque\":true}");
        yield return Row("empty return type", "{\"member\":\"M:A.B.Run~\",\"assemblies\":[\"System.Text.Json\"],\"opaque\":true}");
        yield return Row("nested member parenthesis", "{\"member\":\"M:A.B.Run((System.Int32)\",\"assemblies\":[\"System.Text.Json\"],\"opaque\":true}");
        yield return Row("nested pattern parenthesis", "{\"member\":\"M:A.B.Name((*)\",\"assemblies\":[\"System.Text.Json\"],\"opaque\":true}");
        yield return Row("no assembly", "{\"member\":\"" + SERIALIZE + "\",\"opaque\":true}");
    }

    [Theory]
    [InlineData("M:A.B.Run((System.Int32)")]
    [InlineData("M:A.B.Name((*)")]
    [InlineData("M:A.B.Run(System.Int32,)")]
    [InlineData("M:A-B.Store.Save(*)")]
    [InlineData("M:A.B.Run!")]
    [InlineData("M:A.B.Run!(*)")]
    public async Task Malformed_member_is_rejected_before_assembly_matching(string member)
    {
        using var repo = new Repository();
        repo.Model("{\"schemaVersion\":1,\"models\":[" + Entry("\"assemblies\":[\"NotReferenced\"],\"opaque\":true", member) + "," +
                   Entry("\"assemblies\":[\"System.Text.Json\"],\"opaque\":true") + "]}");
        var result = await Run(repo);

        Assert.Equal(1, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.Equal(1, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Contains(Assert.Single(result.Coverage).Diagnostics, diagnostic => diagnostic.Contains(member, StringComparison.Ordinal));
    }

    private static string Entry(string decision, string member = SERIALIZE, string? assembly = null) =>
        "{\"member\": \"" + member + "\", " + (assembly is null ? "" : "\"assemblies\": [\"" + assembly + "\"], ") + decision + "}";

    private static string FileModel(string entry, string assemblies = "[\"System.Text.Json\"]", string? versions = null) =>
        "{\"schemaVersion\": 1, \"assemblies\": " + assemblies + ", " +
        (versions is null ? "" : "\"versions\": " + versions + ", ") + "\"models\": [" + entry + "]}";

    private static string Range(int minimum, int maximum) =>
        "{\"minimum\": \"" + minimum + ".0.0.0\", \"maximumExclusive\": \"" + maximum + ".0.0.0\"}";

    private static int Counter(AnalysisResult result, string name) => Assert.Single(result.Coverage).Skips.GetValueOrDefault(name);

    private static void AssertOpaque(AnalysisResult result)
    {
        Assert.Equal(1, Counter(result, CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Equal(0, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.Contains(Assert.Single(result.Coverage).Gaps,
                        gap => gap.Callee.StartsWith("System.Text.Json.JsonSerializer.SerializeToUtf8Bytes", StringComparison.Ordinal) ||
                               gap.Callee.Contains("String.Concat", StringComparison.Ordinal));
    }

    private static void AssertRejected(AnalysisResult result, string member)
    {
        var coverage = Assert.Single(result.Coverage);
        Assert.Equal(1, Counter(result, CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.Contains(coverage.Diagnostics, diagnostic => diagnostic.StartsWith("library-models: " + MODEL_PATH, StringComparison.Ordinal) &&
                                                            diagnostic.Contains(member, StringComparison.Ordinal));
    }

    private static Task<AnalysisResult> Run(Repository repo, string work = WORK, FixtureOptions? options = null) =>
        PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(options ?? new FixtureOptions(), ("Case.cs", Source(work))),
                                      ROOT_DIRECTORY, repo.Root, CancellationToken.None);

    private static string Source(string work) => Usings + $$"""
        public sealed class State
        {
            public int Count = 1;
            public string Text = "";
            public int Value { get; set; }
            public void Touch() { Count++; }
        }

        [ApiController]
        public sealed class StateController : ControllerBase
        {
            private readonly State _state;
            public StateController(State state) => _state = state;
            [HttpGet("/state")]
            public int Get() => _state.Count;
        }

        public sealed class StateWorker : BackgroundService
        {
            private readonly State _state;
            public StateWorker(State state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<StateWorker>();");

    private static IDisposable DenyListing(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Directory ACL tests run on Windows.");
        var directory = new DirectoryInfo(path);
        var modified = directory.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory,
                                            AccessControlType.Deny);
        modified.AddAccessRule(rule);
        directory.SetAccessControl(modified);
        return new Restore(() =>
        {
            if (OperatingSystem.IsWindows())
            {
                modified.RemoveAccessRuleSpecific(rule);
                directory.SetAccessControl(modified);
            }
        });
    }

    private static void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private sealed class Repository : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("ch-model-").FullName;
        public string Models => Path.Combine(Root, ".concurrency-hunter", "models");

        public string Model(string text, string name = "model.json")
        {
            var path = Path.Combine(Models, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        public string ModelBytes(byte[] bytes, string name = "model.json")
        {
            var path = Path.Combine(Models, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
