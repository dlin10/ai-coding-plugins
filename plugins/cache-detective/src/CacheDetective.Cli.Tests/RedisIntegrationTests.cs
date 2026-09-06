using System.Diagnostics;
using CacheDetective.Verification;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// A fact that cannot run without a live Redis. <c>CD_TEST_REDIS</c> must name the <em>verifier's own</em>
/// database through <c>defaultDatabase</c>, because the fixture seeds there and database 0 is left alone
/// on purpose: the writing path of the client aims at database 0, and a database 0 nobody touched is what
/// makes that provable.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresRedisFactAttribute : FactAttribute
{
    public RequiresRedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(RedisIntegrationTests.ConfiguredConnectionString))
        {
            Skip = "CD_TEST_REDIS is not set. Set it to a Redis connection string naming a spare database, as in " +
                   "\"localhost:6379,defaultDatabase=9\", to run the cache integration tests.";
        }
    }
}

/// <summary>
/// The cache half of verification against a real server with known keys in it. What only a live server can
/// prove, and what this proves, is that nothing the reader does writes anything anywhere — including the
/// probe the client sends on connecting, which is why the reader is given a connection of its own here
/// rather than the fixture's. The snapshot is taken before that connection is opened and again after it is
/// closed, so the connect and the disconnect are both inside the window being compared.
/// </summary>
/// <summary>
/// Every test that reaches the live server named by <c>CD_TEST_REDIS</c>, run one at a time. They share one
/// database, and the snapshot below compares the whole of it: another class writing its own keys while the
/// window is open would be reported as a change this verifier made. Filtering the snapshot down to the
/// fixture's own key names was the alternative, and it hid the very thing the snapshot exists to catch — a
/// key the client created or removed outside those names.
/// </summary>
[CollectionDefinition(LiveRedisCollection.Name, DisableParallelization = true)]
public sealed class LiveRedisCollection
{
    internal const string Name = "live redis";
}

[Collection(LiveRedisCollection.Name)]
public sealed class RedisIntegrationTests
{
    internal const string ConnectionVariable = "CD_TEST_REDIS";

    private const string Template = "product:{id}";
    private const string CacheTemplate = "cachedet:entry:{id}";
    private const string CacheKey = "cachedet:entry:001";
    private const int Seeded = 25;

    /// <summary>Long enough that the remaining TTL is still comfortably inside it when the run ends.</summary>
    private static readonly TimeSpan SeedTtl = TimeSpan.FromHours(1);

    internal static string? ConfiguredConnectionString => Environment.GetEnvironmentVariable(ConnectionVariable);

    [RequiresRedisFact]
    public async Task The_sample_for_a_template_never_exceeds_twenty()
    {
        await using var fixture = await SeededCache.CreateAsync();
        await using var verifier = await fixture.OpenVerifierAsync();

        var scan = await verifier.Reader.ScanAsync(Template);

        Assert.Equal(RedisReader.MaximumMatches, scan.Entries.Count);
        Assert.True(scan.LimitReached);
        Assert.True(scan.MatchesDiscarded, "the fixture seeds more keys than the limit, so some must be reported discarded");
    }

    /// <summary>The entry is written by the provider itself, so the <c>TYPE</c> then <c>HGET data</c> path
    /// is checked against the real layout of the distributed store rather than our idea of it.</summary>
    [RequiresRedisFact]
    public async Task An_entry_written_by_RedisCache_is_read_through_TYPE_and_HGET_data()
    {
        await using var fixture = await SeededCache.CreateAsync();
        await using var verifier = await fixture.OpenVerifierAsync();

        var scan = await verifier.Reader.ScanAsync(CacheTemplate);

        var entry = Assert.Single(scan.Entries);
        Assert.Null(entry.Reason);
        Assert.Equal(SeededCache.CachePayload, entry.Payload);
        var commands = verifier.Commands.ToList();
        Assert.Contains("HGET", commands);
        Assert.True(commands.IndexOf("TYPE") < commands.IndexOf("HGET"), "TYPE did not come first");
    }

    [RequiresRedisFact]
    public async Task The_age_is_read_or_honestly_unavailable()
    {
        await using var fixture = await SeededCache.CreateAsync();
        await using var verifier = await fixture.OpenVerifierAsync();

        var scan = await verifier.Reader.ScanAsync(Template);

        foreach (var entry in scan.Entries)
        {
            var age = FindingVerifier.EntryAge(SeedTtl.TotalSeconds, entry.TtlSeconds, ttlAgreed: true);
            Assert.True(age is null || age >= 0, "an age was read as a negative duration");
            // A key with no remaining TTL has no age, and says so rather than inventing one.
            Assert.Equal(entry.TtlSeconds is null, age is null);
        }
    }

