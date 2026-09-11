using PlanForge.Infrastructure;
using PlanForge.Vendors;

namespace PlanForge.Repo;

/// <summary>
/// The final code state handed to a Critic, with the identity of the Git range that produced both
/// its changed paths and its content diff. See docs/adr/0016.
/// </summary>
internal sealed record ReviewWindow(string BaselineHead,
                                    string BaseHead,
                                    bool IsFallback,
                                    IReadOnlyList<string> ChangedPaths,
                                    string Diff);

internal interface IReviewGit
{
    /// <summary>Resolves the run baseline or its disclosed fallback, then reads that one window.</summary>
    Task<ReviewWindow> ReadReviewWindowAsync(string baselineHead, CancellationToken ct);
}

internal sealed class GitClient : IReviewGit
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    // These exclusions match by name at any depth because no caller declares where the
    // documentation lives. `plugins/plan-forge-flow/` in this repository is why a root-anchored
    // rule would not do.
    private static readonly string[] ReviewContentFilter =
    [
        ".",
        ":(exclude)CONTEXT.md",
        ":(exclude)**/CONTEXT.md",
        ":(exclude)docs/adr/**",
        ":(exclude)**/docs/adr/**"
    ];

    private readonly string _workspace;

    public GitClient(string workspace) => _workspace = workspace;

    public async Task<string> OutputAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var spec = new ProcessSpec("git", ["-C", _workspace, .. arguments], _workspace, string.Empty);
        var lines = await StreamingProcess.CollectAsync(spec, Timeout, ct);
        return string.Join('\n', lines);
    }

    public Task<string> DiffAsync(CancellationToken ct) => DiffAsync("HEAD", ct);

    public async Task<ReviewWindow> ReadReviewWindowAsync(string baselineHead, CancellationToken ct)
    {
        var resolvedBaseline = await ResolveBaselineAsync(baselineHead, ct);
        var isFallback = !await IsAncestorAsync(resolvedBaseline, ct);
        var baseHead = isFallback ? (await OutputAsync(["rev-parse", "HEAD"], ct)).Trim() : resolvedBaseline;
        var changedPaths = await ChangedPathsAsync(baseHead, ct);
        var diff = await DiffAsync(baseHead, ct);
        return new ReviewWindow(resolvedBaseline, baseHead, isFallback, changedPaths, diff);
    }

    private async Task<string> DiffAsync(string baseHead, CancellationToken ct)
    {
        var trackedDiff = await OutputAsync(["diff", baseHead, "--", .. ReviewContentFilter], ct);
        var parts = new List<string> { trackedDiff };
        foreach (var path in await UntrackedPathsAsync(ct))
            parts.Add(await UntrackedDiffAsync(path, ct));

        return string.Join('\n', parts.Where(part => part.Length > 0));
    }

    public Task<IReadOnlyList<string>> ChangedPathsAsync(CancellationToken ct) => ChangedPathsAsync("HEAD", ct);

    private async Task<IReadOnlyList<string>> ChangedPathsAsync(string baseHead, CancellationToken ct)
    {
        var output = await OutputAsync(["diff", baseHead, "--name-only", "--", .. ReviewContentFilter], ct);
        var tracked = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. tracked, .. await UntrackedPathsAsync(ct)];
    }

    private async Task<IReadOnlyList<string>> UntrackedPathsAsync(CancellationToken ct)
    {
        var output = await OutputAsync(["ls-files", "--others", "--exclude-standard", "--", .. ReviewContentFilter], ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task<bool> IsAncestorAsync(string baselineHead, CancellationToken ct)
    {
        var spec = new ProcessSpec("git", ["-C", _workspace, "merge-base", "--is-ancestor", baselineHead, "HEAD"],
                                   _workspace, string.Empty);
        try
        {
            await StreamingProcess.CollectAsync(spec, Timeout, ct);
            return true;
        }
        catch (VendorException error) when (error.ExitCode == 1)
        {
            return false;
        }
    }

    private async Task<string> ResolveBaselineAsync(string baselineHead, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baselineHead))
            throw new ReviewBaselineUnavailableException(baselineHead);

        try
        {
            var resolved = (await OutputAsync(["rev-parse", "--verify", $"{baselineHead}^{{commit}}"], ct)).Trim();
            return resolved.Length > 0 ? resolved : throw new ReviewBaselineUnavailableException(baselineHead);
        }
        catch (VendorException error)
        {
            throw new ReviewBaselineUnavailableException(baselineHead, error);
        }
    }

    /// <summary>
    /// Renders one untracked file as a new-file diff without touching the index. <c>--no-index</c>
    /// exits 1 when the sides differ, which against the null blob is the expected outcome rather
    /// than a failure; git treats the literal name <c>/dev/null</c> as that blob on every platform.
    /// </summary>
    private async Task<string> UntrackedDiffAsync(string path, CancellationToken ct)
    {
        var spec = new ProcessSpec("git", ["-C", _workspace, "diff", "--no-index", "--", "/dev/null", path],
                                   _workspace, string.Empty);
        var lines = new List<string>();
        try
        {
            await foreach (var line in StreamingProcess.RunAsync(spec, Timeout, ct).ConfigureAwait(false))
                lines.Add(line);
        }
        catch (VendorException error) when (error.ExitCode == 1)
        {
        }

        return string.Join('\n', lines);
    }
}

internal sealed class ReviewBaselineUnavailableException(string baselineHead, Exception? inner = null)
    : Exception($"code-review baseline '{baselineHead}' does not resolve to a commit; begin a new run", inner);

/// <summary>
/// The working tree as it stood at <c>forge.begin</c>. This is the whole of what replaces the
/// protection native plan mode used to give: edits during the interview are visible at approval,
/// except changes to documentation the interview is allowed to write. See docs/adr/0002 and
/// docs/adr/0004.
/// </summary>
/// <remarks>
/// The window is the working tree against <c>HEAD</c> plus untracked files rendered as new-file
/// diffs — staged, unstaged and brand-new alike, composed without touching the index. This stays
/// <c>HEAD</c>-relative because it measures interview drift; code review instead reads a
/// <see cref="ReviewWindow"/> from this baseline's commit. An empty new file renders no hunk, so it
/// remains invisible to both.
/// </remarks>
internal sealed record Baseline(string Head, string Diff)
{
    public static async Task<Baseline> CaptureAsync(GitClient git, CancellationToken ct)
    {
        var head = (await git.OutputAsync(["rev-parse", "HEAD"], ct)).Trim();
        var diff = await git.DiffAsync(ct);
        return new Baseline(head, diff);
    }

    /// <summary>Files touched since this baseline was taken, empty when the tree is unchanged.</summary>
    public async Task<IReadOnlyList<string>> DriftedFilesAsync(GitClient git, CancellationToken ct)
    {
        var current = await git.DiffAsync(ct);
        if (string.Equals(current, Diff, StringComparison.Ordinal)) return [];

        return await git.ChangedPathsAsync(ct);
    }
}
