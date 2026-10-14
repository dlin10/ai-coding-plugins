using System.Globalization;
using System.Text.Json;

namespace PlanForge.Orchestration;

/// <summary>
/// The one normalization of Orchestrator counters (R13), for all four formats: Claude Code's
/// transcript responses, Codex's rollout totals, and both hosts' export events. A phase's counter is
/// the sum of its valid values over the usage units within the phase; no source sums or validates a
/// counter on its own.
/// </summary>
/// <remarks>
/// <para>
/// A valid count is a JSON integer, or a string of decimal digits, from 0 to 2^63−1; a valid cost is
/// any finite JSON number of at least 0, summed as a double. A value that is present but not valid —
/// negative, fractional, out of range, a boolean, an object, an array, a string that is not decimal
/// digits for a count or any string for a cost, or JSON null — leaves that counter absent and the
/// reading incomplete, while the other counters still sum; so does a sum past 2^63−1, or a cost sum
/// that is not finite. A field that is not there, or whose parent object is missing or null, is
/// absent, not invalid. Claude's input is formed per unit from its three components: a unit missing
/// any of them leaves the input absent and the reading incomplete.
/// </para>
/// <para>
/// A phase with at least one usage unit leaves a counter that none of its units carries absent; a
/// phase with no usage unit at all gets zero for every counter a unit outside it carries. The Codex
/// rollout keeps R8's own rule instead: a counter some event of the rollout carries but none within
/// the phase counts zero.
/// </para>
/// </remarks>
internal sealed class UsageCounters
{
    private const int INPUT = 0;
    private const int CACHE_READ = 1;
    private const int CACHE_CREATION = 2;
    private const int OUTPUT = 3;
    private const int REASONING = 4;

    /// <summary>Codex rollout's <c>total_token_usage</c> fields, in counter order.</summary>
    private static readonly string[] CODEX_ROLLOUT = ["input_tokens", "cached_input_tokens", "cache_write_input_tokens",
                                                      "output_tokens", "reasoning_output_tokens"];

    private readonly long?[] _sums = new long?[5];
    private readonly bool[] _carried = new bool[5];
    private readonly bool[] _rolloutCarried = new bool[5];
    private readonly bool[] _invalid = new bool[5];
    private double? _cost;
    private bool _costCarried;
    private bool _costInvalid;
    private bool _incomplete;
    private int _unitsInPhase;

    /// <summary>Adds one Claude Code transcript response, by the <c>usage</c> of its message.</summary>
    /// <param name="usage">The message's <c>usage</c> object.</param>
    /// <param name="inPhase">Whether the response belongs to the phase; one outside it only shows which counters exist.</param>
    public void AddClaudeResponse(JsonElement usage, bool inPhase) =>
        AddClaude(Field(usage, "input_tokens"), Field(usage, "cache_read_input_tokens"), Field(usage, "cache_creation_input_tokens"),
                  Field(usage, "output_tokens"), Field(Field(usage, "output_tokens_details"), "thinking_tokens"), null, inPhase);

    /// <summary>Adds one Claude Code <c>claude_code.api_request</c> export event.</summary>
    /// <param name="attribute">The event's attribute value by name, already unwrapped; null when absent.</param>
    /// <param name="inPhase">Whether the event belongs to the phase; one outside it only shows which counters exist.</param>
    public void AddClaudeExportEvent(Func<string, JsonElement?> attribute, bool inPhase) =>
        AddClaude(attribute("input_tokens"), attribute("cache_read_tokens"), attribute("cache_creation_tokens"),
                  attribute("output_tokens"), null, attribute("cost_usd"), inPhase);

