using System.Text.Json;
using CacheDetective.Configuration;
using Xunit;

namespace CacheDetective.Tests;

public sealed class WorkspaceConfigTests
{
    [Fact]
    public async Task WorkspaceConfig_round_trip_preserves_later_phase_fields()
    {
        using var source = new TemporaryRepository();
        using var destination = new TemporaryRepository();
        await WriteConfigAsync(source, """
            {
              "version": 1,
              "root": "..",
              "solutions": ["src/App.sln", "tests/App.Tests.csproj"],
              "budgets": { "dbo.*": 90, "dbo.Products": 15 },
              "databases": [{ "name": "shop", "connection": "env:CD_SHOP_CONN" }],
              "services": { "catalog": "Catalog.API" },
              "verify": ["schema", { "enabled": true }],
              "sensitive": { "tables": ["dbo.Users"], "redact": true }
            }
            """);

        var configuration = await WorkspaceConfigurationStore.ReadAsync(source.Path);
        Assert.True(await WorkspaceConfigurationStore.WriteAsync(destination.Path, configuration));
        Assert.False(await WorkspaceConfigurationStore.WriteAsync(destination.Path, configuration));
        var roundTripped = await WorkspaceConfigurationStore.ReadAsync(destination.Path);

        Assert.Equal(configuration.Root, roundTripped.Root);
        Assert.Equal(configuration.Solutions, roundTripped.Solutions);
        Assert.Equal(configuration.Budgets, roundTripped.Budgets);
        var database = Assert.Single(roundTripped.Databases!);
        Assert.Equal("shop", database.Name);
        Assert.Equal("env:CD_SHOP_CONN", database.Connection);
        Assert.Null(database.Provider);
        Assert.Equal("Catalog.API", Assert.Single(roundTripped.Services!).Value);
        AssertJsonEqual(configuration.Verify, roundTripped.Verify);
        AssertJsonEqual(configuration.Sensitive, roundTripped.Sensitive);
    }

