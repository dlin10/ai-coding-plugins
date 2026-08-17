using PlanForge.Acts;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class CodeReviewTests : IDisposable
{
    private const string Plan = "## Approach\n\n1. **Change tracked.txt.** Verified by inspection.\n";

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));
    private readonly GitClient _git;

    public CodeReviewTests()
    {
        Directory.CreateDirectory(_repo);
        _git = new GitClient(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (DirectoryNotFoundException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task The_plan_and_the_diff_reach_the_critic_without_documentation()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "CONTEXT.md", "docs/adr/0001.md");
        await WriteFileAsync("CONTEXT.md", "context change\n", ct);
        await WriteFileAsync("docs/adr/0001.md", "adr change\n", ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));
        var run = NewRun();

        var critique = await NewReview(critic).ReviewAsync(run, new Selection("critic-model", "high"), ct);

        var session = Assert.Single(critic.Sessions);
        Assert.Equal("approve", critique.Verdict);
        Assert.Contains("# Approved plan", session.PromptText, StringComparison.Ordinal);
        Assert.Contains("Change tracked.txt.", session.PromptText, StringComparison.Ordinal);
        Assert.Contains("ordinary change", session.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("CONTEXT.md", session.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("adr change", session.PromptText, StringComparison.Ordinal);
        Assert.Equal(1, run.ReadState().CodeReviewRounds);
    }

    [Fact]
    public async Task The_scope_contract_is_part_of_the_critic_system_prompt()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));

        await NewReview(critic).ReviewAsync(NewRun(), new Selection("critic-model", null), ct);

        var session = Assert.Single(critic.Sessions);
        Assert.Contains("judging a diff against the approved plan", session.Role.SystemPrompt,
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task Documentation_only_tree_is_reported_as_nothing_to_review()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "CONTEXT.md", "docs/adr/0001.md");
        await WriteFileAsync("CONTEXT.md", "context change\n", ct);
        await WriteFileAsync("docs/adr/0001.md", "adr change\n", ct);

        var critic = new RecordingVendor("claude");
        var run = NewRun();

        var critique = await NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), ct);

        Assert.Equal("approve", critique.Verdict);
        Assert.Equal("nothing to review", critique.Summary);
        Assert.Empty(critic.Sessions);
        Assert.Equal(0, run.ReadState().CodeReviewRounds);
    }

    [Fact]
    public async Task Review_rounds_continue_the_plan_review_numbering()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("revise", [], "fix it"));
        var run = NewRun(reviewRounds: 5);

        await NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), ct);

        // Plan review used rounds 1-5, so the first code-review round is 6 — never a collision.
        Assert.True(File.Exists(Path.Combine(run.Path, "critiques", "round-06.json")));
    }

    [Fact]
    public async Task Review_refuses_beyond_the_cap()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        var run = NewRun(codeReviewRounds: 3);

        await Assert.ThrowsAsync<CodeReviewCapReachedException>(() =>
            NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), ct));

        Assert.Empty(critic.Sessions);
    }

    [Fact]
    public async Task Review_requires_an_approved_plan()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        var run = NewRun(approved: false);

        await Assert.ThrowsAsync<NotApprovedException>(() =>
            NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), ct));

        Assert.Empty(critic.Sessions);
    }

    [Fact]
    public async Task Sensitive_appsettings_under_an_adr_does_not_abort_the_review()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "docs/adr/appsettings.Production.json");
        await WriteFileAsync("docs/adr/appsettings.Production.json", "secret-looking change\n", ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));

        var critique = await NewReview(critic).ReviewAsync(NewRun(), new Selection("critic-model", null), ct);

        // The name is sensitive, but the pathspec keeps the file's contents out of everything sent
        // to a vendor, so there is nothing to leak and nothing to refuse.
        Assert.Equal("approve", critique.Verdict);
        var session = Assert.Single(critic.Sessions);
        Assert.Contains("tracked.txt", session.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("appsettings.Production.json", session.PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sensitive_appsettings_at_an_unexcluded_path_aborts_the_review()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "config/appsettings.Production.json");
        await WriteFileAsync("config/appsettings.Production.json", "secret-looking change\n", ct);

        var critic = new RecordingVendor("claude");

        await Assert.ThrowsAsync<SensitiveContentException>(() =>
            NewReview(critic).ReviewAsync(NewRun(), new Selection("critic-model", null), ct));

        Assert.Empty(critic.Sessions);
    }

    [Fact]
    public async Task Sensitive_paths_are_guarded_before_an_empty_diff_is_accepted()
    {
        var critic = new RecordingVendor("claude");
        var git = new ReviewGit(["config/password.txt"], string.Empty);

        await Assert.ThrowsAsync<SensitiveContentException>(() =>
            NewReview(critic, git).ReviewAsync(NewRun(), new Selection("critic-model", null), CancellationToken.None));

        Assert.Empty(critic.Sessions);
    }

    [Fact]
    public async Task The_critique_lands_in_the_flow_log_numbered_by_its_own_loop()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("revise", [new Finding("minor", "tracked.txt", "sloppy change")], "one nit"));
        var run = NewRun(reviewRounds: 3);

        await NewReview(critic).ReviewAsync(run, new Selection("critic-model", null), ct);

        // The review log numbers rounds in one sequence across both loops; the flow log is
        // act-labelled, so its code-review rounds start at 1.
        Assert.Contains("## Round 4", run.ReadReviewLog(), StringComparison.Ordinal);
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Code review — round 1", flow, StringComparison.Ordinal);
        Assert.Contains("sloppy change", flow, StringComparison.Ordinal);
    }

    private CodeReview NewReview(RecordingVendor critic, IReviewGit? reviewGit = null) =>
        new(critic, new PromptLibrary(RepositoryPrompts()), reviewGit ?? _git);

    private RunDirectory NewRun(bool approved = true, int reviewRounds = 0, int codeReviewRounds = 0)
    {
        var run = RunDirectory.Create(_repo, "review");
        run.WriteState(new RunState("review", _repo, "Text", DateTimeOffset.Now, reviewRounds, 5,
                                    Approved: approved, CodeReviewRounds: codeReviewRounds, CodeReviewRoundCap: 3));
        run.WritePlan(Plan);
        return run;
    }

    private async Task InitialCommitAsync(CancellationToken ct, params string[] additionalPaths)
    {
        await _git.OutputAsync(["init", "-q"], ct);
        await _git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await _git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        await WriteFileAsync("tracked.txt", "original\n", ct);
        foreach (var path in additionalPaths)
            await WriteFileAsync(path, "original\n", ct);

        await _git.OutputAsync(["add", "--", "tracked.txt", .. additionalPaths], ct);
        await _git.OutputAsync(["commit", "-qm", "initial"], ct);
    }

    private async Task WriteFileAsync(string relativePath, string contents, CancellationToken ct)
    {
        var path = Path.Combine(_repo, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents, ct);
    }

    private sealed class ReviewGit(IReadOnlyList<string> changedPaths, string diff) : IReviewGit
    {
        public Task<string> DiffAsync(IReadOnlyList<string> pathspec, CancellationToken ct) =>
            Task.FromResult(diff);

        public Task<IReadOnlyList<string>> ChangedPathsAsync(IReadOnlyList<string> pathspec,
                                                             CancellationToken ct) =>
            Task.FromResult(changedPaths);
    }

    private static string RepositoryPrompts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var prompts = Path.Combine(directory.FullName, "prompts");
            if (Directory.Exists(prompts)) return prompts;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("could not locate the prompts folder above the test binary");
    }
}
