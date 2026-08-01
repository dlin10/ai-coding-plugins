using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PlanForgeFlow;

internal static class TranscriptReader
{
    private const long MaxTranscriptBytes = 64 * 1024 * 1024;
    private const int MaxEnvironmentMessageBytes = 64 * 1024;

    internal sealed record Record(int Index, string Type, string? TurnId, string? Mode, string? Role, string? Phase, string? Text, JsonObject Raw, string Kind = "other", string? Id = null);
    internal sealed record Transcript(string Path, string SessionId, IReadOnlyList<Record> Records);

    public static IReadOnlyList<Record> Read(string path) => ReadDocument(path).Records;

    public static Transcript ReadDocument(string path)
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
        string? sessionId = null;
        var index = 0;
        foreach (var line in File.ReadLines(canonical))
        {
            if (string.IsNullOrWhiteSpace(line)) { index++; continue; }
            try
            {
                var raw = JsonNode.Parse(line)?.AsObject() ?? throw new FormatException("record is not an object");
                var type = raw["type"]?.GetValue<string>() ?? throw new FormatException("record type is missing");
                var payload = raw["payload"]?.AsObject() ?? throw new FormatException("record payload is missing");
                if (type == "session_meta")
                {
                    var observed = payload["id"]?.GetValue<string>() ?? payload["session_id"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(observed)) throw new FormatException("session metadata is malformed");
                    if (sessionId is not null && !string.Equals(sessionId, observed, StringComparison.Ordinal)) throw new FormatException("session metadata is ambiguous");
                    sessionId = observed;
                    result.Add(new Record(index, type, turn, mode, null, null, null, raw, "other"));
                }
                else if (type == "turn_context")
                {
                    turn = payload["turn_id"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(turn)) throw new FormatException("turn_context has no turn_id");
                    var candidate = payload["collaboration_mode"]?["mode"]?.GetValue<string>();
                    mode = candidate is "plan" or "default" ? candidate : "unknown";
                    result.Add(new Record(index, type, turn, mode, null, null, null, raw, "turn"));
                }
                else if (type == "response_item" && payload["type"]?.GetValue<string>() == "message")
                {
                    var role = payload["role"]?.GetValue<string>();
                    if (role is not ("user" or "assistant" or "developer")) throw new FormatException("message role is unsupported");
                    var content = payload["content"]?.AsArray() ?? throw new FormatException("message content is malformed");
                    if (content.Count == 0) throw new FormatException("message content is empty");
                    var text = new StringBuilder();
                    foreach (var part in content)
                    {
                        var item = part?.AsObject() ?? throw new FormatException("message content item is malformed");
                        var partType = item["type"]?.GetValue<string>();
                        if (partType is not ("input_text" or "output_text")) throw new FormatException("message content uses an unsupported schema");
                        text.Append(item["text"]?.GetValue<string>() ?? throw new FormatException("message content text is missing"));
                    }

                    result.Add(new Record(index, type, turn, mode, role, payload["phase"]?.GetValue<string>(), text.ToString(), raw, "message", payload["id"]?.GetValue<string>()));
                }
                else if (type == "response_item" && payload["type"]?.GetValue<string>() is { } activity &&
                         (activity.EndsWith("call", StringComparison.Ordinal) || activity.EndsWith("call_output", StringComparison.Ordinal) || activity == "tool_search_output"))
                {
                    result.Add(new Record(index, type, turn, mode, null, null, null, raw, "tool"));
                }
                else if (type == "response_item" && payload["type"]?.GetValue<string>() == "reasoning")
                {
                    result.Add(new Record(index, type, turn, mode, null, null, null, raw, "reasoning"));
                }
                else
                {
                    result.Add(new Record(index, type, turn, mode, null, null, null, raw, type == "response_item" ? "unsupported-activity" : "other"));
                }
            }
            catch (Exception error) when (error is not CliFailure)
            {
                throw new CliFailure("state", $"transcript line {index + 1} is malformed: {error.Message}", 3);
            }
            index++;
        }

        if (sessionId is null) throw new CliFailure("state", "transcript has no session metadata", 3);
        return new Transcript(canonical, sessionId, result);
    }

    public static string ModeForTurn(IReadOnlyList<Record> records, string turnId)
    {
        var matches = records.Where(item => item.Type == "turn_context" && item.TurnId == turnId).ToArray();
        return matches.Length == 1 ? matches[0].Mode ?? "unknown" : "unknown";
    }

    public static bool IsSyntheticEnvironmentMessage(Record record)
        => record.Kind == "message" && record.Role == "user" && record.Text is not null &&
           Encoding.UTF8.GetByteCount(record.Text) <= MaxEnvironmentMessageBytes &&
           Regex.IsMatch(record.Text, "^<environment_context>\\r?\\n[\\s\\S]*\\r?\\n</environment_context>\\s*$");
}