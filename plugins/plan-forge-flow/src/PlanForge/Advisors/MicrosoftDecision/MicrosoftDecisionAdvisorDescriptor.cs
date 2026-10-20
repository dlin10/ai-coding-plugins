using System.Text.RegularExpressions;

namespace PlanForge.Advisors.MicrosoftDecision;

/// <summary>Discovers Microsoft-Decision-1 only through Plan Forge's deliberate endpoint, credential and deployment configuration.</summary>
internal sealed class MicrosoftDecisionAdvisorDescriptor : AdvisorDescriptor
{
    /// <summary>The resource endpoint or gateway base that receives every request.</summary>
    private const string ENDPOINT_VARIABLE = "PLANFORGE_MSDECISION_ENDPOINT";

    /// <summary>The credential variable, never a tool argument or a reported value.</summary>
    private const string KEY_VARIABLE = "PLANFORGE_MSDECISION_API_KEY";

    /// <summary>The deployment name, which is the requested model and has no default.</summary>
    private const string DEPLOYMENT_VARIABLE = "PLANFORGE_MSDECISION_DEPLOYMENT";

    /// <summary>The stable Advisor identifier.</summary>
    public override string Id => "microsoft-decision";

    /// <summary>The only environment variables that configure Microsoft-Decision-1, all of them required.</summary>
    public override IReadOnlyList<AdvisorVariable> Variables { get; } =
        [new(ENDPOINT_VARIABLE, Required: true, Secret: false), new(KEY_VARIABLE, Required: true, Secret: true),
         new(DEPLOYMENT_VARIABLE, Required: true, Secret: false)];

    /// <summary>Reads the deployment name, which discovery has already found set and valid.</summary>
    /// <param name="environment">The server process environment lookup.</param>
    public override string Model(Func<string, string?> environment) => environment(DEPLOYMENT_VARIABLE)!;

    /// <summary>Creates the Advisor without sending a request; the credential stays inside the server.</summary>
    /// <param name="environment">The server process environment lookup, read at construction time.</param>
    public override IAdvisor Create(Func<string, string?> environment) =>
        new MicrosoftDecisionAdvisor(environment(ENDPOINT_VARIABLE)!, environment(KEY_VARIABLE)!, Model(environment));

    /// <summary>Accepts any set credential, a plain https endpoint and a deployment name safe to log.</summary>
    /// <param name="variable">The variable being checked.</param>
    /// <param name="value">The non-blank value, never included in a diagnostic.</param>
    protected override bool IsValid(string variable, string value) => variable switch
    {
        ENDPOINT_VARIABLE => MicrosoftDecisionAdvisor.RequestUri(value) is not null,
        DEPLOYMENT_VARIABLE => Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant),
        _ => true
    };
}
