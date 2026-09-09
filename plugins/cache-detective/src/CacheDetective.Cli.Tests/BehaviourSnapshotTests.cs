using System.Diagnostics;
using System.Text.Json;
using CacheDetective.Caching;
using CacheDetective.Configuration;
using CacheDetective.Database;
using CacheDetective.Events;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Rules;
using CacheDetective.Tests.Database;
using CacheDetective.Workspaces;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// What the tool says <em>today</em>, on both corpora, written down before anything changes it. This is
/// not the reference set of right answers: a row here may well be wrong, and the snapshot's whole job is
/// to make every later change to it visible rather than silent.
/// <para>A finding is named by its <em>semantic</em> identity — the rule, the solution and the handler
/// symbol, the project, the key template and store, and the rule's own target — rather than by the
/// session-scoped <c>f:N</c> the MCP catalogue hands out, which is renumbered on every run and says
/// nothing across two of them.</para>
/// <para>The corpus revision is checked first and on its own: with the corpus at a different revision the
/// identities are expected to differ, and a wall of them would hide the one fact that matters.</para>
/// </summary>
public sealed class BehaviourSnapshotTests
{
    private const string PURPOSE =
        "The demo-stand snapshot needs MSBuild and a SQL Server in one process, like the demo-stand "
        + "end-to-end test it is taken through (see docs/adr/0008). Set it to a SQL Server connection "
        + "string and run this test on a developer machine.";

    private const string SOLUTION_NAME = "Shop.slnx";
    private const string DATABASE = "shop";
    private const string SNAPSHOT_FILE = "behaviour-snapshot.json";

    private const string REVISION_MISMATCH = "The corpus revision does not match the one the snapshot was taken at.";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    [RequiresSqlServerFact(PURPOSE)]
    public async Task Demo_stand_matches_the_recorded_behaviour()
    {
        var solutionPath = SqlServerHarness.FindRepositoryFile(
            "plugins", "cache-detective", "demo", SOLUTION_NAME);
        var scriptPath = SqlServerHarness.FindRepositoryFile(
            "plugins", "cache-detective", "demo", "db", "shop.sql");
        var demo = Path.GetDirectoryName(solutionPath)!;

        await using var harness = await SqlServerHarness.CreateAsync();
        await harness.ApplyAsync(scriptPath);

        var graph = new CacheGraph();
        using (var loaded = await new MsBuildSolutionLoader().LoadAsync(solutionPath))
        {
            var code = await new CallGraphIndexer().IndexAsync(loaded.Solution, SOLUTION_NAME);
            graph.ReplaceSolution(SOLUTION_NAME, code);
        }

        await using var connection = await harness.OpenAsync();
        var catalogue = await new DatabaseIndexer().IndexAsync(connection, DATABASE);
        graph.ReplaceDatabase(DATABASE, catalogue.Graph);

        // The demo corpus lives in this repository, so its revision is the last commit that touched it
        // and not the repository's HEAD, which every later task moves without touching the corpus. What
        // counts as the corpus is its inputs — the sources, the projects and the SQL the graph is built
        // from. Excluding only the snapshot was not enough: every expectation file written beside it
        // (expected-findings.json and the like) moved the revision on its first commit and made the
        // snapshot fail its revision check without a single input having changed.
        AssertMatches(Path.Combine(demo, SNAPSHOT_FILE), Take(graph, DemoRevision(demo), demo));
    }

