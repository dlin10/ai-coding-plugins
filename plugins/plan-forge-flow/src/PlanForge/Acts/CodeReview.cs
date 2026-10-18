using System.Globalization;
using System.Text;
using PlanForge.Infrastructure;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// One round of code review. The loop deliberately no longer lives inside this call: when the
/// critic asks for work the approved plan excluded, only the orchestrator can arbitrate, because
/// it alone holds the interview context that settled the scope. Handing findings to the builder
/// is <see cref="ReviewFix"/>, called by the orchestrator after it has filtered them.
/// </summary>
internal sealed class CodeReview(IVendor vendor, PromptLibrary prompts, IReviewGit git)
{
    /// <summary>Set when the round's Fast turn was served at standard speed for part of it.</summary>
    internal string? SpeedWarning { get; private set; }

    /// <summary>Runs one critic round over the review window and records it.</summary>
    /// <param name="run">The run whose approved plan the round judges.</param>
    /// <param name="selection">The critic's model, effort and speed.</param>
    /// <param name="userGrantedRound">
    /// The orchestrator's assertion that it showed the user where the run stands, asked, and was
    /// told yes — the same kind of assertion <c>approved</c> carries on <c>forge.plan.confirm</c>,
    /// and, per docs/adr/0003, one no code here can check.
    /// </param>
    /// <param name="ct">Cancels the round.</param>
    /// <param name="scope">The round's exclusions; null for the whole window.</param>
    public async Task<Critique> ReviewAsync(RunDirectory run,
                                            Selection selection,
                                            bool userGrantedRound,
                                            CancellationToken ct,
                                            ReviewScope? scope = null)
    {
        scope ??= ReviewScope.Whole;
        var state = run.ReadState();
        RequireReady(run, state);
        if (state.CodeReviewRounds >= state.CodeReviewRoundCap && !userGrantedRound)
            throw new CodeReviewCapReachedException(state.CodeReviewRounds, state.CodeReviewRoundCap);
        var granted = state.CodeReviewRounds >= state.CodeReviewRoundCap;
        var ledger = run.ReadDecisionLedger();

        var window = await git.ReadReviewWindowAsync(state.BaselineHead, scope.ExcludedPaths, ct);
        GuardChangedPaths(window);
        if (window.Diff.Length == 0)
        {
            var empty = WithPending(WithReviewWindow(new Critique("approve", [], "nothing to review"), window, scope, null), state, ledger);
            run.AppendFlowCritique("Code review", null, empty);
            return empty;
        }

        var plan = run.ReadPlan();
        var projection = ledger.RenderProjection(LedgerPhase.CodeReview);
        var round = state.CodeReviewRounds + 1;
        string DiffPath(ReviewFile file) => run.ReviewDiffPath(round, file.Path);

        // The embedded prompt carries everything any delivery hands over, so guarding it covers the
        // files a critic is later pointed to: its own read sends them to its vendor all the same.
        var review = ComposeReview(plan, window, projection, state, scope, Delivery.Embedded, DiffPath);
        SensitiveInput.Guard(review, "the diff under review");

        // Refused here rather than by the vendor, which fails the turn after it has started and says
        // no more than its stderr: in run 20261004-093111-b79938 that was all a granted round left.
        // Before refusing, the window travels by path, untracked files first. See docs/adr/0034.
        var delivery = Delivery.Embedded;
        string? byPath = null;
        if (vendor.PromptCharacterLimit is { } limit && Characters(review) > limit)
        {
            var embeddedSize = Characters(review);
            foreach (var next in (Delivery[])[Delivery.UntrackedByPath, Delivery.AllByPath])
            {
                delivery = next;
                review = ComposeReview(plan, window, projection, state, scope, delivery, DiffPath);
                if (Characters(review) <= limit) break;
            }

            var size = Characters(review);
            if (size > limit)
            {
                var error = new ReviewPromptTooLargeException(
                    Oversize(vendor.Id, size, limit, granted, Characters(plan), Characters(projection), window.Files.Count));
                run.AppendFlowRoundNotSent("Code review", round, error.Message);
                throw error;
            }

            byPath = ByPathSummary(vendor.Id, embeddedSize, limit, window, delivery);
        }

        Critique critique;
        IReadOnlyList<string> killed;
        var diffs = run.ReviewDiffsPath(round);
        if (delivery == Delivery.AllByPath)
            WriteDiffs(diffs, window, DiffPath);
        try
        {
            // Fresh critic each round, but handed the current ledger projection so it converges without
            // inheriting old critique or Flow history.
            await using var critic = await vendor.StartAsync(new RoleSpec(VendorRole.Critic, prompts.LoadCodeReviewCritic(vendor.Id),
                                                                          WorkerTools: WorkerTools.Effective(state.WorkerTools),
                                                                          Telemetry: new WorkerTelemetryContext(run.TelemetryPath, run.Log,
                                                                                                                "code_review", Round: round)),
                                                             selection, resumeToken: null, ct);
            VendorCritique wireCritique;
            try
            {
                wireCritique = await critic.RunAsync(review, Schemas.Critique, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                run.AppendFlowCritiqueRejected("Code review", round, null, error.Message);
                throw;
            }

            try
            {
                critique = WithPending(WithReviewWindow(ledger.IngestCritique(wireCritique, LedgerPhase.CodeReview), window, scope, byPath),
                                       state, ledger);
            }
            catch (DecisionLedgerCritiqueException error)
            {
                run.AppendFlowCritiqueRejected("Code review", round, wireCritique, error.Message);
                throw;
            }
            killed = critic.KilledBackgroundTasks;
            SpeedWarning = critic.SpeedWarning;
        }
        finally
        {
            if (delivery == Delivery.AllByPath)
                DeleteDiffs(run, diffs);
        }

        if (granted) run.AppendFlowGrantedRound("Code review", round);
        run.AppendFlowCritique("Code review", round, critique);
        run.AppendFlowKilledTasks("Code review", round, killed);
        run.WriteState(granted
            ? state with { CodeReviewRounds = round, CodeReviewRoundCap = state.CodeReviewRoundCap + 1,
                           GrantedCodeReviewRounds = state.GrantedCodeReviewRounds + 1 }
            : state with { CodeReviewRounds = round });
        return critique;
    }

    internal static void RequireReady(RunDirectory run, RunState state)
    {
        if (!state.Approved) throw new NotApprovedException(run.RunId);
        FixGatePolicy.RequireReviewReady(state.FullGate, run.ReadDecisionLedger().Summary.PendingFullGateFindingIds.Count > 0);
    }

    private static Critique WithPending(Critique critique, RunState state, DecisionLedger ledger) =>
        critique with { PendingFullGateFindingIds = state.FullGate == FixGatePolicy.Final
            ? ledger.Summary.PendingFullGateFindingIds : null };

    /// <summary>
    /// The guard covers exactly the set of paths whose contents are sent, which is why it takes the
    /// same pathspec as the diff. A sensitive <em>name</em> under an excluded path — an ADR called
    /// <c>0005-token-rotation.md</c>, say — is not a leak, because that file's contents never reach
    /// a vendor, and aborting the run over it would refuse a legitimate name for no gain.
    /// </summary>
    private static void GuardChangedPaths(ReviewWindow window)
    {
        foreach (var path in window.ChangedPaths)
        {
            if (SensitiveInput.IsSensitivePath(path))
                throw new SensitiveContentException($"the diff touches {path}, which");
        }
    }

    /// <summary>
    /// The two instruction blocks end the prompt in the order they escalate: what the builder was
    /// told, which is data, and then what this critic was told, which is addressed to it. The
    /// builder's text is whatever is set now — a user who changes it mid-run can have the critic
    /// told about instructions the running builder never received, which the flow log records and
    /// docs/adr/0019 accepts.
    /// </summary>
    /// <param name="plan">The approved plan.</param>
    /// <param name="window">The review window.</param>
    /// <param name="ledgerProjection">The code-review projection of the decision ledger.</param>
    /// <param name="state">The run state, for the instructions it carries.</param>
    /// <param name="scope">What the round leaves out.</param>
    /// <param name="delivery">Which of the window's files are given by path instead of embedded.</param>
    /// <param name="diffPath">Where a tracked file's diff is written when it is given by path.</param>
    private static string ComposeReview(string plan, ReviewWindow window, string ledgerProjection, RunState state,
                                        ReviewScope scope, Delivery delivery, Func<ReviewFile, string> diffPath)
    {
        var prompt = new StringBuilder().AppendLine("# Approved plan")
                                        .AppendLine()
                                        .AppendLine(plan)
                                        .AppendLine($"Full host verification timing: {state.FullGate}")
                                        .AppendLine()
                                        .AppendLine("# Review window")
                                        .AppendLine()
                                        .AppendLine($"Mode: {(window.IsFallback ? "HEAD fallback" : "run baseline")}")
                                        .AppendLine($"Base commit: {window.BaseHead}");

        if (window.IsFallback)
            prompt.AppendLine($"Run baseline: {window.BaselineHead}")
                  .AppendLine("Reason: the run baseline is not an ancestor of the current HEAD; "
                              + "committed run work may be outside this window.");

        prompt.AppendLine("Target: current working tree, including untracked files")
              .AppendLine("Excluded paths: every CONTEXT.md and docs/adr/** at any depth");

        if (scope.ExcludedPaths.Count > 0)
            prompt.AppendLine($"Also excluded from this round by the orchestrator: {Paths(scope.ExcludedPaths)}. "
                              + "Judge neither their changes nor the absence of their changes.");

        var embedded = window.Files.Where(file => !ByPath(file, delivery)).ToList();
        if (embedded.Count > 0)
            prompt.AppendLine()
                  .AppendLine("# Diff under review")
                  .AppendLine()
                  .AppendLine("```diff")
                  .AppendLine(string.Join('\n', embedded.Select(file => file.Diff)))
                  .AppendLine("```");

        var changed = window.Files.Where(file => ByPath(file, delivery) && !file.Untracked).ToList();
        if (changed.Count > 0)
        {
            prompt.AppendLine()
                  .AppendLine("# Changed files to read")
                  .AppendLine()
                  .AppendLine("These tracked files are part of the window, but their diffs are not in this prompt. Each "
                              + "file's diff against the base commit is saved in full at the path beside it: read every one "
                              + "with your own tools before you judge the window, as if this prompt carried it. Its hunks "
                              + "number lines as the working tree does.")
                  .AppendLine();
            foreach (var file in changed)
                prompt.AppendLine($"- `{file.Path}` (+{DiffLines(file.Diff, '+')} −{DiffLines(file.Diff, '-')}): `{diffPath(file)}`");
        }

        var untracked = window.Files.Where(file => ByPath(file, delivery) && file.Untracked).ToList();
        if (untracked.Count > 0)
        {
            prompt.AppendLine()
                  .AppendLine("# Untracked files to read")
                  .AppendLine()
                  .AppendLine("These files are new and part of the window, but their contents are not in this prompt. Read "
                              + "each one in full from the working tree with your own tools before you judge the window: "
                              + "every line of each is an addition.")
                  .AppendLine();
            foreach (var file in untracked)
                prompt.AppendLine($"- `{file.Path}` ({DiffLines(file.Diff, '+')} lines)");
        }

        if (ledgerProjection.Length > 0)
            prompt.AppendLine()
                  .AppendLine(ledgerProjection.TrimEnd())
                  .AppendLine()
                  .AppendLine();

        RunInstructions.AppendBuilderContext(prompt, state.BuilderInstructions);
        RunInstructions.Append(prompt, state.CriticInstructions);

        return prompt.ToString();
    }

    /// <summary>Appends the window the round judged to the critique's summary, and so to the flow log.</summary>
    /// <param name="critique">The critique to annotate.</param>
    /// <param name="window">The review window.</param>
    /// <param name="scope">What the round left out.</param>
    /// <param name="byPath">What was given to the critic by path and why, or null when everything was embedded.</param>
    private static Critique WithReviewWindow(Critique critique, ReviewWindow window, ReviewScope scope, string? byPath)
    {
        var summary = window.IsFallback
            ? $"Review window: HEAD fallback {Short(window.BaseHead)} to working tree; run baseline "
              + $"{Short(window.BaselineHead)} is not an ancestor, so committed run work may be outside this window."
            : $"Review window: run baseline {Short(window.BaseHead)} to working tree.";
        if (scope.ExcludedPaths.Count > 0)
            summary += $" Excluded from this round: {Paths(scope.ExcludedPaths)}.";
        if (byPath is not null)
            summary += $" {byPath}";
        return critique with { Summary = $"{critique.Summary}\n\n{summary}" };
    }

    /// <summary>Says what a round over its vendor's limit gave the critic by path, and why.</summary>
    /// <param name="vendorId">The critic's vendor.</param>
    /// <param name="embeddedSize">The prompt's size with every file embedded.</param>
    /// <param name="limit">The vendor's limit in characters.</param>
    /// <param name="window">The review window.</param>
    /// <param name="delivery">The delivery the round was sent with.</param>
    private static string ByPathSummary(string vendorId, int embeddedSize, int limit, ReviewWindow window, Delivery delivery)
    {
        var untracked = window.Files.Count(file => ByPath(file, delivery) && file.Untracked);
        var tracked = window.Files.Count(file => ByPath(file, delivery) && !file.Untracked);
        return $"Embedded, the window was {Count(embeddedSize)} characters and {vendorId} accepts at most {Count(limit)}, "
               + $"so the critic was given by path {untracked} untracked files"
               + (delivery == Delivery.AllByPath ? $" and the diffs of {tracked} tracked files." : ".");
    }

    private static string Short(string head) => head[..Math.Min(12, head.Length)];

    private static bool ByPath(ReviewFile file, Delivery delivery) =>
        delivery == Delivery.AllByPath || delivery == Delivery.UntrackedByPath && file.Untracked;

    private static string Paths(IEnumerable<string> paths) => string.Join(", ", paths.Select(path => $"`{path}`"));

    /// <summary>
    /// The lines a block adds or removes: those after its first hunk header that open with
    /// <paramref name="sign"/>, so the <c>---</c> and <c>+++</c> file headers are not counted.
    /// </summary>
    /// <param name="diff">One file's block.</param>
    /// <param name="sign"><c>+</c> for added lines, <c>-</c> for removed ones.</param>
    private static int DiffLines(string diff, char sign) =>
        diff.Split('\n').SkipWhile(line => !line.StartsWith("@@", StringComparison.Ordinal)).Count(line => line.StartsWith(sign));

    /// <summary>
    /// Writes each tracked file's block where the prompt says it is, into a folder emptied first:
    /// a round the server died in is retried under the same number.
    /// </summary>
    /// <param name="folder">The round's review folder.</param>
    /// <param name="window">The review window.</param>
    /// <param name="diffPath">Where a tracked file's diff goes.</param>
    private static void WriteDiffs(string folder, ReviewWindow window, Func<ReviewFile, string> diffPath)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        foreach (var file in window.Files.Where(file => !file.Untracked))
            AtomicFile.Write(diffPath(file), file.Diff);
    }

