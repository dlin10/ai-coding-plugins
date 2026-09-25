using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;
using PlanForge.Acts;
using PlanForge.Diagnostics;
using PlanForge.Jobs;
using PlanForge.Orchestration;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Mcp;

[McpServerToolType]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
internal sealed class ForgeTools
{
    private const int DEFAULT_REVIEW_ROUND_CAP = 5;
    private const int DEFAULT_CODE_REVIEW_CAP = 3;
    private const int WORK_POLL_TIMEOUT_SECONDS = 45;
    private const string SOURCE = "server";

    private const string FAST_DESCRIPTION =
        "Run the worker at the vendor's Fast tier: quicker, at a higher usage price. Only for a model and effort forge.models " +
        "lists under fastEfforts, and only when the user chose it; the server refuses a request the catalogue does not " +
        "confirm, and claude refuses one its account will not serve. Omitted means standard speed, asked for explicitly.";

    [McpServerTool(Name = "forge.begin"), Description("Starts a run, takes a working-tree baseline excluding `CONTEXT.md` and `docs/adr/**`, and returns the run id, the capability profile, and the connecting client. `workerTools` names the MCP servers every critic, builder, and Scout of the run may call without being asked; omit it for the Roslyn servers alone.")]
    public static async Task<string> Begin(McpServer server,
                                           CatalogCache catalogs,
                                           SessionRoots roots,
                                           [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                           CancellationToken ct,
                                           [Description("MCP server names, `*` matching any run of characters, that the run's workers may call without being asked — a headless worker is refused every call nothing granted. Omit for [\"roslyn-*\"]; pass [] for none. Name only servers that do not change files: Critic, Builder, and Scout get the same grant.")] string[]? workerTools = null)
    {
        var granted = WorkerTools.Effective(WorkerTools.Validate(workerTools));
        var profile = CapabilityProfileDetector.Detect(server.ClientCapabilities);
        var runId = NewRunId();
        var run = await RunDirectory.CreateAsync(roots, workspaceRoot, runId, ct);

        // The run folder has to exist before anything can be logged, so this is the one tool whose
        // record starts after its first side effect rather than before it. `sessionRoot` is null
        // for a host that declares no roots, which is the one thing the run path alone cannot say.
        return await LoggedAsync(run, "forge.begin",
            [("workspaceRoot", workspaceRoot), ("sessionRoot", await roots.DirectoryAsync(ct)),
             ("client", ClientName(server)), ("profile", profile.ToString()),
             ("workerTools", string.Join(", ", granted))],
            async () =>
            {
                // Fire-and-forget: by the time the interview reaches the vendor question,
                // forge.models finds the catalogues already fetched.
                catalogs.BeginProbing(workspaceRoot);

                var baseline = await Baseline.CaptureAsync(new GitClient(workspaceRoot), ct);
                run.WriteBaseline(baseline);
                run.WriteState(new RunState(runId, workspaceRoot, profile.ToString(), DateTimeOffset.Now,
                    ReviewRounds: 0, ReviewRoundCap: DEFAULT_REVIEW_ROUND_CAP, BaselineHead: baseline.Head,
                    CodeReviewRoundCap: DEFAULT_CODE_REVIEW_CAP, WorkerTools: granted));

                return JsonSerializer.Serialize(
                    new BeginResult(runId, run.Path, profile.ToString(), baseline.Head, ClientName(server)),
                    ForgeToolJson.Default.BeginResult);
            });
    }

    /// <summary>
    /// The clientInfo name from the MCP handshake, verbatim. The skill branches its model-selection
    /// flow on the host, and the orchestrator's own idea of where it runs is a guess; this is not.
    /// </summary>
    /// <param name="server">The MCP server carrying the negotiated client information.</param>
    private static string ClientName(McpServer server) =>
        server.ClientInfo?.Name is { Length: > 0 } name ? name : "unknown";

    /// <summary>
    /// The probes were started by <c>forge.begin</c>, so by interview time this is a cache read;
    /// a cold call probes on the spot and waits. A probe failure is a value here, not an error —
    /// the interview's reaction to a dead vendor is to drop it, not to stop.
    /// </summary>
    /// <param name="catalogs">The process-lifetime vendor catalogue cache.</param>
    /// <param name="roots">The session roots advertised by the MCP host.</param>
    /// <param name="workspaceRoot">The run's workspace root.</param>
    /// <param name="runId">The run to read.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    /// <param name="vendor">The vendor to return, or <see langword="null"/> for every vendor.</param>
    [McpServerTool(Name = "forge.models"), Description("Returns each vendor's model catalogue with effort levels per model, newest first — source `live` where the vendor publishes a list (codex, cursor), `resolved` for claude, whose remembered aliases the CLI turned into the model ids they stand for (displayName). A vendor with available:false is not usable; tell the user why and do not offer it. `fastEfforts` lists the efforts a model is offered at with the vendor's Fast tier, `fastHint` what the vendor says it costs, and `fastUnavailable` why the account will not serve it; offer Fast only where fastEfforts holds the effort and fastUnavailable is null.")]
    public static async Task<string> Models(CatalogCache catalogs,
                                            SessionRoots roots,
                                            [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                            [Description("Run id from forge.begin.")] string runId,
                                            CancellationToken ct,
                                            [Description("Vendor: claude, codex or cursor. Omit for all of them.")] string? vendor = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.models", [("vendor", vendor)],
            async () =>
            {
                string[] ids = vendor is { Length: > 0 } ? [vendor] : CatalogCache.KnownVendors;
                var reports = await Task.WhenAll(ids.Select(id => catalogs.GetAsync(id, workspaceRoot, ct)));

                return JsonSerializer.Serialize(new ModelsResult([.. reports.Select(Catalogue)]),
                    ForgeToolJson.Default.ModelsResult);
            });
    }

    private static VendorCatalogResult Catalogue(VendorCatalogReport report) =>
        new(report.Vendor,
            report.Catalog.Source.ToString().ToLowerInvariant(),
            report.Available,
            report.Detail,
            [
                .. report.Catalog.Models.Select(model => new CatalogModel(model.Id, model.DisplayName,
                    model.Description, model.Efforts, model.DefaultEffort, model.IsDefault,
                    model.FastEfforts, model.FastHint, model.FastUnavailable))
            ]);

    [McpServerTool(Name = "forge.scout.select"), Description("Records the lazy Scout decision for this run. Pass enabled:false with no vendor, model or effort to continue without Scout; pass enabled:true with a vendor and model to select it. The vendor id is checked now, while model and effort are stored exactly for the Vendor CLI to interpret. Repeating a healthy choice keeps its session anchor; changing it or selecting after a failure starts the next Scout call fresh.")]
    public static async Task<string> SelectScout(CatalogCache catalogs,
                                                 SessionRoots roots,
                                                 [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                                 [Description("Run id from forge.begin.")] string runId,
                                                 [Description("Whether this run should use Scout. This decision is required; there is no silent default.")] bool enabled,
                                                 CancellationToken ct,
                                                 [Description("Vendor id, required when enabled is true and forbidden otherwise.")] string? vendor = null,
                                                 [Description("Model string, required when enabled is true and forbidden otherwise. Stored exactly; the Vendor CLI is authoritative.")] string? model = null,
                                                 [Description("Optional effort string, forbidden when enabled is false. Stored exactly; the Vendor CLI is authoritative.")] string? effort = null,
                                                 [Description(FAST_DESCRIPTION + " Confirmed now and kept with the selection; forbidden when enabled is false.")] bool fast = false)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.scout.select",
            [("enabled", enabled ? "true" : "false"), ("vendor", vendor), ("model", model), ("effort", effort),
             ("fast", Flag(fast))],
            async () =>
            {
                var state = run.ReadState();
                var outcome = await SelectScoutStateAsync(catalogs, state.Scout, enabled, vendor, model, effort, fast,
                                                          workspaceRoot, ct);

                run.WriteState(state with { Scout = outcome.State });
                run.AppendFlowScoutSelection(outcome.Action, outcome.State);
                run.Log.Write("info", SOURCE, $"scout.{outcome.Action}",
                    ("enabled", outcome.State.Enabled ? "true" : "false"),
                    ("vendor", outcome.State.Vendor), ("model", outcome.State.Model),
                    ("effort", outcome.State.Effort), ("fast", Flag(outcome.State.Fast)),
                    ("sessionState", outcome.SessionState));

                return JsonSerializer.Serialize(new ScoutSelectionResult(outcome.State, outcome.SessionState),
                                                ForgeToolJson.Default.ScoutSelectionResult);
            });
    }

