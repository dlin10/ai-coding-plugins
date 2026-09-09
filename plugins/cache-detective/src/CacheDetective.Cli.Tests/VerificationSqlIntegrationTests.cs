using System.Data;
using System.Data.Common;
using System.Text.Json;
using CacheDetective.Configuration;
using CacheDetective.Tests.Database;
using CacheDetective.Verification;
using Microsoft.Data.SqlClient;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// A fact that cannot run without a live SQL Server. With <c>CD_TEST_SQL_CONN</c> unset the test is
/// reported as <em>skipped</em>, carrying the reason — it does not fail, and it does not pass while having
/// checked nothing.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresSqlFactAttribute : FactAttribute
{
    public RequiresSqlFactAttribute()
    {
        if (SqlServerHarness.ConfiguredConnectionString is null)
        {
            Skip = $"{SqlServerHarness.ConnectionVariable} is not set. Set it to a SQL Server connection string to run the " +
                   "verification integration tests.";
        }
    }
}

/// <summary>
/// The database half of verification against a real server with known rows in it.
/// <para>There is no separate login here and none is needed. Whether a minimal grant is sufficient is not
/// what this phase proves — the run is assumed to be administrative — and the three permission outcomes
/// are already covered by the unit tests of task 6, which hand error 297 straight to a fake connection.
/// What is proved here is the one thing that can only be proved on a live server and must be: that
/// verification reads and never writes.</para>
/// </summary>
public sealed class VerificationSqlIntegrationTests
{
    private const string Schema = "verify";
    private const string Declared = "Products";
    private const string Undeclared = "Secrets";

    private static readonly VerifyTableConfiguration Configured = new() { Key = "Id", From = "id" };

    [RequiresSqlFact]
    public async Task The_duration_since_the_last_write_is_read()
    {
        await using var fixture = await SeededFixture.CreateAsync();

        var lastWrite = await fixture.Reader.ReadLastWriteAsync(Schema, Declared, default);

        Assert.Null(lastWrite.Reason);
        Assert.NotNull(lastWrite.SecondsSinceLastWrite);
        Assert.InRange(lastWrite.SecondsSinceLastWrite!.Value, 0, 600);
    }

    [RequiresSqlFact]
    public async Task The_row_of_a_declared_table_is_read()
    {
        await using var fixture = await SeededFixture.CreateAsync();
        using var cached = JsonDocument.Parse("""{ "price": 19.5, "name": "Widget" }""");

        var comparison = await fixture.Reader.CompareRowAsync(Schema, Declared, Configured, "product:{id}",
                                                              Placeholders("7"), cached.RootElement, default);

        Assert.Null(comparison.Reason);
        Assert.Equal(FieldVerdict.Equal, Assert.Single(comparison.Fields, field => field.Field == "price").Verdict);
        Assert.Equal(FieldVerdict.Equal, Assert.Single(comparison.Fields, field => field.Field == "name").Verdict);
    }

    [RequiresSqlFact]
    public async Task A_table_outside_the_configuration_is_never_read()
    {
        await using var fixture = await SeededFixture.CreateAsync();
        using var cached = JsonDocument.Parse("""{ "price": 19.5, "name": "Widget" }""");

        await fixture.Reader.ReadLastWriteAsync(Schema, Declared, default);
        await fixture.Reader.CompareRowAsync(Schema, Declared, Configured, "product:{id}",
                                             Placeholders("7"), cached.RootElement, default);

        Assert.NotEmpty(fixture.CommandTexts);
        Assert.All(fixture.CommandTexts, sql => Assert.DoesNotContain(Undeclared, sql, StringComparison.OrdinalIgnoreCase));
    }

    [RequiresSqlFact]
    public async Task The_seeded_rows_are_unchanged_in_every_column()
    {
        await using var fixture = await SeededFixture.CreateAsync();
        var before = await fixture.ReadEverythingAsync();
        using var cached = JsonDocument.Parse("""{ "price": 19.5, "name": "Widget" }""");

        await fixture.Reader.ReadLastWriteAsync(Schema, Declared, default);
        await fixture.Reader.CompareRowAsync(Schema, Declared, Configured, "product:{id}",
                                             Placeholders("7"), cached.RootElement, default);

        Assert.Equal(before, await fixture.ReadEverythingAsync());
    }

