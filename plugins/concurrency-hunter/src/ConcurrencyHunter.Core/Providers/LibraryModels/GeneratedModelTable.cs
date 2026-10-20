using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels;

/// <summary>The generated layer of one scope (R2), as lowering meets it with no file access: per compilation, the map from a declaring type
/// to the key of the file its own target names, and the entries of those files that apply in that compilation. A call is described only
/// by an entry of the file its own target names, so two projects that reference two assets of one assembly each use their own file,
/// and two projects that share a file each use what applies in their own compilation. An entry belongs to a member only when its text is,
/// ordinally, the declaration id the generator writes for that member (<c>GetDocumentationCommentId()</c> of its original definition);
/// no other spelling of an id reaches a member. The run first maps the types of its targets (<see cref="MapType"/>), then adds the files it
/// read (<see cref="AddFile"/>).</summary>
internal sealed class GeneratedModelTable
{
    private readonly Dictionary<Compilation, CompilationView> _views = new();

    /// <summary>Records that, in a compilation, the members of a type are answered by the file of a key: the key of the target
    /// <see cref="Generation.GenerationTargets.Of"/> gave for a member of the type.</summary>
    /// <param name="compilation">The compilation the target was resolved in.</param>
    /// <param name="declaringType">The declaring type of the target's member.</param>
    /// <param name="key">The key of the file that answers for the type.</param>
    internal void MapType(Compilation compilation, INamedTypeSymbol declaringType, GeneratedFileKey key)
    {
        if (!_views.TryGetValue(compilation, out var view))
            _views.Add(compilation, view = new CompilationView());
        view.Files[declaringType.OriginalDefinition] = key;
    }

