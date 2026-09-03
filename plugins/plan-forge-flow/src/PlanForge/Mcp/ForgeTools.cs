using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;
using PlanForge.Acts;
using PlanForge.Diagnostics;
using PlanForge.Jobs;
using PlanForge.Orchestration;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Mcp;

[McpServerToolType]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
internal sealed class ForgeTools
{
    private const int DefaultReviewRoundCap = 5;
    private const int DefaultCodeReviewCap = 3;
    private const int WorkPollTimeoutSeconds = 45;
    private const string Source = "server";

    [McpServerTool(Name = "forge.begin"), Description("Starts a run, takes a working-tree baseline excluding `CONTEXT.md` and `docs/adr/**`, and returns the run id, the capability profile, and the connecting client.")]
    public static async Task<string> Begin(McpServer server,
                                           CatalogCache catalogs,
                                           SessionRoots roots,
                                           [Description("Absolute path to the workspace root.")] string workspaceRoot,
                                           CancellationToken ct)
    {
        var profile = CapabilityProfileDetector.Detect(server.ClientCapabilities);
        var runId = NewRunId();
        var run = await RunDirectory.CreateAsync(roots, workspaceRoot, runId, ct);

        // The run folder has to exist before anything can be logged, so this is the one tool whose
        // record starts after its first side effect rather than before it. `sessionRoot` is null
        // for a host that declares no roots, which is the one thing the run path alone cannot say.
        return await LoggedAsync(run, "forge.begin",
            [("workspaceRoot", workspaceRoot), ("sessionRoot", await roots.DirectoryAsync(ct)),
             ("client", ClientName(server)), ("profile", profile.ToString())],
            async () =>
            {
                // Fire-and-forget: by the time the interview reaches the vendor question,
                // forge.models finds the catalogues already fetched.
                catalogs.BeginProbing(workspaceRoot);

                var baseline = await Baseline.CaptureAsync(new GitClient(workspaceRoot), ct);
                run.WriteBaseline(baseline);
                run.WriteState(new RunState(runId, workspaceRoot, profile.ToString(), DateTimeOffset.Now,
                    ReviewRounds: 0, ReviewRoundCap: DefaultReviewRoundCap, BaselineHead: baseline.Head,
                    CodeReviewRoundCap: DefaultCodeReviewCap));

                return JsonSerializer.Serialize(
                    new BeginResult(runId, run.Path, profile.ToString(), baseline.Head, ClientName(server)),
                    ForgeToolJson.Default.BeginResult);
            });
    }

    /// <summary>
    /// The clientInfo name from the MCP handshake, verbatim. The skill branches its model-selection
    /// flow on the host, and the orchestrator's own idea of where it runs is a guess; this is not.
    /// </summary>
    private static string ClientName(McpServer server) =>
        server.ClientInfo?.Name is { Length: > 0 } name ? name : "unknown";

