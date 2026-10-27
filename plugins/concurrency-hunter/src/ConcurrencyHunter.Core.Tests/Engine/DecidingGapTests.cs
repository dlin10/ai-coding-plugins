using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Reporting;
using ConcurrencyHunter.Scopes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The deciding gaps of a scope, from the checks of every folded occurrence to coverage and the report.</summary>
public sealed class DecidingGapTests
{
    private const string TOUCH = "Opaque.IBase.Touch(object)";
    private const string START = "Opaque.Factory.Start(object)";
    private const string GATE = "Opaque.Factory.Gate(object)";
    private static readonly Lazy<MetadataReference> Library = new(CompileLibrary);

    [Fact]
    public async Task Gap_deciding_the_operation_check_is_listed_with_it()
    {
        var result = await Analyze(Worker("Opaque.ICombined sink = new Opaque.Both(); sink.Touch(work);", reader: true));
        var finding = Assert.Single(result.Findings);

        AssertDecides(Assert.Single(result.Coverage), TOUCH, finding.Fingerprint, ["operation"]);
        Assert.Equal([TOUCH], finding.GapCallees);
        Assert.Equal(["operation"], finding.GapChecks[TOUCH]);
    }

    [Fact]
    public async Task Gap_deciding_the_overlap_check_is_listed_with_it()
    {
        var result = await Analyze(Usings + """
            public sealed class Work { public int Count; public void Run() => Count = 1; }
            public sealed class Worker(Work work) : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken token)
                {
                    var handle = token.IsCancellationRequested ? Task.Run(work.Run) : Opaque.Factory.Start(work);
                    await handle;
                    work.Count = 2;
                }
            }
            """ + Startup("services.AddSingleton<Work>(); services.AddHostedService<Worker>();"));
        var finding = Assert.Single(result.Findings, PlainWrites);

        AssertDecides(Assert.Single(result.Coverage), START, finding.Fingerprint, ["overlap"]);
    }

    [Fact]
    public async Task Gap_deciding_the_protection_check_is_listed_with_it()
    {
        var result = await Analyze(Worker("lock (Opaque.Factory.Gate(work)) { work.Count = 1; }", reader: false));
        var finding = Assert.Single(result.Findings, PlainWrites);

        AssertDecides(Assert.Single(result.Coverage), GATE, finding.Fingerprint, ["protection"]);
    }

    [Fact]
    public void Gap_deciding_only_a_capped_occurrence_of_a_high_finding_is_listed()
    {
        var finding = Assert.Single(Fold(Pair("a", [Check(START, "overlap")]), Pair("b", [])));
        var coverage = Build([Gap(START)], [finding]);

        Assert.Equal("High", finding.Confidence.Label);
        Assert.Equal(2, finding.OccurrenceCount);
        AssertDecides(coverage, START, finding.Fingerprint, ["overlap"]);
    }

    [Fact]
    public async Task Gap_on_the_call_path_that_decides_nothing_is_not_listed()
    {
        var result = await Analyze(Worker("Opaque.Factory.Invoke(() => { }); work.Count = 1;", reader: false));
        var coverage = Assert.Single(result.Coverage);

        Assert.Equal(["Opaque.Factory.Invoke(Action)"], coverage.Gaps.Select(gap => gap.Callee));
        Assert.NotEmpty(result.Findings);
        Assert.Empty(coverage.DecidingGaps);
    }

