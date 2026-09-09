using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StackExchange.Redis;

namespace CacheDetective.Verification;

/// <summary>
/// Every command verification issues against a cache, and the one method that issues them. The allowed
/// set is <c>SCAN</c>, <c>TYPE</c>, <c>GET</c>, <c>HGET</c>, <c>TTL</c> and <c>OBJECT IDLETIME</c>; none
/// of them writes, which is what makes the read-only claim checkable rather than asserted — a unit test
/// drives the reader against a fake command seam and inspects the commands that arrive at
/// <see cref="RedisCommand"/>.
/// <para>The reader's own list is not enough, because <see cref="ConnectionMultiplexer"/> sends
/// housekeeping commands of its own. The ones it is allowed to send are <c>HELLO</c>/<c>AUTH</c>,
/// <c>ECHO</c>, <c>PING</c>, <c>SELECT</c>, <c>INFO</c>, <c>CONFIG GET</c> and <c>CLUSTER NODES</c>. What
/// it must never do is the probe named in <see cref="RefuseReason"/>.</para>
/// <para>A budget stop stops the tool waiting; it does not cancel a command already on the wire. See the
/// README.</para>
/// </summary>
internal sealed class RedisReader
{
    /// <summary>The commands this reader is allowed to send. Anything else is a bug in the reader.</summary>
    internal static readonly string[] AllowedCommands = ["SCAN", "TYPE", "GET", "HGET", "TTL", "OBJECT"];

    internal const int MaximumIterations = 50;
    internal const int MaximumMatches = 20;
    internal const int ScanCount = 1000;

    /// <summary>The payload field <c>Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache</c> stores
    /// the value under. The whole <c>distributed</c> store is a hash, not a string, so <c>GET</c> alone
    /// would read nothing from any ASP.NET Core application.</summary>
    internal const string DistributedPayloadField = "data";

    internal static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(5);

    internal const string RefuseReason =
        "the connection configuration disables INFO, and with INFO unavailable the client would write a probe key";

    private const string AmbiguousReason =
        "the match is ambiguous: the template's placeholders can be assigned to this key in more than one way";

    private const string NoMatchReason = "the key does not match the template";

    /// <summary>The one seam: the only thing in the reader that reaches the server.</summary>
    internal delegate Task<RedisResult> RedisCommand(string command, object[] arguments);

    private readonly RedisCommand _execute;
    private readonly string _keyPrefix;
    private readonly TimeSpan _budget;
    private readonly Stopwatch _observed = Stopwatch.StartNew();

    internal RedisReader(RedisCommand execute, string keyPrefix = "", TimeSpan? budget = null)
    {
        _execute = execute;
        _keyPrefix = keyPrefix;
        _budget = budget ?? ScanBudget;
    }

    /// <summary>
    /// The reader's own clock, running from the moment it was built. Every entry records the moment it was
    /// read against it, and the caller reads it again after each database query, so the two readings of one
    /// key are placed on one timeline by subtraction.
    /// <para>It is the reader's clock rather than the caller's because the entries are the things being
    /// dated, and a sample of twenty keys can take long enough that dating them all from the end of the
    /// scan loses the whole of the sample's own duration.</para>
    /// </summary>
    internal double ElapsedSeconds => _observed.Elapsed.TotalSeconds;

    /// <summary>Builds a reader over a live connection. The database comes from the connection string,
    /// because a workspace that meant another one would have said so there.</summary>
    internal static RedisReader Create(IConnectionMultiplexer multiplexer, ConfigurationOptions options, string keyPrefix)
    {
        var database = multiplexer.GetDatabase(options.DefaultDatabase ?? 0);
        return new RedisReader((command, arguments) => database.ExecuteAsync(command, arguments), keyPrefix);
    }

