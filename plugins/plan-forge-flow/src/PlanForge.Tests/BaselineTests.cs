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

    private async Task InitialCommitAsync(CancellationToken ct)
    {
        await _git.OutputAsync(["init", "-q"], ct);
        await _git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await _git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "original\n", ct);
        await _git.OutputAsync(["add", "tracked.txt"], ct);
        await _git.OutputAsync(["commit", "-qm", "initial"], ct);
    }
}
