using System.Text.Json;
using PlanForge.Evidence;
using PlanForge.Infrastructure;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Complete evidence accounting, local source drift and implication argument validation.</summary>
public sealed class EvidenceIndexTests : IDisposable
{
    /// <summary>The isolated repository used for local locator checks.</summary>
    private readonly string _workspace = Directory.CreateTempSubdirectory("planforge-evidence-index-").FullName;

    /// <summary>Removes only this test's temporary repository.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Every item of every category and answer retains its position and exact content.</summary>
    [Fact]
    public void Every_item_of_every_entry_appears_once_with_its_id()
    {
        ScoutItem[] pair = [Repository(), External()];
        var report = new ScoutReport
        {
            Summary = "not an evidence item",
            ConfirmedFacts = pair,
            MaterialAssumptions = pair,
            OpenDecisions = pair,
            LikelyChangeSurface = pair,
            VerificationEvidence = pair,
            DependentsAndPinnedBehaviour = pair
        };

        var result = Index([Entry(1, report), Entry(2, report)], 2);

        string[] categories = ["confirmedFacts", "materialAssumptions", "openDecisions", "likelyChangeSurface",
                               "verificationEvidence", "dependentsAndPinnedBehaviour"];
        var expected = from number in new[] { 1, 2 }
                       from category in categories
                       from position in new[] { 1, 2 }
                       select ($"A{number}.{category}.{position}", category, pair[position - 1].Text, pair[position - 1].Source);
        Assert.Equal(expected, result.Items.Select(item => (item.Id, item.Category, item.Text, item.Source)));
        Assert.Equal(24, result.Items.Select(item => item.Id).Distinct().Count());
        Assert.All(result.Items, item =>
        {
            Assert.Equal("scout", item.Kind);
            Assert.Empty(item.DerivedFrom);
        });
        Assert.Empty(result.NotIndexedAnswers);
    }

    /// <summary>An external URL is retained without either local check or a source-changed mark.</summary>
    [Fact]
    public void External_items_carry_no_locator_checks()
    {
        var item = Assert.Single(Index([Entry(report: Report(External()))]).Items);

        Assert.Equal("https://example.com/evidence", item.Source);
        Assert.Null(item.RecordedCheck);
        Assert.Null(item.CurrentCheck);
        Assert.False(item.SourceChanged);
    }

    /// <summary>A changed local window marks the item while preserving its original text and check.</summary>
    [Fact]
    public void A_changed_window_marks_source_changed()
    {
        WriteSource("original\n");
        var entry = Entry();
        var recorded = entry.Locators.ConfirmedFacts[0];
        WriteSource("changed\n");

        var item = Assert.Single(Index([entry]).Items);

        Assert.Equal(recorded, item.RecordedCheck);
        Assert.True(item.CurrentCheck!.Resolved);
        Assert.NotEqual(recorded!.Window, item.CurrentCheck.Window);
        Assert.True(item.SourceChanged);
        Assert.Equal("Exact evidence\nwith a second line", item.Text);
    }

    /// <summary>A previously resolved locator becomes changed when its file disappears.</summary>
    [Fact]
    public void A_deleted_file_marks_source_changed()
    {
        WriteSource("original\n");
        var entry = Entry();
        File.Delete(Path.Combine(_workspace, "sample.cs"));

        var item = Assert.Single(Index([entry]).Items);

        Assert.True(item.RecordedCheck!.Resolved);
        Assert.Equal(new LocatorCheck(false, null, "missing_file"), item.CurrentCheck);
        Assert.True(item.SourceChanged);
    }

    /// <summary>A symbol that no longer exists is changed even though its file remains readable.</summary>
    [Fact]
    public void A_now_unresolved_symbol_marks_source_changed()
    {
        WriteSource("void Execute() {}\n");
        var entry = Entry(report: Report(Repository("sample.cs#Sample.Execute(int)")));
        WriteSource("void Replacement() {}\n");

        var item = Assert.Single(Index([entry]).Items);

        Assert.True(item.RecordedCheck!.Resolved);
        Assert.Equal(new LocatorCheck(false, null, "symbol_not_found"), item.CurrentCheck);
        Assert.True(item.SourceChanged);
    }

