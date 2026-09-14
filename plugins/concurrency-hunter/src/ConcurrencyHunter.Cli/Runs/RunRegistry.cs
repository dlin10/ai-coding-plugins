using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Narrative;
using ConcurrencyHunter.Reporting;

namespace ConcurrencyHunter.Runs;

internal sealed class RunRegistry
{
    private static readonly TimeSpan DEADLINE = TimeSpan.FromMinutes(30);
    private static readonly UTF8Encoding UTF8_WITHOUT_BOM = new(false);

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly string _bundleRoot;
    private readonly AnalysisStep _analysis;
    private readonly Dictionary<string, RunEntry> _runs = new(StringComparer.Ordinal);
    private RunEntry? _current;

    internal RunRegistry(TimeProvider time, string bundleRoot, AnalysisStep analysis)
    {
        _time = time;
        _bundleRoot = bundleRoot;
        _analysis = analysis;
    }

    internal StartResult Start(string? target)
    {
        lock (_gate)
        {
            var resolution = TargetResolver.Resolve(target);
            if (resolution.Kind == TargetKind.TooLong)
                return new StartResult("targetPathTooLong", null, null, null, [], 0);
            if (resolution.Kind == TargetKind.Ambiguous)
            {
                var candidates = FitCandidates(resolution.Candidates);
                return new StartResult(null, null, null, null, candidates, resolution.Candidates.Count);
            }

            var now = _time.GetUtcNow();
            if (_current is not null)
            {
                ObserveDeadline(_current, now);
                if (_current.State != RunState.Done && now < _current.Deadline)
                    return new StartResult("runActive", _current.RunId, _current.Deadline, _current.Target, [], 0);
            }

            var runId = CreateRunId(now);
            var run = new RunEntry(runId, resolution.Path, now, now + DEADLINE);
            _runs.Add(runId, run);
            _current = run;
            run.Timer = _time.CreateTimer(_ => DeadlineTimer(run), null, DEADLINE, Timeout.InfiniteTimeSpan);

            if (resolution.Kind == TargetKind.Unresolvable)
            {
                var reason = resolution.Reason ?? "target could not be resolved";
                run.Analysis = new RunAnalysis(true, false, 0, 0, [], null, [reason], 0, 0);
                run.State = RunState.Failed;
                run.AnalysisTask = Task.CompletedTask;
            }
            else
            {
                run.State = RunState.Running;
                run.AnalysisTask = Task.Run(() => CompleteAnalysisAsync(run));
            }

            return new StartResult(null, runId, run.Deadline, run.Target, [], 0);
        }
    }