    /// <summary>
    /// Whether this connection string may be used at all. It is a refusal and not a setting, because the
    /// writing path of the client cannot be closed by configuration: in 2.8.31 <c>AutoConfigureAsync</c>
    /// sends <c>SET</c> with <c>PX 1</c> against database 0 when <c>INFO</c> is unavailable, and the test
    /// it makes is on the command map — so <c>AllowAdmin</c>, <c>ConfigCheckSeconds</c>, <c>TieBreaker</c>
    /// and <c>AbortOnConnectFail</c> are all powerless, and comparing the key list before and after a run
    /// would miss a write that lived one millisecond in somebody else's database.
    /// <para><see cref="CommandMap.IsAvailable"/> is <c>internal</c> in 2.8.31, so the map is read through
    /// <see cref="CommandMap.ToString"/> — the only public surface that prints the deltas, as
    /// <c>$INFO=</c> for a disabled command and <c>$INFO=OTHER</c> for a renamed one. A rename keeps the
    /// command available and is not refused; a disable is.</para>
    /// </summary>
    internal static string? Refuse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return Refuse(ConfigurationOptions.Parse(connectionString));
    }

    internal static string? Refuse(ConfigurationOptions options) =>
        options.CommandMap.ToString()
               .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Any(delta => delta.Equals("$INFO=", StringComparison.OrdinalIgnoreCase))
            ? RefuseReason
            : null;

    /// <summary>One node and one database, or nothing. A second endpoint means the keys are spread over
    /// servers this reader would have to fan out across, and a cluster means the same by another
    /// route.</summary>
    internal static string? UnsupportedTopology(IConnectionMultiplexer multiplexer)
    {
        var endpoints = multiplexer.GetEndPoints();
        return endpoints.Length == 0
                   ? "the connection names no endpoint"
                   : UnsupportedTopology(endpoints.Length, multiplexer.GetServer(endpoints[0]).ServerType);
    }

    internal static string? UnsupportedTopology(int endpointCount, ServerType serverType)
    {
        if (endpointCount > 1)
        {
            return $"the connection names {endpointCount} endpoints and verification reads one node";
        }

        return serverType == ServerType.Cluster
                   ? "the server is in cluster mode and verification reads one node"
                   : null;
    }

    /// <summary>
    /// The glob a template is searched with, by the tail rule of <c>docs/adr/0010</c>: named placeholders
    /// stay in the tail and become <c>*</c>, and the tail begins after a leading placeholder — which is a
    /// whole base address and not a segment — and after the last unknown <c>{?}</c>. So
    /// <c>product:{id}</c> gives <c>*product:*</c>. A tail with no literal in it is not searched at all:
    /// the glob would be <c>*</c> and would return the whole keyspace.
    /// </summary>
    internal static string? Glob(string template)
    {
        var tail = Tail(template);
        if (tail is null)
        {
            return null;
        }

        var glob = new StringBuilder("*");
        foreach (var part in tail)
        {
            glob.Append(part.IsLiteral ? EscapeGlob(part.Text) : "*");
        }

        return glob.ToString();
    }

    /// <summary>
    /// Matches one actual key against a template and says what the placeholders were, or why it cannot
    /// say. Uniqueness is <em>checked</em> and never inferred from the shape of the template: "there is a
    /// non-empty literal between the placeholders" is not enough, because <c>item:{left}:{id}</c> against
    /// <c>item:a:b:c</c> admits both <c>left=a, id=b:c</c> and <c>left=a:b, id=c</c>. The key is matched
    /// twice, once with lazy groups and once with greedy ones; identical assignments mean the assignment
    /// is the only one, and different assignments mean there is more than one and none may be used.
    /// </summary>
    internal static KeyMatch Match(string key, string template, string keyPrefix)
    {
        var tail = Tail(template);
        if (tail is null)
        {
            return KeyMatch.Failed(template, key, NoMatchReason);
        }

        var lazy = Regex.Match(key, Pattern(tail, lazy: true), RegexOptions.CultureInvariant);
        var greedy = Regex.Match(key, Pattern(tail, lazy: false), RegexOptions.CultureInvariant);
        if (!lazy.Success || !greedy.Success)
        {
            return KeyMatch.Failed(template, key, NoMatchReason);
        }

        var lazyGroups = Captures(lazy);
        if (!lazyGroups.SequenceEqual(Captures(greedy), StringComparer.Ordinal))
        {
            return KeyMatch.Failed(template, key, AmbiguousReason);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 1;
        foreach (var part in tail.Where(part => !part.IsLiteral))
        {
            index++;
            if (part.Text.Length > 0 && part.Text != "?")
            {
                values[part.Text] = lazy.Groups[index].Value;
            }
        }

        return new KeyMatch(template, ShortHash(key), values, lazy.Groups[1].Value != keyPrefix, null);
    }

    /// <summary>
    /// Walks the keyspace for one template and reads what it finds. The traversal is a raw <c>SCAN</c>
    /// rather than <see cref="IServer.Keys"/>, because that helper falls back to <c>KEYS</c> on a server
    /// that cannot scan and blocks it for the length of the keyspace.
    /// <para>Even an exhausted <c>SCAN</c> only guarantees the keys that existed for the whole of the
    /// traversal: a key added and removed while it ran may or may not appear, and that is the strongest
    /// statement this method makes.</para>
    /// </summary>
    internal async Task<CacheScanResult> ScanAsync(string template)
    {
        var pattern = Glob(template);
        if (pattern is null)
        {
            return CacheScanResult.NotVerifiable($"the template '{template}' has no literal segment to search on");
        }

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var elapsed = Stopwatch.StartNew();
        var cursor = "0";
        var iterations = 0;
        bool scanExhausted = false, limitReached = false, budgetStopped = false, matchesDiscarded = false;

        while (true)
        {
            if (iterations >= MaximumIterations)
            {
                budgetStopped = true;
                break;
            }

            var remaining = _budget - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                budgetStopped = true;
                break;
            }

            // ExecuteAsync carries neither a cancellation token nor a per-command timeout, so the wait is
            // bounded here instead. The command stays on the wire; we simply stop waiting for it, and the
            // abandoned task gets a continuation so its failure is observed rather than unobserved.
            var pending = _execute("SCAN", [cursor, "MATCH", pattern, "COUNT", ScanCount]);
            if (await Task.WhenAny(pending, Task.Delay(remaining)).ConfigureAwait(false) != pending)
            {
                Observe(pending);
                budgetStopped = true;
                break;
            }

            iterations++;
            var page = (RedisResult[])(await pending.ConfigureAwait(false))!;
            cursor = (string?)page[0] ?? "0";
            foreach (var key in ((RedisValue[])page[1]!).Select(value => (string)value!))
            {
                if (!seen.Add(key))
                {
                    continue;
                }

                if (found.Count >= MaximumMatches)
                {
                    matchesDiscarded = true;
                    continue;
                }

                found.Add(key);
            }

            limitReached = found.Count >= MaximumMatches;
            if (cursor == "0")
            {
                scanExhausted = true;
                break;
            }

            if (limitReached)
            {
                break;
            }
        }

        var entries = new List<CacheEntry>();
        string? interrupted = null;
        foreach (var key in found)
        {
            try
            {
                entries.Add(await ReadAsync(key, template).ConfigureAwait(false));
            }
            catch (ConnectionLost lost)
            {
                // The connection went away mid-sample. What was read before it is still a reading, so the
                // entries already gathered are kept — including whatever the key being read had already
                // answered — and the result says it is partial.
                //
                // The client's own text is not repeated: it names the endpoint it was talking to, host and
                // port included, and R5 keeps every external system's raw text out of the response. This
                // was the one refusal that still published it while every other one carried a fixed
                // sentence or a code.
                if (lost.Partial is { } partial)
                    entries.Add(partial);
                interrupted = InterruptedReason;
                break;
            }
            catch (Exception error) when (error is RedisServerException or RedisTimeoutException)
            {
                // One key failed. The others are still readable, so this key carries a safe reason and the
                // walk goes on rather than the whole sample being thrown away.
                entries.Add(new CacheEntry(Match(key, template, _keyPrefix), null, null, null, SafeReason(error),
                                           VerificationFailure.ReadFailed, ElapsedSeconds));
            }
        }

        return new CacheScanResult(entries, scanExhausted, limitReached, budgetStopped, matchesDiscarded, null, interrupted);
    }

    /// <summary>Why a sample stopped short, said without the client's own text.</summary>
    internal const string InterruptedReason = "the connection to the cache was lost partway through the sample";

    /// <summary>Why the idle time of one entry is missing. <c>OBJECT IDLETIME</c> is refused outright by a
    /// server whose <c>maxmemory-policy</c> is one of the LFU ones, which is a configuration and not a
    /// fault, so it is recorded and the entry keeps everything else that was read.</summary>
    internal const string IdleUnavailableReason = "idle time unavailable: the server refused OBJECT IDLETIME";

    /// <summary>What went wrong with one key, said without quoting the key. A <c>WRONGTYPE</c> is the entry
    /// being of a kind this reader does not read; anything else is named as a failed read.</summary>
    private static string SafeReason(Exception? error) =>
        error is RedisServerException server && server.Message.Contains("WRONGTYPE", StringComparison.OrdinalIgnoreCase)
            ? "the entry is of a type verification does not read"
            : "reading this entry failed and the rest of the sample was read without it";

    /// <summary>Why a hash is there and holds nothing this reader can read.</summary>
    internal const string HashWithoutPayloadReason =
        "the entry is a hash with no 'data' field, so it holds nothing verification can read";

    /// <summary>The answer <c>TTL</c> gives for a key that is not there. It is the one reading that tells a
    /// key which expired between two commands from one that simply has no deadline (<c>-1</c>).</summary>
    private const int TtlKeyMissing = -2;

    /// <summary>
    /// Reads one entry in four stages — <c>TYPE</c>, the payload, <c>TTL</c>, <c>OBJECT IDLETIME</c> —
    /// accumulating as it goes, so that a failure at one stage keeps what the stages before it read. Each
    /// of those is an observation in its own right: a payload already in hand that plainly differs from its
    /// row makes staleness <em>possible</em>, and throwing it away because a later <c>TTL</c> timed out
    /// discarded the only evidence the key had produced.
    /// <para>A key can also go away between two of the stages, and the sample has to say so. <c>TYPE</c>
    /// answering <c>none</c> is the obvious case, but not the only one: <c>GET</c> returning nil on a key
    /// <c>TYPE</c> had just called a string, or <c>TTL</c> answering <c>-2</c>, is the same key expiring one
    /// command later. It used to come back as an entry with an empty payload and no code at all, so the
    /// result called itself whole while a key of its sample had vanished mid-read.</para>
    /// <para>A hash that is there and has no <c>data</c> field is a different thing, and is told apart by
    /// its TTL: the key exists, and what it holds is not something this reader reads.</para>
    /// </summary>
    private async Task<CacheEntry> ReadAsync(string key, string template)
    {
        var match = Match(key, template, _keyPrefix);
        string? payload = null;
        double? ttl = null;
        double? idle = null;
        string? reason = null;
        string? code = null;
        string? idleReason = null;

        CacheEntry Entry() => new(match, payload, ttl, idle, reason ?? match.Reason, code, ElapsedSeconds, idleReason);

        void Vanished()
        {
            payload = null;
            reason = "the entry was gone by the time it was read";
            code = VerificationFailure.KeyVanished;
        }

        try
        {
            var type = ((string?)await _execute("TYPE", [key]).ConfigureAwait(false))?.Trim()?.ToLowerInvariant();
            switch (type)
            {
                case "string":
                    payload = (string?)await _execute("GET", [key]).ConfigureAwait(false);
                    break;
                case "hash":
                    payload = (string?)await _execute("HGET", [key, DistributedPayloadField]).ConfigureAwait(false);
                    break;
                case null or "" or "none":
                    Vanished();
                    break;
                default:
                    reason = $"the entry is of type '{type}' and verification reads strings and hashes";
                    code = VerificationFailure.WrongType;
                    break;
            }

            if (code is null)
            {
                var reported = (double?)await _execute("TTL", [key]).ConfigureAwait(false);
                ttl = reported >= 0 ? reported : null;

                // Now that the TTL is known, the empty payload can be read for what it is.
                if (reported == TtlKeyMissing || (type == "string" && payload is null))
                {
                    Vanished();
                    ttl = null;
                }
                else if (type == "hash" && payload is null)
                {
                    reason = HashWithoutPayloadReason;
                    code = VerificationFailure.WrongType;
                }
            }

            // Idle time is the one optional observation of the four, and it is read last so that a refusal
            // cannot cost the three that matter. A server on an LFU eviction policy refuses OBJECT IDLETIME
            // as a matter of course — Redis rejects it outright when maxmemory-policy is lfu — and letting
            // that throw out of here replaced the whole entry with nulls, so on such a server no key of any
            // sample had a payload to compare and verification quietly stopped working.
            if (code is null)
            {
                try
                {
                    idle = (double?)await _execute("OBJECT", ["IDLETIME", key]).ConfigureAwait(false);
                }
                catch (RedisServerException)
                {
                    idleReason = IdleUnavailableReason;
                }
            }
        }
        catch (RedisConnectionException)
        {
            // The connection died mid-key. Whatever the earlier stages read is still a reading and is
            // handed back for the sample to keep; the scan itself stops, which is the caller's business.
            throw new ConnectionLost(payload is null && ttl is null && code is null ? null : Entry() with
            {
                Reason = reason ?? SafeReason(null),
                FailureCode = code ?? VerificationFailure.ReadFailed
            });
        }
        catch (Exception error) when (error is RedisServerException or RedisTimeoutException)
        {
            // The stages that already answered keep their answers; this key is marked as one the reader
            // could not finish.
            reason ??= SafeReason(error);
            code ??= VerificationFailure.ReadFailed;
        }

        return Entry();
    }

    /// <summary>Carries out of a half-read key whatever its earlier stages managed to read, so that the
    /// scan can keep it and still stop.</summary>
    private sealed class ConnectionLost(CacheEntry? partial) : Exception
    {
        internal CacheEntry? Partial { get; } = partial;
    }

    /// <summary>A short, stable name for a key the caller may not see. The actual key never leaves this
    /// class: what goes out is the template, this hash, and the placeholder values.</summary>
    internal static string ShortHash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];

    private static void Observe(Task task) =>
        task.ContinueWith(faulted => _ = faulted.Exception,
                          CancellationToken.None,
                          TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                          TaskScheduler.Default);

    private static string[] Captures(System.Text.RegularExpressions.Match match) =>
        match.Groups.Cast<Group>().Skip(1).Select(group => group.Value).ToArray();

    private static string Pattern(IReadOnlyList<KeyPart> tail, bool lazy)
    {
        var placeholder = lazy ? "(.+?)" : "(.+)";
        var pattern = new StringBuilder(lazy ? "^(.*?)" : "^(.*)");
        foreach (var part in tail)
        {
            pattern.Append(part.IsLiteral ? Regex.Escape(part.Text) : placeholder);
        }

        return pattern.Append('$').ToString();
    }

    /// <summary>The tail of a template, or <c>null</c> when there is no literal left in it.</summary>
    private static IReadOnlyList<KeyPart>? Tail(string template)
    {
        var parts = Split(template);
        var start = parts.Count > 0 && !parts[0].IsLiteral ? 1 : 0;
        for (var index = parts.Count - 1; index >= start; index--)
        {
            if (!parts[index].IsLiteral && parts[index].Text == "?")
            {
                start = index + 1;
                break;
            }
        }

        var tail = parts.Skip(start).ToArray();
        return tail.Any(part => part.IsLiteral && part.Text.Length > 0) ? tail : null;
    }

    private static List<KeyPart> Split(string template)
    {
        var parts = new List<KeyPart>();
        var literal = new StringBuilder();
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] != '{' || template.IndexOf('}', index) is var close && close < 0)
            {
                literal.Append(template[index]);
                continue;
            }

            if (literal.Length > 0)
            {
                parts.Add(new KeyPart(literal.ToString(), true));
                literal.Clear();
            }

            parts.Add(new KeyPart(template[(index + 1)..close], false));
            index = close;
        }

        if (literal.Length > 0)
        {
            parts.Add(new KeyPart(literal.ToString(), true));
        }

        return parts;
    }

    /// <summary>Glob metacharacters in a literal are escaped, or a key whose name contains one would
    /// search for something else.</summary>
    private static string EscapeGlob(string literal)
    {
        var escaped = new StringBuilder(literal.Length);
        foreach (var character in literal)
        {
            if (character is '*' or '?' or '[' or ']' or '\\')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }

    private readonly record struct KeyPart(string Text, bool IsLiteral);
}