    /// <summary>An unchanged local window has equal checks and no source-changed mark.</summary>
    [Fact]
    public void An_unchanged_window_is_not_source_changed()
    {
        WriteSource("original\n");
        var item = Assert.Single(Index([Entry()]).Items);

        Assert.True(item.CurrentCheck!.Resolved);
        Assert.Equal(item.RecordedCheck, item.CurrentCheck);
        Assert.False(item.SourceChanged);
    }

    /// <summary>Creating a formerly missing source changes an unresolved recorded check.</summary>
    [Fact]
    public void A_newly_resolved_locator_marks_source_changed()
    {
        var entry = Entry();
        WriteSource("now present\n");

        var item = Assert.Single(Index([entry]).Items);

        Assert.Equal("missing_file", item.RecordedCheck!.Reason);
        Assert.True(item.CurrentCheck!.Resolved);
        Assert.True(item.SourceChanged);
    }

    /// <summary>An unresolved locator remains indexed and unchanged when its failure is unchanged.</summary>
    [Fact]
    public void An_unchanged_unresolved_locator_remains_indexed()
    {
        var item = Assert.Single(Index([Entry()]).Items);

        Assert.Equal(new LocatorCheck(false, null, "missing_file"), item.CurrentCheck);
        Assert.Equal(item.RecordedCheck, item.CurrentCheck);
        Assert.False(item.SourceChanged);
    }

    /// <summary>A change in reason alone marks an unresolved item as changed.</summary>
    [Fact]
    public void A_different_unresolved_reason_marks_source_changed()
    {
        var entry = Entry(report: Report(Repository("sample.cs:2")));
        WriteSource("one line\n");

        var item = Assert.Single(Index([entry]).Items);

        Assert.Equal("missing_file", item.RecordedCheck!.Reason);
        Assert.Equal(new LocatorCheck(false, null, "line_out_of_range"), item.CurrentCheck);
        Assert.True(item.SourceChanged);
    }

    /// <summary>Missing answer numbers before, between and after stored entries require manual accounting.</summary>
    [Fact]
    public void Skipped_appends_and_numbers_below_the_first_entry_are_not_indexed()
    {
        var result = Index([Entry(2), Entry(4)], 6);

        Assert.Equal([1, 3, 5, 6], result.NotIndexedAnswers);
        Assert.Equal(["A2.confirmedFacts.1", "A4.confirmedFacts.1"], result.Items.Select(item => item.Id));
    }

    /// <summary>An older Run with no JSON entries still lists every consumed answer.</summary>
    [Fact]
    public void An_empty_store_lists_all_consumed_answers_as_not_indexed()
    {
        var result = Index([], 3);

        Assert.Empty(result.Items);
        Assert.Equal([1, 2, 3], result.NotIndexedAnswers);
    }

