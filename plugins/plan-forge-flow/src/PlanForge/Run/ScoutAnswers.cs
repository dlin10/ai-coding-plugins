using System.Text.Json;
using System.Text.Json.Serialization;
using PlanForge.Evidence;
using PlanForge.Vendors;

namespace PlanForge.Run;

/// <summary>One successful Scout answer and the local checks recorded with it.</summary>
/// <param name="Number">The positive, monotonically increasing answer number.</param>
/// <param name="Question">The bounded question answered by the Scout.</param>
/// <param name="Report">The complete answer returned by the Vendor.</param>
/// <param name="Locators">One local check per repository item, and null per external item.</param>
internal sealed record ScoutAnswerEntry([property: JsonRequired] int Number,
                                        [property: JsonRequired] string Question,
                                        [property: JsonRequired] ScoutReport Report,
                                        [property: JsonRequired] ScoutLocators Locators);

/// <summary>The recorded locator checks, in the same order as each report category.</summary>
/// <param name="ConfirmedFacts">Checks for confirmed facts.</param>
/// <param name="MaterialAssumptions">Checks for material assumptions.</param>
/// <param name="OpenDecisions">Checks for open decisions.</param>
/// <param name="LikelyChangeSurface">Checks for the likely change surface.</param>
/// <param name="VerificationEvidence">Checks for verification evidence.</param>
/// <param name="DependentsAndPinnedBehaviour">Checks for dependents and pinned behaviour.</param>
internal sealed record ScoutLocators([property: JsonRequired, JsonPropertyName(ScoutCategories.ConfirmedFacts)] IReadOnlyList<LocatorCheck?> ConfirmedFacts,
                                    [property: JsonRequired, JsonPropertyName(ScoutCategories.MaterialAssumptions)] IReadOnlyList<LocatorCheck?> MaterialAssumptions,
                                    [property: JsonRequired, JsonPropertyName(ScoutCategories.OpenDecisions)] IReadOnlyList<LocatorCheck?> OpenDecisions,
                                    [property: JsonRequired, JsonPropertyName(ScoutCategories.LikelyChangeSurface)] IReadOnlyList<LocatorCheck?> LikelyChangeSurface,
                                    [property: JsonRequired, JsonPropertyName(ScoutCategories.VerificationEvidence)] IReadOnlyList<LocatorCheck?> VerificationEvidence,
                                    [property: JsonRequired, JsonPropertyName(ScoutCategories.DependentsAndPinnedBehaviour)] IReadOnlyList<LocatorCheck?> DependentsAndPinnedBehaviour)
{
    /// <summary>Checks every repository source without fetching external sources.</summary>
    /// <param name="workspaceRoot">The Run's workspace root.</param>
    /// <param name="report">The answer whose category order is retained.</param>
    public static ScoutLocators Of(string workspaceRoot, ScoutReport report)
    {
        var checks = ScoutCategories.Of(report).Select(category => category.Items.Select(item =>
            item.SourceKind == "repository" ? LocatorWindow.Check(workspaceRoot, item.Source) : null).ToArray()).ToArray();
        return new(checks[0], checks[1], checks[2], checks[3], checks[4], checks[5]);
    }
}

/// <summary>The category names shared by persisted checks and stable item identities.</summary>
internal static class ScoutCategories
{
    /// <summary>The confirmed-facts category name.</summary>
    internal const string ConfirmedFacts = "confirmedFacts";
    /// <summary>The material-assumptions category name.</summary>
    internal const string MaterialAssumptions = "materialAssumptions";
    /// <summary>The open-decisions category name.</summary>
    internal const string OpenDecisions = "openDecisions";
    /// <summary>The likely-change-surface category name.</summary>
    internal const string LikelyChangeSurface = "likelyChangeSurface";
    /// <summary>The verification-evidence category name.</summary>
    internal const string VerificationEvidence = "verificationEvidence";
    /// <summary>The dependents-and-pinned-behaviour category name.</summary>
    internal const string DependentsAndPinnedBehaviour = "dependentsAndPinnedBehaviour";