    /// <summary>
    /// The probes were started by <c>forge.begin</c>, so by interview time this is a cache read;
    /// a cold call probes on the spot and waits. A probe failure is a value here, not an error —
    /// the interview's reaction to a dead vendor is to drop it, not to stop.
    /// </summary>
    [McpServerTool(Name = "forge.models"), Description("Returns each vendor's model catalogue with effort levels per model, newest first — source `live` where the vendor publishes a list (codex, cursor), `resolved` for claude, whose remembered aliases the CLI turned into the model ids they stand for (displayName). A vendor with available:false is not usable; tell the user why and do not offer it.")]
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
                    model.Description, model.Efforts, model.DefaultEffort, model.IsDefault))
            ]);

    /// <summary>
    /// One round only. The critic judges the draft; revising it and calling again is the
    /// orchestrator's job, because the revision needs the interview context.
    /// </summary>
    [McpServerTool(Name = "forge.plan.review"), Description("Runs one round of plan review and returns the critique, plus the run's flow log and the plan it just wrote under `documents`. It writes the draft you pass to `PLAN.md` before the critic starts, so show that file to the user. A round run against an already-approved plan takes the approval back and resets the build progress; say so out loud when it happens.")]
    public static async Task<string> ReviewPlan(
        SessionRoots roots,
        [Description("Absolute path to the workspace root.")] string workspaceRoot,
        [Description("Run id from forge.begin.")] string runId,
        [Description("The current plan draft, as markdown.")] string planDraft,
        [Description("Model for the critic.")] string model,
        CancellationToken ct,
        [Description("Optional effort level.")] string? effort = null,
        [Description("Vendor: claude, codex or cursor. Defaults to claude.")] string? vendor = null,
        [Description("What you changed in the plan in answer to the previous round's findings, as markdown. Required from the second round on, and recorded in the flow log so the user sees your turn between the critic's.")] string? revision = null,
        [Description("Optional markdown list of findings you decided not to act on, each with its reason. Recorded in the flow log and in the review log, so the next round's critic treats them as settled.")] string? deferred = null,
        [Description("At the cap, this raises this run's review-round cap by exactly one and runs the round; below the cap it does nothing. Spent by this call, so a further round past the new cap needs a fresh answer. Never pass true without having shown the user where the run stands and asked.")] bool userGrantedRound = false)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.plan.review",
            [("vendor", vendor), ("model", model), ("effort", effort), ("planDraft", planDraft),
             ("revision", revision), ("deferred", deferred),
             ("userGrantedRound", userGrantedRound ? "true" : "false")],
            async () =>
            {
                var act = new PlanReview(VendorFactory.Create(vendor, workspaceRoot), new PromptLibrary());
                var critique = await act.ReviewAsync(run, planDraft, new Selection(model, effort),
                                                     revision, deferred, userGrantedRound, ct);

                return JsonSerializer.Serialize(new CritiqueResult(critique, Documents(run)),
                                                ForgeToolJson.Default.CritiqueResult);
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
    [McpServerTool(Name = "forge.plan.show"), McpAppUi(ResourceUri = PlanCanvas.ResourceUri), Description("Renders the plan as a document in the host's own UI, with the working-tree drift beside it. Call it only when forge.begin reported profile `Canvas`, immediately before you ask the user to approve — and still ask, because this records nothing. On a `Text` profile nothing renders, so show the plan in the chat instead.")]
    public static async Task<string> ShowPlan(
        SessionRoots roots,
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
    [McpServerTool(Name = "forge.plan.confirm"), Description("Records the user's decision on the plan, and records the approved tasks when it is yes.")]
    public static async Task<string> ConfirmPlan(
        SessionRoots roots,
        [Description("Absolute path to the workspace root.")] string workspaceRoot,
        [Description("Run id from forge.begin.")] string runId,
        [Description("The plan to approve, as markdown.")] string plan,
        [Description("What the user answered. Show them the plan and the filtered drift excluding `CONTEXT.md` and `docs/adr/**`, ask, and pass what they say; never decide this yourself.")] bool approved,
        CancellationToken ct)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.plan.confirm",
            [("approved", approved.ToString()), ("plan", plan)],
            async () =>
            {
                var state = run.ReadState();
                var tasks = PlanTasks.Parse(plan);

                var drifted = await run.ReadBaseline(state.BaselineHead)
                                       .DriftedFilesAsync(new GitClient(workspaceRoot), ct);

                if (!approved) return Serialized(new ApproveResult(false, 0, drifted));

                run.WritePlan(plan);
                run.WriteState(state with { Approved = true });

                return Serialized(new ApproveResult(true, tasks.Count, drifted));
            });
    }

    [McpServerTool(Name = "forge.build.next"), Description("Builds the next unfinished task of the approved plan.")]
    public static async Task<string> BuildNext(
        SessionRoots roots,
        [Description("Absolute path to the workspace root.")] string workspaceRoot,
        [Description("Run id from forge.begin.")] string runId,
        [Description("Model for the builder.")] string model,
        CancellationToken ct,
        [Description("Optional effort level.")] string? effort = null,
        [Description("Vendor: claude, codex or cursor. Defaults to claude.")] string? vendor = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.build.next",
            [("vendor", vendor), ("model", model), ("effort", effort)],
            async () =>
            {
                var act = new Build(VendorFactory.Create(vendor, workspaceRoot), new PromptLibrary());
                var outcome = await act.NextAsync(run, new Selection(model, effort), ct);

                return JsonSerializer.Serialize(new BuildNextResult(outcome, Documents(run)),
                                                ForgeToolJson.Default.BuildNextResult);
            });
    }

    /// <summary>
    /// One round only, like plan review. The loop used to live inside this call on the premise that
    /// nothing in it needed the interview context; a critic asking for work the approved plan
    /// excluded disproved that, so the orchestrator now takes a turn between critic and builder —
    /// see docs/adr/0005.
    /// </summary>
    [McpServerTool(Name = "forge.review.code"), Description("Runs one round of code review: the critic judges the working diff, excluding `CONTEXT.md` and `docs/adr/**`, against the approved plan and returns the critique. Filter the findings yourself, then pass the kept ones to forge.review.fix.")]
    public static async Task<string> ReviewCode(
        SessionRoots roots,
        [Description("Absolute path to the workspace root.")] string workspaceRoot,
        [Description("Run id from forge.begin.")] string runId,
        [Description("Model for the critic.")] string model,
        CancellationToken ct,
        [Description("Optional effort level.")] string? effort = null,
        [Description("Vendor: claude, codex or cursor. Defaults to claude.")] string? vendor = null,
        [Description("At the cap, this raises this run's code-review-round cap by exactly one and runs the round; below the cap it does nothing. Spent by this call, so a further round past the new cap needs a fresh answer. Never pass true without having shown the user where the run stands and asked.")] bool userGrantedRound = false)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.review.code",
            [("vendor", vendor), ("model", model), ("effort", effort),
             ("userGrantedRound", userGrantedRound ? "true" : "false")],
            async () =>
            {
                var act = new CodeReview(VendorFactory.Create(vendor, workspaceRoot), new PromptLibrary(),
                    new GitClient(workspaceRoot));
                var critique = await act.ReviewAsync(run, new Selection(model, effort), userGrantedRound, ct);

                return JsonSerializer.Serialize(new CritiqueResult(critique, Documents(run)),
                                                ForgeToolJson.Default.CritiqueResult);
            });
    }

    [McpServerTool(Name = "forge.review.fix"), Description("Hands the findings you kept after filtering the critique to the builder to fix, and records the deferred ones in the review log so the next round's critic treats them as settled.")]
    public static async Task<string> ReviewFix(
        SessionRoots roots,
        [Description("Absolute path to the workspace root.")] string workspaceRoot,
        [Description("Run id from forge.begin.")] string runId,
        [Description("The findings to fix, as markdown. Compose them from the critique; keep every in-scope correctness finding, and never add work the critic did not ask for.")] string findings,
        [Description("Model for the builder.")] string model,
        CancellationToken ct,
        [Description("Optional markdown list of findings deferred rather than fixed, each with its reason — typically that the approved plan excludes it. Recorded in the review log; report them to the user when the review settles.")] string? deferred = null,
        [Description("Optional effort level.")] string? effort = null,
        [Description("Vendor: claude, codex or cursor. Defaults to claude.")] string? vendor = null)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.review.fix",
            [("vendor", vendor), ("model", model), ("effort", effort), ("findings", findings), ("deferred", deferred)],
            async () =>
            {
                var act = new ReviewFix(VendorFactory.Create(vendor, workspaceRoot), new PromptLibrary());
                var result = await act.FixAsync(run, new Selection(model, effort), findings, deferred, ct);

                return JsonSerializer.Serialize(new ReviewFixResult(result, Documents(run)),
                                                ForgeToolJson.Default.ReviewFixResult);
            });
    }

    [McpServerTool(Name = "forge.work.start"), Description("Starts one worker act in the background and returns its job id.")]
    public static Task<string> StartWork(
        JobRegistry registry,
        SessionRoots roots,
        [Description("Absolute path to the workspace root.")] string workspaceRoot,
        [Description("Run id from forge.begin.")] string runId,
        [Description("Worker act: plan.review, build.next, review.code or review.fix.")] string act,
        [Description("Model for the worker.")] string model,
        CancellationToken ct,
        [Description("Optional effort level.")] string? effort = null,
        [Description("Vendor: claude, codex or cursor. Defaults to claude.")] string? vendor = null,
        [Description("Plan draft, required only by plan.review.")] string? planDraft = null,
        [Description("Findings for review.fix; present but may be blank.")] string? findings = null,
        [Description("Deferred findings, with reasons: for review.fix, and optionally for plan.review.")] string? deferred = null,
        [Description("For plan.review only: what you changed in the plan in answer to the previous round's findings. Required from the second round on.")] string? revision = null,
        [Description("For plan.review and review.code only: at the cap, raises this run's round cap by exactly one and runs the round; below the cap it does nothing, and it is spent by this call. Never pass true without having shown the user where the run stands and asked.")] bool userGrantedRound = false)
    {
        // VendorFactory.Create is deliberately the one line not covered by the factory-seam tests.
        return StartWork(registry, roots, workspaceRoot, runId, act, model, effort, vendor, planDraft, findings,
                         deferred, revision, userGrantedRound, ct, () => VendorFactory.Create(vendor, workspaceRoot));
    }

    internal static async Task<string> StartWork(
        JobRegistry registry,
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
        Func<IVendor> vendorFactory)
    {
        var run = await RunDirectory.OpenAsync(roots, workspaceRoot, runId, ct);
        return await LoggedAsync(run, "forge.work.start",
            [("act", act), ("vendor", vendor), ("model", model), ("effort", effort),
             ("planDraft", planDraft), ("findings", findings), ("deferred", deferred),
             ("revision", revision), ("userGrantedRound", userGrantedRound ? "true" : "false")],
            async () =>
            {
                WorkAct.ValidateArguments(act, planDraft, new Selection(model, effort), findings, deferred, revision,
                                          userGrantedRound);

                // The act would refuse this itself a moment later, inside the job. Refusing here
                // instead keeps a missing revision an argument error, answered by this call rather
                // than by a poll that reports a failure with nothing running behind it.
                if (act == "plan.review") PlanReview.RequireRevision(run.ReadState().ReviewRounds, revision);

                var workAct = new WorkAct(vendorFactory(), new PromptLibrary());
                var selection = new Selection(model, effort);
                var started = registry.Start(run.Path, act,
                    jobCt => workAct.RunAsync(act, run, planDraft, selection, findings, deferred, revision,
                                              userGrantedRound, jobCt));

                var record = started.Record;
                return JsonSerializer.Serialize(
                    new WorkStartResult(record.Id, record.Act, StateName(record.State), started.Started),
                    ForgeToolJson.Default.WorkStartResult);
            });
    }

    [McpServerTool(Name = "forge.work.poll"), Description("Waits for a background worker act to finish, for up to 45 seconds. A `running` result is not the end of the wait: call this again with the same job id.")]
    public static Task<string> PollWork(
        JobRegistry registry,
        SessionRoots roots,
        [Description("Absolute path to the workspace root.")] string workspaceRoot,
        [Description("Run id from forge.begin.")] string runId,
        [Description("Job id returned by forge.work.start.")] string jobId,
        CancellationToken ct) =>
        PollWork(registry, roots, workspaceRoot, runId, jobId, TimeSpan.FromSeconds(WorkPollTimeoutSeconds), ct);

    internal static async Task<string> PollWork(
        JobRegistry registry,
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

                return JsonSerializer.Serialize(
                    new WorkPollResult(record!.Id, record.Act, StateName(record.State), ElapsedSeconds(record),
                                       record.State == JobState.Failed ? record.Error : null, NextCall(record.State)),
                    ForgeToolJson.Default.WorkPollResult);
            });
    }

    [McpServerTool(Name = "forge.work.fetch"), Description("Fetches the terminal result of a background worker act.")]
    public static async Task<string> FetchWork(
        JobRegistry registry,
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

                var result = record.State == JobState.Completed ? record.ResultPayload : null;
                return Task.FromResult(JsonSerializer.Serialize(
                    new WorkFetchResult(record.Id, record.Act, StateName(record.State), result,
                                        record.State == JobState.Failed ? record.Error : null, Documents(run)),
                    ForgeToolJson.Default.WorkFetchResult));
            });
    }

    [McpServerTool(Name = "forge.status"), Description("Reports where the run stands, with any working-tree drift since the baseline, excluding `CONTEXT.md` and `docs/adr/**`.")]
    public static async Task<string> Status(
        JobRegistry registry,
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
                    ? new ActiveJob(job.Id, job.Act, StateName(job.State), ElapsedSeconds(job))
                    : null;

                return JsonSerializer.Serialize(new StatusResult(state, drifted, active), ForgeToolJson.Default.StatusResult);
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
    [McpServerTool(Name = "forge.log.append"), Description("Appends one entry to the run's diagnostic log at `.forge/<runId>/forge.log`. Use it to record what you selected, retried, or decided; never edit the file directly.")]
    public static async Task<string> AppendLog(
        SessionRoots roots,
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
    /// rhythms: the timeline grows with every act, while the plan only moves when a review round
    /// rewrites it. One shared instruction would have to blur that into "show these to the user".
    /// The review log and the diagnostic log are deliberately absent — the first is critic input,
    /// the second is for the orchestrator, and neither is written to be read by a person here.
    /// </remarks>
    private static RunDocuments Documents(RunDirectory run) =>
        new(File.Exists(run.FlowLogPath)
                ? new RunDocument(run.FlowLogPath,
                                  "show this file to the user now — it is the run's user-facing timeline — "
                                  + "and show it again after every later worker act.")
                : null,
            File.Exists(run.PlanPath)
                ? new RunDocument(run.PlanPath,
                                  "the plan as it now stands, rewritten by every review round: show this "
                                  + "file to the user now and again after each later round, so they can "
                                  + "watch it change. Link it; do not paste the draft into the chat.")
                : null);

    private static string StateName(JobState state) =>
        state switch
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
    private static string NextCall(JobState state) =>
        state == JobState.Running
            ? "the job is still running: call forge.work.poll again now with this job id. Do not end your turn, and do not ask the user to continue."
            : "call forge.work.fetch with this job id.";

    private static double ElapsedSeconds(JobRecord record) =>
        Math.Max(0, ((record.CompletedAt ?? DateTimeOffset.UtcNow) - record.StartedAt).TotalSeconds);

    private static void ValidateJobId(string jobId)
    {
        if (jobId.Length != 16 || jobId.Any(character =>
                !((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))))
            throw new ArgumentException("jobId must be 16 lowercase hexadecimal characters", nameof(jobId));
    }

    private static void RequireJob(JobRecord? record, string jobId)
    {
        if (record is null || record.Id != jobId)
            throw new InvalidOperationException($"unknown jobId '{jobId}'");
    }

    private static string Level(string? level) =>
        level?.Trim().ToLowerInvariant() switch
        {
            "warn" or "warning" => "warn",
            "error" => "error",
            _ => "info"
        };

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
    private static async Task<string> LoggedAsync(RunDirectory run,
                                                  string tool,
                                                  (string Name, string? Value)[] arguments,
                                                  Func<Task<string>> act)
    {
        var log = run.Log;
        using var scope = RunLog.Serve(log);

        log.Write("info", Source, "tool.call", [("tool", tool), .. arguments]);
        try
        {
            var result = await act().ConfigureAwait(false);
            log.Write("info", Source, "tool.result", ("tool", tool), ("result", result));
            return result;
        }
        catch (OperationCanceledException)
        {
            // The host giving up is the failure mode with no other trace: it takes the call away
            // before any result exists, which is exactly how a timeout looks from in here.
            log.Write("warn", Source, "tool.cancelled", ("tool", tool));
            throw;
        }
        catch (Exception error)
        {
            log.Write("error", Source, "tool.failed",
                ("tool", tool), ("error", error.Message), ("stack", error.ToString()));
            throw;
        }
    }

    private static string Serialized(ApproveResult result) =>
        JsonSerializer.Serialize(result, ForgeToolJson.Default.ApproveResult);

    // Sortable and collision-free enough for a per-workspace run folder.
    private static string NewRunId() =>
        $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..6]}";
}

