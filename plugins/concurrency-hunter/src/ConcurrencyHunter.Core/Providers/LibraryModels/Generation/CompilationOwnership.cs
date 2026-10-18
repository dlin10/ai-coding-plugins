namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>A cache entry of <see cref="LibraryCompilation"/> that a scope recorded: the key it was added under and the instance, so that a
/// release removes the entry only while the key still maps to this instance.</summary>
/// <param name="Key">The cache key.</param>
/// <param name="Entry">The entry the caller added.</param>
internal readonly record struct OwnedCompilation(string Key, Lazy<Task<LibraryCompilationResult>> Entry);

/// <summary>The one owner of which library compilations a run created (R12). A run opens the scope around each of its generator calls;
/// <see cref="LibraryCompilation.Compile(ImplementationAssembly, IReadOnlySet{string}, GenerationChecks, LibraryCompilationProbe?)"/> records
/// each entry it added to the cache into the scope open on its flow, at the moment it adds it, so that a generation that throws or is
/// cancelled after compiling has handed its entry over already. The scope follows the flow of execution (<see cref="AsyncLocal{T}"/>), not a
/// thread: work the generator starts inside the scope belongs to it, and a caller of another flow never does. Without a scope open nothing
/// is recorded, which is what <c>generate</c> and the model evals do.</summary>
internal sealed class CompilationOwnership
{
    private static readonly AsyncLocal<CompilationOwnership?> Current = new();

    private readonly List<OwnedCompilation> _created = [];

    /// <summary>The entries recorded into this ownership so far, in the order they were added.</summary>
    internal IReadOnlyList<OwnedCompilation> Created
    {
        get
        {
            lock (_created)
                return [.. _created];
        }
    }

    /// <summary>Makes this ownership the one entries are recorded into on the current flow, until the returned scope is disposed, which
    /// restores the ownership that was open before.</summary>
    internal IDisposable Open()
    {
        var previous = Current.Value;
        Current.Value = this;
        return new Scope(previous);
    }

    /// <summary>Records an entry the caller added to the cache into the ownership open on the current flow, if there is one.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="entry">The entry the caller added.</param>
    internal static void Record(string key, Lazy<Task<LibraryCompilationResult>> entry)
    {
        if (Current.Value is not { } owner)
            return;
        lock (owner._created)
            owner._created.Add(new OwnedCompilation(key, entry));
    }

    /// <summary>The scope of one opened ownership: it gives the flow back the ownership it had.</summary>
    /// <param name="previous">The ownership open on the flow before, or <c>null</c>.</param>
    private sealed class Scope(CompilationOwnership? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
