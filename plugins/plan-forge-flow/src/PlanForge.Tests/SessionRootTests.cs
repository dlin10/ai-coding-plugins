using System.Text.Json;
using ModelContextProtocol.Protocol;
using PlanForge.Mcp;
using PlanForge.Repo;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Where a run's own files land. The workspace root is the window under review and the session root
/// is where the user is sitting; they are the same directory until a monorepo pulls them apart, and
/// then everything a person reads has to follow the session while the git window stays put — see
/// <see cref="SessionRoots"/> and issue #53.
/// </summary>
// The roots capability is deprecated by the specification of 2026-07-28; SessionRoots explains why
// this server still uses it and what happens when it goes.
#pragma warning disable MCP9005
public sealed class SessionRootTests : IDisposable
{
    private const string RunId = "20260101-000000-abcdef";

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));
    private readonly string _session;
    private readonly GitClient _git;

    public SessionRootTests()
    {
        _session = Path.Combine(_repo, "plugins", "one");
        Directory.CreateDirectory(_session);
        _git = new GitClient(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_run_lands_beside_the_session_rather_than_in_the_tree_under_review()
    {
        var run = await RunDirectory.CreateAsync(SessionRoots.At(_session), _repo, RunId, CancellationToken.None);

        Assert.Equal(Path.Combine(_session, ".forge", RunId), run.Path);
        Assert.False(Directory.Exists(Path.Combine(_repo, ".forge")));
    }

    [Fact]
    public async Task A_host_that_declares_no_root_keeps_the_run_under_the_workspace_root()
    {
        var run = await RunDirectory.CreateAsync(SessionRoots.None, _repo, RunId, CancellationToken.None);

        Assert.Equal(Path.Combine(_repo, ".forge", RunId), run.Path);
    }

    [Fact]
    public async Task A_run_beside_the_session_is_found_again_by_workspace_root_and_run_id()
    {
        var roots = SessionRoots.At(_session);
        var created = await RunDirectory.CreateAsync(roots, _repo, RunId, CancellationToken.None);

        var opened = await RunDirectory.OpenAsync(roots, _repo, RunId, CancellationToken.None);

        Assert.Equal(created.Path, opened.Path);
    }

    /// <summary>
    /// The upgrade path. A run begun before the session root was consulted sits under the workspace
    /// root, and the same server — now told about a session — has to keep finding it there.
    /// </summary>
    [Fact]
    public async Task A_run_begun_under_the_workspace_root_is_still_found_once_a_root_is_declared()
    {
        var created = await RunDirectory.CreateAsync(SessionRoots.None, _repo, RunId, CancellationToken.None);

        var opened = await RunDirectory.OpenAsync(SessionRoots.At(_session), _repo, RunId, CancellationToken.None);

        Assert.Equal(created.Path, opened.Path);
    }

    /// <summary>
    /// The half of the split that must not move with the run folder: the baseline and the drift are
    /// the repository's, not the session's. A `.` pathspec is resolved against git's own working
    /// directory, so a run that also narrowed the git window would have hidden every change outside
    /// the session — the failure mode issue #25 recorded.
    /// </summary>
    [Fact]
    public async Task Drift_is_still_reported_for_the_whole_repository()
    {
        var ct = CancellationToken.None;
        var roots = SessionRoots.At(_session);
        var run = await StartRunAsync(roots, ct);

        await File.WriteAllTextAsync(Path.Combine(_repo, "README.md"), "edited after the baseline\n", ct);
        await File.WriteAllTextAsync(Path.Combine(_session, "tracked.txt"), "edited too\n", ct);

        var json = await ForgeTools.Status(roots, _repo, run.RunId, ct);
        var status = JsonSerializer.Deserialize(json, ForgeToolJson.Default.StatusResult)!;

        Assert.Equal(["README.md", "plugins/one/tracked.txt"], status.DriftedFiles);
    }

    [Fact]
    public void The_first_root_naming_a_directory_wins_and_the_rest_are_skipped()
    {
        var chosen = SessionRoots.FirstDirectory([
            new Root { Uri = "https://example.invalid/not-a-file" },
            new Root { Uri = new Uri(Path.Combine(_repo, "gone")).AbsoluteUri },
            new Root { Uri = new Uri(_session).AbsoluteUri },
            new Root { Uri = new Uri(_repo).AbsoluteUri }
        ]);

        Assert.Equal(_session, chosen);
    }

    [Fact]
    public void A_client_answering_with_no_usable_root_reads_as_no_root_at_all()
    {
        Assert.Null(SessionRoots.FirstDirectory([]));
        Assert.Null(SessionRoots.FirstDirectory([new Root { Uri = "file:///" + Guid.NewGuid().ToString("n") }]));
    }

    private async Task<RunDirectory> StartRunAsync(SessionRoots roots, CancellationToken ct)
    {
        await _git.OutputAsync(["init", "-q"], ct);
        await _git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await _git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        await File.WriteAllTextAsync(Path.Combine(_repo, "README.md"), "original\n", ct);
        await File.WriteAllTextAsync(Path.Combine(_session, "tracked.txt"), "original\n", ct);
        await _git.OutputAsync(["add", "."], ct);
        await _git.OutputAsync(["commit", "-qm", "initial"], ct);

        var run = await RunDirectory.CreateAsync(roots, _repo, RunId, ct);
        var baseline = await Baseline.CaptureAsync(_git, ct);
        run.WriteBaseline(baseline);
        run.WriteState(new RunState(run.RunId, _repo, "Text", DateTimeOffset.Now,
            ReviewRounds: 0, ReviewRoundCap: 5, BaselineHead: baseline.Head));

        return run;
    }
}
#pragma warning restore MCP9005
