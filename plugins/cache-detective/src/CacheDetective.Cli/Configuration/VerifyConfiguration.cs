using System.Text.Json.Serialization;

namespace CacheDetective.Configuration;

/// <summary>
/// What runtime verification is allowed to touch. Both connections are <c>env:</c> references and never
/// the strings themselves, exactly as <see cref="DatabaseConfiguration.Connection"/> is, because
/// <c>workspace.json</c> is committed. Unknown members are refused rather than ignored: a field this
/// schema does not know is a setting the user believes is doing something.
/// <para>Without <c>tables</c> nothing can be refuted. Refutation is field agreement between the cached
/// value and the row it came from, and that needs the table's key column and where the key's value comes
/// from; age alone never refutes. See <c>docs/adr/0012</c>.</para>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class VerifyConfiguration
{
    private const string ENVIRONMENT_PREFIX = "env:";

    /// <summary>Default stores. A key in process memory belongs to a process this tool is not inside, so
    /// there is nothing to read; the two named here are the ones a scan can actually reach.</summary>
    public static readonly string[] DefaultStores = ["redis", "distributed"];

    public const double DefaultClockMarginSeconds = 60;

    private readonly string[]? _stores;
    private readonly string? _keyPrefix;

    /// <summary>An <c>env:NAME</c> reference to the environment variable holding the Redis connection.</summary>
    [JsonPropertyName("redis")]
    public string? Redis { get; init; }

    /// <summary>An <c>env:NAME</c> reference to the environment variable holding the database connection
    /// verification reads rows through.</summary>
    [JsonPropertyName("database")]
    public string? Database { get; init; }

    /// <summary>Whether the workspace consents to verification running at all. It is a permission and not
    /// a trigger: whether a given scan verifies is the skill's decision, made per run.</summary>
    [JsonPropertyName("auto")]
    public bool Auto { get; init; }

    // The three settings below default through their getters rather than through a property initializer,
    // because the source-generated deserializer builds the object without running initializers: a field
    // absent from the file would otherwise arrive as null or zero instead of its documented default.
    [JsonPropertyName("stores")]
    public string[] Stores { get => _stores ?? DefaultStores; init => _stores = value; }

    [JsonPropertyName("keyPrefix")]
    public string KeyPrefix { get => _keyPrefix ?? string.Empty; init => _keyPrefix = value; }

    /// <summary>What the file said, if it said anything. A value type cannot carry "absent" through the
    /// deserializer the way a reference type can — zero and unset are the same bit pattern — so the
    /// setting and the value it produces are separate members.</summary>
    [JsonPropertyName("clockMarginSeconds")]
    public double? ConfiguredClockMarginSeconds { get; init; }

    /// <summary>How far the cache's clock and the database's clock are allowed to disagree before a
    /// comparison of moments between them means nothing.</summary>
    [JsonIgnore]
    public double ClockMarginSeconds => ConfiguredClockMarginSeconds ?? DefaultClockMarginSeconds;

    /// <summary>Keyed by <c>schema.name</c>. Each entry says which column identifies a row and where the
    /// key's value comes from, and neither half is any use alone.</summary>
    [JsonPropertyName("tables")]
    public Dictionary<string, VerifyTableConfiguration>? Tables { get; init; }

    /// <summary>Whether a key in this store is one verification reads. Matching ignores case because a
    /// store name is a label in a configuration file, not an identifier.</summary>
    public bool Verifies(string store) => Stores.Contains(store, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the Redis connection out of the environment. Turning an <c>env:</c> reference into a
    /// real connection string is the CLI's job, never the core's; see <c>docs/adr/0002</c>.</summary>
    public string ResolveRedis() => Resolve(Redis, "redis");

    public string ResolveDatabase() => Resolve(Database, "database");

    /// <summary>Refuses a section this phase cannot honour, saying which rule it broke.</summary>
    internal void EnsureSupported()
    {
        EnsureReference(Redis, "redis", "CD_VERIFY_REDIS");
        EnsureReference(Database, "database", "CD_VERIFY_DB");

        // Two entries differing only in case name one table, and the reading side matches names without
        // regard to case because they come out of the code. Refusing the pair here is what keeps that
        // insensitive lookup from silently dropping one of them.
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, entry) in Tables ?? [])
        {
            if (!byName.TryAdd(table, table))
            {
                throw new InvalidDataException($"verify.tables names '{byName[table]}' and '{table}', which differ only in " +
                                               "case and are the same table. Declare it once.");
            }

            entry.EnsureSupported(table);
        }
    }

    private static void EnsureReference(string? reference, string field, string example)
    {
        if (reference is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new InvalidDataException($"verify.{field} is empty. It must reference an environment variable, as in " +
                                           $"\"{field}\": \"env:{example}\".");
        }

        if (!reference.StartsWith(ENVIRONMENT_PREFIX, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"verify.{field} carries a connection string instead of an 'env:' reference. " +
                                           "workspace.json is committed, so it must name an environment variable, as in " +
                                           $"\"{field}\": \"env:{example}\".");
        }

        if (reference.Length == ENVIRONMENT_PREFIX.Length)
        {
            throw new InvalidDataException($"verify.{field} has an 'env:' reference that names no variable.");
        }
    }

    private string Resolve(string? reference, string field)
    {
        EnsureSupported();
        if (reference is null)
        {
            throw new InvalidOperationException($"verify.{field} is not configured, so there is no connection to read.");
        }

        var variable = reference[ENVIRONMENT_PREFIX.Length..];
        return Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
                   ? value
                   : throw new InvalidOperationException($"Environment variable '{variable}' is not set; it holds the {field} " +
                                                         "connection string used by verification.");
    }
}

/// <summary>One table verification can read a row from. Both halves are required together: the column
/// alone says which row without saying which value to look for, and the source alone says which value
/// without saying where to look for it.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class VerifyTableConfiguration
{
    [JsonPropertyName("key")] public string? Key { get; init; }

    [JsonPropertyName("from")] public string? From { get; init; }

    internal void EnsureSupported(string table)
    {
        var hasKey = !string.IsNullOrWhiteSpace(Key);
        var hasFrom = !string.IsNullOrWhiteSpace(From);
        if (hasKey == hasFrom)
        {
            if (!hasKey)
            {
                throw new InvalidDataException($"verify.tables['{table}'] declares neither 'key' nor 'from'. Both are required " +
                                               "together, as in { \"key\": \"Id\", \"from\": \"id\" }.");
            }

            return;
        }

        var missing = hasKey ? "from" : "key";
        var present = hasKey ? "key" : "from";
        throw new InvalidDataException($"verify.tables['{table}'] declares '{present}' without '{missing}'. Both are required " +
                                       "together: one names the column that identifies a row, the other where the key's value " +
                                       "comes from, and neither is any use alone.");
    }
}