    /// <summary>Enumerates all categories in report order with their optional recorded checks.</summary>
    /// <param name="report">The complete Scout answer.</param>
    /// <param name="locators">Recorded checks, when reading an entry.</param>
    public static IEnumerable<(string Name, IReadOnlyList<ScoutItem> Items, IReadOnlyList<LocatorCheck?>? Checks)>
        Of(ScoutReport report, ScoutLocators? locators = null)
    {
        yield return (ConfirmedFacts, report.ConfirmedFacts, locators?.ConfirmedFacts);
        yield return (MaterialAssumptions, report.MaterialAssumptions, locators?.MaterialAssumptions);
        yield return (OpenDecisions, report.OpenDecisions, locators?.OpenDecisions);
        yield return (LikelyChangeSurface, report.LikelyChangeSurface, locators?.LikelyChangeSurface);
        yield return (VerificationEvidence, report.VerificationEvidence, locators?.VerificationEvidence);
        yield return (DependentsAndPinnedBehaviour, report.DependentsAndPinnedBehaviour, locators?.DependentsAndPinnedBehaviour);
    }
}

/// <summary>Stable identities for items in append-only Scout answers.</summary>
internal static class ScoutItemId
{
    /// <summary>Names an item by answer, category and one-based position.</summary>
    /// <param name="number">The answer number.</param>
    /// <param name="category">A category name from ScoutCategories.</param>
    /// <param name="position">The one-based item position within the category.</param>
    public static string Of(int number, string category, int position) => $"A{number}.{category}.{position}";
}

/// <summary>The JSON store's shared parsing and validity rules.</summary>
internal static class ScoutAnswers
{
    /// <summary>Reads and validates an array; only an absent file means no entries.</summary>
    /// <param name="text">The file contents, or null when the file is absent.</param>
    public static List<ScoutAnswerEntry> Read(string? text)
    {
        var entries = text is null ? [] : JsonSerializer.Deserialize(text, ScoutAnswersJson.Default.ListScoutAnswerEntry)
                                         ?? throw new JsonException("scout-answers.json must be an array of entries.");
        Validate(entries);
        return entries;
    }

    /// <summary>Refuses malformed entries, checks and non-increasing positive numbers.</summary>
    /// <param name="entries">The complete sequence, in append order.</param>
    public static void Validate(IReadOnlyList<ScoutAnswerEntry> entries)
    {
        var previous = 0;
        foreach (var entry in entries)
        {
            if (entry is null || entry.Number <= previous || entry.Question is null
                || entry.Report is null || entry.Locators is null)
                throw new JsonException("scout-answers.json requires well-formed entries with positive, strictly increasing numbers.");
            previous = entry.Number;
            entry.Report.OnDeserialized();
            foreach (var category in ScoutCategories.Of(entry.Report, entry.Locators))
            {
                if (category.Checks is null || category.Checks.Count != category.Items.Count)
                    throw new JsonException("scout-answers.json locator arrays must match their categories.");
                for (var index = 0; index < category.Items.Count; index++)
                {
                    var item = category.Items[index];
                    item.OnDeserialized();
                    var check = category.Checks[index];
                    if (item.SourceKind == "external" ? check is not null
                        : check is null || (check.Resolved
                            ? check.Window is null || check.Window.Length != 16
                              || check.Window.Any(character => !char.IsAsciiHexDigitLower(character)) || check.Reason is not null
                            : check.Window is not null || !LocatorWindow.IsReason(check.Reason)))
                        throw new JsonException("scout-answers.json requires a locator check for each repository item and null for each external item.");
                }
            }
        }
    }
}

/// <summary>The reflection-free, indented Scout store contract, preserving the Vendor's wire names.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<ScoutAnswerEntry>))]
internal sealed partial class ScoutAnswersJson : JsonSerializerContext;
