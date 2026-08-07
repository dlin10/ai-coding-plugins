using System.Text;
using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Serialization;

namespace PlanForgeFlow.Codex;

internal static class TranscriptReader
{
    private const long MaxTranscriptBytes = 64 * 1024 * 1024;

    internal sealed record Record(string Type, string? TurnId, string? Mode, string? Role, string? Phase, string? Text, string Kind = "other");

    public static IReadOnlyList<Record> ReadDocument(string path)
    {
        if (!File.Exists(path)) throw new CliFailure("state", $"transcript is missing: {path}", 3);
        var canonical = Path.GetFullPath(path);
        if ((File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", "transcript must not be a symlink or junction", 3);
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(canonical)!); parent is not null && parent.Exists; parent = parent.Parent)
        {
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", "transcript path contains a symlinked directory", 3);
        }
        var length = new FileInfo(canonical).Length;
        if (length <= 0 || length > MaxTranscriptBytes) throw new CliFailure("state", "transcript size is invalid", 3);
        var result = new List<Record>();
        string? turn = null;
        var mode = "unknown";
        var index = 0;
        using var stream = new FileStream(canonical,
                                          FileMode.Open,
                                          FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) { index++; continue; }
            try
            {
                var raw = JsonSerializer.Deserialize(line, CodexJsonContext.Default.TranscriptEnvelope) ?? throw new JsonException("JSON value is null");
                var type = raw.Type ?? throw new FormatException("record type is missing");
                var payload = RequireObject(raw.Payload, "record payload is missing");
                if (type == "session_meta")
                {
                    result.Add(new Record(type, turn, mode, null, null, null));
                }
                else if (type == "turn_context")
                {
                    turn = RequireString(payload, "turn_id", "turn_context has no turn_id");
                    if (string.IsNullOrWhiteSpace(turn)) throw new FormatException("turn_context has no turn_id");
                    var candidate = payload.TryGetProperty("collaboration_mode", out var collaboration) && collaboration.ValueKind == JsonValueKind.Object
                                    ? OptionalString(collaboration, "mode")
                                    : null;
                    mode = candidate is "plan" or "default" ? candidate : "unknown";
                    result.Add(new Record(type, turn, mode, null, null, null, "turn"));
                }
                else if (type == "response_item" && OptionalString(payload, "type") == "message")
                {
                    var role = OptionalString(payload, "role");
                    if (role is not ("user" or "assistant" or "developer")) throw new FormatException("message role is unsupported");
                    if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) throw new FormatException("message content is malformed");
                    if (content.GetArrayLength() == 0) throw new FormatException("message content is empty");
                    var text = new StringBuilder();
                    foreach (var part in content.EnumerateArray())
                    {
                        var item = RequireObject(part, "message content item is malformed");
                        var partType = OptionalString(item, "type");
                        if (partType is not ("input_text" or "output_text")) throw new FormatException("message content uses an unsupported schema");
                        text.Append(RequireString(item, "text", "message content text is missing"));
                    }

                    result.Add(new Record(type, turn, mode, role, OptionalString(payload, "phase"), text.ToString(), "message"));
                }
                else if (type == "response_item" && OptionalString(payload, "type") is { } activity &&
                         (activity.EndsWith("call", StringComparison.Ordinal) || activity.EndsWith("call_output", StringComparison.Ordinal) || activity == "tool_search_output"))
                {
                    result.Add(new Record(type, turn, mode, null, null, null, "tool"));
                }
                else if (type == "response_item" && OptionalString(payload, "type") == "reasoning")
                {
                    result.Add(new Record(type, turn, mode, null, null, null, "reasoning"));
                }
                else
                {
                    result.Add(new Record(type, turn, mode, null, null, null, type == "response_item" ? "unsupported-activity" : "other"));
                }
            }
            catch (Exception error) when (error is not CliFailure)
            {
                throw new CliFailure("state", $"transcript line {index + 1} is malformed: {error.Message}", 3);
            }
            index++;
        }

        return result;
    }

    public static string? LatestPlan(IReadOnlyList<Record> records)
    {
        var candidate = records.LastOrDefault(record =>
            record.Kind == "message" && record.Role == "assistant" && record.Phase == "final_answer" && record.Mode == "plan" &&
            record.Text is not null && record.Text.TrimEnd().StartsWith("<proposed_plan>\n", StringComparison.Ordinal) && record.Text.TrimEnd().EndsWith("</proposed_plan>", StringComparison.Ordinal));
        if (candidate?.Text is null) return null;
        var text = candidate.Text.TrimEnd();
        return text["<proposed_plan>\n".Length..^"</proposed_plan>".Length];
    }

    private static JsonElement RequireObject(JsonElement value, string error)
        => value.ValueKind == JsonValueKind.Object ? value : throw new FormatException(error);

    private static string RequireString(JsonElement value, string property, string error)
        => OptionalString(value, property) ?? throw new FormatException(error);

    private static string? OptionalString(JsonElement value, string property)
        => value.TryGetProperty(property, out var candidate) && candidate.ValueKind == JsonValueKind.String ? candidate.GetString() : null;
}
