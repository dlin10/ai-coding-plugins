using Microsoft.CodeAnalysis;

namespace CacheDetective.Indexing.EntryPoints;

/// <summary>The long-running background pair, and the reason it is a finder rather than two more recognizer
/// rows: <c>BackgroundService</c> implements <c>IHostedService</c>, so a table that ran both rows would find
/// <c>ExecuteAsync</c> <em>and</em> <c>StartAsync</c> on one type and record two entry points where the
/// branch records one. The exclusivity is the whole content of this finder. The <c>IHostedService</c> side
/// delegates rather than repeating the interface-implementation lookup.</summary>
internal sealed class HostedServiceEntryPointFinder(IReadOnlyList<EntryPointRecognizer> hostedServices) : IEntryPointFinder
{
    private readonly RecognizerEntryPointFinder _hostedServices = new(hostedServices);

    public void Find(INamedTypeSymbol type, EntryPointContext context, ICollection<EntryPoint> entryPoints)
    {
        if (TypeShapes.DerivesFrom(type, "BackgroundService", 0))
        {
            EntryPointSymbols.AddMethods(type, "ExecuteAsync", "background_service", entryPoints);
        }
        else
        {
            _hostedServices.Find(type, context, entryPoints);
        }
    }
}
