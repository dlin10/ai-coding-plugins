using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanForge.Acts;
using PlanForge.Diagnostics;
using PlanForge.Evidence;
using PlanForge.Infrastructure;
using PlanForge.Repo;
using PlanForge.Vendors;

namespace PlanForge.Run;

/// <summary>One run's state, isolated under <c>.forge/&lt;runId&gt;/</c>.</summary>
internal sealed class RunDirectory
{
    internal const string FORGE_FOLDER = ".forge";
    private const string STATE_FILE_NAME = "state.json";
    private const string FLOW_LOG_FILE_NAME = "flow_log.md";
    private const string DIAGNOSTIC_LOG_FILE_NAME = "forge.log";
    private const string TELEMETRY_FILE_NAME = "telemetry.json";
    private const string JOBS_FOLDER = "jobs";
    private const string BASELINE_FILE_NAME = "baseline.patch";
    private const string PLAN_FILE_NAME = "PLAN.md";
    /// <summary>The sole Scout report file written by this version.</summary>
    private const string SCOUT_ANSWERS_FILE_NAME = "scout-answers.json";
    /// <summary>The content-addressed Evidence map file.</summary>
    private const string EVIDENCE_MAP_FILE_NAME = "evidence-map.json";
    private const string DECISION_LEDGER_FILE_NAME = "decision-ledger.json";
    private const string DECISIONS_FILE_NAME = "decisions.json";
    private const string REVIEW_DIFFS_FOLDER_PREFIX = "review-round-";

    // The folder ignores itself, so no managed block in info/exclude and nothing to clean up.
    private const string SELF_IGNORE = "*\n";

    private RunDirectory(string runId, string path)
    {
        RunId = runId;
        Path = path;
    }

    public string RunId { get; }

    public string Path { get; }

    internal static RunDirectory FromPath(string runPath)
    {
        if (!System.IO.Path.IsPathRooted(runPath))
            throw new WorkspaceNotRootedException(runPath);

        var fullPath = System.IO.Path.GetFullPath(runPath);
        return new RunDirectory(System.IO.Path.GetFileName(fullPath), fullPath);
    }

