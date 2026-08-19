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
    private const string ReviewLogFileName = "review-log.md";
    private const string FlowLogFileName = "flow_log.md";
    private const string DiagnosticLogFileName = "forge.log";
    private const string JobsFolder = "jobs";
    private const string CritiquesFolder = "critiques";
    private const string BaselineFileName = "baseline.patch";
    private const string PlanFileName = "PLAN.md";

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
            throw new ArgumentException("job id must be a file name", nameof(jobId));

        var jobsPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, JobsFolder));
        var jobPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(jobsPath, jobId + ".json"));
        var prefix = jobsPath.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!jobPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("job id resolves outside the run's jobs folder", nameof(jobId));

        return jobPath;
    }

    internal IEnumerable<string> EnumerateJobFiles()
    {
        var jobsPath = System.IO.Path.Combine(Path, JobsFolder);
        return Directory.Exists(jobsPath) ? Directory.EnumerateFiles(jobsPath, "*.json") : [];
    }

    public string ReviewLogPath => System.IO.Path.Combine(Path, ReviewLogFileName);

    public string FlowLogPath => System.IO.Path.Combine(Path, FlowLogFileName);

    /// <summary>
    /// The run's operational log. Append-only and the one file under <c>.forge/</c> that agents may
    /// add to, through <c>forge.log.append</c> rather than by hand.
    /// </summary>
    public string DiagnosticLogPath => System.IO.Path.Combine(Path, DiagnosticLogFileName);

    public RunLog Log => new(DiagnosticLogPath);

    private string PlanPath => System.IO.Path.Combine(Path, PlanFileName);

    public static RunDirectory Create(string workspaceRoot, string runId)
    {
        var runPath = Confine(workspaceRoot, runId);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(runPath)!);
        AtomicFile.Write(System.IO.Path.Combine(workspaceRoot, ForgeFolder, ".gitignore"), SelfIgnore);
        Directory.CreateDirectory(runPath);

        return new RunDirectory(runId, runPath);
    }

    public static RunDirectory Open(string workspaceRoot, string runId)
    {
        var runPath = Confine(workspaceRoot, runId);
        if (!Directory.Exists(runPath)) 
            throw new RunNotFoundException(runId);
        return new RunDirectory(runId, runPath);
    }

    /// <summary>
    /// The second of the two surviving checks: everything this class writes is under the run folder,
    /// so one containment test on the run id replaces the six symlink and reparse-point guards. Both
    /// the workspace root and the run id reach us from a tool call, which makes them caller input.
    /// </summary>
    /// <remarks>
    /// The root has to be absolute, and that is worth refusing rather than resolving. A relative one
    /// would be resolved against the server process's working directory, which is not the repository
    /// and differs by host — the plugin folder under Codex, whatever the host started in elsewhere.
    /// Two sessions passing a relative root would then land in the same folder, and their runs would
    /// silently share it.
    /// </remarks>
    private static string Confine(string workspaceRoot, string runId)
    {
        if (!System.IO.Path.IsPathRooted(workspaceRoot)) throw new WorkspaceNotRootedException(workspaceRoot);

        var forgeRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(workspaceRoot, ForgeFolder));
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

    public void WritePlan(string plan) => AtomicFile.Write(PlanPath, plan);

    public string ReadPlan() => AtomicFile.Read(PlanPath);

    /// <summary>
    /// The log the next round's critic reads. A fresh critic without it oscillates; with it, it
    /// converges without inheriting the previous round's anchoring.
    /// </summary>
    public string ReadReviewLog() =>
        File.Exists(ReviewLogPath) ? AtomicFile.Read(ReviewLogPath) : string.Empty;

    public void AppendReviewRound(int round, Critique critique)
    {
        AtomicFile.Append(ReviewLogPath, CritiqueEntry($"## Round {round}", critique));

        var critiques = System.IO.Path.Combine(Path, CritiquesFolder);
        AtomicFile.Write(System.IO.Path.Combine(critiques, $"round-{round:00}.json"),
            JsonSerializer.Serialize(critique, ContractJson.Default.Critique));
    }

    /// <summary>
    /// Records what the orchestrator did with a round's findings: what went to the builder, and
    /// what was deferred and why. The deferral is the part that matters — the next round's critic
    /// reads it as a decision rather than an omission, which is what stops it re-raising the same
    /// out-of-scope finding as a blocker every round.
    /// </summary>
    public void AppendReviewFix(int round, string findings, string? deferred)
    {
        var entry = new StringBuilder().Append("## Round ").Append(round).Append(" fixes").AppendLine()
                                       .AppendLine()
                                       .AppendLine(findings.TrimEnd());

        if (deferred is { Length: > 0 })
            entry.AppendLine()
                 .AppendLine("### Deferred by the orchestrator")
                 .AppendLine()
                 .AppendLine(deferred.TrimEnd());

        entry.AppendLine();
        AtomicFile.Append(ReviewLogPath, entry.ToString());
    }

    /// <summary>
    /// The user-facing timeline of the delegated acts, one file for the orchestrator to surface in
    /// whatever panel the host has. Unlike the review log it is never fed back to a worker, which
    /// is why builder entries can live here without shifting what the next round's critic judges.
    /// </summary>
    public void AppendFlowCritique(string act, int round, Critique critique) =>
        AtomicFile.Append(FlowLogPath, CritiqueEntry($"## {act} — round {round}", critique));

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
            entry.Append("- **").Append(finding.Severity).Append("** ")
                 .Append(finding.Where).Append(" — ").AppendLine(finding.What);
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
             .AppendLine()
             .AppendLine(result.Summary)
             .AppendLine();

        foreach (var file in result.FilesChanged)
            entry.Append("- `").Append(file).AppendLine("`");

        if (result.FilesChanged.Count > 0) entry.AppendLine();
    }
}

// The code-review defaults keep state files written before the counters existed readable; the cap
// default matches what forge.begin writes today.
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
                                int CodeReviewRoundCap = 3);

internal sealed class RunNotFoundException(string runId) : Exception($"run {runId} was not found");

internal sealed class RunEscapedException(string runId)
    : Exception($"run id {runId} resolves outside .forge/");

internal sealed class WorkspaceNotRootedException(string workspaceRoot)
    : Exception($"workspaceRoot must be an absolute path, and '{workspaceRoot}' is not");

// Reflection-based serialization is off repo-wide (Directory.Build.props), so every persisted
// shape needs a source-generated contract.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RunState))]
internal sealed partial class ForgeJson : JsonSerializerContext;