    [McpServerTool(Name = "forge.scout.run"), Description("Answers one bounded Scout question and returns the complete sourced answer under `scout`, unclipped, which is also appended as a numbered section to the run's `SCOUT.md`. The Vendor, model, effort and Worker-tool grant come only from the persisted Scout selection; pass `sessionMode` explicitly as `continue` to use the current anchor or `fresh` to clear it before starting a new session. A failure before the answer is appended leaves `SCOUT.md` as it was, and the call returns a fixed safe failure.")]
    public static Task<string> ScoutRun(SessionRoots roots,
                                        [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                        [Description("Run id from forge.begin.")] string runId,
                                        [Description("One bounded reconnaissance question, from 1 to 8,000 characters.")] string question,
                                        [Description("Session mode: exactly `continue` or `fresh`. `continue` supplies the persisted anchor; `fresh` clears it first.")] string sessionMode,
                                        CancellationToken ct) =>
        ScoutRun(roots, workspaceRoot, runId, question, sessionMode, ct,
                 vendor => VendorFactory.Create(vendor, workspaceRoot));

    internal static async Task<string> ScoutRun(SessionRoots roots,
                                                string workspaceRoot,
                                                string runId,
                                                string question,
                                                string sessionMode,
                                                CancellationToken ct,
                                                Func<string, IVendor> vendorFactory,
                                                PromptLibrary? prompts = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.scout.run", [("sessionMode", sessionMode)],
            async () =>
            {
                Scout.ValidateQuestion(question);
                Scout.ValidateSessionMode(sessionMode);
                SensitiveInput.Guard(question, "the Scout question");

                var selected = Scout.RequireSelection(run.ReadState());
                var act = new Scout(vendorFactory(selected.Vendor!), prompts ?? new PromptLibrary());
                var outcome = await act.RunAsync(run, question, sessionMode, ct).ConfigureAwait(false);

                return SpeedWarnings.Attach(run, JsonSerializer.Serialize(new ScoutRunResult(outcome, Documents(run, includeScout: true)),
                                                                          ForgeToolJson.Default.ScoutRunResult),
                                            act.SpeedWarning);
            });
    }

    /// <summary>
    /// The user's own instructions to this run's workers, recorded once instead of carried. A
    /// per-call argument would have to survive every round in the orchestrator's context, and text
    /// that has to survive a compaction is text that can quietly stop being sent. See docs/adr/0019.
    /// </summary>
    /// <param name="roots">The session roots advertised by the MCP host.</param>
    /// <param name="workspaceRoot">The run's workspace root.</param>
    /// <param name="runId">The run the instructions belong to.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    /// <param name="criticInstructions">The critic's instructions, or null to leave them as they stand.</param>
    /// <param name="builderInstructions">The builder's instructions, or null to leave them as they stand.</param>
    [McpServerTool(Name = "forge.instructions.set"), Description("Records the user's own free-text instructions to this run's workers, asked for at the end of the interview and kept in the run state. Pass only what the user typed, never anything of your own: nothing here is enforced, and the text goes verbatim into the run's timeline where they will read it. An argument you omit leaves that role's instructions as they stand; an empty string clears them. The critic is handed its text at every round, the builder only by a call that starts its session, so setting builder instructions while a builder session is running answers with a note saying that session will not see them.")]
    public static async Task<string> SetInstructions(SessionRoots roots,
                                                     [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                                     [Description("Run id from forge.begin.")] string runId,
                                                     CancellationToken ct,
                                                     [Description("What the user wants every critic of this run told, verbatim — e.g. a language to answer in, or a class of finding this repository does not want raised. Appended to each review round's prompt after the material under review. Omit to leave it unchanged; pass \"\" to clear it.")] string? criticInstructions = null,
                                                     [Description("What the user wants the builder of this run told, verbatim — e.g. a skill to use, or a house style. Appended to the first prompt of each builder session. Omit to leave it unchanged; pass \"\" to clear it. A code-review critic is shown this text as context for judging the diff.")] string? builderInstructions = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.instructions.set",
            [("criticInstructions", criticInstructions), ("builderInstructions", builderInstructions)],
            () =>
            {
                var outcome = RunInstructions.Set(run, criticInstructions, builderInstructions);

                return Task.FromResult(JsonSerializer.Serialize(
                    new InstructionsResult(outcome.CriticInstructions, outcome.BuilderInstructions,
                                           outcome.Note, Documents(run)),
                    ForgeToolJson.Default.InstructionsResult));
            });
    }

