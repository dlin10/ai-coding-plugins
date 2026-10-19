using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Acts;
using PlanForge.Evidence;
using PlanForge.Infrastructure;
using PlanForge.Prompts;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>The append-only JSON report, its guard and its process-local mutation ordering.</summary>
public sealed class ScoutAnswersStoreTests : IDisposable
{
    /// <summary>The isolated workspace for each test.</summary>
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-scout-store-" + Guid.NewGuid().ToString("N"));

    /// <summary>Removes this test's isolated Run files.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Every category and item field survives the indented file unchanged.</summary>
    [Fact]
    public void The_complete_report_round_trips_exactly()
    {
        var run = NewRun();
        var entry = Entry(1);
        run.AppendScoutAnswer(entry);

        var stored = Assert.Single(run.ReadScoutAnswers());
        Assert.Equal(JsonSerializer.Serialize(entry.Report, ContractJson.Default.ScoutReport),
                     JsonSerializer.Serialize(stored.Report, ContractJson.Default.ScoutReport));
        Assert.Equal(entry.Question, stored.Question);
        using var document = JsonDocument.Parse(AtomicFile.Read(run.ScoutAnswersPath));
        var report = document.RootElement[0].GetProperty("report");
        Assert.Equal(["summary", .. ScoutCategories.Of(entry.Report).Select(category => category.Name)],
                     report.EnumerateObject().Select(property => property.Name));
        Assert.Contains("\n  {", AtomicFile.Read(run.ScoutAnswersPath), StringComparison.Ordinal);
    }

    /// <summary>Gaps are retained, and accepted entries retain their append order.</summary>
    [Fact]
    public void Entries_keep_their_numbers_and_append_order()
    {
        var run = NewRun();
        run.AppendScoutAnswer(Entry(2));
        run.AppendScoutAnswer(Entry(5));
        Assert.Equal([2, 5], run.ReadScoutAnswers().Select(entry => entry.Number));
    }

    /// <summary>Identities use the same category names as the file and one-based positions.</summary>
    [Fact]
    public void Item_ids_use_the_answer_category_and_one_based_position()
    {
        var run = NewRun();
        var entry = Entry(4);
        run.AppendScoutAnswer(entry);
        using var document = JsonDocument.Parse(AtomicFile.Read(run.ScoutAnswersPath));
        foreach (var category in document.RootElement[0].GetProperty("locators").EnumerateObject())
        {
            Assert.Equal($"A4.{category.Name}.1", ScoutItemId.Of(4, category.Name, 1));
            Assert.Equal($"A4.{category.Name}.2", ScoutItemId.Of(4, category.Name, 2));
        }
        Assert.Equal("A1.confirmedFacts.3", ScoutItemId.Of(1, ScoutCategories.ConfirmedFacts, 3));
    }

    /// <summary>Every category has an aligned check array, including external nulls.</summary>
    [Fact]
    public void Locators_align_with_all_six_categories()
    {
        var run = NewRun();
        run.AppendScoutAnswer(Entry(1));
        var entry = Assert.Single(run.ReadScoutAnswers());
        foreach (var category in ScoutCategories.Of(entry.Report, entry.Locators))
        {
            Assert.Equal(category.Items.Count, category.Checks!.Count);
            Assert.Equal(new LocatorCheck(false, null, "missing_file"), category.Checks[0]);
            Assert.Null(category.Checks[1]);
        }
    }

    /// <summary>A resolved locator records the same local window check used by the resolver.</summary>
    [Fact]
    public void Repository_windows_are_recorded_without_source_text()
    {
        var run = NewRun();
        AtomicFile.Write(Path.Combine(_workspace, "sample.cs"), "local source window\n");
        var report = Report();
        run.AppendScoutAnswer(new ScoutAnswerEntry(1, "question", report, ScoutLocators.Of(_workspace, report)));
        var checks = Assert.Single(run.ReadScoutAnswers()).Locators.ConfirmedFacts;
        Assert.Equal(LocatorWindow.Check(_workspace, "sample.cs:1"), checks[0]);
        Assert.DoesNotContain("local source window", AtomicFile.Read(run.ScoutAnswersPath), StringComparison.Ordinal);
    }

