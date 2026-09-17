using ConcurrencyHunter.Di;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers;

/// <summary>Hosted-service roots: each lifecycle method the host invokes on a registered hosted-service
/// implementation, taken as the most derived declaration the runtime dispatches to.</summary>
public sealed class HostingRootProvider : IExecutionRootProvider
{
    public const string PROVIDER_ID = "hosting";

    private const string BACKGROUND_SERVICE = "Microsoft.Extensions.Hosting.BackgroundService";
    private const string HOSTED_SERVICE = "Microsoft.Extensions.Hosting.IHostedService";
    private const string LIFECYCLE_SERVICE = "Microsoft.Extensions.Hosting.IHostedLifecycleService";

    private static readonly SupportedAssemblyVersion Hosting =
        SupportedAssemblyVersion.Framework("Microsoft.Extensions.Hosting.Abstractions");

    private static readonly (string Interface, string Method, string Kind)[] InterfaceLifecycle =
    [
        (HOSTED_SERVICE, "StartAsync", "start"),
        (HOSTED_SERVICE, "StopAsync", "stop"),
        (LIFECYCLE_SERVICE, "StartingAsync", "starting"),
        (LIFECYCLE_SERVICE, "StartedAsync", "started"),
        (LIFECYCLE_SERVICE, "StoppingAsync", "stopping"),
        (LIFECYCLE_SERVICE, "StoppedAsync", "stopped")
    ];

    public string ProviderId => PROVIDER_ID;

    public IReadOnlyList<SupportedAssemblyVersion> SupportedAssemblyVersions => [Hosting];

    public RootDiscoveryResult Discover(RootDiscoveryContext context)
    {
        var (diagnostics, skipped) = ProviderSupport.VersionDiagnostics(context, ProviderId, SupportedAssemblyVersions);
        if (ProviderSupport.Scanned(context, SupportedAssemblyVersions, skipped) is null)
            return new RootDiscoveryResult(RootDiscoveryStatus.NotChecked, [], diagnostics);

        var roots = new List<ExecutionRootDescriptor>();
        // Every compilation's types, so a hosted service declared in a skipped compilation is recognised and left out without a diagnostic.
        var sourceTypes = context.Compilations
                                 .SelectMany(compilation => SourceTypes(compilation.Assembly.GlobalNamespace))
                                 .GroupBy(SymbolNames.TypeKey, StringComparer.Ordinal)
                                 .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var hosted in context.DiIndex.HostedServices)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var implementation = hosted.ImplementationType;
            if (sourceTypes.TryGetValue(hosted.ImplementationTypeKey, out var declared) &&
                ProviderSupport.CompilationOf(context, declared) is { } declaring && skipped.Contains(declaring))
            {
                continue;
            }

            if (implementation.Contains('<', StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic(RootDiscoveryDiagnosticCode.UnsupportedPattern, implementation, $"generic hosted service {implementation}"));
                continue;
            }

            foreach (var diagnostic in context.DiIndex.Diagnostics.Where(diagnostic => diagnostic.Subject == implementation))
                diagnostics.Add(Diagnostic(diagnostic.Code, implementation, diagnostic.Message));

            var typeRoots = sourceTypes.TryGetValue(hosted.ImplementationTypeKey, out var type) && ProviderSupport.CompilationOf(context, type) is { } compilation
                ? Roots(context, compilation, type, hosted).ToArray()
                : [];
            if (typeRoots.Length == 0)
            {
                diagnostics.Add(Diagnostic(RootDiscoveryDiagnosticCode.UnsupportedPattern, implementation,
                                           $"hosted service {implementation} has no lifecycle body in source"));
            }

