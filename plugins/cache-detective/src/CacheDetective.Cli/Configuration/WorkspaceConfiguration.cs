using System.Text.Json;
using System.Text.Json.Serialization;
using CacheDetective.Caching;
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

    /// <summary>Caching libraries this workspace declares, beside <see cref="Events"/>. Until this
    /// existed a cache recognizer could arrive only through <c>annotate</c> and died with the session,
    /// which made a corpus keyed on an object unrepeatable; see <c>docs/adr/0016</c>.</summary>
    [JsonPropertyName("caches")]
    public CacheRecognizerConfiguration[]? Caches { get; init; }

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

/// <summary>
/// One declaration of a caching library, and the only one there is. The workspace's <c>caches</c>
/// section, an <c>annotate</c> resolution of kind <c>cache_api</c> and the <c>--recognizers</c> file
/// all deserialize into this: three readers of one schema, so a file written for one is accepted by
/// the others and a "before" run and an "after" run can read the same bytes.
/// <para>Unknown members are refused rather than ignored, as <see cref="VerifyConfiguration"/> refuses
/// them: a field this schema does not know is a setting the author believes is doing something.</para>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CacheRecognizerConfiguration
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("store")] public string? Store { get; init; }
    [JsonPropertyName("methods")] public CacheMethodConfiguration[]? Methods { get; init; }

    /// <summary>Optional, and absent for every library that passes its key as a string.</summary>
    [JsonPropertyName("key_object")] public KeyObjectConfiguration? KeyObject { get; init; }

    public CacheRecognizer ToRecognizer(Confidence confidence, int? annotationId)
    {
        if (string.IsNullOrWhiteSpace(Type))
            throw new InvalidDataException("caches requires a type naming the caching interface or class.");
        if (string.IsNullOrWhiteSpace(Store))
            throw new InvalidDataException($"caches['{Type}'] requires a store name.");
        if (Methods is not { Length: > 0 })
            throw new InvalidDataException($"caches['{Type}'] requires at least one method; a type with no methods recognizes nothing.");
        return new CacheRecognizer(Type, Store, Methods.Select(method => method.ToRecognizer(Type)).ToArray(),
                                   confidence, annotationId, KeyObject?.ToRecognizer(Type));
    }

    /// <summary>Both spellings of a semantic are accepted: the snake_case <c>remove_by_prefix</c> that
    /// <c>annotate</c> documents, and the bare enum name that the committed <c>--recognizers</c> files
    /// already carry. One shared parse cannot break either without breaking a file that parses today.
    /// </summary>
    internal static bool TryParseSemantic(string? value, out CacheSemantic semantic)
    {
        semantic = default;
        // Enum.TryParse also accepts the underlying number, which would turn a typo into a semantic.
        return !string.IsNullOrWhiteSpace(value) && char.IsLetter(value[0]) &&
               Enum.TryParse(value.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out semantic);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CacheMethodConfiguration
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("semantic")] public string? Semantic { get; init; }

    // Nullable so that "absent" and "argument zero" stay distinguishable — zero is the commonest key
    // position, and a value type cannot carry absence. Same reason as EventRecognizerConfiguration.ConfiguredArity.
    [JsonPropertyName("key_arg")] public int? KeyArgument { get; init; }
    [JsonPropertyName("ttl_arg")] public int? TtlArgument { get; init; }
    [JsonPropertyName("tags_arg")] public int? TagsArgument { get; init; }

    internal CacheMethodRecognizer ToRecognizer(string type)
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidDataException($"caches['{type}'] has a method with no name.");
        if (!CacheRecognizerConfiguration.TryParseSemantic(Semantic, out var semantic))
            throw new InvalidDataException($"caches['{type}'].{Name} has semantic '{Semantic}'. It must be one of " +
                                           "get, set, remove, remove_by_tag, remove_by_prefix, increment, expire, lock.");
        if (KeyArgument is not >= 0)
            throw new InvalidDataException($"caches['{type}'].{Name} requires key_arg, the zero-based position of the key argument.");
        if (TtlArgument is < 0 || TagsArgument is < 0)
            throw new InvalidDataException($"caches['{type}'].{Name} has a negative argument position.");
        return new CacheMethodRecognizer(Name, semantic, KeyArgument.Value, TtlArgument, TagsArgument);
    }
}

/// <summary>A key that is an object: where its template literal lives, and where it meets its arguments.
/// Carried and validated here; what the folder does with it is <c>docs/adr/0016</c>'s work.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class KeyObjectConfiguration
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("template_arg")] public int? TemplateArgument { get; init; }
    [JsonPropertyName("factories")] public KeyObjectFactoryConfiguration[]? Factories { get; init; }

    internal KeyObjectRecognizer ToRecognizer(string cacheType)
    {
        if (string.IsNullOrWhiteSpace(Type))
            throw new InvalidDataException($"caches['{cacheType}'].key_object requires a type naming the key object.");
        if (TemplateArgument is not >= 0)
            throw new InvalidDataException($"caches['{cacheType}'].key_object requires template_arg, the zero-based constructor " +
                                           "argument the template literal comes from.");
        if (Factories is not { Length: > 0 })
            throw new InvalidDataException($"caches['{cacheType}'].key_object requires at least one factory; without one the " +
                                           "template never meets its arguments.");
        return new KeyObjectRecognizer(Type, TemplateArgument.Value, Factories.Select(factory => factory.ToRecognizer(cacheType)).ToArray());
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class KeyObjectFactoryConfiguration
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("methods")] public string[]? Methods { get; init; }
    [JsonPropertyName("key_arg")] public int? KeyArgument { get; init; }
    [JsonPropertyName("args_arg")] public int? ArgumentsArgument { get; init; }

    internal KeyObjectFactory ToRecognizer(string cacheType)
    {
        if (string.IsNullOrWhiteSpace(Type))
            throw new InvalidDataException($"caches['{cacheType}'].key_object.factories requires a type declaring the factory.");
        if (Methods is not { Length: > 0 } || Methods.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException($"caches['{cacheType}'].key_object.factories['{Type}'] requires at least one method name.");
        if (KeyArgument is not >= 0 || ArgumentsArgument is not >= 0)
            throw new InvalidDataException($"caches['{cacheType}'].key_object.factories['{Type}'] requires key_arg and args_arg, the " +
                                           "zero-based positions of the key object and of the argument array substituted into it.");
        if (KeyArgument == ArgumentsArgument)
            throw new InvalidDataException($"caches['{cacheType}'].key_object.factories['{Type}'] gives key_arg and args_arg the same " +
                                           "position; one argument cannot be both the key object and the arguments put into it.");
        return new KeyObjectFactory(Type, Methods, KeyArgument.Value, ArgumentsArgument.Value);
    }
}