    [Fact]
    public async Task Call_a_record_makes_known_is_no_deciding_gap()
    {
        var result = await Analyze(Worker("Opaque.ICombined sink = new Opaque.Both(); sink.Touch(work);", reader: true),
                                   Entry("\"effects\": {\"value\": [\"reads-deep\"]}", "Opaque.ILeft"));
        var coverage = Assert.Single(result.Coverage);

        Assert.Equal(1, coverage.Skips[CoverageCounters.KNOWN_CALL_PROJECT]);
        Assert.Empty(coverage.Gaps);
        Assert.Empty(coverage.DecidingGaps);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Call_a_record_declares_opaque_can_be_a_deciding_gap()
    {
        // A keyed opaque entry and two incomparable keyed known entries both leave the call opaque at this receiver.
        foreach (var entries in new[]
        {
            Entry("\"opaque\": true"),
            Entry("\"opaque\": true", "Opaque.ILeft"),
            Entry("\"effects\": {}", "Opaque.ILeft") + "," + Entry("\"effects\": {}", "Opaque.IRight")
        })
        {
            var result = await Analyze(Worker("Opaque.ICombined sink = new Opaque.Both(); sink.Touch(work);", reader: true), entries);
            var finding = Assert.Single(result.Findings);
            var coverage = Assert.Single(result.Coverage);

            Assert.Equal(1, coverage.Skips[CoverageCounters.OPAQUE_BY_PROJECT]);
            AssertDecides(coverage, TOUCH, finding.Fingerprint, ["operation"]);
        }
    }

    [Fact]
    public void Gap_deciding_two_findings_lists_both_by_fingerprint()
    {
        var findings = Fold(Pair("a", [Check(TOUCH, "operation")]), Pair("a", [Check(TOUCH, "operation")], field: "Other"));
        var gap = Assert.Single(Build([Gap(TOUCH)], findings.Reverse().ToArray()).DecidingGaps);

        Assert.Equal(2, gap.Findings.Count);
        Assert.Equal(findings.Select(finding => finding.Fingerprint).Order(StringComparer.Ordinal), gap.Findings.Select(finding => finding.Fingerprint));
        Assert.All(gap.Findings, finding => Assert.Equal(["operation"], finding.DecidedChecks));
    }

    [Fact]
    public void Two_gaps_deciding_one_finding_are_two_entries()
    {
        var finding = Assert.Single(Fold(Pair("a", [Check(START, "overlap"), Check(GATE, "protection")])));
        var coverage = Build([Gap(START), Gap(GATE)], [finding]);

        Assert.Equal([GATE, START], coverage.DecidingGaps.Select(gap => gap.Callee));
        AssertDecides(coverage, GATE, finding.Fingerprint, ["protection"]);
        AssertDecides(coverage, START, finding.Fingerprint, ["overlap"]);
    }

    [Fact]
    public async Task Call_sites_are_the_gaps_own_sites()
    {
        // The gap call is in Calls.cs; both writes whose protection it decides are in a different body and file.
        const string calls = """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Hosting;
            public sealed class Worker(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken token)
                {
                    work.Write(Opaque.Factory.Gate(work));
                    return Task.CompletedTask;
                }
            }
            """;
        var source = Usings + """
            public sealed class Work { public int Count; public void Write(object gate) { lock (gate) { Count = 1; } } }
            public sealed class Other(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken token) { work.Count = 2; return Task.CompletedTask; }
            }
            """ + Startup("services.AddSingleton<Work>(); services.AddHostedService<Worker>(); services.AddHostedService<Other>();");
        var result = await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(Options, ("Accesses.cs", source), ("src/Calls.cs", calls)),
                                                        ROOT_DIRECTORY, CancellationToken.None);
        var finding = Assert.Single(result.Findings, PlainWrites);
        var gap = Assert.Single(Assert.Single(result.Coverage).DecidingGaps);

        Assert.Equal(["src/Calls.cs:8"], gap.CallSites);
        Assert.Equal("Accesses.cs", finding.AccessA.Source.Path);
        Assert.Equal("Accesses.cs", finding.AccessB.Source.Path);
        AssertDecides(Assert.Single(result.Coverage), GATE, finding.Fingerprint, ["protection"]);
    }

    [Fact]
    public void Checks_of_one_callee_from_different_occurrences_are_united()
    {
        var finding = Assert.Single(Fold(Pair("a", [Check(TOUCH, "operation")]), Pair("b", []), Pair("c", []),
                                        Pair("d", [Check(TOUCH, "protection"), Check(TOUCH, "overlap")])));
        var coverage = Build([Gap(TOUCH)], [finding]);

        Assert.Equal(4, finding.OccurrenceCount);
        Assert.Equal(3, finding.Occurrences.Count);
        Assert.Equal(["operation", "overlap", "protection"], finding.GapChecks[TOUCH]);
        AssertDecides(coverage, TOUCH, finding.Fingerprint, ["operation", "overlap", "protection"]);
    }

    [Fact]
    public void Call_sites_are_one_per_path_and_line_ordered_by_path_then_line_number()
    {
        var finding = Assert.Single(Fold(Pair("a", [Check(TOUCH, "operation")])));
        var sites = new Dictionary<OperationSite, SourceSpan>
        {
            [new("body", 1)] = new("z/Call.cs", 1, 1, 1, 5),
            [new("body", 2)] = new("a/Call.cs", 10, 1, 10, 5),
            [new("body", 3)] = new("a/Call.cs", 9, 20, 9, 30),
            [new("body", 4)] = new("a/Call.cs", 9, 1, 9, 10)
        };
        var gap = Gap(TOUCH) with { Sites = sites.Keys.ToArray(), CallSites = 4 };

        Assert.Equal(["a/Call.cs:9", "a/Call.cs:10", "z/Call.cs:1"], Assert.Single(Build([gap], [finding], sites).DecidingGaps).CallSites);
    }

