using CacheDetective.Verification;
using StackExchange.Redis;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// The cache reader, driven against a fake command seam. Everything it is allowed to do is visible in the
/// commands that arrive there, which is what makes "reads and never writes" a checked claim rather than a
/// stated one.
/// </summary>
public sealed class RedisReaderTests
{
    private const string Template = "product:{id}";

    [Fact]
    public void A_template_with_a_placeholder_gives_a_glob_with_its_literal_segment() =>
        Assert.Equal("*product:*", RedisReader.Glob(Template));

    /// <summary>The tail begins after a leading placeholder — a whole base address, not a segment — and
    /// after the last unknown fragment, per <c>docs/adr/0010</c>.</summary>
    [Fact]
    public void The_tail_begins_after_a_leading_placeholder_and_after_the_last_unknown()
    {
        Assert.Equal("*/catalog/*", RedisReader.Glob("{base}/catalog/{id}"));
        Assert.Equal("*:items:*", RedisReader.Glob("cache:{?}:items:{id}"));
        Assert.Equal("*:items:*", RedisReader.Glob("{base}cache:{?}:v{n}:{?}:items:{id}"));
    }

    [Fact]
    public async Task A_template_with_no_literal_tail_is_not_searched()
    {
        Assert.Null(RedisReader.Glob("{base}{id}"));
        Assert.Null(RedisReader.Glob("prefix:{?}{id}"));
        var server = new FakeServer();

        var result = await new RedisReader(server.ExecuteAsync).ScanAsync("{base}{id}");

        Assert.Contains("no literal segment", result.NotVerifiableReason, StringComparison.Ordinal);
        Assert.Empty(server.Commands);
    }

    [Fact]
    public void Glob_metacharacters_in_a_literal_are_escaped() =>
        Assert.Equal(@"*a\*b\?c\[d\]e\\f:*", RedisReader.Glob(@"a*b?c[d]e\f:{id}"));

    [Fact]
    public async Task The_cursor_runs_through_several_iterations()
    {
        var server = new FakeServer().Page("7", "product:1").Page("9", "product:2").Page("0", "product:3");

        var result = await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        Assert.Equal(3, server.Commands.Count(command => command == "SCAN"));
        Assert.Equal(3, result.Entries.Count);
        Assert.True(result.ScanExhausted);
    }

    [Fact]
    public async Task An_empty_page_still_spends_an_iteration_of_the_budget()
    {
        var server = new FakeServer().Page("4").Page("0", "product:1");

        var result = await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        Assert.Equal(2, server.Commands.Count(command => command == "SCAN"));
        Assert.Single(result.Entries);
    }

    [Fact]
    public async Task The_iteration_budget_stops_the_traversal()
    {
        var server = new FakeServer { NeverEnds = true };

        var result = await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        Assert.Equal(RedisReader.MaximumIterations, server.Commands.Count(command => command == "SCAN"));
        Assert.True(result.BudgetStopped);
        Assert.False(result.ScanExhausted);
    }

