using System.Text;
using PlanForge.Infrastructure;
using PlanForge.Vendors;

namespace PlanForge.Repo;

/// <summary>
/// The final code state handed to a Critic, with the identity of the Git range that produced both
/// its changed paths and its content diff. See docs/adr/0016.
/// </summary>
/// <param name="BaselineHead">The run's baseline commit, resolved.</param>
/// <param name="BaseHead">The commit the window starts from: the baseline, or <c>HEAD</c> when it fell back.</param>
/// <param name="IsFallback">Whether the baseline is no longer an ancestor of <c>HEAD</c>.</param>
/// <param name="ChangedPaths">Every path in the window, tracked and untracked.</param>
/// <param name="Files">The content diff, one block per file in git's order, empty blocks left out.</param>
internal sealed record ReviewWindow(string BaselineHead,
                                    string BaseHead,
                                    bool IsFallback,
                                    IReadOnlyList<string> ChangedPaths,
                                    IReadOnlyList<ReviewFile> Files)
{
    /// <summary>The whole content diff, as one text.</summary>
    public string Diff => string.Join('\n', Files.Select(file => file.Diff));
}

/// <summary>One file's block of a review window's content diff.</summary>
/// <param name="Path">The post-image path.</param>
/// <param name="Diff">The block, header included, rendered as a new-file diff when untracked.</param>
/// <param name="Untracked">Whether git does not track the file, so its whole content is the block.</param>
internal sealed record ReviewFile(string Path, string Diff, bool Untracked);

internal interface IReviewGit
{
    /// <summary>Resolves the run baseline or its disclosed fallback, then reads that one window.</summary>
    /// <param name="baselineHead">The commit recorded by <c>forge.begin</c>.</param>
    /// <param name="excludedPaths">Paths this round leaves out of the window, beside the documentation.</param>
    /// <param name="ct">Cancels the git reads.</param>
    Task<ReviewWindow> ReadReviewWindowAsync(string baselineHead, IReadOnlyList<string> excludedPaths, CancellationToken ct);
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

    public async Task<string> DiffAsync(CancellationToken ct) =>
        string.Join('\n', (await FilesAsync("HEAD", ReviewContentFilter, string.Empty, ct)).Select(file => file.Diff));

    public async Task<ReviewWindow> ReadReviewWindowAsync(string baselineHead,
                                                          IReadOnlyList<string> excludedPaths,
                                                          CancellationToken ct)
    {
        // A round's own exclusions join the documentation's, so the paths the sensitive-path guard
        // reads and the content sent stay one set. See docs/adr/0029.
        string[] filter = [.. ReviewContentFilter, .. excludedPaths.Select(path => $":(exclude){path}")];
        var resolvedBaseline = await ResolveBaselineAsync(baselineHead, ct);
        var isFallback = !await IsAncestorAsync(resolvedBaseline, ct);
        var baseHead = isFallback ? (await OutputAsync(["rev-parse", "HEAD"], ct)).Trim() : resolvedBaseline;
        var changedPaths = await ChangedPathsAsync(baseHead, filter, ct);
        var prefix = (await OutputAsync(["rev-parse", "--show-prefix"], ct)).Trim();
        var files = await FilesAsync(baseHead, filter, prefix, ct);
        return new ReviewWindow(resolvedBaseline, baseHead, isFallback, changedPaths, files);
    }

    /// <summary>The content diff, one block per file: tracked files in git's order, then untracked ones.</summary>
    /// <param name="baseHead">The commit the diff starts from.</param>
    /// <param name="filter">The pathspec both reads take.</param>
    /// <param name="prefix">
    /// The workspace's path inside the repository, which git's diff headers carry and a pathspec
    /// does not: a tracked file is named without it, so a name the window gives is one
    /// <c>excludePaths</c> takes, as an untracked file's already is.
    /// </param>
    /// <param name="ct">Cancels the git reads.</param>
    private async Task<IReadOnlyList<ReviewFile>> FilesAsync(string baseHead, string[] filter, string prefix, CancellationToken ct)
    {
        var files = TrackedFiles(await OutputAsync(["diff", baseHead, "--", .. filter], ct), prefix).ToList();
        foreach (var path in await UntrackedPathsAsync(filter, ct))
        {
            var diff = await UntrackedDiffAsync(path, ct);
            if (diff.Length > 0) files.Add(new ReviewFile(path, diff, Untracked: true));
        }

        return files;
    }

