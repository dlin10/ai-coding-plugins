using ConcurrencyHunter.Providers.LibraryModels.Sources;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The implementation and sources obtained before generation clocks, with this caller's observed compilation outcome.</summary>
/// <param name="Resolution">The implementation resolution, including a refusal when no implementation was found.</param>
/// <param name="Sources">The sources obtained, or null when resolution was refused.</param>
internal sealed record GenerationPreparation(ImplementationResolution Resolution, LibrarySourceResult? Sources)
{
    internal LibraryCode? Code { get; set; }
    internal LibraryCode ObservedCode => Code ?? LibrarySourceDecisions.ForCompilation(Sources);
    // Only finality may assume that incomplete compilation will reject the sources.
    private LibraryCode InterruptedCode => Code ?? LibrarySourceDecisions.ForCompilation(Sources,
        sourcesRejected: Sources?.Kind == LibraryCodeKind.OriginalSources);
    internal bool RunOnly => InterruptedCode.Kind == LibraryCodeKind.DecompiledForRun;

    /// <summary>Records this caller's observed code kind in the source statistics.</summary>
    /// <param name="sources">The source provider collecting the run's statistics.</param>
    internal void RecordObservedCode(LibrarySources sources)
    {
        if (Resolution is { Reason: null, Assembly: { } assembly })
            sources.RecordCode(assembly.Path, ObservedCode);
    }
}