    /// <summary>Not one key appeared or disappeared in either database — the whole keyspace of each, not
    /// the fixture's own names, because a key the client created outside them is exactly what this is
    /// looking for.</summary>
    [RequiresRedisFact]
    public async Task No_key_list_changed_in_either_database()
    {
        var (before, after, _) = await ObserveAsync();

        Assert.Equal(before.Verifier.Keys.Order(StringComparer.Ordinal), after.Verifier.Keys.Order(StringComparer.Ordinal));
        // Database 0 is where the client's own auto-configuration would have written its probe.
        Assert.Equal(before.Zero, after.Zero);
    }

    /// <summary>
    /// Every value the same and every deadline preserved. A TTL is not asserted against a fixed window —
    /// under load the gap between seeding and checking is whatever the machine allows, and a window in
    /// absolute seconds fails on a slow run while proving nothing on a fast one. What matters is that the
    /// deadline was preserved: it may only have moved down, and only by the time that actually passed.
    /// </summary>
    [RequiresRedisFact]
    public async Task The_seeded_values_and_ttls_are_still_there()
    {
        var (before, after, elapsed) = await ObserveAsync();

        foreach (var (key, entry) in before.Verifier)
        {
            var now = after.Verifier[key];
            Assert.Equal(entry.Value, now.Value);
            Assert.Equal(entry.Ttl is null, now.Ttl is null);
            if (entry.Ttl is not { } was || now.Ttl is not { } remains)
                continue;

            Assert.True(remains <= was + 1, $"the TTL of '{key}' grew from {was:F1}s to {remains:F1}s");
            Assert.True(was - remains <= elapsed.TotalSeconds + 2,
                        $"the TTL of '{key}' fell {was - remains:F1}s while only {elapsed.TotalSeconds:F1}s passed");
        }
    }

    /// <summary>The one entry the provider itself wrote, which is the one the reader opens with
    /// <c>HGET data</c> — the path most likely to disturb what it reads.</summary>
    [RequiresRedisFact]
    public async Task The_entry_written_by_RedisCache_is_untouched()
    {
        var (before, after, elapsed) = await ObserveAsync();

        var was = before.Verifier[CacheKey];
        var now = after.Verifier[CacheKey];
        Assert.Equal(SeededCache.CachePayload, was.Value);
        Assert.Equal(was.Value, now.Value);
        Assert.NotNull(was.Ttl);
        Assert.NotNull(now.Ttl);
        Assert.True(now.Ttl <= was.Ttl + 1, $"the TTL of '{CacheKey}' grew from {was.Ttl:F1}s to {now.Ttl:F1}s");
        Assert.True(was.Ttl - now.Ttl <= elapsed.TotalSeconds + 2,
                    $"the TTL of '{CacheKey}' fell {was.Ttl - now.Ttl:F1}s while only {elapsed.TotalSeconds:F1}s passed");
    }

    /// <summary>
    /// The whole of both databases, before the verifier's connection is opened and after it is closed. The
    /// connect is where the client would send its probe, so a snapshot taken while it was already connected
    /// would have compared the keyspace with itself.
    /// <para>The clock has to bracket both snapshots, not just what happens between them. Each snapshot is
    /// a SCAN plus three commands for each key, and under the load package.ps1 creates that takes seconds;
    /// a TTL then honestly falls by more than a window that began after the first snapshot and ended before
    /// the second, and the assertion failed for measuring the wrong span.</para>
    /// </summary>
    private static async Task<(CacheSnapshot Before, CacheSnapshot After, TimeSpan Elapsed)> ObserveAsync()
    {
        await using var fixture = await SeededCache.CreateAsync();
        var elapsed = Stopwatch.StartNew();
        var before = await fixture.SnapshotAsync();

        // Without this the comparisons could pass on two empty lists and prove nothing.
        Assert.True(before.Verifier.Count > Seeded, "the fixture seeded nothing to compare against");
        Assert.Contains(CacheKey, before.Verifier.Keys);

        await using (var verifier = await fixture.OpenVerifierAsync())
        {
            await verifier.Reader.ScanAsync(Template);
            await verifier.Reader.ScanAsync(CacheTemplate);
        }

        var after = await fixture.SnapshotAsync();
        elapsed.Stop();
        return (before, after, elapsed.Elapsed);
    }

    /// <summary>One database as it stands: every key with its value and what is left of its deadline.</summary>
    private sealed record CacheSnapshot(IReadOnlyDictionary<string, (string? Value, double? Ttl)> Verifier, string[] Zero);

    /// <summary>
    /// Keys of known value and known expiry in a database of their own, seeded over a connection of their
    /// own so that nothing the fixture did can be mistaken for something the reader did.
    /// </summary>
    private sealed class SeededCache : IAsyncDisposable
    {
        internal const string CachePayload = """{"price":19.5,"name":"Widget"}""";

        private readonly RedisCache? _cache;

