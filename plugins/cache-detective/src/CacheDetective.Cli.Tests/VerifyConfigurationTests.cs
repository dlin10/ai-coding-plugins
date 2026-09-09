using CacheDetective.Configuration;
using CacheDetective.Mcp;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// The <c>verify</c> section and the redaction masks beside it. Both are read from a file that is
/// committed, so both are refused rather than tolerated when they say something the schema does not know
/// or carry a secret they should have referenced.
/// </summary>
public sealed class VerifyConfigurationTests
{
    [Fact]
    public async Task A_full_verify_section_is_read()
    {
        var configuration = await ReadAsync("""
            {
              "redis": "env:CD_VERIFY_REDIS",
              "database": "env:CD_VERIFY_DB",
              "auto": true,
              "stores": ["redis"],
              "keyPrefix": "shop:",
              "clockMarginSeconds": 15,
              "tables": { "dbo.Products": { "key": "Id", "from": "id" } }
            }
            """);

        var verify = configuration.Verify!;
        Assert.Equal("env:CD_VERIFY_REDIS", verify.Redis);
        Assert.Equal("env:CD_VERIFY_DB", verify.Database);
        Assert.True(verify.Auto);
        Assert.Equal(["redis"], verify.Stores);
        Assert.Equal("shop:", verify.KeyPrefix);
        Assert.Equal(15, verify.ClockMarginSeconds);
        var table = Assert.Single(verify.Tables!);
        Assert.Equal("dbo.Products", table.Key);
        Assert.Equal("Id", table.Value.Key);
        Assert.Equal("id", table.Value.From);
    }