    /// <summary>An entry cannot claim an answer number the Run has not consumed.</summary>
    [Fact]
    public void A_last_number_above_the_state_count_is_refused_naming_the_file()
    {
        var error = Assert.Throws<JsonException>(() => Index([Entry(2)], 1));

        Assert.Contains("scout-answers.json", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Repeated numbers are rejected by the shared Scout-store validity rules.</summary>
    [Fact]
    public void A_repeated_number_is_refused_naming_the_file()
    {
        var error = Assert.Throws<JsonException>(() => Index([Entry(), Entry()], 2));

        Assert.Contains("scout-answers.json", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The index also refuses malformed locator arrays rather than silently losing items.</summary>
    [Fact]
    public void A_malformed_entry_is_refused_naming_the_file()
    {
        var entry = Entry();
        entry = entry with { Locators = entry.Locators with { ConfirmedFacts = [] } };

        var error = Assert.Throws<JsonException>(() => Index([entry]));

        Assert.Contains("scout-answers.json", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Wire-schema validation errors also identify the Scout file at the indexing boundary.</summary>
    [Fact]
    public void An_invalid_source_is_refused_naming_the_file()
    {
        var entry = Entry();
        entry = entry with { Report = Report(Repository("not a locator")) };

        var error = Assert.Throws<JsonException>(() => Index([entry]));

        Assert.Contains("scout-answers.json", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Implications follow Scout items, retain their provenance and receive positional ids.</summary>
    [Fact]
    public void Implications_get_H_ids_and_keep_their_derived_from_ids()
    {
        EvidenceImplication[] implications = [Implication("first"), Implication("second")];

        var result = Index([Entry()], implications: implications);

        Assert.Equal(["A1.confirmedFacts.1", "H1", "H2"], result.Items.Select(item => item.Id));
        Assert.Equal(["first", "second"], result.Items.Skip(1).Select(item => item.Text));
        Assert.All(result.Items.Skip(1), item =>
        {
            Assert.Equal("implication", item.Kind);
            Assert.Null(item.Category);
            Assert.Null(item.Source);
            Assert.Null(item.RecordedCheck);
            Assert.Null(item.CurrentCheck);
            Assert.False(item.SourceChanged);
            Assert.Equal(["A1.confirmedFacts.1"], item.DerivedFrom);
        });
    }

    /// <summary>An unknown dependency identifies both argument positions and the bad id.</summary>
    [Fact]
    public void An_unknown_derived_from_id_is_refused_naming_the_index_and_id()
    {
        var error = Assert.Throws<ArgumentRejectedException>(() => Index([Entry()], implications:
            [Implication(), new("second", ["A1.confirmedFacts.1", "A9.openDecisions.1"])]));

        Assert.Contains("implications[1].derivedFrom[1]", error.Message, StringComparison.Ordinal);
        Assert.Contains("A9.openDecisions.1", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Implications cannot cite other implications instead of Scout evidence.</summary>
    [Fact]
    public void An_implication_id_is_not_a_valid_derived_from_id()
    {
        var error = Assert.Throws<ArgumentRejectedException>(() => Index([Entry()], implications:
            [Implication(), new("second", ["H1"])]));

        Assert.Contains("implications[1].derivedFrom[0]", error.Message, StringComparison.Ordinal);
        Assert.Contains("H1", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An implication must name at least one Scout item.</summary>
    [Fact]
    public void Empty_derived_from_is_refused_naming_the_index()
    {
        var error = Assert.Throws<ArgumentRejectedException>(() => Index([Entry()], implications: [new("text", [])]));

        Assert.Contains("implications[0].derivedFrom", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The dependency limit counts entries, including repeated ids.</summary>
    [Fact]
    public void Twenty_one_derived_from_ids_are_refused_naming_the_index()
    {
        var implication = new EvidenceImplication("text", Enumerable.Repeat("A1.confirmedFacts.1", 21).ToArray());

        var error = Assert.Throws<ArgumentRejectedException>(() => Index([Entry()], implications: [implication]));

        Assert.Contains("implications[0].derivedFrom", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Whitespace alone cannot form an implication.</summary>
    [Fact]
    public void Blank_text_is_refused_naming_the_index()
    {
        var error = Assert.Throws<ArgumentRejectedException>(() => Index([Entry()], implications: [Implication(" \t\n")]));

        Assert.Contains("implications[0].text", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The text limit is enforced without clipping the supplied implication.</summary>
    [Fact]
    public void A_2001_character_text_is_refused_naming_the_index()
    {
        var error = Assert.Throws<ArgumentRejectedException>(() => Index([Entry()], implications: [Implication(new string('x', 2001))]));

        Assert.Contains("implications[0].text", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An oversized implication list names the first disallowed argument index.</summary>
    [Fact]
    public void Five_hundred_and_one_implications_are_refused_naming_the_index()
    {
        var implications = Enumerable.Repeat(Implication(), 501).ToArray();

        var error = Assert.Throws<ArgumentRejectedException>(() => Index([Entry()], implications: implications));

        Assert.Contains("implications[500]", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The exact limits of all three implication bounds are accepted without dropping anything.</summary>
    [Fact]
    public void Implication_bounds_are_inclusive()
    {
        var implication = new EvidenceImplication(new string('x', 2000), Enumerable.Repeat("A1.confirmedFacts.1", 20).ToArray());

        var result = Index([Entry()], implications: Enumerable.Repeat(implication, 500).ToArray());

        Assert.Equal(501, result.Items.Count);
        Assert.Equal("H500", result.Items[^1].Id);
        Assert.Equal(2000, result.Items[^1].Text.Length);
        Assert.Equal(20, result.Items[^1].DerivedFrom.Count);
    }

    /// <summary>Implication text is guarded as plain text and the refusal names its argument index.</summary>
    [Fact]
    public void Sensitive_implication_text_is_refused_naming_the_index_without_its_value()
    {
        const string secret = "AbC123_very_secret_value_789";

        var error = Assert.Throws<SensitiveContentException>(() => Index([Entry()], implications:
            [Implication(), Implication("api_key=" + secret)]));

        Assert.Contains("implications[1].text", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    /// <summary>No entries and no implications produce no synthetic evidence items.</summary>
    [Fact]
    public void An_empty_store_without_implications_gives_no_items()
    {
        var result = Index([], 0);

        Assert.Empty(result.Items);
        Assert.Empty(result.NotIndexedAnswers);
    }

    /// <summary>The implication argument can be read with reflection JSON disabled.</summary>
    [Fact]
    public void Implications_have_a_source_generated_json_contract()
    {
        var implications = JsonSerializer.Deserialize("[{\"text\":\"exact text\",\"derivedFrom\":[\"A1.confirmedFacts.1\"]}]",
                                                      EvidenceIndexJson.Default.EvidenceImplicationArray)!;

        var item = Index([Entry()], implications: implications).Items[1];

        Assert.Equal("exact text", item.Text);
        Assert.Equal(["A1.confirmedFacts.1"], item.DerivedFrom);
    }

    /// <summary>Builds an index from the supplied store entries in the isolated workspace.</summary>
    /// <param name="entries">Scout entries to validate and index.</param>
    /// <param name="count">The Run's consumed answer count.</param>
    /// <param name="implications">Optional Orchestrator-derived implications.</param>
    private EvidenceIndex Index(IReadOnlyList<ScoutAnswerEntry> entries, int count = 1,
                                IReadOnlyList<EvidenceImplication>? implications = null) =>
        EvidenceIndex.Of(entries, count, implications, _workspace);

    /// <summary>Records the locator checks before any test mutation.</summary>
    /// <param name="number">The consumed Scout answer number.</param>
    /// <param name="report">An optional report replacing the single-fact default.</param>
    private ScoutAnswerEntry Entry(int number = 1, ScoutReport? report = null)
    {
        report ??= Report(Repository());
        return new(number, "bounded question", report, ScoutLocators.Of(_workspace, report));
    }

    /// <summary>Builds a report with the supplied facts and five empty categories.</summary>
    /// <param name="facts">The confirmed facts to retain in order.</param>
    private static ScoutReport Report(params ScoutItem[] facts) => new()
    {
        Summary = "summary",
        ConfirmedFacts = facts,
        MaterialAssumptions = [],
        OpenDecisions = [],
        LikelyChangeSurface = [],
        VerificationEvidence = [],
        DependentsAndPinnedBehaviour = []
    };

    /// <summary>Builds a repository fact with exact multiline text.</summary>
    /// <param name="source">The local locator to check.</param>
    private static ScoutItem Repository(string source = "sample.cs:1") =>
        new() { Text = "Exact evidence\nwith a second line", SourceKind = "repository", Source = source };

    /// <summary>Builds an external fact that must never be fetched.</summary>
    private static ScoutItem External() =>
        new() { Text = "External evidence", SourceKind = "external", Source = "https://example.com/evidence" };

    /// <summary>Builds an implication supported by the default Scout fact.</summary>
    /// <param name="text">The implication text.</param>
    private static EvidenceImplication Implication(string text = "derived obligation") => new(text, ["A1.confirmedFacts.1"]);

    /// <summary>Writes the local file atomically before a locator check.</summary>
    /// <param name="text">The complete source file text.</param>
    private void WriteSource(string text) => AtomicFile.Write(Path.Combine(_workspace, "sample.cs"), text);
}
