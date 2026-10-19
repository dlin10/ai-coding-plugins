using System.Text.RegularExpressions;

namespace PlanForge.Advisors.Jev;

/// <summary>Discovers Jev only through Plan Forge's deliberate, pinned-model configuration.</summary>
internal sealed class JevAdvisorDescriptor : AdvisorDescriptor
{
    /// <summary>The credential variable, never a tool argument or a reported value.</summary>
    private const string KEY_VARIABLE = "PLANFORGE_JEV_API_KEY";

    /// <summary>The optional pinned-model variable.</summary>
    private const string MODEL_VARIABLE = "PLANFORGE_JEV_MODEL";

    /// <summary>The pinned model used when the model variable is unset.</summary>
    private const string DEFAULT_MODEL = "jev-1.13.0";

    /// <summary>The stable Advisor identifier.</summary>
    public override string Id => "jev";

    /// <summary>The only environment variables that configure Jev.</summary>
    public override IReadOnlyList<AdvisorVariable> Variables { get; } =
        [new(KEY_VARIABLE, Required: true, Secret: true), new(MODEL_VARIABLE, Required: false, Secret: false)];

    /// <summary>Reads the pinned model without retaining it between calls.</summary>
    /// <param name="environment">The server process environment lookup.</param>
    public override string Model(Func<string, string?> environment) =>
        environment(MODEL_VARIABLE) is { } model && !string.IsNullOrWhiteSpace(model) ? model : DEFAULT_MODEL;

    /// <summary>Creates Jev without sending a request; the credential stays inside the server.</summary>
    /// <param name="environment">The server process environment lookup, read at construction time.</param>
    public override IAdvisor Create(Func<string, string?> environment) => new JevAdvisor(environment(KEY_VARIABLE)!, Model(environment));

    /// <summary>Accepts any set credential and only a pinned Jev model version.</summary>
    /// <param name="variable">The variable being checked.</param>
    /// <param name="value">The non-blank value, never included in a diagnostic.</param>
    protected override bool IsValid(string variable, string value) =>
        variable != MODEL_VARIABLE || Regex.IsMatch(value, @"\Ajev-\d+\.\d+\.\d+\z", RegexOptions.CultureInvariant);
}
