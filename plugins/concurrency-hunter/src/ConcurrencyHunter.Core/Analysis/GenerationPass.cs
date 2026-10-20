using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Analysis;

/// <summary>What a run does with the generated model layer (R9) and what it does it with: the mode and, for a test, the install that target
/// resolution reads and the generator call. Production passes neither.</summary>
/// <param name="Mode">What the analysis does with the generated layer.</param>
/// <param name="Install">The install implementations are resolved from, or <c>null</c> for this process's.</param>
/// <param name="Generator">The generator call, or <c>null</c> for the model generator over <paramref name="Install"/>.</param>
/// <param name="BeforeFileRead">A test hook before a writer re-reads a generated file under its lock.</param>
internal sealed record GenerationSetup(ModelGeneration Mode, ImplementationAssemblies? Install = null,
                                       Func<GenerationRequest, CancellationToken, GeneratedAnswer>? Generator = null, Action? BeforeFileRead = null);

/// <summary>A run's dealings with the generated layer, scope by scope (R1, R2, R4, R5): which library members a scope's direct opaque calls
/// reach and which of them are generation targets, the generated layer a scope's second analysis reads, and what each target stands for
/// in the scope's coverage. The target of a call is looked up once per compilation and member for the whole run. Disposing the pass at the
/// end of the run releases the library compilations the run's own generations created, however the run ended (R12).</summary>
internal sealed class GenerationPass : IDisposable
{
    private const string BODY_PREFIX = "body:";

    private readonly ImplementationAssemblies _install;
    private readonly RunGeneration _generation;
    private readonly CancellationToken _cancellationToken;
    private readonly Action? _beforeFileRead;
    private readonly Dictionary<(Compilation Compilation, string TargetMethodId), (IMethodSymbol Member, IdentifiedGenerationTarget Target)?> _targets = new();
    private int _reported;

    private GenerationPass(GenerationSetup setup, string repositoryRoot, CancellationToken cancellationToken)
    {
        Mode = setup.Mode;
        _install = setup.Install ?? ImplementationAssemblies.ForThisProcess();
        _generation = new RunGeneration(repositoryRoot, cancellationToken, RunGeneration.RUN_LIMIT, RunGeneration.MEMBER_LIMIT, TimeProvider.System,
                                        setup.Generator ?? ((request, token) => ModelGenerator.Generate(request, _install, token)));
        _cancellationToken = cancellationToken;
        _beforeFileRead = setup.BeforeFileRead;
    }

    /// <summary>What the analysis does with the generated layer: never <see cref="ModelGeneration.None"/>, for which there is no pass.</summary>
    internal ModelGeneration Mode { get; }

    /// <summary>The time spent inside generations so far.</summary>
    internal TimeSpan Spent => _generation.Spent;

    /// <summary>The pass of a run, or <c>null</c> when the generated layer is off.</summary>
    /// <param name="setup">What the run does with the generated layer.</param>
    /// <param name="repositoryRoot">The repository root whose generated files the run reads and writes.</param>
    /// <param name="cancellationToken">The run's token.</param>
    internal static GenerationPass? Create(GenerationSetup setup, string repositoryRoot, CancellationToken cancellationToken) =>
        setup.Mode == ModelGeneration.None ? null : new GenerationPass(setup, repositoryRoot, cancellationToken);

