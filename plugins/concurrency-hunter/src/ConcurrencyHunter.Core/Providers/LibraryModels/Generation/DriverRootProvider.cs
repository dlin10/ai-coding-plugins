using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The model generator's roots: the actions of a synthesized driver the fate run roots — <c>V_Setup</c>, <c>V_Call</c> and
/// <c>V_Enum</c> — run once, with no receiver. The triggers <c>T&lt;i&gt;</c> stay in the driver's source and are roots only of a
/// holder's confirmation run. The generator registers it in a registry of its own; no ASP.NET Core assembly or stub is involved.</summary>
/// <param name="triggers">Whether the triggers are roots too, as in a holder's confirmation run.</param>
public sealed class DriverRootProvider(bool triggers = false) : IExecutionRootProvider
{
    public const string PROVIDER_ID = "model-driver";

    private const string ROOT_KIND = "model-driver-action";

    private static readonly SupportedAssemblyVersion Driver = new(DriverSynthesizer.ASSEMBLY, new Version(0, 0, 0, 0), new Version(int.MaxValue, 0, 0, 0));

    public string ProviderId => PROVIDER_ID;

    /// <summary>The stable root id of a driver action, <c>model-driver:&lt;assembly&gt;:&lt;action documentation id&gt;</c>.</summary>
    /// <param name="action">The action's name: <c>V_Setup</c>, <c>V_Call</c> or <c>V_Enum</c>.</param>
    public static string RootId(string action) => $"{PROVIDER_ID}:{DriverSynthesizer.ASSEMBLY}:M:{DriverSynthesizer.DRIVER_TYPE}.{action}";

    public IReadOnlyList<SupportedAssemblyVersion> SupportedAssemblyVersions => [Driver];

    public RootDiscoveryResult Discover(RootDiscoveryContext context)
    {
        var roots = new List<ExecutionRootDescriptor>();
        foreach (var compilation in context.Compilations.Where(compilation => compilation.AssemblyName == DriverSynthesizer.ASSEMBLY))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (compilation.Assembly.GetTypeByMetadataName(DriverSynthesizer.DRIVER_TYPE) is not { } driver)
                continue;
            foreach (var action in driver.GetMembers().OfType<IMethodSymbol>().Where(IsAction))
            {
                var symbol = SymbolNames.Method(action);
                roots.Add(new ExecutionRootDescriptor(
                    RootId(action.Name),
                    ROOT_KIND,
                    ProviderId,
                    new RootEntry(IrLowering.RootBodyId(action), symbol, $"driver action {symbol}", ProviderSupport.Source(action, context.RootDirectory)),
                    new InstanceBindings(ReceiverKind.None, []),
                    new InvocationPolicy(Multiplicity.AtMostOnce, SelfOverlap.Serialized, context.ScopeId),
                    [],
                    [],
                    [],
                    []));
            }
        }

        return new RootDiscoveryResult(RootDiscoveryStatus.Checked, roots, []);
    }

    /// <summary>Whether a method of the driver class is one of the actions the run roots: <c>V_Setup</c>, <c>V_Call</c> and
    /// <c>V_Enum</c>, and the triggers <c>T&lt;i&gt;</c> when they are roots too.</summary>
    /// <param name="method">The method.</param>
    private bool IsAction(IMethodSymbol method) =>
        method is { IsStatic: true, MethodKind: MethodKind.Ordinary, Parameters.Length: 0 } &&
        (method.Name is DriverSynthesizer.SETUP or DriverSynthesizer.CALL or DriverSynthesizer.ENUMERATE ||
         triggers && method.Name is ['T', _, ..] && method.Name[1..].All(char.IsAsciiDigit));
}
