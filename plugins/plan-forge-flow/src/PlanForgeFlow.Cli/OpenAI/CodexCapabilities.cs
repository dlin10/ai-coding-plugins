using PlanForgeFlow.Cli;
using PlanForgeFlow.Serialization;

namespace PlanForgeFlow.OpenAI;

internal static class CodexCapabilities
{
    public static CodexReadinessData Probe(Func<CodexExecutableResolution>? resolver = null,
                                           Func<CodexLaunch, AppServerClient>? clientFactory = null)
    {
        CodexExecutableResolution resolution;
        try { resolution = (resolver ?? CodexExecutableResolver.Resolve)(); }
        catch (Exception error)
        {
            return new CodexReadinessData("unusable", null, null, "process", error.Message, []);
        }
        if (resolution.Status == "absent") return new CodexReadinessData("absent", null, null, null, null, []);
        if (resolution.Launch is null)
        {
            return new CodexReadinessData("unusable", resolution.FoundExecutable, resolution.FoundLaunchKind,
                                          "process", resolution.Error, []);
        }

        try
        {
            using var client = (clientFactory ?? AppServerClient.Start)(resolution.Launch);
            client.Initialize();
            client.ValidateOpenAiAccount();
            return new CodexReadinessData("ready", resolution.Launch.Executable, resolution.Launch.LaunchKind, null, null,
                                          client.ListModels().ToList());
        }
        catch (CliFailure error)
        {
            return new CodexReadinessData("unusable", resolution.Launch.Executable, resolution.Launch.LaunchKind,
                                          error.Code, error.Message, []);
        }
        catch (Exception error)
        {
            return new CodexReadinessData("unusable", resolution.Launch.Executable, resolution.Launch.LaunchKind,
                                          "process", error.Message, []);
        }
    }
}