    /// <summary>
    /// Writing the plan, split off from reviewing it. The write used to happen inside
    /// <c>forge.plan.review</c>, which meant the file the user is told to watch appeared only once
    /// a 50–90 KB draft had finished streaming into the call that then ran the critic for minutes:
    /// the link arrived with the critique it was meant to precede. See docs/adr/0014.
    /// </summary>
    /// <param name="roots">The session roots advertised by the MCP host.</param>
    /// <param name="workspaceRoot">The run's workspace root.</param>
    /// <param name="runId">The run whose plan is written.</param>
    /// <param name="planDraft">The complete current plan draft.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    [McpServerTool(Name = "forge.plan.write"), Description("Writes the current plan draft to `PLAN.md` and returns it under `documents`, without running a critic. Call it before every review round, show the user the path it returns, and then run the round with `planDraft` omitted. A write over an already-approved plan takes the approval back and resets the build progress; say so out loud when it happens.")]
    public static async Task<string> WritePlan(SessionRoots roots,
                                               [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                               [Description("Run id from forge.begin.")] string runId,
                                               [Description("The current plan draft, as markdown. The whole plan, not a summary of it.")] string planDraft,
                                               CancellationToken ct)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.plan.write", [("planDraft", planDraft)],
            () =>
            {
                PlanReview.Write(run, planDraft);

                return Task.FromResult(JsonSerializer.Serialize(new PlanWriteResult(run.RunId, Documents(run)),
                                                                ForgeToolJson.Default.PlanWriteResult));
            });
    }

    /// <summary>
    /// One round only. The critic judges the draft; revising it and calling again is the
    /// orchestrator's job, because the revision needs the interview context.
    /// </summary>
    /// <param name="roots">The session roots advertised by the MCP host.</param>
    /// <param name="workspaceRoot">The run's workspace root.</param>
    /// <param name="runId">The run whose plan is reviewed.</param>
    /// <param name="model">The critic model.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    /// <param name="planDraft">A draft to write before review, or <see langword="null"/> to read the written plan.</param>
    /// <param name="effort">The optional critic effort level.</param>
    /// <param name="vendor">The critic vendor, defaulting to Claude.</param>
    /// <param name="revision">The orchestrator's account of changes after the previous round.</param>
    /// <param name="deferred">Optional Flow-only compatibility narrative; typed decisions are authoritative.</param>
    /// <param name="userGrantedRound">Whether the user granted exactly one round beyond the cap.</param>
    /// <param name="decisions">Typed authoritative ledger decisions to apply before this critic round.</param>
    [McpServerTool(Name = "forge.plan.review"), Description("Applies one typed plan decision batch, then runs one Critic round against the active plan-phase ledger projection. Use one decisionBatchId per logical set and repeat the exact batch when retrying an invalid Critic response. Plan closures use addressedByRevision or duplicateOf here. The result includes the critique, Flow audit and plan under `documents`.")]
    public static async Task<string> ReviewPlan(CatalogCache catalogs,
                                                SessionRoots roots,
                                                [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                                [Description("Run id from forge.begin.")] string runId,
                                                [Description("Model for the critic.")] string model,
                                                CancellationToken ct,
                                                [Description("The plan draft to review. Omit it when forge.plan.write already wrote this round's draft, which is the flow to use; passing it writes it here instead, and the round then starts only once the whole draft has reached the server.")] string? planDraft = null,
                                                [Description("Optional effort level.")] string? effort = null,
                                                [Description("Vendor: claude, codex or cursor. Defaults to claude.")] string? vendor = null,
                                                [Description("What you changed in the plan in answer to the previous round's findings, as markdown. Required from the second round on, and recorded in the flow log so the user sees your turn between the critic's.")] string? revision = null,
                                                [Description("Optional markdown list of findings you decided not to act on, each with its reason. Recorded in the flow log; typed ledger decisions are what the next round's critic treats as settled.")] string? deferred = null,
                                                [Description("At the cap, this raises this run's review-round cap by exactly one and runs the round; below the cap it does nothing. Spent by this call, so a further round past the new cap needs a fresh answer. Never pass true without having shown the user where the run stands and asked.")] bool userGrantedRound = false,
                                                [Description("Optional typed authoritative decisions applied before the critic runs. Reuse the same decisionBatchId and identical decisions when retrying this call.")] OrchestratorDecisionBatch? decisions = null,
                                                [Description(FAST_DESCRIPTION)] bool fast = false)
    {
        if (revision is { Length: > 0 }) SensitiveInput.Guard(revision, "the plan revision");
        if (deferred is { Length: > 0 }) SensitiveInput.Guard(deferred, "the deferred findings");
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.plan.review",
            [("vendor", vendor), ("model", model), ("effort", effort), ("fast", Flag(fast)), ("planDraft", planDraft),
             ("revision", revision), ("deferred", deferred),
             ("userGrantedRound", userGrantedRound ? "true" : "false"),
             ("decisionBatchId", decisions?.DecisionBatchId)],
            async () =>
            {
                var critic = VendorFactory.Create(vendor, workspaceRoot);
                var selection = await FastTier.ConfirmAsync(catalogs, critic, new Selection(model, effort, fast), workspaceRoot, ct);
                var act = new PlanReview(critic, new PromptLibrary());
                var critique = await act.ReviewAsync(run, planDraft, selection,
                                                     revision, deferred, userGrantedRound, ct,
                                                     orchestratorDecisions: decisions);

                return SpeedWarnings.Attach(run, JsonSerializer.Serialize(new CritiqueResult(critique, Documents(run)),
                                                                          ForgeToolJson.Default.CritiqueResult),
                                            act.SpeedWarning);
            });
    }

    /// <summary>
    /// Display only, and the one tool here that exists for the user rather than for the run: it
    /// puts the whole plan and the drift in front of them as a document instead of a wall of chat.
    /// It writes nothing and decides nothing — the answer still arrives through
    /// <c>forge.plan.confirm</c>, and a host that renders this still has to ask.
    /// </summary>
    /// <remarks>
    /// The UI is attached through <c>_meta.ui</c>, which a host without the MCP Apps capability
    /// ignores, so the call degrades to the same JSON every other tool returns. That is why the
    /// description sends Text-profile hosts to the chat rather than here: the result would be the
    /// plan they already hold, rendered by nobody. See docs/adr/0008.
    /// </remarks>
    /// <param name="roots">The session roots advertised by the MCP host.</param>
    /// <param name="workspaceRoot">The run's workspace root.</param>
    /// <param name="runId">The run whose plan is shown.</param>
    /// <param name="plan">The complete plan to render.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    [McpServerTool(Name = "forge.plan.show"), McpAppUi(ResourceUri = PlanCanvas.ResourceUri), Description("Renders the plan as a document in the host's own UI, with the working-tree drift beside it. Call it only when forge.begin reported profile `Canvas`, immediately before you ask the user to approve — and still ask, because this records nothing. On a `Text` profile nothing renders, so show the plan in the chat instead.")]
    public static async Task<string> ShowPlan(SessionRoots roots,
                                              [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                              [Description("Run id from forge.begin.")] string runId,
                                              [Description("The plan to show, as markdown. The whole plan, not a summary of it.")] string plan,
                                              CancellationToken ct)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.plan.show", [("plan", plan)],
            async () =>
            {
                var state = run.ReadState();
                var drifted = await run.ReadBaseline(state.BaselineHead)
                                       .DriftedFilesAsync(new GitClient(workspaceRoot), ct);

                return JsonSerializer.Serialize(
                    new PlanViewResult(state.RunId, plan, drifted, state.ReviewRounds, state.Approved),
                    ForgeToolJson.Default.PlanViewResult);
            });
    }

    /// <summary>
    /// The only approval route. It records a decision the orchestrator collected through the host's
    /// own UI, rather than asking through MCP elicitation, because elicitation could not tell a user
    /// saying no from a host that answered on their behalf without rendering anything. Nothing here
    /// is enforced — see docs/adr/0003.
    /// </summary>
    /// <param name="roots">The session roots advertised by the MCP host.</param>
    /// <param name="workspaceRoot">The run's workspace root.</param>
    /// <param name="runId">The run whose decision is recorded.</param>
    /// <param name="plan">The complete plan presented for approval.</param>
    /// <param name="approved">The user's approval decision.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    /// <param name="gateEnvironment">Optional environment variables required by gate commands.</param>
    /// <param name="builderRoots">Optional extra paths the builder may write.</param>
    [McpServerTool(Name = "forge.plan.confirm"), Description("With approved true, applies the same typed plan decisions as forge.plan.review, then refuses approval while any active plan finding is unresolved. With approved false, decisions are forbidden and the ledger is unchanged. Approval also records tasks, gateEnvironment and builderRoots; code-review entries do not block it.")]
    public static async Task<string> ConfirmPlan(SessionRoots roots,
                                                 [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                                 [Description("Run id from forge.begin.")] string runId,
                                                 [Description("The plan to approve, as markdown.")] string plan,
                                                 [Description("What the user answered. Show them the plan and the filtered drift excluding `CONTEXT.md` and `docs/adr/**`, ask, and pass what they say; never decide this yourself.")] bool approved,
                                                 CancellationToken ct,
                                                 [Description("Environment variables for the gate commands the server runs on the host after every build and fix turn, e.g. {\"CD_TEST_SQL_CONN\": \"Server=…\"}. Ask the user for what the plan's gates need before you confirm; the values are kept in the run state and only their names are logged.")] Dictionary<string, string>? gateEnvironment = null,
                                                 [Description("Absolute paths outside the workspace the builder may write to, e.g. a sibling checkout a task edits. Passed to a codex builder as sandbox_workspace_write.writable_roots; other vendors ignore it.")] string[]? builderRoots = null,
                                                 [Description("Optional complete typed plan decision batch. Approved confirmation applies it before checking for unresolved active plan entries; refused confirmation rejects it without mutation.")] OrchestratorDecisionBatch? decisions = null)
    {
        if (!approved && decisions is not null)
            throw new ArgumentRejectedException("forge.plan.confirm(approved:false) does not accept decisions");
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        // The gate environment is logged by name only: a connection string is exactly what it holds.
        return await LoggedAsync(run, "forge.plan.confirm",
            [("approved", approved.ToString()), ("plan", plan),
             ("gateEnvironment", gateEnvironment is { Count: > 0 } ? string.Join(", ", gateEnvironment.Keys) : null),
             ("builderRoots", builderRoots is { Length: > 0 } ? string.Join(", ", builderRoots) : null),
             ("decisionBatchId", decisions?.DecisionBatchId)],
            async () =>
            {
                var gates = GateSettings.Validate(gateEnvironment, builderRoots);
                var state = run.ReadState();
                var tasks = PlanTasks.Parse(plan);

                var drifted = await run.ReadBaseline(state.BaselineHead)
                                       .DriftedFilesAsync(new GitClient(workspaceRoot), ct);

                if (!approved) return Serialized(new ApproveResult(false, 0, drifted));

                if (decisions is not null)
                {
                    try
                    {
                        var response = run.ReadDecisionLedger().Apply(decisions, LedgerPhase.PlanReview);
                        run.AppendFlowDecisionBatch("Plan confirmation", decisions, response);
                        response.ThrowIfConflict();
                    }
                    catch (DecisionLedgerRequestException error)
                    {
                        run.AppendFlowDecisionRejected("Plan confirmation", decisions.DecisionBatchId, error.Message);
                        throw;
                    }
                }

                var blocking = run.ReadDecisionLedger().Project(LedgerPhase.PlanReview)
                    .Where(entry => entry.Disposition == LedgerDisposition.Unresolved)
                    .Select(entry => entry.FindingId)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();
                if (blocking.Length > 0)
                    throw new ArgumentRejectedException($"plan confirmation is blocked by unresolved plan findings: {string.Join(", ", blocking)}");

                var builderSessionId = state.Approved &&
                                       !string.Equals(PlanTasks.Brief(run.ReadPlan()), PlanTasks.Brief(plan),
                                                      StringComparison.Ordinal)
                    ? string.Empty
                    : state.BuilderSessionId;

                run.WritePlan(plan);
                run.WriteState(state with
                {
                    Approved = true,
                    BuilderSessionId = builderSessionId,
                    GateEnvironment = gates.Environment,
                    BuilderRoots = gates.BuilderRoots,
                    PendingGateFailure = null
                });

                return Serialized(new ApproveResult(true, tasks.Count, drifted));
            });
    }

    [McpServerTool(Name = "forge.build.next"), Description("Builds the next unfinished task of the approved plan, then runs the task's gate command on the host and reports it under `build.result.gate`. A task whose gate exits non-zero comes back with status `gate_failed`, is not counted, and is retried by the next call with the gate's output in front of the builder. The gate runs for a builder that reports `blocked` with a verification of `unavailable` too — it did the work and could not prove it — and a gate that passes then rewrites the status to `done` and counts the task; read `build.result.verification` for what the builder itself could not check. A gate that is a condition rather than a command is `not_executable`, and the builder's own verification is all there is. A turn that ended with a command still running in the background comes back as `background_killed` with the gate `not_run`: the session killed the command, the task is not counted, and the next call retries it.")]
    public static async Task<string> BuildNext(CatalogCache catalogs,
                                               SessionRoots roots,
                                               [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                               [Description("Run id from forge.begin.")] string runId,
                                               [Description("Model for the builder.")] string model,
                                               CancellationToken ct,
                                               [Description("Optional effort level.")] string? effort = null,
                                               [Description("Vendor: claude, codex or cursor. Defaults to claude.")] string? vendor = null,
                                               [Description(FAST_DESCRIPTION)] bool fast = false)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.build.next",
            [("vendor", vendor), ("model", model), ("effort", effort), ("fast", Flag(fast))],
            async () =>
            {
                var builder = VendorFactory.Create(vendor, workspaceRoot);
                var selection = await FastTier.ConfirmAsync(catalogs, builder, new Selection(model, effort, fast), workspaceRoot, ct);
                var act = new Build(builder, new PromptLibrary());
                var outcome = await act.NextAsync(run, selection, ct);

                return SpeedWarnings.Attach(run, JsonSerializer.Serialize(new BuildNextResult(outcome, Documents(run)),
                                                                          ForgeToolJson.Default.BuildNextResult),
                                            act.SpeedWarning);
            });
    }

    /// <summary>
    /// One round only, like plan review. The loop used to live inside this call on the premise that
    /// nothing in it needed the interview context; a critic asking for work the approved plan
    /// excluded disproved that, so the orchestrator now takes a turn between critic and builder —
    /// see docs/adr/0005.
    /// </summary>
    /// <param name="roots">The session roots advertised by the MCP host.</param>
    /// <param name="workspaceRoot">The run's workspace root.</param>
    /// <param name="runId">The run whose code is reviewed.</param>
    /// <param name="model">The critic model.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    /// <param name="effort">The optional critic effort level.</param>
    /// <param name="vendor">The critic vendor, defaulting to Claude.</param>
    /// <param name="userGrantedRound">Whether the user granted exactly one round beyond the cap.</param>
    [McpServerTool(Name = "forge.review.code"), Description("Runs one decision-free code-review round against the approved plan and the code-phase ledger projection. The Critic assesses every displayed unresolved ID and may only propose reopening settled IDs. Apply dispositions, reopening answers, duplicate closures and fixes through forge.review.fix.")]
    public static async Task<string> ReviewCode(CatalogCache catalogs,
                                                SessionRoots roots,
                                                [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                                [Description("Run id from forge.begin.")] string runId,
                                                [Description("Model for the critic.")] string model,
                                                CancellationToken ct,
                                                [Description("Optional effort level.")] string? effort = null,
                                                [Description("Vendor: claude, codex or cursor. Defaults to claude.")] string? vendor = null,
                                                [Description("At the cap, this raises this run's code-review-round cap by exactly one and runs the round; below the cap it does nothing. Spent by this call, so a further round past the new cap needs a fresh answer. Never pass true without having shown the user where the run stands and asked.")] bool userGrantedRound = false,
                                                [Description(FAST_DESCRIPTION)] bool fast = false)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.review.code",
            [("vendor", vendor), ("model", model), ("effort", effort), ("fast", Flag(fast)),
             ("userGrantedRound", userGrantedRound ? "true" : "false")],
            async () =>
            {
                var critic = VendorFactory.Create(vendor, workspaceRoot);
                var selection = await FastTier.ConfirmAsync(catalogs, critic, new Selection(model, effort, fast), workspaceRoot, ct);
                var act = new CodeReview(critic, new PromptLibrary(), new GitClient(workspaceRoot));
                var critique = await act.ReviewAsync(run, selection, userGrantedRound, ct);

                return SpeedWarnings.Attach(run, JsonSerializer.Serialize(new CritiqueResult(critique, Documents(run)),
                                                                          ForgeToolJson.Default.CritiqueResult),
                                            act.SpeedWarning);
            });
    }

    [McpServerTool(Name = "forge.review.fix"), Description("Applies typed code-review decisions, including duplicate and host-verified closures, then independently fixes exactly fixFindingIds under fixAttemptId. Decisions-only calls start no Builder or gate. Retry retained or cut-short work with the same attempt and exact ID set; a conflicting set is refused and a saved terminal attempt returns its result without another Builder or gate.")]
    public static async Task<string> ReviewFix(CatalogCache catalogs,
                                               SessionRoots roots,
                                               [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                               [Description("Run id from forge.begin.")] string runId,
                                               [Description("Model for the builder.")] string model,
                                               CancellationToken ct,
                                               [Description("Optional effort level.")] string? effort = null,
                                               [Description("Vendor: claude, codex or cursor. Defaults to claude.")] string? vendor = null,
                                               [Description("Optional complete typed code-review decision batch.")] OrchestratorDecisionBatch? decisions = null,
                                               [Description("Required when fixFindingIds is non-empty; identifies the retryable fix attempt.")] string? fixAttemptId = null,
                                               [Description("Exact ledger finding IDs to fix. The Builder receives only their verbatim ledger findings.")] string[]? fixFindingIds = null,
                                               [Description(FAST_DESCRIPTION)] bool fast = false)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.review.fix",
            [("vendor", vendor), ("model", model), ("effort", effort), ("fast", Flag(fast)),
             ("decisionBatchId", decisions?.DecisionBatchId), ("fixAttemptId", fixAttemptId),
             ("fixFindingIds", fixFindingIds is { Length: > 0 } ? string.Join(", ", fixFindingIds) : null)],
            async () =>
            {
                var builder = VendorFactory.Create(vendor, workspaceRoot);
                var selection = await FastTier.ConfirmAsync(catalogs, builder, new Selection(model, effort, fast), workspaceRoot, ct);
                var act = new ReviewFix(builder, new PromptLibrary());
                var result = await act.FixAsync(run, selection, decisions, fixAttemptId,
                                                fixFindingIds, ct);

                return SpeedWarnings.Attach(run, JsonSerializer.Serialize(new ReviewFixResult(result, Documents(run)),
                                                                          ForgeToolJson.Default.ReviewFixResult),
                                            act.SpeedWarning);
            });
    }

    [McpServerTool(Name = "forge.work.start"), Description("Starts one worker act in the background. plan.review and review.fix use the same typed decisions, decisionBatchId and fix-attempt rules as their direct tools; ledger IDs, phases, states, batch conflicts and fix sets are preflighted before a job is created. Poll until terminal, then fetch. If started is false, rejoin the returned active job. Scout uses only its persisted selection plus question and explicit sessionMode.")]
    public static Task<string> StartWork(JobRegistry registry,
                                         CatalogCache catalogs,
                                         SessionRoots roots,
                                         [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                         [Description("Run id from forge.begin.")] string runId,
                                         [Description("Worker act: plan.review, build.next, review.code, review.fix or scout.")] string act,
                                         CancellationToken ct,
                                         [Description("Model for the worker. Omit for scout, which uses the persisted Scout selection.")] string? model = null,
                                         [Description("Optional effort level.")] string? effort = null,
                                         [Description("Vendor: claude, codex or cursor. Defaults to claude.")] string? vendor = null,
                                         [Description("Plan draft, used only by plan.review — and omitted there too once forge.plan.write has written this round's draft, which is the flow to use.")] string? planDraft = null,
                                         [Description("Flow-only compatibility narrative for plan.review; it does not change ledger state.")] string? deferred = null,
                                         [Description("For plan.review only: what you changed in the plan in answer to the previous round's findings. Required from the second round on.")] string? revision = null,
                                         [Description("For plan.review and review.code only: at the cap, raises this run's round cap by exactly one and runs the round; below the cap it does nothing, and it is spent by this call. Never pass true without having shown the user where the run stands and asked.")] bool userGrantedRound = false,
                                         [Description("For scout only: the one bounded reconnaissance question.")] string? question = null,
                                         [Description("For scout only: exactly `continue` or `fresh`.")] string? sessionMode = null,
                                         [Description("Optional typed decision batch accepted only by plan.review and review.fix.")] OrchestratorDecisionBatch? decisions = null,
                                         [Description("Required by review.fix when fixFindingIds is non-empty.")] string? fixAttemptId = null,
                                         [Description("Exact ledger finding IDs for review.fix. Empty means decisions-only.")] string[]? fixFindingIds = null,
                                         [Description(FAST_DESCRIPTION + " Not for scout, which uses its persisted selection.")] bool fast = false)
    {
        // VendorFactory.Create is deliberately the one line not covered by the factory-seam tests.
        return StartWork(registry, roots, workspaceRoot, runId, act, model, effort, vendor, planDraft, null,
                         deferred, revision, userGrantedRound, question, sessionMode, ct,
                         id => VendorFactory.Create(id, workspaceRoot), null, decisions, fixAttemptId,
                         fixFindingIds, catalogs, fast);
    }

    internal static Task<string> StartWork(JobRegistry registry,
                                           SessionRoots roots,
                                           string workspaceRoot,
                                           string runId,
                                           string act,
                                           string model,
                                           string? effort,
                                           string? vendor,
                                           string? planDraft,
                                           string? findings,
                                           string? deferred,
                                           string? revision,
                                           bool userGrantedRound,
                                           CancellationToken ct,
                                           Func<IVendor> vendorFactory,
                                           OrchestratorDecisionBatch? decisions = null,
                                           string? fixAttemptId = null,
                                           string[]? fixFindingIds = null) =>
        StartWork(registry, roots, workspaceRoot, runId, act, model, effort, vendor, planDraft,
                  findings, deferred, revision, userGrantedRound, null, null, ct, _ => vendorFactory(),
                  null, decisions, fixAttemptId, fixFindingIds);

    internal static async Task<string> StartWork(JobRegistry registry,
                                                 SessionRoots roots,
                                                 string workspaceRoot,
                                                 string runId,
                                                 string act,
                                                 string? model,
                                                 string? effort,
                                                 string? vendor,
                                                 string? planDraft,
                                                 string? findings,
                                                 string? deferred,
                                                 string? revision,
                                                 bool userGrantedRound,
                                                 string? question,
                                                 string? sessionMode,
                                                 CancellationToken ct,
                                                 Func<string, IVendor> vendorFactory,
                                                 PromptLibrary? prompts = null,
                                                 OrchestratorDecisionBatch? decisions = null,
                                                 string? fixAttemptId = null,
                                                 string[]? fixFindingIds = null,
                                                 CatalogCache? catalogs = null,
                                                 bool fast = false)
    {
        if (revision is { Length: > 0 }) SensitiveInput.Guard(revision, "the plan revision");
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.work.start",
            [("act", act), ("vendor", vendor), ("model", model), ("effort", effort), ("fast", Flag(fast)),
             ("planDraft", planDraft), ("findings", findings), ("deferred", deferred),
             ("revision", revision), ("userGrantedRound", userGrantedRound ? "true" : "false"),
             ("sessionMode", sessionMode), ("decisionBatchId", decisions?.DecisionBatchId),
             ("fixAttemptId", fixAttemptId),
             ("fixFindingIds", fixFindingIds is { Length: > 0 } ? string.Join(", ", fixFindingIds) : null)],
            async () =>
            {
                var selection = model is null ? null : new Selection(model, effort, fast);
                WorkAct.ValidateArguments(act, planDraft, selection, findings, deferred, revision,
                                          userGrantedRound, question, sessionMode, decisions, fixAttemptId,
                                          fixFindingIds);
                if (act == "scout")
                {
                    if (vendor is not null || effort is not null || fast)
                        throw new ArgumentRejectedException("scout takes Vendor, effort and fast only from the persisted selection");
                    SensitiveInput.Guard(question!, "the Scout question");
                }

                // The act would refuse these itself a moment later, inside the job. Refusing here
                // instead keeps a missing revision — or a round with no draft anywhere — an argument
                // error, answered by this call rather than by a poll that reports a failure with
                // nothing running behind it.
                if (act == "plan.review")
                {
                    PlanReview.RequireRevision(run.ReadState().ReviewRounds, revision);
                    PlanReview.RequireDraft(run, planDraft);
                }

                try
                {
                    OrchestrationPreflight.Validate(run, act, decisions, fixAttemptId, fixFindingIds);
                }
                catch (DecisionLedgerRequestException error)
                {
                    if (decisions is not null)
                        run.AppendFlowDecisionRejected($"{act} preflight", decisions.DecisionBatchId, error.Message);
                    throw;
                }

                var vendorForAct = vendorFactory(act == "scout"
                    ? Scout.RequireSelection(run.ReadState()).Vendor!
                    : vendor ?? VendorFactory.DefaultId);
                if (selection is not null)
                    selection = await FastTier.ConfirmAsync(catalogs ?? new CatalogCache((id, _) => vendorFactory(id!)),
                                                            vendorForAct, selection, workspaceRoot, ct);
                var workAct = new WorkAct(vendorForAct, prompts ?? new PromptLibrary());
                var started = registry.Start(run.Path, act,
                    jobCt => workAct.RunAsync(act, run, planDraft, selection, findings, deferred, revision,
                                               userGrantedRound, jobCt, question, sessionMode, decisions,
                                               fixAttemptId, fixFindingIds));

                var record = started.Record;
                return JsonSerializer.Serialize(new WorkStartResult(record.Id, record.Act, StateName(record.State), started.Started, Documents(run)),
                    ForgeToolJson.Default.WorkStartResult);
            });
    }

    [McpServerTool(Name = "forge.work.poll"), Description("Waits for a background worker act to finish, for up to 45 seconds, and reports its last stdout activity and recognised event. A `running` result is not the end of the wait: call this again with the same job id.")]
    public static Task<string> PollWork(JobRegistry registry,
                                        SessionRoots roots,
                                        [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                        [Description("Run id from forge.begin.")] string runId,
                                        [Description("Job id returned by forge.work.start.")] string jobId,
                                        CancellationToken ct) =>
        PollWork(registry, roots, workspaceRoot, runId, jobId, TimeSpan.FromSeconds(WORK_POLL_TIMEOUT_SECONDS), ct);

    internal static async Task<string> PollWork(JobRegistry registry,
                                                SessionRoots roots,
                                                string workspaceRoot,
                                                string runId,
                                                string jobId,
                                                TimeSpan timeout,
                                                CancellationToken ct)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.work.poll", [("jobId", jobId)],
            async () =>
            {
                ValidateJobId(jobId);
                var record = await registry.WaitAsync(run.Path, jobId, timeout, ct)
                    .ConfigureAwait(false);
                RequireJob(record, jobId);

                return JsonSerializer.Serialize(PollResult(record!, run),
                    ForgeToolJson.Default.WorkPollResult);
            });
    }

    [McpServerTool(Name = "forge.work.cancel"), Description("Requests cancellation of one background worker job and returns its current state and liveness. Call this only after the user explicitly asks to cancel, or after showing them the job's liveness and receiving confirmation. Cancellation is non-blocking: while the returned state is `running`, continue with forge.work.poll and then forge.work.fetch. Cancelling a terminal job succeeds without changing it.")]
    public static async Task<string> CancelWork(JobRegistry registry,
                                                 SessionRoots roots,
                                                 [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                                 [Description("Run id from forge.begin.")] string runId,
                                                 [Description("Job id returned by forge.work.start.")] string jobId,
                                                 CancellationToken ct)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.work.cancel", [("jobId", jobId)],
            () =>
            {
                ValidateJobId(jobId);
                var record = registry.Cancel(run.Path, jobId);
                RequireJob(record, jobId);

                return Task.FromResult(JsonSerializer.Serialize(PollResult(record!, run),
                    ForgeToolJson.Default.WorkPollResult));
            });
    }

    [McpServerTool(Name = "forge.work.fetch"), Description("Fetches the terminal result of a background worker act: the act's own payload as the `result` string, beside its state, any `error`, and `documents`. Call it only after a forge.work.poll came back in a state other than `running` — a job still running has no result to fetch. A job id from a server process that has since exited cannot be fetched; start a new act instead, and read the persisted result under `.forge/<runId>/`.")]
    public static async Task<string> FetchWork(JobRegistry registry,
                                               SessionRoots roots,
                                               [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                               [Description("Run id from forge.begin.")] string runId,
                                               [Description("Job id returned by forge.work.start.")] string jobId,
                                               CancellationToken ct)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.work.fetch", [("jobId", jobId)],
            () =>
            {
                ValidateJobId(jobId);
                var record = registry.Get(run.Path, jobId);
                RequireJob(record, jobId);
                if (record!.State == JobState.Running)
                    throw new InvalidOperationException($"job {jobId} is still running");

                if (record.State == JobState.Failed && record.Act == "scout")
                    run.Log.Write("error", SOURCE, "forge.work.fetch.error",
                        ("jobId", record.Id), ("act", record.Act), ("error", record.Error));

                var result = record.State == JobState.Completed ? record.ResultPayload : null;
                return Task.FromResult(JsonSerializer.Serialize(
                    new WorkFetchResult(record.Id, record.Act, StateName(record.State), result,
                                        record.State == JobState.Failed ? record.Error : null,
                                        Documents(run, record.Act == "scout" && record.State == JobState.Completed)),
                    ForgeToolJson.Default.WorkFetchResult));
            });
    }

    [McpServerTool(Name = "forge.status"), Description("Reports where the run stands and changes nothing. `ledger` is the compact source-of-truth summary of current finding IDs by disposition and active phase, so a resumed Orchestrator need not read run files. Also returns run progress, `run.scout` with enabled/selection, current session and last failure, filtered drift, and active-job liveness. Call it before approval, retry or a round-cap decision.")]
    public static async Task<string> Status(JobRegistry registry,
                                            SessionRoots roots,
                                            [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                            [Description("Run id from forge.begin.")] string runId,
                                            CancellationToken ct)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.status", [],
            async () =>
            {
                var state = run.ReadState();
                var drifted = await run.ReadBaseline(state.BaselineHead)
                                       .DriftedFilesAsync(new GitClient(workspaceRoot), ct);
                var active = registry.Get(run.Path) is { State: JobState.Running } job
                    ? new ActiveJob(job.Id, job.Act, StateName(job.State), ElapsedSeconds(job),
                                    job.LastActivityAt, job.LastEvent)
                    : null;

                return JsonSerializer.Serialize(new StatusResult(state, drifted, active,
                                                                  run.ReadDecisionLedger().Summary),
                                                ForgeToolJson.Default.StatusResult);
            });
    }

    public static Task<string> Status(SessionRoots roots, string workspaceRoot, string runId, CancellationToken ct) =>
        Status(new JobRegistry(), roots, workspaceRoot, runId, ct);

    /// <summary>
    /// The orchestrator's own entry point into the run log. Everything else here logs itself; this
    /// is what the acting agent uses to record what it selected, what it retried, and why.
    /// </summary>
    /// <remarks>
    /// A tool rather than a documented licence to edit the file: it keeps the run id inside the
    /// same containment check every other write passes, keeps the format one thing rather than one
    /// per agent, and leaves the "do not hand-edit anything under `.forge/`" rule intact.
    /// </remarks>
    /// <param name="roots">The session roots advertised by the MCP host.</param>
    /// <param name="workspaceRoot">The run's workspace root.</param>
    /// <param name="runId">The run whose log receives the entry.</param>
    /// <param name="message">The one-line description of what happened.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    /// <param name="level">The optional diagnostic level.</param>
    /// <param name="detail">Optional longer diagnostic detail.</param>
    [McpServerTool(Name = "forge.log.append"), Description("Appends one entry to the run's diagnostic log at `.forge/<runId>/forge.log`. Use it to record what you selected, retried, or decided; never edit the file directly.")]
    public static async Task<string> AppendLog(SessionRoots roots,
                                               [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                               [Description("Run id from forge.begin.")] string runId,
                                               [Description("What happened, in one line.")] string message,
                                               CancellationToken ct,
                                               [Description("Optional level: info, warn or error. Defaults to info.")] string? level = null,
                                               [Description("Optional longer detail — a command line, an error, a decision's reasoning.")] string? detail = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        run.Log.Write(Level(level), "orchestrator", "note", ("message", message), ("detail", detail));

        return run.DiagnosticLogPath;
    }

    /// <summary>
    /// The run's user-facing files travelling with every act result, for the same reason
    /// `forge.work.poll` carries its next call: an instruction that lives only in the skill is gone
    /// by mid-run, and the one it lost was "surface this file". Each entry is
    /// <see langword="null"/> until its file exists, which is what makes the first result carrying
    /// one the moment there is something to show.
    /// </summary>
    /// <remarks>
    /// Two files rather than one, each with its own instruction, because they change on different
    /// rhythms: the timeline grows with every act, while the plan only moves when a round's draft is
    /// written. One shared instruction would have to blur that into "show these to the user".
    /// The diagnostic log is deliberately absent — it is for the orchestrator, and is not written
    /// to be read as a user document here.
    /// </remarks>
    /// <param name="run">The run whose user-facing files are described.</param>
    private static RunDocuments Documents(RunDirectory run, bool includeScout = false) =>
        new(File.Exists(run.FlowLogPath)
                ? new RunDocument(run.FlowLogPath,
                                  "show this file to the user now — it is the run's user-facing timeline — "
                                  + "and show it again after every later worker act.")
                : null,
            File.Exists(run.PlanPath)
                ? new RunDocument(run.PlanPath,
                                  "the plan as it now stands, rewritten before every review round: show this "
                                  + "file to the user now and again after each later round, so they can "
                                  + "watch it change. Link it; do not paste the draft into the chat.")
                : null,
            includeScout && File.Exists(run.ScoutReportPath)
                ? new RunDocument(run.ScoutReportPath,
                                  "show the Scout report to the user now, and show it again after each later successful Scout call appends its answer.")
                : null);

    private static string StateName(JobState state) => state switch
                                                       {
                                                           JobState.Running => "running",
                                                           JobState.Completed => "succeeded",
                                                           JobState.Failed => "failed",
                                                           _ => throw new ArgumentOutOfRangeException(nameof(state))
                                                       };

    /// <summary>
    /// The poll payload carries its own next call because the instruction to keep polling lives
    /// only in the skill, and a host whose context has moved on from it reads a bare
    /// <c>running</c> as the end of the wait: it hands the turn back and asks the user to resume a
    /// job that never needed them.
    /// </summary>
    /// <param name="state">The current job state.</param>
    private static string NextCall(JobState state) => state == JobState.Running
                                                          ? "the job is still running: call forge.work.poll again now with this job id. Do not end your turn, and do not ask the user to continue."
                                                          : "call forge.work.fetch with this job id.";

    private static WorkPollResult PollResult(JobRecord record, RunDirectory run) =>
        new(record.Id, record.Act, StateName(record.State), ElapsedSeconds(record), record.LastActivityAt,
            record.LastEvent, record.State == JobState.Failed ? record.Error : null, NextCall(record.State),
            Documents(run));

    private static double ElapsedSeconds(JobRecord record) => Math.Max(0, ((record.CompletedAt ?? DateTimeOffset.UtcNow) - record.StartedAt).TotalSeconds);

    private static void ValidateJobId(string jobId)
    {
        if (jobId.Length != 16 || jobId.Any(character => !((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))))
            throw new ArgumentRejectedException("jobId must be 16 lowercase hexadecimal characters");
    }

    private static void RequireJob(JobRecord? record, string jobId)
    {
        if (record is null || record.Id != jobId)
            throw new InvalidOperationException($"unknown jobId '{jobId}'");
    }

    private static string Level(string? level) => level?.Trim().ToLowerInvariant() switch
                                                  {
                                                      "warn" or "warning" => "warn",
                                                      "error" => "error",
                                                      _ => "info"
                                                  };

    private static string Flag(bool value) => value ? "true" : "false";

    private static async Task<(ScoutState State, string Action, string SessionState)> SelectScoutStateAsync(CatalogCache catalogs,
                                                                                                           ScoutState? current,
                                                                                                           bool enabled,
                                                                                                           string? vendor,
                                                                                                           string? model,
                                                                                                           string? effort,
                                                                                                           bool fast,
                                                                                                           string workspaceRoot,
                                                                                                           CancellationToken ct)
    {
        if (!enabled)
        {
            if (vendor is not null || model is not null || effort is not null || fast)
                throw new ArgumentRejectedException("forge.scout.select rejects vendor, model, effort and fast when enabled is false");

            return (new ScoutState(false), "declined", "disabled");
        }

        if (string.IsNullOrWhiteSpace(vendor))
            throw new ArgumentRejectedException("forge.scout.select requires vendor when enabled is true");
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentRejectedException("forge.scout.select requires model when enabled is true");

        var resolved = VendorFactory.Create(vendor, workspaceRoot);
        var selection = await FastTier.ConfirmAsync(catalogs, resolved, new Selection(model, effort, fast), workspaceRoot, ct);
        if (current is { Enabled: true, LastFailure: null }
            && string.Equals(current.Vendor, resolved.Id, StringComparison.Ordinal)
            && string.Equals(current.Model, selection.Model, StringComparison.Ordinal)
            && string.Equals(current.Effort, selection.Effort, StringComparison.Ordinal)
            && current.Fast == selection.Fast)
            return (current, "selected", current.SessionId is { Length: > 0 } ? "resumed" : "fresh");

        return (new ScoutState(true, resolved.Id, selection.Model, selection.Effort, Fast: selection.Fast),
                current is null ? "selected" : "reselected", "fresh");
    }

    /// <summary>
    /// Wraps one tool call in its run's log: the arguments on the way in, and the result, the
    /// exception, or the cancellation on the way out.
    /// </summary>
    /// <remarks>
    /// Setting the ambient log is the other half of the job. Everything the call reaches — the
    /// vendor sessions, the process runner, the MCP SDK's own logger — finds the run's file through
    /// <see cref="RunLog.Current"/> rather than being handed one, which is what keeps the log out
    /// of every signature between here and a process launch.
    /// </remarks>
    /// <param name="run">The run whose diagnostic log receives the call.</param>
    /// <param name="tool">The MCP tool name.</param>
    /// <param name="arguments">The tool arguments safe to record.</param>
    /// <param name="act">The operation that produces the serialized tool result.</param>
    private static async Task<string> LoggedAsync(RunDirectory run,
                                                  string tool,
                                                  (string Name, string? Value)[] arguments,
                                                  Func<Task<string>> act)
    {
        var log = run.Log;
        using var scope = RunLog.Serve(log);

        log.Write("info", SOURCE, "tool.call", [("tool", tool), .. arguments]);
        try
        {
            var result = await act().ConfigureAwait(false);
            log.Write("info", SOURCE, "tool.result", ("tool", tool), ("result", result));
            return result;
        }
        catch (OperationCanceledException)
        {
            // The host giving up is the failure mode with no other trace: it takes the call away
            // before any result exists, which is exactly how a timeout looks from in here.
            log.Write("warn", SOURCE, "tool.cancelled", ("tool", tool));
            throw;
        }
        catch (Exception error)
        {
            log.Write("error", SOURCE, "tool.failed",
                ("tool", tool), ("error", error.Message), ("stack", error.ToString()));
            throw;
        }
    }

    private static string Serialized(ApproveResult result) => JsonSerializer.Serialize(result, ForgeToolJson.Default.ApproveResult);

    // Sortable and collision-free enough for a per-workspace run folder.
    private static string NewRunId() => $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..6]}";
}

