using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The answer already available for a dependency, before applicability in a generation compilation is checked.</summary>
/// <param name="memberId">The dependency's declaration id.</param>
/// <param name="key">The dependency's own generated file.</param>
internal delegate GeneratedModelEntry? DependencyAnswers(string memberId, GeneratedFileKey key);

internal enum DependencyViewKind { NoAnswer, Refusal, NoImplementation, Model }

/// <summary>What a generation read from its dependency source, independently of applicability.</summary>
/// <param name="Target">The dependency's target.</param>
/// <param name="Kind">What the source answered, or why there is no answer.</param>
/// <param name="Entry">The source's model or refusal, or null when there is no answer.</param>
internal sealed record DependencyView(IdentifiedGenerationTarget Target, DependencyViewKind Kind, GeneratedModelEntry? Entry)
{
    internal string? ModelText { get; } = Entry?.Model is not null ? GeneratedModelFiles.ModelText(Entry) : null;
}

/// <summary>The generated layer of one generation, shared by its library, driver and recipe compilations.</summary>
/// <param name="resolver">Resolves the dependencies' own implementation assets.</param>
/// <param name="assemblyName">The assembly being generated, whose own members stay opaque.</param>
/// <param name="frameworkMajor">The generation request's framework major.</param>
/// <param name="answers">The run's answers for dependency members and their own file keys.</param>
internal sealed class DependencyModels(ImplementationAssemblies resolver, string assemblyName, int frameworkMajor, DependencyAnswers answers)
{
    private readonly GeneratedModelTable _table = new();
    private readonly GenerationTargets.TypeOrigins _origins = new();
    private readonly Dictionary<(Compilation Compilation, IMethodSymbol Member), StoredGeneratedAnswer?> _read = new();
    private readonly Dictionary<(string MemberId, GeneratedFileKey Key), DependencyView> _views = new();

    internal string AssemblyName => assemblyName;

    internal IReadOnlyList<DependencyView> Views => _views.Values.OrderBy(view => view.Target.MemberId, StringComparer.Ordinal)
                                                              .ThenBy(view => view.Target.Key.Text, StringComparer.Ordinal).ToArray();

    internal StoredGeneratedAnswer? Read(IMethodSymbol definition, Compilation compilation, string lookupId)
    {
        WorkCancellation.Check();
        if (_read.TryGetValue((compilation, definition), out var read))
            return read;
        var target = GenerationTargets.Of(definition, compilation, resolver, WorkCancellation.Token, frameworkMajor, _origins);
        if (target is not IdentifiedGenerationTarget identified)
        {
            _read.Add((compilation, definition), null);
            return null;
        }

        var entry = answers(identified.MemberId, identified.Key);
        _views.TryAdd((identified.MemberId, identified.Key), new DependencyView(identified,
            entry?.Model is not null ? DependencyViewKind.Model : entry?.Refusal is not null ? DependencyViewKind.Refusal :
            identified is UnavailableGenerationTarget ? DependencyViewKind.NoImplementation : DependencyViewKind.NoAnswer, entry));
        _table.MapType(compilation, definition.ContainingType, identified.Key);
        if (entry is not null)
        {
            _table.AddFile(new GeneratedModelFile(identified.Key, entry.Path, null,
                new Dictionary<string, GeneratedModelEntry>(StringComparer.Ordinal) { [identified.MemberId] = entry }, []));
        }
        var stored = _table.Lookup(compilation, definition, lookupId);
        _read.Add((compilation, definition), stored);
        return stored;
    }
}
