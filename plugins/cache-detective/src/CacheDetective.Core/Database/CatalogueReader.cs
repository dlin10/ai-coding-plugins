using System.Data.Common;
using CacheDetective.Graph;

namespace CacheDetective.Database;

/// <summary>
/// Every statement the database indexer issues, and the one method that issues them. The queries read
/// <c>sys.</c> catalogue views and one dynamic management function; none names a user table and none
/// executes a user procedure, which is what makes the indexer's read-only claim checkable rather than
/// asserted — a unit test drives the indexer against a fake connection and inspects the texts that
/// arrive at <see cref="QueryAsync"/>.
/// </summary>
internal sealed class CatalogueReader(DbConnection connection)
{
    private const string OBJECT_PARAMETER = "@object";

    private const string PROCEDURES_QUERY = """
                                            SELECT s.name, p.name, m.definition
                                            FROM sys.procedures AS p
                                            INNER JOIN sys.schemas AS s ON s.schema_id = p.schema_id
                                            LEFT JOIN sys.sql_modules AS m ON m.object_id = p.object_id
                                            ORDER BY s.name, p.name
                                            """;

    /// <summary><c>sys.views</c> lists the views by name and carries no dependency at all.</summary>
    private const string VIEWS_QUERY = """
                                       SELECT s.name, v.name
                                       FROM sys.views AS v
                                       INNER JOIN sys.schemas AS s ON s.schema_id = v.schema_id
                                       ORDER BY s.name, v.name
                                       """;

    /// <summary><c>sys.triggers</c> and <c>sys.trigger_events</c> give the host table and the events, and
    /// nothing about what the trigger body touches. A DML trigger has no schema of its own: it lives in
    /// the schema of the table it hangs on.</summary>
    private const string TRIGGERS_QUERY = """
                                          SELECT s.name, t.name, b.name, e.type_desc
                                          FROM sys.triggers AS t
                                          INNER JOIN sys.tables AS b ON b.object_id = t.parent_id
                                          INNER JOIN sys.schemas AS s ON s.schema_id = b.schema_id
                                          INNER JOIN sys.trigger_events AS e ON e.object_id = t.object_id
                                          WHERE t.parent_class = 1 AND t.is_disabled = 0
                                          ORDER BY s.name, t.name
                                          """;

    private const string VIEW_DEFINITION_PERMISSION_QUERY = "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')";

    private const string PROCEDURE_CALLS_QUERY = """
                                                 SELECT s.name, r.name, d.referenced_schema_name, d.referenced_entity_name
                                                 FROM sys.sql_expression_dependencies AS d
                                                 INNER JOIN sys.procedures AS r ON r.object_id = d.referencing_id
                                                 INNER JOIN sys.schemas AS s ON s.schema_id = r.schema_id
                                                 WHERE d.referenced_entity_name IS NOT NULL
                                                 ORDER BY s.name, r.name
                                                 """;

    /// <summary>The one query that separates reading from writing, applied to procedures, views and
    /// triggers alike. Column-level rows are folded into one row per referenced object.</summary>
    private const string REFERENCED_ENTITIES_QUERY = """
                                                     SELECT ISNULL(e.referenced_schema_name, 'dbo'), e.referenced_entity_name,
                                                            MAX(CAST(e.is_selected AS int)), MAX(CAST(e.is_updated AS int))
                                                     FROM sys.dm_sql_referenced_entities(@object, 'OBJECT') AS e
                                                     WHERE e.referenced_entity_name IS NOT NULL AND e.referenced_class = 1
                                                     GROUP BY e.referenced_schema_name, e.referenced_entity_name
                                                     """;

    public Task<List<CatalogueProcedure>> ReadProceduresAsync(CancellationToken cancellationToken) =>
        QueryAsync(PROCEDURES_QUERY, null, reader => new CatalogueProcedure(reader.GetString(0), reader.GetString(1), GetText(reader, 2)), cancellationToken);

    public Task<List<CatalogueObject>> ReadViewsAsync(CancellationToken cancellationToken) =>
        QueryAsync(VIEWS_QUERY, null, reader => new CatalogueObject(reader.GetString(0), reader.GetString(1)), cancellationToken);

