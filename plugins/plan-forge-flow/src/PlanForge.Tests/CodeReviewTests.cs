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
    public async Task Mixed_diff_reaches_the_critic_without_documentation()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "CONTEXT.md", "docs/adr/0001.md");
        await WriteFileAsync("CONTEXT.md", "context change\n", ct);
        await WriteFileAsync("docs/adr/0001.md", "adr change\n", ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("approve", [], "looks good"));
        var builder = new RecordingVendor("codex");

        var outcome = await NewReview(critic, builder).RunAsync(
            NewRun(), new Selection("critic-model", "high"), new Selection("builder-model", "low"), 3, ct);

        var session = Assert.Single(critic.Sessions);
        Assert.Equal("approve", outcome.Verdict?.Verdict);
        Assert.Contains("tracked.txt", session.PromptText, StringComparison.Ordinal);
        Assert.Contains("ordinary change", session.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("CONTEXT.md", session.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("adr change", session.PromptText, StringComparison.Ordinal);
        Assert.Empty(builder.Sessions);
    }

    [Fact]
    public async Task Documentation_only_tree_is_reported_as_nothing_to_review()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "CONTEXT.md", "docs/adr/0001.md");
        await WriteFileAsync("CONTEXT.md", "context change\n", ct);
        await WriteFileAsync("docs/adr/0001.md", "adr change\n", ct);

        var critic = new RecordingVendor("claude");
        var builder = new RecordingVendor("codex");

        var outcome = await NewReview(critic, builder).RunAsync(
            NewRun(), new Selection("critic-model", null), new Selection("builder-model", null), 3, ct);

        Assert.Equal("approve", outcome.Verdict?.Verdict);
        Assert.Equal("nothing to review", outcome.Verdict?.Summary);
        Assert.Equal(0, outcome.Rounds);
        Assert.Empty(critic.Sessions);
        Assert.Empty(builder.Sessions);
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
        var builder = new RecordingVendor("codex");

        var outcome = await NewReview(critic, builder).RunAsync(
            NewRun(), new Selection("critic-model", null), new Selection("builder-model", null), 3, ct);

        // The name is sensitive, but the pathspec keeps the file's contents out of everything sent
        // to a vendor, so there is nothing to leak and nothing to refuse.
        Assert.Equal("approve", outcome.Verdict?.Verdict);
        var session = Assert.Single(critic.Sessions);
        Assert.Contains("tracked.txt", session.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("appsettings.Production.json", session.PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sensitive_critic_findings_are_guarded_before_the_builder_starts()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("revise", [], "api_key: Abcdefghijklmnop1234+"));
        var builder = new RecordingVendor("codex");

        await Assert.ThrowsAsync<SensitiveContentException>(() => NewReview(critic, builder).RunAsync(
            NewRun(), new Selection("critic-model", null), new Selection("builder-model", null), 3, ct));

        Assert.Empty(builder.Sessions);
    }

    [Fact]
    public async Task Sensitive_appsettings_at_an_unexcluded_path_aborts_the_review()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "config/appsettings.Production.json");
        await WriteFileAsync("config/appsettings.Production.json", "secret-looking change\n", ct);

        var critic = new RecordingVendor("claude");
        var builder = new RecordingVendor("codex");

        await Assert.ThrowsAsync<SensitiveContentException>(() => NewReview(critic, builder).RunAsync(
            NewRun(), new Selection("critic-model", null), new Selection("builder-model", null), 3, ct));

        Assert.Empty(critic.Sessions);
    }

    [Fact]
    public async Task A_fresh_code_review_builder_does_not_receive_a_foreign_token_and_records_its_vendor()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("revise", [], "fix it"));
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], "done"), "new-token");
        var run = NewRun("claude", "foreign-token");

        await NewReview(critic, builder).RunAsync(
            run, new Selection("critic-model", null), new Selection("builder-model", "low"), 1, ct);

        Assert.Null(Assert.Single(builder.Sessions).StartedWithResumeToken);
        Assert.Equal("new-token", run.ReadState().BuilderSessionId);
        Assert.Equal(builder.Id, run.ReadState().BuilderVendor);
    }

    [Fact]
    public async Task A_fresh_null_code_review_token_clears_the_foreign_token_and_stays_fresh()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("revise", [], "first fix"));
        critic.Enqueue(new Critique("revise", [], "second fix"));
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], "done"));
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], "done"));
        var run = NewRun("claude", "foreign-token");

        await NewReview(critic, builder).RunAsync(
            run, new Selection("critic-model", null), new Selection("builder-model", "low"), 2, ct);

        Assert.Equal(2, builder.Sessions.Count);
        Assert.All(builder.Sessions, session => Assert.Null(session.StartedWithResumeToken));
        Assert.Equal(string.Empty, run.ReadState().BuilderSessionId);
        Assert.Equal(builder.Id, run.ReadState().BuilderVendor);
    }

    [Fact]
    public async Task A_code_review_reuses_the_builder_token_on_a_second_revise_round()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("revise", [], "first fix"));
        critic.Enqueue(new Critique("revise", [], "second fix"));
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], "done"), "next-token");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], "done"), "final-token");
        var run = NewRun("codex", "existing-token");

        await NewReview(critic, builder).RunAsync(
            run, new Selection("critic-model", "high"), new Selection("builder-model", "low"), 2, ct);

        Assert.Equal(2, builder.Sessions.Count);
        Assert.Equal("existing-token", builder.Sessions[0].StartedWithResumeToken);
        Assert.Equal("next-token", builder.Sessions[1].StartedWithResumeToken);
        Assert.Equal("final-token", run.ReadState().BuilderSessionId);
        Assert.Equal(builder.Id, run.ReadState().BuilderVendor);
    }

    [Fact]
    public async Task A_revise_round_uses_each_vendor_and_its_own_selection()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "ordinary change\n", ct);

        var critic = new RecordingVendor("claude");
        critic.Enqueue(new Critique("revise", [new Finding("major", "tracked.txt", "fix it")], "fix it"));
        critic.Enqueue(new Critique("approve", [], "fixed"));
        var builder = new RecordingVendor("codex");
        builder.Enqueue(new BuildResult("done", ["tracked.txt"], "fixed"), null);
        var criticSelection = new Selection("critic-model", "high");
        var builderSelection = new Selection("builder-model", "low");

        var run = NewRun();
        var outcome = await NewReview(critic, builder).RunAsync(
            run, criticSelection, builderSelection, 3, ct);

        Assert.Equal("approve", outcome.Verdict?.Verdict);
        Assert.Equal(2, critic.Sessions.Count);
        Assert.All(critic.Sessions, session =>
        {
            Assert.Equal(VendorRole.Critic, session.Role.Role);
            Assert.Equal(criticSelection, session.Selection);
            Assert.Null(session.StartedWithResumeToken);
        });

        var builderSession = Assert.Single(builder.Sessions);
        Assert.Equal(VendorRole.Builder, builderSession.Role.Role);
        Assert.Equal(builderSelection, builderSession.Selection);
        Assert.Null(builderSession.StartedWithResumeToken);
        Assert.Null(builderSession.ResumeToken);
        Assert.Equal(string.Empty, run.ReadState().BuilderSessionId);
        Assert.Equal(builder.Id, run.ReadState().BuilderVendor);
    }

    private CodeReview NewReview(RecordingVendor critic, RecordingVendor builder) =>
        new(critic, builder, new PromptLibrary(RepositoryPrompts()), _git);

    private RunDirectory NewRun(string builderVendor = "", string builderSessionId = "")
    {
        var run = RunDirectory.Create(_repo, "review");
        run.WriteState(new RunState("review", _repo, "Text", DateTimeOffset.Now, 0, 5,
            BuilderSessionId: builderSessionId, BuilderVendor: builderVendor));
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