    /// <summary>A missing JSON file yields no entries and creates no report.</summary>
    [Fact]
    public void A_missing_file_reads_as_no_answers()
    {
        var run = NewRun();
        Assert.Empty(run.ReadScoutAnswers());
        Assert.False(File.Exists(run.ScoutAnswersPath));
    }

    /// <summary>Non-JSON bytes survive a refused append.</summary>
    [Fact]
    public void A_non_json_file_refuses_the_append() => Refuses("not json");

    /// <summary>A JSON object cannot masquerade as the entry array.</summary>
    [Fact]
    public void A_non_array_file_refuses_the_append() => Refuses("{}");

    /// <summary>A JSON null cannot masquerade as an absent file.</summary>
    [Fact]
    public void A_null_file_refuses_the_append() => Refuses("null");

    /// <summary>An entry missing its complete Vendor report is refused.</summary>
    [Fact]
    public void An_entry_without_report_refuses_the_append()
    {
        var entries = EntriesJson(Entry(1));
        entries[0]!.AsObject().Remove("report");
        Refuses(entries.ToJsonString());
    }

    /// <summary>Repeated answer numbers would repeat item identities and are refused.</summary>
    [Fact]
    public void Duplicate_numbers_refuse_the_append() => Refuses(EntriesJson(Entry(1), Entry(1)).ToJsonString());

    /// <summary>Stored numbers must increase in file order.</summary>
    [Fact]
    public void Decreasing_numbers_refuse_the_append() => Refuses(EntriesJson(Entry(2), Entry(1)).ToJsonString());

    /// <summary>Zero is not a valid answer number.</summary>
    [Fact]
    public void A_zero_number_refuses_the_append() => Refuses(EntriesJson(Entry(0)).ToJsonString());

    /// <summary>Negative numbers are not valid answer numbers.</summary>
    [Fact]
    public void A_negative_number_refuses_the_append() => Refuses(EntriesJson(Entry(-1)).ToJsonString());

    /// <summary>A new answer must come after the last accepted number.</summary>
    /// <param name="number">An equal or lower number to reject.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_new_number_not_greater_than_the_last_refuses_the_append(int number)
    {
        var run = NewRun();
        run.AppendScoutAnswer(Entry(2));
        var before = AtomicFile.Read(run.ScoutAnswersPath);
        Assert.Throws<JsonException>(() => run.AppendScoutAnswer(Entry(number)));
        Assert.Equal(before, AtomicFile.Read(run.ScoutAnswersPath));
    }

    /// <summary>Every recorded category must have exactly one check per item.</summary>
    [Fact]
    public void A_mismatched_locator_array_refuses_the_append()
    {
        var entries = EntriesJson(Entry(1));
        entries[0]!["locators"]![ScoutCategories.OpenDecisions] = new JsonArray();
        Refuses(entries.ToJsonString());
    }

    /// <summary>External sources must have null checks, never a repository window.</summary>
    [Fact]
    public void An_external_locator_with_a_check_refuses_the_append()
    {
        var entries = EntriesJson(Entry(1));
        var checks = entries[0]!["locators"]![ScoutCategories.ConfirmedFacts]!.AsArray();
        checks[1] = checks[0]!.DeepClone();
        Refuses(entries.ToJsonString());
    }

    /// <summary>Every locator check requires all three fields, including explicit nulls.</summary>
    /// <param name="field">The required field removed from a recorded check.</param>
    [Theory]
    [InlineData("resolved")]
    [InlineData("window")]
    [InlineData("reason")]
    public void A_locator_check_with_a_missing_field_refuses_the_append(string field)
    {
        var entries = EntriesJson(Entry(1));
        entries[0]!["locators"]![ScoutCategories.ConfirmedFacts]![0]!.AsObject().Remove(field);
        Refuses(entries.ToJsonString());
    }

    /// <summary>A secret assignment in only a source is still guarded as plain text.</summary>
    [Fact]
    public Task A_secret_only_in_an_item_source_is_sensitive_output() =>
        RefusesSensitive(Report(source: "api_key=AbC123_very_secret_value_789:1"));

