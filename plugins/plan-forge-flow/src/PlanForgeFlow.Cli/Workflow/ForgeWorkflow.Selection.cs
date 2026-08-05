using PlanForgeFlow.Cli;

namespace PlanForgeFlow.Workflow;

internal static partial class ForgeWorkflow
{
    private static void ValidateObservedSelection(ParsedArgs parsed, string expectedModel, string expectedEffort, bool requireObservation = false)
    {
        var suppliedModel = parsed.Get("model");
        var suppliedEffort = parsed.Get("effort")?.ToLowerInvariant();
        if ((suppliedModel is null) != (suppliedEffort is null)) throw new CliFailure("usage", "--model and --effort must be supplied together");
        if (requireObservation && suppliedModel is null) throw new CliFailure("usage", "session registration requires --model and --effort");
        if (suppliedEffort == "ultra") throw new CliFailure("usage", "ultra reasoning effort is unsupported");
        if (suppliedModel is not null && (!string.Equals(suppliedModel, expectedModel, StringComparison.Ordinal) || !string.Equals(suppliedEffort, expectedEffort, StringComparison.Ordinal)))
        {
            throw new CliFailure("state", "the requested model and effort do not match the pinned selection", 3);
        }
    }
}