internal sealed record BeginResult(string RunId, string RunPath, string Profile, string BaselineHead, string Client);

internal sealed record ApproveResult(bool Approved, int TaskCount, IReadOnlyList<string> DriftedFiles);

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
internal sealed record StatusResult(RunState Run, IReadOnlyList<string> DriftedFiles, ActiveJob? ActiveJob);

internal sealed record ActiveJob(string JobId, string Act, string State, double ElapsedSeconds);

internal sealed record WorkStartResult(string JobId, string Act, string State, bool Started);

/// <param name="Next">What to do with the file: the instruction travels with the path so neither depends on the skill still being in view.</param>
internal sealed record RunDocument(string Path, string Next);

/// <summary>
/// The files of a run that exist to be shown to a person. Both entries are optional and start out
/// absent: the timeline appears with the first act that records one, the plan with the first review
/// round that writes one.
/// </summary>
internal sealed record RunDocuments(RunDocument? FlowLog, RunDocument? Plan);

/// <summary>Both review tools answer with this: one critique, plus where the user can watch the run.</summary>
internal sealed record CritiqueResult(Critique Critique, RunDocuments Documents);

internal sealed record BuildNextResult(BuildOutcome Build, RunDocuments Documents);

internal sealed record ReviewFixResult(BuildResult Fix, RunDocuments Documents);