    public async Task<List<CatalogueTrigger>> ReadTriggersAsync(CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(TRIGGERS_QUERY, null,
                                    reader => (Schema: reader.GetString(0), Name: reader.GetString(1), Table: reader.GetString(2), Event: reader.GetString(3)),
                                    cancellationToken);

        return rows.GroupBy(row => (row.Schema, row.Name, row.Table))
                   .Select(group => new CatalogueTrigger(group.Key.Schema, group.Key.Name, group.Key.Schema, group.Key.Table,
                                                         group.Select(row => ToWriteEvent(row.Event)).OfType<WriteEvent>().Distinct().ToArray()))
                   .ToList();
    }

    public Task<List<CatalogueDependency>> ReadProcedureCallsAsync(CancellationToken cancellationToken) =>
        QueryAsync(PROCEDURE_CALLS_QUERY, null,
                   reader => new CatalogueDependency($"{reader.GetString(0)}.{reader.GetString(1)}", $"{GetText(reader, 2) ?? "dbo"}.{reader.GetString(3)}"),
                   cancellationToken);

    /// <summary>Whether this login can see <c>sys.sql_expression_dependencies</c> at all. Measured on
    /// SQL Server 2019: the view is metadata-visibility filtered, and <c>VIEW DEFINITION</c> granted on a
    /// schema is not enough — the login reads zero rows and every procedure-to-procedure call silently
    /// disappears. Asking the permission directly beats inferring it from an empty result, which cannot
    /// tell insufficient rights from a database whose procedures genuinely call nothing.</summary>
    public async Task<bool> CanSeeDependenciesAsync(CancellationToken cancellationToken)
    {
        var answers = await QueryAsync(VIEW_DEFINITION_PERMISSION_QUERY, null, reader => reader.IsDBNull(0) || reader.GetInt32(0) != 0, cancellationToken)
                         .ConfigureAwait(false);

        // No row is not an answer, so it is not treated as a denial.
        return answers.Count == 0 || answers[0];
    }

    public Task<List<CatalogueReference>> ReadReferencedEntitiesAsync(string qualifiedName, CancellationToken cancellationToken) =>
        QueryAsync(REFERENCED_ENTITIES_QUERY, qualifiedName,
                   reader => new CatalogueReference(reader.GetString(0), reader.GetString(1), GetFlag(reader, 2), GetFlag(reader, 3)), cancellationToken);

    /// <summary>The single seam: the only place in the indexer that creates and runs a
    /// <see cref="DbCommand"/>. The connection is already open and is left as it was found.</summary>
    private async Task<List<T>> QueryAsync<T>(string sql, string? objectName, Func<DbDataReader, T> project, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (objectName is not null)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = OBJECT_PARAMETER;
            parameter.Value = objectName;
            command.Parameters.Add(parameter);
        }

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(project(reader));
        }

        return rows;
    }

    private static string? GetText(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static bool GetFlag(DbDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && Convert.ToInt32(reader.GetValue(ordinal)) != 0;

    private static WriteEvent? ToWriteEvent(string typeDescription) => typeDescription switch
                                                                       {
                                                                           "INSERT" => WriteEvent.Insert,
                                                                           "UPDATE" => WriteEvent.Update,
                                                                           "DELETE" => WriteEvent.Delete,
                                                                           _ => null
                                                                       };
}

internal record CatalogueObject(string Schema, string Name)
{
    public string QualifiedName => $"{Schema}.{Name}";
}

internal sealed record CatalogueProcedure(string Schema, string Name, string? Definition)
    : CatalogueObject(Schema, Name)
{
    public StoredProcedure ToVertex(string database) => new(Schema, Name, database);
}

internal sealed record CatalogueTrigger(string Schema, string Name, string TableSchema, string TableName,
                                        IReadOnlyList<WriteEvent> Events)
    : CatalogueObject(Schema, Name);

internal sealed record CatalogueReference(string Schema, string Name, bool IsSelected, bool IsUpdated);

internal sealed record CatalogueDependency(string ReferencingName, string ReferencedName);
