using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The truth against which a generated library-model answer is compared.</summary>
/// <param name="Model">The true entry, or null when the truth has no entry.</param>
/// <param name="ModelReason">The true model reason, or null for an opaque truth.</param>
internal sealed record EntryTruth(LibraryModel? Model, string? ModelReason)
{
    internal static EntryTruth Opaque { get; } = new(null, null);

    internal static EntryTruth Entry(LibraryModel model) => new(model, null);

    internal static EntryTruth NoEntry(string modelReason) => new(null, modelReason);
}

/// <summary>Whether a generated answer narrows the truth unsafely and whether it is exact.</summary>
/// <param name="IsUnsafeNarrowing">Whether the generated answer claims less than the truth.</param>
/// <param name="IsExact">Whether the generated answer equals the truth canonically.</param>
/// <param name="Detail">The first unsafe difference, or an exactness summary.</param>
internal readonly record struct EntryComparison(bool IsUnsafeNarrowing, bool IsExact, string Detail);

/// <summary>The comparison used by generated-entry tests: exactness uses the vocabulary's canonical equality, while safety checks
/// that every effect and value in the truth is still present and that every delegate fate is at least as wide.</summary>
internal static class EntryComparator
{
    internal static EntryComparison Compare(GeneratedAnswer generated, EntryTruth truth, bool requireSameModelReason = false)
    {
        if (truth.Model is null)
        {
            if (generated.Model is not null)
                return new EntryComparison(true, false, "the truth has no entry but generation produced one");

            var noEntryExact = !requireSameModelReason || generated.ModelReason == truth.ModelReason;
            return new EntryComparison(false, noEntryExact, noEntryExact ? "both answers have no entry" :
                                       $"expected model reason {truth.ModelReason ?? "opaque"}, got {generated.ModelReason ?? generated.Reason ?? "none"}");
        }

        if (generated.Model is not { } actual)
            return new EntryComparison(false, false, $"generation has no entry: {generated.ModelReason ?? generated.Reason ?? "no reason"}");

        var expected = truth.Model;
        var unsafeDifference = UnsafeDifference(actual, expected);
        var exact = unsafeDifference is null && SameEffects(actual.Effects, expected.Effects) &&
                    LibraryVocabulary.Alike(actual.Result, actual.Fates, expected.Result, expected.Fates,
                                            actual.Stores, expected.Stores, actual.Outputs, expected.Outputs,
                                            actual.Keeps, expected.Keeps);
        return new EntryComparison(unsafeDifference is not null, exact,
                                   unsafeDifference ?? (exact ? "entries are canonically equal" : "the generated entry is wider"));
    }

    private static string? UnsafeDifference(LibraryModel actual, LibraryModel expected)
    {
        var missingEffect = expected.Effects.FirstOrDefault(effect => !actual.Effects.Contains(effect));
        if (missingEffect is not null)
            return $"missing effect {missingEffect.Kind} on {missingEffect.Parameter}";

        if (MissingValues(actual.Keeps, expected.Keeps) is { } missingKeep)
            return "missing kept value " + missingKeep;
        if (MissingValues(actual.Stores, expected.Stores) is { } missingStore)
            return "missing stored value " + missingStore;

        if (expected.Result is { } expectedResult)
        {
            if (actual.Result is null)
                return "missing result";
            if (!Covers(actual.Result, expectedResult))
                return $"result {actual.Result} does not cover {expectedResult}";
        }

        foreach (var (parameter, expectedOutput) in expected.Outputs)
        {
            if (!actual.Outputs.TryGetValue(parameter, out var actualOutput))
                return $"missing output {parameter}";
            if (!Covers(actualOutput, expectedOutput))
                return $"output {parameter} {actualOutput} does not cover {expectedOutput}";
        }

        foreach (var expectedFate in expected.Fates)
        {
            var actualFate = actual.Fates.FirstOrDefault(fate => fate.Parameter == expectedFate.Parameter);
            if (actualFate is null)
            {
                return $"missing fate {expectedFate.Parameter}";
            }

            if (!FateIsSafe(actualFate, expectedFate))
                return $"fate {expectedFate.Parameter} is narrower: {actualFate.Kind} {actualFate.Holder}";
            var count = expectedFate.Inputs?.Count ?? 0;
            for (var index = 0; index < count; index++)
            {
                var actualInputs = actualFate.Inputs is { } inputs && index < inputs.Count ? inputs[index] : [];
                var expectedInputs = expectedFate.Inputs![index];
                if (!Covers(actualInputs, expectedInputs))
                    return $"fate {expectedFate.Parameter} input {index} lacks a value";
            }
        }

        return null;
    }

    private static bool FateIsSafe(LibraryFate actual, LibraryFate expected) =>
        actual.Kind == LibraryFateKind.UnknownExecution ||
        actual.Kind == expected.Kind && actual.Holder == expected.Holder ||
        actual is { Kind: LibraryFateKind.Holder, Holder: LibraryHolderKind.Result } && expected.Kind == LibraryFateKind.Iterator;

    private static string? MissingValues(IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> actual,
                                         IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> expected)
    {
        foreach (var (place, expectedValues) in expected)
        {
            if (!actual.TryGetValue(place, out var actualValues))
                return $"at {place}";
            if (!Covers(actualValues, expectedValues))
                return $"at {place}";
        }

        return null;
    }

    private static bool Covers(LibraryResult actual, LibraryResult expected)
    {
        if (actual.Kind != expected.Kind)
            return false;
        if (actual.Kind == LibraryResultKind.Dictionary)
            return actual.Values.Count == expected.Values.Count && actual.Values.Zip(expected.Values).All(pair => Covers(pair.First, pair.Second));
        return Covers(actual.Values, expected.Values);
    }

    private static bool Covers(IReadOnlyList<LibraryValue> actual, IReadOnlyList<LibraryValue> expected) =>
        expected.All(expectedValue => actual.Any(actualValue => Covers(actualValue, expectedValue)));

    private static bool Covers(LibraryValue actual, LibraryValue expected) => (actual, expected) switch
    {
        (SequenceValue a, SequenceValue e) => Covers(a.Values, e.Values),
        (ElementsValue a, ElementsValue e) => Covers(a.Source, e.Source),
        (GroupingValue a, GroupingValue e) => Covers(a.Key, e.Key) && Covers(a.Values, e.Values),
        _ => actual.Canonical == expected.Canonical
    };

    private static bool SameEffects(IReadOnlyList<LibraryEffect> actual, IReadOnlyList<LibraryEffect> expected) =>
        actual.Count == expected.Count && actual.All(expected.Contains);
}
