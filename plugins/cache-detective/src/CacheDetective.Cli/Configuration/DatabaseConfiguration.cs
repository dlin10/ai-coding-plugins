using System.Text.Json.Serialization;

namespace CacheDetective.Configuration;

/// <summary>
/// One database the workspace scans. The connection is an <c>env:</c> reference and never the connection
/// string itself, because <c>workspace.json</c> is committed. Unknown members are refused rather than
/// ignored: a field this schema does not know is a configuration the user believes is doing something.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DatabaseConfiguration
{
    private const string ENVIRONMENT_PREFIX = "env:";
    private static readonly string[] SUPPORTED_PROVIDERS = ["sqlserver", "mssql"];

    [JsonPropertyName("name")] public string? Name { get; init; }

    /// <summary>An <c>env:NAME</c> reference to the environment variable holding the connection string.</summary>
    [JsonPropertyName("connection")]
    public string? Connection { get; init; }

    /// <summary>Optional, and present in the schema for one reason: so a configuration naming another
    /// database engine can be refused by name instead of as an unknown field.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    /// <summary>Reads the connection string out of the environment. Turning an <c>env:</c> reference into a
    /// real connection string is the CLI's job, never the core's; see <c>docs/adr/0002</c>.</summary>
    public string ResolveConnectionString()
    {
        EnsureSupported();
        var variable = Connection![ENVIRONMENT_PREFIX.Length..];
        return Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
                   ? value
                   : throw new InvalidOperationException($"Environment variable '{variable}' is not set; it holds the connection string for " +
                                                         $"database '{Describe()}'.");
    }

    /// <summary>Refuses a record this phase cannot honour, saying which rule it broke.</summary>
    internal void EnsureSupported()
    {
        if (Provider is not null && !SUPPORTED_PROVIDERS.Contains(Normalize(Provider), StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Database '{Describe()}' names provider '{Provider}'. cache-detective supports " +
                                           "SQL Server only; no other database engine is in scope.");
        }

        // The name is how index_database selects a database and what every vertex of that half of the
        // graph is stamped with, so a nameless record cannot be honoured: there would be nothing to ask
        // for and nothing truthful to label the catalogue with.
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidDataException("A database record has no 'name'. index_database selects a database by name, and the " +
                                           "name labels everything read from it, as in \"name\": \"shop\".");
        }

        if (string.IsNullOrWhiteSpace(Connection))
        {
            throw new InvalidDataException($"Database '{Describe()}' has no 'connection'. It must reference an environment variable, " +
                                           "as in \"connection\": \"env:CD_SHOP_CONN\".");
        }

        if (!Connection.StartsWith(ENVIRONMENT_PREFIX, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Database '{Describe()}' carries a connection string instead of an 'env:' reference. " +
                                           "workspace.json is committed, so it must name an environment variable, as in " +
                                           "\"connection\": \"env:CD_SHOP_CONN\".");
        }

        if (Connection.Length == ENVIRONMENT_PREFIX.Length)
        {
            throw new InvalidDataException($"Database '{Describe()}' has an 'env:' reference that names no variable.");
        }
    }

    private string Describe() => Name ?? "(unnamed)";

    private static string Normalize(string provider) =>
        new(provider.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