    /// <summary>Adds one Codex <c>codex.sse_event</c> export event of kind <c>response.completed</c>.</summary>
    /// <param name="attribute">The event's attribute value by name, already unwrapped; null when absent.</param>
    /// <param name="inPhase">Whether the event belongs to the phase; one outside it only shows which counters exist.</param>
    public void AddCodexExportEvent(Func<string, JsonElement?> attribute, bool inPhase)
    {
        if (inPhase) _unitsInPhase++;

        // Codex reports cached tokens as a subset of its input, so input is taken as given.
        Add(INPUT, attribute("input_token_count"), inPhase);
        Add(CACHE_READ, attribute("cached_token_count"), inPhase);
        Add(CACHE_CREATION, attribute("cache_write_token_count"), inPhase);
        Add(OUTPUT, attribute("output_token_count"), inPhase);
        Add(REASONING, attribute("reasoning_token_count"), inPhase);
    }

    /// <summary>
    /// Adds a Codex rollout's <c>token_count</c> totals (R8): each counter on its own, the sum over its
    /// events before the closing that follow its last event before the opening of what its total grew
    /// by since its previous event. A total below the previous one counts from zero and makes the
    /// reading incomplete; with no event before the opening, the first counts from zero. An event whose
    /// value for a counter is absent or not valid neither counts for it nor moves its baseline; one
    /// within the phase whose value is not valid also leaves the counter absent and the reading incomplete.
    /// </summary>
    /// <param name="events">The events in file order: when each happened, and its <c>total_token_usage</c>, if any.</param>
    /// <param name="from">The phase's opening, included.</param>
    /// <param name="to">The phase's closing, excluded.</param>
    public void AddCodexRollout(IReadOnlyList<(DateTimeOffset At, JsonElement? Totals)> events, DateTimeOffset from, DateTimeOffset to)
    {
        for (var counter = 0; counter < CODEX_ROLLOUT.Length; counter++)
        {
            long? previous = null;
            foreach (var (at, totals) in events)
            {
                if (Field(totals, CODEX_ROLLOUT[counter]) is not { } value) continue;

                var inPhase = at >= from && at < to;
                if (!TryCount(value, out var total))
                {
                    if (inPhase) Invalidate(counter);
                    continue;
                }

                _rolloutCarried[counter] = true;
                if (at >= to) continue;

                if (inPhase)
                {
                    if (previous is { } baseline && total >= baseline)
                    {
                        Sum(counter, total - baseline);
                    }
                    else
                    {
                        if (previous is not null) _incomplete = true;
                        Sum(counter, total);
                    }
                }

                previous = total;
            }
        }
    }

    /// <summary>The counters as a Usage source answers them; <c>source_failed</c> when every counter is absent.</summary>
    public UsageReading Reading()
    {
        var tokens = new OrchestratorTokens(Final(INPUT), Final(CACHE_READ), Final(CACHE_CREATION), Final(OUTPUT), Final(REASONING),
                                            _costInvalid ? null : _cost ?? (_unitsInPhase == 0 && _costCarried ? 0 : null));
        return tokens == new OrchestratorTokens()
            ? new UsageReading(null, _incomplete, OrchestratorPhases.SOURCE_FAILED, OrchestratorPhases.SOURCE_FAILED)
            : new UsageReading(tokens, _incomplete);
    }

