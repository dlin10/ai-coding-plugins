using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Common.Mcp;
using ConcurrencyHunter.Runs;
using ConcurrencyHunter.Serialization;
using ModelContextProtocol.Server;

namespace ConcurrencyHunter.Mcp;

[McpServerToolType]
internal sealed class RunTools
{
    [McpServerTool(Name = "run_start"), Description("Starts a Concurrency Hunter analysis for an absolute solution, project, or repository path.")]
    public static string run_start(RunRegistry registry,
                                   [Description("Absolute path to a .sln, .slnx, .csproj, or directory containing solutions.")] string target)
    {
        var result = registry.Start(target);
        if (result.Error is not null)
        {
            return Error(result.Error, Message(result.Error), result.Error == "runActive" ? result.RunId : null);
        }

        return Serialize(new StartPayload(result.RunId, result.Deadline, result.ResolvedTarget, result.Candidates, result.CandidatesTotal),
                         ConcurrencyHunterJsonContext.Default.StartPayload);
    }

    [McpServerTool(Name = "run_poll"), Description("Reports the current phase, progress, deadline, counts, and warnings for a run.")]
    public static string run_poll(RunRegistry registry, [Description("Run identifier returned by run_start.")] string run_id)
    {
        var result = registry.Poll(run_id);
        if (result.Error is not null)
            return RegistryError(result.Error);

        return
            Serialize(new PollPayload(result.Phase!, result.State!, result.DeadlineExceeded, new RunCounts(result.ProjectsExpected, result.ProjectsLoaded, 
                                       result.Roots, result.Findings, result.Groups, result.NarrativesAccepted), result.ElapsedSeconds, result.RemainingSeconds,
                                      result.Warnings, result.WarningsTotal),
                      ConcurrencyHunterJsonContext.Default.PollPayload);
    }

    [McpServerTool(Name = "get_groups"), Description("Returns a one-based page of finding-group digests, optionally filtered by confidence level.")]
    public static string get_groups(RunRegistry registry, [Description("Run identifier returned by run_start.")] string run_id,
                                    [Description("One-based page number. Defaults to 1.")] int page = 1,
                                    [Description("Optional confidence level: high, medium, or low.")] string? level = null)
    {
        if (page < 1)
            return Error("invalidPage", "Page must be at least 1.");

        var result = registry.GetGroups(run_id, level);
        if (result.Error is not null)
            return RegistryError(result.Error);

        var envelope = ResponseEnvelope.Create(result.Groups, new PageArguments { Page = page, PageSize = 5 },
                                               ConcurrencyHunterJsonContext.Default.ListEnvelopeGroupDigest);
        return Serialize(envelope, ConcurrencyHunterJsonContext.Default.ListEnvelopeGroupDigest);
    }

    [McpServerTool(Name = "submit_narrative"), Description("Submits a group or summary narrative for deterministic evidence validation.")]
    public static string submit_narrative(RunRegistry registry, [Description("Run identifier returned by run_start.")] string run_id,
                                          [Description("A group id or summary.")] string target,
                                          [Description("Narrative text to validate and retain when accepted.")] string text)
    {
        var result = registry.Submit(run_id, target, text);
        if (result.Status == "notReady" && result.Reasons.Count == 0 && result.ReasonsTotal == 0 && registry.Poll(run_id).Error == "runNotFound")
        {
            return Error("unknownRun", Message("unknownRun"));
        }

        return Serialize(new SubmissionPayload(result.Status, result.Reasons, result.ReasonsTotal, result.AttemptsRemaining),
                         ConcurrencyHunterJsonContext.Default.SubmissionPayload);
    }

    [McpServerTool(Name = "render_report"),
     Description("Renders and atomically stores the report bundle for a run.")]
    public static string render_report(RunRegistry registry,
                                       [Description("Run identifier returned by run_start.")] string run_id)
    {
        var result = registry.Render(run_id);
        if (result.Error is not null)
            return RegistryError(result.Error, result.Message);

        return Serialize(new RenderPayload(result.Status!.Value.ToString(),
                                           result.Reasons,
                                           result.ReasonsTotal,
                                           new RenderCounts(result.Findings,
                                                            result.Groups,
                                                            result.NarrativesAccepted,
                                                            result.LateResponses),
                                           result.DurationSeconds,
                                           result.BundlePath!,
                                           result.ReportPath!),
            ConcurrencyHunterJsonContext.Default.RenderPayload);
    }

    private static string RegistryError(string error, string? message = null) => error == "runNotFound"
                                                                                     ? Error("unknownRun", Message("unknownRun"))
                                                                                     : Error(error, message ?? Message(error));

    private static string Error(string code, string message, string? runId = null) =>
        Serialize(new ErrorPayload(code, ResponseBudget.Fit(message, 512), runId),
            ConcurrencyHunterJsonContext.Default.ErrorPayload);

    private static string Message(string code) => code switch
    {
        "unknownRun" => "The run id was not found.",
        "targetPathTooLong" => "The target path exceeds the response budget.",
        "runActive" => "A run is already active.",
        "invalidPage" => "Page must be at least 1.",
        "deadlineExceeded" => "The run deadline has passed.",
        "notReady" => "The run is not ready for this operation.",
        "runFailed" => "The run failed.",
        "runRendered" => "The run has already been rendered.",
        "invalidLevel" => "Level must be high, medium, or low.",
        "renderFailed" => "The report could not be rendered.",
        _ => "The operation could not be completed."
    };

    private static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        if (Encoding.UTF8.GetByteCount(json) <= ResponseEnvelope.MaximumSerializedBytes)
            return json;
        return JsonSerializer.Serialize(new ErrorPayload("responseTooLarge"), ConcurrencyHunterJsonContext.Default.ErrorPayload);
    }
}