internal sealed record BeginResult(string RunId, string RunPath, string Profile, string BaselineHead, string Client);

internal sealed record ApproveResult(bool Approved, int TaskCount, IReadOnlyList<string> DriftedFiles);

/// <summary>What <c>forge.plan.write</c> answers with: nothing but the file it wrote and where to show it.</summary>
internal sealed record PlanWriteResult(string RunId, RunDocuments Documents);

/// <param name="Note">
/// What the call could not do: a builder session already running was started before this text and
/// will not see it. Null when there was nothing of the kind to say.
/// </param>
internal sealed record InstructionsResult(string? CriticInstructions,
                                          string? BuilderInstructions,
                                          string? Note,
                                          RunDocuments Documents);

/// <summary>
/// What the canvas renders. It carries the plan back out again rather than reading `PLAN.md`,
/// because the file holds the draft of the last review round while the orchestrator may be holding
/// a newer one — a plan amended after the last verdict is shown for approval before any round has
/// seen it.
/// </summary>
internal sealed record PlanViewResult(string RunId,
                                      string Plan,
                                      IReadOnlyList<string> DriftedFiles,
                                      int ReviewRounds,
                                      bool Approved);

/// <summary>
/// Drift travels with the status rather than only with the decision, because the orchestrator has
/// to show it to the user <em>before</em> asking, and the decision call is where it would arrive
/// too late to matter.
/// </summary>
internal sealed record StatusResult(RunState Run, IReadOnlyList<string> DriftedFiles, ActiveJob? ActiveJob,
                                    LedgerSummary Ledger);

