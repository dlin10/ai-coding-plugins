using System.Text.Json;
using System.Text.Json.Serialization;
using CacheDetective.Events;
using CacheDetective.Graph;
using CacheDetective.Rules;

namespace CacheDetective.Configuration;

public sealed class WorkspaceConfiguration
{
    public const int CurrentVersion = 1;
    public const double DefaultBudgetSeconds = StalenessBudget.DefaultSeconds;

    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("root")]
    public string Root { get; init; } = ".";

    [JsonPropertyName("solutions")]
    public string[] Solutions { get; init; } = [];

    [JsonPropertyName("budgets")]
    public Dictionary<string, double> Budgets { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("databases")]
    public DatabaseConfiguration[]? Databases { get; init; }

    [JsonPropertyName("services")]
    public Dictionary<string, string>? Services { get; init; }

    [JsonPropertyName("events")]
    public EventRecognizerConfiguration[]? Events { get; init; }

    [JsonPropertyName("verify")]
    public JsonElement? Verify { get; init; }

    [JsonPropertyName("sensitive")]
    public JsonElement? Sensitive { get; init; }

    public double GetBudgetSeconds(string tableName) => StalenessBudget.GetSeconds(tableName, Budgets);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EventRecognizerConfiguration
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("publisher")] public string? Publisher { get; init; }
    [JsonPropertyName("publishers")] public string[]? Publishers { get; init; }
    [JsonPropertyName("methods")] public string[] Methods { get; init; } = ["Publish"];
    [JsonPropertyName("event_argument")] public int EventArgument { get; init; }
    [JsonPropertyName("consumer")] public string? Consumer { get; init; }
    [JsonPropertyName("arity")] public int Arity { get; init; } = 1;
    [JsonPropertyName("handle")] public string Handle { get; init; } = "Handle";
    [JsonPropertyName("handler_kind")] public string HandlerKind { get; init; } = "consumer";

    public EventRecognizer ToRecognizer(Confidence confidence, int? annotationId)
    {
        var hasPublisher = !string.IsNullOrWhiteSpace(Publisher);
        var hasPublishers = Publishers is { Length: > 0 };
        if (hasPublisher && hasPublishers)
            throw new InvalidDataException("events requires exactly one of publisher or publishers.");
        if (!hasPublisher && !hasPublishers && string.IsNullOrWhiteSpace(Consumer))
            throw new InvalidDataException("events requires a publisher or consumer.");
        return new EventRecognizer(Name ?? "event_api", Publishers ?? (hasPublisher ? [Publisher!] : []), Methods, EventArgument,
                                   Consumer ?? string.Empty, Arity, Handle, HandlerKind, confidence, annotationId);
    }
}
