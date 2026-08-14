using System.Text.RegularExpressions;
using PlanForgeFlow.Cli;

namespace PlanForgeFlow.Claude;

internal sealed record AnthropicSelection(string RequestedAlias, string? AgentModel, string Effort);

internal sealed record AnthropicModelEvidence(string RequestedAlias,
                                               string ResolvedModel,
                                               IReadOnlyList<string> ModelsUsed,
                                               string Effort);

internal static class AnthropicModels
{
    private const int MaxModelLength = 256;
    private const int MaxModelsUsed = 64;
    private static readonly string[] SupportedAliases = ["sonnet", "opus", "haiku", "fable", "inherit"];
    private static readonly string[] SupportedEfforts = ["none", "low", "medium", "high", "xhigh", "max"];
    private static readonly string[] ConflictingEnvironmentVariables = ["CLAUDE_CODE_EFFORT_LEVEL", "CLAUDE_CODE_SUBAGENT_MODEL"];

    private static readonly Regex ModelIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$", RegexOptions.CultureInvariant);

    public static AnthropicSelection Select(string requestedAlias, string effort)
    {
        if (!SupportedAliases.Contains(requestedAlias, StringComparer.Ordinal))
        {
            throw new CliFailure("usage", "Anthropic model alias must be exactly sonnet, opus, haiku, fable, or inherit");
        }
        if (!SupportedEfforts.Contains(effort, StringComparer.Ordinal))
        {
            throw new CliFailure("usage", "Anthropic effort must be exactly none, low, medium, high, xhigh, or max");
        }

        var allowed = requestedAlias switch
        {
            "haiku" => effort == "none",
            "sonnet" or "opus" or "fable" => effort != "none",
            "inherit" => true,
            _ => false,
        };
        if (!allowed) throw new CliFailure("usage", $"Anthropic model alias {requestedAlias} does not support effort {effort}");

        return new AnthropicSelection(requestedAlias, requestedAlias == "inherit" ? null : requestedAlias, effort);
    }

    public static AnthropicModelEvidence NormalizeEvidence(AnthropicSelection selection,
                                                            string? resolvedModel,
                                                            IReadOnlyList<string>? modelsUsed)
    {
        if (!IsModelId(resolvedModel)) throw new CliFailure("state", "Anthropic agent evidence requires resolvedModel", 3);
        var normalizedModels = modelsUsed is null ? new[] { resolvedModel! } : modelsUsed.ToArray();
        if (normalizedModels.Length is 0 or > MaxModelsUsed || normalizedModels.Any(model => !IsModelId(model)))
        {
            throw new CliFailure("state", "Anthropic agent evidence contains invalid modelsUsed", 3);
        }

        if (selection.RequestedAlias == "inherit")
        {
            if (normalizedModels.Any(model => !string.Equals(model, resolvedModel, StringComparison.Ordinal)))
            {
                throw new CliFailure("state", "Anthropic inherit selection changed resolved model during the agent run", 3);
            }
        }
        else if (!BelongsToFamily(resolvedModel!, selection.RequestedAlias) ||
                 normalizedModels.Any(model => !BelongsToFamily(model, selection.RequestedAlias)))
        {
            throw new CliFailure("state", $"Anthropic {selection.RequestedAlias} selection left its requested model family", 3);
        }

        return new AnthropicModelEvidence(selection.RequestedAlias, resolvedModel!, normalizedModels, selection.Effort);
    }

    public static void ValidateDoctorEnvironment(Func<string, string?> readEnvironment, bool requireClaudeMarker = true)
    {
        var inClaude = string.Equals(readEnvironment("CLAUDECODE"), "1", StringComparison.Ordinal) ||
                       string.Equals(readEnvironment("CLAUDE_CODE_REMOTE"), "true", StringComparison.OrdinalIgnoreCase);
        if (requireClaudeMarker && !inClaude) return;

        var conflicts = ConflictingEnvironmentVariables.Where(name => !string.IsNullOrWhiteSpace(readEnvironment(name))).ToArray();
        if (conflicts.Length > 0)
        {
            throw new CliFailure("environment", $"unset conflicting Claude model override environment variables before Forge: {string.Join(", ", conflicts)}");
        }
    }

    private static bool IsModelId(string? model)
        => model is { Length: > 0 and <= MaxModelLength } && ModelIdPattern.IsMatch(model);

    private static bool BelongsToFamily(string model, string family)
        => Regex.IsMatch(model, $"(?:^|[^a-z0-9]){Regex.Escape(family)}(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
