using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CacheDetective.Configuration;
using CacheDetective.Verification;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// The database side of verification, driven against a fake connection. The fake is the only way the
/// reader can obtain a command, so the recorded texts are a complete account of the SQL it issued —
/// which is what lets a test check the read-only claim instead of taking it on trust.
/// </summary>
public sealed class VerificationReaderTests
{
    private const string Schema = "dbo";
    private const string Table = "Products";
    private const string Template = "product:{id}";

    private static readonly VerifyTableConfiguration Configured = new() { Key = "Id", From = "id" };

    [Fact]
    public async Task The_staleness_query_names_only_sys_objects()
    {
        var database = new FakeDatabase();

        await new VerificationReader(database.Connect()).ReadLastWriteAsync(Schema, Table, default);

        var sql = Assert.Single(database.CommandTexts);
        Assert.All(Objects(sql), name => Assert.StartsWith("sys.", name, StringComparison.Ordinal));
        Assert.DoesNotContain(Table, sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_staleness_query_returns_a_duration()
    {
        var database = new FakeDatabase { Seconds = 3600 };

        var result = await new VerificationReader(database.Connect()).ReadLastWriteAsync(Schema, Table, default);

        Assert.Equal(3600, result.SecondsSinceLastWrite);
        Assert.Null(result.Reason);
    }

    /// <summary><c>last_user_update</c> is stamped with the server's local time, so the only reading that
    /// can be subtracted from it is the server's local one. A UTC now would add the machine's offset to
    /// every answer, and silently.</summary>
    [Fact]
    public async Task Both_sides_of_the_subtraction_come_off_the_servers_local_clock()
    {
        var database = new FakeDatabase { Seconds = 42 };

        var result = await new VerificationReader(database.Connect()).ReadLastWriteAsync(Schema, Table, default);

        var sql = Assert.Single(database.CommandTexts);
        Assert.Contains("last_user_update", sql, StringComparison.Ordinal);
        Assert.Contains("SYSDATETIME()", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SYSUTCDATETIME", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GETUTCDATE", sql, StringComparison.Ordinal);
        // The difference is computed on the server, so no clock of ours takes part in the answer.
        Assert.Equal(42, result.SecondsSinceLastWrite);
    }

    [Fact]
    public async Task A_permission_refusal_is_told_apart()
    {
        var database = new FakeDatabase { StalenessErrorNumber = 297 };

        var result = await new VerificationReader(database.Connect()).ReadLastWriteAsync(Schema, Table, default);

        Assert.Null(result.SecondsSinceLastWrite);
        // sys.dm_db_index_usage_stats is server-scoped, so the grant it names is a server one. The README
        // asks for exactly these two.
        Assert.Contains("VIEW SERVER STATE", result.Reason, StringComparison.Ordinal);
        Assert.Contains("VIEW SERVER PERFORMANCE STATE", result.Reason, StringComparison.Ordinal);
        Assert.True(VerificationReader.IsPermissionDenied(new FakeDbException("denied", 300)));
        Assert.False(VerificationReader.IsPermissionDenied(new FakeDbException("timeout", -2)));
    }

    [Fact]
    public async Task A_table_that_is_not_visible_is_told_apart()
    {
        var database = new FakeDatabase { TableVisible = false };

        var result = await new VerificationReader(database.Connect()).ReadLastWriteAsync(Schema, Table, default);

        Assert.Null(result.SecondsSinceLastWrite);
        Assert.Contains("not visible in sys.tables", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_visible_table_with_no_statistics_row_is_told_apart()
    {
        var database = new FakeDatabase { Seconds = null };

        var result = await new VerificationReader(database.Connect()).ReadLastWriteAsync(Schema, Table, default);

        Assert.Null(result.SecondsSinceLastWrite);
        Assert.Contains("no row", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("not visible", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_read_without_both_key_and_from()
    {
        var database = new FakeDatabase();

        var result = await CompareAsync(database, new VerifyTableConfiguration { Key = "Id" }, Placeholders());

        Assert.Contains("'key' and 'from'", result.Reason, StringComparison.Ordinal);
        AssertReadNoRows(database);
    }

    /// <summary>
    /// A refusal still says which of the cached value's names are columns of this table. It is a fact about
    /// the table and the value, settled before the configuration is looked at, and the caller needs it from
    /// every table to know which names two tables share — a table that bowed out carrying nothing looked
    /// like it owned no names, and a name it shares could then be refuted against the other table alone.
    /// </summary>
    [Fact]
    public async Task A_placeholder_the_template_does_not_have_is_not_compared()
    {
        var database = new FakeDatabase { Columns = { "Id", "price" } };

        var result = await CompareAsync(database, new VerifyTableConfiguration { Key = "Id", From = "sku" }, Placeholders());

        Assert.Contains("no placeholder '{sku}'", result.Reason, StringComparison.Ordinal);
        Assert.Equal(["price"], result.MatchedColumns);
        AssertReadNoRows(database);
    }

    /// <summary>The catalogue is read whatever happens; the table's own rows are not.</summary>
    private static void AssertReadNoRows(FakeDatabase database) =>
        Assert.All(database.CommandTexts,
                   text => Assert.Contains("sys.columns", text, StringComparison.Ordinal));

    [Fact]
    public async Task A_match_the_reader_called_ambiguous_is_not_compared()
    {
        var database = new FakeDatabase();

        var result = await CompareAsync(database, Configured, placeholders: null);

        Assert.Contains("more than one way", result.Reason, StringComparison.Ordinal);
        AssertReadNoRows(database);
    }

    [Fact]
    public async Task A_placeholder_that_occurs_twice_is_not_compared()
    {
        var database = new FakeDatabase();

        var result = await CompareAsync(database, Configured, Placeholders(), template: "product:{id}:mirror:{id}");

        Assert.Contains("more than once", result.Reason, StringComparison.Ordinal);
        AssertReadNoRows(database);
    }

    [Fact]
    public async Task Zero_rows_are_not_compared()
    {
        var database = new FakeDatabase { Columns = { "Id", "price" } };

        var result = await CompareAsync(database, Configured, Placeholders());

        Assert.Contains("found 0 rows", result.Reason, StringComparison.Ordinal);
        Assert.Empty(result.Fields);
    }

    [Fact]
    public async Task More_than_one_row_is_not_compared()
    {
        var database = new FakeDatabase { Columns = { "Id", "price" }, Rows = { new object?[] { 10m }, new object?[] { 11m } } };

        var result = await CompareAsync(database, Configured, Placeholders());

        Assert.Contains("found 2 rows", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_statement_carries_no_value_of_its_own()
    {
        var database = new FakeDatabase { Columns = { "Id", "price" }, Rows = { new object?[] { 10m } } };

        await CompareAsync(database, Configured, Placeholders("42"));

        var sql = database.CommandTexts[^1];
        Assert.DoesNotContain("42", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("10", sql, StringComparison.Ordinal);
        Assert.Contains("@key", sql, StringComparison.Ordinal);
        Assert.Equal("42", database.Parameters[^1]["@key"]);
    }

    [Fact]
    public async Task A_field_the_table_has_no_column_for_is_dropped()
    {
        var database = new FakeDatabase { Columns = { "Id", "price" }, Rows = { new object?[] { 10m } } };

        var result = await CompareAsync(database, Configured, Placeholders(), cached: """{ "price": 10, "nickname": "x" }""");

        Assert.Equal("price", Assert.Single(result.Fields).Field);
        Assert.DoesNotContain("nickname", database.CommandTexts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_field_equals_a_NULL_column()
    {
        Assert.Equal(FieldVerdict.Equal, Compare("null", DBNull.Value).Verdict);
        Assert.Equal(FieldVerdict.Equal, Compare("null", null).Verdict);
        Assert.Equal(FieldVerdict.Different, Compare("null", 7).Verdict);
        Assert.Equal(FieldVerdict.Different, Compare("7", DBNull.Value).Verdict);
    }

    [Fact]
    public void A_boolean_against_a_bit()
    {
        Assert.Equal(FieldVerdict.Equal, Compare("true", true).Verdict);
        Assert.Equal(FieldVerdict.Equal, Compare("false", false).Verdict);
        Assert.Equal(FieldVerdict.Different, Compare("true", false).Verdict);
        Assert.Equal(FieldVerdict.NotCompared, Compare("true", "yes").Verdict);
    }

    [Fact]
    public void A_number()
    {
        Assert.Equal(FieldVerdict.Equal, Compare("10", 10m).Verdict);
        Assert.Equal(FieldVerdict.Equal, Compare("10.50", 10.5m).Verdict);
        Assert.Equal(FieldVerdict.Equal, Compare("10", 10).Verdict);
        Assert.Equal(FieldVerdict.Different, Compare("10", 11m).Verdict);
    }

    [Fact]
    public void A_number_outside_the_range_is_not_compared()
    {
        var cached = Compare("1e40", 1m);

        Assert.Equal(FieldVerdict.NotCompared, cached.Verdict);
        Assert.Contains("range a decimal can hold", cached.Reason, StringComparison.Ordinal);
        var column = Compare("1", double.MaxValue);
        Assert.Equal(FieldVerdict.NotCompared, column.Verdict);
        Assert.Contains("range a decimal can hold", column.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_datetimeoffset_compares_as_a_moment()
    {
        var noon = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal(FieldVerdict.Equal, Compare("\"2024-01-01T10:00:00+00:00\"", noon).Verdict);
        Assert.Equal(FieldVerdict.Different, Compare("\"2024-01-01T12:00:00+00:00\"", noon).Verdict);
        Assert.Equal(FieldVerdict.NotCompared, Compare("\"tuesday\"", noon).Verdict);
    }

    [Fact]
    public void A_datetime_without_an_offset_is_not_compared()
    {
        var comparison = Compare("\"2024-01-01T12:00:00\"", new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Unspecified));

        Assert.Equal(FieldVerdict.NotCompared, comparison.Verdict);
        Assert.Contains("no offset", comparison.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Incompatible_types_are_not_compared()
    {
        Assert.Equal(FieldVerdict.NotCompared, Compare("\"ten\"", 10m).Verdict);
        Assert.Equal(FieldVerdict.NotCompared, Compare("10", "ten").Verdict);
        Assert.Equal(FieldVerdict.NotCompared, Compare("10", new DateTime(2024, 1, 1)).Verdict);
        Assert.Contains("not comparable", Compare("10", "ten").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_value_is_not_compared()
    {
        Assert.Equal(FieldVerdict.NotCompared, Compare("""{ "nested": 1 }""", "x").Verdict);
        Assert.Equal(FieldVerdict.NotCompared, Compare("[1, 2]", "x").Verdict);
        Assert.Contains("composite", Compare("[1, 2]", "x").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_query_writes_and_the_connection_states_read_only_intent()
    {
        string[] writes = ["INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE", "EXEC", "GRANT"];
        var database = new FakeDatabase { Columns = { "Id", "price" }, Rows = { new object?[] { 10m } } };
        var reader = new VerificationReader(database.Connect());

        await reader.ReadLastWriteAsync(Schema, Table, default);
        await CompareAsync(database, Configured, Placeholders(), reader: reader);

        Assert.Equal(3, database.CommandTexts.Count);
        Assert.All(database.CommandTexts, sql => Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.Ordinal));
        // Compared as whole words: "last_user_update" is not an UPDATE.
        Assert.All(database.CommandTexts, sql => Assert.All(Words(sql), word => Assert.DoesNotContain(word, writes)));
        Assert.Contains("ApplicationIntent=ReadOnly",
                        VerificationReader.Connect("Server=.;Database=Shop;Trusted_Connection=True").ConnectionString,
                        StringComparison.Ordinal);
    }

    /// <summary>The statement's words, upper-cased, so a verb is looked for as a word and not as a run of
    /// letters inside a column name.</summary>
    private static string[] Words(string sql) =>
        System.Text.RegularExpressions.Regex.Matches(sql, "[A-Za-z_][A-Za-z0-9_]*")
              .Select(match => match.Value.ToUpperInvariant())
              .ToArray();

    private static FieldComparison Compare(string cachedJson, object? actual)
    {
        using var document = JsonDocument.Parse($$"""{ "field": {{cachedJson}} }""");
        return VerificationReader.Compare("field", document.RootElement.GetProperty("field"), actual);
    }

    /// <summary>
    /// A moment with no offset and no Z is not compared at all. TryParse would supply the machine's own
    /// offset for it, so "2026-09-05T12:00:00" compared differently in Moscow and in London — a silent
    /// wrong answer that depended on where the scan ran.
    /// </summary>
    [Fact]
    public void A_cached_moment_with_no_offset_is_not_compared()
    {
        var comparison = Compare("\"2026-09-05T12:00:00\"", new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(FieldVerdict.NotCompared, comparison.Verdict);
        Assert.Contains("no offset", comparison.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"2026-09-05T12:00:00Z\"")]
    [InlineData("\"2026-09-05T15:00:00+03:00\"")]
    public void A_cached_moment_that_states_its_offset_is_compared(string cached)
    {
        var comparison = Compare(cached, new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(FieldVerdict.Equal, comparison.Verdict);
    }
    private static Dictionary<string, string> Placeholders(string value = "42") =>
        new(StringComparer.Ordinal) { ["id"] = value };

    private static async Task<RowComparison> CompareAsync(FakeDatabase database, VerifyTableConfiguration configuration,
                                                          IReadOnlyDictionary<string, string>? placeholders,
                                                          string template = Template,
                                                          string cached = """{ "price": 10 }""",
                                                          VerificationReader? reader = null)
    {
        using var document = JsonDocument.Parse(cached);
        return await (reader ?? new VerificationReader(database.Connect()))
                   .CompareRowAsync(Schema, Table, configuration, template, placeholders, document.RootElement, default);
    }

    /// <summary>Every object named after a FROM or a JOIN, so a test can say what the statement reached
    /// for rather than what it happens to spell.</summary>
    private static IEnumerable<string> Objects(string sql) =>
        sql.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
           .Select((word, index) => (word, index))
           .Where(entry => entry.index > 0)
           .Where(entry => sql.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[entry.index - 1]
                              is "FROM" or "JOIN")
           .Select(entry => entry.word);

    /// <summary>The seam. Every statement the reader issues lands here and nowhere else.</summary>
    private sealed class FakeDatabase
    {
        internal List<string> CommandTexts { get; } = [];

        internal List<Dictionary<string, object?>> Parameters { get; } = [];

        internal List<string> Columns { get; } = [];

        internal List<object?[]> Rows { get; } = [];

        internal bool TableVisible { get; init; } = true;

        internal double? Seconds { get; init; } = 120;

        internal int? StalenessErrorNumber { get; init; }

        internal DbConnection Connect() => new FakeConnection(this);

        internal IReadOnlyList<object?[]> Read(string sql, Dictionary<string, object?> parameters)
        {
            CommandTexts.Add(sql);
            Parameters.Add(parameters);

            if (sql.Contains("dm_db_index_usage_stats", StringComparison.Ordinal))
            {
                return StalenessErrorNumber is { } number
                           ? throw new FakeDbException("permission denied", number)
                           : TableVisible ? [[Seconds]] : [];
            }

            return sql.Contains("sys.columns", StringComparison.Ordinal)
                       ? Columns.Select(column => new object?[] { column }).ToArray()
                       : Rows;
        }
    }

    internal sealed class FakeDbException : DbException
    {
        public FakeDbException(string message, int number) : base(message) => HResult = number;
    }

    private sealed class FakeConnection(FakeDatabase database) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = "fake";

        public override string Database => "fake";

        public override string DataSource => "fake";

        public override string ServerVersion => "0";

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new FakeCommand(database);
    }

    private sealed class FakeCommand(FakeDatabase database) : DbCommand
    {
        private readonly FakeParameters _parameters = [];

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => _parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => new FakeParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            new FakeReader(database.Read(CommandText, _parameters.Values));
    }

    private sealed class FakeParameters : DbParameterCollection
    {
        private readonly List<DbParameter> _parameters = [];

        internal Dictionary<string, object?> Values =>
            _parameters.ToDictionary(parameter => parameter.ParameterName, parameter => parameter.Value, StringComparer.Ordinal);

        public override int Count => _parameters.Count;

        public override object SyncRoot => _parameters;

        public override int Add(object value)
        {
            _parameters.Add((DbParameter)value);
            return _parameters.Count - 1;
        }

        public override void AddRange(Array values) => throw new NotSupportedException();

        public override void Clear() => _parameters.Clear();

        public override bool Contains(object value) => _parameters.Contains(value);

        public override bool Contains(string value) => IndexOf(value) >= 0;

        public override void CopyTo(Array array, int index) => throw new NotSupportedException();

        public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

        public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);

        public override int IndexOf(string parameterName) =>
            _parameters.FindIndex(parameter => parameter.ParameterName == parameterName);

        public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);

        public override void Remove(object value) => _parameters.Remove((DbParameter)value);

        public override void RemoveAt(int index) => _parameters.RemoveAt(index);

        public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));

        protected override DbParameter GetParameter(int index) => _parameters[index];

        protected override DbParameter GetParameter(string parameterName) => _parameters[IndexOf(parameterName)];

        protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value) =>
            _parameters[IndexOf(parameterName)] = value;
    }

    private sealed class FakeParameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; }

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;

        public override int Size { get; set; }

        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;

        public override bool SourceColumnNullMapping { get; set; }

        public override object? Value { get; set; }

        public override void ResetDbType()
        {
        }
    }

    private sealed class FakeReader(IReadOnlyList<object?[]> rows) : DbDataReader
    {
        private int _index = -1;

        public override int Depth => 0;

        public override int FieldCount => rows.Count == 0 ? 0 : rows[0].Length;

        public override bool HasRows => rows.Count > 0;

        public override bool IsClosed => false;

        public override int RecordsAffected => -1;

        public override object this[int ordinal] => GetValue(ordinal);

        public override object this[string name] => throw new NotSupportedException();

        public override bool Read() => ++_index < rows.Count;

        public override bool NextResult() => false;

        public override object GetValue(int ordinal) => rows[_index][ordinal] ?? DBNull.Value;

        public override bool IsDBNull(int ordinal) => rows[_index][ordinal] is null or DBNull;

        public override string GetString(int ordinal) => (string)GetValue(ordinal);

        public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));

        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal));

        public override byte GetByte(int ordinal) => throw new NotSupportedException();

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
            throw new NotSupportedException();

        public override char GetChar(int ordinal) => throw new NotSupportedException();

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            throw new NotSupportedException();

        public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();

        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();

        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();

        public override double GetDouble(int ordinal) => throw new NotSupportedException();

        public override Type GetFieldType(int ordinal) => throw new NotSupportedException();

        public override float GetFloat(int ordinal) => throw new NotSupportedException();

        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();

        public override short GetInt16(int ordinal) => throw new NotSupportedException();

        public override long GetInt64(int ordinal) => throw new NotSupportedException();

        public override string GetName(int ordinal) => throw new NotSupportedException();

        public override int GetOrdinal(string name) => throw new NotSupportedException();

        public override int GetValues(object[] values) => throw new NotSupportedException();

        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    }
}