    [Fact]
    public async Task A_connection_string_instead_of_an_env_reference_is_refused()
    {
        var error = await AssertRejectedAsync("""{ "redis": "localhost:6379,password=hunter2" }""");

        Assert.Contains("env:", error.Message, StringComparison.Ordinal);
        Assert.Contains("committed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_field_is_refused()
    {
        var error = await AssertRejectedAsync("""{ "redis": "env:CD_VERIFY_REDIS", "sample": 100 }""");

        Assert.Contains("sample", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_verify_section_is_not_an_error()
    {
        using var repository = new TemporaryRepository();
        await WriteAsync(repository, """{ "version": 1, "root": ".", "solutions": [], "budgets": {} }""");

        var configuration = await WorkspaceConfigurationStore.ReadAsync(repository.Path);

        Assert.Null(configuration.Verify);
        Assert.Null(configuration.Sensitive);
    }

    [Fact]
    public async Task An_unset_environment_variable_names_itself()
    {
        var configuration = await ReadAsync("""{ "redis": "env:CD_VERIFY_REDIS_ABSENT" }""");

        var error = Assert.Throws<InvalidOperationException>(() => configuration.Verify!.ResolveRedis());

        Assert.Contains("CD_VERIFY_REDIS_ABSENT", error.Message, StringComparison.Ordinal);
        Assert.Contains("redis", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auto_is_visible_in_workspace_status()
    {
        using var repository = new TemporaryRepository();
        await WriteAsync(repository, """
            { "version": 1, "root": ".", "solutions": [], "budgets": {}, "verify": { "auto": true } }
            """);
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, null, null);

        var status = await session.GetStatusAsync();

        Assert.True(status.VerifyAuto);
    }

    [Fact]
    public async Task A_table_with_a_key_and_no_from_is_refused()
    {
        var error = await AssertRejectedAsync("""{ "tables": { "dbo.Products": { "key": "Id" } } }""");

        Assert.Contains("dbo.Products", error.Message, StringComparison.Ordinal);
        Assert.Contains("'key' without 'from'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_table_with_a_from_and_no_key_is_refused()
    {
        var error = await AssertRejectedAsync("""{ "tables": { "dbo.Products": { "from": "id" } } }""");

        Assert.Contains("dbo.Products", error.Message, StringComparison.Ordinal);
        Assert.Contains("'from' without 'key'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_store_outside_the_list_is_not_verified()
    {
        var configuration = await ReadAsync("""{ "stores": ["redis"] }""");

        Assert.True(configuration.Verify!.Verifies("redis"));
        Assert.False(configuration.Verify.Verifies("distributed"));
        Assert.False(configuration.Verify.Verifies("memory"));
    }

    [Fact]
    public async Task Stores_defaults_to_redis_and_distributed()
    {
        var configuration = await ReadAsync("""{ "auto": true }""");

        Assert.Equal(["redis", "distributed"], configuration.Verify!.Stores);
        Assert.True(configuration.Verify.Verifies("Redis"));
        Assert.False(configuration.Verify.Verifies("memory"));
    }

    [Fact]
    public async Task KeyPrefix_defaults_to_empty()
    {
        var configuration = await ReadAsync("""{ "auto": true }""");

        Assert.Equal(string.Empty, configuration.Verify!.KeyPrefix);
    }

    [Fact]
    public async Task ClockMarginSeconds_defaults_to_sixty()
    {
        var configuration = await ReadAsync("""{ "auto": true }""");

        Assert.Equal(60, configuration.Verify!.ClockMarginSeconds);
    }

    [Fact]
    public void The_default_masks_are_the_documented_five()
    {
        Assert.Equal(["email", "phone", "*password*", "*token*", "*secret*"], SensitiveFields.Masks(null));
        Assert.True(SensitiveFields.IsSensitive("email", null));
        Assert.True(SensitiveFields.IsSensitive("phone", null));
        Assert.False(SensitiveFields.IsSensitive("name", null));
    }

    [Fact]
    public void A_configured_mask_is_added_to_the_defaults()
    {
        string[] configured = ["*apikey*"];

        Assert.True(SensitiveFields.IsSensitive("CustomerApiKey", configured));
        // The defaults are not replaced by what the workspace adds.
        Assert.True(SensitiveFields.IsSensitive("email", configured));
        Assert.Equal([.. SensitiveFields.Default, "*apikey*"], SensitiveFields.Masks(configured));
    }

    [Fact]
    public void Masks_match_without_regard_to_case()
    {
        Assert.True(SensitiveFields.IsSensitive("EMAIL", null));
        Assert.True(SensitiveFields.IsSensitive("Phone", null));
        Assert.True(SensitiveFields.IsSensitive("UserPASSWORDHash", null));
    }

    [Fact]
    public void A_mask_with_a_star_matches_any_substring()
    {
        Assert.True(SensitiveFields.IsSensitive("password", null));
        Assert.True(SensitiveFields.IsSensitive("hashed_password_v2", null));
        Assert.True(SensitiveFields.IsSensitive("refreshToken", null));
        // A mask without a star is the whole name and not a part of it.
        Assert.False(SensitiveFields.IsSensitive("emailAddress", null));
    }

    private static async Task<WorkspaceConfiguration> ReadAsync(string verify)
    {
        using var repository = new TemporaryRepository();
        await WriteAsync(repository, $$"""
            { "version": 1, "root": ".", "solutions": [], "budgets": {}, "verify": {{verify}} }
            """);
        return await WorkspaceConfigurationStore.ReadAsync(repository.Path);
    }

    private static async Task<Exception> AssertRejectedAsync(string verify)
    {
        using var repository = new TemporaryRepository();
        await WriteAsync(repository, $$"""
            { "version": 1, "root": ".", "solutions": [], "budgets": {}, "verify": {{verify}} }
            """);

        return await Assert.ThrowsAnyAsync<Exception>(() => WorkspaceConfigurationStore.ReadAsync(repository.Path));
    }

    private static async Task WriteAsync(TemporaryRepository repository, string json)
    {
        var path = WorkspaceConfigurationStore.GetPath(repository.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>Two entries that differ only in case name one table, and the reading side matches names
    /// without regard to case, so the pair is refused rather than one of them silently dropped.</summary>
    [Fact]
    public void Two_verify_tables_differing_only_in_case_are_refused()
    {
        var verify = new VerifyConfiguration
        {
            Redis = "env:CD_R",
            Database = "env:CD_D",
            Tables = new Dictionary<string, VerifyTableConfiguration>(StringComparer.Ordinal)
            {
                ["dbo.Products"] = new() { Key = "Id", From = "id" },
                ["DBO.PRODUCTS"] = new() { Key = "Id", From = "id" }
            }
        };

        var error = Assert.Throws<InvalidDataException>(() => verify.ResolveRedis());

        Assert.Contains("differ only in", error.Message, StringComparison.Ordinal);
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
                Directory.Delete(Path, recursive: true);
        }
    }
}