    /// <summary>Adds the entries of a file the run read to every compilation that maps a type to its key: a refusal as it stands, a model
    /// when <see cref="GeneratedModelFiles.Applies"/> says it applies in that compilation, otherwise as a model that does not apply. An
    /// entry whose text is not, ordinally, the declaration id of a method of a type that compilation maps to this file — one that is
    /// no generation target, or whose spelling is another one that Roslyn would resolve to such a method — describes no call there,
    /// stays in the file untouched and says nothing.</summary>
    /// <param name="file">The file as the run read it.</param>
    /// <returns>One diagnostic, naming the compilation, for each model that does not apply; in ordinal order.</returns>
    internal IReadOnlyList<string> AddFile(GeneratedModelFile file)
    {
        var diagnostics = new List<string>();
        foreach (var (compilation, view) in _views)
        {
            WorkCancellation.Check();
            if (!view.Files.Values.Contains(file.Key))
                continue;
            var members = view.MembersOf(file.Key);
            foreach (var entry in file.Entries.Values)
            {
                WorkCancellation.Check();
                if (members.TryGetValue(entry.Member, out var definition))
                    Place(compilation, view, file.Key, entry, definition, diagnostics);
            }
        }

        return diagnostics.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>What the file of a call's own target says about the call's member in the call's compilation.</summary>
    /// <param name="compilation">The compilation the call is in.</param>
    /// <param name="definition">The call's member: its original definition.</param>
    /// <param name="lookupId">The member's declaration id as <see cref="LibraryModels.Find"/> looks a call up.</param>
    /// <returns>The stored answer, <see cref="StoredGeneratedAnswer.Absent"/> when the file has none; <c>null</c> when the compilation
    /// maps the member's declaring type to no file, so the member is no target of the scope.</returns>
    internal StoredGeneratedAnswer? Lookup(Compilation compilation, IMethodSymbol definition, string lookupId)
    {
        if (!_views.TryGetValue(compilation, out var view) || view.KeyOf(definition) is not { } key)
            return null;
        return view.Answers.TryGetValue((key, lookupId), out var placed) && SymbolEqualityComparer.Default.Equals(placed.Definition, definition)
            ? placed.Answer
            : StoredGeneratedAnswer.Absent;
    }

    private static void Place(Compilation compilation, CompilationView view, GeneratedFileKey key, GeneratedModelEntry entry, IMethodSymbol definition,
                              List<string> diagnostics)
    {
        if (!GenerationTargets.NamesOnly(definition, entry.Member, compilation) ||
            DocumentationCommentId.CreateDeclarationId(definition) is not { } lookupId)
            return;

        StoredGeneratedAnswer answer;
        if (entry.Refusal is not null)
        {
            answer = new StoredGeneratedAnswer(StoredGeneratedKind.Refusal, Refusal: entry.Refusal);
        }
        else if (GeneratedModelFiles.Applies(entry, definition, compilation, out var diagnostic))
        {
            answer = new StoredGeneratedAnswer(StoredGeneratedKind.Model, ModelOf(entry.Model!, definition, lookupId));
        }
        else
        {
            diagnostics.Add(diagnostic!);
            answer = new StoredGeneratedAnswer(StoredGeneratedKind.ModelNotApplicable);
        }

        // The file's entries and the run's own answers are added one after the other and may name one member twice: a model that applies
        // is never replaced by an entry that does not.
        if (!view.Answers.TryGetValue((key, lookupId), out var existing) || existing.Answer.Kind != StoredGeneratedKind.Model)
            view.Answers[(key, lookupId)] = new PlacedAnswer(definition, answer);
    }

    /// <summary>The model of an entry that applies, as a library model of the generated layer: the entry's own words, under the id a
    /// call is looked up by.</summary>
    /// <param name="model">The entry's model in the project entry form.</param>
    /// <param name="definition">The member it describes.</param>
    /// <param name="lookupId">The declaration id a call is looked up by.</param>
    private static LibraryModel ModelOf(ProjectModelEntry model, IMethodSymbol definition, string lookupId) =>
        new(lookupId, [ProjectModelResolver.Range(model, definition.ContainingAssembly)], model.Effects, ModelLayer.Generated, false,
            definition.ContainingAssembly.Identity.Version)
        {
            Result = model.Result,
            Fates = model.Fates,
            Stores = model.Stores,
            Outputs = model.Outputs,
            Keeps = model.Keeps,
            Calls = model.Calls
        };

    /// <summary>A stored answer with the member it was stored for: a call meets the answer of its own member only.</summary>
    /// <param name="Definition">The member.</param>
    /// <param name="Answer">The answer.</param>
    private sealed record PlacedAnswer(IMethodSymbol Definition, StoredGeneratedAnswer Answer);

    /// <summary>What one compilation of the scope sees: the file of each of its mapped types and the answers of those files.</summary>
    private sealed class CompilationView
    {
        internal Dictionary<INamedTypeSymbol, GeneratedFileKey> Files { get; } = new(SymbolEqualityComparer.Default);

        internal Dictionary<(GeneratedFileKey Key, string LookupId), PlacedAnswer> Answers { get; } = new();

        /// <summary>The key of the file the member's declaring type is mapped to, or <c>null</c> when it is mapped to none.</summary>
        /// <param name="definition">The member.</param>
        internal GeneratedFileKey? KeyOf(IMethodSymbol definition) =>
            definition.ContainingType is { } type && Files.TryGetValue(type.OriginalDefinition, out var key) ? key : null;

        /// <summary>The methods of the types mapped to a key, each by the declaration id the generator writes for it, compared ordinally.
        /// A method of another assembly than the file's, and one whose signature carries a function-pointer type — no declaration id names
        /// it — are left out.</summary>
        /// <param name="key">The file's key.</param>
        internal Dictionary<string, IMethodSymbol> MembersOf(GeneratedFileKey key)
        {
            var members = new Dictionary<string, IMethodSymbol>(StringComparer.Ordinal);
            foreach (var (type, _) in Files.Where(mapped => mapped.Value == key && mapped.Key.ContainingAssembly.Name == key.AssemblyName))
            {
                WorkCancellation.Check();
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                {
                    WorkCancellation.Check();
                    if (!FunctionPointers.In(method) && method.GetDocumentationCommentId() is { } id)
                        members.TryAdd(id, method);
                }
            }

            return members;
        }
    }
}
