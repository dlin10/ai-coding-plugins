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
        try { Directory.Delete(_repo, recursive: true); } catch (DirectoryNotFoundException) { }
        catch (UnauthorizedAccessException) { }
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
