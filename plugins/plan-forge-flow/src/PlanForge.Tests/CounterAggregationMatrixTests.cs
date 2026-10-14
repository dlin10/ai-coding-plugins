using System.Text.Json;
using PlanForge.Orchestration;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Aggregating counters (R8, R13): one counter's value in one usage unit of the phase, through each of
/// the four formats, fed straight to the one normalization, <see cref="UsageCounters"/>. Every other
/// counter of every unit is valid, so each cell shows what that one value does to the whole record.
/// The expectation table is written by hand from R13, not derived from the code.
/// </summary>
public sealed class CounterAggregationMatrixTests
{
    private const long MAX = long.MaxValue;

    private static readonly DateTimeOffset OPENING = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CLOSING = OPENING.AddSeconds(60);

    /// <summary>Axis B: each value's JSON text; null for a value absent from its unit.</summary>
    private static readonly Dictionary<Value, string?> RAW = new()
    {
        [Value.Integer] = "7",
        [Value.DecimalString] = "\"7\"",
        [Value.Absent] = null,
        [Value.Negative] = "-7",
        [Value.Fractional] = "7.5",
        [Value.Boolean] = "true",
        [Value.Object] = """{"intValue":7}""",
        [Value.AboveMax] = "9223372036854775808",
        [Value.OverflowingSum] = "9223372036854775807",
        [Value.Null] = "null",
        [Value.Array] = "[7]",
        [Value.NonDigitString] = "\"7x\"",
    };

    /// <summary>The values that are present but never valid, whatever the counter; before <see cref="CELLS"/>, which reads them.</summary>
    private static readonly Value[] ALWAYS_INVALID = [Value.Negative, Value.Boolean, Value.Object, Value.Null, Value.Array, Value.NonDigitString];

    /// <summary>The combinations the task leaves out: decimal strings in the two transcript formats, absence in the rollout.</summary>
    private static readonly HashSet<(Column, Value)> EXCLUDED =
    [
        (Column.ClaudeTranscriptCacheRead, Value.DecimalString),
        (Column.CodexRolloutCachedInput, Value.DecimalString),
        (Column.CodexRolloutCachedInput, Value.Absent),
    ];

    /// <summary>
    /// The expectations from R13, per column and value: the record with the unit alone, then with the
    /// second, valid unit. Units: Claude A = input 10, cache read X, creation 30, output 40, thinking 50,
    /// cost 0.5; Claude B = 1, 2, 3, 4, 5, cost 0.25; Codex A = input 100, cached X, cache write 30,
    /// output 40, reasoning 50; Codex B = 1, 2, 3, 4, 5, the rollout's second event carrying the totals
    /// 101, 12, 33, 44, 55. For the cost column, Claude's cache read is 20 in A.
    /// </summary>
    private static readonly Dictionary<(Column, Value, Units), Row> CELLS = Cells();

    public enum Column
    {
        /// <summary><c>cache_read_input_tokens</c> of a Claude Code transcript response.</summary>
        ClaudeTranscriptCacheRead,
        /// <summary><c>cached_input_tokens</c> of a Codex rollout <c>token_count</c> total.</summary>
        CodexRolloutCachedInput,
        /// <summary><c>cache_read_tokens</c> of a Claude Code <c>api_request</c> export event.</summary>
        ClaudeExportCacheRead,
        /// <summary><c>cost_usd</c> of a Claude Code <c>api_request</c> export event.</summary>
        ClaudeExportCost,
        /// <summary><c>cached_token_count</c> of a Codex <c>response.completed</c> export event.</summary>
        CodexExportCached,
    }

    public enum Value { Integer, DecimalString, Absent, Negative, Fractional, Boolean, Object, AboveMax, OverflowingSum, Null, Array, NonDigitString }

    public enum Units { Alone, WithSecond }