/// <summary>What a key was, as far as anything outside the reader is concerned: the template it matched,
/// a short hash standing in for the key itself, the placeholder values when the match was the only one
/// possible, and otherwise a reason that names no key.</summary>
internal sealed record KeyMatch(string Template, string KeyHash, IReadOnlyDictionary<string, string>? Values,
                                bool Prefixed, string? Reason)
{
    internal static KeyMatch Failed(string template, string key, string reason) =>
        new(template, RedisReader.ShortHash(key), null, false, reason);
}

/// <param name="FailureCode">The fixed code for a key that could not be read — it vanished, it held a type
/// this reader does not read, or the read itself failed. It travels to the answer beside the reason so that
/// the result is marked partial and a reader can count the three apart.</param>
/// <param name="ObservedSeconds">When this entry was read, on <see cref="RedisReader.ElapsedSeconds"/>. A
/// later database reading is brought back to <em>this</em> moment rather than to the end of the sample, so
/// that the keys read first are not credited with the time it took to read the rest.</param>
/// <param name="IdleReason">Why the idle time is missing, when it is missing for a reason worth saying. It
/// is not a failure: the entry was read, and only this one optional observation was refused.</param>
internal sealed record CacheEntry(KeyMatch Key, string? Payload, double? TtlSeconds, double? IdleSeconds, string? Reason,
                                  string? FailureCode = null, double ObservedSeconds = 0, string? IdleReason = null);

/// <summary>The traversal and what it is worth. The four flags are the whole account of how complete the
/// answer is, and a caller that ignores them reads a partial scan as a whole one.
/// <para><paramref name="InterruptedReason"/> is set when the sample was cut short by a failure rather
/// than by a budget: the entries before it are still reported, and the result is partial.</para></summary>
internal sealed record CacheScanResult(IReadOnlyList<CacheEntry> Entries, bool ScanExhausted, bool LimitReached,
                                       bool BudgetStopped, bool MatchesDiscarded, string? NotVerifiableReason,
                                       string? InterruptedReason = null)
{
    internal static CacheScanResult NotVerifiable(string reason) => new([], false, false, false, false, reason);
}
