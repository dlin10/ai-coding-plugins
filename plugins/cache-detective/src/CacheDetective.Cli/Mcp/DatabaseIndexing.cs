using CacheDetective.Configuration;
using CacheDetective.Database;
using Microsoft.Data.SqlClient;

namespace CacheDetective.Mcp;

/// <summary>Reading one configured database's catalogue, and finding which configured database a name means.
/// Neither holds anything, and neither touches the session: they are given the configuration and the name
/// and answer from those alone, which is what lets the session keep the gate and the graph to itself.</summary>
internal static class DatabaseIndexing
{
    internal static async Task<DatabaseIndexResult> ReadCatalogueAsync(DatabaseConfiguration database, string name, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ReadOnlyIntent.Apply(database.ResolveConnectionString()));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await new DatabaseIndexer().IndexAsync(connection,
                                                      name,
                                                      cancellationToken)
                                          .ConfigureAwait(false);
    }

    internal static DatabaseConfiguration? FindDatabase(WorkspaceConfiguration configuration, string name, out string? error)
    {
        var databases = configuration.Databases ?? [];
        if (databases.Length == 0)
        {
            error = "No database is configured. Add one to the 'databases' array of " + ".cache-detective/workspace.json, as { \"name\": \"shop\", " +
                    "\"connection\": \"env:CD_SHOP_CONN\" }.";
            return null;
        }

        // Matched on the configured name only. A nameless record is refused when the configuration is
        // read, and must not be matched against whatever name the caller happened to type — that would
        // stamp the caller's string onto every vertex read from the catalogue.
        var match = databases.FirstOrDefault(database => string.Equals(database.Name,
                                                                       name,
                                                                       StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var names = string.Join(", ",
                                    databases.Select(database => database.Name));
            error = $"No database named '{name}' is configured. Configured: {names}.";
            return null;
        }

        error = null;
        return match;
    }
}