    /// <summary>The sixth category is guarded along with every other item.</summary>
    [Fact]
    public Task A_secret_only_in_sixth_category_text_is_sensitive_output() =>
        RefusesSensitive(Report(dependentText: "api_key=AbC123_very_secret_value_789"));

    /// <summary>An append must wait until a held update has written before it reads.</summary>
    [Fact]
    public async Task An_append_waits_for_an_update_then_keeps_both_entries()
    {
        var run = NewRun();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var appending = new ManualResetEventSlim();
        var update = Task.Factory.StartNew(() => AtomicFile.Update(run.ScoutAnswersPath, text =>
        {
            Assert.Null(text);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return EntriesJson(Entry(1)).ToJsonString();
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? append = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            append = Task.Factory.StartNew(() =>
            {
                appending.Set();
                run.AppendScoutAnswer(Entry(2));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(appending.Wait(TimeSpan.FromSeconds(10)));
            Assert.NotSame(append, await Task.WhenAny(append, Task.Delay(150)));
        }
        finally
        {
            release.Set();
            await update;
            if (append is not null) await append;
        }
        Assert.Equal([1, 2], run.ReadScoutAnswers().Select(entry => entry.Number));
    }

    /// <summary>Answer three arriving first makes the later answer two invalid without replacement.</summary>
    [Fact]
    public Task Appends_released_three_then_two_keep_one_and_three() => OrderedAppends(3, 2);

    /// <summary>Answers arriving in number order all survive.</summary>
    [Fact]
    public Task Appends_released_two_then_three_keep_all_three() => OrderedAppends(2, 3);

    /// <summary>A rejected transformation creates no temporary file and preserves the old bytes.</summary>
    [Fact]
    public void A_throwing_update_preserves_bytes_and_creates_no_temporary_file()
    {
        var run = NewRun();
        AtomicFile.Write(run.ScoutAnswersPath, "original\nbytes");
        var before = AtomicFile.Read(run.ScoutAnswersPath);
        var files = Directory.GetFiles(run.Path).Order().ToArray();
        Assert.Throws<InvalidOperationException>(() => AtomicFile.Update(run.ScoutAnswersPath, _ =>
            throw new InvalidOperationException("rejected")));
        Assert.Equal(before, AtomicFile.Read(run.ScoutAnswersPath));
        Assert.Equal(files, Directory.GetFiles(run.Path).Order());
    }

    /// <summary>An update of an absent file passes null and atomically creates its replacement.</summary>
    [Fact]
    public void An_update_of_an_absent_file_receives_null()
    {
        var run = NewRun();
        AtomicFile.Update(run.ScoutAnswersPath, text =>
        {
            Assert.Null(text);
            return "[]";
        });
        Assert.Empty(run.ReadScoutAnswers());
    }

    /// <summary>Creates an isolated Run with an enabled Scout.</summary>
    private RunDirectory NewRun()
    {
        var run = RunDirectory.Create(_workspace, Guid.NewGuid().ToString("N"));
        run.WriteState(new RunState(run.RunId, _workspace, "Text", DateTimeOffset.UtcNow, 0, 5,
                                    Scout: new ScoutState(true, "codex", "model", null)));
        return run;
    }

    /// <summary>Builds an entry with all six categories and locally recorded checks.</summary>
    /// <param name="number">The entry's answer number.</param>
    private ScoutAnswerEntry Entry(int number)
    {
        var report = Report();
        return new(number, "question\nwith a second line", report, ScoutLocators.Of(_workspace, report));
    }

    /// <summary>Builds a sanitized report with repository and external items in every category.</summary>
    /// <param name="source">The confirmed fact's repository source.</param>
    /// <param name="dependentText">The sixth category's repository item text.</param>
    private static ScoutReport Report(string source = "sample.cs:1", string dependentText = "dependent\ntext — unchanged")
    {
        return new ScoutReport
        {
            Summary = "summary\nwith \"quotes\" and unicode: שלום",
            ConfirmedFacts = Items("fact", source),
            MaterialAssumptions = Items("assumption", "sample.cs#Sample.Method"),
            OpenDecisions = Items("decision", "sample.cs:2"),
            LikelyChangeSurface = Items("surface", "sample.cs:3"),
            VerificationEvidence = Items("verification", "sample.cs:4"),
            DependentsAndPinnedBehaviour = Items(dependentText, "sample.cs:5")
        };
    }

    /// <summary>Pairs a repository item with an external item, preserving all fields.</summary>
    /// <param name="text">The repository evidence text.</param>
    /// <param name="source">The repository locator.</param>
    private static ScoutItem[] Items(string text, string source) =>
        [new() { Text = text, SourceKind = "repository", Source = source },
         new() { Text = "external " + text, SourceKind = "external", Source = "https://example.com/reference?q=1#part" }];

    /// <summary>Serializes entries through the real store contract for malformed-file mutations.</summary>
    /// <param name="entries">The entries to serialize.</param>
    private static JsonArray EntriesJson(params ScoutAnswerEntry[] entries) =>
        JsonNode.Parse(JsonSerializer.Serialize(entries.ToList(), ScoutAnswersJson.Default.ListScoutAnswerEntry))!.AsArray();

    /// <summary>Checks both readers and appenders refuse malformed content without replacing it.</summary>
    /// <param name="text">The malformed file content.</param>
    private void Refuses(string text)
    {
        var run = NewRun();
        AtomicFile.Write(run.ScoutAnswersPath, text);
        var before = AtomicFile.Read(run.ScoutAnswersPath);
        Assert.Throws<JsonException>(() => run.ReadScoutAnswers());
        Assert.Throws<JsonException>(() => run.AppendScoutAnswer(Entry(10)));
        Assert.Equal(before, AtomicFile.Read(run.ScoutAnswersPath));
    }

    /// <summary>Exercises the complete Scout pipeline's plain-string guard without replacing a prior answer.</summary>
    /// <param name="report">A report whose single secret must cause refusal.</param>
    private async Task RefusesSensitive(ScoutReport report)
    {
        var run = NewRun();
        run.AppendScoutAnswer(Entry(1));
        run.WriteState(run.ReadState() with { ScoutAnswers = 1 });
        var before = AtomicFile.Read(run.ScoutAnswersPath);
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(report, "session");
        var error = await Assert.ThrowsAsync<ScoutException>(() => new Scout(vendor, new PromptLibrary(PromptLibrary.Locate(null)))
            .RunAsync(run, "bounded question", "continue", CancellationToken.None));
        Assert.StartsWith("sensitive_output:", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, AtomicFile.Read(run.ScoutAnswersPath));
        Assert.Equal(1, run.ReadState().ScoutAnswers);
    }

    /// <summary>Starts both appenders, then releases them in a controlled order.</summary>
    /// <param name="first">The answer released first.</param>
    /// <param name="second">The answer released after the first completes.</param>
    private async Task OrderedAppends(int first, int second)
    {
        var run = NewRun();
        run.AppendScoutAnswer(Entry(1));
        using var ready = new CountdownEvent(2);
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var firstTask = Start(first, releaseFirst);
        var secondTask = Start(second, releaseSecond);
        string? afterFirst = null;
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
            releaseFirst.Set();
            Assert.Null(await firstTask);
            afterFirst = AtomicFile.Read(run.ScoutAnswersPath);
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
            await Task.WhenAll(firstTask, secondTask);
        }
        var error = await secondTask;
        if (second < first)
        {
            Assert.IsType<JsonException>(error);
            Assert.Equal(afterFirst, AtomicFile.Read(run.ScoutAnswersPath));
            Assert.Equal([1, 3], run.ReadScoutAnswers().Select(entry => entry.Number));
        }
        else
        {
            Assert.Null(error);
            Assert.Equal([1, 2, 3], run.ReadScoutAnswers().Select(entry => entry.Number));
        }

        Task<Exception?> Start(int number, ManualResetEventSlim release) => Task.Factory.StartNew<Exception?>(() =>
        {
            ready.Signal();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return Record.Exception(() => run.AppendScoutAnswer(Entry(number)));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