    [Fact]
    public async Task WorkspaceConfig_two_databases_are_rejected_with_both_names()
    {
        var exception = await AssertRejectedAsync("""
            {
              "version": 1, "root": ".", "solutions": [], "budgets": {},
              "databases": [
                { "name": "shop", "connection": "env:CD_SHOP_CONN" },
                { "name": "warehouse", "connection": "env:CD_WAREHOUSE_CONN" }
              ]
            }
            """);

        Assert.Contains("one database", exception.Message, StringComparison.Ordinal);
        Assert.Contains("shop", exception.Message, StringComparison.Ordinal);
        Assert.Contains("warehouse", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceConfig_connection_string_is_rejected_in_favour_of_an_env_reference()
    {
        var exception = await AssertRejectedAsync("""
            {
              "version": 1, "root": ".", "solutions": [], "budgets": {},
              "databases": [{ "name": "shop", "connection": "Server=.;Database=Shop;Trusted_Connection=True" }]
            }
            """);

        Assert.Contains("env:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("workspace.json is committed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceConfig_other_provider_is_rejected_by_naming_sql_server()
    {
        var exception = await AssertRejectedAsync("""
            {
              "version": 1, "root": ".", "solutions": [], "budgets": {},
              "databases": [{ "name": "main", "provider": "postgres" }]
            }
            """);

        Assert.Contains("postgres", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SQL Server", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceConfig_database_of_the_wrong_shape_is_rejected()
    {
        var missingConnection = await AssertRejectedAsync("""
            {
              "version": 1, "root": ".", "solutions": [], "budgets": {},
              "databases": [{ "name": "shop" }]
            }
            """);
        Assert.Contains("no 'connection'", missingConnection.Message, StringComparison.Ordinal);

        // The name is what index_database selects by and what labels the catalogue half of the graph,
        // so a nameless record cannot be honoured either.
        var missingName = await AssertRejectedAsync("""
            {
              "version": 1, "root": ".", "solutions": [], "budgets": {},
              "databases": [{ "connection": "env:CD_SHOP_CONN" }]
            }
            """);
        Assert.Contains("no 'name'", missingName.Message, StringComparison.Ordinal);
        Assert.Contains("index_database", missingName.Message, StringComparison.Ordinal);

        using var repository = new TemporaryRepository();
        await WriteConfigAsync(repository, """
            {
              "version": 1, "root": ".", "solutions": [], "budgets": {},
              "databases": [{ "name": "shop", "connection": "env:CD_SHOP_CONN", "readonly": true }]
            }
            """);
        var unknownField = await Assert.ThrowsAsync<JsonException>(
            () => WorkspaceConfigurationStore.ReadAsync(repository.Path));
        Assert.Contains("readonly", unknownField.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceConfig_missing_environment_variable_is_named_in_the_error()
    {
        using var repository = new TemporaryRepository();
        await WriteConfigAsync(repository, """
            {
              "version": 1, "root": ".", "solutions": [], "budgets": {},
              "databases": [{ "name": "shop", "connection": "env:CD_MISSING" }]
            }
            """);

        var configuration = await WorkspaceConfigurationStore.ReadAsync(repository.Path);
        var database = Assert.Single(configuration.Databases!);
        var exception = Assert.Throws<InvalidOperationException>(() => database.ResolveConnectionString());

        Assert.Contains("CD_MISSING", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceConfig_without_a_database_reads_and_writes_as_before()
    {
        using var repository = new TemporaryRepository();
        using var destination = new TemporaryRepository();
        await WriteConfigAsync(repository, """
            {
              "version": 1, "root": ".", "solutions": ["src/App.sln"], "budgets": {}, "databases": []
            }
            """);

        var configuration = await WorkspaceConfigurationStore.ReadAsync(repository.Path);
        Assert.Empty(configuration.Databases!);
        Assert.True(await WorkspaceConfigurationStore.WriteAsync(destination.Path, configuration));
        Assert.False(await WorkspaceConfigurationStore.WriteAsync(destination.Path, configuration));
        Assert.Empty((await WorkspaceConfigurationStore.ReadAsync(destination.Path)).Databases!);
    }

    [Fact]
    public void WorkspaceConfig_budget_lookup_prefers_exact_then_mask_then_default()
    {
        var configuration = new WorkspaceConfiguration
        {
            Budgets = new Dictionary<string, double>
            {
                ["dbo.*"] = 120,
                ["dbo.Products"] = 30
            }
        };

        Assert.Equal(30, configuration.GetBudgetSeconds("dbo.Products"));
        Assert.Equal(120, configuration.GetBudgetSeconds("dbo.Orders"));
        Assert.Equal(60, configuration.GetBudgetSeconds("sales.Orders"));
    }

    [Fact]
    public async Task WorkspaceConfig_unknown_version_is_rejected_with_version_in_message()
    {
        using var repository = new TemporaryRepository();
        Directory.CreateDirectory(Path.GetDirectoryName(WorkspaceConfigurationStore.GetPath(repository.Path))!);
        await File.WriteAllTextAsync(WorkspaceConfigurationStore.GetPath(repository.Path), """
            { "version": 999, "root": ".", "solutions": [], "budgets": {} }
            """);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => WorkspaceConfigurationStore.ReadAsync(repository.Path));

        Assert.Contains("999", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<InvalidDataException> AssertRejectedAsync(string json)
    {
        using var repository = new TemporaryRepository();
        await WriteConfigAsync(repository, json);

        return await Assert.ThrowsAsync<InvalidDataException>(
            () => WorkspaceConfigurationStore.ReadAsync(repository.Path));
    }

    private static async Task WriteConfigAsync(TemporaryRepository repository, string json)
    {
        var path = WorkspaceConfigurationStore.GetPath(repository.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json);
    }

    private static void AssertJsonEqual(JsonElement? expected, JsonElement? actual)
    {
        Assert.True(expected.HasValue);
        Assert.True(actual.HasValue);
        Assert.True(JsonElement.DeepEquals(expected.Value, actual.Value));
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cache-detective-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