    internal string JobFilePath(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (!string.Equals(jobId, System.IO.Path.GetFileName(jobId), StringComparison.Ordinal))
            throw new ArgumentRejectedException("job id must be a file name");

        var jobsPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, JOBS_FOLDER));
        var jobPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(jobsPath, jobId + ".json"));
        var prefix = jobsPath.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!jobPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentRejectedException("job id resolves outside the run's jobs folder");

        return jobPath;
    }

    public string FlowLogPath => System.IO.Path.Combine(Path, FLOW_LOG_FILE_NAME);

    /// <summary>
    /// The run's operational log. Append-only and the one file under <c>.forge/</c> that agents may
    /// add to, through <c>forge.log.append</c> rather than by hand.
    /// </summary>
    public string DiagnosticLogPath => System.IO.Path.Combine(Path, DIAGNOSTIC_LOG_FILE_NAME);

    public RunLog Log => new(DiagnosticLogPath);

    internal string TelemetryPath => System.IO.Path.Combine(Path, TELEMETRY_FILE_NAME);

    /// <summary>
    /// The run's plan as it currently stands, approved or not. Public because the path travels out
    /// with every act result: the plan is written from the first review round on, so the user can
    /// watch it change rather than meeting it once at approval.
    /// </summary>
    public string PlanPath => System.IO.Path.Combine(Path, PLAN_FILE_NAME);

    /// <summary>
    /// The run's cumulative Scout report: every successful answer as a JSON entry.
    /// </summary>
    public string ScoutAnswersPath => System.IO.Path.Combine(Path, SCOUT_ANSWERS_FILE_NAME);

    /// <summary>The Run's stored Evidence judgments and latest map.</summary>
    public string EvidenceMapPath => System.IO.Path.Combine(Path, EVIDENCE_MAP_FILE_NAME);

    internal string DecisionLedgerPath => System.IO.Path.Combine(Path, DECISION_LEDGER_FILE_NAME);

    /// <summary>
    /// The Run's recorded decisions, one per settled question, appended by <c>forge.decision.record</c>.
    /// Public because the path travels out with every act result once the file exists.
    /// </summary>
    public string DecisionsPath => System.IO.Path.Combine(Path, DECISIONS_FILE_NAME);

    /// <summary>
    /// The folder a code-review round's tracked diffs are written to when the window is given to
    /// the critic by path, and deleted from when the round ends. See docs/adr/0034.
    /// </summary>
    /// <param name="round">The code-review round the folder serves.</param>
    internal string ReviewDiffsPath(int round) => System.IO.Path.Combine(Path, $"{REVIEW_DIFFS_FOLDER_PREFIX}{round}");

    /// <summary>
    /// Where one changed file's diff is written for <paramref name="round"/>: its path from the
    /// workspace with <c>.diff</c> appended, refused when it would land outside the round's folder.
    /// </summary>
    /// <param name="round">The code-review round.</param>
    /// <param name="filePath">The changed file's path from the workspace root.</param>
    internal string ReviewDiffPath(int round, string filePath)
    {
        var folder = System.IO.Path.GetFullPath(ReviewDiffsPath(round));
        var diffPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, filePath + ".diff"));
        var prefix = folder.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!diffPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"the diff of {filePath} resolves outside the round's review folder");

        return diffPath;
    }

    public DecisionLedger ReadDecisionLedger() => DecisionLedger.Open(this);

    /// <summary>
    /// Starts a run in the directory the host calls its own, falling back to
    /// <paramref name="workspaceRoot"/> when it declares none. A run's files are the ones a person
    /// reads, so they belong beside the session rather than wherever the task under review happens
    /// to be rooted — see <see cref="SessionRoots"/>.
    /// </summary>
    public static async Task<RunDirectory> CreateAsync(SessionRoots roots,
                                                       string workspaceRoot,
                                                       string runId,
                                                       CancellationToken ct) =>
        Create(await roots.DirectoryAsync(ct).ConfigureAwait(false) ?? workspaceRoot, runId);

    /// <summary>
    /// Finds a run <see cref="CreateAsync"/> started. The session root is tried first and the
    /// workspace root second, so a run begun before the session root was consulted — or begun
    /// against a host that declares none — is still found by the same call.
    /// </summary>
    public static async Task<RunDirectory> OpenAsync(SessionRoots roots,
                                                     string workspaceRoot,
                                                     string runId,
                                                     CancellationToken ct) =>
        await roots.DirectoryAsync(ct).ConfigureAwait(false) is { } sessionRoot && Exists(sessionRoot, runId)
            ? Open(sessionRoot, runId)
            : Open(workspaceRoot, runId);

    public static RunDirectory Create(string runRoot, string runId)
    {
        var runPath = Confine(runRoot, runId);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(runPath)!);
        AtomicFile.Write(System.IO.Path.Combine(runRoot, FORGE_FOLDER, ".gitignore"), SELF_IGNORE);
        Directory.CreateDirectory(runPath);

        var run = new RunDirectory(runId, runPath);
        DecisionLedger.Create(run.DecisionLedgerPath);
        return run;
    }

    public static RunDirectory Open(string runRoot, string runId)
    {
        var runPath = Confine(runRoot, runId);
        if (!Directory.Exists(runPath)) 
            throw new RunNotFoundException(runId);
        return new RunDirectory(runId, runPath);
    }

    private static bool Exists(string runRoot, string runId) => Directory.Exists(Confine(runRoot, runId));

    /// <summary>
    /// The second of the two surviving checks: everything this class writes is under the run folder,
    /// so one containment test on the run id replaces the six symlink and reparse-point guards. The
    /// run id always reaches us from a tool call, and so does the root whenever it is the workspace
    /// root, which makes both caller input.
    /// </summary>
    /// <remarks>
    /// The root has to be absolute, and that is worth refusing rather than resolving. A relative one
    /// would be resolved against the server process's working directory, which is not the repository
    /// and differs by host — the plugin folder under Codex, whatever the host started in elsewhere.
    /// Two sessions passing a relative root would then land in the same folder, and their runs would
    /// silently share it. A session root cannot trip this: it is read out of a <c>file://</c> URI.
    /// </remarks>
    private static string Confine(string runRoot, string runId)
    {
        if (!System.IO.Path.IsPathRooted(runRoot)) throw new WorkspaceNotRootedException(runRoot);

        var forgeRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(runRoot, FORGE_FOLDER));
        var runPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(forgeRoot, runId));

        var prefix = forgeRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!runPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new RunEscapedException(runId);

        return runPath;
    }

    // Never inlined: a failed read has to name RunDirectory in the stack forge.log keeps, and a
    // one-line wrapper is what dynamic PGO inlines into its caller once that caller runs hot.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public RunState ReadState() => ParseState(AtomicFile.Read(System.IO.Path.Combine(Path, STATE_FILE_NAME)));

    /// <summary>
    /// Replaces the state with what <paramref name="change"/> makes of it as it stands now, under the
    /// state file's process-local mutation lock: for a writer that read the state long before it
    /// writes, such as a Scout turn that ran for minutes beside the Orchestrator's own calls.
    /// </summary>
    /// <param name="change">The new state, made from the current one.</param>
    public void UpdateState(Func<RunState, RunState> change) =>
        AtomicFile.Update(System.IO.Path.Combine(Path, STATE_FILE_NAME),
                          json => JsonSerializer.Serialize(change(ParseState(json ?? throw new RunNotFoundException(RunId))),
                                                           ForgeJson.Readable.RunState));

    private RunState ParseState(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("fullGate", out var timing))
        {
            if (timing.ValueKind != JsonValueKind.String
                || timing.GetString() is not FixGatePolicy.BeforeNextRound and not FixGatePolicy.Final)
                throw new ArgumentRejectedException("saved fullGate must be exactly 'beforeNextRound' or 'final' (case-sensitive)");
        }
        return JsonSerializer.Deserialize(json, ForgeJson.Default.RunState)
            ?? throw new RunNotFoundException(RunId);
    }

    public void WriteState(RunState state)
    {
        var json = JsonSerializer.Serialize(state, ForgeJson.Readable.RunState);
        AtomicFile.Write(System.IO.Path.Combine(Path, STATE_FILE_NAME), json);
    }

    public void WriteBaseline(Baseline baseline) =>
        AtomicFile.Write(System.IO.Path.Combine(Path, BASELINE_FILE_NAME), baseline.Diff);

    public Baseline ReadBaseline(string head)
    {
        var path = System.IO.Path.Combine(Path, BASELINE_FILE_NAME);
        return new Baseline(head, File.Exists(path) ? AtomicFile.Read(path) : string.Empty);
    }

    /// <summary>
    /// Whole-file replacement, which is what lets a review round write the draft before the critic
    /// runs: a round that dies and is retried with the same arguments writes the same bytes again,
    /// unlike the log appends that have to wait for the critique.
    /// </summary>
    public void WritePlan(string plan) => AtomicFile.Write(PlanPath, plan);

    public string ReadPlan() => AtomicFile.Read(PlanPath);

    /// <summary>Appends one validated answer under the file's process-local mutation lock.</summary>
    /// <param name="entry">The complete answer and its recorded locator checks.</param>
    public void AppendScoutAnswer(ScoutAnswerEntry entry)
    {
        AtomicFile.Update(ScoutAnswersPath, text =>
        {
            var entries = ScoutAnswers.Read(text);
            entries.Add(entry);
            ScoutAnswers.Validate(entries);
            return JsonSerializer.Serialize(entries, ScoutAnswersJson.Default.ListScoutAnswerEntry);
        });
    }

    /// <summary>Reads the validated Scout entries, or an empty list when no answer has been stored.</summary>
    public IReadOnlyList<ScoutAnswerEntry> ReadScoutAnswers() =>
        ScoutAnswers.Read(File.Exists(ScoutAnswersPath) ? AtomicFile.Read(ScoutAnswersPath) : null);

    /// <summary>Reads the checked decision records, or none when no decision has been recorded.</summary>
    public IReadOnlyList<DecisionRecord> ReadDecisions() =>
        DecisionRecords.Read(File.Exists(DecisionsPath) ? AtomicFile.Read(DecisionsPath) : null);

    /// <summary>
    /// One recorded decision in the timeline, under the ID it received: the question and the answer
    /// verbatim, and the options as the question offered them.
    /// </summary>
    /// <param name="record">The decision as recorded.</param>
    public void AppendFlowDecision(DecisionRecord record)
    {
        var entry = new StringBuilder().Append("## Decision ").AppendLine(record.DecisionId)
                                       .AppendLine()
                                       .Append("Phase: ").AppendLine(record.Phase)
                                       .Append("By: ").AppendLine(record.By);
        if (record.Supersedes is not null) entry.Append("Supersedes: ").AppendLine(record.Supersedes);
        if (record.BearsOn is not null) entry.Append("Bears on: ").AppendLine(record.BearsOn);
        entry.AppendLine()
             .Append("Question: ").AppendLine(record.Question.TrimEnd());
        if (record.Options is { Count: > 0 } options)
        {
            entry.AppendLine();
            foreach (var option in options)
                entry.Append("- ").Append(option.Label).AppendLine(option.Recommended ? " (recommended)" : "");
        }

        entry.AppendLine()
             .Append("Answer: ").AppendLine(record.Answer.TrimEnd())
             .AppendLine();
        AtomicFile.Append(FlowLogPath, entry.ToString());
    }

    /// <summary>
    /// Appends one content-free Evidence map accounting entry, even for an interrupted call. Each
    /// status's total comes from the result's counts; only a flagged status splits it into new and
    /// carried, because the flags hold every item of a flagged status but of any other status only
    /// the items whose source changed.
    /// </summary>
    /// <param name="progress">The mapper's final running accounting.</param>
    public void AppendFlowEvidenceMap(EvidenceMapProgress progress)
    {
        var result = progress.Result!;
        var entry = new StringBuilder().AppendLine("## Evidence map").AppendLine()
                                       .Append("Advisor: ").AppendLine(result.Advisor)
                                       .Append("Requested model: ").AppendLine(result.RequestedModel)
                                       .Append("Served model: ").AppendLine(result.ServedModel ?? "unknown")
                                       .Append("required: ").AppendLine(result.Required.ToString())
                                       .Append("checkedThisCall: ").AppendLine(result.CheckedThisCall.ToString())
                                       .Append("remaining: ").AppendLine(result.Remaining.ToString())
                                       .Append("cacheHits: ").AppendLine(result.CacheHits.ToString())
                                       .Append("alreadyMapped: ").AppendLine(result.AlreadyMapped.ToString());
        foreach (var status in new[] { "contradiction", "unchecked", "context_only", "covered", "excluded", "uncertain", "no_candidates", "no_coverage_established" })
        {
            entry.Append(status).Append(": ").Append(result.Counts[status]);
            if (EvidenceVerdicts.IsFlag(status, sourceChanged: false))
            {
                var flags = result.Flags.Where(flag => flag.Status == status).ToArray();
                entry.Append(" (new ").Append(flags.Count(flag => flag.Change == "new"))
                     .Append(", carried ").Append(flags.Count(flag => flag.Change == "carried")).Append(')');
            }
            entry.AppendLine();
        }
        entry.Append("notIndexedAnswers: ").AppendLine(string.Join(", ", result.NotIndexedAnswers))
             .Append("complete: ").AppendLine(result.Complete ? "true" : "false");
        if (result.StoppedBy is { } stopped) entry.Append("stoppedBy: ").AppendLine(stopped);
        entry.Append("estimatedCostUsd: ").AppendLine(Usd(result.EstimatedCostUsd))
             .Append("costUsd: ").AppendLine(result.CostUsd is { } cost ? Usd(cost) : "unknown");
        if (result.RunCostUsd is { } run && result.BudgetUsd is { } budget)
            entry.Append("runCostUsd: ").Append(Usd(run)).Append(" of budgetUsd ").AppendLine(Usd(budget));
        AtomicFile.Append(FlowLogPath, entry.AppendLine().ToString());
    }

    /// <summary>Writes an amount the way the Flow log shows every cost: four decimals, invariant.</summary>
    /// <param name="amount">The amount in USD.</param>
    private static string Usd(decimal amount) => amount.ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>
    /// The user-facing timeline of the delegated acts, one file for the orchestrator to surface in
    /// whatever panel the host has. It is never fed back to a worker, which
    /// is why builder entries can live here without shifting what the next round's critic judges.
    /// </summary>
    /// <param name="act">The review phase.</param>
    /// <param name="round">The round number, or null for an empty diff that consumes no round.</param>
    /// <param name="critique">The identified critique.</param>
    public void AppendFlowCritique(string act, int? round, Critique critique)
    {
        var heading = round is null ? $"## {act} — empty diff (no round consumed)" : $"## {act} — round {round}";
        var entry = new StringBuilder(CritiqueEntry(heading, critique));
        if (act == "Code review") AppendPendingFullGate(entry);
        AtomicFile.Append(FlowLogPath, entry.ToString());
    }

    public void AppendFlowCritiqueRejected(string act, int round, VendorCritique? critique, string error)
    {
        var entry = new StringBuilder().Append("## ").Append(act).Append(" — round ").Append(round)
                                       .AppendLine(" rejected")
                                       .AppendLine()
                                       .Append("Error: ").AppendLine(error)
                                       .AppendLine();

        if (critique is not null)
        {
            entry.AppendLine("Rejected critique:")
                 .Append("Verdict: ").AppendLine(critique.Verdict)
                 .Append("Summary: ").AppendLine(critique.Summary)
                 .Append("Finding count: ").AppendLine((critique.Findings?.Count ?? 0).ToString())
                 .Append("Assessment IDs: ").AppendLine(string.Join(", ",
                     critique.UnresolvedAssessments?.Where(assessment => assessment is not null)
                         .Select(assessment => assessment.FindingId) ?? []))
                 .Append("Reopening IDs: ").AppendLine(string.Join(", ",
                     critique.Reopenings?.Where(reopening => reopening is not null)
                         .Select(reopening => reopening.FindingId) ?? []))
                 .AppendLine();

            if (critique.Findings is not null)
                foreach (var finding in critique.Findings)
                    if (finding is not null)
                        entry.Append("- ").Append(finding.Severity).Append(" ")
                             .Append(finding.Where).Append(" — ").AppendLine(finding.What);

            if (critique.UnresolvedAssessments is not null)
                foreach (var assessment in critique.UnresolvedAssessments)
                    if (assessment is not null)
                        entry.Append("assessment ").Append(assessment.FindingId).Append(" (")
                             .Append(assessment.StillPresent ? "still present" : "not present")
                             .Append("): ").AppendLine(assessment.Evidence);

            if (critique.Reopenings is not null)
                foreach (var reopening in critique.Reopenings)
                    if (reopening is not null)
                        entry.Append("reopening ").Append(reopening.FindingId).Append(": ")
                             .AppendLine(reopening.Evidence);
        }

        AtomicFile.Append(FlowLogPath, entry.ToString());
    }

    /// <summary>
    /// A raise is written with the ID it received and the finding itself, because the finding exists
    /// nowhere else in the timeline: no critique carried it. A plan revision closure ends its line
    /// with the evidence it rested on, put on that one line.
    /// </summary>
    /// <param name="act">The act that applied the batch, for the entry's heading.</param>
    /// <param name="batch">The batch as the orchestrator sent it.</param>
    /// <param name="response">What the ledger did with it; a retry that changed nothing writes no entry.</param>
    /// <param name="phase">The phase in which the decisions were applied.</param>
    public void AppendFlowDecisionBatch(string act, OrchestratorDecisionBatch batch, DecisionBatchResponse response, LedgerPhase phase) =>
        AppendFlowDecisionBatch(act, response, phase, batch.Decisions
            .OrderBy(decision => decision.FindingId, StringComparer.Ordinal)
            .Select(decision => $"- {decision.FindingId} {decision.Action} ({decision.By})"
                                + (decision.ForTasks is { Count: > 0 } tasks ? $" for tasks {string.Join(", ", tasks)}" : "")
                                + $": {decision.Reason}{RevisionEvidence(decision)}")
            .Concat((batch.Raises ?? []).Zip(response.Result.RaisedFindingIds ?? [])
                .SelectMany(raise => new[]
                {
                    $"- {raise.Second} raised ({raise.First.By}): {raise.First.Reason}",
                    $"  **{raise.First.Severity}** {raise.First.Where} — {raise.First.What}"
                })));

    public void AppendFlowDecisionBatch(string act, DecisionBatchRequest batch, DecisionBatchResponse response, LedgerPhase phase) =>
        AppendFlowDecisionBatch(act, response, phase, batch.Decisions
            .Select(decision => $"- {decision.FindingId} {Name(decision.Disposition)} ({Name(decision.By)}): {decision.Reason}")
            .Concat(batch.Reopenings.Select(reopening =>
                $"- {reopening.FindingId} reopen ({Name(reopening.By)}): {reopening.Reason}"))
            .Concat(batch.Closures.Select(closure =>
                $"- {closure.FindingId} {Name(closure.Kind)} ({Name(closure.By)}): {closure.Reason}")));

    /// <summary>
    /// One line per decision, so the timeline says why a finding was settled; a conflict shows the
    /// saved batch's IDs instead, because the decisions just sent are not the ones that stand.
    /// </summary>
    /// <param name="act">The act that applied the batch, for the entry's heading.</param>
    /// <param name="response">What the ledger did with the batch.</param>
    /// <param name="phase">The phase in which the decisions were applied.</param>
    /// <param name="decisions">The batch's lines, already written as the timeline shows them.</param>
    private void AppendFlowDecisionBatch(string act, DecisionBatchResponse response, LedgerPhase phase, IEnumerable<string> decisions)
    {
        if (response.Outcome == "no_op") return;

        var result = response.Result;
        var entry = new StringBuilder().Append("## ").Append(act).Append(" — decisions ")
                                       .AppendLine(response.Outcome)
                                       .AppendLine()
                                       .Append("decisionBatchId: ").AppendLine(result.DecisionBatchId);
        if (response.Outcome == "conflict")
        {
            entry.Append("saved decisions: ").AppendLine(string.Join(", ", result.DecisionFindingIds));
            if (result.RaisedFindingIds is { Count: > 0 } raised)
                entry.Append("saved raises: ").AppendLine(string.Join(", ", raised));
        }
        else
            foreach (var decision in decisions)
                entry.AppendLine(decision);

        entry.Append("closures: ").AppendLine(string.Join(", ", result.ClosedFindingIds)).AppendLine();
        if (phase == LedgerPhase.CodeReview) AppendPendingFullGate(entry);
        AtomicFile.Append(FlowLogPath, entry.ToString());
    }

    /// <summary>
    /// What a plan revision closure rested on, for the end of its line in the timeline. The evidence
    /// is put on that one line however many it spanned; any other decision's line is left as it was.
    /// </summary>
    /// <param name="decision">The decision as the orchestrator sent it.</param>
    /// <returns>An em dash, <c>evidence:</c> and the evidence, or nothing for a decision that is no revision closure or carries none.</returns>
    private static string RevisionEvidence(OrchestratorDecision decision) =>
        decision.Evidence is { } evidence && DecisionLedger.NormalizeAction(decision.Action) == "addressedByRevision"
            ? $" — evidence: {DecisionRecords.Flat(evidence)}"
            : "";

    private static string Name<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public void AppendFlowDecisionRejected(string act, string decisionBatchId, string error)
    {
        AtomicFile.Append(FlowLogPath,
            new StringBuilder().Append("## ").Append(act).AppendLine(" — decisions rejected")
                               .AppendLine()
                               .Append("decisionBatchId: ").AppendLine(decisionBatchId)
                               .Append("Error: ").AppendLine(error)
                               .AppendLine()
                               .ToString());
    }

    /// <summary>
    /// The orchestrator's own turn in the plan-review loop, which the timeline used to skip: the
    /// user saw one verdict, then the next, with nothing between them saying what the findings
    /// changed. The critic's rounds are worker acts and the server records them; this is the only
    /// entry the orchestrator has to hand in itself.
    /// </summary>
    public void AppendFlowRevision(int round, string revision, string? deferred)
    {
        var entry = new StringBuilder().Append("## Plan revision after round ").Append(round).AppendLine()
                                       .AppendLine()
                                       .AppendLine(revision.TrimEnd())
                                       .AppendLine();

        if (deferred is { Length: > 0 })
            entry.AppendLine("### Deferred by the orchestrator")
                 .AppendLine()
                 .AppendLine(deferred.TrimEnd())
                 .AppendLine();

        AtomicFile.Append(FlowLogPath, entry.ToString());
    }

    /// <summary>
    /// The instructions the user gave this run's workers, verbatim. The server cannot tell text the
    /// user typed from text the orchestrator invented — the same hole `approved` has, see
    /// docs/adr/0019 — so this entry is what makes the answer auditable by the one person who knows.
    /// </summary>
    /// <param name="critic">The critic's new text, <see langword="null"/> when the call left it alone.</param>
    /// <param name="builder">The builder's new text, <see langword="null"/> when the call left it alone.</param>
    public void AppendFlowInstructions(string? critic, string? builder)
    {
        var entry = new StringBuilder().AppendLine("## Instructions for this run's workers")
                                       .AppendLine();

        Section(entry, "Critic", critic);
        Section(entry, "Builder", builder);

        AtomicFile.Append(FlowLogPath, entry.ToString());
        return;

        static void Section(StringBuilder entry, string role, string? text)
        {
            if (text is null) return;

            entry.Append("### ").AppendLine(role)
                 .AppendLine()
                 .AppendLine(text.Length == 0 ? "(cleared)" : text.TrimEnd())
                 .AppendLine();
        }
    }

    public void AppendFlowScoutSelection(string action, ScoutState scout)
    {
        var entry = new StringBuilder().Append("## Scout ").AppendLine(action)
                                       .AppendLine();

        if (scout.Enabled)
        {
            entry.Append("Vendor: ").AppendLine(scout.Vendor)
                 .Append("Model: ").AppendLine(scout.Model);

            if (scout.Effort is not null)
                entry.Append("Effort: ").AppendLine(scout.Effort);
            if (scout.Fast)
                entry.AppendLine("Speed: Fast");
        }
        else
        {
            entry.AppendLine("The run will continue without Scout.");
        }

        AtomicFile.Append(FlowLogPath, entry.AppendLine().ToString());
    }

    public void AppendFlowScoutOutcome(string outcome, string question, ScoutFailure? failure = null)
    {
        var entry = new StringBuilder().Append("## Scout ").AppendLine(outcome)
                                       .AppendLine()
                                       .Append("Question: ").AppendLine(question.ReplaceLineEndings(" "));
        if (failure is null && outcome == "completed")
            entry.AppendLine("The answer was appended to the Scout report.");
        else if (failure is not null)
            entry.Append("Failure: ").Append(failure.Code).Append(" — ").AppendLine(failure.Summary);

        AtomicFile.Append(FlowLogPath, entry.AppendLine().ToString());
    }

    /// <summary>
    /// A review round or plan write takes an existing approval back, and the user
    /// meets that as a build refusing for no visible reason unless it is in the timeline. The
    /// entry names the task count that went with it, because that is the part that costs money to
    /// rebuild.
    /// </summary>
    /// <param name="tasksCompleted">The completed tasks whose progress was reset.</param>
    /// <param name="cause">The action that invalidated the approval.</param>
    /// <summary>Records that a change to the plan took its approval or part of its build progress back.</summary>
    /// <param name="wasApproved">Whether the plan was approved when the change came.</param>
    /// <param name="tasksCompleted">The tasks built before the change.</param>
    /// <param name="tasksKept">The tasks that stay built after it.</param>
    /// <param name="firstTaskOwed">The task the next build runs, or null when the plan has none after the kept ones.</param>
    /// <param name="cause">The change.</param>
    public void AppendFlowReopened(bool wasApproved, int tasksCompleted, int tasksKept, int? firstTaskOwed, string cause)
    {
        var entry = new StringBuilder().AppendLine("## Plan reopened")
                                       .AppendLine()
                                       .Append(wasApproved ? "The approval no longer holds because " : "No approval was standing when ")
                                       .Append(cause).Append(". ");
        if (tasksKept < tasksCompleted)
            entry.Append("Build progress was reset from ").Append(tasksCompleted)
                 .Append(tasksCompleted == 1 ? " completed task to " : " completed tasks to ").Append(tasksKept).AppendLine(".");
        else if (tasksKept == 0) entry.AppendLine("No task had been built.");
        else if (tasksKept == 1) entry.AppendLine("Task 1 stays built.");
        else entry.Append("Tasks 1–").Append(tasksKept).AppendLine(" stay built.");

        entry.AppendLine()
             .Append("The plan has to be approved again before the builder will run, and ");
        if (firstTaskOwed is > 1) entry.Append("it will start from task ").Append(firstTaskOwed.Value).AppendLine(".");
        else if (firstTaskOwed is 1 || tasksKept == 0) entry.AppendLine("it will start from the first task.");
        else entry.AppendLine("every task of the plan is built.");
        AtomicFile.Append(FlowLogPath, entry.AppendLine().ToString());
    }

    /// <summary>Records that an approval sent the build back to a built task the user asked to rebuild.</summary>
    /// <param name="task">The task the build starts from again.</param>
    /// <param name="tasksCompleted">The tasks built before the approval.</param>
    public void AppendFlowRebuildFrom(int task, int tasksCompleted) =>
        AtomicFile.Append(FlowLogPath,
            new StringBuilder().AppendLine("## Rebuild requested")
                               .AppendLine()
                               .Append("The approval asks to rebuild from task ").Append(task).Append(". ")
                               .Append("Build progress was reset from ").Append(tasksCompleted)
                               .Append(tasksCompleted == 1 ? " completed task to " : " completed tasks to ").Append(task - 1)
                               .Append(", and the builder will start from ")
                               .AppendLine(task == 1 ? "the first task." : $"task {task}.")
                               .AppendLine()
                               .ToString());

    public void AppendFlowPlanWritten(string planHash, PlanShape shape)
    {
        var entry = new StringBuilder().AppendLine("## Plan written")
                                       .AppendLine()
                                       .Append("Plan hash: ").AppendLine(planHash)
                                       .AppendLine();
        AppendPlanShape(entry, shape);
        AtomicFile.Append(FlowLogPath, entry.AppendLine().ToString());
    }

    public void AppendFlowPlanPatched(string baseHash, string planHash,
                                      IReadOnlyList<PlanPatchChange> changes, PlanPatchSizes sizes, PlanShape shape)
    {
        var entry = new StringBuilder().AppendLine("## Plan patched")
                                       .AppendLine()
                                       .Append("Plan hash: ").Append(baseHash).Append(" → ").AppendLine(planHash)
                                       .AppendLine();
        foreach (var change in changes)
        {
            entry.Append("- ").Append(change.Op).Append(' ').Append(change.Target).Append(": ").Append(change.Label);
            if (change.Op == "edit")
                entry.Append(" — ").Append(Preview(change.Find!)).Append(" → ").Append(Preview(change.Replace!));
            entry.AppendLine();
        }
        entry.AppendLine();
        AppendPlanShape(entry, shape);
        entry.AppendLine();
        foreach (var (name, size) in sizes.Measurements())
            entry.Append(name).Append(": ").Append(size.Characters).Append(" characters, ")
                 .Append(size.Utf8Bytes).AppendLine(" UTF-8 bytes");
        AtomicFile.Append(FlowLogPath, entry.AppendLine().ToString());

        static string Preview(string text) => JsonSerializer.Serialize(text.Length > 80 ? text[..80] + "…" : text,
                                                              ForgeJson.Readable.String);
    }

    private static void AppendPlanShape(StringBuilder entry, PlanShape shape)
    {
        entry.AppendLine("Shape:")
             .Append("- Characters: ").AppendLine(shape.Characters.ToString(CultureInfo.InvariantCulture))
             .Append("- Tasks: ").Append(shape.TaskCount).Append("; task characters: ").Append(shape.TaskCharacters)
             .Append("; largest task: ").AppendLine(shape.LargestTaskCharacters.ToString(CultureInfo.InvariantCulture))
             .Append("- Requirements: ").Append(shape.RequirementsCharacters)
             .Append("; Approach: ").Append(shape.ApproachCharacters)
             .Append("; ratio: ").AppendLine(shape.ApproachToRequirementsRatio?.ToString("0.00", CultureInfo.InvariantCulture) ?? "n/a")
             .Append("- R-items: ").Append(shape.RequirementItemCount)
             .Append("; named test classes: ").Append(shape.NamedTestClassCount)
             .Append("; MatrixTests: ").AppendLine(shape.MatrixTestClassCount.ToString(CultureInfo.InvariantCulture))
             .Append("- Offer a split: ").AppendLine(SplitOffer(shape));
    }

    private static string SplitOffer(PlanShape shape) =>
        !shape.SplitSuggested ? "no"
        : shape.SplitReason is null ? "yes"
        : $"yes — {shape.SplitReason} since {shape.SplitCheckpoint?.ToString("N0", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Records that a round ran past its cap because the user granted it, so a reader of
    /// <c>flow_log.md</c> sees the round as bought rather than budgeted.
    /// </summary>
    /// <summary>
    /// A round refused before any critic started, which spends nothing: the timeline says why, so a
    /// round the user granted does not read as one that ran and failed. See docs/adr/0029.
    /// </summary>
    /// <param name="act">The act's label, such as "Code review".</param>
    /// <param name="round">The round that was not sent.</param>
    /// <param name="reason">Why it was refused.</param>
    public void AppendFlowRoundNotSent(string act, int round, string reason) =>
        AtomicFile.Append(FlowLogPath,
            new StringBuilder().Append("## ").Append(act).Append(" — round ").Append(round).AppendLine(" not sent")
                               .AppendLine()
                               .AppendLine(reason.TrimEnd())
                               .AppendLine()
                               .ToString());

    /// <summary>A review round that ran past the cap without asking, because the findings fell and no blocker was left.</summary>
    /// <param name="act">The review phase.</param>
    /// <param name="round">The round that ran.</param>
    /// <param name="continuation">The last two rounds' finding counts, as <c>4 → 2</c>.</param>
    public void AppendFlowAutoContinuedRound(string act, int round, string continuation) =>
        AtomicFile.Append(FlowLogPath,
            new StringBuilder().Append("## ").Append(act).AppendLine(" — round continued automatically")
                               .AppendLine()
                               .Append("Round ").Append(round)
                               .Append(" runs past the cap without asking, because the findings fell and the last round left no blocker: continued automatically: ")
                               .Append(continuation).AppendLine(".")
                               .AppendLine()
                               .ToString());

    public void AppendFlowGrantedRound(string act, int round) =>
        AtomicFile.Append(FlowLogPath,
            new StringBuilder().Append("## ").Append(act).AppendLine(" — extra round granted")
                               .AppendLine()
                               .Append("Round ").Append(round)
                               .AppendLine(" runs because the user granted it past the cap.")
                               .AppendLine()
                               .ToString());

    /// <summary>A plan-review round that judged a plan the Evidence map had not seen since an earlier round.</summary>
    /// <param name="round">The round that ran.</param>
    /// <param name="warning">The warning returned beside the round's critique.</param>
    public void AppendFlowEvidenceMapWarning(int round, string warning) =>
        AtomicFile.Append(FlowLogPath,
            new StringBuilder().Append("## Evidence map not current — plan review round ").Append(round).AppendLine()
                               .AppendLine()
                               .AppendLine(warning)
                               .AppendLine()
                               .ToString());

    /// <summary>
    /// A critic that ended its turn with work still running in the background judged without that
    /// work's result. Its critique stands — the orchestrator filters every critique anyway — but the
    /// reader of the timeline should know what it never saw the end of. See docs/adr/0018.
    /// </summary>
    public void AppendFlowKilledTasks(string act, int round, IReadOnlyList<string> killed)
    {
        if (killed.Count == 0) return;

        RunLog.Current?.Write("warn", "critic", "critic.background-killed",
            ("act", act), ("round", round.ToString()), ("tasks", string.Join("; ", killed)));

        var entry = new StringBuilder().Append("## ").Append(act).Append(" — round ").Append(round)
                                       .AppendLine(": background work killed")
                                       .AppendLine()
                                       .AppendLine("The critic ended its turn while this still ran in the background, so the session killed it "
                                                   + "and the critique above was written without its result:")
                                       .AppendLine();

        foreach (var task in killed)
            entry.Append("- `").Append(task).AppendLine("`");

        AtomicFile.Append(FlowLogPath, entry.AppendLine().ToString());
    }

    /// <summary>A Fast turn served at standard speed for part of it: the work counts, the speed did not hold.</summary>
    public void AppendFlowSpeedWarning(string warning) =>
        AtomicFile.Append(FlowLogPath, new StringBuilder().AppendLine("## Speed")
                                                          .AppendLine()
                                                          .AppendLine(warning)
                                                          .AppendLine()
                                                          .ToString());

    public void AppendFlowBuild(int number, int total, BuildResult result)
    {
        var entry = new StringBuilder().Append("## Task ").Append(number).Append(" of ").Append(total).AppendLine()
                                       .AppendLine();

        AppendBuildResult(entry, result);
        AtomicFile.Append(FlowLogPath, entry.ToString());
    }

    /// <summary>One fix turn in the timeline: the findings it was given, the note beside them, and its result.</summary>
    /// <param name="round">The code-review round the fix answers.</param>
    /// <param name="findings">The findings the builder was given, empty for a decisions-only call.</param>
    /// <param name="note">The orchestrator's framing the builder was shown after the findings, verbatim.</param>
    /// <param name="result">The builder's result as the gates left it.</param>
    /// <param name="fixAttemptId">The attempt whose result is recorded, absent for decisions only.</param>
    /// <param name="fixFindingIds">The findings bound to the attempt, absent for decisions only.</param>
    public void AppendFlowFix(int round, string findings, string? note, BuildResult result,
                              string? fixAttemptId = null, IReadOnlyList<string>? fixFindingIds = null)
    {
        var entry = new StringBuilder().Append("## Fixes — round ").Append(round).AppendLine()
                                       .AppendLine();

        if (findings.Trim().Length > 0)
            entry.AppendLine(findings.TrimEnd())
                 .AppendLine();

        if (!string.IsNullOrWhiteSpace(note))
            entry.AppendLine("### From the orchestrator")
                 .AppendLine()
                 .AppendLine(note.TrimEnd())
                 .AppendLine();

        if (fixAttemptId is not null)
            entry.Append("fixAttemptId: ").AppendLine(fixAttemptId)
                 .Append("fixFindingIds: ").AppendLine(string.Join(", ", fixFindingIds ?? []));
        AppendBuildResult(entry, result);
        AppendPendingFullGate(entry);
        AtomicFile.Append(FlowLogPath, entry.ToString());
    }

    public void AppendFlowFixAttemptNoOp(string fixAttemptId, IReadOnlyList<string> findingIds, BuildResult result)
    {
        var entry = new StringBuilder().AppendLine("## Fix attempt — no-op")
                                       .AppendLine()
                                       .Append("fixAttemptId: ").AppendLine(fixAttemptId)
                                       .Append("fixFindingIds: ").AppendLine(string.Join(", ", findingIds))
                                       .AppendLine("The saved terminal result was returned without starting the Builder or running a gate.")
                                       .AppendLine();
        AppendBuildResult(entry, result);
        AppendPendingFullGate(entry);
        AtomicFile.Append(FlowLogPath, entry.ToString());
    }

    private void AppendPendingFullGate(StringBuilder entry)
    {
        var summary = ReadDecisionLedger().Summary;
        entry.Append("fullGate: ").AppendLine(ReadState().FullGate);
        entry.Append("pendingFullGateFindingIds: ").AppendLine(string.Join(", ", summary.PendingFullGateFindingIds));
        foreach (var attempt in summary.PendingFullGateAttempts)
            entry.Append("pendingFullGateAttempt: ").Append(attempt.FixAttemptId).Append(" — ")
                 .AppendLine(string.Join(", ", attempt.FindingIds));
        entry.AppendLine();
    }

    /// <summary>
    /// A turn the host took away before the builder answered. There is no status, no verification
    /// and no gate to record — the builder never reported — so the files on disk are the entry,
    /// and the timeline says plainly that nothing here was checked.
    /// </summary>
    /// <remarks>
    /// Round 5 of run 20260917-111319-20e672 is why this exists: an hour of finished work, and a
    /// timeline that stepped straight from the critique to the next round as though the fix had
    /// never run.
    /// </remarks>
    /// <param name="heading">What the act calls this turn, without the cut-short suffix.</param>
    /// <param name="filesWritten">What the builder had written, as git saw it after the kill.</param>
    /// <param name="fixAttemptId"></param>
    /// <param name="fixFindingIds"></param>
    public void AppendFlowCutShort(string heading, IReadOnlyList<string> filesWritten,
                                   string? fixAttemptId = null,
                                   IReadOnlyList<string>? fixFindingIds = null)
    {
        var entry = new StringBuilder().Append("## ").Append(heading).AppendLine(": cut short")
                                       .AppendLine()
                                       .AppendLine("The host took the call away before the builder answered, so the session was killed "
                                                   + "with it. No gate ran and nothing was counted.")
                                       .AppendLine();

        if (filesWritten.Count == 0)
        {
            entry.AppendLine("It had written nothing the working tree can show.");
        }
        else
        {
            entry.AppendLine("What it had already written is still on disk:")
                 .AppendLine();

            foreach (var file in filesWritten) entry.Append("- `").Append(file).AppendLine("`");
        }

        if (fixAttemptId is { Length: > 0 })
            entry.AppendLine().Append("fixAttemptId: ").AppendLine(fixAttemptId)
                 .Append("fixFindingIds: ").AppendLine(string.Join(", ", fixFindingIds ?? []));

        AtomicFile.Append(FlowLogPath, entry.AppendLine().ToString());
    }

    private static string CritiqueEntry(string heading, Critique critique)
    {
        var entry = new StringBuilder().Append(heading).AppendLine()
                                       .AppendLine()
                                       .Append("Verdict: ").Append(critique.Verdict).AppendLine()
                                       .AppendLine()
                                       .AppendLine(critique.Summary)
                                       .AppendLine();

        foreach (var finding in critique.Findings)
        {
            entry.Append("- ");
            if (finding.FindingId is { Length: > 0 }) entry.Append(finding.FindingId).Append(" ");
            entry.Append("**").Append(finding.Severity).Append("** ")
                 .Append(finding.Where).Append(" — ").AppendLine(finding.What);
        }

        if (critique.UnresolvedAssessments is { Count: > 0 })
        {
            entry.AppendLine().AppendLine("### Unresolved assessments");
            foreach (var assessment in critique.UnresolvedAssessments)
                entry.Append("- ").Append(assessment.FindingId).Append(": ")
                     .Append(assessment.StillPresent ? "still present" : "not present")
                     .Append(" — ").AppendLine(assessment.Evidence);
        }

        if (critique.Reopenings is { Count: > 0 })
        {
            entry.AppendLine().AppendLine("### Reopening proposals");
            foreach (var reopening in critique.Reopenings)
                entry.Append("- ").Append(reopening.FindingId).Append(" — ")
                     .AppendLine(reopening.Evidence);
        }

        entry.AppendLine();
        return entry.ToString();
    }

    private static void AppendBuildResult(StringBuilder entry, BuildResult result)
    {
        if (result.GateMode is not null) entry.Append("gateMode: ").AppendLine(result.GateMode);
        entry.Append("Status: ").Append(result.Status).AppendLine()
             .AppendLine()
             .Append("Verification: ").Append(result.Verification.Outcome)
             .Append(" — ").Append(result.Verification.Evidence).AppendLine()
             .AppendLine();

        if (result.Gate is { } gate) AppendGate(entry, gate);

        entry.AppendLine(result.Summary)
             .AppendLine();

        foreach (var file in result.FilesChanged)
            entry.Append("- `").Append(file).AppendLine("`");

        if (result.FilesChanged.Count > 0) entry.AppendLine();
    }

    /// <summary>
    /// The host's own line beside the builder's verification, so a reader of the timeline sees
    /// which of the two decided the task. The output travels only when the gate did not pass:
    /// that is when someone has to read it.
    /// </summary>
    /// <param name="entry">The timeline entry receiving the gate result.</param>
    /// <param name="gate">The host's gate result.</param>
    private static void AppendGate(StringBuilder entry, GateRun gate)
    {
        var label = gate.Label is "Gate" or "Fix gate" ? gate.Label : "Gates " + gate.Label;
        entry.Append(label).Append(": ").Append(gate.Outcome.Replace('_', ' ')).Append(" — ");

        switch (gate.Outcome)
        {
            case "passed":
                entry.Append(Inline(gate.Command)).Append(" exited 0 in ").Append(gate.Seconds?.ToString("0")).AppendLine(" s");
                break;
            case "failed":
                entry.Append(Inline(gate.Command))
                     .Append(gate.ExitCode is { } code ? $" exited {code}" : " did not run")
                     .Append(" after ").Append(gate.Seconds?.ToString("0")).AppendLine(" s");
                break;
            case "timeout":
                entry.Append(Inline(gate.Command)).Append(' ').AppendLine(gate.Detail);
                break;
            default:
                entry.AppendLine(gate.Detail);
                break;
        }

        entry.AppendLine();

        if (gate.Outcome is "failed" or "timeout" && gate.Output is { Length: > 0 })
            entry.AppendLine("```text")
                 .AppendLine(gate.Output)
                 .AppendLine("```")
                 .AppendLine();
    }

    // A one-line command reads inline; a script keeps its lines, each in its own span.
    private static string Inline(string? command) =>
        command is null ? string.Empty
        : command.Contains('\n') ? string.Join(" · ", command.Split('\n').Select(line => $"`{line}`"))
        : $"`{command}`";
}

