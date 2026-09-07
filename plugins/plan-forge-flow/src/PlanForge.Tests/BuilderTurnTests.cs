using PlanForge.Acts;
using PlanForge.Repo;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// What a builder turn says when it does not come back. Run 20260902-224201-7bf03b is the case
/// these hold: a <c>forge.review.fix</c> that had applied most of five findings died while
/// reporting, the orchestrator was told only "An error occurred invoking 'forge.review.fix'", and
/// read that as an act that changed nothing.
/// </summary>
public sealed class BuilderTurnTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));
    private readonly GitClient _git;

    public BuilderTurnTests()
    {
        Directory.CreateDirectory(_repo);
        _git = new GitClient(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_turn_that_dies_after_writing_names_the_files_it_wrote()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        var session = new FailingSession(() => Write("tracked.txt", "written by the builder\n"),
                                         new InvalidOperationException("the target element has type 'String'"));

        var error = await Assert.ThrowsAsync<VendorException>(
            () => BuilderTurn.RunAsync(session, _repo, "fix these", ct));

        Assert.Contains("tracked.txt", error.Message, StringComparison.Ordinal);
        Assert.Contains("still on disk", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The message the orchestrator had to go to the run log for. It reaches the caller because
    /// <see cref="VendorException"/> is declared in the server's own assembly; see ToolErrors.
    /// </summary>
    [Fact]
    public async Task The_underlying_failure_survives_in_the_message_and_as_the_inner_exception()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        var cause = new InvalidOperationException("the target element has type 'String'");
        var session = new FailingSession(() => { }, cause);

        var error = await Assert.ThrowsAsync<VendorException>(
            () => BuilderTurn.RunAsync(session, _repo, "fix these", ct));

        Assert.Contains("the target element has type 'String'", error.Message, StringComparison.Ordinal);
        Assert.Same(cause, error.InnerException);
    }

    /// <summary>
    /// The case that made a name-only comparison useless: a fix round edits a file an earlier round
    /// already changed, so the path is not new to the tree and only its content moves.
    /// </summary>
    [Fact]
    public async Task A_rewrite_of_an_already_dirty_file_still_counts_as_written()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        Write("tracked.txt", "changed by an earlier round\n");

        var session = new FailingSession(() => Write("tracked.txt", "changed again by this turn\n"),
                                         new InvalidOperationException("died while reporting"));

        var error = await Assert.ThrowsAsync<VendorException>(
            () => BuilderTurn.RunAsync(session, _repo, "fix these", ct));

        Assert.Contains("tracked.txt", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_turn_that_dies_having_written_nothing_says_so()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        var session = new FailingSession(() => { }, new InvalidOperationException("died on the first line"));

        var error = await Assert.ThrowsAsync<VendorException>(
            () => BuilderTurn.RunAsync(session, _repo, "fix these", ct));

        Assert.Contains("left the working tree as it found it", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reading the tree is forensics, not a precondition: a workspace git cannot read must still
    /// build. Nothing here is a git repository.
    /// </summary>
    [Fact]
    public async Task A_workspace_git_cannot_read_does_not_stop_the_turn()
    {
        var session = new RecordingVendorSession(new RoleSpec(VendorRole.Builder, "prompt"),
                                                 new Selection("model", null), null,
                                                 new BuildResult("done", ["a.cs"],
                                                                 new Verification("passed", "ran"), "done"),
                                                 "token");

        var result = await BuilderTurn.RunAsync(session, _repo, "build this", CancellationToken.None);

        Assert.Equal("done", result.Status);
    }

    [Fact]
    public async Task Cancellation_passes_through_untouched()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var session = new FailingSession(() => { }, new InvalidOperationException("never reached"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BuilderTurn.RunAsync(session, _repo, "fix these", cancelled.Token));
    }

    private void Write(string relativePath, string contents) =>
        File.WriteAllText(Path.Combine(_repo, relativePath), contents);

    private async Task InitialCommitAsync(CancellationToken ct)
    {
        await _git.OutputAsync(["init", "-q"], ct);
        await _git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await _git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        Write("tracked.txt", "original\n");
        await _git.OutputAsync(["add", "--", "tracked.txt"], ct);
        await _git.OutputAsync(["commit", "-qm", "initial"], ct);
    }

    /// <summary>A session that writes what the builder wrote, then dies the way the CLI parse did.</summary>
    private sealed class FailingSession(Action write, Exception failure) : IVendorSession
    {
        public IAsyncEnumerable<VendorEvent> Events => Empty();

        public bool CanResume => true;

        public string? ResumeToken => "token";

        public Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            write();
            throw failure;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<VendorEvent> Empty()
        {
            yield break;
        }
    }
}
