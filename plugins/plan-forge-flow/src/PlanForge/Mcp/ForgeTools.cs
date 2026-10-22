using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;
using PlanForge.Acts;
using PlanForge.Advisors;
using PlanForge.Diagnostics;
using PlanForge.Evidence;
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
    private const string SOURCE = "server";

    /// <summary>Only one Evidence map may mutate a Run in this server process at a time.</summary>
    private static readonly ConcurrentDictionary<string, byte> EvidenceMaps = new(StringComparer.OrdinalIgnoreCase);
    // The documents each job last answered with, by run path and job id, until its result is fetched.
    private static readonly ConcurrentDictionary<string, RunDocuments> ShownDocuments = new(StringComparer.OrdinalIgnoreCase);

    private const string FAST_DESCRIPTION =
        "Run the worker at the vendor's Fast tier: quicker, at a higher usage price. Only for a model and effort forge.models " +
        "lists under fastEfforts, and only when the user chose it; the server refuses a request the catalogue does not " +
        "confirm, and claude refuses one its account will not serve. Omitted means standard speed, asked for explicitly.";

    private const string NOTE_DESCRIPTION =
        "Framing for the Builder, shown after the verbatim findings under \"From the orchestrator\": for each rule the " +
        "findings break, the rule with the requirement it comes from, its owner and its axes; for the batch, what must not " +
        "change and when to stop. A retry's note says what changed since the last call. It never replaces a finding. Only " +
        "with non-empty fixFindingIds; recorded verbatim in the Flow log.";

    private const string EXCLUDE_PATHS_DESCRIPTION =
        "Paths or git pathspec patterns, relative to workspaceRoot, that this round leaves out of the review window beside " +
        "every CONTEXT.md and docs/adr/**: nothing of theirs is sent, and the critic is told they were left out. For what " +
        "is not the run's work, such as run tooling or generated output, and only after asking the user. This call only.";

    [McpServerTool(Name = "forge.begin"), Description("Starts a run, takes a working-tree baseline excluding `CONTEXT.md` and `docs/adr/**`, and returns the run id, the capability profile, and the connecting client. `workerTools` names the MCP servers every critic, builder, and Scout of the run may call without being asked; omit it for the Roslyn servers alone.")]
    public static async Task<string> Begin(McpServer server,
                                           CatalogCache catalogs,
                                           SessionRoots roots,
                                           [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                           CancellationToken ct,
                                           [Description("MCP server names, `*` matching any run of characters, that the run's workers may call without being asked — a headless worker is refused every call nothing granted. Omit for [\"roslyn-*\"]; pass [] for none. Name only servers that do not change files: Critic, Builder, and Scout get the same grant.")] string[]? workerTools = null) =>
        await Begin(roots, ClientName(server), CapabilityProfileDetector.Detect(server.ClientCapabilities),
                    catalogs.BeginProbing, workspaceRoot, ct, workerTools);

    /// <summary>
    /// <c>forge.begin</c> without an <see cref="McpServer"/>: what the handshake would say is passed
    /// in, and so is the catalogue probe, so a test can begin a Run without a billed probe.
    /// </summary>
    /// <param name="roots">The session roots advertised by the MCP host.</param>
    /// <param name="client">The MCP client's name from the handshake.</param>
    /// <param name="profile">The host's capability profile.</param>
    /// <param name="beginProbing">Starts the catalogue probes for the workspace, without waiting.</param>
    /// <param name="workspaceRoot">The workspace root of the new Run.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    /// <param name="workerTools">The MCP servers the Run's workers may call unasked, or null for the default.</param>
    /// <param name="environment">The environment lookup for Advisor discovery, or null for the process environment.</param>
    internal static async Task<string> Begin(SessionRoots roots,
                                             string client,
                                             CapabilityProfile profile,
                                             Action<string> beginProbing,
                                             string workspaceRoot,
                                             CancellationToken ct,
                                             string[]? workerTools = null,
                                             Func<string, string?>? environment = null)
    {
        using var scope = OrchestratorCall.Join(workspaceRoot, null);
        var granted = WorkerTools.Effective(WorkerTools.Validate(workerTools));
        var runId = NewRunId();
        var run = await RunDirectory.CreateAsync(roots, workspaceRoot, runId, ct);
        scope.Call.Attach(workspaceRoot, runId);

        // The run folder has to exist before anything can be logged, so this is the one tool whose
        // record starts after its first side effect rather than before it. `sessionRoot` is null
        // for a host that declares no roots, which is the one thing the run path alone cannot say.
        return await LoggedAsync(run, workspaceRoot, "forge.begin",
            [("workspaceRoot", workspaceRoot), ("sessionRoot", await roots.DirectoryAsync(ct)),
             ("client", client), ("profile", profile.ToString()),
             ("workerTools", string.Join(", ", granted))],
            async () =>
            {
                // Fire-and-forget: by the time the interview reaches the vendor question,
                // forge.models finds the catalogues already fetched.
                beginProbing(workspaceRoot);

                var baseline = await Baseline.CaptureAsync(new GitClient(workspaceRoot), ct);
                run.WriteBaseline(baseline);
                run.WriteState(new RunState(runId, workspaceRoot, profile.ToString(), DateTimeOffset.Now,
                    ReviewRounds: 0, ReviewRoundCap: DEFAULT_REVIEW_ROUND_CAP, BaselineHead: baseline.Head,
                    CodeReviewRoundCap: DEFAULT_CODE_REVIEW_CAP, WorkerTools: granted));
                await scope.Call.Phases.BeginAsync(run, scope.Call, ct);

                return JsonSerializer.Serialize(
                    new BeginResult(runId, run.Path, profile.ToString(), baseline.Head, client,
                                    AdvisorCatalog.Default.Discover(environment ?? Environment.GetEnvironmentVariable)),
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
    [McpServerTool(Name = "forge.models"), Description("Returns each vendor's model catalogue with effort levels per model, newest first — source `live` for codex, which publishes a list, `resolved` for claude, whose remembered aliases the CLI turned into the model ids they stand for (displayName). A vendor with available:false is not usable; tell the user why and do not offer it. `fastEfforts` lists the efforts a model is offered at with the vendor's Fast tier, `fastHint` what the vendor says it costs, and `fastUnavailable` why the account will not serve it; offer Fast only where fastEfforts holds the effort and fastUnavailable is null.")]
    public static async Task<string> Models(CatalogCache catalogs,
                                            SessionRoots roots,
                                            [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                            [Description("Run id from forge.begin.")] string runId,
                                            CancellationToken ct,
                                            [Description("Vendor: claude or codex. Omit for both.")] string? vendor = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, workspaceRoot, "forge.models", [("vendor", vendor)],
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
                    model.Efforts, model.DefaultEffort, model.IsDefault,
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
        return await LoggedAsync(run, workspaceRoot, "forge.scout.select",
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

    /// <summary>Answers a Scout question and appends its complete answer to scout-answers.json.</summary>
    /// <param name="registry">The server's jobs, read for the Scout jobs a continue may not run beside.</param>
    /// <param name="roots">The host's session roots.</param>
    /// <param name="workspaceRoot">The absolute repository root.</param>
    /// <param name="runId">The Run receiving the answer.</param>
    /// <param name="question">The bounded reconnaissance question.</param>
    /// <param name="sessionMode">Whether to continue or start a fresh session.</param>
    /// <param name="ct">Cancels the Scout turn.</param>
    [McpServerTool(Name = "forge.scout.run"), Description("Answers one bounded Scout question and returns the complete sourced answer under `scout`, unclipped, which is also appended as a numbered entry to the run's `scout-answers.json`. The Vendor, model, effort and Worker-tool grant come only from the persisted Scout selection; pass `sessionMode` explicitly as `continue` to use the current anchor or `fresh` to clear it before starting a new session. A `continue` is refused while a Scout job runs, and a turn that ran beside another Scout turn leaves no anchor. A failure before the answer is appended leaves `scout-answers.json` as it was, and the call returns a fixed safe failure.")]
    public static Task<string> ScoutRun(JobRegistry registry,
                                        SessionRoots roots,
                                        [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                        [Description("Run id from forge.begin.")] string runId,
                                        [Description("One bounded reconnaissance question, from 1 to 8,000 characters.")] string question,
                                        [Description("Session mode: exactly `continue` or `fresh`. `continue` supplies the persisted anchor; `fresh` clears it first.")] string sessionMode,
                                        CancellationToken ct) =>
        ScoutRun(roots, workspaceRoot, runId, question, sessionMode, ct,
                 vendor => VendorFactory.Create(vendor, workspaceRoot), registry: registry);

    /// <summary>Answers a Scout question through <paramref name="vendorFactory"/>.</summary>
    /// <param name="roots">The host's session roots.</param>
    /// <param name="workspaceRoot">The absolute repository root.</param>
    /// <param name="runId">The Run receiving the answer.</param>
    /// <param name="question">The bounded reconnaissance question.</param>
    /// <param name="sessionMode">Whether to continue or start a fresh session.</param>
    /// <param name="ct">Cancels the Scout turn.</param>
    /// <param name="vendorFactory">Makes the vendor the persisted selection names.</param>
    /// <param name="prompts">The role prompts, or null for the shipped ones.</param>
    /// <param name="registry">The server's jobs, or null for a caller that runs none.</param>
    internal static async Task<string> ScoutRun(SessionRoots roots,
                                                string workspaceRoot,
                                                string runId,
                                                string question,
                                                string sessionMode,
                                                CancellationToken ct,
                                                Func<string, IVendor> vendorFactory,
                                                PromptLibrary? prompts = null,
                                                JobRegistry? registry = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, workspaceRoot, "forge.scout.run", [("sessionMode", sessionMode)],
            async () =>
            {
                Scout.ValidateQuestion(question);
                Scout.ValidateSessionMode(sessionMode);
                SensitiveInput.Guard(question, "the Scout question");

                string[] scoutJobs = [.. (registry?.Running(run.Path) ?? []).Where(job => job.Act == "scout").Select(job => job.Id)];
                if (sessionMode == "continue" && scoutJobs.Length > 0)
                    throw new ArgumentRejectedException(
                        $"a continue runs alone, and Scout jobs {string.Join(", ", scoutJobs)} are running: fetch them first, or ask fresh");

                var selected = Scout.RequireSelection(run.ReadState());
                var act = new Scout(vendorFactory(selected.Vendor!), prompts ?? new PromptLibrary());
                var outcome = await act.RunAsync(run, question, sessionMode, ct).ConfigureAwait(false);

                return SpeedWarnings.Attach(run, JsonSerializer.Serialize(new ScoutRunResult(outcome, Documents(run, includeScout: true)),
                                                                          ForgeToolJson.Default.ScoutRunResult),
                                            act.SpeedWarning);
            });
    }

    /// <summary>Advises the manual Evidence check in bounded portions through a configured Advisor.</summary>
    /// <param name="roots">The host's session roots.</param>
    /// <param name="workspaceRoot">The absolute repository root.</param>
    /// <param name="runId">The Run receiving the map.</param>
    /// <param name="changePoints">Every Change point accumulated by the Run's Impact passes.</param>
    /// <param name="ct">Caller cancellation, propagated after persistence and records.</param>
    /// <param name="advisor">The configured Advisor id, or null to select the sole configured Advisor.</param>
    /// <param name="implications">Optional obligations derived from existing Scout items.</param>
    [McpServerTool(Name = "forge.evidence.map"), Description("Advises the manual Evidence check through a configured Advisor in a bounded call. Call again until complete is true or checkedThisCall is zero. Supply all Change points and optional implications citing Scout item ids. A credential is never an argument: configuration comes only from the server process environment.")]
    public static Task<string> EvidenceMap(SessionRoots roots,
                                            [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                            [Description("Run id from forge.begin.")] string runId,
                                            [Description("All Run Change points: 1-200 non-blank strings, each at most 2000 characters.")] string[] changePoints,
                                            CancellationToken ct,
                                            [Description("Configured Advisor id; omit when exactly one is configured.")] string? advisor = null,
                                            [Description("At most 500 derived obligations, each with text and 1-20 existing Scout item ids in derivedFrom.")] EvidenceImplication[]? implications = null) =>
        EvidenceMap(roots, workspaceRoot, runId, changePoints, ct, Environment.GetEnvironmentVariable,
                    (environment, id) => AdvisorCatalog.Default.Select(environment, id), TimeProvider.System, advisor, implications);

    /// <summary>Runs the tool with local configuration, service construction and time seams.</summary>
    /// <param name="roots">The host's session roots.</param>
    /// <param name="workspaceRoot">The absolute repository root.</param>
    /// <param name="runId">The Run receiving the map.</param>
    /// <param name="changePoints">All accumulated Change points.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <param name="environment">The process environment lookup, never logged.</param>
    /// <param name="advisorFactory">Selects and constructs a configured Advisor.</param>
    /// <param name="clock">The clock for portion budgets and usage.</param>
    /// <param name="advisor">An optional explicit Advisor id.</param>
    /// <param name="implications">Optional derived obligations.</param>
    internal static async Task<string> EvidenceMap(SessionRoots roots, string workspaceRoot, string runId,
                                                    string[] changePoints, CancellationToken ct,
                                                    Func<string, string?> environment,
                                                    Func<Func<string, string?>, string?, IAdvisor> advisorFactory,
                                                    TimeProvider clock, string? advisor = null,
                                                    EvidenceImplication[]? implications = null)
    {
        var mapper = new EvidenceMapper(clock);
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, workspaceRoot, "forge.evidence.map",
            [("advisor", advisor), ("changePoints", changePoints?.Length.ToString(CultureInfo.InvariantCulture)),
             ("implications", implications?.Length.ToString(CultureInfo.InvariantCulture))], async () =>
            {
                if (!EvidenceMaps.TryAdd(run.Path, 0))
                    throw new ArgumentRejectedException("an Evidence map is already running for this Run");
                try
                {
                    var plan = File.Exists(run.PlanPath) ? run.ReadPlan() : null;
                    if (string.IsNullOrWhiteSpace(plan))
                        throw new ArgumentRejectedException("forge.evidence.map requires a non-blank PLAN.md");
                    if (changePoints is null || changePoints.Length == 0)
                        throw new ArgumentRejectedException("changePoints must contain 1-200 entries");
                    if (changePoints.Length > 200)
                        throw new ArgumentRejectedException("changePoints[200] exceeds the limit of 200 Change points");
                    for (var index = 0; index < changePoints.Length; index++)
                    {
                        if (string.IsNullOrWhiteSpace(changePoints[index]) || changePoints[index].Length > 2000)
                            throw new ArgumentRejectedException($"changePoints[{index}] must be non-blank and at most 2000 characters");
                        SensitiveInput.Guard(changePoints[index], $"changePoints[{index}]");
                    }
                    var state = run.ReadState();
                    IReadOnlyList<ScoutAnswerEntry> entries;
                    try { entries = run.ReadScoutAnswers(); }
                    catch (JsonException) { throw new ArgumentRejectedException("scout-answers.json is malformed"); }
                    EvidenceIndex items;
                    try { items = EvidenceIndex.Of(entries, state.ScoutAnswers, implications, state.WorkspaceRoot); }
                    catch (JsonException) { throw new ArgumentRejectedException("scout-answers.json is malformed"); }
                    var selected = advisorFactory(environment, advisor);
                    try { mapper.Prepare(plan, items, changePoints, selected, run.EvidenceMapPath, state.ReviewRounds); }
                    catch (JsonException) { throw new ArgumentRejectedException("evidence-map.json is malformed"); }

                    EvidenceMapResult mapped;
                    try
                    {
                        var map = await mapper.MapPreparedAsync(ct).ConfigureAwait(false);
                        mapped = map.Result;
                    }
                    finally
                    {
                        try { run.AppendFlowEvidenceMap(mapper.Progress); }
                        catch (Exception error)
                        {
                            run.Log.Write("error", SOURCE, "evidence.flow.write.failed", ("error", error.GetType().Name));
                        }
                        WorkerTelemetryFile.Append(run.TelemetryPath, run.Log, AdvisorUsageRecord.From(mapper.Progress, selected));
                    }
                    var result = JsonSerializer.SerializeToNode(mapped, ForgeToolJson.Default.EvidenceMapResult)!.AsObject();
                    result["documents"] = JsonSerializer.SerializeToNode(Documents(run), ForgeToolJson.Default.RunDocuments);
                    return result.ToJsonString();
                }
                finally { EvidenceMaps.TryRemove(run.Path, out _); }
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
        return await LoggedAsync(run, workspaceRoot, "forge.instructions.set",
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
    [McpServerTool(Name = "forge.plan.write"), Description("Writes the complete plan draft to `PLAN.md` for the first draft or a rewrite, without running a critic. A local revision can use forge.plan.patch. Returns the outline computed from the plan read back after writing (null for blank plan text) and its path under `documents`; show the user that path, then review with `planDraft` omitted. A write over an already-approved plan takes the approval back and resets the build progress; say so out loud when it happens.")]
    public static async Task<string> WritePlan(SessionRoots roots,
                                               [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                               [Description("Run id from forge.begin.")] string runId,
                                               [Description("The current plan draft, as markdown. The whole plan, not a summary of it.")] string planDraft,
                                               CancellationToken ct)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, workspaceRoot, "forge.plan.write", [("planDraft", planDraft)],
            () =>
            {
                PlanReview.Write(run, planDraft);
                var plan = run.ReadPlan();
                var outline = PlanOutline.IsBlank(plan) ? null : PlanOutline.Of(plan);
                return Task.FromResult(EvidenceMapCurrency.Attach(
                    JsonSerializer.Serialize(new PlanWriteResult(run.RunId, outline, Documents(run)), ForgeToolJson.Default.PlanWriteResult),
                    EvidenceMapCurrency.Warning(run, run.ReadState().ReviewRounds)));
            });
    }

    /// <summary>
    /// Reads the current plan's snapshot selectors without returning its bodies.
    /// </summary>
    /// <param name="roots">The host's session roots.</param>
    /// <param name="workspaceRoot">The absolute workspace root.</param>
    /// <param name="runId">The run whose plan is read.</param>
    /// <param name="ct">Cancels the call.</param>
    [McpServerTool(Name = "forge.plan.outline"), Description("Returns the current plan hash and document-order Edit targets with snapshot IDs s0, s1, …, kind (preamble, heading, task), trimmed heading/task first-line labels of at most 80 characters, target hashes and sizes in UTF-16 characters. Plan text is UTF-8 read from PLAN.md with its leading byte-order mark dropped. Refuses missing or blank text (whitespace after setting aside leading byte-order marks); call forge.plan.write first. Targets start at line beginnings, never overlap and concatenate to the exact plan text, including each line's ending break. Outside Approach, headings begin at column 0 with 1–6 # followed by space or tab. With exactly one build-recognized ## Approach, the build's own patterns run on CRLF-normalized text and positions map back: its heading runs to the first task, tasks start on the first digit's line and run to the next task or section end; other headings inside are not targets. The section-end line starts a heading target even if not an ordinary heading. Otherwise only heading lines divide the plan. Nonempty preamble precedes the first target; its first nonblank line is its label, or (blank). Fences are not considered. Hashes are the first 16 lowercase hex SHA-256 characters of UTF-8 text. IDs hold only for this snapshot. Returns no plan text beyond labels.")]
    public static async Task<string> OutlinePlan(SessionRoots roots,
                                                 [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                                 [Description("Run id from forge.begin.")] string runId,
                                                 CancellationToken ct)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct).ConfigureAwait(false);
        return await LoggedAsync(run, workspaceRoot, "forge.plan.outline", [], () =>
        {
            if (!File.Exists(run.PlanPath)) throw new ArgumentRejectedException("Write the plan first with forge.plan.write.");
            var plan = run.ReadPlan();
            if (PlanOutline.IsBlank(plan)) throw new ArgumentRejectedException("Write the plan first with forge.plan.write.");
            return Task.FromResult(JsonSerializer.Serialize(new PlanOutlineResult(run.RunId, PlanOutline.Of(plan), Documents(run)),
                                                            ForgeToolJson.Default.PlanOutlineResult));
        }).ConfigureAwait(false);
    }

    /// <summary>Applies local edits against one exact plan snapshot.</summary>
    /// <param name="roots">The host's session roots.</param>
    /// <param name="workspaceRoot">The absolute workspace root.</param>
    /// <param name="runId">The run whose plan is patched.</param>
    /// <param name="baseHash">The plan hash from its current outline.</param>
    /// <param name="edits">The operations and exact target selectors against that outline.</param>
    /// <param name="ct">Cancels the call.</param>
    [McpServerTool(Name = "forge.plan.patch"), Description("Revises local targets against baseHash from forge.plan.outline, with a required nonempty edits array. Each edit requires op, target ID and targetHash: replace, delete, insertBefore or insertAfter. Replace/insert require nonblank content; delete permits no content, even an empty string. Refuses the whole patch for no_plan, stale plan/target hash, missing edit/fields, unknown target/operation, invalid content, two replacements/deletions on one target, two insertions at the same position (including after X/before its successor), or insertion anchored on a deleted target. Refusals identify the edit index where applicable and change no protected file. All edits use one snapshot and apply independently of list order; untouched text is exact. Written line breaks become CRLF only when every existing break is CRLF and there is at least one, otherwise LF; append a final break if missing and keep supplied trailing blank lines. Insert a separator in that style between pieces only when the preceding piece lacks a final break. Remove leading byte-order marks from the complete result before checking, writing, hashing or measuring it. Refuses blank or identical results, and any result the build task parser rejects when the base parses, including unreadable/overflowing task numbers; no renumbering. A successful patch withdraws approval and forgets the Builder session before persisting the complete plan, but keeps the build progress before the first task whose text it changed. Task texts as the build parses them are compared by position, so changing, inserting or removing task k at or below the built count — prose inserted after task k joins it — sends the build back to k − 1 tasks, trimming their changed-file hand-over and dropping a pending gate failure; a patch changing no built task's text, such as one to the title, Requirements, Gates or unbuilt tasks, keeps it all. No Worker, gate, review round or cap change. Returns the new outline, tasksKept and firstTaskOwed (null when every task is built), and documents, with no plan text beyond labels; the Flow log records each change's op/target/label and the character and UTF-8 byte sizes (payload JSON, supplied text, plan before, removed, written excluding separators, plan after). Missing/blank plans require forge.plan.write. Before replacement failures leave the plan unchanged and say whether approval was withdrawn; a failure of the patch's Flow entry says the plan was patched and gives its new hash. There is no lock or multi-file transaction; the hash check and replacement are not atomic. A replay conflicts as stale: read forge.plan.outline and redo edits against its new hash; never resend the refused patch or infer success from equal text.")]
    public static async Task<string> PatchPlan(SessionRoots roots,
                                               [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                               [Description("Run id from forge.begin.")] string runId,
                                               [Description("The exact current plan hash from forge.plan.outline.")] string baseHash,
                                               [Description("Edits against the same outline, each with op, target, targetHash and optional content.")] IReadOnlyList<PlanEdit?> edits,
                                               CancellationToken ct)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct).ConfigureAwait(false);
        var elapsed = Stopwatch.StartNew();
        var sizes = PlanPatchSizes.Payload(edits);
        return await LoggedAsync(run, workspaceRoot, "forge.plan.patch",
            [("baseHash", PlanPatch.Shown(baseHash, PlanValueKind.Hash)),
             ("editCount", (edits?.Count ?? 0).ToString(CultureInfo.InvariantCulture)),
             .. sizes.LogFields().Where(field => field.Name.StartsWith("payload", StringComparison.Ordinal))], () =>
        {
            var outcome = "failed_unchanged";
            try
            {
                var patch = PlanReview.Patch(run, baseHash, edits);
                sizes = patch.Sizes;
                outcome = "applied";
                return Task.FromResult(EvidenceMapCurrency.Attach(JsonSerializer.Serialize(
                    new PlanPatchResult(run.RunId, patch.Outline, patch.TasksKept, patch.FirstTaskOwed, Documents(run)),
                    ForgeToolJson.Default.PlanPatchResult), EvidenceMapCurrency.Warning(run, run.ReadState().ReviewRounds)));
            }
            catch (PlanPatchRejectedException error)
            {
                sizes = error.Sizes;
                outcome = error.Reason;
                throw;
            }
            catch (PlanPatchFailedException error)
            {
                sizes = error.Sizes;
                outcome = error.Outcome;
                throw;
            }
            finally
            {
                run.Log.Write("info", SOURCE, "plan.patch",
                    [("outcome", outcome), ("editCount", (edits?.Count ?? 0).ToString(CultureInfo.InvariantCulture)),
                     ("elapsedMs", elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)), .. sizes.LogFields()]);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// One round only. The critic judges the draft; revising it and calling again is the
    /// orchestrator's job, because the revision needs the interview context.
    /// </summary>
    /// <param name="catalogs">The model catalogs used to confirm the selected speed.</param>
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
    /// <param name="fast">Whether the critic must run at the confirmed Fast tier.</param>
    [McpServerTool(Name = "forge.plan.review"), Description("Applies one typed plan decision batch, then runs one Critic round against the active plan-phase ledger projection. Use one decisionBatchId per logical set and repeat the exact batch when retrying an invalid Critic response. Plan closures use addressedByRevision or duplicateOf here. The result includes the critique, Flow audit and plan under `documents`.")]
    public static async Task<string> ReviewPlan(CatalogCache catalogs,
                                                SessionRoots roots,
                                                [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                                [Description("Run id from forge.begin.")] string runId,
                                                [Description("Model for the critic.")] string model,
                                                CancellationToken ct,
                                                [Description("The plan draft to review. Omit it when forge.plan.write or forge.plan.patch left this round's plan file, which is the flow to use; passing it writes it here instead, and the round then starts only once the whole draft has reached the server.")] string? planDraft = null,
                                                [Description("Optional effort level.")] string? effort = null,
                                                [Description("Vendor: claude or codex. Defaults to claude.")] string? vendor = null,
                                                [Description("What you changed in the plan in answer to the previous round's findings, as markdown. Required from the second round on, and recorded in the flow log so the user sees your turn between the critic's.")] string? revision = null,
                                                [Description("Optional markdown list of findings you decided not to act on, each with its reason. Recorded in the flow log; typed ledger decisions are what the next round's critic treats as settled.")] string? deferred = null,
                                                [Description("At the cap, this raises this run's review-round cap by exactly one and runs the round; below the cap it does nothing. Spent by this call, so a further round past the new cap needs a fresh answer. Never pass true without having shown the user where the run stands and asked. Without it a round at the cap still runs when the last round found fewer findings than the one before and no blocker; its result then carries continuedAutomatically.")] bool userGrantedRound = false,
                                                [Description("Optional typed authoritative decisions applied before the critic runs. Reuse the same decisionBatchId and identical decisions when retrying this call.")] OrchestratorDecisionBatch? decisions = null,
                                                [Description(FAST_DESCRIPTION)] bool fast = false)
    {
        if (revision is { Length: > 0 }) SensitiveInput.Guard(revision, "the plan revision");
        if (deferred is { Length: > 0 }) SensitiveInput.Guard(deferred, "the deferred findings");
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, workspaceRoot, "forge.plan.review",
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

                var json = ReviewCap.Attach(JsonSerializer.Serialize(new CritiqueResult(critique, Documents(run)),
                                                                     ForgeToolJson.Default.CritiqueResult),
                                            act.ContinuedAutomatically);
                return SpeedWarnings.Attach(run, EvidenceMapCurrency.Attach(json, act.EvidenceMapWarning), act.SpeedWarning);
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
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    /// <param name="plan">The complete plan to render, or null for <c>PLAN.md</c> as it stands.</param>
    [McpServerTool(Name = "forge.plan.show"), McpAppUi(ResourceUri = PlanCanvas.ResourceUri), Description("Renders the plan as a document in the host's own UI, with the working-tree drift beside it. Call it only when forge.begin reported profile `Canvas`, immediately before you ask the user to approve — and still ask, because this records nothing. On a `Text` profile nothing renders, so show the plan in the chat instead. Omit plan to show PLAN.md as forge.plan.write and forge.plan.patch left it; the result's planHash names what was shown, for forge.plan.confirm.")]
    public static async Task<string> ShowPlan(SessionRoots roots,
                                              [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                              [Description("Run id from forge.begin.")] string runId,
                                              CancellationToken ct,
                                              [Description("Optional plan to show instead of PLAN.md, as markdown: the whole plan, not a summary of it. Omit it to show PLAN.md.")] string? plan = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, workspaceRoot, "forge.plan.show", [("plan", plan)],
            async () =>
            {
                var state = run.ReadState();
                var shown = plan ?? CurrentPlan(run, "forge.plan.show");
                var drifted = await run.ReadBaseline(state.BaselineHead)
                                       .DriftedFilesAsync(new GitClient(workspaceRoot), ct);

                return JsonSerializer.Serialize(
                    new PlanViewResult(state.RunId, shown, PlanOutline.Hash(shown), drifted, state.ReviewRounds, state.Approved),
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
    /// <param name="approved">The user's approval decision.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    /// <param name="gateEnvironment">Optional environment variables required by gate commands.</param>
    /// <param name="builderRoots">Compatibility-only absolute paths retained in the Run; vendors ignore them.</param>
    /// <param name="decisions">Optional final plan decisions, applied only with an approval.</param>
    /// <param name="fullGate">Optional timing for full host verification of targeted fixes; omission preserves the saved choice.</param>
    /// <param name="plan">The complete plan presented for approval, or null for <c>PLAN.md</c> as it stands.</param>
    /// <param name="planHash">The hash of the plan the user was shown, or null to approve without checking it.</param>
    /// <param name="rebuildFrom">A built task the user asked to rebuild from, or null to keep the build progress.</param>
    /// <returns>The recorded decision, the task count and the filtered drift, as JSON.</returns>
    [McpServerTool(Name = "forge.plan.confirm"), Description("With approved true, applies the same typed plan decisions as forge.plan.review, then refuses approval while any active plan finding is unresolved. With approved false, decisions are forbidden and the ledger is unchanged. Approval records tasks and optional fullGate=beforeNextRound|final (case-sensitive). Omitted timing and settings are preserved; explicit settings replace, empty settings clear. Approval clears the global gate failure, preserving attempts. Approved false leaves PLAN/state/ledger/approval unchanged; invalid fullGate is still refused. Saved timing is read first and a corrupt value stops the call unchanged. Confirm does not select gate mode or validate Fix gate. Code-review entries do not block confirmation. Omit plan to approve PLAN.md as forge.plan.write and forge.plan.patch left it; a plan passed instead replaces PLAN.md. Pass planHash, the planHash of the plan the user was shown, and a plan with any other hash is refused before anything changes. Approval keeps the build progress a patch left; rebuildFrom k, only with approved true and only for a built task 1..tasksCompleted, sends the build back to task k as a patch into task k would, when the user says a change outside the tasks invalidates built ones.")]
    public static async Task<string> ConfirmPlan(SessionRoots roots,
                                                 [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                                 [Description("Run id from forge.begin.")] string runId,
                                                 [Description("What the user answered. Show them the plan and the filtered drift excluding `CONTEXT.md` and `docs/adr/**`, ask, and pass what they say; never decide this yourself.")] bool approved,
                                                 CancellationToken ct,
                                                 [Description("Environment variables for server-owned task gates and selected full or targeted fix gates, e.g. {\"CD_TEST_SQL_CONN\": \"Server=…\"}. Ask the user for what the plan's gates need before you confirm; the values are kept in the run state and only their names are logged.")] Dictionary<string, string>? gateEnvironment = null,
                                                 [Description("Compatibility-only absolute paths retained in the run state. Vendors ignore this field; it does not control Builder permissions.")] string[]? builderRoots = null,
                                                 [Description("Optional complete typed plan decision batch. Approved confirmation applies it before checking for unresolved active plan entries; refused confirmation rejects it without mutation.")] OrchestratorDecisionBatch? decisions = null,
                                                 [Description("Full host verification timing for targeted fixes: beforeNextRound or final, case-sensitive. Omission preserves the saved timing; old runs default to beforeNextRound. Changing timing does not verify pending fixes.")] string? fullGate = null,
                                                 [Description("Optional plan to approve instead of PLAN.md, as markdown; it replaces PLAN.md. Omit it to approve PLAN.md as it stands.")] string? plan = null,
                                                 [Description("The planHash of the plan the user was shown: from forge.plan.show, or from the outline forge.plan.write, forge.plan.patch or forge.plan.outline answered with. A plan with another hash is refused.")] string? planHash = null,
                                                 [Description("Optional built task to rebuild from, 1 to the run's tasksCompleted, with approved true only. Pass it only when the user, shown how many tasks the approval keeps, says a change invalidates built tasks; omit it to keep them.")] int? rebuildFrom = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        // The gate environment is logged by name only: a connection string is exactly what it holds.
        return await LoggedAsync(run, workspaceRoot, "forge.plan.confirm",
            [("approved", approved.ToString()), ("plan", plan), ("planHash", planHash),
             ("gateEnvironment", gateEnvironment is { Count: > 0 } ? string.Join(", ", gateEnvironment.Keys) : null),
             ("builderRoots", builderRoots is { Length: > 0 } ? string.Join(", ", builderRoots) : null),
             ("decisionBatchId", decisions?.DecisionBatchId), ("fullGate", fullGate),
             ("rebuildFrom", rebuildFrom?.ToString(CultureInfo.InvariantCulture))],
            async () =>
            {
                var state = run.ReadState();
                var timing = fullGate is null ? state.FullGate : FixGatePolicy.FullGateTiming(fullGate);
                var gates = GateSettings.Validate(gateEnvironment, builderRoots);
                if (!approved && decisions is not null)
                    throw new ArgumentRejectedException("forge.plan.confirm(approved:false) does not accept decisions");
                if (rebuildFrom is { } task && (!approved || task < 1 || task > state.TasksCompleted))
                    throw new ArgumentRejectedException(
                        !approved ? "forge.plan.confirm(approved:false) does not accept rebuildFrom"
                        : state.TasksCompleted == 0 ? $"rebuildFrom {task} names no built task: no task is built yet"
                        : $"rebuildFrom {task} names no built task: pass a task from 1 to {state.TasksCompleted}");
                var confirmed = plan ?? CurrentPlan(run, "forge.plan.confirm");
                if (planHash is not null && PlanOutline.Hash(confirmed) is var hash && hash != planHash)
                    throw new ArgumentRejectedException(
                        $"planHash {planHash} is not the plan's hash: {(plan is null ? "PLAN.md" : "the plan passed")} hashes to {hash}. " +
                        "Show the user the plan as it stands and confirm with its hash.");
                var tasks = PlanTasks.Parse(confirmed);

                if (!approved) return Serialized(new ApproveResult(false, 0));

                if (decisions is not null)
                {
                    try
                    {
                        var response = run.ReadDecisionLedger().Apply(decisions, LedgerPhase.PlanReview);
                        run.AppendFlowDecisionBatch("Plan confirmation", decisions, response, LedgerPhase.PlanReview);
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

                var briefChanged = state.Approved
                                   && !string.Equals(PlanTasks.Brief(run.ReadPlan()), PlanTasks.Brief(confirmed),
                                                     StringComparison.Ordinal);

                // Rewriting PLAN.md with what was just read from it would only drop a leading BOM.
                if (plan is not null) run.WritePlan(plan);
                var progress = briefChanged ? BuilderSession.Forget(state) : state;
                if (rebuildFrom is { } first) progress = PlanReview.Rewind(progress, first - 1);
                run.WriteState(progress with
                {
                    Approved = true,
                    FullGate = timing,
                    GateEnvironment = gateEnvironment is null ? state.GateEnvironment : gates.Environment,
                    BuilderRoots = builderRoots is null ? state.BuilderRoots : gates.BuilderRoots,
                    PendingGateFailure = null
                });
                if (rebuildFrom is { } rebuilt) run.AppendFlowRebuildFrom(rebuilt, state.TasksCompleted);

                return Serialized(new ApproveResult(true, tasks.Count));
            });
    }

    [McpServerTool(Name = "forge.build.next"), Description("Builds the next unfinished task of the approved plan, then runs the task's gate command on the host and reports it under `build.result.gate`. The Builder must not run an executable task gate, including on retries. Separate Builder checks are optional; if none ran because the gate belongs to the server, verification is unavailable with that explicit reason, while completed implementation remains done. A task whose gate exits non-zero comes back with status `gate_failed`, is not counted, and is retried by the next call with the gate's output in front of the builder. The gate runs for a builder that reports `blocked` with a verification of `unavailable` too — it did the work and could not prove it — and a gate that passes then rewrites the status to `done` and counts the task; read `build.result.verification` for what the builder itself could not check. A gate that is a condition rather than a command is `not_executable`, and the builder's own verification is all there is. A turn that ended with a command still running in the background comes back as `background_killed` with the gate `not_run`: the session killed the command, the task is not counted, and the next call retries it.")]
    public static async Task<string> BuildNext(CatalogCache catalogs,
                                               SessionRoots roots,
                                               [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                               [Description("Run id from forge.begin.")] string runId,
                                               [Description("Model for the builder.")] string model,
                                               CancellationToken ct,
                                               [Description("Optional effort level.")] string? effort = null,
                                               [Description("Vendor: claude or codex. Defaults to claude.")] string? vendor = null,
                                               [Description(FAST_DESCRIPTION)] bool fast = false)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, workspaceRoot, "forge.build.next",
            [("vendor", vendor), ("model", model), ("effort", effort), ("fast", Flag(fast))],
            async () =>
            {
                var builder = VendorFactory.Create(vendor, workspaceRoot);
                var selection = await FastTier.ConfirmAsync(catalogs, builder, new Selection(model, effort, fast), workspaceRoot, ct);
                var act = new Build(builder, new PromptLibrary());
                var outcome = await act.NextAsync(run, selection, ct);

                var result = new JsonObject
                {
                    ["build"] = BuildOutcomeView.Of(outcome),
                    ["documents"] = JsonSerializer.SerializeToNode(Documents(run), ForgeToolJson.Default.RunDocuments)
                };
                return SpeedWarnings.Attach(run, result.ToJsonString(), act.SpeedWarning);
            });
    }

    /// <summary>
    /// One round only, like plan review. The loop used to live inside this call on the premise that
    /// nothing in it needed the interview context; a critic asking for work the approved plan
    /// excluded disproved that, so the orchestrator now takes a turn between critic and builder —
    /// see docs/adr/0005.
    /// </summary>
    /// <param name="catalogs">The vendor catalogues a Fast request is confirmed against.</param>
    /// <param name="roots">The session roots advertised by the MCP host.</param>
    /// <param name="workspaceRoot">The run's workspace root.</param>
    /// <param name="runId">The run whose code is reviewed.</param>
    /// <param name="model">The critic model.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    /// <param name="effort">The optional critic effort level.</param>
    /// <param name="vendor">The critic vendor, defaulting to Claude.</param>
    /// <param name="userGrantedRound">Whether the user granted exactly one round beyond the cap.</param>
    /// <param name="fast">Whether the critic runs at the vendor's Fast tier.</param>
    /// <param name="excludePaths">Paths this round leaves out of the review window.</param>
    [McpServerTool(Name = "forge.review.code"), Description("Runs one decision-free code-review round against the approved plan and the code-phase ledger projection. Saved fullGate=beforeNextRound blocks review while fixes await full host verification, before cap/grant or worker startup. Saved final allows review and returns current pendingFullGateFindingIds, including an empty array. Critic assessments do not verify pending fixes. The Critic assesses every displayed unresolved ID and may only propose reopening settled IDs. Apply dispositions, reopening answers, duplicate closures and fixes through forge.review.fix. A prompt longer than the critic's vendor accepts — codex takes 1,048,576 characters — gives the critic its untracked files by path, then the tracked files' diffs too, and says so in the summary; one still too long with every file given by path is refused before the critic starts, with its sizes and nothing counted, and excludePaths narrows the round.")]
    public static async Task<string> ReviewCode(CatalogCache catalogs,
                                                SessionRoots roots,
                                                [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                                [Description("Run id from forge.begin.")] string runId,
                                                [Description("Model for the critic.")] string model,
                                                CancellationToken ct,
                                                [Description("Optional effort level.")] string? effort = null,
                                                [Description("Vendor: claude or codex. Defaults to claude.")] string? vendor = null,
                                                [Description("At the cap, this raises this run's code-review-round cap by exactly one and runs the round; below the cap it does nothing. Spent by this call, so a further round past the new cap needs a fresh answer. Never pass true without having shown the user where the run stands and asked. Without it a round at the cap still runs when the last round found fewer findings than the one before and no blocker; its result then carries continuedAutomatically.")] bool userGrantedRound = false,
                                                [Description(FAST_DESCRIPTION)] bool fast = false,
                                                [Description(EXCLUDE_PATHS_DESCRIPTION)] string[]? excludePaths = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, workspaceRoot, "forge.review.code",
            [("vendor", vendor), ("model", model), ("effort", effort), ("fast", Flag(fast)),
             ("userGrantedRound", userGrantedRound ? "true" : "false"),
             ("excludePaths", excludePaths is { Length: > 0 } ? string.Join(", ", excludePaths) : null)],
            async () =>
            {
                var scope = ReviewScope.From(excludePaths);
                CodeReview.RequireReady(run, run.ReadState());
                var critic = VendorFactory.Create(vendor, workspaceRoot);
                var selection = await FastTier.ConfirmAsync(catalogs, critic, new Selection(model, effort, fast), workspaceRoot, ct);
                var act = new CodeReview(critic, new PromptLibrary(), new GitClient(workspaceRoot));
                var critique = await act.ReviewAsync(run, selection, userGrantedRound, ct, scope);

                return SpeedWarnings.Attach(run, ReviewCap.Attach(JsonSerializer.Serialize(new CritiqueResult(critique, Documents(run)),
                                                                                           ForgeToolJson.Default.CritiqueResult),
                                                                  act.ContinuedAutomatically),
                                            act.SpeedWarning);
            });
    }

    [McpServerTool(Name = "forge.review.fix"), Description("Applies typed code-review decisions, including duplicate and host-verified closures and raises, then independently fixes exactly fixFindingIds under fixAttemptId. Decisions-only calls start no Builder or gate; a batch with raises is decisions-only and answers with raisedFindingIds. Retry retained or cut-short work with the same attempt and exact ID set; gate is full by default or targeted for only the approved Fix gate. Preserve gate with the attempt and exact IDs on retry; a conflicting set or mode is refused and a saved terminal attempt returns its result without another Builder or gate, annotated with current pending IDs. Targeted success leaves findings pending full host verification by the Orchestrator at saved fullGate timing; short success and Critic assessment do not close them. Explicit gate requires fix IDs; empty, whitespace or unknown modes are refused before decisions or workers. New/nonterminal targeted fixes validate the approved Fix gate before decisions; terminal replay does not validate the current plan.")]
    public static async Task<string> ReviewFix(CatalogCache catalogs,
                                               SessionRoots roots,
                                               [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                               [Description("Run id from forge.begin.")] string runId,
                                               [Description("Model for the builder.")] string model,
                                               CancellationToken ct,
                                               [Description("Optional effort level.")] string? effort = null,
                                               [Description("Vendor: claude or codex. Defaults to claude.")] string? vendor = null,
                                               [Description("Optional complete typed code-review decision batch.")] OrchestratorDecisionBatch? decisions = null,
                                               [Description("Required when fixFindingIds is non-empty; identifies the retryable fix attempt.")] string? fixAttemptId = null,
                                               [Description("Exact ledger finding IDs to fix. The Builder receives only their verbatim ledger findings.")] string[]? fixFindingIds = null,
                                               [Description(NOTE_DESCRIPTION)] string? note = null,
                                               [Description(FAST_DESCRIPTION)] bool fast = false,
                                               [Description("Fix verification: exactly full (default) or targeted (case-sensitive). Requires non-empty fixFindingIds.")] string? gate = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, workspaceRoot, "forge.review.fix",
            [("vendor", vendor), ("model", model), ("effort", effort), ("fast", Flag(fast)),
             ("decisionBatchId", decisions?.DecisionBatchId), ("fixAttemptId", fixAttemptId),
             ("fixFindingIds", fixFindingIds is { Length: > 0 } ? string.Join(", ", fixFindingIds) : null),
             ("note", note), ("gate", gate)],
            async () =>
            {
                try
                {
                    OrchestrationPreflight.Validate(run, "review.fix", decisions, fixAttemptId, fixFindingIds, note, gate);
                }
                catch (DecisionLedgerRequestException error)
                {
                    if (decisions is not null)
                        run.AppendFlowDecisionRejected("review.fix preflight", decisions.DecisionBatchId, error.Message);
                    throw;
                }
                var builder = VendorFactory.Create(vendor, workspaceRoot);
                var selection = await FastTier.ConfirmAsync(catalogs, builder, new Selection(model, effort, fast), workspaceRoot, ct);
                var act = new ReviewFix(builder, new PromptLibrary());
                var result = await act.FixAsync(run, selection, decisions, fixAttemptId,
                                                fixFindingIds, ct, note, gate);

                var json = JsonSerializer.Serialize(new ReviewFixResult(result, Documents(run)), ForgeToolJson.Default.ReviewFixResult);
                return SpeedWarnings.Attach(run, RaisedFindings.Attach(json, act.RaisedFindingIds), act.SpeedWarning);
            });
    }

    [McpServerTool(Name = "forge.work.start"), Description("Starts a worker act in the background: a Builder act — build.next or review.fix — that may outlast the host's clock on a single call, or a scout question to overlap with your own work. review.fix uses the same typed decisions, decisionBatchId and fix-attempt rules as its direct tool; ledger IDs, phases, states, batch conflicts and fix sets are preflighted before a job is created. Optional case-sensitive gate=full|targeted requires fix IDs when explicit; preserve gate, attempt and exact IDs on retry. Targeted success leaves fixes pending full host verification. Empty, whitespace, unknown modes and new/nonterminal invalid Fix gates are refused before decisions or jobs; terminal replay uses its saved result. scout takes only question and sessionMode and runs on the persisted Scout selection. A fresh scout job runs beside other fresh scout jobs; every other job runs alone. Poll until terminal, then fetch. If started is false, the jobs in activeJobIds kept it from starting: poll and fetch them, then start again.")]
    public static Task<string> StartWork(JobRegistry registry,
                                         CatalogCache catalogs,
                                         SessionRoots roots,
                                         [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                         [Description("Run id from forge.begin.")] string runId,
                                         [Description("Worker act: build.next, review.fix or scout.")] string act,
                                         CancellationToken ct,
                                         [Description("Model for the builder. Not for scout.")] string? model = null,
                                         [Description("Optional effort level. Not for scout.")] string? effort = null,
                                         [Description("Vendor: claude or codex. Defaults to claude. Not for scout.")] string? vendor = null,
                                         [Description("Optional typed decision batch, accepted only by review.fix.")] OrchestratorDecisionBatch? decisions = null,
                                         [Description("Required by review.fix when fixFindingIds is non-empty.")] string? fixAttemptId = null,
                                         [Description("Exact ledger finding IDs for review.fix. Empty means decisions-only.")] string[]? fixFindingIds = null,
                                         [Description("For review.fix only. " + NOTE_DESCRIPTION)] string? note = null,
                                         [Description(FAST_DESCRIPTION + " Not for scout.")] bool fast = false,
                                         [Description("For review.fix only: exactly full (default) or targeted (case-sensitive). Requires non-empty fixFindingIds.")] string? gate = null,
                                         [Description("For scout only: the one bounded reconnaissance question, from 1 to 8,000 characters.")] string? question = null,
                                         [Description("For scout only: exactly `fresh` or `continue`. A continue runs alone.")] string? sessionMode = null)
    {
        // VendorFactory.Create is deliberately the one line not covered by the factory-seam tests.
        return StartWork(registry, roots, workspaceRoot, runId, act, model, effort, vendor, ct,
                         id => VendorFactory.Create(id, workspaceRoot), null, decisions, fixAttemptId,
                         fixFindingIds, catalogs, fast, note, gate, question, sessionMode);
    }

    internal static Task<string> StartWork(JobRegistry registry,
                                           SessionRoots roots,
                                           string workspaceRoot,
                                           string runId,
                                           string act,
                                           string model,
                                           string? effort,
                                           string? vendor,
                                           CancellationToken ct,
                                           Func<IVendor> vendorFactory,
                                           OrchestratorDecisionBatch? decisions = null,
                                           string? fixAttemptId = null,
                                           string[]? fixFindingIds = null, string? gate = null) =>
        StartWork(registry, roots, workspaceRoot, runId, act, model, effort, vendor, ct, _ => vendorFactory(),
                  null, decisions, fixAttemptId, fixFindingIds, gate: gate);

    internal static async Task<string> StartWork(JobRegistry registry,
                                                 SessionRoots roots,
                                                 string workspaceRoot,
                                                 string runId,
                                                 string act,
                                                 string? model,
                                                 string? effort,
                                                 string? vendor,
                                                 CancellationToken ct,
                                                 Func<string, IVendor> vendorFactory,
                                                 PromptLibrary? prompts = null,
                                                 OrchestratorDecisionBatch? decisions = null,
                                                 string? fixAttemptId = null,
                                                 string[]? fixFindingIds = null,
                                                 CatalogCache? catalogs = null,
                                                 bool fast = false,
                                                 string? note = null, string? gate = null,
                                                 string? question = null, string? sessionMode = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, workspaceRoot, "forge.work.start",
            [("act", act), ("vendor", vendor), ("model", model), ("effort", effort), ("fast", Flag(fast)),
             ("decisionBatchId", decisions?.DecisionBatchId),
             ("fixAttemptId", fixAttemptId),
             ("fixFindingIds", fixFindingIds is { Length: > 0 } ? string.Join(", ", fixFindingIds) : null),
             ("note", note), ("gate", gate), ("sessionMode", sessionMode)],
            async () =>
            {
                if (act == "review.fix") run.ReadState();
                var requested = model is null ? null : new Selection(model, effort, fast);
                WorkAct.ValidateArguments(act, requested, decisions, fixAttemptId, fixFindingIds, note, gate,
                                          question, sessionMode);
                if (act == "scout")
                {
                    if (vendor is not null || effort is not null || fast)
                        throw new ArgumentRejectedException("scout takes its vendor, effort and fast only from the persisted Scout selection");
                    SensitiveInput.Guard(question!, "the Scout question");
                }

                try
                {
                    OrchestrationPreflight.Validate(run, act, decisions, fixAttemptId, fixFindingIds, note, gate);
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
                var selection = requested is null
                    ? null
                    : await FastTier.ConfirmAsync(catalogs ?? new CatalogCache((id, _) => vendorFactory(id!)),
                                                  vendorForAct, requested, workspaceRoot, ct);
                var workAct = new WorkAct(vendorForAct, prompts ?? new PromptLibrary());
                var started = registry.Start(run.Path, act,
                    jobCt => workAct.RunAsync(act, run, selection, jobCt, decisions,
                                               fixAttemptId, fixFindingIds, note, gate, question, sessionMode),
                    shared: act == "scout" && sessionMode == "fresh");

                var record = started.Record;
                var documents = Documents(run);
                ShownDocuments[JobKey(run, record.Id)] = documents;
                return JsonSerializer.Serialize(new WorkStartResult(record.Id, record.Act, StateName(record.State), started.Started,
                                                                    documents, started.ActiveJobIds),
                    ForgeToolJson.Default.WorkStartResult);
            });
    }

    /// <summary><c>forge.work.poll</c>: waits as long as <paramref name="waitSeconds"/> asks, held to its range.</summary>
    /// <param name="registry">The server's active jobs.</param>
    /// <param name="roots">The session roots advertised by the host.</param>
    /// <param name="workspaceRoot">The Run's workspace root.</param>
    /// <param name="runId">The Run the job belongs to.</param>
    /// <param name="jobId">The job to wait for.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    /// <param name="waitSeconds">The requested wait, or null for the 45-second default.</param>
    [McpServerTool(Name = "forge.work.poll"), Description("Waits for a background worker act to finish, for 45 seconds or `waitSeconds`, returning the moment it ends, and reports its last stdout activity and recognised event. A `running` result is not the end of the wait: call this again with the same job id.")]
    public static Task<string> PollWork(JobRegistry registry,
                                        SessionRoots roots,
                                        [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                        [Description("Run id from forge.begin.")] string runId,
                                        [Description("Job id returned by forge.work.start.")] string jobId,
                                        CancellationToken ct,
                                        [Description("Seconds to wait before answering `running`. Omit for 45. At most 3000; a value outside 1 to 3000 is clamped, and `waitClamped` in the result says so.")] int? waitSeconds = null)
    {
        var wait = PollWait.For(waitSeconds);
        return PollWork(registry, roots, workspaceRoot, runId, jobId, wait.Timeout, ct, waitSeconds, wait.Clamped);
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the job and reports where it stands.</summary>
    /// <param name="registry">The server's active jobs.</param>
    /// <param name="roots">The session roots advertised by the host.</param>
    /// <param name="workspaceRoot">The Run's workspace root.</param>
    /// <param name="runId">The Run the job belongs to.</param>
    /// <param name="jobId">The job to wait for.</param>
    /// <param name="timeout">How long to wait before answering <c>running</c>.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    /// <param name="waitSeconds">The wait the caller asked for, logged as asked; null when it asked for none.</param>
    /// <param name="waitClamped">Why the wait differs from the one asked for, or null when it does not.</param>
    internal static async Task<string> PollWork(JobRegistry registry,
                                                SessionRoots roots,
                                                string workspaceRoot,
                                                string runId,
                                                string jobId,
                                                TimeSpan timeout,
                                                CancellationToken ct,
                                                int? waitSeconds = null,
                                                string? waitClamped = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, workspaceRoot, "forge.work.poll",
            [("jobId", jobId), ("waitSeconds", waitSeconds?.ToString(CultureInfo.InvariantCulture))],
            async () =>
            {
                ValidateJobId(jobId);
                var record = await registry.WaitAsync(run.Path, jobId, timeout, ct)
                    .ConfigureAwait(false);
                RequireJob(record, jobId);

                return JsonSerializer.Serialize(PollResult(record!, run, waitClamped),
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
        return await LoggedAsync(run, workspaceRoot, "forge.work.cancel", [("jobId", jobId)],
            () =>
            {
                ValidateJobId(jobId);
                var record = registry.Cancel(run.Path, jobId);
                RequireJob(record, jobId);

                return Task.FromResult(JsonSerializer.Serialize(PollResult(record!, run),
                    ForgeToolJson.Default.WorkPollResult));
            });
    }

    [McpServerTool(Name = "forge.work.fetch"), Description("Fetches the terminal result of a background worker act: the act's own payload as the `result` object, beside its state, any `error`, and `documents`. Call it only after a forge.work.poll came back in a state other than `running` — a job still running has no result to fetch. The payload remains a historical completion snapshot; status and a new fix replay show current pending IDs. A job id from a server process that has since exited cannot be fetched; start a new act instead, and read the persisted result under `.forge/<runId>/`.")]
    public static async Task<string> FetchWork(JobRegistry registry,
                                               SessionRoots roots,
                                               [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                               [Description("Run id from forge.begin.")] string runId,
                                               [Description("Job id returned by forge.work.start.")] string jobId,
                                               CancellationToken ct)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, workspaceRoot, "forge.work.fetch", [("jobId", jobId)],
            () =>
            {
                ValidateJobId(jobId);
                var record = registry.Get(run.Path, jobId);
                RequireJob(record, jobId);
                if (record!.State == JobState.Running)
                    throw new InvalidOperationException($"job {jobId} is still running");

                ShownDocuments.TryRemove(JobKey(run, record.Id), out _);
                var result = record is { State: JobState.Completed, ResultPayload: { } payload } ? Payload(payload) : (JsonElement?)null;
                return Task.FromResult(JsonSerializer.Serialize(
                    new WorkFetchResult(record.Id, record.Act, StateName(record.State), result,
                                        record.State == JobState.Failed ? record.Error : null,
                                        Documents(run, includeScout: record is { Act: "scout", State: JobState.Completed })),
                    ForgeToolJson.Default.WorkFetchResult));
            });
    }

    /// <summary>Reports the Run and the server's current Advisor configuration.</summary>
    /// <param name="registry">The server's active jobs.</param>
    /// <param name="roots">The session roots advertised by the host.</param>
    /// <param name="workspaceRoot">The Run's workspace root.</param>
    /// <param name="runId">The Run to read.</param>
    /// <param name="ct">Caller cancellation.</param>
    [McpServerTool(Name = "forge.status"), Description("Reports where the run stands and changes nothing. `ledger` is the compact source-of-truth summary of current finding IDs by disposition and active phase, so a resumed Orchestrator need not read run files. Also returns saved `run.fullGate` timing and current `ledger.pendingFullGateFindingIds` and covering attempts; corrupted or unknown saved timing, including non-string JSON, is refused without changing the run. Log the failure with forge.log.append and start a new run; do not hand-edit run files. beforeNextRound blocks review with pending fixes; final permits review, but full verification remains required before successful completion. Also returns run progress, `run.scout` with enabled/selection, current session and last failure, filtered drift, and active-job liveness. Call it before approval, retry or a round-cap decision.")]
    public static async Task<string> Status(JobRegistry registry,
                                            SessionRoots roots,
                                            [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                            [Description("Run id from forge.begin.")] string runId,
                                            CancellationToken ct) =>
        await Status(registry, roots, workspaceRoot, runId, ct, environment: null);

    /// <summary>Reports run status with Advisor discovery from the environment of this call.</summary>
    /// <param name="registry">The server's active jobs.</param>
    /// <param name="roots">The session roots advertised by the host.</param>
    /// <param name="workspaceRoot">The Run's workspace root.</param>
    /// <param name="runId">The Run to read.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <param name="environment">The environment lookup, or null for the process environment.</param>
    internal static async Task<string> Status(JobRegistry registry, SessionRoots roots, string workspaceRoot, string runId,
                                              CancellationToken ct, Func<string, string?>? environment = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, workspaceRoot, "forge.status", [],
            async () =>
            {
                var state = run.ReadState();
                var drifted = await run.ReadBaseline(state.BaselineHead)
                                       .DriftedFilesAsync(new GitClient(workspaceRoot), ct);
                ActiveJob[] active = [.. registry.Running(run.Path)
                    .Select(job => new ActiveJob(job.Id, job.Act, StateName(job.State), ElapsedSeconds(job),
                                                 job.LastActivityAt, job.LastEvent))];

                return JsonSerializer.Serialize(new StatusResult(StatusRun.Of(state), drifted, active is [] ? null : active,
                                                                  run.ReadDecisionLedger().Summary,
                                                                  AdvisorCatalog.Default.Discover(environment ?? Environment.GetEnvironmentVariable)),
                                                ForgeToolJson.Default.StatusResult);
            });
    }

    /// <summary>Reports status without a server-owned job registry, for in-process callers.</summary>
    /// <param name="roots">The session roots advertised by the host.</param>
    /// <param name="workspaceRoot">The Run's workspace root.</param>
    /// <param name="runId">The Run to read.</param>
    /// <param name="ct">Caller cancellation.</param>
    /// <param name="environment">The environment lookup, or null for the process environment.</param>
    internal static Task<string> Status(SessionRoots roots, string workspaceRoot, string runId, CancellationToken ct,
                                        Func<string, string?>? environment = null) =>
        Status(new JobRegistry(), roots, workspaceRoot, runId, ct, environment);

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
    /// <param name="phase">The Orchestrator phase this call begins, or null to declare none.</param>
    [McpServerTool(Name = "forge.log.append"), Description("Appends one entry to the run's diagnostic log at `.forge/<runId>/forge.log`. Use it to record what you selected, retried, or decided; never edit the file directly.")]
    public static async Task<string> AppendLog(SessionRoots roots,
                                               [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                               [Description("Run id from forge.begin.")] string runId,
                                               [Description("What happened, in one line.")] string message,
                                               CancellationToken ct,
                                               [Description("Optional level: info, warn or error. Defaults to info.")] string? level = null,
                                               [Description("Optional longer detail — a command line, an error, a decision's reasoning.")] string? detail = null,
                                               [Description("Optional Orchestrator phase this call begins, exactly one of interview, planning, plan_revision, approval, build, code_review, wrap_up or done (case-sensitive). It closes the phase open until now and opens this one; done closes it and opens none.")] string? phase = null)
    {
        using var scope = OrchestratorCall.Join(workspaceRoot, runId);
        if (phase is not null) OrchestratorPhases.Validate(phase);

        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);

        // Served whether or not a phase is declared, so a choice this call decides lands in the Run's own log (R6).
        scope.Call.Serve(run);
        run.Log.Write(Level(level), "orchestrator", "note", ("message", message), ("detail", detail));
        if (phase is not null) await scope.Call.Phases.DeclareAsync(run, scope.Call, phase, ct);

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
    /// <param name="includeScout">Whether to include the Scout report when it exists.</param>
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
            includeScout && File.Exists(run.ScoutAnswersPath)
                ? new RunDocument(run.ScoutAnswersPath,
                                  "show the Scout report to the user now, and show it again after each later successful Scout call appends its answer.")
                : null);

    /// <summary>
    /// The plan in <c>PLAN.md</c> as it stands, for a tool handed none: what <c>forge.plan.write</c>
    /// and <c>forge.plan.patch</c> left there is the plan the user was shown. See docs/adr/0038.
    /// </summary>
    /// <param name="run">The run whose plan is read.</param>
    /// <param name="tool">The tool that needs it, named in the refusal.</param>
    /// <returns>The non-blank plan text.</returns>
    private static string CurrentPlan(RunDirectory run, string tool)
    {
        var plan = File.Exists(run.PlanPath) ? run.ReadPlan() : null;
        return PlanOutline.IsBlank(plan)
            ? throw new ArgumentRejectedException($"{tool} found no plan in PLAN.md: write it with forge.plan.write, or pass plan")
            : plan!;
    }

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

    private static WorkPollResult PollResult(JobRecord record, RunDirectory run, string? waitClamped = null) =>
        new(record.Id, record.Act, StateName(record.State), ElapsedSeconds(record), record.LastActivityAt,
            record.LastEvent, record.State == JobState.Failed ? record.Error : null, NextCall(record.State),
            Unshown(run, record.Id), waitClamped);

    /// <summary>
    /// The run's documents for a poll of <paramref name="jobId"/>, or null when they are the ones
    /// this job last answered with. A long act is polled again and again, and the same two paths and
    /// instructions on every poll were the larger half of what polling put in the Orchestrator's
    /// context; a file that appears or changes mid-job still reaches it on the next poll.
    /// </summary>
    /// <param name="run">The run the job belongs to.</param>
    /// <param name="jobId">The job being polled.</param>
    private static RunDocuments? Unshown(RunDirectory run, string jobId)
    {
        var documents = Documents(run);
        var key = JobKey(run, jobId);
        if (ShownDocuments.TryGetValue(key, out var shown) && shown == documents) return null;

        ShownDocuments[key] = documents;
        return documents;
    }

    private static string JobKey(RunDirectory run, string jobId) => run.Path + "|" + jobId;

    /// <summary>
    /// A finished act's payload as the JSON it is, so <c>forge.work.fetch</c> carries the object
    /// itself rather than a string the Orchestrator reads through a second layer of escaping.
    /// </summary>
    /// <param name="payload">The JSON the act returned.</param>
    private static JsonElement Payload(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

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
    /// <param name="workspaceRoot">The call's <c>workspaceRoot</c>, which with the run id names the Run's call clock.</param>
    /// <param name="tool">The MCP tool name.</param>
    /// <param name="arguments">The tool arguments safe to record.</param>
    /// <param name="act">The operation that produces the serialized tool result.</param>
    private static async Task<string> LoggedAsync(RunDirectory run,
                                                  string workspaceRoot,
                                                  string tool,
                                                  (string Name, string? Value)[] arguments,
                                                  Func<Task<string>> act)
    {
        // A call that passed the filter is in the clock already; one that did not enters it here.
        using var timing = OrchestratorCall.Join(workspaceRoot, run.RunId);
        timing.Call.Serve(run);
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

/// <summary>The new Run and the Advisors configured when it began.</summary>
/// <param name="RunId">The new Run's identifier.</param>
/// <param name="RunPath">The directory holding its documents.</param>
/// <param name="Profile">The host capability profile.</param>
/// <param name="BaselineHead">The captured baseline commit.</param>
/// <param name="Client">The client name from the handshake.</param>
/// <param name="Advisors">Configured and partly configured Advisors, without credentials.</param>
internal sealed record BeginResult(string RunId, string RunPath, string Profile, string BaselineHead, string Client,
                                   AdvisorDiscovery Advisors);

/// <summary>
/// The decision as recorded. Drift is not repeated here: the Orchestrator read it from
/// <c>forge.status</c> to show the user before asking, which is the only moment it could matter.
/// </summary>
/// <param name="Approved">Whether the user approved the plan.</param>
/// <param name="TaskCount">How many tasks the approved plan holds; 0 when it was not approved.</param>
internal sealed record ApproveResult(bool Approved, int TaskCount);

/// <summary>The current plan file and its outline after forge.plan.write; forge.plan.patch updates the same document.</summary>
/// <param name="RunId">The run whose plan was written.</param>
/// <param name="Outline">The outline read back from disk, or null for blank plan text.</param>
/// <param name="Documents">The run's documents and display instructions.</param>
internal sealed record PlanWriteResult(string RunId, PlanOutline? Outline, RunDocuments Documents);

internal sealed record PlanOutlineResult(string RunId, PlanOutline Outline, RunDocuments Documents);

/// <summary>
/// The plan <c>forge.plan.patch</c> left, and the build progress the run keeps after it. The base
/// hash, the applied edits and their sizes are the call's own arguments measured back, so they go to
/// the Flow log and <c>forge.log</c> rather than into the Orchestrator's context — docs/adr/0042.
/// </summary>
/// <param name="RunId">The run whose plan was patched.</param>
/// <param name="Outline">The outline of the patched plan, whose hash the next patch or confirmation names.</param>
/// <param name="TasksKept">How many built tasks stay built: tasks 1 to this number.</param>
/// <param name="FirstTaskOwed">The task the next build runs, or null when every task of the patched plan is built.</param>
/// <param name="Documents">The run's documents and display instructions.</param>
internal sealed record PlanPatchResult(string RunId, PlanOutline Outline, int TasksKept, int? FirstTaskOwed,
                                       RunDocuments Documents);

/// <param name="Note">
/// What the call could not do: a builder session already running was started before this text and
/// will not see it. Null when there was nothing of the kind to say.
/// </param>
internal sealed record InstructionsResult(string? CriticInstructions,
                                          string? BuilderInstructions,
                                          string? Note,
                                          RunDocuments Documents);

/// <summary>
/// What the canvas renders: <c>PLAN.md</c> as it stands, or the plan the call was handed, and the
/// hash that lets <c>forge.plan.confirm</c> approve exactly that text — see docs/adr/0038.
/// </summary>
/// <param name="RunId">The run whose plan is shown.</param>
/// <param name="Plan">The plan text shown.</param>
/// <param name="PlanHash">The hash of <paramref name="Plan"/>, as <c>forge.plan.outline</c> reports it.</param>
/// <param name="DriftedFiles">The working-tree files changed since the baseline.</param>
/// <param name="ReviewRounds">The plan-review rounds run so far.</param>
/// <param name="Approved">Whether the run is approved.</param>
internal sealed record PlanViewResult(string RunId,
                                      string Plan,
                                      string PlanHash,
                                      IReadOnlyList<string> DriftedFiles,
                                      int ReviewRounds,
                                      bool Approved);

/// <summary>
/// Drift travels with the status rather than only with the decision, because the orchestrator has
/// to show it to the user <em>before</em> asking, and the decision call is where it would arrive
/// too late to matter.
/// </summary>
/// <param name="Run">The part of the Run's saved state the Orchestrator acts on.</param>
/// <param name="DriftedFiles">Paths changed since the baseline.</param>
/// <param name="ActiveJobs">The running jobs, oldest first, or null when none runs.</param>
/// <param name="Ledger">The current finding summary.</param>
/// <param name="Advisors">Current Advisor discovery, without credentials.</param>
internal sealed record StatusResult(StatusRun Run, IReadOnlyList<string> DriftedFiles, IReadOnlyList<ActiveJob>? ActiveJobs,
                                    LedgerSummary Ledger, AdvisorDiscovery Advisors);

/// <summary>
/// What <c>forge.status</c> shows of <see cref="RunState"/>: everything the Orchestrator decides
/// from, and after a compaction resumes from, and none of the state only the server reads — the
/// files each task changed, the builder session's resume token, vendor and scope, the Worker tool
/// patterns, the builder roots, the start time and the baseline commit. The gate environment goes
/// out by name: its values may be connection strings, and the result is logged in full to
/// <c>forge.log</c>. See docs/adr/0042.
/// </summary>
/// <param name="RunId">The run's id.</param>
/// <param name="WorkspaceRoot">The git window the run reviews.</param>
/// <param name="Profile">The capability profile of the host that began the run.</param>
/// <param name="ReviewRounds">Plan-review rounds run so far.</param>
/// <param name="ReviewRoundCap">How many plan-review rounds run without the user granting another.</param>
/// <param name="Approved">Whether the plan as it stands was approved.</param>
/// <param name="TasksCompleted">Plan tasks counted done.</param>
/// <param name="CodeReviewRounds">Code-review rounds run so far.</param>
/// <param name="CodeReviewRoundCap">How many code-review rounds run without the user granting another.</param>
/// <param name="GrantedReviewRounds">Plan-review rounds the user granted past the cap.</param>
/// <param name="GrantedCodeReviewRounds">Code-review rounds the user granted past the cap.</param>
/// <param name="GateEnvironment">The names of the variables gate commands run with, sorted; null when none were given.</param>
/// <param name="PendingGateFailure">What the last failed gate said, owed to the next builder turn; null while nothing is owed.</param>
/// <param name="CriticInstructions">The user's text for the run's critics.</param>
/// <param name="BuilderInstructions">The user's text for the run's builder.</param>
/// <param name="Scout">The Scout choice, its current session and last failure, or null before the choice.</param>
/// <param name="ScoutAnswers">How many Scout answers have consumed a number.</param>
/// <param name="FullGate">When targeted fixes are verified: beforeNextRound or final.</param>
/// <param name="AutoContinuedReviewRounds">Plan-review rounds that ran past the cap without asking.</param>
/// <param name="AutoContinuedCodeReviewRounds">Code-review rounds that ran past the cap without asking.</param>
/// <param name="ReviewTallies">What each plan-review round left for the cap, oldest first.</param>
/// <param name="CodeReviewTallies">What each code-review round left for the cap, oldest first.</param>
internal sealed record StatusRun(string RunId,
                                 string WorkspaceRoot,
                                 string Profile,
                                 int ReviewRounds,
                                 int ReviewRoundCap,
                                 bool Approved,
                                 int TasksCompleted,
                                 int CodeReviewRounds,
                                 int CodeReviewRoundCap,
                                 int GrantedReviewRounds,
                                 int GrantedCodeReviewRounds,
                                 IReadOnlyList<string>? GateEnvironment,
                                 string? PendingGateFailure,
                                 string? CriticInstructions,
                                 string? BuilderInstructions,
                                 ScoutState? Scout,
                                 int ScoutAnswers,
                                 string FullGate,
                                 int AutoContinuedReviewRounds,
                                 int AutoContinuedCodeReviewRounds,
                                 IReadOnlyList<ReviewTally>? ReviewTallies,
                                 IReadOnlyList<ReviewTally>? CodeReviewTallies)
{
    /// <summary>The status view of a saved state.</summary>
    /// <param name="state">The Run's saved state.</param>
    public static StatusRun Of(RunState state) =>
        new(state.RunId, state.WorkspaceRoot, state.Profile, state.ReviewRounds, state.ReviewRoundCap, state.Approved,
            state.TasksCompleted, state.CodeReviewRounds, state.CodeReviewRoundCap, state.GrantedReviewRounds,
            state.GrantedCodeReviewRounds, state.GateEnvironment?.Keys.Order(StringComparer.Ordinal).ToArray(),
            state.PendingGateFailure, state.CriticInstructions, state.BuilderInstructions, state.Scout, state.ScoutAnswers,
            state.FullGate, state.AutoContinuedReviewRounds, state.AutoContinuedCodeReviewRounds, state.ReviewTallies,
            state.CodeReviewTallies);
}

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
/// start and every poll rather than only with the fetch, because a background act runs for minutes
/// and the plan is on disk before the first of them — see docs/adr/0014.
/// </summary>
/// <param name="JobId">The job's id, or the oldest running job's when this one did not start.</param>
/// <param name="Act">The act the job runs.</param>
/// <param name="State">running, succeeded or failed.</param>
/// <param name="Started">False when the Run's running jobs kept this one from starting.</param>
/// <param name="Documents">The run's documents as they stand at the start.</param>
/// <param name="ActiveJobIds">The jobs that kept this one from starting, oldest first; null when it started.</param>
internal sealed record WorkStartResult(string JobId, string Act, string State, bool Started, RunDocuments Documents,
                                       IReadOnlyList<string>? ActiveJobIds = null);

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


internal sealed record ReviewFixResult(BuildResult Fix, RunDocuments Documents);

/// <summary>Where a background job stands, as <c>forge.work.poll</c> and <c>forge.work.cancel</c> report it.</summary>
/// <param name="JobId">The job's id.</param>
/// <param name="Act">The act the job runs.</param>
/// <param name="State">running, succeeded or failed.</param>
/// <param name="ElapsedSeconds">How long the job has run, or ran.</param>
/// <param name="LastActivityAt">When the worker last wrote to stdout, if it has.</param>
/// <param name="LastEvent">The last event recognised in the worker's output, if any.</param>
/// <param name="Error">Why the job failed; null unless it did.</param>
/// <param name="Next">The call this result asks for: another poll while the job runs, a fetch once it stops.</param>
/// <param name="Documents">The run's documents; absent when they are the ones this job last answered with.</param>
/// <param name="WaitClamped">Why this poll waited other than the <c>waitSeconds</c> it was given; absent when it waited as asked.</param>
internal sealed record WorkPollResult(string JobId,
                                      string Act,
                                      string State,
                                      double ElapsedSeconds,
                                      DateTimeOffset? LastActivityAt,
                                      string? LastEvent,
                                      string? Error,
                                      string Next,
                                      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                      RunDocuments? Documents,
                                      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                      string? WaitClamped = null);

/// <summary>
/// How long one <c>forge.work.poll</c> waits. A host gives a call an hour, and a poll that waits
/// most of it costs the Orchestrator one turn where 45-second polls cost dozens — issue #169.
/// </summary>
/// <param name="Timeout">How long the poll waits before answering <c>running</c>.</param>
/// <param name="Clamped">Why <see cref="Timeout"/> differs from the requested wait, or null when it does not.</param>
internal sealed record PollWait(TimeSpan Timeout, string? Clamped)
{
    private const int DEFAULT_SECONDS = 45;

    // Claude Code's manifest "timeout" and Codex's tool_timeout_sec both end a call at the hour;
    // ten minutes short of it leaves the result room to land before that clock does.
    private const int MAX_SECONDS = 3000;

    /// <summary>The wait for a poll that asked for <paramref name="waitSeconds"/>, held to 1 to 3000 seconds.</summary>
    /// <param name="waitSeconds">The requested wait, or null for the 45-second default.</param>
    public static PollWait For(int? waitSeconds)
    {
        if (waitSeconds is not { } requested) return new PollWait(TimeSpan.FromSeconds(DEFAULT_SECONDS), null);

        var granted = Math.Clamp(requested, 1, MAX_SECONDS);
        var clamped = granted == requested
            ? null
            : $"waitSeconds {requested} is outside 1 to {MAX_SECONDS}; this poll waited up to {granted} seconds";
        return new PollWait(TimeSpan.FromSeconds(granted), clamped);
    }
}

internal sealed record WorkFetchResult(string JobId, string Act, string State, JsonElement? Result, string? Error, RunDocuments Documents);

internal sealed record ModelsResult(IReadOnlyList<VendorCatalogResult> Vendors);

/// <summary>One vendor's entry in the <c>forge.models</c> answer.</summary>
/// <param name="Vendor">The vendor id.</param>
/// <param name="Source">"live" when the vendor reported the list itself, "resolved" when it resolved aliases this repo remembers.</param>
/// <param name="Available">Whether the vendor can do the work at all; one that cannot is not offered.</param>
/// <param name="Detail">The catalogue's size and any warnings, or why the vendor is unavailable.</param>
/// <param name="Models">The models the vendor advertises, newest first.</param>
internal sealed record VendorCatalogResult(string Vendor,
                                           string Source,
                                           bool Available,
                                           string Detail,
                                           IReadOnlyList<CatalogModel> Models);

/// <summary>
/// One model as the interview offers it. The vendor's own description of the model is left out: the
/// model question is asked from the id, the display name, the efforts and the Fast fields, and
/// nothing reads the description — docs/adr/0042.
/// </summary>
/// <param name="Id">The model id the vendor CLI is given.</param>
/// <param name="DisplayName">The vendor's name for the model, where it gives one.</param>
/// <param name="Efforts">The efforts the model is offered at.</param>
/// <param name="DefaultEffort">The effort the vendor uses when none is named, where it says.</param>
/// <param name="IsDefault">Whether this is the vendor's default model.</param>
/// <param name="FastEfforts">The efforts this model is offered at with a Fast tier; empty when it has none.</param>
/// <param name="FastHint">The vendor's own word on what Fast costs, where it gives one.</param>
/// <param name="FastUnavailable">Why the account will not serve a Fast tier the model offers.</param>
internal sealed record CatalogModel(string Id,
                                    string? DisplayName,
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
[JsonSerializable(typeof(OrchestratorRaise))]
[JsonSerializable(typeof(LedgerDispositionDecision))]
[JsonSerializable(typeof(LedgerReopeningDecision))]
[JsonSerializable(typeof(LedgerClosureDecision))]
[JsonSerializable(typeof(EvidenceImplication))]
[JsonSerializable(typeof(EvidenceImplication[]))]
[JsonSerializable(typeof(PlanEdit))]
[JsonSerializable(typeof(IReadOnlyList<PlanEdit?>))]
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
[JsonSerializable(typeof(EvidenceMapResult))]
[JsonSerializable(typeof(AdvisorDiscovery))]
[JsonSerializable(typeof(ConfiguredAdvisor))]
[JsonSerializable(typeof(PartlyConfiguredAdvisor))]
[JsonSerializable(typeof(AdvisorProblem))]
[JsonSerializable(typeof(BeginResult))]
[JsonSerializable(typeof(ApproveResult))]
[JsonSerializable(typeof(PlanWriteResult))]
[JsonSerializable(typeof(PlanOutlineResult))]
[JsonSerializable(typeof(PlanPatchResult))]
[JsonSerializable(typeof(PlanOutline))]
[JsonSerializable(typeof(PlanTarget))]
[JsonSerializable(typeof(InstructionsResult))]
[JsonSerializable(typeof(PlanViewResult))]
[JsonSerializable(typeof(StatusResult))]
[JsonSerializable(typeof(LedgerSummary))]
[JsonSerializable(typeof(PendingFullGateAttempt))]
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
[JsonSerializable(typeof(ReviewFixResult))]
[JsonSerializable(typeof(WorkStartResult))]
[JsonSerializable(typeof(WorkPollResult))]
[JsonSerializable(typeof(WorkFetchResult))]
[JsonSerializable(typeof(ModelsResult))]
internal sealed partial class ForgeToolJson : JsonSerializerContext;
