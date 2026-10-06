using PlanForge.Repo;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Drives real git in a throwaway repository. No model calls, so this runs in the default suite.
/// </summary>
public sealed class BaselineTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));
    private readonly GitClient _git;

    public BaselineTests()
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
    public async Task An_untouched_tree_has_not_drifted()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);

        var baseline = await Baseline.CaptureAsync(_git, ct);

        Assert.NotEmpty(baseline.Head);
        Assert.Empty(await baseline.DriftedFilesAsync(_git, ct));
    }

    [Fact]
    public async Task A_file_edited_after_the_baseline_shows_up_as_drift()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        var baseline = await Baseline.CaptureAsync(_git, ct);

        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "edited after the baseline\n", ct);

        var drifted = await baseline.DriftedFilesAsync(_git, ct);

        Assert.Equal(["tracked.txt"], drifted);
    }

    [Fact]
    public async Task Context_files_at_any_depth_do_not_show_as_drift()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "CONTEXT.md", "nested/CONTEXT.md");
        var baseline = await Baseline.CaptureAsync(_git, ct);

        await WriteFileAsync("CONTEXT.md", "edited root\n", ct);
        await WriteFileAsync("nested/CONTEXT.md", "edited nested\n", ct);

        Assert.Empty(await baseline.DriftedFilesAsync(_git, ct));
    }

    [Fact]
    public async Task Adr_files_at_any_depth_do_not_show_as_drift()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "docs/adr/0001.md", "nested/docs/adr/0002.md");
        var baseline = await Baseline.CaptureAsync(_git, ct);

        await WriteFileAsync("docs/adr/0001.md", "edited root\n", ct);
        await WriteFileAsync("nested/docs/adr/0002.md", "edited nested\n", ct);

        Assert.Empty(await baseline.DriftedFilesAsync(_git, ct));
    }

    [Fact]
    public async Task Mixed_documentation_and_ordinary_edits_only_show_the_ordinary_file()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "CONTEXT.md", "nested/CONTEXT.md", "docs/adr/0001.md", "nested/docs/adr/0002.md");
        var baseline = await Baseline.CaptureAsync(_git, ct);

        await WriteFileAsync("CONTEXT.md", "edited root\n", ct);
        await WriteFileAsync("nested/CONTEXT.md", "edited nested\n", ct);
        await WriteFileAsync("docs/adr/0001.md", "edited root\n", ct);
        await WriteFileAsync("nested/docs/adr/0002.md", "edited nested\n", ct);
        await WriteFileAsync("tracked.txt", "ordinary edit\n", ct);

        Assert.Equal(["tracked.txt"], await baseline.DriftedFilesAsync(_git, ct));
    }

    [Fact]
    public async Task Capture_excludes_a_dirty_context_file_from_the_baseline_diff()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "CONTEXT.md");
        await WriteFileAsync("CONTEXT.md", "edited\n", ct);

        var baseline = await Baseline.CaptureAsync(_git, ct);

        Assert.DoesNotContain("CONTEXT.md", baseline.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dirty_documentation_and_ordinary_file_have_no_drift_after_capture()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "CONTEXT.md");
        await WriteFileAsync("CONTEXT.md", "edited documentation\n", ct);
        await WriteFileAsync("tracked.txt", "edited ordinary file\n", ct);

        var baseline = await Baseline.CaptureAsync(_git, ct);

        Assert.Contains("tracked.txt", baseline.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("CONTEXT.md", baseline.Diff, StringComparison.Ordinal);
        Assert.Empty(await baseline.DriftedFilesAsync(_git, ct));
    }

    [Fact]
    public async Task A_brand_new_file_shows_as_drift_with_its_contents_in_the_diff()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        var baseline = await Baseline.CaptureAsync(_git, ct);

        await WriteFileAsync("brand-new.txt", "created after the baseline\n", ct);

        Assert.Equal(["brand-new.txt"], await baseline.DriftedFilesAsync(_git, ct));
        Assert.Contains("created after the baseline",
                        await _git.DiffAsync(ct),
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_staged_edit_shows_as_drift()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        var baseline = await Baseline.CaptureAsync(_git, ct);

        await WriteFileAsync("tracked.txt", "staged after the baseline\n", ct);
        await _git.OutputAsync(["add", "--", "tracked.txt"], ct);

        Assert.Equal(["tracked.txt"], await baseline.DriftedFilesAsync(_git, ct));
    }

    [Fact]
    public async Task An_untracked_file_present_at_capture_is_not_drift()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("pre-existing.txt", "already here at forge.begin\n", ct);

        var baseline = await Baseline.CaptureAsync(_git, ct);

        Assert.Contains("pre-existing.txt", baseline.Diff, StringComparison.Ordinal);
        Assert.Empty(await baseline.DriftedFilesAsync(_git, ct));
    }

    [Fact]
    public async Task Brand_new_documentation_does_not_show_as_drift()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        var baseline = await Baseline.CaptureAsync(_git, ct);

        await WriteFileAsync("docs/adr/0009-new-decision.md", "written during the interview\n", ct);
        await WriteFileAsync("nested/CONTEXT.md", "written during the interview\n", ct);

        Assert.Empty(await baseline.DriftedFilesAsync(_git, ct));
    }

    [Fact]
    public async Task A_new_binary_file_contributes_no_contents_to_the_diff()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        var baseline = await Baseline.CaptureAsync(_git, ct);

        var path = Path.Combine(_repo, "binary.bin");
        await File.WriteAllBytesAsync(path, [0x00, 0x01, 0x02, 0x00, 0xff], ct);

        var diff = await _git.DiffAsync(ct);

        Assert.Contains("Binary files", diff, StringComparison.Ordinal);
        Assert.Equal(["binary.bin"], await baseline.DriftedFilesAsync(_git, ct));
    }

    [Fact]
    public async Task A_self_ignoring_folder_stays_outside_the_window()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        var baseline = await Baseline.CaptureAsync(_git, ct);

        await WriteFileAsync(".forge/.gitignore", "*\n", ct);
        await WriteFileAsync(".forge/run1/state.json", "{}\n", ct);

        Assert.Empty(await baseline.DriftedFilesAsync(_git, ct));
    }

    /// <summary>
    /// The shape a builder turn takes: earlier turns have already changed the tree, and this
    /// baseline is taken in the middle of the run. Drift is what changed after it, not everything
    /// that differs from <c>HEAD</c> — run 20260919-160244-77fd30 told the orchestrator a failed
    /// turn had written 92 files, which was the whole run and not the turn, in the one message that
    /// turn left behind.
    /// </summary>
    [Fact]
    public async Task Drift_after_a_mid_run_baseline_excludes_what_earlier_turns_changed()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "earlier.txt");
        await WriteFileAsync("earlier.txt", "written by an earlier turn\n", ct);
        await WriteFileAsync("untracked-earlier.txt", "created by an earlier turn\n", ct);

        var baseline = await Baseline.CaptureAsync(_git, ct);
        await WriteFileAsync("tracked.txt", "written by this turn\n", ct);

        Assert.Equal(["tracked.txt"], await baseline.DriftedFilesAsync(_git, ct));
    }

    /// <summary>
    /// The other half of the same comparison: a file the baseline already found dirty is drift
    /// again once this turn changes it further.
    /// </summary>
    [Fact]
    public async Task A_file_already_dirty_at_the_baseline_drifts_when_it_is_changed_again()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "written by an earlier turn\n", ct);

        var baseline = await Baseline.CaptureAsync(_git, ct);
        await WriteFileAsync("tracked.txt", "written by an earlier turn, then by this one\n", ct);

        Assert.Equal(["tracked.txt"], await baseline.DriftedFilesAsync(_git, ct));
    }

    [Fact]
    public async Task A_review_window_keeps_work_committed_after_the_baseline()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        var baseline = await Baseline.CaptureAsync(_git, ct);

        await WriteFileAsync("tracked.txt", "committed during the run\n", ct);
        await _git.OutputAsync(["add", "--", "tracked.txt"], ct);
        await _git.OutputAsync(["commit", "-qm", "run work"], ct);

        var window = await _git.ReadReviewWindowAsync(baseline.Head, [], ct);

        Assert.False(window.IsFallback);
        Assert.Equal(baseline.Head, window.BaseHead);
        Assert.Equal(["tracked.txt"], window.ChangedPaths);
        Assert.Contains("committed during the run", window.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_review_window_contains_committed_and_uncommitted_work_once()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "dirty.txt");
        var baseline = await Baseline.CaptureAsync(_git, ct);

        await WriteFileAsync("tracked.txt", "committed during the run\n", ct);
        await _git.OutputAsync(["add", "--", "tracked.txt"], ct);
        await _git.OutputAsync(["commit", "-qm", "committed phase"], ct);
        await WriteFileAsync("dirty.txt", "still dirty\n", ct);

        var window = await _git.ReadReviewWindowAsync(baseline.Head, [], ct);

        Assert.Equal(["dirty.txt", "tracked.txt"], window.ChangedPaths.Order(StringComparer.Ordinal));
        Assert.Contains("committed during the run", window.Diff, StringComparison.Ordinal);
        Assert.Contains("still dirty", window.Diff, StringComparison.Ordinal);
        Assert.Equal(1, window.ChangedPaths.Count(path => path == "tracked.txt"));
        Assert.Equal(1, window.ChangedPaths.Count(path => path == "dirty.txt"));
    }

    [Fact]
    public async Task A_review_window_includes_dirt_already_present_when_the_run_began()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        await WriteFileAsync("tracked.txt", "dirty at forge.begin\n", ct);
        var baseline = await Baseline.CaptureAsync(_git, ct);

        var window = await _git.ReadReviewWindowAsync(baseline.Head, [], ct);

        Assert.Equal(["tracked.txt"], window.ChangedPaths);
        Assert.Contains("dirty at forge.begin", window.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_review_window_excludes_work_reverted_to_the_baseline_state()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        var baseline = await Baseline.CaptureAsync(_git, ct);
        await WriteFileAsync("tracked.txt", "temporary run work\n", ct);
        await _git.OutputAsync(["add", "--", "tracked.txt"], ct);
        await _git.OutputAsync(["commit", "-qm", "temporary phase"], ct);
        await WriteFileAsync("tracked.txt", "original\n", ct);

        var window = await _git.ReadReviewWindowAsync(baseline.Head, [], ct);

        Assert.Empty(window.ChangedPaths);
        Assert.Empty(window.Diff);
    }

    [Fact]
    public async Task A_review_window_falls_back_to_head_after_history_diverges()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);
        var baseline = await Baseline.CaptureAsync(_git, ct);

        await _git.OutputAsync(["checkout", "-q", "--orphan", "replacement"], ct);
        await WriteFileAsync("tracked.txt", "replacement history\n", ct);
        await _git.OutputAsync(["add", "--", "tracked.txt"], ct);
        await _git.OutputAsync(["commit", "-qm", "replacement root"], ct);
        var fallbackHead = (await _git.OutputAsync(["rev-parse", "HEAD"], ct)).Trim();
        await WriteFileAsync("tracked.txt", "dirty after divergence\n", ct);

        var window = await _git.ReadReviewWindowAsync(baseline.Head, [], ct);

        Assert.True(window.IsFallback);
        Assert.Equal(fallbackHead, window.BaseHead);
        Assert.Equal(["tracked.txt"], window.ChangedPaths);
        Assert.Contains("-replacement history", window.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("-original", window.Diff, StringComparison.Ordinal);
    }

    /// <summary>
    /// The window is cut into one block per file so a refusal can name the largest, and the cut
    /// loses nothing: the tracked blocks put back together are git's own diff. A round's exclusion
    /// leaves both reads, the paths and the content, as the documentation's does.
    /// </summary>
    [Fact]
    public async Task A_review_window_is_one_block_per_file_and_an_exclusion_leaves_both_reads()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "dirty.txt");
        var baseline = await Baseline.CaptureAsync(_git, ct);
        await WriteFileAsync("tracked.txt", "changed\nand longer\n", ct);
        await WriteFileAsync("dirty.txt", "dirty\n", ct);
        await WriteFileAsync("new.txt", "brand new\n", ct);
        await WriteFileAsync("build/gate.ps1", "run tooling\n", ct);

        var window = await _git.ReadReviewWindowAsync(baseline.Head, ["build"], ct);

        Assert.Equal(["dirty.txt", "tracked.txt", "new.txt"], window.Files.Select(file => file.Path));
        Assert.Equal([false, false, true], window.Files.Select(file => file.Untracked));
        Assert.All(window.Files, file => Assert.StartsWith("diff --git ", file.Diff, StringComparison.Ordinal));
        Assert.Equal(await _git.OutputAsync(["diff", baseline.Head], ct),
                     string.Join('\n', window.Files.Where(file => !file.Untracked).Select(file => file.Diff)));
        Assert.DoesNotContain("build/gate.ps1", window.ChangedPaths);
        Assert.DoesNotContain("run tooling", window.Diff, StringComparison.Ordinal);
    }

    /// <summary>
    /// A workspace below the repository root, as concurrency-hunter's is: git's diff headers carry
    /// the workspace's own path and a pathspec does not, so the window names a tracked file the way
    /// <c>excludePaths</c> takes it, and handing that name back takes the file out.
    /// </summary>
    [Fact]
    public async Task A_workspace_below_the_repository_root_names_its_files_as_an_exclusion_takes_them()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct, "plugin/a.txt");
        var baseline = await Baseline.CaptureAsync(_git, ct);
        await WriteFileAsync("plugin/a.txt", "changed\n", ct);
        await WriteFileAsync("plugin/b.txt", "new\n", ct);
        var plugin = new GitClient(Path.Combine(_repo, "plugin"));

        var whole = await plugin.ReadReviewWindowAsync(baseline.Head, [], ct);
        var narrowed = await plugin.ReadReviewWindowAsync(baseline.Head, [whole.Files[0].Path], ct);

        Assert.Equal(["a.txt", "b.txt"], whole.Files.Select(file => file.Path));
        Assert.Equal(["b.txt"], narrowed.Files.Select(file => file.Path));
    }

    [Fact]
    public async Task A_review_window_refuses_an_unresolvable_baseline()
    {
        var ct = CancellationToken.None;
        await InitialCommitAsync(ct);

        var error = await Assert.ThrowsAsync<ReviewBaselineUnavailableException>(() =>
            _git.ReadReviewWindowAsync("missing-baseline", [], ct));

        Assert.Contains("begin a new run", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing-baseline", error.Message, StringComparison.Ordinal);
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
}
