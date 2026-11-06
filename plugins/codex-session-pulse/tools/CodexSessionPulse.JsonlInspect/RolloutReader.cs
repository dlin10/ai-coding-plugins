using System.Text.Json;

namespace CodexSessionPulse.JsonlInspect;

/// <summary>
/// Reads a rollout once, front to back, without locking it against Codex, which keeps appending to
/// the file while a thread is loaded.
/// </summary>
internal static class RolloutReader
{
    private const string TimingOpen = "open";

    /// <summary>Collects one row per <c>token_count</c> event in <paramref name="path"/>.</summary>
    /// <param name="path">A plain <c>.jsonl</c> rollout file.</param>
    /// <returns>The rows in file order and the count of lines that were not valid JSON.</returns>
    public static ReadResult Read(string path)
    {
        var rows = new List<UsageRow>();
        var unparsed = 0;
        string? ownThreadId = null;
        string? lastMetaThreadId = null;
        long inheritedUntil = 0;
        string? model = null;
        string? turnId = null;
        var firstOpenRow = 0;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                unparsed++;
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                    continue;

                switch (Text(root, "type"), Text(payload, "type"))
                {
                    case ("session_meta", _):
                        lastMetaThreadId = Text(payload, "id");
                        if (ownThreadId is null)
                        {
                            ownThreadId = lastMetaThreadId;
                            inheritedUntil = Count(payload, "subagent_history_start_ordinal");
                        }
                        break;
                    case ("turn_context", _):
                        model = Text(payload, "model") ?? model;
                        break;
                    case ("event_msg", "task_started"):
                        turnId = Text(payload, "turn_id");
                        break;
                    case ("event_msg", "token_count"):
                        var threadId = Owner(Count(root, "ordinal"), ownThreadId, lastMetaThreadId, inheritedUntil);
                        rows.Add(new UsageRow(Text(root, "timestamp") ?? "-", threadId, model, turnId, Usage(payload), TimingOpen));
                        break;
                    case ("event_msg", "task_complete"):
                        var timing = payload.TryGetProperty("time_to_first_token_ms", out _) ? "ttft+duration"
                                   : payload.TryGetProperty("duration_ms", out _) ? "duration"
                                   : "none";
                        var completed = Text(payload, "turn_id");
                        for (var i = firstOpenRow; i < rows.Count; i++)
                            if (rows[i].TurnId == completed)
                                rows[i] = rows[i] with { Timing = timing };
                        firstOpenRow = rows.Count;
                        break;
                }
            }
        }

        return new ReadResult(rows, unparsed);
    }

    /// <summary>
    /// Picks the thread a <c>token_count</c> belongs to. A subagent rollout whose first
    /// <c>session_meta</c> carries <c>subagent_history_start_ordinal</c> starts with a copy of its
    /// parent's history, headed by the parent's own <c>session_meta</c>; only records from that
    /// ordinal on are the subagent's. Without that field the nearest preceding <c>session_meta</c> owns it.
    /// </summary>
    /// <param name="ordinal">The record's <c>ordinal</c>, or 0 when it has none.</param>
    /// <param name="ownThreadId">The <c>id</c> of the file's first <c>session_meta</c>.</param>
    /// <param name="lastMetaThreadId">The <c>id</c> of the nearest preceding <c>session_meta</c>.</param>
    /// <param name="inheritedUntil">The first meta's <c>subagent_history_start_ordinal</c>, or 0.</param>
    /// <returns>The owning thread id, or <see langword="null"/> before any <c>session_meta</c>.</returns>
    private static string? Owner(long ordinal, string? ownThreadId, string? lastMetaThreadId, long inheritedUntil)
        => inheritedUntil > 0 && ordinal >= inheritedUntil ? ownThreadId : lastMetaThreadId;

    /// <summary>Reads <c>info.last_token_usage</c>, the counts of the one request this event reports.</summary>
    /// <param name="payload">The <c>token_count</c> payload.</param>
    /// <returns>The counts, or <see langword="null"/> when <c>info</c> is null or has no <c>last_token_usage</c>.</returns>
    private static TokenUsage? Usage(JsonElement payload)
    {
        if (!payload.TryGetProperty("info", out var info)
         || info.ValueKind != JsonValueKind.Object
         || !info.TryGetProperty("last_token_usage", out var last)
         || last.ValueKind != JsonValueKind.Object)
            return null;

        return new TokenUsage(Count(last, "input_tokens"),
                              Count(last, "cached_input_tokens"),
                              Count(last, "cache_write_input_tokens"),
                              Count(last, "output_tokens"));
    }

    /// <summary>Reads an integer property, treating a missing or non-numeric value as zero.</summary>
    /// <param name="element">The object to read from.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value.</returns>
    private static long Count(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) ? count : 0;

    /// <summary>Reads a string property.</summary>
    /// <param name="element">The object to read from.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The string, or <see langword="null"/> when it is missing or not a string.</returns>
    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