    /// <summary>The library members a scope's direct opaque calls reach and that are generation targets (R1): every opaque call of a
    /// reachable summary that is not known, mapped through the call operation of its body to the target it names, and resolved in the
    /// compilation the body belongs to. A call has a target only when its declaration id names exactly one method of that assembly whose own
    /// id it is, and the member carries no function-pointer type: Roslyn writes nothing for a function-pointer type, so <c>M:P.F()</c> is
    /// both the id of <c>F(delegate*&lt;void&gt;)</c> and what finds a parameterless <c>F()</c>. A member is a candidate once per compilation,
    /// handed a delegate when a delegate is created as the argument of any of its reached calls; this is the one place that flag is read.</summary>
    /// <param name="run">The scope's analysis without the generated layer.</param>
    /// <param name="compilations">The scope's compilations.</param>
    /// <exception cref="OperationCanceledException">The run's token fired.</exception>
    internal IReadOnlyList<GenerationCandidate> Candidates(ScopeRun run, IReadOnlyList<Compilation> compilations)
    {
        var found = new Dictionary<(Compilation Compilation, string TargetMethodId), GenerationCandidate>();
        foreach (var group in run.Heap!.Instances.Values.GroupBy(instance => instance.BodyId, StringComparer.Ordinal))
        {
            WorkCancellation.Check();
            _cancellationToken.ThrowIfCancellationRequested();
            var summary = group.First().Summary;
            var opaque = summary.OpaqueCalls.Where(call => !call.IsKnown).ToArray();
            if (opaque.Length == 0 || !run.Reachable.Bodies.TryGetValue(summary.BodyId, out var body) || CompilationOf(summary.BodyId, compilations) is not { } compilation)
                continue;

            var operations = new Dictionary<int, IrCallOperation>();
            foreach (var operation in body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>())
            {
                WorkCancellation.Check();
                operations.TryAdd(operation.Id, operation);
            }
            foreach (var call in opaque)
            {
                WorkCancellation.Check();
                if (!operations.TryGetValue(call.OperationId, out var operationOfCall) || operationOfCall.TargetMethodId is not { } targetId ||
                    TargetOf(compilation, targetId) is not { } resolved)
                    continue;

                var handed = call.Delegates.Count != 0;
                var key = (compilation, targetId);
                found[key] = found.TryGetValue(key, out var earlier)
                    ? earlier with { HandedDelegate = earlier.HandedDelegate || handed }
                    : new GenerationCandidate(compilation, resolved.Member, resolved.Target, handed);
            }
        }

        return found.Values.ToArray();
    }

    /// <summary>Generates what the scope's candidates call for, within the run's budget, when the run generates, writes what a file keeps,
    /// and builds the generated layer the scope's second analysis reads.</summary>
    /// <param name="models">The scope's models without the generated layer.</param>
    /// <param name="candidates">The scope's candidates, from <see cref="Candidates"/>.</param>
    /// <exception cref="OperationCanceledException">The run's token fired.</exception>
    internal ScopeLayer Prepare(LibraryModels models, IReadOnlyList<GenerationCandidate> candidates)
    {
        if (Mode == ModelGeneration.Generate)
        {
            _generation.Generate(models, candidates);
            _generation.EndScope(_beforeFileRead);
        }

        return _generation.Layer(models, candidates);
    }

    /// <summary>The targets a run that generates would have generated for the scope's candidates, when this run only reads the generated
    /// files (<see cref="ModelGeneration.ReadOnly"/>); none for a run that generates, which generated them.</summary>
    /// <param name="models">The scope's models without the generated layer.</param>
    /// <param name="candidates">The scope's candidates, from <see cref="Candidates"/>.</param>
    /// <exception cref="OperationCanceledException">The run's token fired.</exception>
    internal IReadOnlyList<WantedGeneration> Wanted(LibraryModels models, IReadOnlyList<GenerationCandidate> candidates) =>
        Mode == ModelGeneration.ReadOnly
            ? [.. _generation.WouldGenerate(models, candidates).Select(target => new WantedGeneration(target.MemberId, target.Key.Text))]
            : [];

    /// <summary>Releases the library compilations the run's own generations created from the cache (R12). A run that read only, or had the
    /// generated layer off, created none.</summary>
    public void Dispose() => _generation.Release();

    /// <summary>The diagnostics of the generated files the run met since the last call, in the order they arose: an unreadable file, an invalid
    /// entry, a write that failed.</summary>
    internal IReadOnlyList<string> TakeDiagnostics()
    {
        var fresh = _generation.Diagnostics.Skip(_reported).ToArray();
        _reported = _generation.Diagnostics.Count;
        return fresh;
    }

