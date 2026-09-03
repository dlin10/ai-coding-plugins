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
        Directory.CreateDirectory(Path.GetDirectoryName(WorkspaceConfigurationStore.GetPath(source.Path))!);
        await File.WriteAllTextAsync(WorkspaceConfigurationStore.GetPath(source.Path), """
            {
              "version": 1,
              "root": "..",
              "solutions": ["src/App.sln", "tests/App.Tests.csproj"],
              "budgets": { "dbo.*": 90, "dbo.Products": 15 },
              "databases": [{ "name": "main", "provider": "postgres" }],
              "services": { "catalog": { "url": "https://example.invalid" } },
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
        AssertJsonEqual(configuration.Databases, roundTripped.Databases);
        AssertJsonEqual(configuration.Services, roundTripped.Services);
        AssertJsonEqual(configuration.Verify, roundTripped.Verify);
        AssertJsonEqual(configuration.Sensitive, roundTripped.Sensitive);
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
