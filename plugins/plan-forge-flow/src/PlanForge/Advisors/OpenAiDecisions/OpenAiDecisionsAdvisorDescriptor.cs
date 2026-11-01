using System.Text.RegularExpressions;

namespace PlanForge.Advisors.OpenAiDecisions;

/// <summary>Discovers OpenAI Decisions only through Plan Forge's deliberate credential and optional model configuration.</summary>
internal sealed class OpenAiDecisionsAdvisorDescriptor : AdvisorDescriptor
{
    /// <summary>The credential variable, never a tool argument or a reported value.</summary>
    private const string KEY_VARIABLE = "PLANFORGE_OPENAI_API_KEY";

    /// <summary>The optional model variable.</summary>
    private const string MODEL_VARIABLE = "PLANFORGE_OPENAI_MODEL";

    /// <summary>The model used when the model variable is unset.</summary>
    private const string DEFAULT_MODEL = "gpt-6-luna";

    /// <summary>The stable Advisor identifier.</summary>
    public override string Id => "openai-decisions";

    /// <summary>The only environment variables that configure OpenAI Decisions.</summary>
    public override IReadOnlyList<AdvisorVariable> Variables { get; } =
        [new(KEY_VARIABLE, Required: true, Secret: true), new(MODEL_VARIABLE, Required: false, Secret: false)];

    /// <summary>Reads the requested model without retaining it between calls.</summary>
    /// <param name="environment">The server process environment lookup.</param>
    public override string Model(Func<string, string?> environment) =>
        environment(MODEL_VARIABLE) is { } model && !string.IsNullOrWhiteSpace(model) ? model : DEFAULT_MODEL;

    /// <summary>Creates the Advisor without sending a request; the credential stays inside the server.</summary>
    /// <param name="environment">The server process environment lookup, read at construction time.</param>
    public override IAdvisor Create(Func<string, string?> environment) =>
        new OpenAiDecisionsAdvisor(environment(KEY_VARIABLE)!, Model(environment));

    /// <summary>Accepts any set credential and only a model name safe to report and log.</summary>
    /// <param name="variable">The variable being checked.</param>
    /// <param name="value">The non-blank value, never included in a diagnostic.</param>
    protected override bool IsValid(string variable, string value) =>
        variable != MODEL_VARIABLE || Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);
}
