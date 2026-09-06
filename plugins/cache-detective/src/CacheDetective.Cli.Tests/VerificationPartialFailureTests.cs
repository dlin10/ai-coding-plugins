using System.Text.Json;
using CacheDetective.Mcp;
using CacheDetective.Serialization;
using CacheDetective.Verification;
using StackExchange.Redis;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// What survives a failure. R7 asks that an impossibility be recorded with its reason, that a partial
/// result say so, and that the observations already taken are not lost — none of which held while one
/// key's failure threw the whole sample away.
/// </summary>
public sealed class VerificationPartialFailureTests
{
    private const string Template = "product:{id}";

    /// <summary>One key of the wrong type is a reason on that key. The others are still read.</summary>
    [Fact]
    public async Task A_wrong_type_on_one_key_leaves_the_rest_of_the_sample_readable()
    {
        var reader = Reader(("product:1", "string"), ("product:2", "WRONGTYPE"), ("product:3", "string"));

        var scan = await reader.ScanAsync(Template);

        Assert.Equal(3, scan.Entries.Count);
        Assert.Null(scan.InterruptedReason);
        var failed = scan.Entries.Single(entry => entry.Payload is null);
        Assert.Contains("type verification does not read", failed.Reason, StringComparison.Ordinal);
        Assert.Equal(2, scan.Entries.Count(entry => entry.Payload is not null));
    }

