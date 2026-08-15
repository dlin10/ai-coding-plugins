using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanForge.Repo;
using PlanForge.Vendors;

namespace PlanForge.Run;

/// <summary>One run's state, isolated under <c>.forge/&lt;runId&gt;/</c>.</summary>
internal sealed class RunDirectory
{
    private const string ForgeFolder = ".forge";
    private const string StateFileName = "state.json";
    private const string ReviewLogFileName = "review-log.md";
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

    public string ReviewLogPath => System.IO.Path.Combine(Path, ReviewLogFileName);

    public string PlanPath => System.IO.Path.Combine(Path, PlanFileName);

    public static RunDirectory Create(string workspaceRoot, string runId)
    {
        var runPath = Confine(workspaceRoot, runId);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(runPath)!);
        File.WriteAllText(System.IO.Path.Combine(workspaceRoot, ForgeFolder, ".gitignore"), SelfIgnore);
        Directory.CreateDirectory(runPath);

        return new RunDirectory(runId, runPath);
    }

    public static RunDirectory Open(string workspaceRoot, string runId)
    {
        var runPath = Confine(workspaceRoot, runId);
        if (!Directory.Exists(runPath)) throw new RunNotFoundException(runId);
        return new RunDirectory(runId, runPath);
    }

    /// <summary>
    /// The second of the two surviving checks: everything this class writes is under the run folder,
    /// so one containment test on the run id replaces the six symlink and reparse-point guards. The
    /// run id reaches us from a tool call, which makes it caller-controlled input.
    /// </summary>
    private static string Confine(string workspaceRoot, string runId)
    {
        var forgeRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(workspaceRoot, ForgeFolder));
        var runPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(forgeRoot, runId));

        var prefix = forgeRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!runPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new RunEscapedException(runId);

        return runPath;
    }

    public RunState ReadState()
    {
        var path = System.IO.Path.Combine(Path, StateFileName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, ForgeJson.Default.RunState)
            ?? throw new RunNotFoundException(RunId);
    }

    public void WriteState(RunState state)
    {
        var json = JsonSerializer.Serialize(state, ForgeJson.Default.RunState);
        File.WriteAllText(System.IO.Path.Combine(Path, StateFileName), json);
    }

    public void WriteBaseline(Baseline baseline) =>
        File.WriteAllText(System.IO.Path.Combine(Path, BaselineFileName), baseline.Diff);

    public Baseline ReadBaseline(string head)
    {
        var path = System.IO.Path.Combine(Path, BaselineFileName);
        return new Baseline(head, File.Exists(path) ? File.ReadAllText(path) : string.Empty);
    }

    public void WritePlan(string plan) => File.WriteAllText(PlanPath, plan);

    /// <summary>
    /// The log the next round's critic reads. A fresh critic without it oscillates; with it, it
    /// converges without inheriting the previous round's anchoring.
    /// </summary>
    public string ReadReviewLog() =>
        File.Exists(ReviewLogPath) ? File.ReadAllText(ReviewLogPath) : string.Empty;

    public void AppendReviewRound(int round, Critique critique)
    {
        var entry = new StringBuilder()
            .Append("## Round ").Append(round).AppendLine()
            .AppendLine()
            .Append("Verdict: ").Append(critique.Verdict).AppendLine()
            .AppendLine()
            .AppendLine(critique.Summary)
            .AppendLine();

        foreach (var finding in critique.Findings)
            entry.Append("- **").Append(finding.Severity).Append("** ")
                 .Append(finding.Where).Append(" — ").AppendLine(finding.What);

        entry.AppendLine();
        File.AppendAllText(ReviewLogPath, entry.ToString());

        var critiques = System.IO.Path.Combine(Path, CritiquesFolder);
        Directory.CreateDirectory(critiques);
        File.WriteAllText(System.IO.Path.Combine(critiques, $"round-{round:00}.json"),
            JsonSerializer.Serialize(critique, ContractJson.Default.Critique));
    }
}

internal sealed record RunState(string RunId,
                                string WorkspaceRoot,
                                string Profile,
                                DateTimeOffset StartedAt,
                                int ReviewRounds,
                                int ReviewRoundCap,
                                string BaselineHead = "",
                                bool Approved = false,
                                int TasksCompleted = 0,
                                string BuilderSessionId = "");

internal sealed class RunNotFoundException(string runId) : Exception($"run {runId} was not found");

internal sealed class RunEscapedException(string runId)
    : Exception($"run id {runId} resolves outside .forge/");

// Reflection-based serialization is off repo-wide (Directory.Build.props), so every persisted
// shape needs a source-generated contract.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RunState))]
internal sealed partial class ForgeJson : JsonSerializerContext;
