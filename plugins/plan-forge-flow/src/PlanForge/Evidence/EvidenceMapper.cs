using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanForge.Advisors;
using PlanForge.Run;

namespace PlanForge.Evidence;

/// <summary>A candidate unit and the verdict obtained from its pair.</summary>
/// <param name="Id">The local unit id.</param>
/// <param name="Label">The recorded unit label.</param>
/// <param name="Verdict">The settled pair verdict.</param>
internal sealed record EvidenceUnitVerdict(string Id, string Label, string Verdict);

/// <summary>An item requiring the Orchestrator's attention.</summary>
/// <param name="Id">The item identity.</param>
/// <param name="Kind">Scout or implication.</param>
/// <param name="Category">The Scout category, if any.</param>
/// <param name="Text">The exact recorded item text.</param>
/// <param name="Source">The exact recorded source.</param>
/// <param name="Status">The settled item status.</param>
/// <param name="SourceChanged">Whether the local locator check changed.</param>
/// <param name="LocatorReason">The unresolved locator's reason.</param>
/// <param name="UncheckedReasons">Distinct reasons for unchecked questions.</param>
/// <param name="Change">New or carried against the previous complete map.</param>
/// <param name="Units">The candidate units and their verdicts.</param>
internal sealed record EvidenceFlag(string Id, string Kind, string? Category, string Text, string? Source,
                                    string Status, bool SourceChanged, string? LocatorReason,
                                    IReadOnlyList<string> UncheckedReasons, string Change,
                                    IReadOnlyList<EvidenceUnitVerdict> Units);

/// <summary>The map result before the tool adds Run documents.</summary>
/// <param name="Advisor">The selected Advisor.</param>
/// <param name="RequestedModel">The requested model.</param>
/// <param name="ServedModel">The first fresh model, or the stored model when no fresh answer arrived.</param>
/// <param name="Complete">Whether every judgment has a reusable outcome.</param>
/// <param name="Required">Distinct required keys.</param>
/// <param name="CacheHits">Keys found in the store at the start.</param>
/// <param name="CheckedThisCall">Keys given reusable outcomes during this call.</param>
/// <param name="Remaining">Keys with a transient reason.</param>
/// <param name="StoppedBy">Unauthorized or forbidden, if either stopped dispatch.</param>
/// <param name="Counts">Item status counts, source-changed items and unavailable answer numbers.</param>
/// <param name="Flags">Items requiring attention.</param>
/// <param name="NotIndexedAnswers">Answer numbers unavailable to the map.</param>
/// <param name="Path">The Evidence map file.</param>
internal sealed record EvidenceMapResult(string Advisor, string RequestedModel, string? ServedModel,
                                         bool Complete, int Required, int CacheHits, int CheckedThisCall,
                                         int Remaining, string? StoppedBy, IReadOnlyDictionary<string, int> Counts,
                                         IReadOnlyList<EvidenceFlag> Flags, IReadOnlyList<int> NotIndexedAnswers,
                                         string Path);

/// <summary>A logical judgment's actual outcomes and their provenance.</summary>
/// <param name="Key">The shared content key.</param>
/// <param name="Kind">Relevance or pair.</param>
/// <param name="UnitId">The local unit id for a pair.</param>
/// <param name="Questions">The outcomes by question role.</param>
/// <param name="FromStore">Whether these outcomes were read from the store.</param>
internal sealed record EvidenceJudgmentObservation(string Key, string Kind, string? UnitId,
    IReadOnlyDictionary<string, EvidenceQuestionOutcome> Questions, bool FromStore);

/// <summary>One item, including all its logical judgments even when keys are shared.</summary>
/// <param name="Id">The item id.</param>
/// <param name="Status">The settled status.</param>
/// <param name="SourceChanged">Whether the locator changed.</param>
/// <param name="Locator">External, none, or the current check's canonical fields.</param>
/// <param name="LocatorCheck">The current repository check, otherwise null.</param>
/// <param name="CandidateUnitIds">All candidates in plan order.</param>
/// <param name="Judgments">Every logical judgment with its shared outcome.</param>
internal sealed record EvidenceItemObservation(string Id, string Status, bool SourceChanged, string Locator,
    LocatorCheck? LocatorCheck, IReadOnlyList<string> CandidateUnitIds,
    IReadOnlyList<EvidenceJudgmentObservation> Judgments);

