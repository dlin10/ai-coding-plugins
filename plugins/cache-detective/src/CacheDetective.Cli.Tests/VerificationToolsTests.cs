using System.Text.Json;
using CacheDetective.Configuration;
using CacheDetective.Graph;
using CacheDetective.Mcp;
using CacheDetective.Rules;
using CacheDetective.Serialization;
using CacheDetective.Verification;
using StackExchange.Redis;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// The <c>verify_finding</c> tool: one run per finding per graph version, remembered, paged by sampled key,
/// and never larger than the response limit. Everything that stops a run before a reading comes back as a
/// successful answer carrying <c>not_verifiable</c> and a code, because a caller is owed an account of why
/// there is no verification rather than an error.
/// </summary>
/// <remarks>One of these connects to the live server named by <c>CD_TEST_REDIS</c>, so the whole class
/// joins the collection that runs one at a time — <c>RedisIntegrationTests</c> compares that database's
/// whole keyspace across a window, and a key written from here while the window is open would read as a
/// change the verifier made.</remarks>
[Collection(LiveRedisCollection.Name)]
public sealed class VerificationToolsTests
{
    private const string Template = "product:{id}";
    private const string RedisVariable = "CD_TEST_VERIFY_REDIS";

    [Fact]
    public async Task An_unknown_finding_id_is_refused()
    {
        using var repository = new TemporaryRepository();
        var session = await SessionAsync(repository);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.VerifyFindingAsync("f:999", false, null));

        Assert.Contains("f:999", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workspace_with_no_verify_section_is_not_verifiable_with_a_code()
    {
        using var repository = new TemporaryRepository();
        var session = await SessionAsync(repository);

        var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null);

        Assert.Equal("not_verifiable", result.Observation);
        Assert.Equal(VerificationFailure.NotConfigured, result.Reason);
        Assert.Empty(result.Keys.Items);
    }

    /// <summary>
    /// A <c>STALE_PARENT_KEY</c> finding is a claim about the parent, and the parent is what gets read.
    /// The catalogue puts the <em>child's</em> template in <c>keyTemplate</c>, so choosing the subject from
    /// that field read the child instead: a healthy child could be refuted in the parent's name, which is a
    /// refutation through a different key and is what R8 forbids.
    /// <para>The two keys are put in different stores and only the child's store is verified, so which one
    /// the run chose is visible in the refusal it gives — and the run stops before it opens anything.</para>
    /// </summary>
    [Fact]
    public async Task A_stale_parent_finding_reads_the_parent_key_and_not_the_child()
    {
        using var repository = new TemporaryRepository();
        Environment.SetEnvironmentVariable(RedisVariable, "127.0.0.1:6390,abortConnect=false,connectTimeout=200,connectRetry=1");
        try
        {
            Environment.SetEnvironmentVariable("CD_TEST_VERIFY_DB", "Server=(local);Database=shop;Integrated Security=true");
            var session = await SessionAsync(repository,
                $$"""{ "redis": "env:{{RedisVariable}}", "database": "env:CD_TEST_VERIFY_DB", "stores": ["redis"] }""");
            PlantStaleParent(session.Graph);
            var finding = await FindingIdAsync(session, StaleParentKeyFinding.Rule);

            var result = await session.VerifyFindingAsync(finding, false, null);

            Assert.Equal("not_verifiable", result.Observation);
            Assert.NotEqual("refuted", result.Observation);

            // The parent lives in 'memory', which verify.stores does not list; the child lives in 'redis',
            // which it does. Naming 'memory' is the proof that the parent was the subject.
            Assert.Contains("memory", result.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RedisVariable, null);
        }
    }

