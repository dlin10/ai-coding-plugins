using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.Di;

public enum DiLifetime
{
    Singleton,
    Scoped,
    Transient
}

/// <summary>How a registration produces its object: the container constructs the implementation type, calls a factory, or
/// hands out an instance built elsewhere.</summary>
public enum DiRegistrationForm
{
    Type,
    Factory,
    Instance
}

/// <summary>One registration call. <see cref="ServiceType"/> is null when the service type is a <c>Type</c> value
/// the analysis cannot determine; such a registration may override any binding of its scope. The type keys
/// (<see cref="DiIndex.TypeKey"/>) identify the types; the type names are for display. <see cref="Assembly"/>,
/// <see cref="ProjectPath"/>, the source position, <see cref="BodyId"/> and <see cref="OperationId"/> order the scope's
/// registrations; <see cref="BodyId"/> is the lowered body holding the call and <see cref="OperationId"/> the call in it, both
/// null when the holding member could not be lowered. <see cref="Number"/> is the 1-based position among the scope's
/// registrations with the same service type, implementation and lifetime, set by <see cref="DiIndex"/>.</summary>
public sealed record DiRegistration(string? ServiceType, string? ImplementationType, DiLifetime Lifetime, bool IsHostedService,
                                    string Method, SourceSpan Source, string? UnsupportedReason, string? ServiceTypeKey,
                                    string? ImplementationTypeKey, DiRegistrationForm Form = DiRegistrationForm.Type)
{
    public bool IsSupported => UnsupportedReason is null;
    public string Assembly { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public string? BodyId { get; init; }
    public int? OperationId { get; init; }
    public int Number { get; init; } = 1;
}

public enum DiResolutionKind
{
    Unregistered,
    Unsupported,
    Ambiguous,
    Bound
}

/// <summary>A service bound to the last of its agreeing registrations. <see cref="RegionId"/> is that registration's region
/// identity (<see cref="DiIndex.RegionId"/>) and <see cref="RegionDisplay"/> its display.</summary>
public sealed record DiBinding(string ServiceType, string ImplementationType, DiLifetime Lifetime, string RegionId,
                               IReadOnlyList<SourceSpan> Sources, IReadOnlyList<string> Uncertainties, string ServiceTypeKey,
                               string ImplementationTypeKey, string RegionDisplay);

public sealed record DiResolution(DiResolutionKind Kind, string ServiceType, DiBinding? Binding,
                                  IReadOnlyList<DiRegistration> Registrations);

public enum HostedServiceInstanceCount
{
    One,
    Unknown
}

public sealed record HostedServiceRegistration(string ImplementationType, HostedServiceInstanceCount InstanceCount,
                                               IReadOnlyList<DiRegistration> Registrations, string ImplementationTypeKey);

public sealed record DiDiagnostic(RootDiscoveryDiagnosticCode Code, string Subject, string Message, SourceSpan? Source);

public sealed class DiIndex
{
    public const string HOSTED_SERVICE_TYPE = "Microsoft.Extensions.Hosting.IHostedService";
    public const string HOSTED_SERVICE_METHOD = "AddHostedService";
    public const string HOSTED_SERVICE_KEY = "Microsoft.Extensions.Hosting.Abstractions:" + HOSTED_SERVICE_TYPE;

    public DiIndex(string scopeId, IReadOnlyList<DiRegistration> registrations, IReadOnlyList<DiDiagnostic> diagnostics)
    {
        ScopeId = scopeId;
        registrations = Numbered(registrations);
        Registrations = registrations;
        var hosted = HostedServicesOf(registrations).ToArray();
        HostedServices = hosted.Select(item => item.Registration).ToArray();
        Diagnostics = diagnostics.Concat(hosted.Select(item => item.Diagnostic).OfType<DiDiagnostic>())
                                 .Concat(UnsupportedDiagnostics(registrations))
                                 .Concat(AmbiguousDiagnostics(registrations))
                                 .ToArray();
        UnresolvedRegistrations = registrations.Where(registration => registration.ServiceType is null).ToArray();
    }

    public static DiIndex Empty(string scopeId) => new(scopeId, [], []);

    public string ScopeId { get; }
    public IReadOnlyList<DiRegistration> Registrations { get; }
    public IReadOnlyList<HostedServiceRegistration> HostedServices { get; }
    public IReadOnlyList<DiDiagnostic> Diagnostics { get; }

    /// <summary>Registrations whose service type is unknown; while any exist every bound answer is uncertain.</summary>
    public IReadOnlyList<DiRegistration> UnresolvedRegistrations { get; }

    /// <summary>A registration's region identity: its service type key, implementation type key, lifetime and number.</summary>
    public static string RegionId(string serviceTypeKey, string implementationTypeKey, DiLifetime lifetime, int number) =>
        $"di|{serviceTypeKey}|{implementationTypeKey}@{lifetime}#{number}";

    /// <summary>A registration's region display: <c>di:&lt;Type&gt;@&lt;Lifetime&gt;</c>, with <c>#&lt;n&gt;</c> from the second
    /// identical registration on.</summary>
    public static string RegionDisplay(string implementationType, DiLifetime lifetime, int number) =>
        number < 2 ? $"di:{implementationType}@{lifetime}" : $"di:{implementationType}@{lifetime}#{number}";

    /// <summary>A type's identity within a scope: its display name qualified by the assembly that declares it, so
    /// same-named types of two assemblies never share a registration, a binding or a key.</summary>
    public static string TypeKey(string assembly, string type) => $"{assembly}:{type}";

    /// <summary>Resolves a service by its type key (<see cref="TypeKey"/>); the resolution's service type is that key. Agreeing
    /// registrations bind the last of them, as the container does; <see cref="DiResolution.Registrations"/> lists every
    /// registration of the service in the total order.</summary>
    public DiResolution Resolve(string serviceTypeKey)
    {
        var registrations = Registrations.Where(registration => registration.ServiceTypeKey == serviceTypeKey).ToArray();
        if (registrations.Length == 0)
            return new DiResolution(DiResolutionKind.Unregistered, serviceTypeKey, null, []);
        if (registrations.Any(registration => !registration.IsSupported))
            return new DiResolution(DiResolutionKind.Unsupported, serviceTypeKey, null, registrations);

        var targets = registrations.Select(registration => (registration.ImplementationTypeKey!, registration.Lifetime)).Distinct().ToArray();
        if (targets.Length != 1)
            return new DiResolution(DiResolutionKind.Ambiguous, serviceTypeKey, null, registrations);

        var (implementationKey, lifetime) = targets[0];
        var last = registrations[^1];
        var uncertainties = UnresolvedRegistrations
            .Select(unresolved => $"An unresolved registration at {unresolved.Source.Path}:{unresolved.Source.StartLine} may change this binding.")
            .ToArray();
        var binding = new DiBinding(last.ServiceType!, last.ImplementationType!, lifetime,
                                    RegionId(serviceTypeKey, implementationKey, lifetime, last.Number),
                                    registrations.Select(registration => registration.Source).ToArray(), uncertainties, serviceTypeKey,
                                    implementationKey, RegionDisplay(last.ImplementationType!, lifetime, last.Number));
        return new DiResolution(DiResolutionKind.Bound, serviceTypeKey, binding, registrations);
    }

    /// <summary>The registrations in the scope's total order (assembly, project path, document path, line, column, body id,
    /// operation id), each numbered among those with the same service type, implementation and lifetime.</summary>
    private static DiRegistration[] Numbered(IReadOnlyList<DiRegistration> registrations)
    {
        var counts = new Dictionary<(string?, string?, DiLifetime), int>();
        return registrations.OrderBy(registration => registration.Assembly, StringComparer.Ordinal)
                            .ThenBy(registration => registration.ProjectPath, StringComparer.Ordinal)
                            .ThenBy(registration => registration.Source.Path, StringComparer.Ordinal)
                            .ThenBy(registration => registration.Source.StartLine)
                            .ThenBy(registration => registration.Source.StartColumn)
                            .ThenBy(registration => registration.BodyId, StringComparer.Ordinal)
                            .ThenBy(registration => registration.OperationId)
                            .Select(registration =>
                            {
                                var key = (registration.ServiceTypeKey, registration.ImplementationTypeKey, registration.Lifetime);
                                var number = counts.GetValueOrDefault(key) + 1;
                                counts[key] = number;
                                return registration with { Number = number };
                            })
                            .ToArray();
    }

    private static IEnumerable<DiDiagnostic> UnsupportedDiagnostics(IReadOnlyList<DiRegistration> registrations) =>
        registrations.Where(registration => !registration.IsSupported)
                     .Select(registration =>
                     {
                         var subject = registration.ServiceType ?? registration.ImplementationType ?? "unknown service type";
                         return new DiDiagnostic(
                             RootDiscoveryDiagnosticCode.UnsupportedPattern,
                             subject,
                             $"{registration.Method} registration of {subject} at {Location(registration.Source)} is not supported " +
                             $"({registration.UnsupportedReason}); it binds nothing.",
                             registration.Source);
                     });

    /// <summary>One diagnostic per service type whose supported registrations disagree, as <see cref="Resolve"/> answers
    /// <see cref="DiResolutionKind.Ambiguous"/>. Hosted services are many registrations of one service type by design.</summary>
    private static IEnumerable<DiDiagnostic> AmbiguousDiagnostics(IReadOnlyList<DiRegistration> registrations) =>
        registrations.Where(registration => registration.ServiceTypeKey is not null && !registration.IsHostedService)
                     .GroupBy(registration => registration.ServiceTypeKey!, StringComparer.Ordinal)
                     .Where(group => group.All(registration => registration.IsSupported) &&
                                     group.Select(registration => (registration.ImplementationTypeKey, registration.Lifetime)).Distinct().Count() > 1)
                     .OrderBy(group => group.Key, StringComparer.Ordinal)
                     .Select(group => new DiDiagnostic(
                         RootDiscoveryDiagnosticCode.UnresolvedBinding,
                         group.First().ServiceType!,
                         $"{group.First().ServiceType} is registered with conflicting implementations or lifetimes (" +
                         string.Join(", ", group.Select(registration =>
                             $"{registration.ImplementationType}@{registration.Lifetime} at {Location(registration.Source)}")) +
                         "); it binds nothing.",
                         group.First().Source));

    private static string Location(SourceSpan source) => $"{source.Path}:{source.StartLine}";

    private static IEnumerable<(HostedServiceRegistration Registration, DiDiagnostic? Diagnostic)> HostedServicesOf(
        IReadOnlyList<DiRegistration> registrations)
    {
        var hosted = registrations.Where(registration => registration.IsHostedService && registration.IsSupported)
                                  .GroupBy(registration => registration.ImplementationTypeKey!, StringComparer.Ordinal)
                                  .OrderBy(group => group.First().ImplementationType, StringComparer.Ordinal)
                                  .ThenBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in hosted)
        {
            var implementation = group.First().ImplementationType!;
            var singletons = group.Count(registration => registration.Method != HOSTED_SERVICE_METHOD);
            var addHostedService = group.Count(registration => registration.Method == HOSTED_SERVICE_METHOD);
            if (singletons > 1 || singletons == 1 && addHostedService > 0)
            {
                var first = group.First();
                var diagnostic = new DiDiagnostic(
                    RootDiscoveryDiagnosticCode.UnresolvedBinding,
                    implementation,
                    $"Hosted service {implementation} is registered {singletons} time(s) as a singleton IHostedService and " +
                    $"{addHostedService} time(s) with AddHostedService; how many instances run depends on registration order.",
                    first.Source);
                yield return (new HostedServiceRegistration(implementation, HostedServiceInstanceCount.Unknown, group.ToArray(), group.Key), diagnostic);
            }
            else
            {
                yield return (new HostedServiceRegistration(implementation, HostedServiceInstanceCount.One, group.ToArray(), group.Key), null);
            }
        }
    }
}
