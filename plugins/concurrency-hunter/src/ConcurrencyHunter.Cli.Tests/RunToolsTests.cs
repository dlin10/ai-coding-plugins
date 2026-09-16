using System.Text;
using System.Text.Json;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Mcp;
using ConcurrencyHunter.Runs;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

public sealed class RunToolsTests
{
    [Fact]
    public async Task Every_tool_response_is_at_most_8_KB_with_200_groups()
    {
        using var environment = new RunTestEnvironment();
        var analysis = CreateAnalysis(Enumerable.Range(1, 200).Select(_ => "High").ToArray());
        var registry = environment.Registry((_, _) => Task.FromResult(Success(analysis)));

        var startJson = RunTools.run_start(registry, environment.SolutionPath);
        var runId = Parse(startJson).GetProperty("run_id").GetString()!;
        await registry.WaitForAnalysisAsync(runId);
        var responses = new[]
        {
            startJson,
            RunTools.run_poll(registry, runId),
            RunTools.get_groups(registry, runId),
            RunTools.submit_narrative(registry, runId, "unknown", "text"),
            RunTools.render_report(registry, runId)
        };

        Assert.All(responses, AssertFits);
        Assert.DoesNotContain(responses, response => response.Contains("responseTooLarge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Get_groups_pages_High_Medium_Low_and_filters_by_level()
    {
        using var environment = new RunTestEnvironment();
        var analysis = CreateAnalysis("Low", "Medium", "High", "Low", "High", "Medium", "High");
        var registry = environment.Registry((_, _) => Task.FromResult(Success(analysis)));
        var runId = Start(registry, environment.SolutionPath);
        await registry.WaitForAnalysisAsync(runId);

        var first = Parse(RunTools.get_groups(registry, runId));
        var second = Parse(RunTools.get_groups(registry, runId, 2));
        var medium = Parse(RunTools.get_groups(registry, runId, 1, "medium"));
        var allLabels = first.GetProperty("items").EnumerateArray()
            .Concat(second.GetProperty("items").EnumerateArray())
            .Select(group => group.GetProperty("confidenceLabel").GetString()!)
            .ToArray();

        Assert.Equal(["High", "High", "High", "Medium", "Medium", "Low", "Low"], allLabels);
        Assert.All(medium.GetProperty("items").EnumerateArray(), group =>
            Assert.Equal("Medium", group.GetProperty("confidenceLabel").GetString()));
    }

    [Fact]
    public async Task Demo_shaped_group_digest_is_at_most_1536_bytes_and_lists_at_most_three_findings()
    {
        using var environment = new RunTestEnvironment();
        var analysis = CreateAnalysis("High", findingsPerGroup: 4);
        var registry = environment.Registry((_, _) => Task.FromResult(Success(analysis)));
        var runId = Start(registry, environment.SolutionPath);
        await registry.WaitForAnalysisAsync(runId);

        var payload = Parse(RunTools.get_groups(registry, runId));
        var digest = Assert.Single(payload.GetProperty("items").EnumerateArray());

        Assert.True(Encoding.UTF8.GetByteCount(digest.GetRawText()) <= 1536);
        Assert.True(digest.GetProperty("findings").GetArrayLength() <= 3);
        Assert.Equal(4, digest.GetProperty("findingCount").GetInt32());
    }

    [Fact]
    public void Unknown_run_id_is_unknownRun_on_every_tool()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var responses = new[]
        {
            RunTools.run_poll(registry, "missing"),
            RunTools.get_groups(registry, "missing"),
            RunTools.submit_narrative(registry, "missing", "summary", "text"),
            RunTools.render_report(registry, "missing")
        };

        Assert.All(responses, response =>
            Assert.Equal("unknownRun", Parse(response).GetProperty("error").GetString()));
    }

    [Fact]
    public void Ambiguous_target_returns_candidates_without_a_run_id()
    {
        using var environment = new RunTestEnvironment();
        File.WriteAllText(Path.Combine(environment.Root, "Another.sln"), string.Empty);
        var registry = environment.Registry();

        var payload = Parse(RunTools.run_start(registry, environment.Root));

        Assert.False(payload.TryGetProperty("run_id", out _));
        Assert.Equal(2, payload.GetProperty("candidatesTotal").GetInt32());
        Assert.Equal(["Another.sln", "Demo.slnx"],
            payload.GetProperty("candidates").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Run_poll_reports_phase_state_counts_elapsed_and_remaining()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var runId = Start(registry, environment.SolutionPath);
        await registry.WaitForAnalysisAsync(runId);
        environment.Time.Advance(TimeSpan.FromSeconds(12));

        var payload = Parse(RunTools.run_poll(registry, runId));

        Assert.Equal("narrative", payload.GetProperty("phase").GetString());
        Assert.Equal("awaiting_narrative", payload.GetProperty("state").GetString());
        Assert.Equal(1, payload.GetProperty("counts").GetProperty("projectsLoaded").GetInt32());
        Assert.Equal(1, payload.GetProperty("counts").GetProperty("findings").GetInt32());
        Assert.Equal(12, payload.GetProperty("elapsed").GetInt64());
        Assert.Equal(1788, payload.GetProperty("remaining").GetInt64());
    }

    [Fact]
    public void Page_below_one_is_invalidPage()
    {
        using var environment = new RunTestEnvironment();

        var payload = Parse(RunTools.get_groups(environment.Registry(), "missing", 0));

        Assert.Equal("invalidPage", payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Oversized_unicode_group_digest_is_fitted_and_the_other_groups_stay_reachable()
    {
        using var environment = new RunTestEnvironment();
        var analysis = CreateAnalysis("High", "High", "Medium", "Low", "Low", "High", "Medium");
        analysis = WithOversizedFirstGroup(analysis, new string('я', 2000));
        var registry = environment.Registry((_, _) => Task.FromResult(Success(analysis)));
        var runId = Start(registry, environment.SolutionPath);
        await registry.WaitForAnalysisAsync(runId);
        var seen = new List<string>();

        for (var page = 1; ; page++)
        {
            var payload = Parse(RunTools.get_groups(registry, runId, page));
            var items = payload.GetProperty("items").EnumerateArray().ToArray();
            if (page > payload.GetProperty("pages").GetInt32())
                break;
            Assert.NotEmpty(items);
            Assert.All(items, item => Assert.True(Encoding.UTF8.GetByteCount(item.GetRawText()) <= 4096));
            seen.AddRange(items.Select(item => item.GetProperty("groupId").GetString()!));
        }

        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Group_digest_with_binding_and_overlap_evidence_stays_within_budget()
    {
        using var environment = new RunTestEnvironment();
        var realistic = WithEvidence(CreateAnalysis("High", findingsPerGroup: 4), 1);
        var oversized = WithEvidence(CreateAnalysis(Enumerable.Range(1, 12).Select(_ => "High").ToArray(), 4), 600);
        var realisticRegistry = environment.Registry((_, _) => Task.FromResult(Success(realistic)));
        var oversizedRegistry = environment.Registry((_, _) => Task.FromResult(Success(oversized)), Path.Combine(environment.Root, "oversized"));
        var realisticRun = Start(realisticRegistry, environment.SolutionPath);
        await realisticRegistry.WaitForAnalysisAsync(realisticRun);

        var realisticResponse = RunTools.get_groups(realisticRegistry, realisticRun);
        var digest = Assert.Single(Parse(realisticResponse).GetProperty("items").EnumerateArray());
        var finding = digest.GetProperty("findings")[0];
        var access = finding.GetProperty("accesses")[0];
        Assert.True(Encoding.UTF8.GetByteCount(digest.GetRawText()) <= 1536, $"Digest was {Encoding.UTF8.GetByteCount(digest.GetRawText())} bytes.");
        Assert.Equal("action Ns.Type1.Post() of controller Ns.Type1", access.GetProperty("root").GetString());
        Assert.Equal(["static:Ns.Sync.Gate"], access.GetProperty("heldProtection").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["AddSingleton registers Ns.State at src/Startup.cs:12"],
                     finding.GetProperty("bindingEvidence").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["The root action Ns.Type1.Post() may run concurrently with itself in scope Web."],
                     finding.GetProperty("overlapEvidence").EnumerateArray().Select(item => item.GetString()));

        var oversizedRun = Start(oversizedRegistry, environment.SolutionPath);
        await oversizedRegistry.WaitForAnalysisAsync(oversizedRun);
        var seen = new List<string>();
        for (var page = 1; ; page++)
        {
            var response = RunTools.get_groups(oversizedRegistry, oversizedRun, page);
            AssertFits(response);
            var payload = Parse(response);
            if (page > payload.GetProperty("pages").GetInt32())
                break;
            var items = payload.GetProperty("items").EnumerateArray().ToArray();
            Assert.NotEmpty(items);
            Assert.All(items, item => Assert.True(Encoding.UTF8.GetByteCount(item.GetRawText()) <= 4096));
            Assert.All(items, item => Assert.NotEmpty(item.GetProperty("findings")[0].GetProperty("bindingEvidence").EnumerateArray()));
            seen.AddRange(items.Select(item => item.GetProperty("groupId").GetString()!));
        }

        Assert.Equal(12, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Group_digest_with_ownership_and_call_steps_stays_within_budget()
    {
        using var environment = new RunTestEnvironment();
        var realistic = WithOwnershipAndCalls(CreateAnalysis("High", findingsPerGroup: 4), 1);
        var oversized = WithOwnershipAndCalls(CreateAnalysis(Enumerable.Range(1, 12).Select(_ => "High").ToArray(), 4), 600);
        var realisticRegistry = environment.Registry((_, _) => Task.FromResult(Success(realistic)));
        var oversizedRegistry = environment.Registry((_, _) => Task.FromResult(Success(oversized)), Path.Combine(environment.Root, "oversized"));
        var realisticRun = Start(realisticRegistry, environment.SolutionPath);
        await realisticRegistry.WaitForAnalysisAsync(realisticRun);

        var digest = Assert.Single(Parse(RunTools.get_groups(realisticRegistry, realisticRun)).GetProperty("items").EnumerateArray());
        Assert.True(Encoding.UTF8.GetByteCount(digest.GetRawText()) <= 1536, $"Digest was {Encoding.UTF8.GetByteCount(digest.GetRawText())} bytes.");
        Assert.Equal("Shared: static:Ns.Type1 is static storage.", digest.GetProperty("ownership").GetString());
        Assert.False(digest.TryGetProperty("sharingKey", out _));
        Assert.Equal(["calls Ns.Service.Run() on di:Ns.Service@Singleton", "calls Ns.Store.Write() on di:Ns.Store@Singleton"],
                     digest.GetProperty("findings")[0].GetProperty("accesses")[0].GetProperty("calls").EnumerateArray().Select(item => item.GetString()));

        var oversizedRun = Start(oversizedRegistry, environment.SolutionPath);
        await oversizedRegistry.WaitForAnalysisAsync(oversizedRun);
        var seen = new List<string>();
        for (var page = 1; ; page++)
        {
            var response = RunTools.get_groups(oversizedRegistry, oversizedRun, page);
            AssertFits(response);
            var payload = Parse(response);
            if (page > payload.GetProperty("pages").GetInt32())
                break;
            var items = payload.GetProperty("items").EnumerateArray().ToArray();
            Assert.NotEmpty(items);
            Assert.All(items, item => Assert.True(Encoding.UTF8.GetByteCount(item.GetRawText()) <= 4096));
            Assert.All(items, item => Assert.StartsWith("Shared: ", item.GetProperty("ownership").GetString(), StringComparison.Ordinal));
            Assert.All(items, item => Assert.NotEmpty(item.GetProperty("findings")[0].GetProperty("accesses")[0].GetProperty("calls").EnumerateArray()));
            seen.AddRange(items.Select(item => item.GetProperty("groupId").GetString()!));
        }

        Assert.Equal(12, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Rejection_with_600_long_unknown_citations_stays_under_8_KB()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var runId = Start(registry, environment.SolutionPath);
        await registry.WaitForAnalysisAsync(runId);
        var citations = string.Join(' ', Enumerable.Range(1, 600)
            .Select(number => $"[E:Unknown{number}{new string('x', 200)}]"));

        var response = RunTools.submit_narrative(registry, runId, "G1", citations);
        var payload = Parse(response);

        AssertFits(response);
        Assert.Equal("rejected", payload.GetProperty("status").GetString());
        Assert.Equal(20, payload.GetProperty("reasons").GetArrayLength());
        Assert.Equal(603, payload.GetProperty("reasonsTotal").GetInt32());
    }

    [Fact]
    public void Many_long_unicode_candidate_names_stay_whole_and_fit_8_KB_with_the_total()
    {
        using var environment = new RunTestEnvironment();
        File.Delete(environment.SolutionPath);
        var names = Enumerable.Range(1, 30)
            .Select(number => $"{number:D2}-{new string((char)('а' + number % 20), 60)}.slnx")
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var name in names)
            File.WriteAllText(Path.Combine(environment.Root, name), "<Solution />");

        var response = RunTools.run_start(environment.Registry(), environment.Root);
        var payload = Parse(response);
        var returned = payload.GetProperty("candidates").EnumerateArray().Select(item => item.GetString()).ToArray();

        AssertFits(response);
        Assert.Equal(names.Length, payload.GetProperty("candidatesTotal").GetInt32());
        Assert.NotEmpty(returned);
        Assert.True(returned.Length < names.Length);
        Assert.Equal(names.Take(returned.Length), returned);
        Assert.All(returned, name => Assert.DoesNotContain('…', name!));
    }

    [Fact]
    public async Task Long_unicode_warnings_reasons_and_messages_fit_8_KB()
    {
        using var environment = new RunTestEnvironment();
        var longText = new string('я', 2000);
        var failed = new RunAnalysis(
            true,
            false,
            30,
            0,
            Enumerable.Range(1, 30).Select(number => $"{number}-{longText}").ToArray(),
            null,
            [longText],
            0,
            0);
        var registry = environment.Registry((_, _) => Task.FromResult(failed));
        var runId = Start(registry, environment.SolutionPath);
        await registry.WaitForAnalysisAsync(runId);

        var poll = RunTools.run_poll(registry, runId);
        var render = RunTools.render_report(registry, runId);
        var longRootRegistry = environment.Registry(bundleRoot: Path.Combine(environment.Root, longText));
        var secondRunId = Start(longRootRegistry, environment.SolutionPath);
        await longRootRegistry.WaitForAnalysisAsync(secondRunId);
        var renderError = RunTools.render_report(longRootRegistry, secondRunId);

        Assert.All(new[] { poll, render, renderError }, AssertFits);
        Assert.Equal(30, Parse(poll).GetProperty("warningsTotal").GetInt32());
        Assert.Equal("renderFailed", Parse(renderError).GetProperty("error").GetString());
    }

    private static string Start(RunRegistry registry, string target) =>
        Parse(RunTools.run_start(registry, target)).GetProperty("run_id").GetString()!;

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertFits(string json) =>
        Assert.True(Encoding.UTF8.GetByteCount(json) <= 8192, $"Response was {Encoding.UTF8.GetByteCount(json)} bytes.");

    private static RunAnalysis Success(AnalysisResult analysis) =>
        new(false, false, 1, 1, [], analysis, [], 0.1, 0.2);

    private static AnalysisResult CreateAnalysis(params string[] labels) => CreateAnalysis(labels, 1);

    private static AnalysisResult CreateAnalysis(string label, int findingsPerGroup) =>
        CreateAnalysis([label], findingsPerGroup);

    private static AnalysisResult CreateAnalysis(IReadOnlyList<string> labels, int findingsPerGroup)
    {
        var findings = new List<Finding>();
        var groups = new List<FindingGroup>();
        var findingNumber = 0;
        for (var groupIndex = 0; groupIndex < labels.Count; groupIndex++)
        {
            var groupId = $"G{groupIndex + 1}";
            var resource = FindingTestData.Resource($"static:Ns.Type{groupIndex + 1}", $"Field{groupIndex + 1}");
            var groupFindingIds = new List<string>();
            for (var index = 0; index < findingsPerGroup; index++)
            {
                findingNumber++;
                var findingId = $"F{findingNumber}";
                var accessA = FindingTestData.Access(
                    resource,
                    AccessOperation.Write,
                    $"root-{findingNumber}",
                    $"Ns.Type{groupIndex + 1}.Post()",
                    new SourceSpan($"src/Type{groupIndex + 1}.cs", 10 + index, 1, 10 + index, 8));
                var accessB = accessA with { Operation = AccessOperation.Read };
                var evidence = new[] { "A", "B", "R", "O", "P", "S" }
                    .Select(suffix => new EvidenceItem($"{findingId}.{suffix}", suffix, suffix))
                    .ToArray();
                findings.Add(new Finding(
                    findingId,
                    $"stable-{findingId}",
                    groupId,
                    "DCA1001",
                    resource,
                    accessA,
                    accessB,
                    "unprotected",
                    new FindingConfidence(labels[groupIndex], 85, new ConfidenceComponents(25, 20, 20, 20, 0)),
                    ["Two actions overlap."],
                    ["A writes the field", "B reads the field", "B sees either value"],
                    ["Path feasibility is not analyzed in this version."],
                    evidence));
                groupFindingIds.Add(findingId);
            }

            groups.Add(FindingTestData.Group(groupId, labels[groupIndex], resource, groupFindingIds));
        }

        return FindingTestData.Result(findings, groups);
    }

    /// <summary>Gives every access a root display, a held protection and binding evidence, and every finding overlap
    /// evidence; <paramref name="repeat"/> above 1 pads each text with that many Cyrillic letters.</summary>
    private static AnalysisResult WithEvidence(AnalysisResult analysis, int repeat)
    {
        var padding = repeat > 1 ? new string('я', repeat) : "";
        Access Enrich(Access access) => access with
        {
            Root = access.Root with { Display = $"action {access.Symbol[..access.Symbol.IndexOf(".Post", StringComparison.Ordinal)]}.Post() of controller " +
                                                $"{access.Symbol[..access.Symbol.IndexOf(".Post", StringComparison.Ordinal)]}{padding}" },
            HeldProtection = [$"static:Ns.Sync.Gate{padding}"],
            BindingEvidence = [new BindingEvidence("registration", $"AddSingleton registers Ns.State{padding}", new SourceSpan("src/Startup.cs", 12, 1, 12, 30))]
        };

        var findings = analysis.Findings.Select(finding => finding with
        {
            AccessA = Enrich(finding.AccessA),
            AccessB = Enrich(finding.AccessB),
            ConcurrencyEvidence = [$"The root action {finding.AccessA.Symbol} may run concurrently with itself in scope Web.{padding}"]
        }).ToArray();
        return analysis with { Findings = findings };
    }

    /// <summary>Gives every access two call steps and every group's ownership evidence; <paramref name="repeat"/> above 1 pads each
    /// text with that many Cyrillic letters.</summary>
    private static AnalysisResult WithOwnershipAndCalls(AnalysisResult analysis, int repeat)
    {
        var padding = repeat > 1 ? new string('я', repeat) : "";
        Access Enrich(Access access) => access with
        {
            CodeFlow =
            [
                new CodeFlowStep("root", $"action {access.Symbol} starts", access.Source),
                new CodeFlowStep("call", $"calls Ns.Service.Run() on di:Ns.Service@Singleton{padding}", access.Source),
                new CodeFlowStep("call", $"calls Ns.Store.Write() on di:Ns.Store@Singleton{padding}", access.Source),
                new CodeFlowStep("access", "write field", access.Source)
            ]
        };

        var findings = analysis.Findings.Select(finding => finding with { AccessA = Enrich(finding.AccessA), AccessB = Enrich(finding.AccessB) }).ToArray();
        var groups = analysis.Groups.Select(group => group with { OwnershipEvidence = group.OwnershipEvidence.Select(item => item + padding).ToArray() })
                             .ToArray();
        return analysis with { Findings = findings, Groups = groups };
    }

    private static AnalysisResult WithOversizedFirstGroup(AnalysisResult analysis, string text)
    {
        var original = analysis.Findings[0];
        var resource = FindingTestData.Resource($"static:{text}", text, text) with { AccessPath = [text, text, text, text] };
        var root = original.AccessA.Root with { Symbol = text, Display = text };
        var accessA = original.AccessA with
        {
            Resource = resource,
            Root = root,
            Symbol = text,
            Source = original.AccessA.Source with { Path = text + ".cs" },
            HeldProtection = [text]
        };
        var accessB = original.AccessB with
        {
            Resource = resource,
            Root = root,
            Symbol = text,
            Source = original.AccessB.Source with { Path = text + ".cs" },
            HeldProtection = [text]
        };
        var finding = original with
        {
            Resource = resource,
            AccessA = accessA,
            AccessB = accessB,
            ProtectionResult = text,
            Scenario = [text, text, text],
            Evidence = original.Evidence.Select(item => item with { Id = text, Text = text }).ToArray()
        };
        var findings = analysis.Findings.ToArray();
        findings[0] = finding;
        var groups = analysis.Groups.ToArray();
        groups[0] = groups[0] with { Resource = resource };
        return analysis with { Findings = findings, Groups = groups };
    }
}
