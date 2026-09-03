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
/// </remarks>
internal static class BuilderTurn
{
    private const string Source = "builder";

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
        // Cancellation is the host taking the call away rather than the builder failing, and it
        // has no report to replace, so it flows on untouched.
        catch (Exception error) when (error is not OperationCanceledException)
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

    private static string Describe(Exception error, Baseline? before, IReadOnlyList<string> written) =>
        before is null ? $"the builder failed, and whether it had already written anything could not be read: {error.Message}"
        : written.Count == 0 ? $"the builder failed and left the working tree as it found it: {error.Message}"
        : $"the builder failed after writing {written.Count} file(s), which are still on disk "
          + $"({string.Join(", ", written)}): {error.Message}";
}
