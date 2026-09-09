using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CacheDetective.Configuration;
using Microsoft.Data.SqlClient;

namespace CacheDetective.Verification;

/// <summary>
/// Every statement runtime verification issues against a database, and the one method that issues them.
/// It is built the way <c>CatalogueReader</c> is, for the same reason: a unit test drives it against a
/// fake connection and inspects the texts that arrive at <see cref="QueryAsync"/>, so "reads and never
/// writes" is checkable rather than asserted.
/// </summary>
internal sealed class VerificationReader(DbConnection connection)
{
    private const string SCHEMA_PARAMETER = "@schema";
    private const string NAME_PARAMETER = "@name";
    private const string KEY_PARAMETER = "@key";

    /// <summary>SQL Server's numbers for "you may not do that". 297 is the permission refusal on a
    /// dynamic management view, 300 the <c>VIEW SERVER STATE</c> refusal behind it.</summary>
    private static readonly int[] PERMISSION_DENIED = [297, 300];

    /// <summary>SQL Server's numbers for "that value or that name is wrong", which is a different thing
    /// from the database being unavailable: 207 an invalid column name, 208 an invalid object name, 245
    /// and 8114 a value the server would not convert to the column's type.</summary>
    private static readonly int[] PARAMETER_OR_NAME = [207, 208, 245, 8114];

    /// <summary>
    /// How long ago the table was last written, in seconds, computed on the server. Both sides of the
    /// subtraction come off the same clock and it is the server's local one: <c>last_user_update</c> is
    /// stamped with local time, so <c>SYSDATETIME()</c> is the only reading that can be subtracted from
    /// it — a UTC now, or the client's clock, would silently add the machine's offset to every answer.
    /// <para>The join runs from <c>sys.tables</c> outwards so that the three outcomes stay apart: no row
    /// means the table is not visible to this login, a row holding <c>NULL</c> means it is visible and
    /// the usage statistics have nothing for it, and a row holding a number is the answer. The
    /// <c>GROUP BY</c> is what keeps an aggregate over no rows from inventing one.</para>
    /// </summary>
    private const string LAST_WRITE_QUERY = """
                                            SELECT DATEDIFF_BIG(SECOND, MAX(u.last_user_update), SYSDATETIME())
                                            FROM sys.tables AS t
                                            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
                                            LEFT JOIN sys.dm_db_index_usage_stats AS u
                                                ON u.object_id = t.object_id AND u.database_id = DB_ID()
                                            WHERE s.name = @schema AND t.name = @name
                                            GROUP BY t.object_id
                                            """;

    /// <summary>The columns the table actually has. Names are checked against the catalogue before any
    /// of them is written into a statement, because a column name cannot be a parameter.</summary>
    private const string COLUMNS_QUERY = """
                                         SELECT c.name
                                         FROM sys.columns AS c
                                         INNER JOIN sys.tables AS t ON t.object_id = c.object_id
                                         INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
                                         WHERE s.name = @schema AND t.name = @name
                                         """;

    /// <summary>Opens a connection that says what it is for. See <see cref="ReadOnlyIntent"/>.</summary>
    internal static SqlConnection Connect(string connectionString) =>
        new(ReadOnlyIntent.Apply(connectionString));

    internal async Task<LastWrite> ReadLastWriteAsync(string schema, string table, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await QueryAsync(LAST_WRITE_QUERY,
                                        [(SCHEMA_PARAMETER, schema), (NAME_PARAMETER, table)],
                                        reader => reader.IsDBNull(0) ? (double?)null : Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture),
                                        cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                return LastWrite.Unavailable($"'{schema}.{table}' is not visible in sys.tables to this login");
            }

