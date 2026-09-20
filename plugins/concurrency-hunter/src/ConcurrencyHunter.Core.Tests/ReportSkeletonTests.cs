using System.Text.Json;
using System.Text.RegularExpressions;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Reporting;
using Microsoft.CodeAnalysis;
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

        Assert.Contains("| IR schema | 1.2 |\n", markdown, StringComparison.Ordinal);
        Assert.Contains("| Providers | aspnetcore (Microsoft.AspNetCore.Mvc.Core 8.0.0.0..11.0.0.0, Microsoft.AspNetCore.Mvc 8.0.0.0..11.0.0.0, " +
                        "Microsoft.AspNetCore.Routing 8.0.0.0..11.0.0.0, Microsoft.AspNetCore.Http.Abstractions 8.0.0.0..11.0.0.0, " +
                        "Microsoft.Extensions.DependencyInjection.Abstractions 8.0.0.0..11.0.0.0, " +
                        "Grpc.AspNetCore.Server 2.0.0.0..3.0.0.0, Grpc.Core.Api 2.0.0.0..3.0.0.0); " +
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
        Assert.DoesNotContain("startup-construction-access", coverage, StringComparison.Ordinal);
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
            Assert.Contains("- Not analyzed in this version: semantic gaps, path feasibility, element accesses\n", coverage, StringComparison.Ordinal);
            Assert.DoesNotContain("spawn sites and ordering", coverage, StringComparison.Ordinal);
            Assert.DoesNotContain("Reachable set", coverage, StringComparison.Ordinal);
            Assert.DoesNotContain("Calls from roots", coverage, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Process scope", unanalyzed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coverage_lists_the_ordering_lines_and_run_metadata_carries_them()
    {
        var (result, bundle) = await Render(source: """
            public sealed class Clock { public int Ticks; }
            public sealed class Ticker(Clock clock) : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    clock.Ticks = 1;
                    var joined = Task.Run(() => clock.Ticks = 2);
                    await joined;
                    clock.Ticks = 3;
                    new Timer(_ => clock.Ticks = 4, null, 0, 1000);
                    new Timer(_ => clock.Ticks = 5, null, Timeout.Infinite, 1000);
                    var tasks = new System.Collections.Generic.List<Task> { Task.Run(() => clock.Ticks = 6) };
                    await Task.WhenAll(tasks);
                }
            }
            public class ClockController(Clock clock) : ControllerBase { public void Post() => clock.Ticks = 7; }
            """, registrations: "services.AddSingleton<Clock>(); services.AddHostedService<Ticker>();");
        var coverage = Section(bundle.ReportMarkdown, "### Coverage");
        var ordered = result.Pairs.Skips[InterproceduralPairing.SKIP_ORDERED];

        Assert.True(ordered > 0);
        Assert.Contains($"- pairs ordered by happens-before: {ordered}\n", coverage, StringComparison.Ordinal);
        Assert.Contains("- spawn sites: Task.Run 2\n", coverage, StringComparison.Ordinal);
        Assert.Contains("- timers: disabled 1, one-shot 0, periodic 1\n", coverage, StringComparison.Ordinal);
        Assert.Contains("- joins without proven identity: 1\n", coverage, StringComparison.Ordinal);
        Assert.True(coverage.IndexOf("- Pairs: ", StringComparison.Ordinal) < coverage.IndexOf("- pairs ordered by happens-before: ", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(bundle.RunMetadataJson);
        var root = json.RootElement;
        Assert.Equal(ordered, root.GetProperty("orderedPairs").GetInt32());
        Assert.Equal([("Task.Run", 2)], root.GetProperty("spawnSites").EnumerateObject().Select(property => (property.Name, property.Value.GetInt32())));
        Assert.Equal([("disabled", 1), ("oneShot", 0), ("periodic", 1)],
                     root.GetProperty("timers").EnumerateObject().Select(property => (property.Name, property.Value.GetInt32())));
        Assert.Equal(1, root.GetProperty("unprovenJoins").GetInt32());
    }

    [Fact]
    public async Task Coverage_counts_a_shared_spawn_and_timer_site_once_over_two_scopes()
    {
        const string library = """
            namespace Shared;
            public sealed class Ticker
            {
                public int Value;
                public void Spawn() => Task.Run(() => Value = 1);
                public Timer Arm() => new Timer(_ => Value = 2, null, 100, Timeout.Infinite);
            }
            """;
        const string once = """
            public static class OnceWorkerProgram { public static void Main() { } }
            public sealed class OnceWorker(Shared.Ticker ticker) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    ticker.Spawn();
                    GC.KeepAlive(ticker.Arm());
                    return Task.CompletedTask;
                }
            }
            """;
        const string periodic = """
            public static class PeriodicWorkerProgram { public static void Main() { } }
            public sealed class PeriodicWorker(Shared.Ticker ticker) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    ticker.Spawn();
                    ticker.Arm().Change(0, 1000);
                    return Task.CompletedTask;
                }
            }
            """;
        var options = new FixtureOptions
        {
            ProjectOutputKinds = new Dictionary<string, OutputKind> { ["Once"] = OutputKind.ConsoleApplication, ["Periodic"] = OutputKind.ConsoleApplication },
            ProjectReferences = [("Once", "Library"), ("Periodic", "Library")]
        };
        var solution = FixtureSolution.CreateProjects(
            options,
            ("Library", "Ticker.cs", EngineFixture.Usings + library),
            ("Once", "Once.cs", EngineFixture.Usings + once + EngineFixture.Startup("services.AddSingleton<Shared.Ticker>(); services.AddHostedService<OnceWorker>();")),
            ("Periodic", "Periodic.cs",
             EngineFixture.Usings + periodic + EngineFixture.Startup("services.AddSingleton<Shared.Ticker>(); services.AddHostedService<PeriodicWorker>();")));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, EngineFixture.ROOT_DIRECTORY, CancellationToken.None);
        var bundle = ReportRenderer.Render(ReportingTestData.CreateReport(result));
        var coverage = Section(bundle.ReportMarkdown, "### Coverage");

        Assert.Equal(["Once", "Periodic"], result.Scopes.Select(scope => scope.Id).Order(StringComparer.Ordinal));
        string Line(string prefix) => Assert.Single(coverage.Split('\n'), line => line.StartsWith(prefix, StringComparison.Ordinal));
        Assert.Equal("- spawn sites: Task.Run 1", Line("- spawn sites: "));
        Assert.Equal("- timers: disabled 0, one-shot 0, periodic 1", Line("- timers: "));
        using var json = JsonDocument.Parse(bundle.RunMetadataJson);
        var root = json.RootElement;
        Assert.Equal([("Task.Run", 1)], root.GetProperty("spawnSites").EnumerateObject().Select(property => (property.Name, property.Value.GetInt32())));
        Assert.Equal([("disabled", 0), ("oneShot", 0), ("periodic", 1)],
                     root.GetProperty("timers").EnumerateObject().Select(property => (property.Name, property.Value.GetInt32())));
    }

    [Fact]
    public async Task Coverage_ordering_lines_say_none_and_zero_without_spawns()
    {
        var coverage = Section((await Render()).Bundle.ReportMarkdown, "### Coverage");

        Assert.Contains("- spawn sites: none\n", coverage, StringComparison.Ordinal);
        Assert.Contains("- timers: disabled 0, one-shot 0, periodic 0\n", coverage, StringComparison.Ordinal);
        Assert.Contains("- joins without proven identity: 0\n", coverage, StringComparison.Ordinal);
        Assert.Contains($"  - {CoverageCounters.DELEGATE_TO_OPAQUE} ", coverage, StringComparison.Ordinal);
        Assert.Contains(": delegates handed to such calls other than the recognized spawn and timer APIs, never invoked\n", coverage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Occurrence_started_at_startup_shows_its_spawn_segment_and_spawn_evidence()
    {
        var (result, bundle) = await Render(source: Source + """
            public sealed class Warmup : BackgroundService
            {
                public Warmup(Ledger ledger) { Task.Run(() => ledger.Entry = "warm"); }
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            """, registrations: "services.AddSingleton<Ledger>(); services.AddHostedService<LedgerWorker>(); services.AddHostedService<Warmup>();");
        var finding = result.Findings.First(item => item.Occurrences.Any(occurrence => occurrence.RootA.RootId == "startup" ||
                                                                                          occurrence.RootB.RootId == "startup"));
        var occurrence = finding.Occurrences.First(item => item.RootA.RootId == "startup" || item.RootB.RootId == "startup");
        var path = occurrence.RootA.RootId == "startup" ? occurrence.CallPathA : occurrence.CallPathB;
        var segment = Assert.Single(path, step => step.StartsWith("spawn:", StringComparison.Ordinal));
        var block = Block(bundle.ReportMarkdown, finding.FindingId);

        Assert.StartsWith("spawn:Task.Run@Warmup", segment, StringComparison.Ordinal);
        Assert.Contains(block.Split('\n'), line => line.StartsWith("  - ", StringComparison.Ordinal) && line.Contains("startup (", StringComparison.Ordinal) &&
                                                   line.Contains($" → {segment} → ", StringComparison.Ordinal));
        var spawn = Assert.Single(finding.Evidence, item => item.Kind == "spawn");
        Assert.Equal($"{finding.FindingId}.SP1", spawn.Id);
        Assert.Matches(@"^Spawn site: Task\.Run in Warmup\S* at Case\.cs:\d+\.$", spawn.Text);
        Assert.Contains($", {spawn.Id}\n", block, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(bundle.FindingsJson);
        var element = json.RootElement.GetProperty("findings").EnumerateArray().Single(item => item.GetProperty("findingId").GetString() == finding.FindingId);
        Assert.Contains(element.GetProperty("occurrences").EnumerateArray(),
                        item => item.GetProperty("roots").EnumerateArray().Any(root => root.GetString() == "startup") &&
                                item.GetProperty("callPaths").EnumerateArray().Any(side => side.EnumerateArray().Any(step => step.GetString() == segment)));
    }

    [Fact]
    public async Task Grpc_method_root_description_names_its_kind()
    {
        var (result, bundle) = await Render(source: """
            public sealed class Request { }
            public sealed class Reply { }
            public static partial class Greeter
            {
                [Grpc.Core.BindServiceMethod(typeof(Greeter), "BindService")]
                public abstract partial class GreeterBase
                {
                    public virtual Task<Reply> SayHello(Request request, Grpc.Core.ServerCallContext context) => throw new NotSupportedException();
                }
            }
            public sealed class GreetingLog { public string? Last; }
            public sealed class GreeterService(GreetingLog log) : Greeter.GreeterBase
            {
                public override Task<Reply> SayHello(Request request, Grpc.Core.ServerCallContext context) { log.Last = "hello"; return Task.FromResult(new Reply()); }
            }
            public static class GrpcMapping
            {
                public static void Map(IServiceCollection services, IEndpointRouteBuilder app) { services.AddGrpc(); app.MapGrpcService<GreeterService>(); }
            }
            """, registrations: "services.AddSingleton<GreetingLog>(); GrpcMapping.Map(services, app);");
        var finding = Assert.Single(result.Findings);

        Assert.Equal("grpc-method", finding.AccessA.Root.RootKind);
        Assert.Contains("under root grpc-method GreeterService.SayHello(Request, ServerCallContext) of gRPC service GreeterService;",
                        Block(bundle.ReportMarkdown, finding.FindingId), StringComparison.Ordinal);
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
            "- Access A: ", "- Access B: ", "- Code path A: ", "- Code path B: ", "- Occurrences: ", "- Resource: ", "- Binding evidence: ",
            "- Overlap: ", "- Protection: ", "- Path feasibility: ", "- Scenario: ", "- Remediation: ", "- Uncertainty: ", "- Confidence: ",
            "- Evidence: "
        };
        Assert.Equal(prefixes, block.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                                    .Where(line => !line.StartsWith("  - ", StringComparison.Ordinal))
                                    .Select(line => prefixes.First(prefix => line.StartsWith(prefix, StringComparison.Ordinal))));
    }

    /// <summary>The skeleton carries a remediation of its own before any narrative is accepted, and it is the one the pair's
    /// operations ask for, not the one its resource would suggest (ADR 0010).</summary>
    [Fact]
    public async Task Finding_block_carries_the_remediation_its_operations_ask_for()
    {
        var (result, bundle) = await Render();
        var lostUpdate = result.Findings.First(finding => finding.RuleId == "DCA1002");
        var block = Block(bundle.ReportMarkdown, lostUpdate.FindingId);

        // A lost update on a field is neither a sequence over a collection nor a read against a write, so what it asks for is
        // one primitive held over the whole update rather than an atomic member of a collection.
        Assert.Contains("- Remediation: make every access to this resource hold one synchronization primitive for the whole of its update",
                        block, StringComparison.Ordinal);
        Assert.Contains("- Path feasibility: ", block, StringComparison.Ordinal);
        Assert.All(result.Findings.Select(finding => Block(bundle.ReportMarkdown, finding.FindingId)),
                   item => Assert.Contains("verify manually.\n", item, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Finding_section_shows_occurrences_and_their_count()
    {
        var (result, bundle) = await Render();

        Assert.All(result.Findings, finding =>
        {
            var block = Block(bundle.ReportMarkdown, finding.FindingId);
            var lines = block.Split('\n');
            var count = Array.FindIndex(lines, line => line.StartsWith("- Occurrences: ", StringComparison.Ordinal));
            Assert.StartsWith($"- Occurrences: {finding.OccurrenceCount}", lines[count], StringComparison.Ordinal);
            Assert.InRange(finding.Occurrences.Count, 1, 3);
            foreach (var (occurrence, index) in finding.Occurrences.Select((occurrence, index) => (occurrence, index)))
            {
                Assert.StartsWith($"  - {occurrence.RootA.Display} (", lines[count + 1 + index], StringComparison.Ordinal);
                Assert.Contains($" with {occurrence.RootB.Display} (", lines[count + 1 + index], StringComparison.Ordinal);
                Assert.EndsWith($"; protection {occurrence.Protection}", lines[count + 1 + index], StringComparison.Ordinal);
            }

            Assert.False(lines[count + 1 + finding.Occurrences.Count].StartsWith("  - ", StringComparison.Ordinal));
        });
        Assert.Contains(result.Groups, group => bundle.ReportMarkdown.Contains($"({group.FindingIds.Count} findings, {group.OccurrenceCount} occurrences)",
                                                                               StringComparison.Ordinal));
    }

    [Fact]
    public async Task Finding_block_shows_code_paths_binding_evidence_both_policies_and_protection_analysis()
    {
        var (result, bundle) = await Render();
        var partial = Assert.Single(result.Findings, finding => finding.ProtectionResult == "partial");
        var block = Block(bundle.ReportMarkdown, partial.FindingId);

        Assert.Contains("- Code path A: action LedgerController.Post() of controller LedgerController starts (Case.cs:", block, StringComparison.Ordinal);
        Assert.Contains("→ acquires alloc:Gates..cctor()#object (Case.cs:", block, StringComparison.Ordinal);
        Assert.Contains("→ write Ledger.Entry (Case.cs:", block, StringComparison.Ordinal);
        Assert.Contains("- Resource: Fixture · di:Ledger@Singleton · Entry · scope solution · ownership Shared " +
                        "(di:Ledger@Singleton is one container object for the whole scope (singleton).)\n", block, StringComparison.Ordinal);
        Assert.Contains("- Binding evidence: LedgerController.ledger holds constructor parameter ledger at Case.cs:", block, StringComparison.Ordinal);
        Assert.Contains("; AddSingleton registers Ledger at Case.cs:", block, StringComparison.Ordinal);
        Assert.Contains("A: Repeated/MayOverlap in solution; B: Unknown/Unknown in solution\n", block, StringComparison.Ordinal);
        Assert.Contains("- Protection: partial; A holds alloc:Gates..cctor()#object; B holds no protection; common single-object protection: none\n", block,
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task Groups_render_in_the_analysis_group_order()
    {
        var (result, bundle) = await Render();

        var headings = bundle.ReportMarkdown.Split('\n').Where(line => line.StartsWith("#### G", StringComparison.Ordinal))
                             .Select(line => line[5..line.IndexOf(' ', 5)])
                             .ToArray();
        // One resource carries two groups: the two workers race it unprotected, and the locked action against a worker is the
        // protection rule, and a group is one rule on one object.
        Assert.Equal(["di:Ledger@Singleton", "static:Hits", "di:Ledger@Singleton"], result.Groups.Select(group => group.Resource.Region));
        Assert.Equal(["DCA1001", "DCA1002", "DCA1003"], result.Groups.Select(group => group.RuleId));
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
        Assert.Equal("Inconsistent synchronization of shared Ledger.Entry", finding.GetProperty("title").GetString());
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
        Assert.Equal(["alloc:Gates..cctor()#object"], accessA.GetProperty("heldProtection").EnumerateArray().Select(item => item.GetString()));
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
        }
    }

    [Fact]
    public async Task Coverage_lists_comparisons_the_cartesian_bound_buckets_the_largest_bucket_and_candidates()
    {
        var (result, bundle) = await Render();
        var coverage = Section(bundle.ReportMarkdown, "### Coverage");
        var pairs = result.Pairs;

        Assert.True(pairs.Comparisons > 0);
        Assert.True(pairs.Candidates < pairs.Comparisons);
        Assert.Contains($"- Pairs: comparisons {pairs.Comparisons} of a Cartesian bound {pairs.CartesianBound}; buckets {pairs.Buckets}, " +
                        $"largest bucket {pairs.LargestBucket}; candidates {pairs.Candidates}, suppressed {pairs.Suppressed}\n",
                        coverage, StringComparison.Ordinal);
        Assert.Equal(result.Findings.Count == 0 ? 0 : pairs.Candidates, pairs.Candidates);
        Assert.Contains($"- Candidate pairs: {pairs.Candidates}\n", Section(bundle.ReportMarkdown, "### Diagnostics"), StringComparison.Ordinal);
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

    [Fact]
    public async Task Coverage_lists_reachable_bodies_and_every_counter()
    {
        var (result, bundle) = await Render();
        var coverage = Section(bundle.ReportMarkdown, "### Coverage");
        var lines = coverage.Split('\n');

        Assert.Contains($"  - Reachable bodies: {result.Coverage[0].Skips[CoverageCounters.REACHABLE_BODIES]}\n", coverage, StringComparison.Ordinal);
        var counters = typeof(CoverageCounters).GetFields().Select(field => (string)field.GetRawConstantValue()!)
                                               .Where(counter => counter != CoverageCounters.REACHABLE_BODIES).ToArray();
        Assert.Equal(9, counters.Length);
        foreach (var counter in counters)
            Assert.Matches($@"^  - {Regex.Escape(counter)} \d+: \S", Assert.Single(lines, line => line.StartsWith($"  - {counter} ", StringComparison.Ordinal)));
        var opaque = Array.FindIndex(lines, line => line.StartsWith($"  - {CoverageCounters.OPAQUE_CALL} ", StringComparison.Ordinal));
        Assert.StartsWith("    - Top opaque callees: ", lines[opaque + 1], StringComparison.Ordinal);
        Assert.Contains("object..ctor() ", lines[opaque + 1], StringComparison.Ordinal);
        var reached = Array.FindIndex(lines, line => line.StartsWith("  - Reachable bodies: ", StringComparison.Ordinal));
        Assert.True(Array.FindIndex(lines, line => line.StartsWith("  - Other diagnostics: ", StringComparison.Ordinal)) < reached);
        var notAnalyzed = Array.FindIndex(lines, line => line.StartsWith("- Not analyzed in this version: ", StringComparison.Ordinal));
        Assert.StartsWith("  - Lowered but not reached (inventory, not counted against coverage): ", lines[notAnalyzed - 2], StringComparison.Ordinal);
        Assert.StartsWith("  - Source bodies outside the lowered set (inventory, not counted against coverage): ", lines[notAnalyzed - 1], StringComparison.Ordinal);
        Assert.True(Array.FindIndex(lines, line => line.StartsWith($"  - {CoverageCounters.WILDCARD_ACCESS} ", StringComparison.Ordinal)) < notAnalyzed - 2);
    }

    [Fact]
    public async Task Finding_block_shows_ownership_and_call_steps()
    {
        var (result, bundle) = await Render(source: """
            public sealed class Journal { private string? _last; public void Append(string entry) => _last = entry; }
            public sealed class JournalService(Journal journal) { public void Record(string entry) => journal.Append(entry); }
            public class JournalController(JournalService service) : ControllerBase { public void Post(string entry) => service.Record(entry); }
            """, registrations: "services.AddSingleton<Journal>(); services.AddSingleton<JournalService>();");
        var finding = Assert.Single(result.Findings);
        var block = Block(bundle.ReportMarkdown, finding.FindingId);

        Assert.Contains("- Resource: Fixture · di:Journal@Singleton · _last · scope solution · ownership Shared " +
                        "(di:Journal@Singleton is one container object for the whole scope (singleton).)\n", block, StringComparison.Ordinal);
        Assert.Contains("→ calls JournalService.Record(string) on di:JournalService@Singleton (Case.cs:", block, StringComparison.Ordinal);
        Assert.Contains("→ calls Journal.Append(string) on di:Journal@Singleton (Case.cs:", block, StringComparison.Ordinal);
        Assert.Contains("ownership: Shared (di:Journal@Singleton is one container object for the whole scope (singleton).)",
                        Assert.Single(finding.Evidence, item => item.Kind == "resource").Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wildcard_resource_renders_its_path_as_a_star()
    {
        var chain = string.Concat(Enumerable.Range(1, 11).Select(level => $"public sealed class Level{level} {{ public Level{level + 1} Next {{ get; }} = new(); }}\n"));
        var (result, bundle) = await Render(source: chain + """
            public sealed class Level12 { public string? Value { get; set; } }
            public sealed class DeepChain
            {
                public Level1 First { get; } = new();
                public void Write(string value) => First.Next.Next.Next.Next.Next.Next.Next.Next.Next.Next.Next.Value = value;
            }
            public class DeepController(DeepChain chain) : ControllerBase { public void Put(string value) => chain.Write(value); }
            """, registrations: "services.AddSingleton<DeepChain>();");
        var wildcard = result.Findings.Where(item => item.Resource.IsWildcard).ToArray();

        Assert.NotEmpty(wildcard);
        foreach (var finding in wildcard)
        {
            Assert.Contains("- Resource: Fixture · di:DeepChain@Singleton · * · scope solution · ownership Shared " +
                            "(di:DeepChain@Singleton is one container object for the whole scope (singleton).)\n", Block(bundle.ReportMarkdown, finding.FindingId),
                            StringComparison.Ordinal);
            Assert.Contains($"#### {finding.GroupId} · DCA1001 · * on di:DeepChain@Singleton (", bundle.ReportMarkdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Resource_line_shows_the_whole_escape_chain_of_an_escaped_object()
    {
        var (result, bundle) = await Render(source: """
            public sealed class Slot { public string? Label; }
            public sealed class Board { public readonly Slot?[] Slots = new Slot?[2]; }
            public class BoardController(Board board) : ControllerBase
            {
                public void Post(string label) { var slot = new Slot(); board.Slots[0] = slot; slot.Label = label; }
                public string? Get() => board.Slots[0]?.Label;
            }
            """, registrations: "services.AddSingleton<Board>();");
        var finding = result.Findings.First(item => item.Resource.Member.Name == "Label");
        Assert.Equal(OwnershipKind.Escaped, finding.AccessA.Ownership);
        var chain = finding.AccessA.OwnershipEvidence;

        Assert.Equal(2, chain.Count);
        Assert.Contains("is stored into Slots of di:Board@Singleton at Case.cs:", chain[0], StringComparison.Ordinal);
        Assert.Contains("is stored into [] of ", chain[1], StringComparison.Ordinal);
        Assert.All(chain, hop => Assert.Matches(@" at Case\.cs:\d+\.$", hop));
        Assert.Contains($"· ownership Escaped ({string.Join(" ", chain)})\n", Block(bundle.ReportMarkdown, finding.FindingId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_modify_write_shows_its_read_sources()
    {
        var (result, bundle) = await Render();
        var finding = Assert.Single(result.Findings, item => item.RuleId == "DCA1002");
        var block = Block(bundle.ReportMarkdown, finding.FindingId);
        var read = Assert.Single(finding.AccessA.ReadSources);

        Assert.Contains($"- Read source of A: LedgerController.Post() reads at Case.cs:{read.Source.StartLine}; code path: action LedgerController.Post()", block,
                        StringComparison.Ordinal);
        Assert.Contains("→ read Hits.Count (Case.cs:", block, StringComparison.Ordinal);
        Assert.Contains("- Read source of B: ", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Findings_json_carries_ownership_chain_read_sources_and_group_ownership()
    {
        var (result, bundle) = await Render();
        using var json = JsonDocument.Parse(bundle.FindingsJson);
        var root = json.RootElement;
        var lostUpdate = result.Findings.Single(finding => finding.RuleId == "DCA1002").FindingId;

        foreach (var finding in root.GetProperty("findings").EnumerateArray())
        {
            var ownership = finding.GetProperty("aliasEvidence").EnumerateArray().Select(item => item.GetString()!)
                                   .Where(item => item.StartsWith("ownership ", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(ownership);
            Assert.All(ownership, item => Assert.Matches(@"^ownership \w+: \S", item));
            Assert.All(finding.GetProperty("accesses").EnumerateArray(), access => Assert.Equal(JsonValueKind.Array, access.GetProperty("readSources").ValueKind));
        }

        var readSources = root.GetProperty("findings").EnumerateArray().Single(item => item.GetProperty("findingId").GetString() == lostUpdate)
                              .GetProperty("accesses")[0].GetProperty("readSources");
        Assert.NotEmpty(readSources.EnumerateArray());
        foreach (var read in readSources.EnumerateArray())
        {
            Assert.Equal("Case.cs", read.GetProperty("source").GetProperty("path").GetString());
            Assert.Equal(4, read.GetProperty("source").GetProperty("span").GetArrayLength());
            Assert.False(string.IsNullOrEmpty(read.GetProperty("symbol").GetString()));
            Assert.NotEmpty(read.GetProperty("codeFlow").EnumerateArray());
        }

        foreach (var group in root.GetProperty("groups").EnumerateArray())
        {
            var ownership = group.GetProperty("ownership");
            Assert.False(string.IsNullOrEmpty(ownership.GetProperty("kind").GetString()));
            Assert.NotEmpty(ownership.GetProperty("evidence").EnumerateArray());
            Assert.False(group.TryGetProperty("sharingKey", out _));
        }

        Assert.DoesNotContain("sharingKey", bundle.FindingsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shared as", bundle.ReportMarkdown, StringComparison.Ordinal);
    }

    private static async Task<(AnalysisResult Result, RenderedBundle Bundle)> Render(string extraRegistrations = "", string source = Source,
                                                                                     string? registrations = null)
    {
        var solution = FixtureSolution.Create(("Case.cs", EngineFixture.Usings + source + EngineFixture.Startup(
            (registrations ?? "services.AddSingleton<Ledger>(); services.AddHostedService<LedgerWorker>(); services.AddSingleton<IHostedService, LedgerWorker>();") +
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