internal sealed record ScoutSelectionResult(ScoutState Scout, string SessionState);

internal sealed record ScoutRunResult(ScoutReport Scout, RunDocuments Documents);

internal sealed record ActiveJob(string JobId,
                                 string Act,
                                 string State,
                                 double ElapsedSeconds,
                                 DateTimeOffset? LastActivityAt,
                                 string? LastEvent);

/// <summary>
/// A started job, and the run's files as they stand at that moment. The documents travel with the
/// start and every poll rather than only with the fetch, because a Cursor act runs for minutes and
/// the plan is on disk before the first of them — see docs/adr/0014.
/// </summary>
internal sealed record WorkStartResult(string JobId, string Act, string State, bool Started, RunDocuments Documents);

/// <param name="Next">What to do with the file: the instruction travels with the path so neither depends on the skill still being in view.</param>
internal sealed record RunDocument(string Path, string Next);

/// <summary>
/// The files of a run that exist to be shown to a person. All entries are optional and start out
/// absent: the timeline appears with the first act that records one, the plan with the write that
/// precedes the first round, and Scout only after a successful Scout call.
/// </summary>
internal sealed record RunDocuments(RunDocument? FlowLog,
                                    RunDocument? Plan,
                                    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                    RunDocument? Scout = null);

/// <summary>Both review tools answer with this: one critique, plus where the user can watch the run.</summary>
internal sealed record CritiqueResult(Critique Critique, RunDocuments Documents);