    [Fact]
    public void Checks_are_listed_operation_overlap_protection()
    {
        var finding = Assert.Single(Fold(Pair("a", [Check(TOUCH, "protection"), Check(TOUCH, "operation"), Check(TOUCH, "overlap"),
                                                   Check(TOUCH, "operation")])));

        AssertDecides(Build([Gap(TOUCH)], [finding]), TOUCH, finding.Fingerprint, ["operation", "overlap", "protection"]);
    }

    [Fact]
    public void Deciding_gaps_are_ordered_by_materiality_then_callee()
    {
        var callees = new[] { "A", "C", "B", "Z", "Y" };
        var finding = Assert.Single(Fold(Pair("a", callees.Select(callee => Check(callee, "operation")).ToArray())));
        var coverage = Build([Gap("A", roots: 1), Gap("C", roots: 2), Gap("B", roots: 2),
                              Gap("Z", roots: 2, regions: 2), Gap("Y", roots: 2, sites: 2)], [finding]);

        Assert.Equal(["Z", "Y", "B", "C", "A"], coverage.DecidingGaps.Select(gap => gap.Callee));
    }

    [Fact]
    public async Task Report_lists_the_deciding_gaps_after_the_semantic_gaps_of_their_scope()
    {
        var options = Options with
        {
            ProjectOutputKinds = new Dictionary<string, OutputKind> { ["A"] = OutputKind.ConsoleApplication, ["B"] = OutputKind.ConsoleApplication }
        };
        var source = Worker("Opaque.ICombined sink = new Opaque.Both(); sink.Touch(work);", reader: true);
        var other = Worker("Opaque.Factory.Invoke(() => { }); work.Count = 1;", reader: false);
        var solution = FixtureSolution.CreateProjects(options, ("B", "Case.cs", other), ("B", "Program.cs", "System.Console.WriteLine();"),
                                                       ("A", "Case.cs", source), ("A", "Program.cs", "System.Console.WriteLine();"));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);
        var finding = Assert.Single(result.Findings, finding => finding.Resource.Scope == "A");
        var report = ReportRenderer.Render(ReportingTestData.CreateReport(result)).ReportMarkdown;
        var start = report.IndexOf("- Process scope A:", StringComparison.Ordinal);
        var end = report.IndexOf("- Process scope B:", StringComparison.Ordinal);
        var scopeReport = report[start..end];