/// <summary>The return value keeps evaluation observations separate from tool result data.</summary>
/// <param name="Result">The content suitable for the tool result.</param>
/// <param name="Observation">The internal evaluation data, never returned by the tool.</param>
internal sealed record EvidenceMap(EvidenceMapResult Result, IReadOnlyList<EvidenceItemObservation> Observation);

/// <summary>Call counters remain available to the caller even when mapping throws.</summary>
internal sealed class EvidenceMapProgress
{
    /// <summary>The call's opening time.</summary>
    public DateTimeOffset At { get; internal set; }
    /// <summary>The most recent complete snapshot of the running accounting.</summary>
    public EvidenceMapResult? Result { get; internal set; }
    /// <summary>The latest internal observation, including cancellation outcomes.</summary>
    public IReadOnlyList<EvidenceItemObservation> Observation { get; internal set; } = [];
    /// <summary>AskAsync invocations, excluding the adapter's internal retries.</summary>
    public int Requests { get; internal set; }
    /// <summary>Invocations ending in AdvisorException.</summary>
    public int FailedRequests { get; internal set; }
    /// <summary>Judgments placed in requests, including isolated retries.</summary>
    public int JudgmentsAsked { get; internal set; }
    /// <summary>The sum of reported input tokens, or null without reported input usage.</summary>
    public long? InputTokens { get; internal set; }
    /// <summary>The sum of reported output tokens, or null without reported output usage.</summary>
    public long? OutputTokens { get; internal set; }
    /// <summary>Every AskAsync duration in milliseconds, including failed and cancelled requests.</summary>
    public List<double> AskDurations { get; } = [];
    /// <summary>The elapsed call duration.</summary>
    public TimeSpan Duration { get; internal set; }
    /// <summary>The nearest-rank median request duration, or null without requests.</summary>
    public double? LatencyP50 => Percentile(0.5);
    /// <summary>The nearest-rank ninetieth percentile, or null without requests.</summary>
    public double? LatencyP90 => Percentile(0.9);

    /// <summary>Computes nearest rank over every completed invocation.</summary>
    /// <param name="fraction">The percentile as a fraction.</param>
    private double? Percentile(double fraction) => AskDurations.Count == 0 ? null
        : AskDurations.Order().ElementAt((int)Math.Ceiling(fraction * AskDurations.Count) - 1);
}

/// <summary>Owns one bounded map call, from local indexing through final atomic persistence.</summary>
internal sealed class EvidenceMapper
{
    /// <summary>No request starts at or after this elapsed time.</summary>
    private static readonly TimeSpan DispatchBudget = TimeSpan.FromSeconds(40);
    /// <summary>In-flight requests are cancelled at this elapsed time.</summary>
    private static readonly TimeSpan PortionBudget = TimeSpan.FromSeconds(45);
    /// <summary>The monotonic opening timestamp, including tool preflight.</summary>
    private readonly long started;
    /// <summary>The single prepared call, validated before records become due.</summary>
    private Call? prepared;
    /// <summary>The clock used by budgets, durations and persistence throttling.</summary>
    private readonly TimeProvider clock;
    /// <summary>An optional observer of successful store writes, used by deterministic tests.</summary>
    private readonly Action? storeWritten;

    /// <summary>Creates a call owner with an injectable clock and store-write observer.</summary>
    /// <param name="clock">The budget clock, or the system clock.</param>
    /// <param name="storeWritten">An optional notification after each successful store write.</param>
    public EvidenceMapper(TimeProvider? clock = null, Action? storeWritten = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.storeWritten = storeWritten;
        started = this.clock.GetTimestamp();
        Progress.At = this.clock.GetUtcNow();
    }

    /// <summary>Running accounting, readable after success, cancellation or persistence failure.</summary>
    public EvidenceMapProgress Progress { get; private set; } = new();