internal sealed record BuildNextResult(BuildOutcome Build, RunDocuments Documents);

internal sealed record ReviewFixResult(BuildResult Fix, RunDocuments Documents);

/// <param name="Next">The call this result asks for: another poll while the job runs, a fetch once it stops.</param>
internal sealed record WorkPollResult(string JobId,
                                      string Act,
                                      string State,
                                      double ElapsedSeconds,
                                      DateTimeOffset? LastActivityAt,
                                      string? LastEvent,
                                      string? Error,
                                      string Next,
                                      RunDocuments Documents);

internal sealed record WorkFetchResult(string JobId, string Act, string State, string? Result, string? Error, RunDocuments Documents);

internal sealed record ModelsResult(IReadOnlyList<VendorCatalogResult> Vendors);

/// <param name="Source">"live" when the vendor reported the list itself, "resolved" when it resolved aliases this repo remembers.</param>
internal sealed record VendorCatalogResult(string Vendor,
                                           string Source,
                                           bool Available,
                                           string Detail,
                                           IReadOnlyList<CatalogModel> Models);

/// <param name="FastEfforts">The efforts this model is offered at with a Fast tier; empty when it has none.</param>
/// <param name="FastHint">The vendor's own word on what Fast costs, where it gives one.</param>
/// <param name="FastUnavailable">Why the account will not serve a Fast tier the model offers.</param>
internal sealed record CatalogModel(string Id,
                                    string? DisplayName,
                                    string? Description,
                                    IReadOnlyList<string> Efforts,
                                    string? DefaultEffort,
                                    bool IsDefault,
                                    IReadOnlyList<string> FastEfforts,
                                    string? FastHint,
                                    string? FastUnavailable);