    /// <summary>
    /// A database connection string that is not one. It used to be parsed deep inside the reading, after
    /// the cache had been scanned, and the <c>ArgumentException</c> it throws is not one the verification
    /// classifies — so it escaped past the sample already in hand to an empty refusal that did not even say
    /// the run was partial. Settling it before anything is opened costs the cache no commands at all.
    /// </summary>
    [Fact]
    public async Task A_malformed_database_connection_string_answers_before_the_cache_is_opened()
    {
        using var repository = new TemporaryRepository();
        Environment.SetEnvironmentVariable(RedisVariable, "127.0.0.1:6390,abortConnect=false,connectTimeout=200,connectRetry=1");
        try
        {
            Environment.SetEnvironmentVariable("CD_TEST_VERIFY_DB", "Server=(local);Integrated Security=yes please;;=;");
            var session = await SessionAsync(repository,
                $$"""{ "redis": "env:{{RedisVariable}}", "database": "env:CD_TEST_VERIFY_DB" }""");
            var opened = false;
            session.OpenCacheReader = (_, _, _) =>
            {
                opened = true;
                return new RedisReader((_, _) => Task.FromResult(RedisResult.Create((RedisValue)"string")));
            };

            var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null);

            Assert.Equal("not_verifiable", result.Observation);
            Assert.Equal(VerificationFailure.NotConfigured, result.Reason);
            Assert.False(opened, "the cache was opened for a run that could never have reached the database");
        }
        finally
        {
            Environment.SetEnvironmentVariable(RedisVariable, null);
        }
    }

    /// <summary>A real connection attempt against a port nothing listens on, with the client told not to
    /// abort so the failure arrives as a disconnected multiplexer rather than a throw.</summary>
    [Fact]
    public async Task An_unreachable_cache_is_not_verifiable_with_a_code()
    {
        using var repository = new TemporaryRepository();
        Environment.SetEnvironmentVariable(RedisVariable, "127.0.0.1:6390,abortConnect=false,connectTimeout=200,connectRetry=1");
        try
        {
            Environment.SetEnvironmentVariable("CD_TEST_VERIFY_DB", "Server=(local);Database=shop;Integrated Security=true");
            var session = await SessionAsync(repository, $$"""{ "redis": "env:{{RedisVariable}}", "database": "env:CD_TEST_VERIFY_DB" }""");

            var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null);

            Assert.Equal("not_verifiable", result.Observation);
            Assert.Equal(VerificationFailure.CacheUnavailable, result.Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RedisVariable, null);
        }
    }

    /// <summary>
    /// Whether verification begins at all is settled before a connection is opened. A key held in a store
    /// the workspace did not list is owed that reason, not "the cache was unreachable" — and the Redis
    /// client is never contacted, so its own housekeeping commands are not sent for a run that was going
    /// to read nothing. The cache here is deliberately unreachable: if the order were still wrong, the
    /// answer would be <c>cache_unavailable</c>.
    /// </summary>
    [Fact]
    public async Task A_store_outside_the_configured_list_is_refused_before_the_cache_is_contacted()
    {
        using var repository = new TemporaryRepository();
        Environment.SetEnvironmentVariable(RedisVariable, "127.0.0.1:6390,abortConnect=false,connectTimeout=200,connectRetry=1");
        try
        {
            var session = await SessionAsync(repository, $$"""{ "redis": "env:{{RedisVariable}}" }""", store: "memory");

            var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null);

            Assert.Equal("not_verifiable", result.Observation);
            Assert.Contains("'memory' is not listed in verify.stores", result.Reason, StringComparison.Ordinal);
            Assert.NotEqual(VerificationFailure.CacheUnavailable, result.Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RedisVariable, null);
        }
    }

    /// <summary>
    /// A verify section that names a cache and no database. Nothing resolved the database until deep
    /// inside the reading, where the exception it threw was not one Classify knew, so it escaped
    /// verify_finding as an error instead of an account of why there is no verification.
    /// </summary>
    [Fact]
    public async Task A_verify_section_without_a_database_is_not_verifiable_with_a_code()
    {
        using var repository = new TemporaryRepository();
        Environment.SetEnvironmentVariable(RedisVariable, "127.0.0.1:6390,abortConnect=false,connectTimeout=200,connectRetry=1");
        try
        {
            var session = await SessionAsync(repository, $$"""{ "redis": "env:{{RedisVariable}}" }""");

            var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null);

            Assert.Equal("not_verifiable", result.Observation);
            Assert.Equal(VerificationFailure.NotConfigured, result.Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RedisVariable, null);
        }
    }

    /// <summary>The database variable is named and unset. The reason is a code, never the message, which
    /// could name the variable's value.</summary>
    [Fact]
    public async Task An_unset_database_variable_is_not_verifiable_with_a_code()
    {
        using var repository = new TemporaryRepository();
        Environment.SetEnvironmentVariable(RedisVariable, "127.0.0.1:6390,abortConnect=false,connectTimeout=200,connectRetry=1");
        try
        {
            var session = await SessionAsync(repository,
                $$"""{ "redis": "env:{{RedisVariable}}", "database": "env:CD_TEST_VERIFY_DB_UNSET" }""");

            var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null);

            Assert.Equal("not_verifiable", result.Observation);
            Assert.Equal(VerificationFailure.NotConfigured, result.Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RedisVariable, null);
        }
    }

    /// <summary>A connection string neither client will parse. Its text is the string itself, so what
    /// travels is the code.</summary>
    [Fact]
    public async Task A_malformed_redis_connection_string_is_not_verifiable_with_a_code()
    {
        using var repository = new TemporaryRepository();
        Environment.SetEnvironmentVariable(RedisVariable, "=:=,,,");
        Environment.SetEnvironmentVariable("CD_TEST_VERIFY_DB", "Server=(local);Database=shop;Integrated Security=true");
        try
        {
            var session = await SessionAsync(repository,
                $$"""{ "redis": "env:{{RedisVariable}}", "database": "env:CD_TEST_VERIFY_DB" }""");

            var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null);

            Assert.Equal("not_verifiable", result.Observation);
            Assert.DoesNotContain("=:=", result.Reason ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RedisVariable, null);
            Environment.SetEnvironmentVariable("CD_TEST_VERIFY_DB", null);
        }
    }

    /// <summary>
    /// A SCAN the server answers and refuses — an ACL without SCAN. The tool reports a verification that
    /// could not be taken, under the cache's own code, and the reason never carries the server's text,
    /// which can quote a key.
    /// </summary>
    [Fact]
    public async Task A_scan_the_server_refuses_is_not_verifiable_with_a_code()
    {
        using var repository = new TemporaryRepository();
        var session = await SessionAsync(repository);

        var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null,
                                                       (_, _) => Task.FromResult(Refused(VerificationFailure.CacheUnavailable)));

        Assert.Equal("not_verifiable", result.Observation);
        Assert.Equal(VerificationFailure.CacheUnavailable, result.Reason);
        Assert.DoesNotContain("NOPERM", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same refusal, but reached through the live path instead of the run seam. The connection is a
    /// real one to the test server — the catch this exercises sits after the connect and the topology
    /// check, so a substituted run, or an unreachable port, answers before ever getting there. The reader
    /// is the only fake, and it refuses the SCAN the way an ACL without SCAN would.
    /// </summary>
    [RequiresRedisFact]
    public async Task A_scan_the_server_refuses_inside_the_run_is_not_verifiable_with_a_code()
    {
        using var repository = new TemporaryRepository();
        Environment.SetEnvironmentVariable(RedisVariable, RedisIntegrationTests.ConfiguredConnectionString);
        Environment.SetEnvironmentVariable("CD_TEST_VERIFY_DB", "Server=(local);Database=shop;Integrated Security=true");
        try
        {
            var session = await SessionAsync(repository,
                $$"""{ "redis": "env:{{RedisVariable}}", "database": "env:CD_TEST_VERIFY_DB" }""");
            session.OpenCacheReader = (_, _, _) => new RedisReader((_, _) =>
                throw new RedisServerException("NOPERM this user has no permissions to run the 'scan' command"));

            var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null);

            Assert.Equal("not_verifiable", result.Observation);
            Assert.Equal(VerificationFailure.CacheUnavailable, result.Reason);
            Assert.DoesNotContain("NOPERM", result.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("scan", result.Reason!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RedisVariable, null);
            Environment.SetEnvironmentVariable("CD_TEST_VERIFY_DB", null);
        }
    }

    /// <summary>The mapping from a SQL permission refusal to this code is tested against the reader; what
    /// is checked here is that the tool carries it out whole and distinct from the others.</summary>
    [Fact]
    public async Task A_permission_refusal_carries_a_different_code()
    {
        using var repository = new TemporaryRepository();
        var session = await SessionAsync(repository);

        var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null,
                                                       (_, _) => Task.FromResult(Refused(VerificationFailure.PermissionDenied)));

        Assert.Equal("not_verifiable", result.Observation);
        Assert.Equal(VerificationFailure.PermissionDenied, result.Reason);
        Assert.NotEqual(VerificationFailure.CacheUnavailable, result.Reason);
    }

    [Fact]
    public async Task The_result_serializes_through_the_source_generated_context()
    {
        using var repository = new TemporaryRepository();
        var session = await SessionAsync(repository);

        var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null,
                                                       (_, _) => Task.FromResult(Run(keys: 2)));

        var json = JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.VerifyFindingResult);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("possible", document.RootElement.GetProperty("observation").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("keys").GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task The_response_stays_under_the_limit()
    {
        using var repository = new TemporaryRepository();
        var session = await SessionAsync(repository);

        var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null,
                                                       (_, _) => Task.FromResult(Run(keys: 40, fields: 60, tables: 20)));

        Assert.True(Size(result) <= ResponseEnvelope.MaximumSerializedBytes, $"the response was {Size(result)} bytes");
    }

    [Fact]
    public async Task A_record_with_many_dependent_tables_stays_under_the_limit_and_says_how_many_are_hidden()
    {
        using var repository = new TemporaryRepository();
        var session = await SessionAsync(repository);

        var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null,
                                                       (_, _) => Task.FromResult(Run(keys: 1, fields: 0, tables: 900)));

        var key = Assert.Single(result.Keys.Items);
        Assert.True(Size(result) <= ResponseEnvelope.MaximumSerializedBytes);
        Assert.True(key.TablesHidden > 0, "no table was reported as hidden");
        Assert.Equal(900, key.Tables.Count + key.TablesHidden);
        Assert.Equal(Template, key.Template);
    }

    [Fact]
    public async Task The_second_page_comes_from_the_snapshot()
    {
        using var repository = new TemporaryRepository();
        var session = await SessionAsync(repository);
        var findingId = await FindingIdAsync(session);
        var runs = 0;
        Task<VerificationRun> Count(FindingSnapshot _, CancellationToken __)
        {
            runs++;
            return Task.FromResult(Run(keys: 5));
        }

        await session.VerifyFindingAsync(findingId, false, Page(1, 2), Count);
        var second = await session.VerifyFindingAsync(findingId, false, Page(2, 2), Count);

        Assert.Equal(1, runs);
        Assert.Equal(2, second.Keys.Page);
        Assert.Equal(2, second.Keys.Items.Count);
    }

    [Fact]
    public async Task The_pages_do_not_overlap_and_together_hold_everything()
    {
        using var repository = new TemporaryRepository();
        var session = await SessionAsync(repository);
        var findingId = await FindingIdAsync(session);

        var hashes = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await session.VerifyFindingAsync(findingId, false, Page(page, 2), (_, _) => Task.FromResult(Run(keys: 5)));
            hashes.AddRange(result.Keys.Items.Select(item => item.KeyHash));
            Assert.Equal(3, result.Keys.Pages);
        }

        Assert.Equal(5, hashes.Count);
        Assert.Equal(5, hashes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Refresh_verifies_again()
    {
        using var repository = new TemporaryRepository();
        var session = await SessionAsync(repository);
        var findingId = await FindingIdAsync(session);
        var runs = 0;
        Task<VerificationRun> Count(FindingSnapshot _, CancellationToken __)
        {
            runs++;
            return Task.FromResult(Run(keys: 1));
        }

        await session.VerifyFindingAsync(findingId, false, null, Count);
        await session.VerifyFindingAsync(findingId, false, null, Count);
        await session.VerifyFindingAsync(findingId, refresh: true, null, Count);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_new_version_token_verifies_again()
    {
        using var repository = new TemporaryRepository();
        var session = await SessionAsync(repository);
        var findingId = await FindingIdAsync(session);
        var runs = 0;
        Task<VerificationRun> Count(FindingSnapshot _, CancellationToken __)
        {
            runs++;
            return Task.FromResult(Run(keys: 1));
        }

        await session.VerifyFindingAsync(findingId, false, null, Count);
        var before = session.Graph.VersionToken;
        session.Graph.AddEdge(new Caches(Handler("App.Warm"), new CacheKey("brand:{id}", "redis", null, [], "cache"), Confidence.Confirmed));
        await session.VerifyFindingAsync(findingId, false, null, Count);

        Assert.NotEqual(before, session.Graph.VersionToken);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_new_workspace_does_not_answer_from_the_old_snapshot()
    {
        using var first = new TemporaryRepository();
        using var second = new TemporaryRepository();
        var session = await SessionAsync(first);
        var runs = 0;
        Task<VerificationRun> Count(FindingSnapshot _, CancellationToken __)
        {
            runs++;
            return Task.FromResult(Run(keys: 1));
        }

        await session.VerifyFindingAsync(await FindingIdAsync(session), false, null, Count);
        await session.InitializeAsync(second.Path, ["App.csproj"], null);
        Plant(session.Graph);
        await session.VerifyFindingAsync(await FindingIdAsync(session), false, null, Count);

        Assert.Equal(2, runs);
    }

    /// <summary>A template long enough to break the budget on its own is shortened, and the hash is what
    /// still tells one record from another.</summary>
    [Fact]
    public async Task A_record_that_does_not_fit_even_without_its_collections_stays_distinguishable()
    {
        using var repository = new TemporaryRepository();
        var session = await SessionAsync(repository);
        var enormous = new string('k', 20000);

        var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null, (_, _) =>
            Task.FromResult(new VerificationRun(
                new FindingVerification(VerificationOutcome.Possible, null,
                    [Key("first", enormous), Key("second", enormous)], 0, 2, 0, false, false), [])));

        Assert.True(Size(result) <= ResponseEnvelope.MaximumSerializedBytes, $"the response was {Size(result)} bytes");
        Assert.Equal(2, result.Keys.Items.Count);
        Assert.Equal(["first", "second"], result.Keys.Items.Select(item => item.KeyHash).Order(StringComparer.Ordinal));
        Assert.All(result.Keys.Items, item => Assert.StartsWith("kkk", item.Template, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_partial_result_is_marked()
    {
        using var repository = new TemporaryRepository();
        var session = await SessionAsync(repository);

        var result = await session.VerifyFindingAsync(await FindingIdAsync(session), false, null,
                                                       (_, _) => Task.FromResult(Run(keys: 1, partial: true)));

        Assert.True(result.Partial);
        Assert.Equal(VerificationFailure.WrongType, Assert.Single(result.Keys.Items).FailureCode);
    }

    private static int Size(VerifyFindingResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, CacheDetectiveJsonContext.Default.VerifyFindingResult).Length;

    private static PageArguments Page(int page, int size) => new() { Page = page, PageSize = size };

    private static VerificationRun Refused(string reason) =>
        new(new FindingVerification(VerificationOutcome.NotVerifiable, reason, [], 0, 0, 0, false, false), []);

    /// <summary>A run of <paramref name="keys"/> keys, each disagreeing on one field so the finding is
    /// possible and every record carries something worth trimming.</summary>
    private static VerificationRun Run(int keys, int fields = 1, int tables = 1, bool partial = false)
    {
        var verified = Enumerable.Range(0, keys)
                                 .Select(index => Key($"hash{index:D4}", Template, fields, partial))
                                 .ToArray();
        var verification = new FindingVerification(VerificationOutcome.Possible, "a field differs", verified,
                                                   0, keys, 0, false, partial);
        return new VerificationRun(verification,
                                   Enumerable.Range(0, tables).Select(index => $"dbo.DependentTable{index:D4}").ToArray());
    }

    private static KeyVerification Key(string hash, string template, int fields = 1, bool partial = false) =>
        new(template, hash, VerificationOutcome.Possible,
            Enumerable.Range(0, fields)
                      .Select(index => new FieldObservation($"field_number_{index:D3}", FieldVerdict.Different, false,
                                                            "the cached value and the row disagree"))
                      .ToArray(),
            false, null, "a field differs", partial ? VerificationFailure.WrongType : null);

    private static async Task<WorkspaceSession> SessionAsync(TemporaryRepository repository, string? verify = null, string store = "redis")
    {
        if (verify is not null)
        {
            await WriteConfigurationAsync(repository, verify);
        }

        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, ["App.csproj"], null);
        Plant(session.Graph, store);
        return session;
    }

    private static async Task WriteConfigurationAsync(TemporaryRepository repository, string verify)
    {
        var path = WorkspaceConfigurationStore.GetPath(repository.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $$"""
            { "version": 1, "root": ".", "solutions": ["App.csproj"], "budgets": {}, "verify": {{verify}} }
            """);
    }

    /// <summary>One unguarded write, so there is a finding to ask about.</summary>
    private static void Plant(CacheGraph graph, string store = "redis")
    {
        var handler = Handler("App.Get");
        graph.AddEdge(new Writes(handler, new Table("dbo.Products", "shop"), Confidence.Confirmed));
        graph.AddEdge(new Caches(handler, new CacheKey(Template, store, null, [], "cache"), Confidence.Confirmed));
        graph.AddEdge(new Reads(handler, new Table("dbo.Products", "shop"), Confidence.Confirmed));
    }

    /// <summary>A parent key that outlives the child it is built from, with only the child invalidated:
    /// one STALE_PARENT_KEY finding. The two keys sit in different stores so that a test can tell which of
    /// them a run chose to read.</summary>
    private static void PlantStaleParent(CacheGraph graph)
    {
        var parent = new CacheKey("basket:{id}", "memory", TimeSpan.FromSeconds(600), [], "cache");
        // A template of its own: the workspace's own planted key is 'product:{id}' with no TTL, and a
        // second key of that template and store would be the same vertex with a different lifetime.
        var child = new CacheKey("price:{id}", "redis", TimeSpan.FromSeconds(30), [], "cache");
        var reader = Handler("App.GetBasket");
        graph.AddEdge(new Caches(reader, parent, Confidence.Confirmed));
        graph.AddEdge(new Reads(reader, child, Confidence.Confirmed));
        graph.AddEdge(new Caches(Handler("App.GetProduct"), child, Confidence.Confirmed));
        graph.AddEdge(new Invalidates(Handler("App.Write"), child, Confidence.Confirmed));
    }

    private static Handler Handler(string symbol) => new("app", symbol, "http", "C.cs", 1);

    private static async Task<string> FindingIdAsync(WorkspaceSession session, string? rule = null)
    {
        var envelope = await session.ReadFindingsAsync((graph, configuration, _, catalog) =>
            FindingQueries.FindIssues(graph, configuration?.Budgets, catalog, rule, null, true, new PageArguments()));
        return envelope.Items[0].Id;
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
