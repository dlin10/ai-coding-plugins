namespace ConcurrencyHunter.Roots;

public enum RootDiscoveryDiagnosticCode
{
    UnsupportedAssemblyVersion,
    UnsupportedPattern,
    UnresolvedBinding,
    DiscoveryFailed
}

public sealed record RootDiscoveryDiagnostic(string ProviderId, RootDiscoveryDiagnosticCode Code, string AffectedScope,
                                             string Reason, IReadOnlyList<string> EvidenceIds);

public enum RootDiscoveryStatus
{
    Checked,
    NotChecked
}

/// <summary>What one provider found in one scope. A scope the provider did not check is never an empty
/// success: a <see cref="RootDiscoveryStatus.NotChecked"/> result must say why in a diagnostic.</summary>
public sealed record RootDiscoveryResult
{
    public RootDiscoveryResult(RootDiscoveryStatus status, IReadOnlyList<ExecutionRootDescriptor> roots,
                               IReadOnlyList<RootDiscoveryDiagnostic> diagnostics)
    {
        if (status == RootDiscoveryStatus.NotChecked && diagnostics.Count == 0)
            throw new ArgumentException("A not-checked discovery result needs at least one diagnostic.", nameof(diagnostics));

        Status = status;
        Roots = roots;
        Diagnostics = diagnostics;
    }

    public RootDiscoveryStatus Status { get; }
    public IReadOnlyList<ExecutionRootDescriptor> Roots { get; }
    public IReadOnlyList<RootDiscoveryDiagnostic> Diagnostics { get; }
}