    public static TheoryData<Column, Value, Units> AllCells()
    {
        var data = new TheoryData<Column, Value, Units>();
        foreach (var (column, value, units) in CELLS.Keys) data.Add(column, value, units);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllCells))]
    public void Every_cell_is_exact_for_every_counter_of_the_record(Column column, Value value, Units units)
    {
        var row = CELLS[(column, value, units)];

        var reading = Read(column, value, units);

        Assert.Equal(Expected(column, units, row), reading.Tokens);
        Assert.Equal(row.Incomplete, reading.Incomplete);
        Assert.Null(reading.Missing);
    }

    [Fact]
    public void Every_axis_value_occurs()
    {
        Assert.Equal(Enum.GetValues<Column>().Order(), CELLS.Keys.Select(key => key.Item1).Distinct().Order());
        Assert.Equal(Enum.GetValues<Value>().Order(), CELLS.Keys.Select(key => key.Item2).Distinct().Order());
        Assert.Equal(Enum.GetValues<Units>().Order(), CELLS.Keys.Select(key => key.Item3).Distinct().Order());
    }

    [Fact]
    public void The_excluded_combinations_are_the_only_ones_missing()
    {
        var missing = from column in Enum.GetValues<Column>()
                      from value in Enum.GetValues<Value>()
                      from units in Enum.GetValues<Units>()
                      where !CELLS.ContainsKey((column, value, units))
                      select (column, value);

        Assert.Equal(EXCLUDED.Order(), missing.Distinct().Order());
        Assert.Equal(EXCLUDED.Count * 2, missing.Count());
    }

    [Fact]
    public void Claude_transcript_a_response_whose_every_counter_is_invalid_gives_source_failed()
    {
        var counters = new UsageCounters();
        counters.AddClaudeResponse(Object(("input_tokens", "-1"), ("cache_read_input_tokens", "1.5"),
                                          ("cache_creation_input_tokens", "true"), ("output_tokens", "{}"),
                                          ("output_tokens_details", """{"thinking_tokens":"x"}""")), inPhase: true);

        AssertSourceFailed(counters.Reading());
    }

    [Fact]
    public void Codex_rollout_an_event_whose_every_total_is_invalid_gives_source_failed()
    {
        var counters = new UsageCounters();
        counters.AddCodexRollout([(OPENING.AddSeconds(10), Object(("input_tokens", "-1"), ("cached_input_tokens", "1.5"),
                                                                  ("cache_write_input_tokens", "true"), ("output_tokens", "{}"),
                                                                  ("reasoning_output_tokens", "\"x\"")))],
                                 OPENING, CLOSING);

        AssertSourceFailed(counters.Reading());
    }

    [Fact]
    public void Claude_export_an_event_whose_every_counter_is_invalid_gives_source_failed()
    {
        var counters = new UsageCounters();
        var unit = Object(("input_tokens", "-1"), ("cache_read_tokens", "1.5"), ("cache_creation_tokens", "true"),
                          ("output_tokens", "{}"), ("cost_usd", "-0.5"));
        counters.AddClaudeExportEvent(name => Field(unit, name), inPhase: true);

        AssertSourceFailed(counters.Reading());
    }

    [Fact]
    public void Codex_export_an_event_whose_every_counter_is_invalid_gives_source_failed()
    {
        var counters = new UsageCounters();
        var unit = Object(("input_token_count", "-1"), ("cached_token_count", "1.5"), ("cache_write_token_count", "true"),
                          ("output_token_count", "{}"), ("reasoning_token_count", "\"x\""));
        counters.AddCodexExportEvent(name => Field(unit, name), inPhase: true);

        AssertSourceFailed(counters.Reading());
    }

    // ---- Claude's input, formed per unit ----

    [Theory]
    [InlineData(false, "cache_read")]
    [InlineData(false, "cache_read,cache_creation")]
    [InlineData(false, "input,cache_read,cache_creation")]
    [InlineData(true, "cache_creation")]
    [InlineData(true, "input,cache_creation")]
    [InlineData(true, "input,cache_read,cache_creation")]
    public void A_Claude_unit_missing_input_components_leaves_input_absent_beside_a_complete_unit(bool export, string missing)
    {
        var lacking = missing.Split(',');
        string? Part(string component, string raw) => lacking.Contains(component) ? null : raw;
        var counters = new UsageCounters();
        if (export)
        {
            var first = ClaudeEvent(Part("input", "10")!, Part("cache_read", "20"), Part("cache_creation", "30")!, "40", null);
            var second = ClaudeEvent("1", "2", "3", "4", null);
            counters.AddClaudeExportEvent(name => Field(first, name), inPhase: true);
            counters.AddClaudeExportEvent(name => Field(second, name), inPhase: true);
        }
        else
        {
            counters.AddClaudeResponse(Object(("input_tokens", Part("input", "10")), ("cache_read_input_tokens", Part("cache_read", "20")),
                                              ("cache_creation_input_tokens", Part("cache_creation", "30")), ("output_tokens", "40")),
                                       inPhase: true);
            counters.AddClaudeResponse(Object(("input_tokens", "1"), ("cache_read_input_tokens", "2"), ("cache_creation_input_tokens", "3"),
                                              ("output_tokens", "4")), inPhase: true);
        }

        var reading = counters.Reading();

        Assert.Equal(new OrchestratorTokens(null, lacking.Contains("cache_read") ? 2 : 22, lacking.Contains("cache_creation") ? 3 : 33, 44),
                     reading.Tokens);
        Assert.True(reading.Incomplete);
    }

    // ---- Cost ----

    [Theory]
    [InlineData("1e-29", 1e-29)]
    [InlineData("1e29", 1e29)]
    [InlineData("0", 0.0)]
    public void A_cost_of_any_finite_number_of_at_least_0_is_valid(string raw, double expected)
    {
        var counters = new UsageCounters();
        var unit = ClaudeEvent("10", "20", "30", "40", raw);
        counters.AddClaudeExportEvent(name => Field(unit, name), inPhase: true);

        var reading = counters.Reading();

        Assert.Equal(expected, reading.Tokens!.CostUsd);
        Assert.False(reading.Incomplete);
    }

    [Fact]
    public void A_cost_sum_that_is_not_finite_leaves_cost_absent_and_the_record_incomplete()
    {
        var counters = new UsageCounters();
        var unit = ClaudeEvent("10", "20", "30", "40", "1.7e308");
        counters.AddClaudeExportEvent(name => Field(unit, name), inPhase: true);
        counters.AddClaudeExportEvent(name => Field(unit, name), inPhase: true);

        var reading = counters.Reading();

        Assert.Equal(new OrchestratorTokens(120, 40, 60, 80), reading.Tokens);
        Assert.True(reading.Incomplete);
    }

    // ---- A counter none of the phase's units carries ----

    [Fact]
    public void Claude_transcript_a_phase_whose_units_lack_thinking_leaves_reasoning_absent_though_one_outside_carries_it()
    {
        var counters = new UsageCounters();
        counters.AddClaudeResponse(ClaudeResponse("1", "2", "9"), inPhase: false);
        counters.AddClaudeResponse(Object(("input_tokens", "10"), ("cache_read_input_tokens", "20"), ("cache_creation_input_tokens", "30"),
                                          ("output_tokens", "40"), ("output_tokens_details", "null")), inPhase: true);

        var reading = counters.Reading();

        Assert.Equal(new OrchestratorTokens(60, 20, 30, 40), reading.Tokens);
        Assert.False(reading.Incomplete);
    }

    [Fact]
    public void Claude_transcript_an_empty_phase_counts_zero_for_every_counter_carried_outside_it()
    {
        var counters = new UsageCounters();
        counters.AddClaudeResponse(ClaudeResponse("1", "2", "9"), inPhase: false);

        Assert.Equal(new OrchestratorTokens(0, 0, 0, 0, 0), counters.Reading().Tokens);
    }

    [Fact]
    public void Claude_export_a_phase_whose_units_lack_cost_leaves_cost_absent_and_an_empty_one_counts_zero()
    {
        var outside = ClaudeEvent("1", "2", "3", "4", "0.25");
        var inside = ClaudeEvent("10", "20", "30", "40", null);
        var withUnit = new UsageCounters();
        withUnit.AddClaudeExportEvent(name => Field(outside, name), inPhase: false);
        withUnit.AddClaudeExportEvent(name => Field(inside, name), inPhase: true);
        var empty = new UsageCounters();
        empty.AddClaudeExportEvent(name => Field(outside, name), inPhase: false);

        Assert.Equal(new OrchestratorTokens(60, 20, 30, 40), withUnit.Reading().Tokens);
        Assert.Equal(new OrchestratorTokens(0, 0, 0, 0, null, 0), empty.Reading().Tokens);
    }

    [Fact]
    public void Codex_export_a_phase_whose_units_lack_reasoning_leaves_it_absent_and_an_empty_one_counts_zero()
    {
        var outside = CodexEvent("1", "2", "3", "4", "5");
        var inside = Object(("input_token_count", "100"), ("cached_token_count", "7"), ("cache_write_token_count", "30"),
                            ("output_token_count", "40"));
        var withUnit = new UsageCounters();
        withUnit.AddCodexExportEvent(name => Field(outside, name), inPhase: false);
        withUnit.AddCodexExportEvent(name => Field(inside, name), inPhase: true);
        var empty = new UsageCounters();
        empty.AddCodexExportEvent(name => Field(outside, name), inPhase: false);

        Assert.Equal(new OrchestratorTokens(100, 7, 30, 40), withUnit.Reading().Tokens);
        Assert.Equal(new OrchestratorTokens(0, 0, 0, 0, 0), empty.Reading().Tokens);
    }

    [Fact]
    public void Codex_rollout_a_counter_carried_before_the_phase_but_by_none_of_its_events_still_counts_zero()
    {
        var counters = new UsageCounters();
        counters.AddCodexRollout([(OPENING.AddSeconds(-10), CodexTotals("50", "5", "5", "5", "5")),
                                  (OPENING.AddSeconds(10), Object(("input_tokens", "60"), ("cached_input_tokens", "6"),
                                                                  ("output_tokens", "7"), ("reasoning_output_tokens", "8")))],
                                 OPENING, CLOSING);

        var reading = counters.Reading();

        Assert.Equal(new OrchestratorTokens(10, 1, 0, 2, 3), reading.Tokens);
        Assert.False(reading.Incomplete);
    }

    [Fact]
    public void Codex_rollout_a_null_info_or_total_lacks_the_counters_rather_than_invalidating_them()
    {
        var counters = new UsageCounters();
        counters.AddCodexRollout([(OPENING.AddSeconds(10), CodexTotals("50", "5", "5", "5", "5")),
                                  (OPENING.AddSeconds(20), JsonDocument.Parse("null").RootElement),
                                  (OPENING.AddSeconds(30), null)],
                                 OPENING, CLOSING);

        var reading = counters.Reading();

        Assert.Equal(new OrchestratorTokens(50, 5, 5, 5, 5), reading.Tokens);
        Assert.False(reading.Incomplete);
    }

    private static Dictionary<(Column, Value, Units), Row> Cells()
    {
        var cells = new Dictionary<(Column, Value, Units), Row>();
        void Add(Column column, Value value, Row alone, Row withSecond)
        {
            cells.Add((column, value, Units.Alone), alone);
            cells.Add((column, value, Units.WithSecond), withSecond);
        }

        // Claude's input is formed per unit from its three components, so a cache read that is absent,
        // invalid, or overflows that unit's input leaves the input absent too.
        foreach (var column in new[] { Column.ClaudeTranscriptCacheRead, Column.ClaudeExportCacheRead })
        {
            Add(column, Value.Integer, new(7, 47, false), new(9, 53, false));
            if (column == Column.ClaudeExportCacheRead) Add(column, Value.DecimalString, new(7, 47, false), new(9, 53, false));
            Add(column, Value.Absent, new(null, null, true), new(2, null, true));
            foreach (var invalid in ALWAYS_INVALID) Add(column, invalid, new(null, null, true), new(null, null, true));
            Add(column, Value.Fractional, new(null, null, true), new(null, null, true));
            Add(column, Value.AboveMax, new(null, null, true), new(null, null, true));
            Add(column, Value.OverflowingSum, new(MAX, null, true), new(null, null, true));
        }

        // A cost is valid as any finite JSON number of at least 0: fractions and values past 2^63−1 sum.
        Add(Column.ClaudeExportCost, Value.Integer, new(7m, 60, false), new(7.25m, 66, false));
        Add(Column.ClaudeExportCost, Value.DecimalString, new(null, 60, true), new(null, 66, true));
        Add(Column.ClaudeExportCost, Value.Absent, new(null, 60, false), new(0.25m, 66, false));
        foreach (var invalid in ALWAYS_INVALID) Add(Column.ClaudeExportCost, invalid, new(null, 60, true), new(null, 66, true));
        Add(Column.ClaudeExportCost, Value.Fractional, new(7.5m, 60, false), new(7.75m, 66, false));
        Add(Column.ClaudeExportCost, Value.AboveMax, new(9223372036854775808m, 60, false), new(9223372036854775808.25m, 66, false));
        Add(Column.ClaudeExportCost, Value.OverflowingSum, new(9223372036854775807m, 60, false), new(9223372036854775807.25m, 66, false));

        Add(Column.CodexExportCached, Value.Integer, new(7, 100, false), new(9, 101, false));
        Add(Column.CodexExportCached, Value.DecimalString, new(7, 100, false), new(9, 101, false));
        Add(Column.CodexExportCached, Value.Absent, new(null, 100, false), new(2, 101, false));
        foreach (var invalid in ALWAYS_INVALID) Add(Column.CodexExportCached, invalid, new(null, 100, true), new(null, 101, true));
        Add(Column.CodexExportCached, Value.Fractional, new(null, 100, true), new(null, 101, true));
        Add(Column.CodexExportCached, Value.AboveMax, new(null, 100, true), new(null, 101, true));
        Add(Column.CodexExportCached, Value.OverflowingSum, new(MAX, 100, false), new(null, 101, true));

        // An invalid total within the phase leaves the counter absent, though the event also counts as
        // lacking it for the baseline: the second event's 12 does not bring it back. A total of 2^63−1
        // followed by 12 falls, counts the 12 from zero, and overflows the sum.
        Add(Column.CodexRolloutCachedInput, Value.Integer, new(7, 100, false), new(12, 101, false));
        foreach (var invalid in ALWAYS_INVALID) Add(Column.CodexRolloutCachedInput, invalid, new(null, 100, true), new(null, 101, true));
        Add(Column.CodexRolloutCachedInput, Value.Fractional, new(null, 100, true), new(null, 101, true));
        Add(Column.CodexRolloutCachedInput, Value.AboveMax, new(null, 100, true), new(null, 101, true));
        Add(Column.CodexRolloutCachedInput, Value.OverflowingSum, new(MAX, 100, false), new(null, 101, true));
        return cells;
    }

    /// <summary>The whole record a cell expects: the row's two varying counters, and the fixed sums of the others.</summary>
    /// <param name="column">The format and counter under test.</param>
    /// <param name="units">Whether the second unit is in the phase.</param>
    /// <param name="row">The cell's expectation.</param>
    private static OrchestratorTokens Expected(Column column, Units units, Row row)
    {
        var alone = units == Units.Alone;
        return column switch
        {
            Column.ClaudeTranscriptCacheRead => new(row.Input, (long?)row.Target, alone ? 30 : 33, alone ? 40 : 44, alone ? 50 : 55),
            Column.ClaudeExportCacheRead => new(row.Input, (long?)row.Target, alone ? 30 : 33, alone ? 40 : 44, null, alone ? 0.5 : 0.75),
            Column.ClaudeExportCost => new(row.Input, alone ? 20 : 22, alone ? 30 : 33, alone ? 40 : 44, null, (double?)row.Target),
            _ => new(row.Input, (long?)row.Target, alone ? 30 : 33, alone ? 40 : 44, alone ? 50 : 55),
        };
    }

    /// <summary>Feeds the cell's units, all within the phase, to one normalization and reads it.</summary>
    /// <param name="column">The format and counter under test.</param>
    /// <param name="value">The value of that counter in the first unit.</param>
    /// <param name="units">Whether the second, valid unit is in the phase.</param>
    private static UsageReading Read(Column column, Value value, Units units)
    {
        var x = RAW[value];
        var second = units == Units.WithSecond;
        var counters = new UsageCounters();
        switch (column)
        {
            case Column.ClaudeTranscriptCacheRead:
                counters.AddClaudeResponse(ClaudeResponse("10", x, "50"), inPhase: true);
                if (second) counters.AddClaudeResponse(ClaudeResponse("1", "2", "5", creation: "3", output: "4"), inPhase: true);
                break;

            case Column.ClaudeExportCacheRead:
            case Column.ClaudeExportCost:
                var cost = column == Column.ClaudeExportCost;
                var first = ClaudeEvent("10", cost ? "20" : x, "30", "40", cost ? x : "0.5");
                counters.AddClaudeExportEvent(name => Field(first, name), inPhase: true);
                if (second)
                {
                    var other = ClaudeEvent("1", "2", "3", "4", "0.25");
                    counters.AddClaudeExportEvent(name => Field(other, name), inPhase: true);
                }
                break;

            case Column.CodexExportCached:
                var codex = CodexEvent("100", x, "30", "40", "50");
                counters.AddCodexExportEvent(name => Field(codex, name), inPhase: true);
                if (second)
                {
                    var other = CodexEvent("1", "2", "3", "4", "5");
                    counters.AddCodexExportEvent(name => Field(other, name), inPhase: true);
                }
                break;

            case Column.CodexRolloutCachedInput:
                var events = new List<(DateTimeOffset, JsonElement?)> { (OPENING.AddSeconds(10), CodexTotals("100", x, "30", "40", "50")) };
                if (second) events.Add((OPENING.AddSeconds(20), CodexTotals("101", "12", "33", "44", "55")));
                counters.AddCodexRollout(events, OPENING, CLOSING);
                break;
        }

        return counters.Reading();
    }

    private static JsonElement ClaudeResponse(string input, string? cacheRead, string thinking, string creation = "30", string output = "40") =>
        Object(("input_tokens", input), ("cache_read_input_tokens", cacheRead), ("cache_creation_input_tokens", creation),
               ("output_tokens", output), ("output_tokens_details", $$"""{"thinking_tokens":{{thinking}}}"""));

    private static JsonElement ClaudeEvent(string input, string? cacheRead, string creation, string output, string? cost) =>
        Object(("input_tokens", input), ("cache_read_tokens", cacheRead), ("cache_creation_tokens", creation),
               ("output_tokens", output), ("cost_usd", cost));

    private static JsonElement CodexEvent(string input, string? cached, string cacheWrite, string output, string reasoning) =>
        Object(("input_token_count", input), ("cached_token_count", cached), ("cache_write_token_count", cacheWrite),
               ("output_token_count", output), ("reasoning_token_count", reasoning));

    private static JsonElement CodexTotals(string input, string? cached, string cacheWrite, string output, string reasoning) =>
        Object(("input_tokens", input), ("cached_input_tokens", cached), ("cache_write_input_tokens", cacheWrite),
               ("output_tokens", output), ("reasoning_output_tokens", reasoning));

    /// <summary>A JSON object of raw JSON values; a field whose value is null is left out.</summary>
    /// <param name="fields">Each field's name and raw JSON text.</param>
    private static JsonElement Object(params (string Name, string? Raw)[] fields) =>
        JsonDocument.Parse("{" + string.Join(",", fields.Where(field => field.Raw is not null)
                                                         .Select(field => $"\"{field.Name}\":{field.Raw}")) + "}").RootElement;

    private static JsonElement? Field(JsonElement unit, string name) => unit.TryGetProperty(name, out var value) ? value : null;

    private static void AssertSourceFailed(UsageReading reading)
    {
        Assert.Null(reading.Tokens);
        Assert.True(reading.Incomplete);
        Assert.Equal(OrchestratorPhases.SOURCE_FAILED, reading.Missing);
    }

    /// <summary>One cell's expectation.</summary>
    /// <param name="Target">The counter under test: a token count, or the cost in US dollars; null when absent.</param>
    /// <param name="Input">The record's <c>inputTokens</c>; null when absent.</param>
    /// <param name="Incomplete">Whether the record is <c>usageIncomplete</c>.</param>
    private sealed record Row(decimal? Target, long? Input, bool Incomplete);
}
