using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace CacheDetective.Tests.Database;

/// <summary>
/// A fact that cannot run without a live SQL Server. With <c>CD_TEST_SQL_CONN</c> unset the test is
/// reported as <em>skipped</em>, carrying the reason and what to do about it — it does not fail, and it
/// does not pass while having checked nothing. xunit 2 decides this at discovery, so the attribute sets
/// <see cref="FactAttribute.Skip"/> in its constructor.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresSqlServerFactAttribute : FactAttribute
{
    public RequiresSqlServerFactAttribute(string purpose)
    {
        if (SqlServerHarness.ConfiguredConnectionString is null)
        {
            Skip = $"{SqlServerHarness.ConnectionVariable} is not set. {purpose}";
        }
    }
}

/// <summary>
/// A throwaway database on a live server, for the tests that cannot be honest without one. It is shared
/// by both test projects through a linked compile item rather than a project reference, because a test
/// project referencing another test project is worse than one file in two compilations.
/// <para>Everything here is gated on <c>CD_TEST_SQL_CONN</c>. When that variable is unset the callers
/// skip — they do not pass quietly.</para>
/// </summary>
internal sealed class SqlServerHarness : IAsyncDisposable
{
    internal const string ConnectionVariable = "CD_TEST_SQL_CONN";

    private readonly string _server;
    private string? _login;

    private SqlServerHarness(string server, string database)
    {
        _server = server;
        Database = database;
    }

    /// <summary>The database created for this run.</summary>
    internal string Database { get; }

    internal static string? ConfiguredConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable);

    internal static async Task<SqlServerHarness> CreateAsync(CancellationToken cancellationToken = default)
    {
        var configured = ConfiguredConnectionString
            ?? throw new InvalidOperationException($"{ConnectionVariable} is not set.");
        var database = $"cd_test_{Guid.NewGuid():N}";
        var harness = new SqlServerHarness(configured, database);

        await harness.ExecuteAsync("master", $"CREATE DATABASE [{database}];", cancellationToken)
                     .ConfigureAwait(false);
        return harness;
    }

    /// <summary>Runs a T-SQL script against the throwaway database, one <c>GO</c>-separated batch at a
    /// time, as a client tool would.</summary>
    internal async Task ApplyAsync(string scriptPath, CancellationToken cancellationToken = default)
    {
        var script = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
        foreach (var batch in SplitBatches(script))
        {
            await ExecuteAsync(Database, batch, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates a login with the rights the README asks a user for, and nothing else: <c>VIEW
    /// DEFINITION</c> on the schema plus read access to the dependency catalogue, and no <c>SELECT</c> on
    /// any user table. Indexing under it turns the read-only claim into something the server enforces
    /// rather than something the code asserts about itself.
    /// </summary>
    internal async Task<string> GrantCatalogueOnlyLoginAsync(CancellationToken cancellationToken = default)
    {
        var login = $"cd_reader_{Guid.NewGuid():N}";
        var password = $"P{Guid.NewGuid():N}!aA1";
        await ExecuteAsync("master",
            $"CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF;",
            cancellationToken).ConfigureAwait(false);
        _login = login;

        // VIEW DEFINITION is granted on the database, not on the schema, and the difference is not
        // cosmetic: measured on SQL Server 2019, a login holding it only on SCHEMA::dbo sees zero rows
        // in sys.sql_expression_dependencies, so every procedure-to-procedure call disappears. These are
        // the rights the README asks a user for, so they have to be the rights this test runs under.
        await ExecuteAsync(Database, $"""
            CREATE USER [{login}] FOR LOGIN [{login}];
            GRANT VIEW DEFINITION ON DATABASE::[{Database}] TO [{login}];
            GRANT SELECT ON sys.sql_expression_dependencies TO [{login}];
            """, cancellationToken).ConfigureAwait(false);

        return Connection(Database, login, password);
    }

    internal async Task<SqlConnection> OpenAsync(string? connectionString = null,
                                                  CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(connectionString ?? Connection(Database));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    internal string Connection(string database, string? login = null, string? password = null)
    {
        var builder = new SqlConnectionStringBuilder(_server) { InitialCatalog = database };
        if (login is not null)
        {
            builder.IntegratedSecurity = false;
            builder.UserID = login;
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    internal static IEnumerable<string> SplitBatches(string script) =>
        Regex.Split(script, @"^\s*GO\s*$",
                    RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
             .Select(batch => batch.Trim())
             .Where(batch => batch.Length > 0);

    /// <summary>Walks up from the test binaries to a file in the repository, so the demo stand can be
    /// found without hard-coding a checkout layout.</summary>
    internal static string FindRepositoryFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(relativePath)}.");
    }

    /// <summary>Takes the database and the login away again, whether the test passed or threw.</summary>
    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await TryExecuteAsync("master", $"""
            IF DB_ID(N'{Database}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{Database}];
            END
            """).ConfigureAwait(false);

        if (_login is not null)
        {
            await TryExecuteAsync("master", $"""
                IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{_login}')
                    DROP LOGIN [{_login}];
                """).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(string database, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(Connection(database));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TryExecuteAsync(string database, string sql)
    {
        try
        {
            await ExecuteAsync(database, sql, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqlException)
        {
            // Cleanup is best effort: a server that cannot be reached any more has nothing to clean.
        }
    }
}