/// <summary>
/// The structured tool <em>arguments</em>, as opposed to the results above. The SDK marshals scalar
/// arguments out of its own context, and reflection is off repo-wide, so the two non-scalar
/// arguments of <c>forge.plan.confirm</c> need a contract of their own — chained after the SDK's in
/// <see cref="ArgumentOptions"/>, which is what <c>WithTools</c> and the schema test are handed.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(DecisionBatchRequest))]
[JsonSerializable(typeof(OrchestratorDecisionBatch))]
[JsonSerializable(typeof(OrchestratorDecision))]
[JsonSerializable(typeof(LedgerDispositionDecision))]
[JsonSerializable(typeof(LedgerReopeningDecision))]
[JsonSerializable(typeof(LedgerClosureDecision))]
internal sealed partial class ToolArgumentJson : JsonSerializerContext
{
    // Lazy rather than a field initializer: the generated half of this class initializes its own
    // statics in its own order, and an initializer here that reads `Default` ran before them.
    private static readonly Lazy<JsonSerializerOptions> _argumentOptions = new(Build);

    public static JsonSerializerOptions ArgumentOptions => _argumentOptions.Value;

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.TypeInfoResolverChain.Add(Default);
        return options;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BeginResult))]
[JsonSerializable(typeof(ApproveResult))]
[JsonSerializable(typeof(PlanWriteResult))]
[JsonSerializable(typeof(InstructionsResult))]
[JsonSerializable(typeof(PlanViewResult))]
[JsonSerializable(typeof(StatusResult))]
[JsonSerializable(typeof(ScoutSelectionResult))]
[JsonSerializable(typeof(ScoutRunResult))]
[JsonSerializable(typeof(ScoutReport))]
[JsonSerializable(typeof(ActiveJob))]
[JsonSerializable(typeof(BuildOutcome))]
[JsonSerializable(typeof(RunDocument))]
[JsonSerializable(typeof(RunDocuments))]
[JsonSerializable(typeof(CritiqueResult))]
[JsonSerializable(typeof(Critique))]
[JsonSerializable(typeof(Finding))]
[JsonSerializable(typeof(UnresolvedAssessment))]
[JsonSerializable(typeof(ReopeningProposal))]
[JsonSerializable(typeof(BuildNextResult))]
[JsonSerializable(typeof(ReviewFixResult))]
[JsonSerializable(typeof(WorkStartResult))]
[JsonSerializable(typeof(WorkPollResult))]
[JsonSerializable(typeof(WorkFetchResult))]
[JsonSerializable(typeof(ModelsResult))]
internal sealed partial class ForgeToolJson : JsonSerializerContext;