    /// <summary>
    /// A tracked diff cut into one block per file at each <c>diff --git</c> header. The cut is exact
    /// because git prefixes every content line of a hunk, so no line of a file's content can open a
    /// line of the diff with that header.
    /// </summary>
    /// <param name="diff">The output of one <c>git diff</c>.</param>
    /// <param name="prefix">The workspace's path inside the repository, taken off each file's name.</param>
    private static IEnumerable<ReviewFile> TrackedFiles(string diff, string prefix)
    {
        if (diff.Length == 0) yield break;

        var lines = diff.Split('\n');
        var start = 0;
        for (var index = 1; index <= lines.Length; index++)
        {
            if (index < lines.Length && !lines[index].StartsWith("diff --git ", StringComparison.Ordinal)) continue;

            var path = Baseline.HeaderPath(lines[start]);
            if (path.StartsWith(prefix, StringComparison.Ordinal)) path = path[prefix.Length..];
            yield return new ReviewFile(path, string.Join('\n', lines[start..index]), Untracked: false);
            start = index;
        }
    }

    public Task<IReadOnlyList<string>> ChangedPathsAsync(CancellationToken ct) => ChangedPathsAsync("HEAD", ReviewContentFilter, ct);

    private async Task<IReadOnlyList<string>> ChangedPathsAsync(string baseHead, string[] filter, CancellationToken ct)
    {
        var output = await OutputAsync(["diff", baseHead, "--name-only", "--", .. filter], ct);
        var tracked = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. tracked, .. await UntrackedPathsAsync(filter, ct)];
    }

    private async Task<IReadOnlyList<string>> UntrackedPathsAsync(string[] filter, CancellationToken ct)
    {
        var output = await OutputAsync(["ls-files", "--others", "--exclude-standard", "--", .. filter], ct);
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
    /// <remarks>
    /// Per file rather than per tree. The name list git can produce answers a different question -
    /// what differs from <c>HEAD</c> - and where a baseline is taken mid-run that is most of the
    /// work of every earlier turn: run 20260919-160244-77fd30 reported a failed builder turn as
    /// having written 92 files, which was the whole run's drift and not that turn's, in the one
    /// message anybody had left to read. So the two diffs are compared block by block, and a file
    /// changed back to the state the baseline found it in is not drift.
    /// </remarks>
    public async Task<IReadOnlyList<string>> DriftedFilesAsync(GitClient git, CancellationToken ct)
    {
        var current = await git.DiffAsync(ct);
        if (string.Equals(current, Diff, StringComparison.Ordinal)) return [];

        var before = Blocks(Diff);
        var after = Blocks(current);
        var drifted = after.Where(file => !before.TryGetValue(file.Key, out var was)
                                          || !string.Equals(was, file.Value, StringComparison.Ordinal))
                           .Select(file => file.Key)
                           .Concat(before.Keys.Where(path => !after.ContainsKey(path)))
                           .Distinct(StringComparer.Ordinal)
                           .OrderBy(path => path, StringComparer.Ordinal)
                           .ToList();

        // The two diffs differ and no file block accounts for it: a diff shape this parse does not
        // know, and a wide answer is worth more here than an empty one.
        return drifted.Count > 0 ? drifted : await git.ChangedPathsAsync(ct);
    }

    /// <summary>
    /// A composed diff split into one block per file, keyed by the post-image path. Untracked files
    /// key the same way as tracked ones: git rewrites the <c>/dev/null</c> side of a
    /// <c>--no-index</c> comparison to the real name, so both sides of the header carry it.
    /// </summary>
    /// <param name="diff">The diff as <see cref="GitClient.DiffAsync(CancellationToken)"/> composes it.</param>
    private static Dictionary<string, string> Blocks(string diff)
    {
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        string? path = null;
        var body = new StringBuilder();

        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Close(blocks, path, body);
                path = HeaderPath(line);
                continue;
            }

            if (path is not null) body.Append(line).Append('\n');
        }

        Close(blocks, path, body);
        return blocks;
    }

    /// <summary>
    /// The post-image path of a <c>diff --git a/x b/x</c> header. Read from the <c>b/</c> side, and
    /// from its last occurrence, because a path may contain the separator itself; a path containing
    /// a space is ambiguous in this header and is left to the comparison to treat as one file.
    /// </summary>
    /// <param name="header">The header line.</param>
    internal static string HeaderPath(string header)
    {
        var marker = header.LastIndexOf(" b/", StringComparison.Ordinal);
        return marker < 0 ? header : header[(marker + 3)..];
    }

    private static void Close(Dictionary<string, string> blocks, string? path, StringBuilder body)
    {
        if (path is not null) blocks[path] = body.ToString();
        body.Clear();
    }
}