            return rows[0] is { } seconds
                       ? new LastWrite(seconds, null)
                       : LastWrite.Unavailable($"the index usage statistics hold no row for '{schema}.{table}', " +
                                               "which is what they say after a restart and for a table nothing has touched");
        }
        catch (DbException error) when (IsPermissionDenied(error))
        {
            // sys.dm_db_index_usage_stats is a server-scoped dynamic management view, so the grant it wants
            // is a server one: VIEW SERVER STATE, or the narrower VIEW SERVER PERFORMANCE STATE that
            // replaced it in SQL Server 2022. VIEW DATABASE STATE does not reach it.
            return LastWrite.Unavailable("this login may not read sys.dm_db_index_usage_stats; it needs VIEW SERVER STATE, " +
                                         "or VIEW SERVER PERFORMANCE STATE on SQL Server 2022 and later");
        }
    }

    /// <summary>
    /// Reads the one row a cached value was built from and compares it field by field. The key's value
    /// comes from the placeholder dictionary the cache reader produced — the actual key never reaches
    /// this class — and the comparison is refused, with its own reason, whenever the value cannot be
    /// identified beyond doubt.
    /// </summary>
    /// <summary>
    /// Which of the cached value's field names are columns of this table, for a table whose row will not be
    /// read at all. Whether two dependent tables share a field name is a fact about the tables, and a table
    /// left out of <c>verify.tables</c> is no less capable of having the column: the workspace not saying
    /// how to look up its row is not evidence that the value was not partly built from it.
    /// <para>This reads <c>INFORMATION_SCHEMA</c> and nothing else. The rule against reading the
    /// <em>rows</em> of an undeclared table stands — there is no key to look one up by, and none is
    /// invented.</para>
    /// </summary>
    internal async Task<IReadOnlyList<string>> MatchedColumnsAsync(string schema, string table, JsonElement cached,
                                                                   CancellationToken cancellationToken) =>
        MatchedColumns(await ColumnsAsync(schema, table, cancellationToken).ConfigureAwait(false), cached);

    private async Task<HashSet<string>> ColumnsAsync(string schema, string table, CancellationToken cancellationToken) =>
        (await QueryAsync(COLUMNS_QUERY, [(SCHEMA_PARAMETER, schema), (NAME_PARAMETER, table)],
                          reader => reader.GetString(0), cancellationToken).ConfigureAwait(false))
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>Only the columns whose names matched a field name literally. A field the table does not
    /// have is dropped here rather than guessed at, and never reaches a statement.</summary>
    private static string[] MatchedColumns(HashSet<string> known, JsonElement cached) =>
        cached.ValueKind == JsonValueKind.Object
            ? cached.EnumerateObject().Select(field => field.Name).Where(known.Contains).ToArray()
            : [];

    internal async Task<RowComparison> CompareRowAsync(string schema, string table, VerifyTableConfiguration configuration,
                                                       string template, IReadOnlyDictionary<string, string>? placeholders,
                                                       JsonElement cached, CancellationToken cancellationToken)
    {
        // Which of the cached value's names are columns of this table comes first, before every refusal
        // below. It is a fact about the table and the value, true whatever the configuration says and
        // whether or not a row is ever looked up, and the caller needs it from every table to know which
        // names two tables share. Established after the refusals, a table that bowed out early looked like
        // it owned no names at all, and a name it shares with another table could be refuted against that
        // other one — which is exactly what the ambiguity rule exists to forbid.
        var known = await ColumnsAsync(schema, table, cancellationToken).ConfigureAwait(false);
        var selected = MatchedColumns(known, cached);

        if (string.IsNullOrWhiteSpace(configuration.Key) || string.IsNullOrWhiteSpace(configuration.From))
        {
            return RowComparison.NotCompared($"'{schema}.{table}' is not declared in verify.tables with both 'key' and 'from'",
                                             selected);
        }

        if (placeholders is null)
        {
            return RowComparison.NotCompared("the key matched the template in more than one way, so no placeholder value can be trusted",
                                             selected);
        }

        if (Occurrences(template, configuration.From) > 1)
        {
            return RowComparison.NotCompared($"the placeholder '{{{configuration.From}}}' occurs more than once in the template, " +
                                             "so which occurrence supplies the key is undecided", selected);
        }

        if (!placeholders.TryGetValue(configuration.From, out var keyValue))
        {
            return RowComparison.NotCompared($"the template has no placeholder '{{{configuration.From}}}' to take the key's value from",
                                             selected);
        }

        if (cached.ValueKind != JsonValueKind.Object)
        {
            return RowComparison.NotCompared("the cached value is not an object, so it has no fields to compare", selected);
        }

        if (!known.Contains(configuration.Key))
        {
            return RowComparison.NotCompared($"'{schema}.{table}' has no column '{configuration.Key}' to look the row up by",
                                             selected);
        }

        if (selected.Length == 0)
        {
            return RowComparison.NotCompared("none of the cached value's field names is a column of the table", selected);
        }

        var rows = await QueryAsync(RowQuery(schema, table, configuration.Key!, selected),
                                    [(KEY_PARAMETER, keyValue)],
                                    reader => selected.Select((_, ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal)).ToArray(),
                                    cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1)
        {
            // The columns travel with the refusal: which names this table owns is settled, and the next
            // table's comparison must not be allowed to treat one of them as its own alone.
            return RowComparison.NotCompared($"the lookup found {rows.Count} rows and a comparison needs exactly one", selected);
        }

        var comparisons = selected.Select((column, ordinal) => Compare(column, cached.GetProperty(column), rows[0][ordinal])).ToArray();
        return new RowComparison(comparisons, null, selected);
    }

    /// <summary>
    /// One field of a cached value against one column of its row. Every way of not being able to compare
    /// carries its own reason and none of them throws, because a verification that crashes on an
    /// unexpected type is worse than one that says it could not tell.
    /// </summary>
    internal static FieldComparison Compare(string field, JsonElement cached, object? actual)
    {
        if (cached.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return FieldComparison.NotCompared(field, "the cached field holds a composite value and only scalars are compared");
        }

        var missing = actual is null or DBNull;
        if (cached.ValueKind == JsonValueKind.Null)
        {
            return Verdict(field, missing);
        }

        if (missing)
        {
            return Verdict(field, false);
        }

        switch (cached.ValueKind)
        {
            case JsonValueKind.True or JsonValueKind.False:
                return actual is bool flag
                           ? Verdict(field, flag == (cached.ValueKind == JsonValueKind.True))
                           : Incompatible(field, "boolean", actual);

            case JsonValueKind.Number:
                if (!cached.TryGetDecimal(out var cachedNumber))
                {
                    return FieldComparison.NotCompared(field, "the cached number is outside the range a decimal can hold");
                }

                try
                {
                    return actual is bool or string or DateTime or DateTimeOffset or Guid or byte[]
                               ? Incompatible(field, "number", actual)
                               : Verdict(field, Convert.ToDecimal(actual, CultureInfo.InvariantCulture) == cachedNumber);
                }
                catch (Exception error) when (error is OverflowException or InvalidCastException or FormatException)
                {
                    return FieldComparison.NotCompared(field, "the column's value is outside the range a decimal can hold");
                }

            case JsonValueKind.String:
                return CompareText(field, cached, actual);

            default:
                return FieldComparison.NotCompared(field, $"the cached field is of kind '{cached.ValueKind}' and is not compared");
        }
    }

    /// <summary>A JSON string is a string, or a moment written as one. A moment only compares when both
    /// sides carry an offset: <c>datetime</c> and <c>datetime2</c> have none, and reading one as if it
    /// were UTC is exactly the silent hour this refuses to risk.</summary>
    private static FieldComparison CompareText(string field, JsonElement cached, object? actual)
    {
        var text = cached.GetString()!;
        switch (actual)
        {
            case string value:
                return Verdict(field, string.Equals(value, text, StringComparison.Ordinal));

            case DateTimeOffset moment:
                // TryParse is happy to supply the machine's own offset for a moment that states none, so
                // "2026-09-05T12:00:00" would compare differently in Moscow and in London. A moment with no
                // offset and no Z is not compared at all rather than compared against the local clock.
                if (!HasExplicitOffset(text))
                {
                    return FieldComparison.NotCompared(field, "the cached moment states no offset and no 'Z', so reading it " +
                                                              "would attach this machine's own offset to it");
                }

                return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var cachedMoment)
                           ? Verdict(field, cachedMoment.ToUniversalTime() == moment.ToUniversalTime())
                           : FieldComparison.NotCompared(field, "the cached field is not a moment and the column is a datetimeoffset");

            case DateTime:
                return FieldComparison.NotCompared(field, "the column is a datetime or datetime2 and carries no offset, " +
                                                          "so the two moments cannot be placed on one timeline");

            default:
                return Incompatible(field, "string", actual);
        }
    }

    /// <summary>Whether the text carries an offset of its own: a trailing <c>Z</c>, or a <c>+hh:mm</c> /
    /// <c>-hh:mm</c> after the time. The sign is looked for past the date, so that the dashes in
    /// <c>2026-09-05</c> are not mistaken for one.</summary>
    private static bool HasExplicitOffset(string text)
    {
        var trimmed = text.AsSpan().Trim();
        if (trimmed.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var time = trimmed.IndexOfAny('T', ' ');
        return time >= 0 && trimmed[(time + 1)..].IndexOfAny('+', '-') >= 0;
    }

    private static FieldComparison Incompatible(string field, string cachedKind, object? actual) =>
        FieldComparison.NotCompared(field, $"the cached field is a {cachedKind} and the column is a " +
                                           $"{actual?.GetType().Name ?? "null"}, which are not comparable");

    private static FieldComparison Verdict(string field, bool equal) =>
        new(field, equal ? FieldVerdict.Equal : FieldVerdict.Different, null);

    internal static bool IsPermissionDenied(DbException error) =>
        Numbers(error).Any(PERMISSION_DENIED.Contains);

    internal static bool IsParameterOrNameError(DbException error) =>
        Numbers(error).Any(PARAMETER_OR_NAME.Contains);

    private static IEnumerable<int> Numbers(DbException error) =>
        error is SqlException sql
            ? sql.Errors.Cast<SqlError>().Select(entry => entry.Number)
            : [error.ErrorCode];

    private static int Occurrences(string template, string placeholder)
    {
        var needle = $"{{{placeholder}}}";
        var count = 0;
        for (var index = template.IndexOf(needle, StringComparison.Ordinal); index >= 0;
             index = template.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The one statement that names a user table. Every identifier in it came out of the
    /// catalogue a moment ago and is quoted anyway; the only value in it is a parameter.</summary>
    private static string RowQuery(string schema, string table, string keyColumn, IReadOnlyList<string> columns) =>
        $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(schema)}.{Quote(table)} WHERE {Quote(keyColumn)} = {KEY_PARAMETER}";

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    /// <summary>The single seam: the only place in the reader that creates and runs a
    /// <see cref="DbCommand"/>. The connection is already open and is left as it was found.</summary>
    private async Task<List<T>> QueryAsync<T>(string sql, IReadOnlyList<(string Name, object Value)> parameters,
                                              Func<DbDataReader, T> project, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
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
}

/// <summary>How long ago the table was written, or why that cannot be said.</summary>
internal sealed record LastWrite(double? SecondsSinceLastWrite, string? Reason)
{
    internal static LastWrite Unavailable(string reason) => new(null, reason);
}

internal enum FieldVerdict
{
    Equal,
    Different,
    NotCompared
}

internal sealed record FieldComparison(string Field, FieldVerdict Verdict, string? Reason)
{
    internal static FieldComparison NotCompared(string field, string reason) =>
        new(field, FieldVerdict.NotCompared, reason);
}

/// <summary>
/// One table's row against one cached value.
/// </summary>
/// <param name="MatchedColumns">The cached value's field names that are columns of this table, established
/// from the <c>sys.</c> catalogue and carried <em>whether or not a row was found</em>. Whether a field name
/// belongs to two dependent tables is a fact about the tables and not about how the readings turned out;
/// leaving it out of a comparison that found no row let the same name look unambiguous and be refuted
/// against the one table that did answer.</param>
internal sealed record RowComparison(IReadOnlyList<FieldComparison> Fields, string? Reason,
                                     IReadOnlyList<string>? MatchedColumns = null)
{
    internal static RowComparison NotCompared(string reason, IReadOnlyList<string>? matchedColumns = null) =>
        new([], reason, matchedColumns);
}