// The code-review defaults keep state files written before the counters existed readable; the cap
// default matches what forge.begin writes today. The granted-round defaults do the same for state
// files written before the user could buy a round past either cap, and the null gate settings for
// runs begun before the server ran gates at all. A run with no tallies yet asks at the cap, as every
// run did before rounds could continue automatically.
/// <summary>Everything a run keeps between tool calls, stored as the run folder's <c>state.json</c>.</summary>
/// <param name="RunId">The run's id, which names its folder under <c>.forge/</c>.</param>
/// <param name="WorkspaceRoot">The git window the run reviews and the workers' working directory.</param>
/// <param name="Profile">The capability profile of the host that began the run.</param>
/// <param name="StartedAt">When <c>forge.begin</c> opened the run.</param>
/// <param name="ReviewRounds">Plan-review rounds run so far.</param>
/// <param name="ReviewRoundCap">How many plan-review rounds run without the user granting another.</param>
/// <param name="BaselineHead">The commit <c>forge.begin</c> took as the baseline.</param>
/// <param name="Approved">Whether the plan as it stands was approved; a write or a round after it takes this back.</param>
/// <param name="TasksCompleted">Plan tasks counted done; the next build is the one after them.</param>
/// <param name="BuilderSessionId">The recorded builder session's resume token, empty when there is none.</param>
/// <param name="BuilderVendor">The vendor that ran the recorded builder session.</param>
/// <param name="CodeReviewRounds">Code-review rounds run so far.</param>
/// <param name="CodeReviewRoundCap">How many code-review rounds run without the user granting another.</param>
/// <param name="GrantedReviewRounds">Plan-review rounds the user granted past the cap.</param>
/// <param name="GrantedCodeReviewRounds">Code-review rounds the user granted past the cap.</param>
/// <param name="GateEnvironment">Environment variables every gate command runs with, from <c>forge.plan.confirm</c>.</param>
/// <param name="BuilderRoots">Paths outside the workspace the builder may write to, from <c>forge.plan.confirm</c>; codex-only today.</param>
/// <param name="PendingGateFailure">
/// What the last gate run said when it failed — or what the last builder turn left running when
/// it ended — handed to the next builder turn and cleared by the first gate that passes. Null
/// while nothing is owed.
/// </param>
/// <param name="WorkerTools">
/// Patterns naming the MCP servers every worker of the run may call unasked, from
/// <c>forge.begin</c>. Null for a run begun before the setting existed, which gets the default.
/// </param>
/// <param name="CriticInstructions">The user's text for the run's critics, from <c>forge.instructions.set</c>.</param>
/// <param name="BuilderInstructions">The user's text for the run's builder, from <c>forge.instructions.set</c>.</param>
/// <param name="Scout">The lazy Scout choice and its current continuation state, or null before the choice is made.</param>
/// <param name="ScoutAnswers">How many Scout answers have consumed a number, which numbers the next one.</param>
/// <param name="BuilderSessionScope">
/// The plan task or code-review round <see cref="BuilderSessionId"/> belongs to; a builder turn of
/// any other scope starts a session of its own. Null for a run begun before sessions had scopes,
/// whose next builder turn therefore starts fresh.
/// </param>
/// <param name="TaskChanges">
/// The files each completed task reported changing, which a task's fresh session is told about.
/// Trimmed with the progress to the tasks that stay built when a change to the plan sends the build back.
/// </param>
/// <param name="FullGate">When the orchestrator verifies targeted fixes: beforeNextRound or final.</param>
/// <param name="AutoContinuedReviewRounds">Plan-review rounds that ran past the cap without asking, because the findings fell.</param>
/// <param name="AutoContinuedCodeReviewRounds">Code-review rounds that ran past the cap without asking, because the findings fell.</param>
/// <param name="ReviewTallies">What each plan-review round left for the cap, oldest first; null before the first tallied round.</param>
/// <param name="CodeReviewTallies">What each code-review round left for the cap, oldest first; null before the first tallied round.</param>
/// <param name="SplitCheckpoint">
/// The Approach size the plan's growth is measured from for the split offer: where the last offer was
/// owed, or the latest draft's before round 1. Null before any plan was measured, and for a run begun
/// before the checkpoint existed, which is offered nothing until its next write or patch sets one.
/// </param>
internal sealed record RunState(string RunId,
                                string WorkspaceRoot,
                                string Profile,
                                DateTimeOffset StartedAt,
                                int ReviewRounds,
                                int ReviewRoundCap,
                                string BaselineHead = "",
                                bool Approved = false,
                                int TasksCompleted = 0,
                                string BuilderSessionId = "",
                                string BuilderVendor = "",
                                int CodeReviewRounds = 0,
                                int CodeReviewRoundCap = 3,
                                int GrantedReviewRounds = 0,
                                int GrantedCodeReviewRounds = 0,
                                IReadOnlyDictionary<string, string>? GateEnvironment = null,
                                IReadOnlyList<string>? BuilderRoots = null,
                                string? PendingGateFailure = null,
                                IReadOnlyList<string>? WorkerTools = null,
                                string? CriticInstructions = null,
                                string? BuilderInstructions = null,
                                ScoutState? Scout = null,
                                int ScoutAnswers = 0,
                                string? BuilderSessionScope = null,
                                IReadOnlyList<TaskChange>? TaskChanges = null,
                                string FullGate = FixGatePolicy.BeforeNextRound,
                                int AutoContinuedReviewRounds = 0,
                                int AutoContinuedCodeReviewRounds = 0,
                                IReadOnlyList<ReviewTally>? ReviewTallies = null,
                                IReadOnlyList<ReviewTally>? CodeReviewTallies = null,
                                int? SplitCheckpoint = null);