    /// <summary>A timeout on one key is that key's failure and not the sample's.</summary>
    [Fact]
    public async Task A_timeout_on_one_key_is_recorded_against_that_key()
    {
        var reader = Reader(("product:1", "string"), ("product:2", "TIMEOUT"));

        var scan = await reader.ScanAsync(Template);

        Assert.Equal(2, scan.Entries.Count);
        Assert.Contains("reading this entry failed", scan.Entries.Single(entry => entry.Payload is null).Reason,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// A server that refuses <c>OBJECT IDLETIME</c>. Redis rejects the command outright when
    /// <c>maxmemory-policy</c> is one of the LFU ones, so this is a configuration and not a fault — and
    /// letting it out of the read replaced the entry with nulls, which meant that on such a server no key
    /// of any sample had a payload and comparison stopped working everywhere at once.
    /// </summary>
    [Fact]
    public async Task A_refused_idle_time_keeps_the_payload_and_the_ttl()
    {
        var reader = Reader(refuseIdle: true, ("product:1", "string"), ("product:2", "string"));

        var scan = await reader.ScanAsync(Template);

        Assert.All(scan.Entries, entry =>
        {
            Assert.Equal("{}", entry.Payload);
            Assert.Equal(60, entry.TtlSeconds);
            Assert.Null(entry.IdleSeconds);
            Assert.Null(entry.FailureCode);
            Assert.Equal(RedisReader.IdleUnavailableReason, entry.IdleReason);
        });

        // A field that differs is still found, and the one refused observation does not make the run
        // partial: nothing the verification needed was lost.
        var differs = new TableRowComparison("dbo.Products",
            new RowComparison([new FieldComparison("price", FieldVerdict.Different, null)], null, ["price"]));
        var keys = scan.Entries.Select(entry =>
            FindingVerifier.VerifyKey(entry.Key, [differs], new AgeSignal(null, null, VerificationOutcome.NotVerifiable, null),
                                      null, null, entry.FailureCode, entry.IdleReason)).ToArray();
        var verification = FindingVerifier.Verify(keys, scan, new Applicability(true, true, null, []));

        Assert.Equal(VerificationOutcome.Possible, verification.Outcome);
        Assert.False(verification.Partial);
    }

    /// <summary>
    /// The connection going away partway through, said in fixed words. The client's own text names the
    /// endpoint it was talking to — host and port — and R5 keeps every external system's raw text out of
    /// the response; this was the last refusal that still published it.
    /// </summary>
    [Fact]
    public async Task A_lost_connection_is_reported_without_the_clients_own_text()
    {
        var reader = Reader(("product:1", "DROP"));

        var scan = await reader.ScanAsync(Template);

        Assert.Empty(scan.Entries);
        Assert.Equal(RedisReader.InterruptedReason, scan.InterruptedReason);

        var verification = FindingVerifier.Verify([], scan, new Applicability(true, true, null, []));
        var json = JsonSerializer.Serialize(VerificationQueries.Present("f:1", new VerificationRun(verification, []), null),
                                            CacheDetectiveJsonContext.Default.VerifyFindingResult);

        Assert.DoesNotContain("socket", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SocketFailure", json, StringComparison.Ordinal);
        Assert.Empty(JsonDocument.Parse(json).RootElement.GetProperty("keys").GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// A key that expires between <c>TYPE</c> and <c>GET</c>. <c>TYPE</c> said string, the read came back
    /// nil, and <c>TTL</c> says <c>-2</c> — the key is not there any more. It used to leave with an empty
    /// payload and no code at all, so the run called itself whole while one of its keys had vanished
    /// mid-read: exactly the case the sample cannot speak for.
    /// </summary>
    [Fact]
    public async Task A_key_that_expires_between_the_type_and_the_read_is_reported_as_vanished()
    {
        var reader = OneKey(command => command switch
        {
            "TYPE" => (RedisValue)"string",
            "GET" => RedisValue.Null,
            "TTL" => (RedisValue)(-2),
            _ => (RedisValue)0
        });

        var scan = await reader.ScanAsync(Template);

        var entry = Assert.Single(scan.Entries);
        Assert.Equal(VerificationFailure.KeyVanished, entry.FailureCode);
        Assert.Null(entry.Payload);
        Assert.Null(entry.TtlSeconds);
        Assert.True(FindingVerifier.Verify([Verified(entry)], scan, new Applicability(true, true, null, [])).Partial);
    }

    /// <summary>
    /// A hash that is there and holds no <c>data</c> field. It reads the same way at the command level —
    /// <c>HGET</c> answers nil — and is a different thing: the key exists, and its TTL is what says so.
    /// </summary>
    [Fact]
    public async Task A_hash_without_a_data_field_is_told_apart_from_a_vanished_key()
    {
        var reader = OneKey(command => command switch
        {
            "TYPE" => (RedisValue)"hash",
            "HGET" => RedisValue.Null,
            "TTL" => (RedisValue)60,
            _ => (RedisValue)0
        });

        var scan = await reader.ScanAsync(Template);

        var entry = Assert.Single(scan.Entries);
        Assert.Equal(VerificationFailure.WrongType, entry.FailureCode);
        Assert.NotEqual(VerificationFailure.KeyVanished, entry.FailureCode);
        Assert.Equal(RedisReader.HashWithoutPayloadReason, entry.Reason);
    }

    /// <summary>
    /// A timeout on <c>TTL</c>, after <c>GET</c> already answered. The payload is an observation in its own
    /// right — a field of it that plainly differs from its row makes staleness possible — and replacing the
    /// whole entry with nulls because a later stage timed out discarded the only evidence the key had.
    /// </summary>
    [Fact]
    public async Task A_timeout_after_the_payload_was_read_keeps_the_payload()
    {
        var reader = OneKey(command => command switch
        {
            "TYPE" => (RedisValue)"string",
            "GET" => (RedisValue)"""{ "price": 10 }""",
            "TTL" => throw new RedisTimeoutException("timed out", CommandStatus.Sent),
            _ => (RedisValue)0
        });

        var scan = await reader.ScanAsync(Template);

        var entry = Assert.Single(scan.Entries);
        Assert.Equal("""{ "price": 10 }""", entry.Payload);
        Assert.Null(entry.TtlSeconds);
        Assert.Equal(VerificationFailure.ReadFailed, entry.FailureCode);

        // And the difference the surviving payload shows still carries the finding.
        var differs = new TableRowComparison("dbo.Products",
            new RowComparison([new FieldComparison("price", FieldVerdict.Different, null)], null, ["price"]));
        var key = FindingVerifier.VerifyKey(entry.Key, [differs], new AgeSignal(null, null, VerificationOutcome.NotVerifiable, null),
                                            null, null, entry.FailureCode, entry.Reason);
        Assert.Equal(VerificationOutcome.Possible,
                     FindingVerifier.Verify([key], scan, new Applicability(true, true, null, [])).Outcome);
    }

    /// <summary>
    /// The connection dying on the last and most optional command of all. The payload and the TTL were
    /// already in hand, and the key used to disappear from the sample entirely because the whole read was
    /// abandoned at the point of failure.
    /// </summary>
    [Fact]
    public async Task A_connection_lost_on_the_idle_time_keeps_the_payload_and_the_ttl()
    {
        var reader = OneKey(command => command switch
        {
            "TYPE" => (RedisValue)"string",
            "GET" => (RedisValue)"{}",
            "TTL" => (RedisValue)60,
            _ => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "the socket closed")
        });

        var scan = await reader.ScanAsync(Template);

        var entry = Assert.Single(scan.Entries);
        Assert.Equal("{}", entry.Payload);
        Assert.Equal(60, entry.TtlSeconds);
        Assert.Equal(RedisReader.InterruptedReason, scan.InterruptedReason);
    }

    /// <summary>A reader over one key whose every command after the scan is answered by the script.</summary>
    private static RedisReader OneKey(Func<string, RedisValue> respond) =>
        new((command, _) => command == "SCAN"
                ? Task.FromResult(Page(["product:1"]))
                : Task.FromResult(RedisResult.Create(respond(command))));

    /// <summary>The connection going away partway through keeps what was read before it, and says the
    /// result is partial and why.</summary>
    [Fact]
    public async Task A_connection_lost_partway_keeps_what_was_already_read()
    {
        var reader = Reader(("product:1", "string"), ("product:2", "DROP"), ("product:3", "string"));

        var scan = await reader.ScanAsync(Template);

        Assert.Single(scan.Entries);
        Assert.NotNull(scan.InterruptedReason);
        Assert.Contains("lost partway", scan.InterruptedReason, StringComparison.Ordinal);

        var verification = FindingVerifier.Verify([Verified(scan.Entries[0])], scan,
                                                   new Applicability(true, true, null, []));
        Assert.True(verification.Partial);
        Assert.Contains("lost partway", verification.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A server that answers the SCAN and refuses it — an ACL without SCAN, most often. The refusal is a
    /// code and never the server's own text, which can quote a key, and the run comes back as a
    /// verification that could not be taken rather than as an error out of the tool.
    /// </summary>
    [Fact]
    public async Task A_scan_the_server_refuses_is_not_verifiable_with_a_code()
    {
        var reader = new RedisReader((command, _) => command == "SCAN"
            ? throw new RedisServerException("NOPERM this user has no permissions to run the 'scan' command")
            : Task.FromResult(RedisResult.Create((RedisValue)"string")));

        var error = await Assert.ThrowsAsync<RedisServerException>(() => reader.ScanAsync(Template));

        // The reader lets a refused SCAN out rather than inventing an empty sample; the tool's own catch
        // is what turns it into cache_unavailable, and the code it reports never carries the server's
        // text, which can quote a key. A_scan_the_server_refuses_is_not_verifiable_with_a_code in
        // VerificationToolsTests checks that half.
        Assert.Contains("NOPERM", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("NOPERM", VerificationFailure.CacheUnavailable, StringComparison.Ordinal);
    }

    /// <summary>A database that will not open after the cache was read still reports what the cache said,
    /// with the failure against it, rather than an empty refusal.</summary>
    [Fact]
    public void A_database_that_will_not_open_after_the_scan_still_reports_the_entries()
    {
        var entry = new CacheEntry(RedisReader.Match("product:42", Template, string.Empty), "{}", 60, null, null);
        var scan = new CacheScanResult([entry], true, false, false, false, null);

        var key = FindingVerifier.VerifyKey(entry.Key, [], new AgeSignal(240, null, VerificationOutcome.NotVerifiable, null),
                                            null, null, VerificationFailure.DatabaseUnavailable);
        var verification = FindingVerifier.Verify([key], scan, new Applicability(true, true, null, []));

        Assert.Single(verification.Keys);
        Assert.True(verification.Partial);
        Assert.Equal(VerificationFailure.DatabaseUnavailable, Assert.Single(verification.Keys).FailureCode);
    }

    /// <summary>
    /// The three ways one key of a sample can fail, told apart and carried out to the answer. Each of them
    /// used to become a sentence on the entry and nothing else: the run reported no code, so its result
    /// called itself whole, and a sample in which a key had vanished was indistinguishable from one in
    /// which every key was read and agreed.
    /// </summary>
    /// <param name="behaviour">What the scripted server does with the second key.</param>
    /// <param name="expected">The code that failure has to reach the response under.</param>
    [Theory]
    [InlineData("none", VerificationFailure.KeyVanished)]
    [InlineData("list", VerificationFailure.WrongType)]
    [InlineData("TIMEOUT", VerificationFailure.ReadFailed)]
    public async Task A_key_that_could_not_be_read_carries_its_code_into_the_response(string behaviour, string expected)
    {
        var reader = Reader(("product:1", "string"), ("product:2", behaviour));

        var scan = await reader.ScanAsync(Template);

        var failed = Assert.Single(scan.Entries, entry => entry.FailureCode is not null);
        Assert.Equal(expected, failed.FailureCode);

        // Through the answer the tool serializes, because a code that stops at the entry is a code no
        // reader ever sees.
        var verification = FindingVerifier.Verify(scan.Entries.Select(Verified).ToArray(), scan,
                                                  new Applicability(true, true, null, []));
        Assert.True(verification.Partial);
        var json = JsonSerializer.Serialize(VerificationQueries.Present("f:1", new VerificationRun(verification, []), null),
                                            CacheDetectiveJsonContext.Default.VerifyFindingResult);
        using var document = JsonDocument.Parse(json);
        Assert.Contains(document.RootElement.GetProperty("keys").GetProperty("items").EnumerateArray(),
                        item => item.TryGetProperty("failureCode", out var code) && code.GetString() == expected);
        Assert.True(document.RootElement.GetProperty("partial").GetBoolean());
    }

    /// <summary>The partitioning is decided once for the snapshot. It used to be recomputed from the page
    /// that happened to be asked for, so two pages of one snapshot disagreed about how many there were.</summary>
    [Fact]
    public void Two_pages_of_one_snapshot_agree_and_do_not_overlap()
    {
        var run = new VerificationRun(
            new FindingVerification(VerificationOutcome.Possible, new string('r', 40000),
                Enumerable.Range(0, 6).Select(index => Key($"hash{index:D4}")).ToArray(), 0, 6, 0, false, false), []);

        var first = VerificationQueries.Present("f:1", run, new PageArguments { Page = 1, PageSize = 3 });
        var second = VerificationQueries.Present("f:1", run, new PageArguments { Page = 2, PageSize = 3 });

        Assert.Equal(first.Keys.Pages, second.Keys.Pages);
        Assert.Empty(first.Keys.Items.Select(item => item.KeyHash).Intersect(second.Keys.Items.Select(item => item.KeyHash)));
        Assert.All([first, second], result =>
            Assert.True(Size(result) <= ResponseEnvelope.MaximumSerializedBytes, $"the page was {Size(result)} bytes"));
    }

    /// <summary>
    /// A page of several middling records. The envelope fills itself to the whole limit unless it is told
    /// what wraps it, so this used to fit the envelope and overflow the result; the single-record check
    /// only ever caught one enormous record per page.
    /// </summary>
    [Fact]
    public void A_page_of_middling_records_leaves_room_for_the_result_around_it()
    {
        var keys = Enumerable.Range(0, 12).Select(index => Middling($"hash{index:D4}")).ToArray();
        var run = new VerificationRun(
            new FindingVerification(VerificationOutcome.Possible, new string('r', 4000), keys, 0, keys.Length, 0, false, false),
            ["dbo.Products"]);

        for (var page = 1; page <= 12; page++)
        {
            var result = VerificationQueries.Present("f:1", run, new PageArguments { Page = page, PageSize = 7 });
            Assert.True(Size(result) <= ResponseEnvelope.MaximumSerializedBytes,
                        $"page {page} was {Size(result)} bytes");
        }
    }

    private static KeyVerification Middling(string hash) =>
        new(Template, hash, VerificationOutcome.Possible,
            Enumerable.Range(0, 8)
                      .Select(index => new FieldObservation($"field_number_{index:D3}", FieldVerdict.Different, false,
                                                            "the cached value and the row disagree"))
                      .ToArray(),
            false, null, "a field differs", null);

    private static int Size(VerifyFindingResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, CacheDetectiveJsonContext.Default.VerifyFindingResult).Length;

    private static KeyVerification Key(string hash) =>
        new(Template, hash, VerificationOutcome.Possible, [], false, null, "a field differs", null);

    private static KeyVerification Verified(CacheEntry entry) =>
        FindingVerifier.VerifyKey(entry.Key, [], new AgeSignal(null, null, VerificationOutcome.NotVerifiable, null), null,
                                  null, entry.FailureCode, entry.Reason);

    private static RedisReader Reader(params (string Key, string Behaviour)[] keys) => Reader(false, keys);

    /// <summary>A reader over a scripted server: the scan returns the named keys, and each key's TYPE
    /// either answers or throws the failure the script asks for.</summary>
    /// <param name="refuseIdle">Whether the server refuses <c>OBJECT IDLETIME</c>, as one on an LFU
    /// eviction policy does for every key.</param>
    private static RedisReader Reader(bool refuseIdle, params (string Key, string Behaviour)[] keys)
    {
        var behaviours = keys.ToDictionary(entry => entry.Key, entry => entry.Behaviour, StringComparer.Ordinal);
        return new RedisReader((command, arguments) =>
        {
            if (command == "SCAN")
                return Task.FromResult(Page(keys.Select(entry => entry.Key).ToArray()));

            if (command == "OBJECT" && refuseIdle)
                throw new RedisServerException("ERR An LFU maxmemory policy is selected, idle time is not tracked.");

            var key = (string)arguments[command == "OBJECT" ? 1 : 0];
            var behaviour = behaviours[key];
            if (command == "TYPE")
            {
                return behaviour switch
                {
                    "WRONGTYPE" => throw new RedisServerException("WRONGTYPE Operation against a key holding the wrong kind of value"),
                    "TIMEOUT" => throw new RedisTimeoutException("timed out", CommandStatus.Sent),
                    "DROP" => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "the socket closed"),
                    // Anything else is the type the server reports, so a script can ask for 'none' — the
                    // key was gone by the time it was read — as easily as for 'string' or 'list'.
                    _ => Task.FromResult(RedisResult.Create((RedisValue)behaviour))
                };
            }

            return Task.FromResult(command switch
            {
                "GET" => RedisResult.Create((RedisValue)"{}"),
                "TTL" => RedisResult.Create((RedisValue)60),
                _ => RedisResult.Create((RedisValue)0)
            });
        });
    }

    private static RedisResult Page(IReadOnlyList<string> keys) =>
        RedisResult.Create([RedisResult.Create((RedisValue)"0"),
                            RedisResult.Create(keys.Select(key => (RedisValue)key).ToArray())]);
}
