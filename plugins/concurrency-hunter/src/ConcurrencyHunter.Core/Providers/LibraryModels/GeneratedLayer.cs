using System.Diagnostics;
using ConcurrencyHunter.Providers.LibraryModels.Generation;

namespace ConcurrencyHunter.Providers.LibraryModels;

/// <summary>The layer that describes a call, in the order of R1: a project entry over a built-in entry in range over a generated entry.</summary>
internal enum DescribingLayer
{
    /// <summary>No layer describes the call: it is an opaque call.</summary>
    None,

    /// <summary>A project entry describes the call: a model, or <c>opaque</c>, or two entries that disagree and so leave it opaque.</summary>
    Project,

    /// <summary>A built-in entry in range describes the call.</summary>
    BuiltIn,

    /// <summary>A generated model that applies in the call's compilation describes the call.</summary>
    Generated
}

/// <summary>What the generated file of a call's own target says about the call's member, as a scope reads it.</summary>
internal enum StoredGeneratedKind
{
    /// <summary>The file has no valid entry for the member.</summary>
    Absent,

    /// <summary>A valid model that applies in the call's compilation.</summary>
    Model,

    /// <summary>A valid model that applies in no call of the compilation: kept in the file and not generated again.</summary>
    ModelNotApplicable,

    /// <summary>A refusal of the generator: kept in the file and not generated again.</summary>
    Refusal
}

/// <summary>The stored answer of a generated file for one member in one compilation.</summary>
/// <param name="Kind">What the file holds for the member.</param>
/// <param name="Model">The model, for <see cref="StoredGeneratedKind.Model"/>; otherwise <c>null</c>.</param>
/// <param name="Refusal">The refusal, for <see cref="StoredGeneratedKind.Refusal"/>; otherwise <c>null</c>.</param>
internal readonly record struct StoredGeneratedAnswer(StoredGeneratedKind Kind, LibraryModel? Model = null, GeneratedRefusal? Refusal = null)
{
    /// <summary>The file has no valid entry for the member.</summary>
    internal static StoredGeneratedAnswer Absent { get; } = new(StoredGeneratedKind.Absent);
}

/// <summary>Whether a call's member is a generation target and whether its implementation is on disk (R1).</summary>
internal enum GenerationOrigin
{
    /// <summary>Not a generation target: never generated, and no generated file answers for it.</summary>
    NotATarget,

    /// <summary>A target whose implementation is not on disk: a stored answer describes it, nothing can be generated.</summary>
    ImplementationMissing,

    /// <summary>A target whose implementation is on disk.</summary>
    Available
}

/// <summary>What <see cref="GeneratedLayer.Decide"/> decided about one call.</summary>
/// <param name="Layer">The layer that describes the call.</param>
/// <param name="Generate">Whether the run generates the call's member now.</param>
internal readonly record struct GeneratedDecision(DescribingLayer Layer, bool Generate);

/// <summary>The one decider of which layer describes a library call (R1) and of whether the run generates its member now.</summary>
internal static class GeneratedLayer
{
    /// <summary>The origin of the target <see cref="GenerationTargets.Of"/> answered with.</summary>
    /// <param name="target">The answer.</param>
    internal static GenerationOrigin OriginOf(GenerationTargetAnswer target) => target switch
    {
        GenerationTarget => GenerationOrigin.Available,
        UnavailableGenerationTarget => GenerationOrigin.ImplementationMissing,
        NoGenerationTarget => GenerationOrigin.NotATarget,
        _ => throw new UnreachableException($"Unknown generation target {target}.")
    };

    /// <summary>The layer that describes a call, and whether the run generates its member now. A project match describes the call
    /// whether it is a model or <c>opaque</c>; a built-in match describes it only in range; a generated model describes it only when
    /// the member is a target and the file holds a model that applies. A member is generated only when no layer describes it, it is a
    /// target whose implementation is on disk and its file holds no answer: a refusal, and a model that does not apply, are answers.</summary>
    /// <param name="project">What the project entries say, or <c>null</c> for no entry.</param>
    /// <param name="builtIn">What the built-in entries say, or <c>null</c> for no entry.</param>
    /// <param name="stored">What the generated file of the call's own target says.</param>
    /// <param name="origin">Whether the member is a generation target.</param>
    internal static GeneratedDecision Decide(LibraryMatch? project, LibraryMatch? builtIn, StoredGeneratedAnswer stored, GenerationOrigin origin)
    {
        if (Describes(project))
            return new GeneratedDecision(DescribingLayer.Project, false);
        if (Describes(builtIn))
            return new GeneratedDecision(DescribingLayer.BuiltIn, false);
        if (origin == GenerationOrigin.NotATarget)
            return new GeneratedDecision(DescribingLayer.None, false);
        return stored.Kind switch
        {
            StoredGeneratedKind.Model => new GeneratedDecision(DescribingLayer.Generated, false),
            StoredGeneratedKind.ModelNotApplicable or StoredGeneratedKind.Refusal => new GeneratedDecision(DescribingLayer.None, false),
            StoredGeneratedKind.Absent => new GeneratedDecision(DescribingLayer.None, origin == GenerationOrigin.Available),
            _ => throw new UnreachableException($"Unknown stored answer {stored.Kind}.")
        };
    }

    /// <summary>Whether a layer's match describes the call: a match out of the layer's version range does not.</summary>
    /// <param name="match">The layer's match, or <c>null</c>.</param>
    private static bool Describes(LibraryMatch? match) => match is { Kind: LibraryMatchKind.Known or LibraryMatchKind.Opaque };
}
