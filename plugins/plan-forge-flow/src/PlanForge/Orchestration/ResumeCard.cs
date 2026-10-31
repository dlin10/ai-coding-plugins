using System.Globalization;
using System.Text;
using System.Text.Json;
using PlanForge.Acts;
using PlanForge.Infrastructure;
using PlanForge.Prompts;
using PlanForge.Run;

namespace PlanForge.Orchestration;

/// <summary>
/// The resume card: <c>planforge.exe --resume-card [&lt;directory&gt;]</c>, the binary's third entry point,
/// which serves no MCP. The plugin's Claude Code and Codex <c>SessionStart</c> hooks run it after a
/// context compaction and the host adds what it prints to the compacted context, so an Orchestrator
/// working from its own summary is told where its Run stands, which part of the forge skill to read
/// again, and the exact argument shapes of the calls ahead — the shapes a summary paraphrased wrongly
/// in run B3c. See docs/adr/0040.
/// </summary>
/// <remarks>
/// <para>
/// It prints only for a Run whose open Orchestrator phase names this Host session — Claude Code's
/// <c>CLAUDE_CODE_SESSION_ID</c>, or the <c>session_id</c> Codex hands its hook, the root thread id —
/// and nothing otherwise: an old Run in the same folder would follow every compaction of every session
/// there, and a Run that declared <c>done</c> keeps no session. It prints IDs, counts, names and shapes —
/// never plan text, finding text, instructions or a gate environment value — and only ASCII of its own,
/// because a hook runs it through PowerShell, which may decode a native program's output in the
/// console's code page.
/// </para>
/// <para>
/// It always exits 0 once its arguments parse: a failing hook shows the user an error notice and
/// hands the Orchestrator nothing, which is the state the card exists to end.
/// </para>
/// </remarks>
internal static class ResumeCard
{
    private const string COMMAND = "--resume-card";
    private const string TOOL_CALL = "\"event\":\"tool.call\"";

    /// <summary>The longest ID list printed in full; a longer one points at <c>forge.status</c>.</summary>
    private const int MAX_IDS = 40;

    /// <summary>Whether <paramref name="args"/> ask for the resume card rather than the MCP server.</summary>
    /// <param name="args">The process's arguments.</param>
    public static bool IsCommand(string[] args) => args is [COMMAND, ..];

    /// <summary>
    /// Prints the card for this Host session's Run under <c>&lt;directory&gt;/.forge</c>, if it has one. Claude
    /// Code's hook names the directory and leaves the session in <c>CLAUDE_CODE_SESSION_ID</c>; Codex's hook
    /// passes no directory, and the session and directory come from the JSON Codex writes to the hook's
    /// stdin, whose <c>session_id</c> is the root thread id the server records for the Run.
    /// </summary>
    /// <param name="args"><c>--resume-card</c>, then the absolute directory the Host session's Runs live in, or nothing.</param>
    /// <param name="input">The hook's stdin, read only when no directory is given.</param>
    /// <param name="output">Receives the card.</param>
    /// <param name="environment">Reads the process environment: the Host session and the prompts folder.</param>
    /// <returns>0 once the arguments or the hook input parse, printed or not; 2 when they do not.</returns>
    public static int Run(string[] args, TextReader input, TextWriter output, Func<string, string?> environment)
    {
        string root;
        string? session;
        switch (args)
        {
            case [COMMAND, var directory] when Path.IsPathRooted(directory):
                (root, session) = (directory, environment(HostSessions.CLAUDE_SESSION_VARIABLE));
                break;
            case [COMMAND] when HookInput.Read(input) is { } hook:
                (root, session) = (hook.Cwd, hook.SessionId);
                break;
            default:
                return 2;
        }

        var prompts = PromptLibrary.Locate(environment(PromptLibrary.RootVariable));
        var references = Path.Combine(Path.GetDirectoryName(prompts) ?? prompts, "skills", "forge", "references");
        output.Write(Of(root, session, Directory.Exists(references) ? references : null));
        return 0;
    }

