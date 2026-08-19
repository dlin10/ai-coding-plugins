using PlanForge.Infrastructure;
using PlanForge.Vendors;

namespace PlanForge.Repo;

internal interface IReviewGit
{
    Task<string> DiffAsync(IReadOnlyList<string> pathspec, CancellationToken ct);

    Task<IReadOnlyList<string>> ChangedPathsAsync(IReadOnlyList<string> pathspec, CancellationToken ct);
}

internal sealed class GitClient : IReviewGit
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly string _workspace;

    public GitClient(string workspace) => _workspace = workspace;

    public async Task<string> OutputAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var spec = new ProcessSpec("git", ["-C", _workspace, .. arguments], _workspace, string.Empty);
        var lines = await StreamingProcess.CollectAsync(spec, Timeout, ct);
        return string.Join('\n', lines);
    }

    public async Task<string> DiffAsync(IReadOnlyList<string> pathspec, CancellationToken ct)
    {
        var parts = new List<string> { await OutputAsync(["diff", "HEAD", "--", .. pathspec], ct) };
        foreach (var path in await UntrackedPathsAsync(pathspec, ct))
            parts.Add(await UntrackedDiffAsync(path, ct));

        return string.Join('\n', parts.Where(part => part.Length > 0));
    }

    public async Task<IReadOnlyList<string>> ChangedPathsAsync(IReadOnlyList<string> pathspec,
                                                               CancellationToken ct)
    {
        var output = await OutputAsync(["diff", "HEAD", "--name-only", "--", .. pathspec], ct);
        var tracked = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. tracked, .. await UntrackedPathsAsync(pathspec, ct)];
    }

    private async Task<IReadOnlyList<string>> UntrackedPathsAsync(IReadOnlyList<string> pathspec,
                                                                  CancellationToken ct)
    {
        var output = await OutputAsync(["ls-files", "--others", "--exclude-standard", "--", .. pathspec], ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

/// <summary>
/// The working tree as it stood at <c>forge.begin</c>. This is the whole of what replaces the
/// protection native plan mode used to give: edits during the interview are visible at approval,
/// except changes to documentation the interview is allowed to write. See docs/adr/0002 and
/// docs/adr/0004.
/// </summary>
/// <remarks>
/// The window is the working tree against <c>HEAD</c> plus untracked files rendered as new-file
/// diffs — staged, unstaged and brand-new alike, composed without touching the index. Two limits
/// remain, recorded rather than fixed: a commit made mid-run moves <c>HEAD</c> and takes its
/// changes out of the window, and an empty new file renders no hunk, so it stays invisible.
/// </remarks>
internal sealed record Baseline(string Head, string Diff)
{
    public static async Task<Baseline> CaptureAsync(GitClient git, CancellationToken ct)
    {
        var head = (await git.OutputAsync(["rev-parse", "HEAD"], ct)).Trim();
        var diff = await git.DiffAsync(GitPathspec.WithoutDocumentation, ct);
        return new Baseline(head, diff);
    }

    /// <summary>Files touched since this baseline was taken, empty when the tree is unchanged.</summary>
    public async Task<IReadOnlyList<string>> DriftedFilesAsync(GitClient git, CancellationToken ct)
    {
        var current = await git.DiffAsync(GitPathspec.WithoutDocumentation, ct);
        if (string.Equals(current, Diff, StringComparison.Ordinal)) return [];

        return await git.ChangedPathsAsync(GitPathspec.WithoutDocumentation, ct);
    }
}