    internal PollResult Poll(string runId)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run))
                return EmptyPoll("runNotFound");

            var now = _time.GetUtcNow();
            ObserveDeadline(run, now);
            var analysis = run.Analysis;
            var warnings = new List<string>();
            if (analysis is not null)
                warnings.AddRange(analysis.MissingProjects);
            if (run.DeadlineExceeded)
                warnings.Add("deadlineExceeded");
            var warningResults = warnings.Take(20).Select(item => ResponseBudget.Fit(item, 256)).ToArray();
            return new PollResult(null, Phase(run.State), StateName(run.State), run.DeadlineExceeded, analysis?.ProjectsExpected ?? 0,
                                  analysis?.ProjectsLoaded ?? 0, analysis?.Result?.Roots.Count ?? 0, analysis?.Result?.Findings.Count ?? 0,
                                  analysis?.Result?.Groups.Count ?? 0, NarrativesAccepted(run), WholeSeconds(now - run.StartedAt),
                                  WholeSeconds(run.Deadline - now), warningResults, warnings.Count);
        }
    }

    internal GroupsResult GetGroups(string runId, string? level)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run))
                return new GroupsResult("runNotFound", []);

            ObserveDeadline(run, _time.GetUtcNow());
            if (run.DeadlineExceeded)
                return new GroupsResult("deadlineExceeded", []);
            if (run.State == RunState.Running)
                return new GroupsResult("notReady", []);
            if (run.State == RunState.Failed)
                return new GroupsResult("runFailed", []);
            if (run.State == RunState.Done)
                return new GroupsResult("runRendered", []);

            var normalizedLevel = level?.ToLowerInvariant();
            if (normalizedLevel is not null and not ("high" or "medium" or "low"))
                return new GroupsResult("invalidLevel", []);

            var result = run.Analysis?.Result;
            if (result is null)
                return new GroupsResult("notReady", []);

            var groups = result.Groups
                               .Where(group => normalizedLevel is null || group.ConfidenceLabel.Equals(normalizedLevel, StringComparison.OrdinalIgnoreCase))
                               .OrderBy(group => ConfidenceRank(group.ConfidenceLabel))
                               .ThenBy(group => GroupNumber(group.GroupId))
                               .ThenBy(group => group.GroupId, StringComparer.Ordinal)
                               .Select(group => BuildDigest(group, result))
                               .ToArray();
            return new GroupsResult(null, groups);
        }
    }

    internal SubmissionResult Submit(string runId, string target, string text)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run))
                return Submission("notReady", [], 2);

            var now = _time.GetUtcNow();
            ObserveDeadline(run, now);
            if (run.DeadlineExceeded)
            {
                run.LateResponses.Add(new LateResponse(target, now));
                return Submission("late", [], AttemptsRemaining(run, target));
            }

            var result = run.Analysis?.Result;
            if (run.State != RunState.AwaitingNarrative || result is null)
                return Submission("notReady", [], AttemptsRemaining(run, target));

            var isSummary = target == "summary";
            var isGroup = result.Groups.Any(group => group.GroupId == target);
            if (!isSummary && !isGroup)
                return Submission("rejected", ["unknownTarget"], 2);

            var submission = GetSubmission(run, target);
            if (submission.Accepted)
                return Submission("rejected", ["alreadyAccepted"], 0);
            if (submission.Rejections >= 2)
                return Submission("rejected", ["retryExhausted"], 0);

            var scope = isSummary
                            ? NarrativeScope.ForSummary(result)
                            : NarrativeScope.ForGroup(result, target,
                                                      BuildDigest(result.Groups.Single(group => group.GroupId == target), result).Findings.Count);
            var verdict = NarrativeValidator.Validate(text, scope);
            submission.Attempts++;
            if (verdict.Accepted)
            {
                submission.Accepted = true;
                submission.LastReasons = [];
                if (isSummary)
                    run.AcceptedSummary = text;
                else
                    run.AcceptedGroupNarratives[target] = text;
                return Submission("accepted", [], 0);
            }

            submission.Rejections++;
            submission.LastReasons = verdict.Reasons;
            return Submission("rejected", verdict.Reasons, Math.Max(0, 2 - submission.Rejections));
        }
    }

    internal RenderResult Render(string runId)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run))
                return RenderError("runNotFound", null);
            if (run.Rendered is not null)
                return run.Rendered;

            var now = _time.GetUtcNow();
            ObserveDeadline(run, now);
            if (run.State == RunState.Running)
                return RenderError("notReady", null);

            string fullBundleRoot;
            try
            {
                fullBundleRoot = Path.GetFullPath(_bundleRoot);
            }
            catch (Exception error)
            {
                return RenderError("renderFailed", error.Message);
            }

            if (ResponseBudget.Weight(fullBundleRoot) > 960)
                return RenderError("renderFailed", "bundle root exceeds 960 bytes");

            var analysis = run.Analysis;
            var decision = RunStatusRules.Decide(new StatusInputs(analysis?.LoadFailed ?? false, analysis?.AnalysisFailed ?? false,
                                                                  analysis is null || analysis.MissingProjects.Count == 0, run.DeadlineExceeded,
                                                                  analysis?.Result, run.AcceptedGroupNarratives.Keys.ToHashSet(StringComparer.Ordinal)));
            var diagnostics = analysis?.Diagnostics.Select(item => ResponseBudget.Fit(item, 256)).ToArray() ?? [];
            var report = new RunReport(run.RunId, run.Target, BuildInfo.Version, run.StartedAt, run.Deadline, now, decision, analysis?.ProjectsExpected ?? 0,
                                       analysis?.ProjectsLoaded ?? 0, analysis?.MissingProjects.Select(item => ResponseBudget.Fit(item, 256)).ToArray() ?? [],
                                       analysis?.Result, run.AcceptedGroupNarratives, run.AcceptedSummary, NarrativeRecords(run), run.LateResponses,
                                       diagnostics, analysis?.LoadSeconds ?? 0, analysis?.AnalysisSeconds ?? 0);

            string? partialPath = null;
            try
            {
                var bundle = ReportRenderer.Render(report);
                var repositoryDirectory = RepositoryDirectory(run.Target);
                var repositoryHash = repositoryDirectory is null ? "unresolved" : HashRepository(repositoryDirectory);
                var repositoryRoot = Path.Combine(fullBundleRoot, repositoryHash);
                var finalPath = Path.Combine(repositoryRoot, run.RunId);
                partialPath = Path.Combine(repositoryRoot, $"{run.RunId}.partial-{RandomHex()}");
                Directory.CreateDirectory(partialPath);
                File.WriteAllText(Path.Combine(partialPath, "report.md"), bundle.ReportMarkdown, UTF8_WITHOUT_BOM);
                File.WriteAllText(Path.Combine(partialPath, "findings.json"), bundle.FindingsJson, UTF8_WITHOUT_BOM);
                File.WriteAllText(Path.Combine(partialPath, "run-metadata.json"), bundle.RunMetadataJson, UTF8_WITHOUT_BOM);
                Directory.Move(partialPath, finalPath);

                var reasons = decision.Reasons.Take(10).Select(item => ResponseBudget.Fit(item, 256)).ToArray();
                var rendered = new RenderResult(null, null, decision.Status, reasons, decision.Reasons.Count, analysis?.Result?.Findings.Count ?? 0,
                                                analysis?.Result?.Groups.Count ?? 0, NarrativesAccepted(run), run.LateResponses.Count,
                                                (now - run.StartedAt).TotalSeconds, finalPath, Path.Combine(finalPath, "report.md"));
                run.State = RunState.Done;
                run.Timer?.Dispose();
                run.Timer = null;
                run.Rendered = rendered;
                return rendered;
            }
            catch (Exception error)
            {
                if (partialPath is not null && Directory.Exists(partialPath))
                {
                    try
                    {
                        Directory.Delete(partialPath, true);
                    }
                    catch
                    {
                    }
                }

                return RenderError("renderFailed", error.Message);
            }
        }
    }

    internal async Task WaitForAnalysisAsync(string runId)
    {
        Task analysisTask;
        lock (_gate)
        {
            analysisTask = _runs.TryGetValue(runId, out var run) ? run.AnalysisTask : Task.CompletedTask;
        }

        await analysisTask.ConfigureAwait(false);
    }

    private async Task CompleteAnalysisAsync(RunEntry run)
    {
        try
        {
            var result = await _analysis(run.Target!, run.Cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                ObserveDeadline(run, _time.GetUtcNow());
                if (run.State != RunState.Running)
                    return;
                run.Analysis = result;
                run.State = result.LoadFailed || result.AnalysisFailed ? RunState.Failed : RunState.AwaitingNarrative;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                ObserveDeadline(run, _time.GetUtcNow());
                if (run.State == RunState.Running)
                    run.State = RunState.AwaitingNarrative;
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                ObserveDeadline(run, _time.GetUtcNow());
                if (run.State != RunState.Running)
                    return;
                run.Analysis = new RunAnalysis(false, true, 0, 0, [], null, [error.Message], 0, 0);
                run.State = RunState.Failed;
            }
        }
    }

    private void DeadlineTimer(RunEntry run)
    {
        lock (_gate)
            ObserveDeadline(run, _time.GetUtcNow());
    }

    private static void ObserveDeadline(RunEntry run, DateTimeOffset now)
    {
        if (run.DeadlineExceeded || now < run.Deadline)
            return;

        run.DeadlineExceeded = true;
        if (run.State == RunState.Running)
        {
            run.Cancellation.Cancel();
            run.State = RunState.AwaitingNarrative;
            run.Analysis = null;
        }
    }

    private static GroupDigest BuildDigest(FindingGroup group, AnalysisResult result)
    {
        foreach (var variant in new[] { (3, 200), (1, 200), (1, 100), (1, 48) })
        {
            var digest = BuildDigest(group, result, variant.Item1, variant.Item2);
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(digest)) <= 4096)
                return digest;
        }

        return BuildDigest(group, result, 1, 48);
    }

    private static GroupDigest BuildDigest(FindingGroup group, AnalysisResult result, int findingLimit, int stringLimit)
    {
        var allFindings = group.FindingIds.Select(id => result.Findings.Single(finding => finding.FindingId == id)).ToArray();
        var findings = allFindings.Take(findingLimit)
                                  .Select(finding => new DigestFinding(ResponseBudget.Fit(finding.FindingId, stringLimit),
                                                                       ResponseBudget.Fit(finding.ProtectionResult, stringLimit),
                                                                       finding.Evidence.Select(item => ResponseBudget.Fit(item.Id, stringLimit)).ToArray(),
                                                                       new[]
                                                                       {
                                                                           DigestAccess("A", finding.AccessA, stringLimit),
                                                                           DigestAccess("B", finding.AccessB, stringLimit)
                                                                       }))
                                  .ToArray();
        var scenario = allFindings.FirstOrDefault()?.Scenario.Select(step => ResponseBudget.Fit(step, stringLimit)).ToArray() ?? [];
        return new GroupDigest(ResponseBudget.Fit(group.GroupId, stringLimit), ResponseBudget.Fit(group.RuleId, stringLimit),
                               ResponseBudget.Fit(group.ConfidenceLabel, stringLimit), ResponseBudget.Fit(group.Resource.Region, stringLimit),
                               group.Resource.AccessPath.Take(3).Select(item => ResponseBudget.Fit(item, stringLimit)).ToArray(), group.FindingIds.Count,
                               findings, scenario);
    }

    private static DigestAccess DigestAccess(string role, StaticAccess access, int stringLimit) => new(ResponseBudget.Fit(role, stringLimit),
                                                                                                       ResponseBudget.Fit(access.Symbol, stringLimit),
                                                                                                       ResponseBudget.Fit(access.Operation.ToWireName(),
                                                                                                        stringLimit),
                                                                                                       ResponseBudget.Fit(access.Source.Path, stringLimit),
                                                                                                       access.Source.StartLine);

    private static SubmissionState GetSubmission(RunEntry run, string target)
    {
        if (run.Submissions.TryGetValue(target, out var submission))
            return submission;

        submission = new SubmissionState();
        run.Submissions.Add(target, submission);
        run.SubmissionOrder.Add(target);
        return submission;
    }

    private static IReadOnlyList<NarrativeRecord> NarrativeRecords(RunEntry run) =>
        run.SubmissionOrder.Select(target =>
            {
                var submission = run.Submissions[target];
                return new NarrativeRecord(ResponseBudget.Fit(target, 256), submission.Attempts, submission.Accepted ? "Accepted" : "Rejected",
                                           submission.LastReasons.Select(reason => ResponseBudget.Fit(reason, 256)).ToArray());
            })
           .ToArray();

    private static SubmissionResult Submission(string status, IReadOnlyList<string> reasons, int attemptsRemaining)
    {
        var returned = reasons.Take(20).Select(reason => ResponseBudget.Fit(reason, 256)).ToArray();
        return new SubmissionResult(status, returned, reasons.Count, attemptsRemaining);
    }

    private static int AttemptsRemaining(RunEntry run, string target) =>
        run.Submissions.TryGetValue(target, out var submission) ? submission.Accepted ? 0 : Math.Max(0, 2 - submission.Rejections) : 2;

    private static RenderResult RenderError(string error, string? message) =>
        new(error, message is null ? null : ResponseBudget.Fit(message, 512), null, [], 0, 0, 0, 0, 0, 0, null, null);

    private static PollResult EmptyPoll(string error) => new(error, null, null, false, 0, 0, 0, 0, 0, 0, 0, 0, [], 0);

    private static IReadOnlyList<string> FitCandidates(IReadOnlyList<string> candidates)
    {
        var returned = new List<string>();
        var weight = 0;
        foreach (var candidate in candidates)
        {
            var candidateWeight = ResponseBudget.Weight(candidate);
            if (weight + candidateWeight > 6144)
                break;
            returned.Add(candidate);
            weight += candidateWeight;
        }

        return returned;
    }

    private string CreateRunId(DateTimeOffset now)
    {
        string runId;
        do
        {
            runId = now.ToString("yyyyMMdd-HHmmss-", CultureInfo.InvariantCulture) + RandomHex();
        } while (_runs.ContainsKey(runId));

        return runId;
    }

    private static string RandomHex() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    private static string? RepositoryDirectory(string? target)
    {
        if (target is null)
            return null;
        var fullPath = Path.GetFullPath(target);
        return Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
    }

    private static string HashRepository(string directory)
    {
        var identity = Path.GetFullPath(directory).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12].ToLowerInvariant();
    }

    private static int NarrativesAccepted(RunEntry run) =>
        run.AcceptedGroupNarratives.Count + (run.AcceptedSummary is null ? 0 : 1);

    private static long WholeSeconds(TimeSpan value) =>
        Math.Max(0, (long)Math.Floor(value.TotalSeconds));

    private static string Phase(RunState state) => state switch
                                                   {
                                                       RunState.Running => "analysis",
                                                       RunState.AwaitingNarrative => "narrative",
                                                       RunState.Failed => "failed",
                                                       RunState.Done => "report",
                                                       _ => throw new ArgumentOutOfRangeException(nameof(state))
                                                   };

    private static string StateName(RunState state) => state switch
                                                       {
                                                           RunState.Running => "running",
                                                           RunState.AwaitingNarrative => "awaiting_narrative",
                                                           RunState.Failed => "failed",
                                                           RunState.Done => "done",
                                                           _ => throw new ArgumentOutOfRangeException(nameof(state))
                                                       };

    private static int ConfidenceRank(string label) => label switch
                                                       {
                                                           "High" => 0,
                                                           "Medium" => 1,
                                                           _ => 2
                                                       };

    private static int GroupNumber(string groupId) =>
        groupId.Length > 1 && int.TryParse(groupId.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : int.MaxValue;

    private enum RunState
    {
        Running,
        AwaitingNarrative,
        Failed,
        Done
    }

    private sealed class RunEntry(string runId, string? target, DateTimeOffset startedAt, DateTimeOffset deadline)
    {
        internal string RunId { get; } = runId;
        internal string? Target { get; } = target;
        internal DateTimeOffset StartedAt { get; } = startedAt;
        internal DateTimeOffset Deadline { get; } = deadline;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Dictionary<string, string> AcceptedGroupNarratives { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, SubmissionState> Submissions { get; } = new(StringComparer.Ordinal);
        internal List<string> SubmissionOrder { get; } = [];
        internal List<LateResponse> LateResponses { get; } = [];
        internal RunState State { get; set; }
        internal bool DeadlineExceeded { get; set; }
        internal RunAnalysis? Analysis { get; set; }
        internal string? AcceptedSummary { get; set; }
        internal Task AnalysisTask { get; set; } = Task.CompletedTask;
        internal ITimer? Timer { get; set; }
        internal RenderResult? Rendered { get; set; }
    }

    private sealed class SubmissionState
    {
        internal int Attempts { get; set; }
        internal int Rejections { get; set; }
        internal bool Accepted { get; set; }
        internal IReadOnlyList<string> LastReasons { get; set; } = [];
    }
}