    /// <summary>The card for <paramref name="sessionId"/>'s newest Run under <paramref name="root"/>, or empty when it has none.</summary>
    /// <param name="root">The directory whose <c>.forge</c> folder holds the Runs.</param>
    /// <param name="sessionId">The Host session, or null when the host names none.</param>
    /// <param name="references">The forge skill's <c>references</c> folder, or null when it cannot be found.</param>
    internal static string Of(string root, string? sessionId, string? references)
    {
        if (sessionId is not { Length: > 0 }) return "";

        var forge = Path.Combine(root, RunDirectory.FORGE_FOLDER);
        if (!Directory.Exists(forge)) return "";

        var (runId, phase) = Directory.EnumerateDirectories(forge)
                                      .Select(path => (Id: Path.GetFileName(path), Phase: OpenPhase(path)))
                                      .Where(candidate => candidate.Phase?.HostSessionId == sessionId)
                                      .OrderByDescending(candidate => candidate.Id, StringComparer.Ordinal)
                                      .FirstOrDefault();
        if (phase is null) return "";

        var run = RunDirectory.Open(root, runId);
        try
        {
            return Card(run, phase, references);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return $"Plan Forge Flow: your context was compacted during forge run {runId} ({run.Path}), and its files "
                   + $"could not be read for a resume card ({error.GetType().Name}: {error.Message}). Call forge.status for this run "
                   + "before any other forge call, and read the forge skill's reference for the phase you are in.\n";
        }
    }

    private static string Card(RunDirectory run, OrchestratorPhaseFile phase, string? references)
    {
        var state = run.ReadState();
        var plan = File.Exists(run.PlanPath) ? run.ReadPlan() : null;
        var hash = PlanOutline.IsBlank(plan) ? null : PlanOutline.Hash(plan!);
        var tasks = hash is null ? null : TaskCount(plan!);
        var ledger = run.ReadDecisionLedger().Summary;
        var (critic, builder) = LastChoices(run);
        var stand = hash is null ? Stand.Planning
                    : !state.Approved ? Stand.PlanReview
                    : tasks is { } built && state.TasksCompleted < built ? Stand.Build
                    : Stand.CodeReview;

        var card = new StringBuilder()
            .Append("Plan Forge Flow resume card. Your context was compacted during forge run ").Append(state.RunId)
            .AppendLine(". These lines come from the run's own files, not from your summary: where the two disagree, these are right. "
                        + "They hold IDs and argument shapes only; read the plan from PLAN.md and findings from forge.status or the Flow log, never from memory.")
            .AppendLine()
            .Append("- Run folder: ").AppendLine(run.Path)
            .Append("- workspaceRoot: ").Append(state.WorkspaceRoot).Append("; runId: ").Append(state.RunId)
            .AppendLine(". Every forge call takes both; the shapes below leave them out.")
            .Append("- Last declared phase: ").Append(phase.Phase).Append(", opened ").Append(phase.OpenedAtLocal)
            .AppendLine(Declared(stand).Contains(phase.Phase)
                            ? ". Declare each phase as it begins, with forge.log.append's phase argument."
                            : ". The run has moved past it: declare the phase you are in with forge.log.append before the next act, and each later one as it begins.")
            .AppendLine(hash is null
                            ? "- Plan: none written yet."
                            : $"- Plan: PLAN.md, planHash {hash}, {(tasks is { } count ? $"{count} tasks" : "tasks not parseable yet")}.")
            .AppendLine(state.Approved
                            ? $"- Approved: yes. Tasks built: {state.TasksCompleted} of {tasks?.ToString(CultureInfo.InvariantCulture) ?? "?"}."
                            : "- Approved: no.")
            .Append("- Plan review: ").Append(Rounds(state.ReviewRounds, state.ReviewRoundCap, state.GrantedReviewRounds,
                                                     state.AutoContinuedReviewRounds))
            .Append("; code review: ").Append(Rounds(state.CodeReviewRounds, state.CodeReviewRoundCap, state.GrantedCodeReviewRounds,
                                                     state.AutoContinuedCodeReviewRounds))
            .Append("; saved fullGate: ").Append(state.FullGate).AppendLine(".");
        if (state.PendingGateFailure is not null)
            card.AppendLine("- A gate failure is pending: the next builder turn retries with its output.");
        if (state.GateEnvironment is { Count: > 0 } environment)
            card.Append("- Gate environment holds: ").Append(string.Join(", ", environment.Keys.Order(StringComparer.Ordinal))).AppendLine(" (names only).");
        card.Append("- Critic: ").AppendLine(critic is null ? "not used yet." : critic.Arguments + ", as last passed.")
            .Append("- Builder: ").AppendLine(builder is null ? "not used yet." : builder.Arguments + ", as last passed.")
            .Append("- Scout: ").AppendLine(state.Scout switch
            {
                null => "not chosen yet.",
                { Enabled: false } => "declined for this run.",
                { } scout => new WorkerChoice(scout.Vendor, scout.Model ?? "?", scout.Effort, scout.Fast).Arguments
                             + ", as selected; every call passes sessionMode explicitly."
            });
        var decisions = run.ReadDecisions();
        var widenings = DecisionRecords.Widenings(decisions);
        card.AppendLine(decisions.Count == 0
                            ? "- Decisions: none recorded yet."
                            : $"- Decisions: {decisions.Count} recorded in {run.DecisionsPath}. Read them there, not in your summary, "
                              + "before you ask the user anything the run may have settled."
                              + (widenings == 0 ? "" : $" Widenings since the interview: {widenings}."));

        AppendIds(card, "Unresolved", ledger.UnresolvedFindingIds);
        AppendIds(card, "  of which active in plan review", ledger.PlanReviewActiveFindingIds.Intersect(ledger.UnresolvedFindingIds).ToArray());
        AppendIds(card, "  of which active in code review", ledger.CodeReviewActiveFindingIds.Intersect(ledger.UnresolvedFindingIds).ToArray());
        AppendIds(card, "Deferred", ledger.DeferredFindingIds);
        AppendIds(card, "Rejected", ledger.RejectedFindingIds);
        foreach (var attempt in ledger.PendingFullGateAttempts)
            AppendIds(card, $"Pending full host verification under fix attempt {attempt.FixAttemptId}", attempt.FindingIds);

        card.AppendLine();
        AppendNext(card, stand, state, hash, tasks, critic, builder, references);
        return card.ToString();
    }