        Assert.Contains($"    - {TOUCH} (unknown-library): roots 1, regions 1, call sites 1\n", scopeReport, StringComparison.Ordinal);
        Assert.Contains($"  - Deciding gaps:\n    - {TOUCH}\n      - {finding.Fingerprint}: operation\n      - Call sites: A/Case.cs:15\n",
                        scopeReport, StringComparison.Ordinal);
        Assert.True(scopeReport.IndexOf("  - Deciding gaps:", StringComparison.Ordinal) > scopeReport.IndexOf("    - " + TOUCH + " (", StringComparison.Ordinal));
        Assert.DoesNotContain("  - Deciding gaps:", report[end..], StringComparison.Ordinal);
    }

    [Fact]
    public void Scope_without_deciding_gaps_has_an_empty_list_and_no_report_section()
    {
        var findings = Fold(Pair("a", [Check(TOUCH, "operation")]));
        var scope = new ScopeCoverage("another-scope", new Dictionary<string, int>(), [], 0, new Dictionary<string, int>()) { Gaps = [Gap(TOUCH)] };
        var coverage = scope with { DecidingGaps = DecidingGaps.Build(scope, findings, new Dictionary<OperationSite, SourceSpan>()) };
        var result = FindingTestData.Result(findings, []) with { Scopes = [new ProcessScope(scope.ScopeId, null, [])], Coverage = [coverage] };

        Assert.Empty(coverage.DecidingGaps);
        Assert.Empty(new ScopeCoverage("empty", new Dictionary<string, int>(), [], 0, new Dictionary<string, int>()).DecidingGaps);
        Assert.DoesNotContain("Deciding gaps:", ReportRenderer.Render(ReportingTestData.CreateReport(result)).ReportMarkdown, StringComparison.Ordinal);
    }

    private static void AssertDecides(ScopeCoverage scope, string callee, string fingerprint, string[] checks)
    {
        var gap = Assert.Single(scope.DecidingGaps, gap => gap.Callee == callee);
        var finding = Assert.Single(gap.Findings, finding => finding.Fingerprint == fingerprint);
        Assert.Equal(checks, finding.DecidedChecks);
    }

    private static ScopeCoverage Build(IReadOnlyList<SemanticGap> gaps, IReadOnlyList<Finding> findings,
                                        IReadOnlyDictionary<OperationSite, SourceSpan>? sources = null)
    {
        var scope = new ScopeCoverage(FindingTestData.SCOPE, new Dictionary<string, int>(), [], 0, new Dictionary<string, int>()) { Gaps = gaps };
        sources ??= gaps.SelectMany(gap => gap.Sites).Distinct().ToDictionary(site => site, _ => new SourceSpan("Calls.cs", 7, 1, 7, 20));
        return scope with { DecidingGaps = DecidingGaps.Build(scope, findings, sources) };
    }

    private static SemanticGap Gap(string callee, int roots = 1, int regions = 1, int sites = 1) =>
        new(callee, SemanticGapKinds.UNKNOWN_LIBRARY, roots, regions, sites) { Sites = [new("call-body", 1)] };

    private static GapCheck Check(string callee, string component) => new(callee, SemanticGapKinds.UNKNOWN_LIBRARY, component, "a reason");

    private static IReadOnlyList<Finding> Fold(params AccessPair[] pairs) => ConflictFindings.Create(pairs, CancellationToken.None).Findings;

    private static AccessPair Pair(string root, IReadOnlyList<GapCheck> checks, string field = "Hits")
    {
        var resource = FindingTestData.Resource("static:Counters", field);
        var first = FindingTestData.Access(resource, AccessOperation.Write, root + "-a", "One.Post()", new SourceSpan("Accesses.cs", 10, 1, 10, 20));
        var second = FindingTestData.Access(resource, AccessOperation.Write, root + "-b", "Two.Post()", new SourceSpan("Accesses.cs", 20, 1, 20, 20));
        return new AccessPair(first, second, PairProtection.UNPROTECTED) { GapChecks = checks };
    }

    private static bool PlainWrites(Finding finding) =>
        !finding.AccessA.Operation.IsUnknownEffect() && !finding.AccessB.Operation.IsUnknownEffect();

    private static string Worker(string work, bool reader) => Usings + $$"""
        public sealed class Work { public int Count; }
        public sealed class Worker(Work work) : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken token)
            {
                {{work}}
                return Task.CompletedTask;
            }
        }
        public sealed class Other(Work work) : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken token) { {{(reader ? "_ = work.Count;" : "work.Count = 2;")}} return Task.CompletedTask; }
        }
        """ + Startup("services.AddSingleton<Work>(); services.AddHostedService<Worker>(); services.AddHostedService<Other>();");

    private static string Entry(string body, string? receiver = null) =>
        "{\"member\":\"M:Opaque.IBase.Touch(System.Object)\",\"assemblies\":[\"Opaque\"]," + body +
        (receiver is null ? "" : ",\"receiver\":\"T:" + receiver + "\"") + "}";

    private static FixtureOptions Options => new() { MetadataReferences = [Library.Value] };

    private static async Task<AnalysisResult> Analyze(string source, string? entries = null)
    {
        var solution = FixtureSolution.Create(Options, ("Case.cs", source));
        if (entries is null)
            return await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);
        var directory = Directory.CreateTempSubdirectory("ch-deciding-gaps-").FullName;
        try
        {
            var models = Path.Combine(directory, ".concurrency-hunter", "models");
            Directory.CreateDirectory(models);
            await File.WriteAllTextAsync(Path.Combine(models, "model.json"), "{\"schemaVersion\":1,\"models\":[" + entries + "]}");
            return await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, directory, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MetadataReference CompileLibrary()
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            using System;
            using System.Threading.Tasks;
            namespace Opaque;
            public interface IBase { void Touch(object value); }
            public interface ILeft : IBase { }
            public interface IRight : IBase { }
            public interface ICombined : ILeft, IRight { }
            public sealed class Both : ICombined { public void Touch(object value) { } }
            public static class Factory
            {
                public static Task Start(object owner) => Task.CompletedTask;
                public static object Gate(object owner) => new();
                public static void Invoke(Action action) { }
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }
}