    /// <summary>The tool stops waiting; it does not cancel the command already on the wire. What it must
    /// not do is send the next one.</summary>
    [Fact]
    public async Task The_time_budget_stops_waiting_and_sends_no_further_command()
    {
        var server = new FakeServer { NeverEnds = true, Delay = TimeSpan.FromSeconds(30) };

        var result = await new RedisReader(server.ExecuteAsync, budget: TimeSpan.FromMilliseconds(50)).ScanAsync(Template);

        Assert.True(result.BudgetStopped);
        Assert.Equal(1, server.Commands.Count(command => command == "SCAN"));
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task A_late_failure_of_an_abandoned_request_does_not_bring_the_process_down()
    {
        var unobserved = new List<Exception>();
        void Record(object? sender, UnobservedTaskExceptionEventArgs eventArgs) => unobserved.Add(eventArgs.Exception);
        TaskScheduler.UnobservedTaskException += Record;
        try
        {
            var server = new FakeServer { NeverEnds = true, Delay = TimeSpan.FromMilliseconds(200), FailAfterDelay = true };

            var result = await new RedisReader(server.ExecuteAsync, budget: TimeSpan.FromMilliseconds(20)).ScanAsync(Template);
            Assert.True(result.BudgetStopped);

            await server.LastCommand!.ContinueWith(_ => { }, TaskScheduler.Default);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Record;
        }

        Assert.Empty(unobserved);
    }

    [Fact]
    public async Task A_repeated_key_does_not_spend_the_limit()
    {
        var server = new FakeServer().Page("2", "product:1", "product:1").Page("0", "product:1", "product:2");

        var result = await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        Assert.Equal(2, result.Entries.Count);
        Assert.False(result.LimitReached);
    }

    [Fact]
    public async Task No_match_on_an_exhausted_traversal()
    {
        var server = new FakeServer().Page("0");

        var result = await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        Assert.Empty(result.Entries);
        Assert.True(result.ScanExhausted);
        Assert.False(result.LimitReached);
        Assert.False(result.MatchesDiscarded);
    }

    [Fact]
    public async Task Fewer_than_twenty_matches_do_not_reach_the_limit()
    {
        var server = new FakeServer().Page("0", Keys(19));

        var result = await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        Assert.Equal(19, result.Entries.Count);
        Assert.True(result.ScanExhausted);
        Assert.False(result.LimitReached);
    }

    [Fact]
    public async Task Exactly_twenty_matches_reach_the_limit()
    {
        var server = new FakeServer().Page("0", Keys(20));

        var result = await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        Assert.Equal(RedisReader.MaximumMatches, result.Entries.Count);
        Assert.True(result.LimitReached);
        Assert.False(result.MatchesDiscarded);
    }

    [Fact]
    public async Task More_than_twenty_in_the_middle_of_the_traversal_stop_it()
    {
        var server = new FakeServer().Page("3", Keys(25)).Page("0", "product:999");

        var result = await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        Assert.Equal(RedisReader.MaximumMatches, result.Entries.Count);
        Assert.True(result.LimitReached);
        Assert.True(result.MatchesDiscarded);
        Assert.False(result.ScanExhausted);
        Assert.Equal(1, server.Commands.Count(command => command == "SCAN"));
    }

    [Fact]
    public async Task A_final_page_with_more_matches_than_fit_reports_them_discarded()
    {
        var server = new FakeServer().Page("0", Keys(25));

        var result = await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        Assert.True(result.MatchesDiscarded);
        Assert.True(result.ScanExhausted);
        Assert.Equal(RedisReader.MaximumMatches, result.Entries.Count);
    }

    [Fact]
    public async Task A_string_entry_is_read_with_GET()
    {
        var server = new FakeServer { Type = "string", Payload = "{\"price\":10}" }.Page("0", "product:1");

        var result = await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        Assert.Equal("{\"price\":10}", Assert.Single(result.Entries).Payload);
        Assert.Contains("GET", server.Commands);
        Assert.DoesNotContain("HGET", server.Commands);
    }

    /// <summary>The whole <c>distributed</c> store is a hash whose payload lives in <c>data</c>, so
    /// <c>GET</c> alone would read nothing from any ASP.NET Core application.</summary>
    [Fact]
    public async Task A_hash_entry_is_read_with_HGET_data()
    {
        var server = new FakeServer { Type = "hash", Payload = "{\"price\":10}" }.Page("0", "product:1");

        var result = await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        Assert.Equal("{\"price\":10}", Assert.Single(result.Entries).Payload);
        var call = Assert.Single(server.Calls, call => call.Command == "HGET");
        Assert.Equal(RedisReader.DistributedPayloadField, call.Arguments[1]);
        Assert.DoesNotContain("GET", server.Commands);
    }

    [Fact]
    public async Task Another_type_is_not_verifiable()
    {
        var server = new FakeServer { Type = "zset" }.Page("0", "product:1");

        var result = await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        var entry = Assert.Single(result.Entries);
        Assert.Null(entry.Payload);
        Assert.Contains("zset", entry.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("GET", server.Commands);
        Assert.DoesNotContain("HGET", server.Commands);
    }

    [Fact]
    public async Task TYPE_comes_before_the_read()
    {
        var server = new FakeServer { Type = "string" }.Page("0", "product:1");

        await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        Assert.True(server.Commands.IndexOf("TYPE") < server.Commands.IndexOf("GET"));
    }

    [Fact]
    public void More_than_one_endpoint_is_not_verifiable()
    {
        var reason = RedisReader.UnsupportedTopology(2, ServerType.Standalone);

        Assert.Contains("2 endpoints", reason, StringComparison.Ordinal);
        Assert.Null(RedisReader.UnsupportedTopology(1, ServerType.Standalone));
    }

    [Fact]
    public void Cluster_mode_is_not_verifiable() =>
        Assert.Contains("cluster", RedisReader.UnsupportedTopology(1, ServerType.Cluster), StringComparison.Ordinal);

    [Fact]
    public void A_key_whose_left_part_is_the_configured_prefix_is_not_flagged() =>
        Assert.False(RedisReader.Match("shop:product:42", Template, "shop:").Prefixed);

    [Fact]
    public void A_key_with_another_left_part_is_flagged() =>
        Assert.True(RedisReader.Match("other:product:42", Template, "shop:").Prefixed);

    [Fact]
    public void Values_are_extracted_when_the_lazy_and_greedy_matches_agree()
    {
        var match = RedisReader.Match("shop:product:42", Template, "shop:");

        Assert.Null(match.Reason);
        Assert.Equal("42", Assert.Single(match.Values!).Value);
        Assert.Equal("id", Assert.Single(match.Values!).Key);
    }

    /// <summary>A non-empty literal between the placeholders is not enough: <c>left=a, id=b:c</c> and
    /// <c>left=a:b, id=c</c> both fit, so no assignment may be used.</summary>
    [Fact]
    public void A_key_that_admits_two_assignments_is_ambiguous()
    {
        var match = RedisReader.Match("item:a:b:c", "item:{left}:{id}", string.Empty);

        Assert.Null(match.Values);
        Assert.Contains("ambiguous", match.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Adjacent_placeholders_are_ambiguous_too()
    {
        var match = RedisReader.Match("cart:abc", "cart:{left}{right}", string.Empty);

        Assert.Null(match.Values);
        Assert.Contains("ambiguous", match.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_match_gives_a_reason_that_names_no_key()
    {
        var match = RedisReader.Match("basket:42", Template, string.Empty);

        Assert.Null(match.Values);
        Assert.NotNull(match.Reason);
        Assert.DoesNotContain("basket", match.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("42", match.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_actual_key_never_leaves_the_reader()
    {
        const string key = "shop:product:9f3c1e";
        var server = new FakeServer().Page("0", key);

        var result = await new RedisReader(server.ExecuteAsync, "shop:").ScanAsync(Template);

        var entry = Assert.Single(result.Entries);
        var everything = string.Join('|', entry.Key.Template, entry.Key.KeyHash, entry.Key.Reason,
                                     string.Join(',', entry.Key.Values?.Select(value => $"{value.Key}={value.Value}") ?? []));
        Assert.DoesNotContain(key, everything, StringComparison.Ordinal);
        Assert.Equal(8, entry.Key.KeyHash.Length);
        Assert.Equal(RedisReader.ShortHash(key), entry.Key.KeyHash);
    }

    [Fact]
    public async Task No_command_the_reader_sends_writes()
    {
        string[] writes = ["SET", "SETEX", "GETSET", "DEL", "UNLINK", "EXPIRE", "PEXPIRE", "HSET", "HDEL",
                           "FLUSHDB", "FLUSHALL", "RENAME", "PERSIST", "APPEND", "INCR"];
        var server = new FakeServer { Type = "hash" }.Page("5", "product:1").Page("0", "product:2");

        await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        Assert.NotEmpty(server.Commands);
        Assert.All(server.Commands, command => Assert.Contains(command, RedisReader.AllowedCommands));
        Assert.All(server.Commands, command => Assert.DoesNotContain(command, writes));
    }

    [Fact]
    public async Task KEYS_is_never_sent()
    {
        var server = new FakeServer().Page("0", "product:1");

        await new RedisReader(server.ExecuteAsync).ScanAsync(Template);

        Assert.DoesNotContain("KEYS", server.Commands);
        Assert.Contains("SCAN", server.Commands);
    }

    /// <summary>Not a setting but a refusal: with <c>INFO</c> disabled the multiplexer's own
    /// auto-configuration writes a probe key into database 0, and no client option prevents it.</summary>
    [Fact]
    public void A_connection_string_that_disables_INFO_is_refused_with_the_reason_named()
    {
        var reason = RedisReader.Refuse("localhost:6379,$INFO=");

        Assert.Equal(RedisReader.RefuseReason, reason);
        Assert.Contains("INFO", reason, StringComparison.Ordinal);
        Assert.Contains("probe key", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_connection_string_with_INFO_available_is_not_refused()
    {
        Assert.Null(RedisReader.Refuse("localhost:6379"));
        Assert.Null(RedisReader.Refuse("localhost:6379,allowAdmin=true,abortConnect=false,defaultDatabase=3"));
        // A rename keeps the command available, so it is not the failure this refusal is about.
        Assert.Null(RedisReader.Refuse("localhost:6379,$INFO=DETAILS"));
    }

    private static string[] Keys(int count) =>
        Enumerable.Range(1, count).Select(index => $"product:{index}").ToArray();

    /// <summary>The seam. Every command the reader sends lands here and nowhere else.</summary>
    private sealed class FakeServer
    {
        private readonly Queue<(string Cursor, string[] Keys)> _pages = new();

        internal List<string> Commands { get; } = [];

        internal List<(string Command, object[] Arguments)> Calls { get; } = [];

        internal string Type { get; init; } = "string";

        internal string? Payload { get; init; } = "{}";

        internal TimeSpan Delay { get; init; }

        internal bool NeverEnds { get; init; }

        internal bool FailAfterDelay { get; init; }

        internal Task<RedisResult>? LastCommand { get; private set; }

        internal FakeServer Page(string cursor, params string[] keys)
        {
            _pages.Enqueue((cursor, keys));
            return this;
        }

        internal Task<RedisResult> ExecuteAsync(string command, object[] arguments)
        {
            Commands.Add(command);
            Calls.Add((command, arguments));
            return LastCommand = RunAsync(command);
        }

        private async Task<RedisResult> RunAsync(string command)
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay);
                if (FailAfterDelay)
                    throw new RedisTimeoutException("the reply arrived after nobody was waiting", CommandStatus.Sent);
            }

            return command switch
            {
                "SCAN" => Scan(),
                "TYPE" => RedisResult.Create((RedisValue)Type),
                "GET" or "HGET" => RedisResult.Create(Payload is null ? RedisValue.Null : (RedisValue)Payload),
                "TTL" => RedisResult.Create((RedisValue)60),
                "OBJECT" => RedisResult.Create((RedisValue)3),
                _ => throw new InvalidOperationException($"The reader sent an unexpected command: {command}")
            };
        }

        private RedisResult Scan()
        {
            var (cursor, keys) = _pages.Count > 0 ? _pages.Dequeue() : (NeverEnds ? "1" : "0", []);
            return RedisResult.Create([RedisResult.Create((RedisValue)cursor),
                                       RedisResult.Create(keys.Select(key => (RedisValue)key).ToArray())]);
        }
    }
}