    private static void AppendNext(StringBuilder card, Stand stand, RunState state, string? hash, int? tasks,
                                   WorkerChoice? critic, WorkerChoice? builder, string? references)
    {
        var criticArguments = critic?.Arguments ?? "model: \"<the critic's model>\", effort?, vendor?, fast?";
        var builderArguments = builder?.Arguments ?? "model: \"<the builder's model>\", effort?, vendor?, fast?";

        string[] read;
        card.Append("Where the run stands: ");
        if (stand == Stand.Planning)
        {
            card.AppendLine("no plan is written yet: the interview, the vendor choice or the first draft.")
                .AppendLine("Next calls (? marks an optional argument):")
                .AppendLine("- forge.status {} if Scout jobs were running: activeJobs names them; forge.work.poll { jobId, waitSeconds: 3000 } "
                            + "and forge.work.fetch { jobId } for each before you use its answer.")
                .AppendLine("- forge.plan.write { planDraft: \"<the whole plan>\" }, once the Impact pass has run against the draft.");
            read = ["interview.md", "planning.md", "scout.md"];
        }
        else if (stand == Stand.PlanReview)
        {
            card.AppendLine("plan review, not approved. Revise and run the next round, or ask for approval once the critique settles.")
                .AppendLine("Next calls (? marks an optional argument):")
                .AppendLine("- forge.plan.outline {} for the targets s0, s1, ... and their targetHash.")
                .Append("- forge.plan.patch { baseHash: \"").Append(hash).AppendLine("\", edits: [{ op: \"replace\" | \"delete\" | \"insertBefore\" | \"insertAfter\", "
                                                                                   + "target: \"s3\", targetHash: \"<from the outline>\", content?: \"<none for delete>\" }] }; "
                                                                                   + "forge.plan.write { planDraft } only for a rewrite.")
                .Append("- forge.plan.review { ").Append(criticArguments).Append(", revision: \"<what you changed; required from round 2>\", ")
                .Append(Decisions("addressedByRevision")).AppendLine(" }; never planDraft.")
                .Append("- forge.plan.confirm { approved: true, planHash: \"").Append(hash).Append("\", decisions?: {...as above}, ")
                .AppendLine("fullGate?: \"beforeNextRound\" | \"final\", gateEnvironment?: { \"NAME\": \"value\" }, rebuildFrom?: <a built task> }; "
                            + "never plan, and only after the user said yes. approved: false takes no decisions.");
            read = ["plan-review.md", "ledger.md", "approval.md"];
        }
        else if (stand == Stand.Build)
        {
            card.Append("build, task ").Append(state.TasksCompleted + 1).Append(" of ").Append(tasks).AppendLine(" is next.")
                .AppendLine("Next calls (? marks an optional argument):")
                .AppendLine("- forge.log.append { message: \"...\", phase: \"build\" } before each build act, a retry included.")
                .Append("- forge.build.next { ").Append(builderArguments).AppendLine(" }, once per task until every task is built.")
                .Append("- A task expected to run past the host's hour: forge.work.start { act: \"build.next\", ").Append(builderArguments)
                .AppendLine(" }, then forge.work.poll { jobId, waitSeconds: 3000 } until it is no longer running, then forge.work.fetch { jobId }.");
            read = ["build.md"];
        }
        else
        {
            card.AppendLine("every task is built: code review, or its wrap-up.")
                .AppendLine("Next calls (? marks an optional argument):")
                .AppendLine("- forge.status {} for the pending full-gate IDs and the drift.")
                .Append("- forge.review.code { ").Append(criticArguments).AppendLine(", userGrantedRound?: true only after the user said yes at the cap, excludePaths?: [\"...\"] }.")
                .Append("- forge.review.fix { ").Append(builderArguments).Append(", ")
                .Append(Decisions("hostVerified", ", raises?: [{ severity: \"blocker\" | \"major\" | \"minor\", where, what, by, reason }]"))
                .AppendLine(", fixAttemptId?: \"<one per logical fix>\", fixFindingIds?: [\"F-...\"], note?: \"...\", gate?: \"full\" | \"targeted\" }. "
                            + "Without fixFindingIds it is decisions-only and starts no Builder; a batch with raises carries no fix; a retry keeps the attempt, the IDs and the gate.")
                .AppendLine("- forge.log.append { message: \"...\", phase: \"code_review\" | \"wrap_up\" | \"done\" } as each begins.");
            read = ["code-review.md", "ledger.md"];
        }

        card.AppendLine("- forge.decision.record { phase: \"<the phase you are in>\", question: \"<as asked>\", answer: \"<as given>\", "
                        + "by: \"user\" | \"orchestrator\", options?: [{ label, recommended?: true }], bearsOn?: \"R3, F-0012\", "
                        + "supersedes?: \"D-...\", scope?: \"widens\" | \"narrows\" } the moment an answer settles something; "
                        + "never forge.log.append for it.");
        card.Append("Before your next forge call, read ")
            .Append(string.Join(", ", read.Select(file => references is null ? $"references/{file}" : Path.Combine(references, file))))
            .AppendLine(references is null
                            ? " in the forge skill's folder: the part of the skill for this phase, which compaction dropped."
                            : ": the part of the forge skill for this phase, which compaction dropped.");
    }

