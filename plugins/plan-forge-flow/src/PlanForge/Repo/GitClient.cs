using PlanForge.Infrastructure;

namespace PlanForge.Repo;

internal sealed class GitClient
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

    public Task<string> DiffAsync(IReadOnlyList<string> pathspec, CancellationToken ct) =>
        OutputAsync(["diff", "--", .. pathspec], ct);

    public async Task<IReadOnlyList<string>> ChangedPathsAsync(IReadOnlyList<string> pathspec,
                                                               CancellationToken ct)
    {
        var output = await OutputAsync(["diff", "--name-only", "--", .. pathspec], ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>
/// The working tree as it stood at <c>forge.begin</c>. This is the whole of what replaces the
/// protection native plan mode used to give: edits during the interview are visible at approval,
/// except changes to documentation the interview is allowed to write. See docs/adr/0002 and
/// docs/adr/0004.
/// </summary>
/// <remarks>
/// <c>git diff</c> lists neither untracked nor staged files, so a brand-new ADR was invisible here
/// long before the documentation exclusions arrived. That limit is recorded rather than fixed;
/// widening the window is its own change, with its own review.
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
