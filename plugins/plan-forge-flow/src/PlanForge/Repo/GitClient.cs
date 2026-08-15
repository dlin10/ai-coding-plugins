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
}

/// <summary>
/// The working tree as it stood at <c>forge.begin</c>. This is the whole of what replaces the
/// protection native plan mode used to give: it does not prevent edits during the interview, it
/// makes them visible at approval. See docs/adr/0002.
/// </summary>
internal sealed record Baseline(string Head, string Diff)
{
    public static async Task<Baseline> CaptureAsync(GitClient git, CancellationToken ct)
    {
        var head = (await git.OutputAsync(["rev-parse", "HEAD"], ct)).Trim();
        var diff = await git.OutputAsync(["diff"], ct);
        return new Baseline(head, diff);
    }

    /// <summary>Files touched since this baseline was taken, empty when the tree is unchanged.</summary>
    public async Task<IReadOnlyList<string>> DriftedFilesAsync(GitClient git, CancellationToken ct)
    {
        var current = await git.OutputAsync(["diff"], ct);
        if (string.Equals(current, Diff, StringComparison.Ordinal)) return [];

        var names = await git.OutputAsync(["diff", "--name-only"], ct);
        return names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