    /// <summary>The Orchestrator phases a Run standing at <paramref name="stand"/> can be in.</summary>
    /// <param name="stand">Where the Run stands.</param>
    private static string[] Declared(Stand stand) => stand switch
    {
        Stand.Planning => ["interview", "planning"],
        Stand.PlanReview => ["planning", "plan_revision", "approval"],
        Stand.Build => ["approval", "build", "plan_revision"],
        _ => ["build", "code_review", "wrap_up", "plan_revision"]
    };

    /// <summary>The open phase in a Run folder's phase file, or null when it holds none or cannot be read.</summary>
    /// <param name="runPath">The Run folder.</param>
    private static OrchestratorPhaseFile? OpenPhase(string runPath)
    {
        try
        {
            var path = Path.Combine(runPath, OrchestratorPhases.FILE_NAME);
            if (!File.Exists(path)) return null;
            var file = JsonSerializer.Deserialize(AtomicFile.Read(path), OrchestratorPhaseJson.Default.OrchestratorPhaseFile);
            return file is { State: OrchestratorPhases.OPEN } ? file : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The critic's and the builder's selection as each was last passed to a worker act. The Run keeps
    /// them nowhere else: <c>forge.log</c> records every tool call with its arguments.
    /// </summary>
    /// <param name="run">The Run.</param>
    private static (WorkerChoice? Critic, WorkerChoice? Builder) LastChoices(RunDirectory run)
    {
        WorkerChoice? critic = null, builder = null;
        if (!File.Exists(run.DiagnosticLogPath)) return (null, null);

        foreach (var line in AtomicFile.Read(run.DiagnosticLogPath).Split('\n'))
        {
            if (!line.Contains(TOOL_CALL, StringComparison.Ordinal)) continue;
            try
            {
                using var entry = JsonDocument.Parse(line);
                var fields = entry.RootElement.GetProperty("fields");
                var act = Text(fields, "tool") switch
                {
                    "forge.work.start" => Text(fields, "act"),
                    { } tool => tool.Replace("forge.", "", StringComparison.Ordinal),
                    null => null
                };
                if (Text(fields, "model") is not { } model) continue;

                var choice = new WorkerChoice(Text(fields, "vendor"), model, Text(fields, "effort"), Text(fields, "fast") == "true");
                if (act is "plan.review" or "review.code") critic = choice;
                else if (act is "build.next" or "review.fix") builder = choice;
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            {
            }
        }

        return (critic, builder);
    }

    /// <summary>The <c>decisions</c> argument of a review tool, as one shape.</summary>
    /// <param name="closure">The closing action this phase takes: addressedByRevision for plans, hostVerified for code.</param>
    /// <param name="raises">What the batch takes beside its decisions, or empty.</param>
    private static string Decisions(string closure, string raises = "") =>
        "decisions?: { decisionBatchId: \"<a new id per logical set; the same id and bytes on a retry>\", "
        + $"decisions: [{{ findingId: \"F-0001\", action: \"defer\" | \"reject\" | \"{closure}\" | \"duplicateOf\" | \"accept\" | \"decline\", "
        + "by: \"user\" | \"orchestrator\", reason: \"...\", duplicateOf?: \"F-...\", evidence?: \"<required for "
        + (closure == "addressedByRevision" ? "accept, and for addressedByRevision of a blocker or major" : "hostVerified and accept")
        + $">\" }}]{raises} }}";

    private static string? Text(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? TaskCount(string plan)
    {
        try
        {
            return PlanTasks.Parse(plan).Count;
        }
        catch (Exception error) when (error is PlanShapeException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static string Rounds(int rounds, int cap, int granted, int automatic) =>
        $"{rounds} rounds, cap {cap}" + (granted > 0 ? $" + {granted} granted" : "")
                                       + (automatic > 0 ? $" + {automatic} automatic" : "");

    private static void AppendIds(StringBuilder card, string label, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return;

        card.Append("- ").Append(label).Append(": ").Append(string.Join(", ", ids.Take(MAX_IDS)));
        if (ids.Count > MAX_IDS) card.Append(", and ").Append(ids.Count - MAX_IDS).Append(" more; forge.status lists them all");
        card.AppendLine(".");
    }

    /// <summary>What a Codex hook is handed on stdin, as far as the card needs it.</summary>
    /// <param name="SessionId">The session, the root thread's id.</param>
    /// <param name="Cwd">The session's absolute working directory.</param>
    private sealed record HookInput(string SessionId, string Cwd)
    {
        /// <summary>The input, or null when it is not a JSON object with a session and an absolute directory.</summary>
        /// <param name="input">The hook's stdin.</param>
        public static HookInput? Read(TextReader input)
        {
            try
            {
                using var json = JsonDocument.Parse(input.ReadToEnd());
                return Text(json.RootElement, "session_id") is { Length: > 0 } session
                       && Text(json.RootElement, "cwd") is { } cwd && Path.IsPathRooted(cwd)
                    ? new HookInput(session, cwd)
                    : null;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>Where a Run stands, read from its files: what decides the next call.</summary>
    private enum Stand
    {
        Planning,
        PlanReview,
        Build,
        CodeReview
    }

    /// <summary>A worker selection as a tool call takes it.</summary>
    /// <param name="Vendor">The vendor, or null for the default, claude.</param>
    /// <param name="Model">The model.</param>
    /// <param name="Effort">The effort, or null when none was passed.</param>
    /// <param name="Fast">Whether the act ran at the Fast tier.</param>
    private sealed record WorkerChoice(string? Vendor, string Model, string? Effort, bool Fast)
    {
        /// <summary>The selection as a tool call's arguments.</summary>
        public string Arguments =>
            $"model: \"{Model}\"" + (Effort is null ? "" : $", effort: \"{Effort}\"") + $", vendor: \"{Vendor ?? "claude"}\", fast: {(Fast ? "true" : "false")}";
    }
}
