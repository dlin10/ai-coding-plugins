using PlanForge.Diagnostics;
using PlanForge.Repo;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// One builder turn, with the files it had already written recorded when the turn does not come
/// back.
/// </summary>
/// <remarks>
/// <para>
/// A builder writes files and then reports what it wrote, in that order, so a turn that dies while
/// reporting leaves work on disk that no caller hears about. In run 20260902-224201-7bf03b two
/// <c>forge.review.fix</c> calls died that way and the orchestrator read a failed act as an act
/// that changed nothing — wrong by most of five findings, and caught only later by inspection.
/// </para>
/// <para>
/// Git replaces the lost report: the tree as it stood when the turn started, differenced against
/// what the failure left behind. The whole diff rather than the file list, because a fix round
/// edits files earlier rounds already changed and a name-only comparison calls that no change at
/// all. The cost is one <c>git diff HEAD</c> per turn on the way in.
/// </para>
/// <para>
/// Neither git call may decide whether the turn runs or how it ends: this exists to explain a
/// failure, and something that can cause one is worse than nothing. A workspace git cannot read
/// costs the file list and nothing else.
/// </para>
/// <para>
/// A host that takes the call away is the same loss wearing different clothes, and used to be
/// excluded here on the grounds that cancellation has no report to replace. It has no report —
/// but it has a tree, which is what run 20260917-111319-20e672 proved when an hour of finished
/// work went unrecorded because the host's clock ran out four seconds after the builder got a
/// hung command back. The cancellation still travels, because the call really is gone; it now
/// carries the file list, so the act above can write the round down before it goes.
/// </para>
/// </remarks>
internal static class BuilderTurn
{
    private const string Source = "builder";

    // The turn's own token is dead by the time a cut-short salvage runs, so the salvage gets a
    // fresh one. Long enough for two git calls on a large tree, short enough that a git that hangs
    // cannot hold the call open past the deadline that already expired.
    private static readonly TimeSpan _salvageBound = TimeSpan.FromSeconds(30);

    public static async Task<BuildResult> RunAsync(IVendorSession session,
                                                    string workspaceRoot,
                                                    string prompt,
                                                    CancellationToken ct)
    {
        var git = new GitClient(workspaceRoot);
        var before = await TreeAsync(git, ct);

        try
        {
            return await session.RunAsync(prompt, Schemas.BuildResult, ct);
        }
        // The host taking the call away, rather than the builder failing. Nothing here decides
        // otherwise — the cancellation travels on as itself — but it leaves with the file list.
        catch (OperationCanceledException cancelled)
        {
            var written = await SalvageAsync(git, before);
            RunLog.Current?.Write("warn", Source, "builder.cut-short",
                ("filesWritten", before is null ? "unknown" : written.Count.ToString()),
                ("files", written.Count == 0 ? null : string.Join(", ", written)));

            throw new TurnCutShortException(written, cancelled);
        }
        catch (Exception error)
        {
            var written = before is null ? [] : await WrittenAsync(git, before, ct);
            RunLog.Current?.Write("error", Source, "builder.failed",
                ("error", error.Message),
                ("filesWritten", before is null ? "unknown" : written.Count.ToString()),
                ("files", written.Count == 0 ? null : string.Join(", ", written)));

            // Wrapped rather than rethrown because the SDK replaces the message of any exception
            // this assembly did not declare with a generic one, and a bare "An error occurred
            // invoking 'forge.review.fix'" sent the orchestrator to the run log to learn anything
            // at all. See ToolErrors.
            throw new VendorException(Describe(error, before, written), inner: error);
        }
    }

    private static async Task<Baseline?> TreeAsync(GitClient git, CancellationToken ct)
    {
        try
        {
            return await Baseline.CaptureAsync(git, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RunLog.Current?.Write("warn", Source, "builder.tree-unreadable", ("error", error.Message));
            return null;
        }
    }

    private static async Task<IReadOnlyList<string>> WrittenAsync(GitClient git,
                                                                   Baseline before,
                                                                   CancellationToken ct)
    {
        try
        {
            return await before.DriftedFilesAsync(git, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RunLog.Current?.Write("warn", Source, "builder.tree-unreadable", ("error", error.Message));
            return [];
        }
    }

    /// <summary>
    /// The file list for a turn the host cut short. Total rather than selective in what it
    /// swallows: a cancellation is already on its way out, and nothing read for the record may
    /// replace it — least of all the salvage's own deadline expiring.
    /// </summary>
    private static async Task<IReadOnlyList<string>> SalvageAsync(GitClient git, Baseline? before)
    {
        if (before is null) return [];

        try
        {
            using var salvage = new CancellationTokenSource(_salvageBound);
            return await before.DriftedFilesAsync(git, salvage.Token);
        }
        catch (Exception error)
        {
            RunLog.Current?.Write("warn", Source, "builder.tree-unreadable", ("error", error.Message));
            return [];
        }
    }

    private static string Describe(Exception error, Baseline? before, IReadOnlyList<string> written) =>
        before is null ? $"the builder failed, and whether it had already written anything could not be read: {error.Message}"
        : written.Count == 0 ? $"the builder failed and left the working tree as it found it: {error.Message}"
        : $"the builder failed after writing {written.Count} file(s), which are still on disk "
          + $"({string.Join(", ", written)}): {error.Message}";
}

/// <summary>
/// The host took the call away before the builder answered, carrying what the builder had already
/// written so the act above can record the turn on its way out.
/// </summary>
/// <remarks>
/// Still an <see cref="OperationCanceledException"/>, because everything above it reads
/// cancellation as its own thing rather than as a failure: <c>ForgeTools.LoggedAsync</c> logs
/// <c>tool.cancelled</c> and not <c>tool.failed</c>, and the SDK answers the host's own
/// cancellation instead of inventing an error the host never waited for.
/// </remarks>
internal sealed class TurnCutShortException(IReadOnlyList<string> filesWritten, OperationCanceledException cancelled)
    : OperationCanceledException(Describe(filesWritten), cancelled, cancelled.CancellationToken)
{
    /// <summary>What the builder had written when the call was taken away, and still on disk.</summary>
    public IReadOnlyList<string> FilesWritten { get; } = filesWritten;

    private static string Describe(IReadOnlyList<string> filesWritten) =>
        filesWritten.Count == 0
            ? "the host took the call away before the builder answered, and it had written nothing the working tree can show"
            : $"the host took the call away before the builder answered, after it had written {filesWritten.Count} "
              + $"file(s), which are still on disk ({string.Join(", ", filesWritten)})";
}