    /// <summary>
    /// Deletes the round's diffs. A folder that will not go is logged rather than thrown: the round
    /// itself is over, the folder ignores itself with the rest of <c>.forge/</c>, and a retry of the
    /// round empties it first.
    /// </summary>
    /// <param name="run">The run, for its log.</param>
    /// <param name="folder">The round's review folder.</param>
    private static void DeleteDiffs(RunDirectory run, string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            run.Log.Write("warn", "code_review", "review.diffs-left", ("folder", folder), ("error", error.Message));
        }
    }

    /// <summary>
    /// Unicode scalar values, which is what codex counts — Rust's <c>chars()</c> — so a surrogate
    /// pair is one character, where <see cref="string.Length"/> would count two.
    /// </summary>
    /// <param name="text">The text to measure.</param>
    internal static int Characters(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }

    /// <summary>
    /// Why the round was not sent, written for the orchestrator that has to choose what to narrow.
    /// It is measured with every file already given by path, so what is left is the plan, the
    /// ledger projection and the list of files, and only <c>excludePaths</c> can shorten the list.
    /// </summary>
    /// <param name="vendorId">The critic's vendor.</param>
    /// <param name="size">The prompt's size in characters, every file given by path.</param>
    /// <param name="limit">The vendor's limit in characters.</param>
    /// <param name="granted">Whether the round was one the user granted past the cap.</param>
    /// <param name="planSize">The approved plan's share.</param>
    /// <param name="projectionSize">The ledger projection's share.</param>
    /// <param name="fileCount">How many files the window lists.</param>
    private static string Oversize(string vendorId, int size, int limit, bool granted, int planSize, int projectionSize,
                                   int fileCount) =>
        new StringBuilder()
            .Append($"the code-review prompt is {Count(size)} characters with every changed file given to the critic by ")
            .Append($"path, and {vendorId} accepts at most {Count(limit)}, {Count(size - limit)} over. Nothing was sent ")
            .Append("and the round was not counted")
            .AppendLine(granted ? "; the user's grant for it still stands." : ".")
            .AppendLine()
            .AppendLine("What the prompt holds:")
            .AppendLine($"- approved plan: {Count(planSize)}")
            .AppendLine($"- ledger projection: {Count(projectionSize)}")
            .AppendLine($"- the list of {fileCount} changed files, and everything else: {Count(size - planSize - projectionSize)}")
            .AppendLine()
            .Append("To fit, start the round again with `excludePaths` naming what is not the run's work, such as run ")
            .Append("tooling or generated output. What the critic is handed is the user's call: show them these sizes and ")
            .Append("ask first.")
            .ToString();

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// How a round hands the window's files to the critic, in the order the server tries them when
    /// the embedded prompt is over the vendor's limit. See docs/adr/0034.
    /// </summary>
    private enum Delivery
    {
        /// <summary>Every file's block in the prompt.</summary>
        Embedded,

        /// <summary>Untracked files listed for the critic to read from the working tree.</summary>
        UntrackedByPath,

        /// <summary>Untracked files listed as above, and every tracked file's diff written to the run folder.</summary>
        AllByPath
    }
}

internal sealed class CodeReviewCapReachedException(int rounds, int cap)
    : Exception($"code review already ran {rounds} rounds, and the cap is {cap}. Ask the user "
                + "whether to run another round, and pass `userGrantedRound: true` if they say yes.");

/// <summary>A code-review prompt longer than the critic's vendor accepts, refused before it starts.</summary>
/// <param name="message">The overrun, what the prompt holds and how to narrow it.</param>
internal sealed class ReviewPromptTooLargeException(string message) : Exception(message);
