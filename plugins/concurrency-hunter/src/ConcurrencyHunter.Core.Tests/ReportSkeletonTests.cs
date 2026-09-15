using System.Text.Json;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Reporting;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class ReportSkeletonTests
{
    private const string Source = """
        public static class Gates { public static readonly object G = new(); }
        public static class Hits { public static int Count; }
        public sealed class Ledger { public string? Entry; }
        public class LedgerController(Ledger ledger) : ControllerBase
        {
            public void Post() { lock (Gates.G) { ledger.Entry = "request"; } new Ledger().Entry = "local"; Hits.Count++; }
        }
        public sealed class LedgerWorker(Ledger ledger) : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken) { ledger.Entry = null; return Task.CompletedTask; }
        }
        """;

    private static readonly string[] Sections =
    [
        "### Executive summary", "### Coverage", "### High findings", "### Medium findings", "### Low findings",
        "### Suppressed findings", "### Diagnostics"
    ];

    [Fact]
    public async Task Status_table_carries_the_ir_schema_and_providers_with_version_ranges()
    {
        var markdown = (await Render()).Bundle.ReportMarkdown;

        Assert.Contains("| IR schema | 1.0 |\n", markdown, StringComparison.Ordinal);
        Assert.Contains("| Providers | aspnetcore (Microsoft.AspNetCore.Mvc.Core 8.0.0.0..11.0.0.0, Microsoft.AspNetCore.Mvc 8.0.0.0..11.0.0.0, " +
                        "Microsoft.AspNetCore.Routing 8.0.0.0..11.0.0.0, Microsoft.AspNetCore.Http.Abstractions 8.0.0.0..11.0.0.0, " +
                        "Microsoft.Extensions.DependencyInjection.Abstractions 8.0.0.0..11.0.0.0); " +
                        "hosting (Microsoft.Extensions.Hosting.Abstractions 8.0.0.0..11.0.0.0) |\n", markdown, StringComparison.Ordinal);
        Assert.True(markdown.IndexOf("| Version |", StringComparison.Ordinal) < markdown.IndexOf("| IR schema |", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sections_are_the_skeleton_in_order_and_nothing_else()
    {
        var markdown = (await Render()).Bundle.ReportMarkdown;

        var headings = markdown.Split('\n').Where(line => line.StartsWith("### ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(Sections, headings);
    }

    [Fact]
    public async Task Coverage_lists_each_scope_with_roots_diagnostics_registrations_and_skips()
    {
        var markdown = (await Render()).Bundle.ReportMarkdown;
        var coverage = Section(markdown, "### Coverage");

        Assert.Contains("- Execution roots: 2\n", coverage, StringComparison.Ordinal);
        Assert.Contains("- Process scope solution: no executable project; projects Fixture\n", coverage, StringComparison.Ordinal);
        Assert.Contains("  - Roots per provider: aspnetcore 1, hosting 1\n", coverage, StringComparison.Ordinal);
        Assert.Contains("  - Diagnostics from aspnetcore: none\n", coverage, StringComparison.Ordinal);
        Assert.Contains("  - Diagnostics from hosting: UnresolvedBinding LedgerWorker: Hosted service LedgerWorker is registered", coverage,
                        StringComparison.Ordinal);
        Assert.Contains("  - Registrations: 3\n", coverage, StringComparison.Ordinal);
        Assert.Contains("  - DI diagnostics: UnresolvedBinding LedgerWorker:", coverage, StringComparison.Ordinal);
        Assert.Contains("  - Skipped accesses: allocation 1, local 1\n", coverage, StringComparison.Ordinal);
        Assert.Contains("  - Other diagnostics: No executable project was found; the whole solution is analyzed as one process scope.\n",
                        coverage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coverage_lists_ambiguous_registration_diagnostic()
    {
        var markdown = (await Render("services.AddScoped<Ledger>();")).Bundle.ReportMarkdown;
        var coverage = Section(markdown, "### Coverage");

        var line = coverage.Split('\n').Single(item => item.StartsWith("  - DI diagnostics: ", StringComparison.Ordinal));
        Assert.Contains("UnresolvedBinding Ledger: ", line, StringComparison.Ordinal);
        Assert.Contains("Ledger@Singleton at Case.cs:", line, StringComparison.Ordinal);
        Assert.Contains("Ledger@Scoped at Case.cs:", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coverage_names_what_is_not_analyzed_in_this_version_with_or_without_analysis()
    {
        var analyzed = Section((await Render()).Bundle.ReportMarkdown, "### Coverage");
        var unanalyzed = Section(ReportRenderer.Render(ReportingTestData.CreateReport(null)).ReportMarkdown, "### Coverage");

        foreach (var coverage in new[] { analyzed, unanalyzed })
        {
            Assert.Contains("- Reachable set: not analyzed in this version\n" +
                            "- Semantic gaps: not analyzed in this version\n" +
                            "- Calls from roots: not analyzed in this version\n" +
                            "- Path feasibility: not analyzed in this version\n", coverage, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Process scope", unanalyzed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Suppressed_section_says_suppressions_are_not_analyzed()
    {
        var markdown = (await Render()).Bundle.ReportMarkdown;

        Assert.Contains("### Suppressed findings\nSuppressions are not analyzed in this version.\n\n### Diagnostics\n", markdown,
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_carry_the_pair_counters()
    {
        var (result, bundle) = await Render();
        var diagnostics = Section(bundle.ReportMarkdown, "### Diagnostics");

        Assert.True(result.Pairs.Suppressed > 0);
        Assert.Contains($"- Candidate pairs: {result.Pairs.Candidates}\n", diagnostics, StringComparison.Ordinal);
        Assert.Contains($"- Suppressed pairs: {result.Pairs.Suppressed}\n", diagnostics, StringComparison.Ordinal);
        Assert.Contains("- Skipped pairs: " + string.Join(", ", result.Pairs.Skips.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                                                   .Select(pair => $"{pair.Key} {pair.Value}")) + "\n",
                        diagnostics, StringComparison.Ordinal);
        Assert.NotEmpty(result.Pairs.Skips);
    }

    [Fact]
    public async Task Finding_block_lists_every_part_in_order()
    {
        var (result, bundle) = await Render();
        var finding = result.Findings[0];
        var block = Block(bundle.ReportMarkdown, finding.FindingId);

        var prefixes = new[]
        {
            "- Access A: ", "- Access B: ", "- Code path A: ", "- Code path B: ", "- Resource: ", "- Binding evidence: ", "- Overlap: ",
            "- Protection: ", "- Scenario: ", "- Uncertainty: ", "- Confidence: ", "- Evidence: "
        };
        Assert.Equal(prefixes, block.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                                    .Select(line => prefixes.First(prefix => line.StartsWith(prefix, StringComparison.Ordinal))));
    }

    [Fact]
    public async Task Finding_block_shows_code_paths_binding_evidence_both_policies_and_protection_analysis()
    {
        var (result, bundle) = await Render();
        var partial = Assert.Single(result.Findings, finding => finding.ProtectionResult == "partial");
        var block = Block(bundle.ReportMarkdown, partial.FindingId);

        Assert.Contains("- Code path A: action LedgerController.Post() of controller LedgerController starts (Case.cs:", block, StringComparison.Ordinal);
        Assert.Contains("→ acquires static:Gates.G (Case.cs:", block, StringComparison.Ordinal);
        Assert.Contains("→ write Ledger.Entry (Case.cs:", block, StringComparison.Ordinal);
        Assert.Contains("- Resource: Fixture · di:Ledger@Singleton · Entry · scope solution · shared as process:Fixture:Ledger\n", block, StringComparison.Ordinal);
        Assert.Contains("- Binding evidence: LedgerController.ledger holds constructor parameter ledger at Case.cs:", block, StringComparison.Ordinal);
        Assert.Contains("; AddSingleton registers Ledger at Case.cs:", block, StringComparison.Ordinal);
        Assert.Contains("A: Repeated/MayOverlap in solution; B: Unknown/Unknown in solution\n", block, StringComparison.Ordinal);
        Assert.Contains("- Protection: partial; A holds static:Gates.G; B holds no protection; common single-object protection: none\n", block,
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task Groups_render_in_the_analysis_group_order()
    {
        var (result, bundle) = await Render();

        var headings = bundle.ReportMarkdown.Split('\n').Where(line => line.StartsWith("#### G", StringComparison.Ordinal))
                             .Select(line => line[5..line.IndexOf(' ', 5)])
                             .ToArray();
        Assert.Equal(["di:Ledger@Singleton", "static:Hits"], result.Groups.Select(group => group.Resource.Region));
        Assert.Equal(result.Groups.Select(group => group.GroupId), headings);
    }

    [Fact]
    public async Task Findings_json_carries_the_full_finding_schema()
    {
        var (result, bundle) = await Render();
        using var json = JsonDocument.Parse(bundle.FindingsJson);
        var partialId = result.Findings.Single(finding => finding.ProtectionResult == "partial").FindingId;
        var finding = json.RootElement.GetProperty("findings").EnumerateArray()
                          .Single(item => item.GetProperty("findingId").GetString() == partialId);

        Assert.Equal(["schemaVersion", "runId", "findings", "groups", "narrative"],
                     json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Unsynchronized access to shared Ledger.Entry", finding.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, finding.GetProperty("severity").ValueKind);
        Assert.Equal("deterministic", finding.GetProperty("evidenceMode").GetString());
        Assert.False(finding.GetProperty("confidence").GetProperty("isProbability").GetBoolean());
        Assert.Equal(5, finding.GetProperty("confidence").GetProperty("components").EnumerateObject().Count());
        var resource = finding.GetProperty("resource");
        Assert.Equal(("managed-heap", "di:Ledger@Singleton", "solution"),
                     (resource.GetProperty("domain").GetString(), resource.GetProperty("region").GetString(), resource.GetProperty("scope").GetString()));
        Assert.Equal(["Entry"], resource.GetProperty("accessPath").EnumerateArray().Select(item => item.GetString()));
        var accessA = finding.GetProperty("accesses")[0];
        Assert.Equal("A", accessA.GetProperty("role").GetString());
        Assert.Equal(4, accessA.GetProperty("source").GetProperty("span").GetArrayLength());
        Assert.Equal("LedgerController.Post()", accessA.GetProperty("source").GetProperty("symbol").GetString());
        Assert.Equal(3, accessA.GetProperty("codeFlow").GetArrayLength());
        Assert.Equal(["static:Gates.G"], accessA.GetProperty("heldProtection").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(finding.GetProperty("aliasEvidence").EnumerateArray(),
                        item => item.GetString()!.StartsWith("AddSingleton registers Ledger at Case.cs:", StringComparison.Ordinal));
        Assert.Equal("partial", finding.GetProperty("protectionAnalysis").GetProperty("result").GetString());
        Assert.Empty(finding.GetProperty("protectionAnalysis").GetProperty("commonProtection").EnumerateArray());
        Assert.Equal("not-analyzed", finding.GetProperty("pathFeasibility").GetProperty("result").GetString());
        Assert.NotEmpty(finding.GetProperty("scenario").EnumerateArray());
        Assert.NotEmpty(finding.GetProperty("uncertainty").EnumerateArray());
        Assert.Empty(finding.GetProperty("aiContributions").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, finding.GetProperty("suppression").ValueKind);
        Assert.Equal(["aspnetcore", "hosting"],
                     finding.GetProperty("analysis").GetProperty("providers").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(6, finding.GetProperty("evidence").GetArrayLength());
    }

    [Fact]
    public async Task Groups_json_carries_severity_representative_locations_and_occurrence_count()
    {
        var (result, bundle) = await Render();
        using var json = JsonDocument.Parse(bundle.FindingsJson);

        foreach (var (group, element) in result.Groups.Zip(json.RootElement.GetProperty("groups").EnumerateArray()))
        {
            Assert.Equal(group.GroupId, element.GetProperty("groupId").GetString());
            Assert.Equal(JsonValueKind.Null, element.GetProperty("severity").ValueKind);
            Assert.Equal(group.FindingIds.Count, element.GetProperty("occurrenceCount").GetInt32());
            var locations = element.GetProperty("representativeLocations").EnumerateArray().ToArray();
            Assert.InRange(locations.Length, 1, 3);
            Assert.All(locations, location => Assert.Equal("Case.cs", location.GetProperty("path").GetString()));
            Assert.Equal(group.SharingKey, element.GetProperty("sharingKey").GetString());
        }
    }

    [Fact]
    public async Task Run_metadata_keeps_run_id_status_target_and_started_at_with_offset()
    {
        var (_, bundle) = await Render();
        using var json = JsonDocument.Parse(bundle.RunMetadataJson);
        var root = json.RootElement;

        Assert.Equal("run-123", root.GetProperty("runId").GetString());
        Assert.Equal("CompleteWithFindings", root.GetProperty("status").GetString());
        Assert.Equal(@"C:\repo", root.GetProperty("target").GetString());
        var startedAt = root.GetProperty("startedAt").GetString()!;
        Assert.Equal("2026-01-02T03:04:05.0000000+00:00", startedAt);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), DateTimeOffset.Parse(startedAt, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<(AnalysisResult Result, RenderedBundle Bundle)> Render(string extraRegistrations = "")
    {
        var solution = FixtureSolution.Create(("Case.cs", EngineFixture.Usings + Source + EngineFixture.Startup(
            "services.AddSingleton<Ledger>(); services.AddHostedService<LedgerWorker>(); services.AddSingleton<IHostedService, LedgerWorker>();" +
            extraRegistrations)));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, EngineFixture.ROOT_DIRECTORY, CancellationToken.None);
        return (result, ReportRenderer.Render(ReportingTestData.CreateReport(result)));
    }

    private static string Section(string markdown, string heading)
    {
        var start = markdown.IndexOf(heading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{heading} is missing.");
        var end = markdown.IndexOf("\n### ", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? markdown[start..] : markdown[start..(end + 1)];
    }

    private static string Block(string markdown, string findingId)
    {
        var start = markdown.IndexOf($"##### {findingId}\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Finding {findingId} is missing.");
        var end = markdown.IndexOf("\n\n", start, StringComparison.Ordinal);
        return markdown[start..(end + 1)];
    }
}