            roots.AddRange(typeRoots);
        }

        return new RootDiscoveryResult(RootDiscoveryStatus.Checked, roots, diagnostics);
    }

    private IEnumerable<ExecutionRootDescriptor> Roots(RootDiscoveryContext context, Compilation compilation, INamedTypeSymbol type,
                                                       HostedServiceRegistration hosted)
    {
        var lifecycle = new List<(IMethodSymbol Method, string Kind)>();
        var backgroundService = ExactSymbols.FindType(compilation, Hosting, BACKGROUND_SERVICE);
        if (backgroundService is not null && ProviderSupport.DerivesFrom(type, backgroundService) &&
            backgroundService.GetMembers("ExecuteAsync").OfType<IMethodSymbol>().FirstOrDefault() is { } execute &&
            MostDerived(type, execute) is { } executeOverride)
        {
            lifecycle.Add((executeOverride, "execute"));
        }

        foreach (var (interfaceName, methodName, kind) in InterfaceLifecycle)
        {
            var @interface = ExactSymbols.FindType(compilation, Hosting, interfaceName);
            if (@interface is null || !type.AllInterfaces.Contains(@interface, SymbolEqualityComparer.Default) ||
                @interface.GetMembers(methodName).OfType<IMethodSymbol>().FirstOrDefault() is not { } member ||
                type.FindImplementationForInterfaceMember(member) is not IMethodSymbol implementation)
            {
                continue;
            }

            var dispatched = implementation.IsVirtual || implementation.IsOverride || implementation.IsAbstract
                ? MostDerived(type, implementation)
                : implementation;
            if (dispatched is not null)
                lifecycle.Add((dispatched, kind));
        }

        var isOne = hosted.InstanceCount == HostedServiceInstanceCount.One;
        foreach (var (method, kind) in lifecycle.Where(item => ProviderSupport.HasSourceBody(item.Method)))
        {
            var symbol = SymbolNames.Method(method);
            yield return new ExecutionRootDescriptor(
                $"hosting:{kind}:{type.ContainingAssembly.Name}:{type.GetDocumentationCommentId()}:{method.GetDocumentationCommentId()}",
                $"hosted-{kind}",
                ProviderId,
                new RootEntry(IrLowering.RootBodyId(method), symbol, $"{kind} of hosted service {SymbolNames.Type(type)}",
                              ProviderSupport.Source(method, context.RootDirectory)),
                new InstanceBindings(ReceiverKind.HostedService,
                    method.Parameters.Select(parameter => new ParameterBinding(
                                                 parameter.Name, SymbolNames.Type(parameter.Type), ParameterBindingKind.RequestData,
                                                 parameter.Type.IsValueType, SymbolNames.TypeKey(parameter.Type)))
                          .ToArray(),
                    SymbolNames.Type(type), SymbolNames.TypeKey(type)),
                new InvocationPolicy(isOne ? Multiplicity.AtMostOnce : Multiplicity.Unknown,
                                     isOne ? SelfOverlap.Serialized : SelfOverlap.Unknown, context.ScopeId),
                [],
                [],
                hosted.Registrations.Select((registration, index) => new DiscoveryEvidence(
                                                $"E{index + 1}", "registration", $"{registration.Method} registers {hosted.ImplementationType}",
                                                registration.Source))
                      .ToArray(),
                []);
        }
    }

    /// <summary>The declaration the runtime dispatches to for <paramref name="baseMethod"/> on <paramref name="type"/>.</summary>
    private static IMethodSymbol? MostDerived(INamedTypeSymbol type, IMethodSymbol baseMethod)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var match = current.GetMembers(baseMethod.Name).OfType<IMethodSymbol>()
                               .FirstOrDefault(candidate => ProviderSupport.Overrides(candidate, baseMethod));
            if (match is not null)
                return match;
        }

        return null;
    }

    private RootDiscoveryDiagnostic Diagnostic(RootDiscoveryDiagnosticCode code, string affectedScope, string reason) =>
        new(ProviderId, code, affectedScope, reason, []);

    private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol @namespace) =>
        @namespace.GetTypeMembers().SelectMany(NestedAndSelf)
                  .Where(type => type.TypeKind == TypeKind.Class && type.DeclaringSyntaxReferences.Length != 0)
                  .Concat(@namespace.GetNamespaceMembers().SelectMany(SourceTypes));

    private static IEnumerable<INamedTypeSymbol> NestedAndSelf(INamedTypeSymbol type) =>
        new[] { type }.Concat(type.GetTypeMembers().SelectMany(NestedAndSelf));
}
