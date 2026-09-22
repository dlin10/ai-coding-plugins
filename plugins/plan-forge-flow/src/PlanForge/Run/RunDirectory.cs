using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;
using PlanForge.Repo;
using PlanForge.Vendors;

namespace PlanForge.Run;

/// <summary>One run's state, isolated under <c>.forge/&lt;runId&gt;/</c>.</summary>
internal sealed class RunDirectory
{
    private const string ForgeFolder = ".forge";
    private const string StateFileName = "state.json";
    private const string FlowLogFileName = "flow_log.md";
    private const string DiagnosticLogFileName = "forge.log";
    private const string TelemetryFileName = "telemetry.json";
    private const string JobsFolder = "jobs";
    private const string BaselineFileName = "baseline.patch";
    private const string PlanFileName = "PLAN.md";
    private const string ScoutReportFileName = "SCOUT.md";
    private const string DecisionLedgerFileName = "decision-ledger.json";

    // The folder ignores itself, so no managed block in info/exclude and nothing to clean up.
    private const string SelfIgnore = "*\n";

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

        var jobsPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, JobsFolder));
        var jobPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(jobsPath, jobId + ".json"));
        var prefix = jobsPath.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!jobPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentRejectedException("job id resolves outside the run's jobs folder");

        return jobPath;
    }

    internal IEnumerable<string> EnumerateJobFiles()
    {
        var jobsPath = System.IO.Path.Combine(Path, JobsFolder);
        return Directory.Exists(jobsPath) ? Directory.EnumerateFiles(jobsPath, "*.json") : [];
    }

    public string FlowLogPath => System.IO.Path.Combine(Path, FlowLogFileName);

    /// <summary>
    /// The run's operational log. Append-only and the one file under <c>.forge/</c> that agents may
    /// add to, through <c>forge.log.append</c> rather than by hand.
    /// </summary>
    public string DiagnosticLogPath => System.IO.Path.Combine(Path, DiagnosticLogFileName);

    public RunLog Log => new(DiagnosticLogPath);

    internal string TelemetryPath => System.IO.Path.Combine(Path, TelemetryFileName);

    /// <summary>
    /// The run's plan as it currently stands, approved or not. Public because the path travels out
    /// with every act result: the plan is written from the first review round on, so the user can
    /// watch it change rather than meeting it once at approval.
    /// </summary>
    public string PlanPath => System.IO.Path.Combine(Path, PlanFileName);

    /// <summary>The complete structured answer to the most recent Scout question.</summary>
    public string ScoutReportPath => System.IO.Path.Combine(Path, ScoutReportFileName);

    internal string DecisionLedgerPath => System.IO.Path.Combine(Path, DecisionLedgerFileName);

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
        AtomicFile.Write(System.IO.Path.Combine(runRoot, ForgeFolder, ".gitignore"), SelfIgnore);
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

        var forgeRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(runRoot, ForgeFolder));
        var runPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(forgeRoot, runId));

        var prefix = forgeRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!runPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new RunEscapedException(runId);

        return runPath;
    }

    public RunState ReadState()
    {
        var json = AtomicFile.Read(System.IO.Path.Combine(Path, StateFileName));
        return JsonSerializer.Deserialize(json, ForgeJson.Default.RunState)
            ?? throw new RunNotFoundException(RunId);
    }

    public void WriteState(RunState state)
    {
        var json = JsonSerializer.Serialize(state, ForgeJson.Default.RunState);
        AtomicFile.Write(System.IO.Path.Combine(Path, StateFileName), json);
    }

    public void WriteBaseline(Baseline baseline) =>
        AtomicFile.Write(System.IO.Path.Combine(Path, BaselineFileName), baseline.Diff);

    public Baseline ReadBaseline(string head)
    {
        var path = System.IO.Path.Combine(Path, BaselineFileName);
        return new Baseline(head, File.Exists(path) ? AtomicFile.Read(path) : string.Empty);
    }

    /// <summary>
    /// Whole-file replacement, which is what lets a review round write the draft before the critic
    /// runs: a round that dies and is retried with the same arguments writes the same bytes again,
    /// unlike the log appends that have to wait for the critique.
    /// </summary>
    public void WritePlan(string plan) => AtomicFile.Write(PlanPath, plan);

    public string ReadPlan() => AtomicFile.Read(PlanPath);

    /// <summary>Atomically replaces the latest Scout snapshot.</summary>
    public void WriteScoutReport(string report) => AtomicFile.Write(ScoutReportPath, report);

    /// <summary>
    /// The user-facing timeline of the delegated acts, one file for the orchestrator to surface in
    /// whatever panel the host has. It is never fed back to a worker, which
    /// is why builder entries can live here without shifting what the next round's critic judges.
    /// </summary>
    public void AppendFlowCritique(string act, int round, Critique critique) =>
        AtomicFile.Append(FlowLogPath, CritiqueEntry($"## {act} — round {round}", critique));

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

    public void AppendFlowDecisionBatch(string act, DecisionBatchResponse response)
    {
        if (response.Outcome == "no_op") return;

        var result = response.Result;
        AtomicFile.Append(FlowLogPath,
            new StringBuilder().Append("## ").Append(act).Append(" — decisions ")
                               .AppendLine(response.Outcome)
                               .AppendLine()
                               .Append("decisionBatchId: ").AppendLine(result.DecisionBatchId)
                               .Append("decisions: ").AppendLine(string.Join(", ", result.DecisionFindingIds))
                               .Append("reopenings: ").AppendLine(string.Join(", ", result.ReopenedFindingIds))
                               .Append("closures: ").AppendLine(string.Join(", ", result.ClosedFindingIds))
                               .Append("canonical: ").AppendLine(response.ExistingBatch is { } existing
                                   ? Encoding.UTF8.GetString(existing.CanonicalBytes)
                                   : response.CanonicalBytes is { } bytes
                                       ? Encoding.UTF8.GetString(bytes)
                                       : "(not persisted)")
                               .AppendLine()
                               .ToString());
    }

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
        if (failure is null)
            entry.AppendLine("The latest Scout report was replaced.");
        else
            entry.Append("Failure: ").Append(failure.Code).Append(" — ").AppendLine(failure.Summary);

        AtomicFile.Append(FlowLogPath, entry.AppendLine().ToString());
    }

    /// <summary>
    /// A review round run against an already-approved plan takes the approval back, and the user
    /// meets that as a build refusing for no visible reason unless it is in the timeline. The
    /// entry names the task count that went with it, because that is the part that costs money to
    /// rebuild.
    /// </summary>
    public void AppendFlowReopened(int tasksCompleted) =>
        AtomicFile.Append(FlowLogPath,
            new StringBuilder().AppendLine("## Plan reopened")
                               .AppendLine()
                               .Append("A review round ran against the approved plan, so the approval no longer holds. ")
                               .Append("Build progress was reset from ").Append(tasksCompleted)
                               .AppendLine(tasksCompleted == 1 ? " completed task." : " completed tasks.")
                               .AppendLine()
                               .Append("The plan has to be approved again before the builder will run, ")
                               .AppendLine("and it will start from the first task.")
                               .AppendLine()
                               .ToString());

    /// <summary>
    /// Records that a round ran past its cap because the user granted it, so a reader of
    /// <c>flow_log.md</c> sees the round as bought rather than budgeted.
    /// </summary>
    public void AppendFlowGrantedRound(string act, int round) =>
        AtomicFile.Append(FlowLogPath,
            new StringBuilder().Append("## ").Append(act).AppendLine(" — extra round granted")
                               .AppendLine()
                               .Append("Round ").Append(round)
                               .AppendLine(" runs because the user granted it past the cap.")
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

    public void AppendFlowBuild(int number, int total, BuildResult result)
    {
        var entry = new StringBuilder().Append("## Task ").Append(number).Append(" of ").Append(total).AppendLine()
                                       .AppendLine();

        AppendBuildResult(entry, result);
        AtomicFile.Append(FlowLogPath, entry.ToString());
    }

    public void AppendFlowFix(int round, string findings, string? deferred, BuildResult result)
    {
        var entry = new StringBuilder().Append("## Fixes — round ").Append(round).AppendLine()
                                       .AppendLine();

        if (findings.Trim().Length > 0)
            entry.AppendLine(findings.TrimEnd())
                 .AppendLine();

        if (deferred is { Length: > 0 })
            entry.AppendLine("### Deferred by the orchestrator")
                 .AppendLine()
                 .AppendLine(deferred.TrimEnd())
                 .AppendLine();

        AppendBuildResult(entry, result);
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
        AtomicFile.Append(FlowLogPath, entry.ToString());
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
    private static void AppendGate(StringBuilder entry, GateRun gate)
    {
        var label = gate.Label == "Gate" ? "Gate" : "Gates " + gate.Label;
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
// runs begun before the server ran gates at all.
/// <param name="GateEnvironment">Environment variables every gate command runs with, from <c>forge.begin</c>.</param>
/// <param name="BuilderRoots">Paths outside the workspace the builder may write to, from <c>forge.begin</c>; codex-only today.</param>
/// <param name="PendingGateFailure">
/// What the last gate run said when it failed — or what the last builder turn left running when
/// it ended — handed to the next builder turn and cleared by the first gate that passes. Null
/// while nothing is owed.
/// </param>
/// <param name="WorkerTools">
/// Patterns naming the MCP servers every worker of the run may call unasked, from
/// <c>forge.begin</c>. Null for a run begun before the setting existed, which gets the default.
/// </param>
/// <param name="Scout">The lazy Scout choice and its current continuation state, or null before the choice is made.</param>
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
                                ScoutState? Scout = null);

internal sealed record ScoutState(bool Enabled,
                                  string? Vendor = null,
                                  string? Model = null,
                                  string? Effort = null,
                                  string? SessionId = null,
                                  ScoutFailure? LastFailure = null);

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
internal sealed partial class ForgeJson : JsonSerializerContext;
