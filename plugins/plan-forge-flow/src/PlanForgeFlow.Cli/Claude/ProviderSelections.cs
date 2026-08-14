using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Claude;

internal static class ProviderSelections
{
    private static readonly string[] OpenAiEfforts = ["none", "low", "medium", "high", "xhigh", "max"];

    public static ProviderSelection Parse(string provider,
                                          string requestedModel,
                                          string resolvedModel,
                                          string? modelsUsedJson,
                                          string effort)
    {
        var kind = provider switch
        {
            "anthropic" => ProviderKind.Anthropic,
            "openai" => ProviderKind.OpenAI,
            _ => throw new CliFailure("usage", "--provider must be anthropic or openai"),
        };
        IReadOnlyList<string>? modelsUsed = null;
        if (modelsUsedJson is not null)
        {
            try { modelsUsed = JsonSerializer.Deserialize(modelsUsedJson, ForgeJsonContext.Default.StringArray); }
            catch (JsonException) { throw new CliFailure("usage", "--models-used must be a JSON string array"); }
        }
        return Validate(kind, requestedModel, resolvedModel, modelsUsed, effort);
    }

    public static ProviderSelection Validate(ProviderKind provider,
                                             string requestedModel,
                                             string resolvedModel,
                                             IReadOnlyList<string>? modelsUsed,
                                             string effort)
    {
        if (provider == ProviderKind.Anthropic)
        {
            var selected = AnthropicModels.Select(requestedModel, effort);
            var evidence = AnthropicModels.NormalizeEvidence(selected, resolvedModel, modelsUsed);
            return new ProviderSelection(provider, evidence.RequestedAlias, evidence.ResolvedModel, evidence.ModelsUsed, evidence.Effort);
        }

        if (provider != ProviderKind.OpenAI || string.IsNullOrWhiteSpace(requestedModel) || requestedModel.Length > 256 ||
            string.IsNullOrWhiteSpace(resolvedModel) || resolvedModel.Length > 256 ||
            !OpenAiEfforts.Contains(effort, StringComparer.Ordinal))
            throw new CliFailure("usage", "OpenAI provider selection is invalid");
        var normalized = modelsUsed?.ToArray() ?? [resolvedModel];
        if (normalized.Length is 0 or > 64 || !string.Equals(requestedModel, resolvedModel, StringComparison.Ordinal) ||
            normalized.Any(model => !string.Equals(model, resolvedModel, StringComparison.Ordinal)))
            throw new CliFailure("state", "OpenAI provider selection contains model drift", 3);
        return new ProviderSelection(provider, requestedModel, resolvedModel, normalized, effort);
    }
}