    /// <summary>Maps all evidence without sending local source windows or local unit identities.</summary>
    /// <param name="workspaceRoot">The Run's repository root.</param>
    /// <param name="plan">The complete current plan.</param>
    /// <param name="entries">The validated Scout entries in append order.</param>
    /// <param name="scoutAnswers">The consumed answer counter.</param>
    /// <param name="changePoints">The Run's ordered Change points.</param>
    /// <param name="advisor">The selected, configured service.</param>
    /// <param name="path">The Run's Evidence map path.</param>
    /// <param name="implications">Optional Orchestrator-derived obligations.</param>
    /// <param name="ct">Caller cancellation, propagated after final persistence.</param>
    public async Task<EvidenceMap> MapAsync(string workspaceRoot, string plan, IReadOnlyList<ScoutAnswerEntry> entries,
                                           int scoutAnswers, IReadOnlyList<string> changePoints, IAdvisor advisor,
                                           string path, IReadOnlyList<EvidenceImplication>? implications = null,
                                           CancellationToken ct = default)
    {
        var index = EvidenceIndex.Of(entries, scoutAnswers, implications, workspaceRoot);
        Prepare(plan, index, changePoints, advisor, path);
        return await MapPreparedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Prepares and validates all map inputs once, without requests or final records.</summary>
    /// <param name="plan">The complete current plan.</param>
    /// <param name="index">The validated items with their current locator checks.</param>
    /// <param name="changePoints">The Run's ordered Change points.</param>
    /// <param name="advisor">The selected, configured service.</param>
    /// <param name="path">The Run's Evidence map path.</param>
    public void Prepare(string plan, EvidenceIndex index, IReadOnlyList<string> changePoints, IAdvisor advisor, string path)
    {
        var units = PlanUnits.Of(plan);
        var shared = EvidenceRubric.SharedState(units, changePoints);
        var work = new Dictionary<string, Work>(StringComparer.Ordinal);
        var items = new List<ItemWork>();
        foreach (var item in index.Items)
        {
            var candidates = CandidateSelection.Select(item, units, changePoints);
            var logical = new List<LogicalJudgment>();
            foreach (var judgment in EvidenceRubric.Judgments(item, candidates))
            {
                var key = EvidenceStore.Hash(EvidenceStore.KeyText(advisor.Id, advisor.RequestedModel, shared, item, judgment));
                if (!work.ContainsKey(key)) work.Add(key, new(key, item, judgment));
                logical.Add(new(judgment, work[key]));
            }
            items.Add(new(item, candidates, logical));
        }
        var store = EvidenceStore.Load(path, work.ToDictionary(pair => pair.Key, pair => pair.Value.Judgment));
        prepared = new Call(advisor, path, shared, index, items, work, store, Progress);
    }

    /// <summary>Runs the validated call with budgets and accounting measured from its opening.</summary>
    /// <param name="ct">Caller cancellation, propagated after final persistence.</param>
    public async Task<EvidenceMap> MapPreparedAsync(CancellationToken ct = default)
    {
        var session = prepared ?? throw new InvalidOperationException("The Evidence map must be prepared before dispatch.");
        var advisor = session.Advisor;
        session.ReadCache();
        var queue = session.Pack();
        var portionLeft = PortionBudget - clock.GetElapsedTime(started);
        using var portion = new CancellationTokenSource(portionLeft > TimeSpan.Zero ? portionLeft : TimeSpan.Zero, clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, portion.Token);
        var running = new List<Task<Reply>>();
        var lastWrite = started;
        Exception? failure = null;
        Exception? storeFailure = null;
        try
        {
            session.Snapshot();
            while (queue.Count > 0 || running.Count > 0)
            {
                while (queue.Count > 0 && running.Count < advisor.Limits.MaxConcurrency && !running.Any(task => task.IsCompleted)
                       && session.StoppedBy is null && !cancellation.IsCancellationRequested
                       && clock.GetElapsedTime(started) < DispatchBudget)
                {
                    running.Add(AskAsync(advisor, queue.Dequeue(), cancellation.Token));
                }
                if (running.Count == 0) break;
                var task = await Task.WhenAny(running).ConfigureAwait(false);
                running.Remove(task);
                session.Receive(await task.ConfigureAwait(false), queue);
                session.Snapshot();
                if (clock.GetElapsedTime(lastWrite) >= TimeSpan.FromSeconds(1))
                {
                    try { Write(session, false); }
                    catch (Exception error) { storeFailure = error; throw; }
                    lastWrite = clock.GetTimestamp();
                }
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            // Drain every started invocation before the last write or letting cancellation escape.
            cancellation.Cancel();
            foreach (var task in running)
            {
                try { session.Receive(await task.ConfigureAwait(false), queue); }
                catch (Exception error) { failure ??= error; }
            }
            session.Snapshot();
            try { Write(session, true); }
            catch (Exception error) { storeFailure ??= error; }
            Progress.Duration = clock.GetElapsedTime(started);
        }
        ct.ThrowIfCancellationRequested();
        if (storeFailure is not null) ExceptionDispatchInfo.Capture(storeFailure).Throw();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return new(Progress.Result!, Progress.Observation);
    }

    /// <summary>Persists the latest snapshot, advancing comparison marks only on the final write.</summary>
    /// <param name="call">The accumulated call state.</param>
    /// <param name="final">Whether this is the unconditional final write.</param>
    private void Write(Call call, bool final)
    {
        var result = Progress.Result!;
        var latest = JsonSerializer.SerializeToNode(result, EvidenceMapperJson.Default.EvidenceMapResult)!.AsObject();
        call.Store.Write(call.Work.Keys, latest, call.Marks, final && result.Complete);
        storeWritten?.Invoke();
    }

    /// <summary>Captures one invocation's outcome and duration without leaking adapter error content.</summary>
    /// <param name="advisor">The service to invoke.</param>
    /// <param name="batch">The exact request and distinct judgments it contains.</param>
    /// <param name="ct">The linked caller and portion cancellation.</param>
    private async Task<Reply> AskAsync(IAdvisor advisor, Batch batch, CancellationToken ct)
    {
        Progress.Requests++;
        Progress.JudgmentsAsked += batch.Work.Count;
        var started = clock.GetTimestamp();
        AdviceResult? result = null;
        string? reason = null;
        var failed = false;
        try { result = await advisor.AskAsync(batch.Request, ct).ConfigureAwait(false); }
        catch (AdvisorException error) { reason = Reason(error.Kind); failed = true; }
        catch (OperationCanceledException) { reason = "cancelled"; }
        return new(batch, result, reason, failed, clock.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Maps every service failure to its R13 unchecked reason.</summary>
    /// <param name="kind">The service-independent failure.</param>
    private static string Reason(AdvisorFailureKind kind) => kind switch
    {
        AdvisorFailureKind.Unauthorized => "unauthorized", AdvisorFailureKind.Forbidden => "forbidden",
        AdvisorFailureKind.RateLimited => "rate_limited", AdvisorFailureKind.Overloaded => "overloaded",
        AdvisorFailureKind.TooLarge => "too_large", AdvisorFailureKind.Rejected => "rejected",
        AdvisorFailureKind.TimedOut => "timed_out", AdvisorFailureKind.Unavailable => "unavailable",
        AdvisorFailureKind.InvalidResponse => "invalid_response",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>One distinct key's mutable outcome shared by every logical judgment using it.</summary>
    /// <param name="key">The content key.</param>
    /// <param name="item">The first item requiring the key.</param>
    /// <param name="judgment">The first logical judgment with this content.</param>
    private sealed class Work(string key, EvidenceItem item, EvidenceJudgment judgment)
    {
        /// <summary>The content-addressed identity.</summary>
        public string Key { get; } = key;
        /// <summary>The item whose canonical state is sent.</summary>
        public EvidenceItem Item { get; } = item;
        /// <summary>The questions sent for this key.</summary>
        public EvidenceJudgment Judgment { get; } = judgment;
        /// <summary>Outcomes by role, initially unsent.</summary>
        public IReadOnlyDictionary<string, EvidenceQuestionOutcome> Questions { get; set; } = Reasons(judgment, "not_yet_checked");
        /// <summary>Whether the current outcomes came from the store.</summary>
        public bool FromStore { get; set; }
        /// <summary>Whether this key is excluded from further dispatch in this call.</summary>
        public bool Settled { get; set; }
        /// <summary>Whether a reusable outcome was produced during this call.</summary>
        public bool Checked { get; set; }
    }

    /// <summary>A logical judgment pointing to the one shared key outcome.</summary>
    /// <param name="Judgment">The local judgment, retaining its unit identity.</param>
    /// <param name="Work">The shared key outcome.</param>
    private sealed record LogicalJudgment(EvidenceJudgment Judgment, Work Work);
    /// <summary>One item's local units and logical judgments.</summary>
    /// <param name="Item">The recorded item.</param>
    /// <param name="Candidates">All selected units in plan order.</param>
    /// <param name="Judgments">Every logical judgment.</param>
    private sealed record ItemWork(EvidenceItem Item, IReadOnlyList<PlanUnit> Candidates,
                                   IReadOnlyList<LogicalJudgment> Judgments);
    /// <summary>One dispatchable request of distinct keys.</summary>
    /// <param name="Request">The exact Advisor input.</param>
    /// <param name="Work">The distinct keys in question order.</param>
    private sealed record Batch(AdviceRequest Request, IReadOnlyList<Work> Work);
    /// <summary>A finished invocation, including failures and cancellation.</summary>
    /// <param name="Batch">The invoked request.</param>
    /// <param name="Result">The response, when any.</param>
    /// <param name="Reason">The whole-request unchecked reason.</param>
    /// <param name="Failed">Whether an AdvisorException ended the invocation.</param>
    /// <param name="Milliseconds">The invocation's elapsed time.</param>
    private sealed record Reply(Batch Batch, AdviceResult? Result, string? Reason, bool Failed, double Milliseconds);

    /// <summary>Gives every question in a judgment the same unchecked reason.</summary>
    /// <param name="judgment">The indivisible judgment.</param>
    /// <param name="reason">The reason it could not be checked.</param>
    private static Dictionary<string, EvidenceQuestionOutcome> Reasons(EvidenceJudgment judgment, string reason) =>
        judgment.Questions.ToDictionary(question => EvidenceStore.QuestionName(question.Id),
                                       _ => new EvidenceQuestionOutcome(null, reason), StringComparer.Ordinal);

    /// <summary>The mutable state owned exclusively by the mapper's sequential completion loop.</summary>
    /// <param name="advisor">The selected service.</param>
    /// <param name="path">The Evidence map path.</param>
    /// <param name="shared">The canonical shared state.</param>
    /// <param name="index">The complete item index.</param>
    /// <param name="items">The local logical judgments.</param>
    /// <param name="work">The distinct required keys.</param>
    /// <param name="store">The validated persistence state.</param>
    /// <param name="progress">The externally readable call progress.</param>
    private sealed class Call(IAdvisor advisor, string path, EvidenceSharedState shared, EvidenceIndex index,
                              IReadOnlyList<ItemWork> items, Dictionary<string, Work> work, EvidenceStore store,
                              EvidenceMapProgress progress)
    {
        /// <summary>The configured service used by the prepared call.</summary>
        public IAdvisor Advisor => advisor;
        /// <summary>The current distinct keys.</summary>
        public Dictionary<string, Work> Work { get; } = work;
        /// <summary>The call's validated store.</summary>
        public EvidenceStore Store { get; } = store;
        /// <summary>Current item comparison marks.</summary>
        public Dictionary<string, EvidenceItemMark> Marks { get; } = new(StringComparer.Ordinal);
        /// <summary>The access failure that stopped dispatch.</summary>
        public string? StoppedBy { get; private set; }
        /// <summary>The model selected by the first fresh response or stored fallback.</summary>
        private string? servedModel;
        /// <summary>Whether a fresh valid response has established the model.</summary>
        private bool freshModel;
        /// <summary>The number of keys found when the call began.</summary>
        private int cacheHits;

        /// <summary>Loads cache outcomes before any request and chooses the stored fallback model.</summary>
        public void ReadCache()
        {
            foreach (var part in Work.Values)
            {
                var stored = Store.Find(part.Key, part.Judgment);
                if (stored is null) continue;
                cacheHits++;
                servedModel ??= stored.ServedModel;
                if (stored.ServedModel is not null && stored.ServedModel != servedModel) { Drop(part); continue; }
                part.Questions = stored.Questions;
                part.FromStore = true;
                part.Settled = true;
            }
        }

        /// <summary>Packs each distinct unsatisfied key once, keeping every pair together.</summary>
        public Queue<Batch> Pack()
        {
            var queue = new Queue<Batch>();
            foreach (var group in Work.Values.Where(part => !part.Settled).GroupBy(part => part.Item))
            {
                var parts = group.ToArray();
                var packing = EvidenceRubric.Pack(advisor, shared, group.Key, parts.Select(part => part.Judgment).ToArray());
                foreach (var skipped in packing.Unchecked)
                {
                    var part = parts.Single(part => ReferenceEquals(part.Judgment, skipped.Judgment));
                    Save(part, Reasons(part.Judgment, skipped.Reason), null);
                }
                foreach (var request in packing.Requests)
                    queue.Enqueue(new(request.Request, request.Judgments.Select(judgment =>
                        parts.Single(part => ReferenceEquals(part.Judgment, judgment))).ToArray()));
            }
            return queue;
        }

        /// <summary>Applies one completion, splitting persistent request-wide failures into isolated requests.</summary>
        /// <param name="reply">The completed invocation.</param>
        /// <param name="queue">Requests still eligible for dispatch.</param>
        public void Receive(Reply reply, Queue<Batch> queue)
        {
            progress.AskDurations.Add(reply.Milliseconds);
            if (reply.Failed) progress.FailedRequests++;
            var result = reply.Result;
            if (result?.Usage?.InputTokens is { } input) progress.InputTokens = (progress.InputTokens ?? 0) + input;
            if (result?.Usage?.OutputTokens is { } output) progress.OutputTokens = (progress.OutputTokens ?? 0) + output;
            var validation = result is null ? null : EvidenceRubric.Validate(reply.Batch.Request, result);
            var reason = validation?.InvalidRequest == true ? "invalid_answer" : reply.Reason;
            if (reason is "unauthorized" or "forbidden") StoppedBy ??= reason;
            if (validation is { InvalidRequest: false })
            {
                if (!freshModel)
                {
                    servedModel = result!.ServedModel;
                    freshModel = true;
                    foreach (var part in Work.Values.Where(part => part.FromStore).ToArray())
                    {
                        var stored = Store.Judgments[part.Key];
                        if (stored.ServedModel is not null && stored.ServedModel != servedModel) Drop(part);
                    }
                }
                else if (result!.ServedModel != servedModel)
                {
                    foreach (var part in reply.Batch.Work) Drop(part);
                    return;
                }
            }
            if (reason is not null && EvidenceStore.IsPersistent(reason) && reply.Batch.Work.Count > 1)
            {
                foreach (var part in reply.Batch.Work)
                    queue.Enqueue(new(new(reply.Batch.Request.State, part.Judgment.Questions), [part]));
                return;
            }
            foreach (var part in reply.Batch.Work)
            {
                var outcomes = reason is not null ? Reasons(part.Judgment, reason)
                    : part.Judgment.Questions.ToDictionary(question => EvidenceStore.QuestionName(question.Id),
                                                          question => validation!.Questions[question.Id], StringComparer.Ordinal);
                Save(part, outcomes, validation is { InvalidRequest: false } ? result!.ServedModel : null);
            }
        }

        /// <summary>Invalidates an incompatible model without re-asking the key during this portion.</summary>
        /// <param name="part">The incompatible key.</param>
        private void Drop(Work part)
        {
            Store.Remove(part.Key);
            part.Questions = Reasons(part.Judgment, "not_yet_checked");
            part.FromStore = false;
            part.Checked = false;
            part.Settled = true;
        }

        /// <summary>Stores only reusable judgments, while retaining transient outcomes in the observation.</summary>
        /// <param name="part">The shared key.</param>
        /// <param name="outcomes">Every question's independently checked outcome.</param>
        /// <param name="model">The model reported for these outcomes, or null when no response established one.</param>
        private void Save(Work part, IReadOnlyDictionary<string, EvidenceQuestionOutcome> outcomes, string? model)
        {
            part.Questions = outcomes;
            part.FromStore = false;
            part.Settled = true;
            part.Checked = Store.Put(part.Key, new(part.Judgment.Kind, advisor.Id, advisor.RequestedModel, model, outcomes));
        }

        /// <summary>Settles every item, then publishes result and observation from the same outcomes.</summary>
        public void Snapshot()
        {
            var flags = new List<EvidenceFlag>();
            var observations = new List<EvidenceItemObservation>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var status in new[] { "contradiction", "unchecked", "context_only", "covered", "excluded",
                                          "uncertain", "no_candidates", "no_coverage_established", "sourceChanged", "notIndexedAnswers" })
                counts.Add(status, 0);
            Marks.Clear();
            foreach (var local in items)
            {
                var item = local.Item;
                var pairs = local.Judgments.Where(part => part.Judgment.Kind == "pair").ToArray();
                var verdicts = pairs.Select(part => new EvidenceUnitVerdict(part.Judgment.Unit!.Id,
                    part.Judgment.Unit.Label, EvidenceVerdicts.Pair(part.Work.Questions["relationship"], part.Work.Questions["obligation"]))).ToArray();
                var relevance = local.Judgments.FirstOrDefault(part => part.Judgment.Kind == "relevance")?.Work.Questions["relevance"];
                var status = EvidenceVerdicts.Item(relevance, verdicts.Select(unit => unit.Verdict).ToArray(),
                                                    local.Candidates.Any(unit => unit.Kind != "exclusion"));
                counts[status]++;
                if (item.SourceChanged) counts["sourceChanged"]++;
                var mark = new EvidenceItemMark(status, item.SourceChanged);
                Marks[EvidenceStore.ComparisonKey(item)] = mark;
                if (EvidenceVerdicts.IsFlag(status, item.SourceChanged))
                    flags.Add(new(item.Id, item.Kind, item.Category, item.Text, item.Source, status, item.SourceChanged,
                        item.CurrentCheck?.Reason, local.Judgments.SelectMany(part => part.Work.Questions.Values)
                            .Where(outcome => outcome.Reason is not null).Select(outcome => outcome.Reason!).Distinct().ToArray(),
                        Store.Change(item, mark), verdicts));
                observations.Add(new(item.Id, status, item.SourceChanged, EvidenceStore.Locator(item), item.CurrentCheck,
                    local.Candidates.Select(unit => unit.Id).ToArray(), local.Judgments.Select(part =>
                        new EvidenceJudgmentObservation(part.Work.Key, part.Judgment.Kind, part.Judgment.Unit?.Id,
                                                         part.Work.Questions, part.Work.FromStore)).ToArray()));
            }
            counts["notIndexedAnswers"] = index.NotIndexedAnswers.Count;
            var remaining = Work.Values.Count(part => part.Questions.Values.Any(outcome =>
                outcome.Reason is { } reason && !EvidenceStore.IsPersistent(reason)));
            progress.Result = new(advisor.Id, advisor.RequestedModel, servedModel, remaining == 0, Work.Count,
                cacheHits, Work.Values.Count(part => part.Checked), remaining, StoppedBy, counts, flags, index.NotIndexedAnswers, path);
            progress.Observation = observations;
        }
    }
}

/// <summary>The reflection-free map result contract used for latest and tool composition.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EvidenceMapResult))]
internal sealed partial class EvidenceMapperJson : JsonSerializerContext;
