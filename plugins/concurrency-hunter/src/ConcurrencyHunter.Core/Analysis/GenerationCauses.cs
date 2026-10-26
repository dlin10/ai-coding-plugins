using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Analysis;

/// <summary>What a scope knows about one generation target in the compilation of one call that reaches it (R5): the answer a generated file
/// holds for it, whether its implementation is on disk, and what the run's own generation of it came to.</summary>
/// <param name="Stored">What the generated file of the call's own target says about the member in the call's compilation.</param>
/// <param name="ImplementationMissing">Whether the target's implementation is not on disk: nothing can be generated for it.</param>
/// <param name="Generated">The answer this run's generation of the target came to, or <c>null</c> when it generated nothing for it.</param>
/// <param name="GeneratedModelApplies">For a model <paramref name="Generated"/> holds: whether it applies in the call's compilation.</param>
internal readonly record struct TargetStanding(StoredGeneratedAnswer Stored, bool ImplementationMissing, GeneratedModelAnswer? Generated = null,
                                               bool GeneratedModelApplies = false);

/// <summary>A call that reaches a generation target: the member it calls, the target and what the scope knows of the target there.</summary>
/// <param name="Definition">The called member's original definition.</param>
/// <param name="Target">The member's generation target.</param>
/// <param name="Standing">What the scope knows of the target in the call's compilation.</param>
internal readonly record struct ReachedTarget(IMethodSymbol Definition, IdentifiedGenerationTarget Target, TargetStanding Standing);

/// <summary>The one owner of R5's causes: why no generated model describes a call of a generation target, by mode, and the list a scope's
/// coverage keeps of the targets it does so for.</summary>
internal static class GenerationCauses
{
    /// <summary>The cause of a target a read-only run found no stored answer for: a <see cref="ModelGeneration.Generate"/> run would have
    /// generated it.</summary>
    internal const string NOT_GENERATED = "not-generated";

    /// <summary>The cause of a target whose valid model passes the checks of a project entry in some compilation of the scope and not in
    /// another, or in none.</summary>
    internal const string NOT_APPLICABLE = "not-applicable";

    /// <summary>Why no generated model describes a call, or <c>null</c> when one does. A stored answer decides first: a model that applies
    /// describes the call, one that does not is <see cref="NOT_APPLICABLE"/>, a refusal is its reason word. Without a stored answer a
    /// target whose implementation is not on disk is <c>no-implementation</c>; otherwise a <see cref="ModelGeneration.Generate"/> run's own
    /// answer decides (a model, or the word of its refusal: <c>timeout</c>, <c>budget</c>, <c>failed</c> or the generator's), then
    /// <see cref="NOT_GENERATED"/> for a read-only run or <c>budget</c> for a generating run with no answer.
    /// The generated layer is off under <see cref="ModelGeneration.None"/>, which has no cause.</summary>
    /// <param name="mode">What the analysis does with the generated layer.</param>
    /// <param name="standing">What the scope knows of the target in the call's compilation.</param>
    internal static string? Of(ModelGeneration mode, TargetStanding standing)
    {
        if (mode == ModelGeneration.None)
            return null;
        var stored = standing.Stored;
        switch (stored.Kind)
        {
            case StoredGeneratedKind.Model:
                return null;
            case StoredGeneratedKind.ModelNotApplicable:
                return NOT_APPLICABLE;
            case StoredGeneratedKind.Refusal:
                return stored.Refusal!.Word;
        }

        if (standing.ImplementationMissing)
            return GenerationReasons.NO_IMPLEMENTATION;
        if (mode == ModelGeneration.Generate && standing.Generated is { } generated)
        {
            return generated.Refusal is { } refusal
                ? refusal.Word
                : standing.GeneratedModelApplies ? null : NOT_APPLICABLE;
        }

        return mode == ModelGeneration.ReadOnly ? NOT_GENERATED : GenerationOutcome.BUDGET;
    }

    /// <summary>The generation targets of a scope that no generated model describes, once each: a target at least one of whose calls no
    /// generated model describes, however many calls reach it and in whatever order. A target is a declaration id with its file key, so
    /// one member through two file keys is two targets, each with the answer of its own file; a target that is not described in some
    /// compilations and is in others is one item, and so are the unavailable targets of one package version, whose key is the package's
    /// id and version alone. The items are in ordinal order of declaration id and then of file key.</summary>
    /// <param name="mode">What the analysis does with the generated layer.</param>
    /// <param name="reached">Every call of the scope that reaches a generation target.</param>
    internal static IReadOnlyList<GenerationRefusal> Items(ModelGeneration mode, IEnumerable<ReachedTarget> reached)
    {
        if (mode == ModelGeneration.None)
            return [];
        return reached.GroupBy(call => (call.Target.MemberId, call.Target.Key))
                      .Select(group => (group.Key.MemberId, FileKey: group.Key.Key.Text,
                                        Cause: group.Select(call => Of(mode, call.Standing)).OfType<string>().Order(StringComparer.Ordinal).FirstOrDefault(),
                                        Member: group.Select(call => call.Definition.ToDisplayString()).Order(StringComparer.Ordinal).First()))
                      .Where(item => item.Cause is not null)
                      .OrderBy(item => item.MemberId, StringComparer.Ordinal)
                      .ThenBy(item => item.FileKey, StringComparer.Ordinal)
                      .Select(item => new GenerationRefusal(item.Member, item.MemberId, item.FileKey, item.Cause!))
                      .ToArray();
    }
}
