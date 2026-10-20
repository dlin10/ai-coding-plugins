using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The comparison witnesses a member's driver needs, keyed by the member's type parameter or the parameter of a generic
/// static type the driver constructs. A probe implements <c>IComparable&lt;T&gt;</c> or <c>IEquatable&lt;T&gt;</c> exactly when its type
/// is a recorded closed binding of the corresponding <c>Default</c> getter's or initializer's factory mark. The reachable set's
/// per-mark record decides this, whether the heap instance is merged, unmerged or absent because the closure bound stopped the pass.
/// A binding past the context count gets no witness and is served by the ordinary factory call.
/// The places of the rule: <c>ModelGenerator.Classify</c> adds the recorded needs after every pass (<see cref="Add"/>) and synthesizes
/// again while needs or loaded statics grow; <c>DriverSynthesizer.Synthesize</c> takes the record, and <c>ProbeDeclarations</c>, from
/// <c>ChooseTypeArguments</c> and <c>TryConstructStaticType</c> alike, asks <see cref="Interfaces"/> for every probe. <c>ClassDeclaration</c>
/// emits the interfaces' members as read witnesses, which <c>ReadsByConvention</c>, <c>GenerationHandoffs</c> and <c>ValueProvenance</c>
/// already read as comparison members. A probe with no recorded need keeps its other interfaces unchanged.</summary>
/// <remarks>CoreLib's automatic <c>Default</c> property is read as storage without a getter call; its initializer holds the factory mark.
/// A getter with a body can hold the mark itself. Both are read from the same per-mark bindings as the heap's comparer selection.</remarks>
public sealed class ComparisonWitnesses
{
    private const string ORDERING = ":M:System.Collections.Generic.Comparer`1.";
    private const string EQUALITY = ":M:System.Collections.Generic.EqualityComparer`1.";
    private static readonly string[] Members = ["get_Default", "#cctor"];

    private readonly Dictionary<ITypeParameterSymbol, (bool Ordering, bool Equality)> _needs = new(SymbolEqualityComparer.Default);

    /// <summary>How many needs the record holds, counting each interface of each type parameter.</summary>
    public int Count => _needs.Values.Sum(need => (need.Ordering ? 1 : 0) + (need.Equality ? 1 : 0));

    /// <summary>Adds exactly the closed bindings recorded for a Default getter's or initializer's factory mark, through
    /// <see cref="DefaultComparers.Bindings"/> as the heap's allocation does. This record is the same with a merged instance, an
    /// unmerged one or a pass stopped before the heap. A binding past the context limit has no record and gets no witness;
    /// the ordinary factory call serves it.</summary>
    /// <param name="run">The pass's run.</param>
    /// <param name="driver">The pass's driver.</param>
    /// <returns>Whether the record grew.</returns>
    public bool Add(ScopeRun run, Driver driver)
    {
        var before = Count;
        foreach (var (site, _) in run.Reachable.DefaultComparerBindings)
        {
            WorkCancellation.Check();
            var ordering = Members.Any(member => site.BodyId.EndsWith(ORDERING + member, StringComparison.Ordinal));
            if (!ordering && !Members.Any(member => site.BodyId.EndsWith(EQUALITY + member, StringComparison.Ordinal)))
                continue;
            var call = run.Reachable.Bodies[site.BodyId].Blocks.SelectMany(block => block.Operations)
                          .OfType<IrCallOperation>().Single(operation => operation.Id == site.Operation);
            foreach (var argument in DefaultComparers.Bindings(run.Program, run.Reachable, site.BodyId, site.Operation, call.DefaultComparer!.TypeKey))
            {
                WorkCancellation.Check();
                Need(driver, argument, ordering);
            }
        }
        return Count != before;
    }

    /// <summary>Records the need of every type parameter whose probe a type argument is.</summary>
    /// <param name="driver">The pass's driver.</param>
    /// <param name="argument">The type argument, as a type key.</param>
    /// <param name="ordering">Whether the comparer is <c>Comparer&lt;X&gt;</c>'s rather than <c>EqualityComparer&lt;X&gt;</c>'s.</param>
    private void Need(Driver driver, string argument, bool ordering)
    {
        foreach (var (parameter, _) in driver.ProbeTypes.Where(pair => argument == $"{DriverSynthesizer.ASSEMBLY}:{pair.Value}"))
        {
            WorkCancellation.Check();
            var need = _needs.GetValueOrDefault(parameter);
            _needs[parameter] = ordering ? need with { Ordering = true } : need with { Equality = true };
        }
    }

    /// <summary>The comparison interfaces a probe for <paramref name="parameter"/> implements: <c>IComparable&lt;T&gt;</c> and
    /// <c>IEquatable&lt;T&gt;</c> over the type parameter itself, which the driver writes as the probe.</summary>
    /// <param name="library">The library compilation, which names the interfaces.</param>
    /// <param name="parameter">The type parameter the probe stands for.</param>
    public IReadOnlyList<INamedTypeSymbol> Interfaces(Compilation library, ITypeParameterSymbol parameter)
    {
        if (!_needs.TryGetValue(parameter, out var need))
            return [];
        var interfaces = new List<INamedTypeSymbol>();
        if (need.Ordering && library.GetTypeByMetadataName("System.IComparable`1") is { } comparable)
            interfaces.Add(comparable.Construct(parameter));
        if (need.Equality && library.GetTypeByMetadataName("System.IEquatable`1") is { } equatable)
            interfaces.Add(equatable.Construct(parameter));
        return interfaces;
    }
}