        private SeededCache(ConnectionMultiplexer control, int database, RedisCache cache)
        {
            Control = control;
            Database = database;
            _cache = cache;
        }

        private ConnectionMultiplexer Control { get; }

        internal int Database { get; }

        internal static async Task<SeededCache> CreateAsync()
        {
            var connectionString = ConfiguredConnectionString!;
            var options = ConfigurationOptions.Parse(connectionString);
            var database = options.DefaultDatabase ?? 0;
            Assert.True(database != 0,
                        $"{ConnectionVariable} must name a spare database through defaultDatabase, so that database 0 stays untouched");

            var control = await ConnectionMultiplexer.ConnectAsync(connectionString);
            var seeding = control.GetDatabase(database);
            for (var index = 0; index < Seeded; index++)
            {
                await seeding.StringSetAsync($"product:{index:D3}", Value(index), SeedTtl);
            }

            // One entry written by the provider, in its own layout: a hash whose payload lives in "data".
            var cache = new RedisCache(Options.Create(new RedisCacheOptions { Configuration = connectionString }));
            await cache.SetStringAsync(CacheKey, CachePayload,
                                       new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = SeedTtl });

            return new SeededCache(control, database, cache);
        }

        /// <summary>
        /// A reader over a connection of the verifier's own, opened from the same string the working path
        /// opens. Reading through the fixture's connection would never exercise the connect, and the
        /// connect is exactly where the client sends the probe this test exists to rule out.
        /// </summary>
        internal async Task<VerifierConnection> OpenVerifierAsync() =>
            await VerifierConnection.OpenAsync(ConfiguredConnectionString!, Database);

        internal static string Value(int index) => $$"""{"price":{{index}}.5,"name":"Seeded{{index:D3}}"}""";

        /// <summary>Both databases as they stand, read over the fixture's own connection.</summary>
        internal async Task<CacheSnapshot> SnapshotAsync()
        {
            var reading = Control.GetDatabase(Database);
            var entries = new Dictionary<string, (string? Value, double? Ttl)>(StringComparer.Ordinal);

            // Every key of the database, not only the ones this fixture seeded. A key the client created or
            // removed under a name of its own is precisely what the snapshot is for, and filtering to the
            // fixture's own names made it invisible. What made the filter necessary — other classes writing
            // to the same database at the same time — is settled by the collection instead.
            foreach (var key in await ReadKeysAsync(Database))
            {
                var type = (string?)await reading.ExecuteAsync("TYPE", [key]);
                var value = string.Equals(type?.Trim(), "hash", StringComparison.OrdinalIgnoreCase)
                    ? (string?)await reading.ExecuteAsync("HGET", [key, RedisReader.DistributedPayloadField])
                    : (string?)await reading.StringGetAsync(key);
                entries[key] = (value, (await reading.KeyTimeToLiveAsync(key))?.TotalSeconds);
            }

            return new CacheSnapshot(entries, await ReadKeysAsync(0));
        }

        /// <summary>Every key of one database, in a stable order. Read with <c>SCAN</c> over a cursor of its
        /// own, so the snapshot costs the server no more than the reader does.</summary>
        internal async Task<string[]> ReadKeysAsync(int database)
        {
            var reading = Control.GetDatabase(database);
            var keys = new List<string>();
            var cursor = "0";
            do
            {
                var page = (RedisResult[])(await reading.ExecuteAsync("SCAN", [cursor, "COUNT", 1000]))!;
                cursor = (string?)page[0] ?? "0";
                keys.AddRange(((RedisValue[])page[1]!).Select(value => (string)value!));
            }
            while (cursor != "0");

            return keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            var seeding = Control.GetDatabase(Database);
            for (var index = 0; index < Seeded; index++)
            {
                await seeding.KeyDeleteAsync($"product:{index:D3}");
            }

            _cache?.Dispose();
            await seeding.KeyDeleteAsync(CacheKey);
            await Control.DisposeAsync();
        }
    }

    /// <summary>The reader and the connection it owns, closed together.</summary>
    private sealed class VerifierConnection : IAsyncDisposable
    {
        private readonly List<string> _commands = [];
        private readonly ConnectionMultiplexer _multiplexer;

        private VerifierConnection(ConnectionMultiplexer multiplexer, int database)
        {
            _multiplexer = multiplexer;
            var reading = multiplexer.GetDatabase(database);
            Reader = new RedisReader((command, arguments) =>
            {
                _commands.Add(command);
                return reading.ExecuteAsync(command, arguments);
            });
        }

        internal RedisReader Reader { get; }

        internal IReadOnlyList<string> Commands => _commands;

        internal static async Task<VerifierConnection> OpenAsync(string connectionString, int database) =>
            new(await ConnectionMultiplexer.ConnectAsync(connectionString), database);

        public async ValueTask DisposeAsync() => await _multiplexer.DisposeAsync();
    }
}
