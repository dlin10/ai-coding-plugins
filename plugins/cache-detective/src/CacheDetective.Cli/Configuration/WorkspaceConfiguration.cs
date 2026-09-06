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
    public VerifyConfiguration? Verify { get; init; }

    /// <summary>Extra field-name masks, added to <see cref="SensitiveFields.Default"/> and never
    /// replacing them.</summary>
    [JsonPropertyName("sensitive")]
    public string[]? Sensitive { get; init; }

    public double GetBudgetSeconds(string tableName) => StalenessBudget.GetSeconds(tableName, Budgets);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EventRecognizerConfiguration
{
    private const int DEFAULT_ARITY = 1;
    private const string DEFAULT_HANDLE = "Handle";
    private const string DEFAULT_HANDLER_KIND = "consumer";
    private static readonly string[] DEFAULT_METHODS = ["Publish"];

    // Methods, Arity, Handle and HandlerKind default through their getters rather than through a property
    // initializer, because the source-generated deserializer builds the object without running
    // initializers: a field absent from the file would otherwise arrive as null or zero instead of its
    // documented default. See VerifyConfiguration, which carries the same pattern for the same reason.
    private readonly string[]? _methods;
    private readonly string? _handle;
    private readonly string? _handlerKind;

    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("publisher")] public string? Publisher { get; init; }
    [JsonPropertyName("publishers")] public string[]? Publishers { get; init; }
    [JsonPropertyName("methods")] public string[] Methods { get => _methods ?? DEFAULT_METHODS; init => _methods = value; }
    [JsonPropertyName("event_argument")] public int EventArgument { get; init; }
    [JsonPropertyName("consumer")] public string? Consumer { get; init; }

    /// <summary>What the file said, if it said anything. A value type cannot carry "absent" through the
    /// deserializer the way a reference type can — zero and unset are the same bit pattern — so the
    /// setting and the value it produces are separate members.</summary>
    [JsonPropertyName("arity")] public int? ConfiguredArity { get; init; }

    /// <summary>How many type arguments the consumer interface takes.</summary>
    [JsonIgnore] public int Arity => ConfiguredArity ?? DEFAULT_ARITY;

    [JsonPropertyName("handle")] public string Handle { get => _handle ?? DEFAULT_HANDLE; init => _handle = value; }
    [JsonPropertyName("handler_kind")] public string HandlerKind { get => _handlerKind ?? DEFAULT_HANDLER_KIND; init => _handlerKind = value; }

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