    /// <summary>Whether <paramref name="value"/> is a valid count: a JSON integer or a string of decimal digits, 0 to 2^63−1.</summary>
    /// <param name="value">The raw value.</param>
    /// <param name="count">The count, when valid.</param>
    internal static bool TryCount(JsonElement value, out long count)
    {
        count = 0;
        return value.ValueKind switch
        {
            // TryGetInt64 refuses a fraction or an exponent, so only integers pass.
            JsonValueKind.Number => value.TryGetInt64(out count) && count >= 0,
            JsonValueKind.String => value.GetString() is { Length: > 0 } text && text.All(char.IsAsciiDigit)
                                    && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out count),
            _ => false,
        };
    }

    /// <summary>Whether <paramref name="value"/> is a valid cost: a finite JSON number of at least 0.</summary>
    /// <param name="value">The raw value.</param>
    /// <param name="cost">The cost in US dollars, when valid.</param>
    internal static bool TryCost(JsonElement value, out double cost)
    {
        cost = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out cost) && double.IsFinite(cost) && cost >= 0;
    }

    /// <summary>
    /// One Claude unit: its input formed from its three components, which a unit missing any of them
    /// leaves absent and incomplete.
    /// </summary>
    /// <param name="input">Uncached input tokens.</param>
    /// <param name="cacheRead">Tokens read from the prompt cache.</param>
    /// <param name="cacheCreation">Tokens written to the prompt cache.</param>
    /// <param name="output">Output tokens.</param>
    /// <param name="reasoning">Thinking tokens, where the format reports them.</param>
    /// <param name="cost">Cost in US dollars, where the format reports it.</param>
    /// <param name="inPhase">Whether the unit belongs to the phase.</param>
    private void AddClaude(JsonElement? input, JsonElement? cacheRead, JsonElement? cacheCreation, JsonElement? output,
                           JsonElement? reasoning, JsonElement? cost, bool inPhase)
    {
        if (inPhase) _unitsInPhase++;

        var components = new[] { input, cacheRead, cacheCreation };
        if (components.All(component => component is not null))
        {
            long sum = 0;
            var valid = true;
            foreach (var component in components)
            {
                if (!TryCount(component!.Value, out var value))
                {
                    valid = false;
                    break;
                }

                try { sum = checked(sum + value); }
                catch (OverflowException) { valid = false; break; }
            }

            if (valid) Add(INPUT, sum, inPhase);
            else if (inPhase) Invalidate(INPUT);
        }
        else if (inPhase)
        {
            // Missing one, two or all three components: the unit's input cannot be formed.
            Invalidate(INPUT);
        }

        Add(CACHE_READ, cacheRead, inPhase);
        Add(CACHE_CREATION, cacheCreation, inPhase);
        Add(OUTPUT, output, inPhase);
        Add(REASONING, reasoning, inPhase);
        AddCost(cost, inPhase);
    }

    private void Add(int counter, JsonElement? value, bool inPhase)
    {
        if (value is not { } raw) return;
        if (TryCount(raw, out var count)) Add(counter, count, inPhase);
        else if (inPhase) Invalidate(counter);
    }

    private void Add(int counter, long count, bool inPhase)
    {
        _carried[counter] = true;
        if (inPhase) Sum(counter, count);
    }

    private void AddCost(JsonElement? value, bool inPhase)
    {
        if (value is not { } raw) return;
        if (!TryCost(raw, out var cost))
        {
            if (inPhase) _costInvalid = _incomplete = true;
            return;
        }

        _costCarried = true;
        if (!inPhase || _costInvalid) return;
        _cost = (_cost ?? 0) + cost;
        if (!double.IsFinite(_cost.Value)) _costInvalid = _incomplete = true;
    }

    private void Sum(int counter, long value)
    {
        if (_invalid[counter]) return;
        try { _sums[counter] = checked((_sums[counter] ?? 0) + value); }
        catch (OverflowException) { Invalidate(counter); }
    }

    private void Invalidate(int counter)
    {
        _invalid[counter] = true;
        _incomplete = true;
    }

    /// <summary>
    /// The counter as the record carries it: absent when invalid; else its sum; else zero where the
    /// rollout carries it, or where the phase has no unit and one outside it carries it; else absent.
    /// </summary>
    /// <param name="counter">The counter.</param>
    private long? Final(int counter) =>
        _invalid[counter] ? null
        : _sums[counter] ?? (_rolloutCarried[counter] || _unitsInPhase == 0 && _carried[counter] ? 0 : null);

    /// <summary>
    /// A property of an object, JSON null included, since a present null is an invalid value; null
    /// when the property is not there or the object is missing, null or not an object.
    /// </summary>
    /// <param name="element">The object, if any.</param>
    /// <param name="name">The property's name.</param>
    private static JsonElement? Field(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) ? property : null;
}
