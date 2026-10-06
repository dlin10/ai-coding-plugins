using System.Globalization;
using System.Text;
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
    /// <param name="scope">The round's exclusions and how its untracked files are given; null for the whole window.</param>
    public async Task<Critique> ReviewAsync(RunDirectory run,
                                            Selection selection,
                                            bool userGrantedRound,
                                            CancellationToken ct,
                                            ReviewScope? scope = null)
    {
        scope ??= ReviewScope.Whole;
        var state = run.ReadState();
        if (!state.Approved) throw new NotApprovedException(run.RunId);
        if (state.CodeReviewRounds >= state.CodeReviewRoundCap && !userGrantedRound)
            throw new CodeReviewCapReachedException(state.CodeReviewRounds, state.CodeReviewRoundCap);
        var granted = state.CodeReviewRounds >= state.CodeReviewRoundCap;
        var ledger = run.ReadDecisionLedger();

        var window = await git.ReadReviewWindowAsync(state.BaselineHead, scope.ExcludedPaths, ct);
        GuardChangedPaths(window);
        if (window.Diff.Length == 0)
            return WithReviewWindow(new Critique("approve", [], "nothing to review"), window, scope);

        var plan = run.ReadPlan();
        var projection = ledger.RenderProjection(LedgerPhase.CodeReview);
        var review = ComposeReview(plan, window, projection, state, scope);
        SensitiveInput.Guard(review, "the diff under review");
        // A file the critic is pointed to leaves for the vendor all the same, read by its own tools.
        var referenced = window.Files.Where(file => ByReference(file, scope)).ToList();
        if (referenced.Count > 0)
            SensitiveInput.Guard(string.Join('\n', referenced.Select(file => file.Diff)), "an untracked file the critic is pointed to");
        var round = state.CodeReviewRounds + 1;

        // Refused here rather than by the vendor, which fails the turn after it has started and says
        // no more than its stderr: in run 20261004-093111-b79938 that was all a granted round left.
        var size = Characters(review);
        if (vendor.PromptCharacterLimit is { } limit && size > limit)
        {
            var error = new ReviewPromptTooLargeException(
                Oversize(vendor.Id, size, limit, granted, Characters(plan), Characters(projection), window, scope));
            run.AppendFlowRoundNotSent("Code review", round, error.Message);
            throw error;
        }

        Critique critique;
        IReadOnlyList<string> killed;
        // Fresh critic each round, but handed the current ledger projection so it converges without
        // inheriting old critique or Flow history.
        await using (var critic = await vendor.StartAsync(new RoleSpec(VendorRole.Critic, prompts.LoadCodeReviewCritic(vendor.Id),
                                                                       WorkerTools: WorkerTools.Effective(state.WorkerTools),
                                                                       Telemetry: new WorkerTelemetryContext(run.TelemetryPath, run.Log,
                                                                                                             "code_review", Round: round)),
                                                          selection, resumeToken: null, ct))
        {
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
                critique = WithReviewWindow(ledger.IngestCritique(wireCritique, LedgerPhase.CodeReview), window, scope);
            }
            catch (DecisionLedgerCritiqueException error)
            {
                run.AppendFlowCritiqueRejected("Code review", round, wireCritique, error.Message);
                throw;
            }
            killed = critic.KilledBackgroundTasks;
            SpeedWarning = critic.SpeedWarning;
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
    /// <param name="scope">What the round leaves out, and whether untracked files are given by path.</param>
    private static string ComposeReview(string plan, ReviewWindow window, string ledgerProjection, RunState state,
                                        ReviewScope scope)
    {
        var prompt = new StringBuilder().AppendLine("# Approved plan")
                                        .AppendLine()
                                        .AppendLine(plan)
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

        prompt.AppendLine()
              .AppendLine("# Diff under review")
              .AppendLine()
              .AppendLine("```diff")
              .AppendLine(string.Join('\n', window.Files.Where(file => !ByReference(file, scope)).Select(file => file.Diff)))
              .AppendLine("```");

        var referenced = window.Files.Where(file => ByReference(file, scope)).ToList();
        if (referenced.Count > 0)
        {
            prompt.AppendLine()
                  .AppendLine("# Untracked files to read")
                  .AppendLine()
                  .AppendLine("These files are new and part of the window, but their contents are not in this prompt. Read "
                              + "each one in full from the working tree with your own tools before you judge the window: "
                              + "every line of each is an addition, as if the diff above showed it.")
                  .AppendLine();
            foreach (var file in referenced)
                prompt.AppendLine($"- `{file.Path}` ({AddedLines(file.Diff)} lines)");
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

    private static Critique WithReviewWindow(Critique critique, ReviewWindow window, ReviewScope scope)
    {
        var summary = window.IsFallback
            ? $"Review window: HEAD fallback {Short(window.BaseHead)} to working tree; run baseline "
              + $"{Short(window.BaselineHead)} is not an ancestor, so committed run work may be outside this window."
            : $"Review window: run baseline {Short(window.BaseHead)} to working tree.";
        if (scope.ExcludedPaths.Count > 0)
            summary += $" Excluded from this round: {Paths(scope.ExcludedPaths)}.";
        var referenced = window.Files.Count(file => ByReference(file, scope));
        if (referenced > 0)
            summary += $" Untracked files given to the critic by path: {referenced}.";
        return critique with { Summary = $"{critique.Summary}\n\n{summary}" };
    }

    private static string Short(string head) => head[..Math.Min(12, head.Length)];

    private static bool ByReference(ReviewFile file, ReviewScope scope) => scope.UntrackedByReference && file.Untracked;

    private static string Paths(IEnumerable<string> paths) => string.Join(", ", paths.Select(path => $"`{path}`"));

    /// <summary>The lines a new-file block adds: those after its hunk header, all of them additions.</summary>
    /// <param name="diff">An untracked file's block.</param>
    private static int AddedLines(string diff) =>
        diff.Split('\n').SkipWhile(line => !line.StartsWith("@@", StringComparison.Ordinal)).Count(line => line.StartsWith('+'));

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
    /// Why the round was not sent, written for the orchestrator that has to choose what to narrow:
    /// the overrun, where the characters are, and the largest files, so it can tell run tooling and
    /// generated output from the run's work without reading the diff.
    /// </summary>
    /// <param name="vendorId">The critic's vendor.</param>
    /// <param name="size">The prompt's size in characters.</param>
    /// <param name="limit">The vendor's limit in characters.</param>
    /// <param name="granted">Whether the round was one the user granted past the cap.</param>
    /// <param name="planSize">The approved plan's share.</param>
    /// <param name="projectionSize">The ledger projection's share.</param>
    /// <param name="window">The review window.</param>
    /// <param name="scope">What the round left out, and whether untracked files were given by path.</param>
    private static string Oversize(string vendorId, int size, int limit, bool granted, int planSize, int projectionSize,
                                   ReviewWindow window, ReviewScope scope)
    {
        var embedded = window.Files.Where(file => !ByReference(file, scope))
                                   .Select(file => new FileSize(file, Characters(file.Diff)))
                                   .ToList();
        var tracked = embedded.Where(part => !part.File.Untracked).ToList();
        var untracked = embedded.Where(part => part.File.Untracked).ToList();
        var referenced = window.Files.Count(file => ByReference(file, scope));
        var rest = size - planSize - projectionSize - embedded.Sum(part => part.Size);
        var advice = referenced > 0 || untracked.Count == 0
            ? "To fit, start the round again with `excludePaths` naming what is not the run's work, such as run "
              + "tooling or generated output."
            : "To fit, start the round again with `untrackedByReference: true`, which lists the untracked files for "
              + "the critic to read instead of embedding them, or with `excludePaths` naming what is not the run's "
              + "work, such as run tooling or generated output.";

        var message = new StringBuilder()
            .Append($"the code-review prompt is {Count(size)} characters and {vendorId} accepts at most {Count(limit)}, ")
            .Append($"{Count(size - limit)} over. Nothing was sent and the round was not counted")
            .AppendLine(granted ? "; the user's grant for it still stands." : ".")
            .AppendLine()
            .AppendLine("What the prompt holds:")
            .AppendLine($"- tracked diff: {Count(tracked.Sum(part => part.Size))} in {tracked.Count} files")
            .AppendLine(referenced > 0
                ? $"- untracked files: given by path, {referenced} files"
                : $"- untracked files: {Count(untracked.Sum(part => part.Size))} in {untracked.Count} files")
            .AppendLine($"- approved plan: {Count(planSize)}")
            .AppendLine($"- ledger projection: {Count(projectionSize)}")
            .AppendLine($"- everything else: {Count(rest)}")
            .AppendLine()
            .AppendLine("Largest files:");
        foreach (var part in embedded.OrderByDescending(part => part.Size).Take(10))
            message.AppendLine($"- {Count(part.Size)}  {part.File.Path}{(part.File.Untracked ? " (untracked)" : "")}");

        message.AppendLine()
               .Append(advice)
               .Append(" What the critic is handed is the user's call: show them these sizes and ask first.");
        return message.ToString();
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>One embedded file and its share of the prompt, in characters.</summary>
    /// <param name="File">The file.</param>
    /// <param name="Size">Its block's characters.</param>
    private sealed record FileSize(ReviewFile File, int Size);
}

internal sealed class CodeReviewCapReachedException(int rounds, int cap)
    : Exception($"code review already ran {rounds} rounds, and the cap is {cap}. Ask the user "
                + "whether to run another round, and pass `userGrantedRound: true` if they say yes.");

/// <summary>A code-review prompt longer than the critic's vendor accepts, refused before it starts.</summary>
/// <param name="message">The overrun, what the prompt holds and how to narrow it.</param>
internal sealed class ReviewPromptTooLargeException(string message) : Exception(message);
