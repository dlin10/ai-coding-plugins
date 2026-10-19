using System.Text.Json;
using System.Text.Json.Serialization;
using PlanForge.Review;
using PlanForge.Run;

namespace PlanForge.Evidence;

/// <summary>An obligation derived by the Orchestrator from existing Scout items.</summary>
/// <param name="Text">The implication's exact text.</param>
/// <param name="DerivedFrom">The stable Scout item ids supporting the implication.</param>
internal sealed record EvidenceImplication([property: JsonRequired] string Text,
                                           [property: JsonRequired] IReadOnlyList<string> DerivedFrom);

/// <summary>One Scout item or implication accounted for by the Evidence map.</summary>
/// <param name="Id">The stable Scout item id or positional implication id.</param>
/// <param name="Kind">Either scout or implication.</param>
/// <param name="Category">The Scout category, or null for an implication.</param>
/// <param name="Text">The exact recorded or supplied evidence text.</param>
/// <param name="Source">The recorded Scout source, or null for an implication.</param>
/// <param name="RecordedCheck">The repository locator check recorded with the answer.</param>
/// <param name="CurrentCheck">The repository locator check at indexing time.</param>
/// <param name="SourceChanged">Whether any field of the repository locator check changed.</param>
/// <param name="DerivedFrom">The supporting Scout item ids, empty for a Scout item.</param>
internal sealed record EvidenceItem(string Id, string Kind, string? Category, string Text, string? Source,
                                    LocatorCheck? RecordedCheck, LocatorCheck? CurrentCheck, bool SourceChanged,
                                    IReadOnlyList<string> DerivedFrom);

/// <summary>The complete evidence items and answer numbers unavailable to the map.</summary>
/// <param name="Items">Every Scout item followed by every implication, in their original order.</param>
/// <param name="NotIndexedAnswers">Answer numbers counted by the Run but absent from the Scout store.</param>
internal sealed record EvidenceIndex(IReadOnlyList<EvidenceItem> Items, IReadOnlyList<int> NotIndexedAnswers)
{
    /// <summary>Validates the Scout store and implications, then rechecks every repository locator locally.</summary>
    /// <param name="entries">The stored Scout answers in append order.</param>
    /// <param name="scoutAnswers">The Run state's consumed Scout answer count.</param>
    /// <param name="implications">Optional obligations derived from existing Scout items.</param>
    /// <param name="workspaceRoot">The Run's repository root for local locator checks.</param>
    public static EvidenceIndex Of(IReadOnlyList<ScoutAnswerEntry> entries, int scoutAnswers,
                                   IReadOnlyList<EvidenceImplication>? implications, string workspaceRoot)
    {
        try
        {
            ScoutAnswers.Validate(entries);
        }
        catch (JsonException error)
        {
            throw new JsonException("scout-answers.json is malformed.", error);
        }
        if (entries.Count > 0 && entries[^1].Number > scoutAnswers)
            throw new JsonException("scout-answers.json has an answer number above RunState.ScoutAnswers.");

        var items = new List<EvidenceItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var numbers = new HashSet<int>();
        foreach (var entry in entries)
        {
            numbers.Add(entry.Number);
            foreach (var category in ScoutCategories.Of(entry.Report, entry.Locators))
            {
                for (var index = 0; index < category.Items.Count; index++)
                {
                    var item = category.Items[index];
                    var id = ScoutItemId.Of(entry.Number, category.Name, index + 1);
                    var recorded = category.Checks![index];
                    var current = item.SourceKind == "repository" ? LocatorWindow.Check(workspaceRoot, item.Source) : null;
                    items.Add(new EvidenceItem(id, "scout", category.Name, item.Text, item.Source,
                                               recorded, current, recorded != current, []));
                    ids.Add(id);
                }
            }
        }

        if (implications is { Count: > 500 })
            throw new ArgumentRejectedException("implications[500] exceeds the limit of 500 implications.");
        for (var index = 0; index < (implications?.Count ?? 0); index++)
        {
            var implication = implications![index];
            if (implication is null || string.IsNullOrWhiteSpace(implication.Text) || implication.Text.Length > 2000)
                throw new ArgumentRejectedException($"implications[{index}].text must be non-blank and at most 2000 characters.");
            if (implication.DerivedFrom is null || implication.DerivedFrom.Count is < 1 or > 20)
                throw new ArgumentRejectedException($"implications[{index}].derivedFrom must contain 1–20 Scout item ids.");
            for (var source = 0; source < implication.DerivedFrom.Count; source++)
            {
                var id = implication.DerivedFrom[source];
                if (id is null || !ids.Contains(id))
                    throw new ArgumentRejectedException($"implications[{index}].derivedFrom[{source}] names an unknown Scout item id '{id ?? "<null>"}'.");
            }
            SensitiveInput.Guard(implication.Text, $"implications[{index}].text");
            items.Add(new EvidenceItem($"H{index + 1}", "implication", null, implication.Text, null,
                                       null, null, false, implication.DerivedFrom.ToArray()));
        }

        return new EvidenceIndex(items, Enumerable.Range(1, scoutAnswers).Where(number => !numbers.Contains(number)).ToArray());
    }
}

/// <summary>The reflection-free implication argument contract.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EvidenceImplication[]))]
internal sealed partial class EvidenceIndexJson : JsonSerializerContext;