/// <param name="Next">The call this result asks for: another poll while the job runs, a fetch once it stops.</param>
internal sealed record WorkPollResult(string JobId, string Act, string State, double ElapsedSeconds, string? Error,
                                      string Next);

internal sealed record WorkFetchResult(string JobId, string Act, string State, string? Result, string? Error,
                                       RunDocuments Documents);

internal sealed record ModelsResult(IReadOnlyList<VendorCatalogResult> Vendors);

/// <param name="Source">"live" when the vendor reported the list itself, "resolved" when it resolved aliases this repo remembers.</param>
internal sealed record VendorCatalogResult(string Vendor,
                                           string Source,
                                           bool Available,
                                           string Detail,
                                           IReadOnlyList<CatalogModel> Models);

internal sealed record CatalogModel(string Id,
                                    string? DisplayName,
                                    string? Description,
                                    IReadOnlyList<string> Efforts,
                                    string? DefaultEffort,
                                    bool IsDefault);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BeginResult))]
[JsonSerializable(typeof(ApproveResult))]
[JsonSerializable(typeof(PlanViewResult))]
[JsonSerializable(typeof(StatusResult))]
[JsonSerializable(typeof(ActiveJob))]
[JsonSerializable(typeof(BuildOutcome))]
[JsonSerializable(typeof(RunDocument))]
[JsonSerializable(typeof(RunDocuments))]
[JsonSerializable(typeof(CritiqueResult))]
[JsonSerializable(typeof(BuildNextResult))]
[JsonSerializable(typeof(ReviewFixResult))]
[JsonSerializable(typeof(WorkStartResult))]
[JsonSerializable(typeof(WorkPollResult))]
[JsonSerializable(typeof(WorkFetchResult))]
[JsonSerializable(typeof(ModelsResult))]
internal sealed partial class ForgeToolJson : JsonSerializerContext;