    [RequiresEShopFact]
    public async Task EShop_matches_the_recorded_behaviour()
    {
        var root = Environment.GetEnvironmentVariable("CD_ESHOP_ROOT")!;

        // A revision alone does not pin a corpus. With the phase-3 patch applied the revision is unchanged
        // and the sources are not, and the snapshot then fails as a wall of identity differences instead of
        // saying the one thing that is true: the corpus is dirty. The metrics manifest checks both halves
        // for the same reason.
        var dirty = Git(root, ["status", "--porcelain"], allowEmpty: true);
        Assert.True(dirty.Length == 0,
                    $"The corpus at '{root}' has uncommitted changes, so the snapshot cannot be compared against it. " +
                    $"Unapply them (the phase-3 patch, most likely) and run again. git status --porcelain said:\n{dirty}");

        var eval = Path.GetDirectoryName(SqlServerHarness.FindRepositoryFile(
            "plugins", "cache-detective", "skills", "scan", "evals", "eshop", "workspace.json"))!;
        var configuration = await WorkspaceConfigurationStore.ReadFileAsync(Path.Combine(eval, "workspace.json"));
        var solutionPath = Path.Combine(root, configuration.Solutions.Single());

        CacheGraph graph;
        using (var loaded = await new MsBuildSolutionLoader().LoadAsync(solutionPath))
        {
            var configuredEvents = (configuration.Events ?? []).Select(item => item.ToRecognizer(Confidence.Confirmed, null));
            var indexer = new CallGraphIndexer(new IndexerOptions(CacheRecognizers.All,
                                                                  EventRecognizers.All.Concat(configuredEvents).ToArray()));
            graph = await indexer.IndexAsync(loaded.Solution, Path.GetFileName(solutionPath));
        }

        AssertMatches(Path.Combine(eval, SNAPSHOT_FILE), Take(graph, Git(root, "rev-parse", "HEAD"), root));
    }