    [RequiresSqlFact]
    public async Task No_statement_that_was_sent_writes()
    {
        string[] verbs = ["INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE", "EXEC", "GRANT"];
        await using var fixture = await SeededFixture.CreateAsync();
        using var cached = JsonDocument.Parse("""{ "price": 19.5, "name": "Widget" }""");

        await fixture.Reader.ReadLastWriteAsync(Schema, Declared, default);
        await fixture.Reader.CompareRowAsync(Schema, Declared, Configured, "product:{id}",
                                             Placeholders("7"), cached.RootElement, default);

        Assert.NotEmpty(fixture.CommandTexts);
        Assert.All(fixture.CommandTexts, sql => Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.Ordinal));
        // Compared as whole words, so that "last_user_update" is not read as an UPDATE.
        Assert.All(fixture.CommandTexts, sql => Assert.All(Words(sql), word => Assert.DoesNotContain(word, verbs)));
    }

    private static Dictionary<string, string> Placeholders(string value) =>
        new(StringComparer.Ordinal) { ["id"] = value };

    private static string[] Words(string sql) =>
        System.Text.RegularExpressions.Regex.Matches(sql, "[A-Za-z_][A-Za-z0-9_]*")
              .Select(match => match.Value.ToUpperInvariant())
              .ToArray();

    /// <summary>
    /// A throwaway database with two tables in it: one the workspace declares and one it does not. The
    /// schema is created and seeded on its own connection, and verification reads on another, so nothing
    /// the fixture did can be mistaken for something the reader did.
    /// </summary>
    private sealed class SeededFixture : IAsyncDisposable
    {
        private readonly SqlServerHarness _harness;
        private readonly RecordingConnection _connection;

        private SeededFixture(SqlServerHarness harness, RecordingConnection connection)
        {
            _harness = harness;
            _connection = connection;
            Reader = new VerificationReader(connection);
        }

        internal VerificationReader Reader { get; }

        internal IReadOnlyList<string> CommandTexts => _connection.CommandTexts;

        internal static async Task<SeededFixture> CreateAsync()
        {
            var harness = await SqlServerHarness.CreateAsync();
            await using (var seeding = await harness.OpenAsync())
            {
                foreach (var statement in SchemaScript())
                {
                    await using var command = seeding.CreateCommand();
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync();
                }
            }

            var connection = new RecordingConnection(new SqlConnection(harness.Connection(harness.Database)));
            await connection.OpenAsync();
            return new SeededFixture(harness, connection);
        }

        /// <summary>Every column of every seeded row of both tables, so that "unchanged" means what it
        /// says rather than "unchanged in the columns we thought to look at".</summary>
        internal async Task<string> ReadEverythingAsync()
        {
            await using var reading = await _harness.OpenAsync();
            var rows = new List<string>();
            foreach (var table in new[] { Declared, Undeclared })
            {
                await using var command = reading.CreateCommand();
                command.CommandText = $"SELECT * FROM [{Schema}].[{table}] ORDER BY Id";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rows.Add($"{table}|" + string.Join('|', Enumerable.Range(0, reader.FieldCount)
                                                                     .Select(ordinal => $"{reader.GetName(ordinal)}={Describe(reader, ordinal)}")));
                }
            }

            return string.Join('\n', rows);
        }

        private static string Describe(DbDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? "NULL" : Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";

        private static string[] SchemaScript() =>
        [
            $"CREATE SCHEMA [{Schema}]",
            $"CREATE TABLE [{Schema}].[{Declared}] (Id int NOT NULL PRIMARY KEY, price decimal(10,2) NOT NULL, name nvarchar(64) NOT NULL)",
            $"CREATE TABLE [{Schema}].[{Undeclared}] (Id int NOT NULL PRIMARY KEY, token nvarchar(64) NOT NULL)",
            $"INSERT INTO [{Schema}].[{Declared}] (Id, price, name) VALUES (7, 19.50, N'Widget'), (8, 3.25, N'Bolt')",
            $"INSERT INTO [{Schema}].[{Undeclared}] (Id, token) VALUES (1, N'never-read')"
        ];

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            await _harness.DisposeAsync();
        }
    }

    /// <summary>
    /// A real connection with a notebook. Every command the reader creates comes through here, so the
    /// recorded texts are the whole of what it sent — the same claim the fake-connection unit tests make,
    /// made this time against a server that would have obeyed a write.
    /// </summary>
    private sealed class RecordingConnection(SqlConnection inner) : DbConnection
    {
        private readonly List<string> _commandTexts = [];

        internal IReadOnlyList<string> CommandTexts => _commandTexts;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => inner.ConnectionString; set => inner.ConnectionString = value; }

        public override string Database => inner.Database;

        public override string DataSource => inner.DataSource;

        public override string ServerVersion => inner.ServerVersion;

        public override ConnectionState State => inner.State;

        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);

        public override void Close() => inner.Close();

        public override void Open() => inner.Open();

        public override Task OpenAsync(CancellationToken cancellationToken) => inner.OpenAsync(cancellationToken);

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new RecordingCommand(inner.CreateCommand(), _commandTexts);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class RecordingCommand(SqlCommand inner, List<string> commandTexts) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }

        public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }

        public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }

        public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }

        public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }

        protected override DbConnection? DbConnection { get => inner.Connection; set => throw new NotSupportedException(); }

        protected override DbParameterCollection DbParameterCollection => inner.Parameters;

        protected override DbTransaction? DbTransaction { get => inner.Transaction; set => throw new NotSupportedException(); }

        public override void Cancel() => inner.Cancel();

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare() => inner.Prepare();

        protected override DbParameter CreateDbParameter() => inner.CreateParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            commandTexts.Add(inner.CommandText);
            return inner.ExecuteReader(behavior);
        }

        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            commandTexts.Add(inner.CommandText);
            return await inner.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
