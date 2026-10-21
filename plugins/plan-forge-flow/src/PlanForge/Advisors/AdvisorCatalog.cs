using PlanForge.Advisors.Jev;
using PlanForge.Advisors.MicrosoftDecision;
using PlanForge.Run;

namespace PlanForge.Advisors;

/// <summary>An Advisor's environment variable, without its value.</summary>
/// <param name="Name">The process-environment name.</param>
/// <param name="Required">Whether configuration requires a non-blank value.</param>
/// <param name="Secret">Whether child processes must be denied this variable.</param>
internal sealed record AdvisorVariable(string Name, bool Required, bool Secret);

/// <summary>A configured Advisor, safe to expose to the Orchestrator.</summary>
/// <param name="Id">The Advisor identifier.</param>
/// <param name="Model">The pinned requested model.</param>
internal sealed record ConfiguredAdvisor(string Id, string Model);

/// <summary>One missing or invalid variable, never its value.</summary>
/// <param name="Variable">The variable's name.</param>
/// <param name="Problem">Either missing or invalid.</param>
internal sealed record AdvisorProblem(string Variable, string Problem);

/// <summary>An Advisor with some configuration but no usable configuration.</summary>
/// <param name="Id">The Advisor identifier.</param>
/// <param name="Problems">The missing and invalid variables.</param>
internal sealed record PartlyConfiguredAdvisor(string Id, IReadOnlyList<AdvisorProblem> Problems);

/// <summary>The Advisors configured in the server process at the time of discovery.</summary>
/// <param name="Configured">Advisors ready for selection, in catalogue order.</param>
/// <param name="PartlyConfigured">Advisors with configuration problems, in catalogue order.</param>
internal sealed record AdvisorDiscovery(IReadOnlyList<ConfiguredAdvisor> Configured,
                                        IReadOnlyList<PartlyConfiguredAdvisor> PartlyConfigured);

/// <summary>Owns one Advisor's configuration contract and construction, without caching credentials.</summary>
internal abstract class AdvisorDescriptor
{
    /// <summary>The stable Advisor identifier.</summary>
    public abstract string Id { get; }

    /// <summary>The variables belonging to this Advisor, in reporting order.</summary>
    public abstract IReadOnlyList<AdvisorVariable> Variables { get; }

    /// <summary>Reads the requested model, applying the Advisor's default when unset.</summary>
    /// <param name="environment">The server process environment lookup.</param>
    public abstract string Model(Func<string, string?> environment);

    /// <summary>Creates the Advisor from configuration already checked by the catalogue.</summary>
    /// <param name="environment">The server process environment lookup, read when the Advisor is needed.</param>
    public abstract IAdvisor Create(Func<string, string?> environment);

    /// <summary>Checks a set variable's value without returning it in a diagnostic.</summary>
    /// <param name="variable">The variable's name.</param>
    /// <param name="value">Its non-blank value.</param>
    protected abstract bool IsValid(string variable, string value);

    /// <summary>Discovers this Advisor without constructing it or retaining variable values.</summary>
    /// <param name="environment">The server process environment lookup.</param>
    public (ConfiguredAdvisor? Configured, PartlyConfiguredAdvisor? PartlyConfigured) Discover(Func<string, string?> environment)
    {
        var present = false;
        var problems = new List<AdvisorProblem>();
        foreach (var variable in Variables)
        {
            var value = environment(variable.Name);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (variable.Required) problems.Add(new(variable.Name, "missing"));
            }
            else
            {
                present = true;
                if (!IsValid(variable.Name, value)) problems.Add(new(variable.Name, "invalid"));
            }
        }

        if (!present) return (null, null);
        return problems.Count == 0 ? (new(Id, Model(environment)), null) : (null, new(Id, problems));
    }
}

/// <summary>Discovers and selects only Advisors deliberately configured for Plan Forge.</summary>
internal sealed class AdvisorCatalog
{
    /// <summary>The descriptors in stable discovery and diagnostic order.</summary>
    private readonly IReadOnlyList<AdvisorDescriptor> _descriptors;

    /// <summary>Creates a catalogue without reading the environment or contacting a service.</summary>
    /// <param name="descriptors">The supported Advisors, in reporting order.</param>
    public AdvisorCatalog(IReadOnlyList<AdvisorDescriptor> descriptors) => _descriptors = descriptors;

    /// <summary>The production catalogue; other vendors' credentials never configure it.</summary>
    public static AdvisorCatalog Default { get; } = new([new JevAdvisorDescriptor(), new MicrosoftDecisionAdvisorDescriptor()]);

    /// <summary>All secret variable names, for gate validation and child-environment filtering.</summary>
    public IReadOnlyList<string> SecretVariables =>
        [.. _descriptors.SelectMany(descriptor => descriptor.Variables).Where(variable => variable.Secret)
                       .Select(variable => variable.Name).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Reports current configuration without exposing credentials.</summary>
    /// <param name="environment">The server process environment lookup for this call.</param>
    public AdvisorDiscovery Discover(Func<string, string?> environment)
    {
        var configured = new List<ConfiguredAdvisor>();
        var partlyConfigured = new List<PartlyConfiguredAdvisor>();
        foreach (var descriptor in _descriptors)
        {
            var found = descriptor.Discover(environment);
            if (found.Configured is { } ready) configured.Add(ready);
            if (found.PartlyConfigured is { } partial) partlyConfigured.Add(partial);
        }
        return new(configured, partlyConfigured);
    }

    /// <summary>Selects one configured Advisor, refusing ambiguity and incomplete explicit selections.</summary>
    /// <param name="environment">The server process environment lookup for this call.</param>
    /// <param name="advisor">The exact Advisor id, or null to select the sole configured Advisor.</param>
    public IAdvisor Select(Func<string, string?> environment, string? advisor)
    {
        if (advisor is not null)
        {
            var descriptor = _descriptors.FirstOrDefault(candidate => candidate.Id == advisor)
                ?? throw new ArgumentRejectedException($"unknown Advisor '{advisor}'; known Advisors: {string.Join(", ", _descriptors.Select(candidate => candidate.Id))}");
            var found = descriptor.Discover(environment);
            if (found.Configured is not null) return descriptor.Create(environment);
            if (found.PartlyConfigured is { } partial)
                throw new ArgumentRejectedException($"Advisor '{advisor}' is partly configured: {string.Join(", ", partial.Problems.Select(problem => $"{problem.Variable}: {problem.Problem}"))}");
            throw new ArgumentRejectedException($"Advisor '{advisor}' is absent; required variables: {string.Join(", ", descriptor.Variables.Where(variable => variable.Required).Select(variable => variable.Name))}");
        }

        var discovered = Discover(environment);
        if (discovered.Configured.Count == 0)
            throw new ArgumentRejectedException("no Advisor is configured; the Evidence check stays manual");
        if (discovered.Configured.Count > 1)
            throw new ArgumentRejectedException($"several Advisors are configured: {string.Join(", ", discovered.Configured.Select(configured => configured.Id))}; select an Advisor");
        return _descriptors.First(descriptor => descriptor.Id == discovered.Configured[0].Id).Create(environment);
    }
}