    /// <summary>A corpus at a different revision is reported as exactly that, and not as the difference
    /// between the two identity lists that a different corpus is bound to produce.</summary>
    [Fact]
    public void A_different_corpus_revision_is_reported_as_the_revision()
    {
        var eval = Path.GetDirectoryName(SqlServerHarness.FindRepositoryFile(
            "plugins", "cache-detective", "skills", "scan", "evals", "eshop", "workspace.json"))!;
        var recorded = Read(Path.Combine(eval, SNAPSHOT_FILE));
        var observed = recorded with
        {
            Corpus = "0000000000000000000000000000000000000000",
            Findings = [.. recorded.Findings, "SENTINEL | only | in | the | run"]
        };

        var message = Compare(recorded, observed);

        Assert.NotNull(message);
        Assert.StartsWith(REVISION_MISMATCH, message, StringComparison.Ordinal);
        Assert.Contains(recorded.Corpus, message, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", message, StringComparison.Ordinal);
    }

    /// <summary>The identity is a property of the finding, not of the run that produced it: two runs over
    /// the same graph name the same findings, which <c>f:N</c> does not.</summary>
    [Fact]
    public void The_identity_is_the_same_in_two_runs()
    {
        var revision = "3ce3f4a";
        var first = Take(Sample(null), revision, Root);
        var second = Take(Sample(null), revision, Root);

        Assert.Equal(first.Findings, second.Findings);
        Assert.Contains(first.Findings,
                        row => row.StartsWith($"{UnguardedWriteFinding.Rule} | fixture | Sample.Write | Sample | product:{{id}} | memory | dbo.Products",
                                              StringComparison.Ordinal));
    }

    /// <summary>A finding suppressed by a TTL inside the budget is a different outcome from no finding at
    /// all, so the snapshot carries it and says which it is.</summary>
    [Fact]
    public void A_suppressed_finding_is_in_the_snapshot()
    {
        var snapshot = Take(Sample(TimeSpan.FromSeconds(30)), "3ce3f4a", Root);

        Assert.Contains(snapshot.Findings, row => row.EndsWith("| suppressed", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Findings, row => row.EndsWith("| reported", StringComparison.Ordinal));
    }

    /// <summary>An <c>unresolved</c> row is what the tool says instead of a finding, and the next task
    /// changes which of them the role rule produces — so they are snapped by kind and place too.</summary>
    [Fact]
    public void An_unresolved_row_is_in_the_snapshot()
    {
        var graph = Sample(null);
        graph.AddUnresolved(UnresolvedKind.Role, "fixture", Path.Combine(Root, "Fixture.cs"), 42,
                            "product:{id}", "the solutions disagree about the role");

        var snapshot = Take(graph, "3ce3f4a", Root);

        Assert.Equal("Role | fixture | Fixture.cs:42", Assert.Single(snapshot.Unresolved));
    }

    [Fact]
    public void An_added_identity_fails_the_comparison()
    {
        var recorded = Take(Sample(null), "3ce3f4a", Root);
        var observed = recorded with { Findings = [.. recorded.Findings, "EXTRA | one | that | was | not | there | before"] };

        var message = Compare(recorded, observed);

        Assert.NotNull(message);
        Assert.Contains("added finding: EXTRA | one | that | was | not | there | before", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_vanished_identity_fails_the_comparison()
    {
        var recorded = Take(Sample(null), "3ce3f4a", Root);
        var observed = recorded with { Findings = [] };

        var message = Compare(recorded, observed);

        Assert.NotNull(message);
        Assert.Contains($"vanished finding: {Assert.Single(recorded.Findings)}", message, StringComparison.Ordinal);
    }

    private static string Root => Path.Combine(Path.GetTempPath(), "cache-detective-fixture-corpus");

    private static void AssertMatches(string path, BehaviourSnapshot observed)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(observed, Format));
            Assert.Fail($"No snapshot at '{path}'; the run's own behaviour was written there. Review it and commit it.");
        }

        var message = Compare(Read(path), observed);
        Assert.True(message is null, message);
    }

    private static BehaviourSnapshot Read(string path) =>
        JsonSerializer.Deserialize<BehaviourSnapshot>(File.ReadAllText(path))
        ?? throw new InvalidOperationException($"'{path}' does not hold a behaviour snapshot.");

    /// <summary>The revision first and alone, then everything else at once.</summary>
    private static string? Compare(BehaviourSnapshot recorded, BehaviourSnapshot observed)
    {
        if (!string.Equals(recorded.Corpus, observed.Corpus, StringComparison.Ordinal))
        {
            return $"{REVISION_MISMATCH} Snapshot: {recorded.Corpus}. This run: {observed.Corpus}.";
        }

        var lines = Differences("finding", recorded.Findings, observed.Findings)
                   .Concat(Differences("unresolved", recorded.Unresolved, observed.Unresolved))
                   .ToArray();
        return lines.Length == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<string> Differences(string what, IReadOnlyList<string> recorded,
                                                   IReadOnlyList<string> observed)
    {
        foreach (var row in observed.Except(recorded, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            yield return $"added {what}: {row}";
        }

        foreach (var row in recorded.Except(observed, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            yield return $"vanished {what}: {row}";
        }
    }

    private static BehaviourSnapshot Take(CacheGraph graph, string revision, string corpusRoot) =>
        new(revision, Findings(graph), UnresolvedRows(graph, corpusRoot));

    private static IReadOnlyList<string> Findings(CacheGraph graph) =>
        FindingIdentities.Collect(graph).Select(finding => finding.ToString()).Order(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> UnresolvedRows(CacheGraph graph, string corpusRoot) =>
        graph.Unresolved.Select(item => $"{item.Kind} | {item.Solution ?? "-"} | {Place(item.Site, corpusRoot)}")
             .Order(StringComparer.Ordinal)
             .ToArray();


    private static string Describe(CacheKey key) => $"{key.Template}@{key.Store}";

    private static string Describe(ExternalSource source) =>
        $"external:{source.Kind}:{source.Owner}:{source.ClientName ?? "-"}:{source.Method} {source.Template}";

    private static string Place(Evidence site, string corpusRoot) => site.File is null
        ? site.Describe()
        : $"{Path.GetRelativePath(corpusRoot, site.File).Replace('\\', '/')}:{site.Line}";

    /// <summary>
    /// The last commit that changed an input of the demo corpus. The inputs are named positively — the
    /// sources, the projects, the solution and the SQL the graph is built from — rather than by excluding
    /// the expectation files one at a time, because every new expectation file added beside them would
    /// otherwise have to be remembered here, and forgetting one moves the revision without a single input
    /// having changed.
    /// </summary>
    private static string DemoRevision(string demo) =>
        Git(demo, "log", "-1", "--format=%H", "--", "*.cs", "*.csproj", "*.slnx", "*.sln", "*.sql");

    /// <summary>
    /// A clone of the corpus shape, with a commit that changes only an expectation file. The corpus
    /// revision must not move: committing an expectation used to advance it and make the snapshot fail its
    /// revision check with not one input having changed.
    /// </summary>
    [Fact]
    public void Changing_an_expectation_does_not_move_the_demo_revision()
    {
        var demo = Path.Combine(Path.GetTempPath(), $"cache-detective-corpus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(demo);
        try
        {
            File.WriteAllText(Path.Combine(demo, "Shop.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(demo, "Catalog.cs"), "public class Catalog { }");
            File.WriteAllText(Path.Combine(demo, "expected-findings.json"), "{ \"findings\": [] }");
            Git(demo, "init");
            Commit(demo, "the corpus");
            var before = DemoRevision(demo);

            File.WriteAllText(Path.Combine(demo, "expected-findings.json"), "{ \"findings\": [ \"one\" ] }");
            Commit(demo, "a new expectation");

            Assert.Equal(before, DemoRevision(demo));

            // And the guard is real: touching an input does move it.
            File.WriteAllText(Path.Combine(demo, "Catalog.cs"), "public class Catalog { public int Id; }");
            Commit(demo, "an input changed");
            Assert.NotEqual(before, DemoRevision(demo));
        }
        finally
        {
            try
            {
                Directory.Delete(demo, recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Commit(string directory, string message)
    {
        Git(directory, ["add", "."], allowEmpty: true);
        Git(directory, ["-c", "user.name=Test", "-c", "user.email=test@example.test", "commit", "-m", message],
            allowEmpty: true);
    }

    /// <summary>What git in <paramref name="directory"/> says. <paramref name="allowEmpty"/> is for the
    /// questions whose right answer is silence — a clean tree prints nothing.</summary>
    private static string Git(string directory, string[] arguments, bool allowEmpty) => Run(directory, arguments, allowEmpty);

    private static string Git(string directory, params string[] arguments) => Run(directory, arguments, allowEmpty: false);

    private static string Run(string directory, string[] arguments, bool allowEmpty)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("git could not be started.");
        var revision = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0 || (revision.Length == 0 && !allowEmpty))
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} in '{directory}' said: {error}");
        }

        return revision;
    }

    /// <summary>One unguarded write, enough to have an identity to compare.</summary>
    private static CacheGraph Sample(TimeSpan? ttl)
    {
        var graph = new CacheGraph();
        var writer = new Handler("fixture", "Sample.Write", "method", "Fixture.cs", 1) { Project = "Sample" };
        var reader = new Handler("fixture", "Sample.Read", "method", "Fixture.cs", 2) { Project = "Sample" };
        var table = new Table("dbo.Products", "default");
        var key = new CacheKey("product:{id}", "memory", ttl, [], "cache");
        graph.AddEdge(new Writes(writer, table, Confidence.Confirmed));
        graph.AddEdge(new Caches(reader, key, Confidence.Confirmed));
        graph.AddEdge(new Reads(reader, table, Confidence.Confirmed));
        return graph;
    }
}

/// <summary>The corpus revision, the findings by identity, and the <c>unresolved</c> rows by kind and
/// place — the three things the snapshot files hold.</summary>
public sealed record BehaviourSnapshot(string Corpus, IReadOnlyList<string> Findings,
                                       IReadOnlyList<string> Unresolved);