internal sealed record TaskChange(int TaskNumber, IReadOnlyList<string> FilesChanged);

/// <summary>What one review round left for the cap: how many findings were open after it, and whether one was a blocker.</summary>
/// <param name="Findings">The round's new findings plus the unresolved findings it assessed as still present.</param>
/// <param name="Blocker">Whether any of those is a <c>blocker</c>.</param>
internal sealed record ReviewTally(int Findings, bool Blocker);

internal sealed record ScoutState(bool Enabled,
                                  string? Vendor = null,
                                  string? Model = null,
                                  string? Effort = null,
                                  string? SessionId = null,
                                  ScoutFailure? LastFailure = null,
                                  bool Fast = false);

internal sealed class RunNotFoundException(string runId) : Exception($"run {runId} was not found");

internal sealed class RunEscapedException(string runId)
    : Exception($"run id {runId} resolves outside .forge/");

internal sealed class WorkspaceNotRootedException(string workspaceRoot)
    : Exception($"workspaceRoot must be an absolute path, and '{workspaceRoot}' is not");

// A tool argument the server refuses: an act that does not exist, an argument an act does not take,
// a job id that is not one. A type of ours rather than ArgumentException because the SDK blanks a
// framework exception's message on the wire, and these messages are written for the orchestrator.
internal sealed class ArgumentRejectedException(string message) : Exception(message);

// Reflection-based serialization is off repo-wide (Directory.Build.props), so every persisted
// shape needs a source-generated contract.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RunState))]
[JsonSerializable(typeof(string))]
internal sealed partial class ForgeJson : JsonSerializerContext
{
    /// <summary>For the file a person opens: non-ASCII text as written, not <c>\uXXXX</c>.</summary>
    internal static ForgeJson Readable =>
        field ??= new(new JsonSerializerOptions(Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