    /// <summary>The candidates as coverage lists them: each with what the scope knows of its target in its own compilation. A member a project
    /// or a built-in entry describes (a project entry may declare it opaque) is no member that misses a generated model, and is left out.</summary>
    /// <param name="models">The scope's models without the generated layer.</param>
    /// <param name="layer">The scope's generated layer.</param>
    /// <param name="candidates">The candidates.</param>
    /// <param name="throughModel">Whether only the analysis with the generated layer reaches them.</param>
    internal IReadOnlyList<ReachedTarget> Reached(LibraryModels models, ScopeLayer layer, IEnumerable<GenerationCandidate> candidates, bool throughModel)
    {
        var reached = new List<ReachedTarget>();
        foreach (var candidate in candidates)
        {
            WorkCancellation.Check();
            if (RunGeneration.Decide(models, layer.Table, candidate).Layer is DescribingLayer.Project or DescribingLayer.BuiltIn)
                continue;
            var definition = (candidate.Member.ReducedFrom ?? candidate.Member).OriginalDefinition;
            var stored = layer.Table.Lookup(candidate.Compilation, definition, candidate.Target.LookupId) ?? StoredGeneratedAnswer.Absent;
            reached.Add(new ReachedTarget(definition, candidate.Target,
                                          new TargetStanding(stored, candidate.Target is UnavailableGenerationTarget, _generation.AnswerOf(candidate.Target),
                                                             stored.Kind == StoredGeneratedKind.Model, throughModel)));
        }

        return reached;
    }

    /// <summary>The scope's generation targets whose model this run generated, in this scope or an earlier one, each once however many calls
    /// reach it.</summary>
    /// <param name="candidates">The scope's candidates.</param>
    internal int GeneratedInRun(IEnumerable<GenerationCandidate> candidates) =>
        candidates.Select(candidate => candidate.Target)
                  .DistinctBy(target => (target.MemberId, target.Key))
                  .Count(target => _generation.AnswerOf(target) is { Refusal: null });

    /// <summary>The target of a call, looked up once per compilation and target id.</summary>
    /// <param name="compilation">The compilation the call is in.</param>
    /// <param name="targetId">The call's <c>TargetMethodId</c>: <c>body:&lt;assembly name&gt;:&lt;declaration id&gt;</c>.</param>
    private (IMethodSymbol Member, IdentifiedGenerationTarget Target)? TargetOf(Compilation compilation, string targetId)
    {
        if (_targets.TryGetValue((compilation, targetId), out var known))
            return known;
        var resolved = Resolve(compilation, targetId);
        _targets[(compilation, targetId)] = resolved;
        return resolved;
    }

    private (IMethodSymbol Member, IdentifiedGenerationTarget Target)? Resolve(Compilation compilation, string targetId)
    {
        if (Split(targetId) is not { } parts)
            return null;
        var (assemblyName, id) = parts;
        IMethodSymbol[] named;
        try
        {
            named = DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation)
                                          .OfType<IMethodSymbol>()
                                          .Select(symbol => (symbol.ReducedFrom ?? symbol).OriginalDefinition)
                                          .Where(symbol => symbol.ContainingAssembly.Name == assemblyName &&
                                                           string.Equals(symbol.GetDocumentationCommentId(), id, StringComparison.Ordinal))
                                          .Take(2)
                                          .ToArray();
        }
        catch (ArgumentException)
        {
            return null;
        }

        return named is [{ } only] && !FunctionPointers.In(only) &&
               GenerationTargets.Of(only, compilation, _install, _cancellationToken) is IdentifiedGenerationTarget target
            ? (only, target)
            : null;
    }

    /// <summary>The compilation a body belongs to: the first of the scope's compilations whose assembly has the name the body id carries.</summary>
    /// <param name="bodyId">The body id, <c>body:&lt;assembly name&gt;:&lt;declaration id&gt;</c> with any nested-body suffix.</param>
    /// <param name="compilations">The scope's compilations.</param>
    private static Compilation? CompilationOf(string bodyId, IReadOnlyList<Compilation> compilations) =>
        Split(bodyId) is { } parts
            ? compilations.FirstOrDefault(compilation => string.Equals(compilation.AssemblyName, parts.AssemblyName, StringComparison.Ordinal))
            : null;

    /// <summary>The assembly name and the declaration id of a body id, <c>body:&lt;assembly name&gt;:&lt;declaration id&gt;</c>, or
    /// <c>null</c> for an id of another shape.</summary>
    /// <param name="bodyId">The body id.</param>
    private static (string AssemblyName, string Id)? Split(string bodyId)
    {
        var separator = bodyId.StartsWith(BODY_PREFIX, StringComparison.Ordinal) ? bodyId.IndexOf(':', BODY_PREFIX.Length) : -1;
        return separator > BODY_PREFIX.Length ? (bodyId[BODY_PREFIX.Length..separator], bodyId[(separator + 1)..]) : null;
    }
}
