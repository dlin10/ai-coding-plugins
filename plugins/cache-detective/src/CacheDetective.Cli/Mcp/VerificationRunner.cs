using CacheDetective.Configuration;
using CacheDetective.Graph;
using CacheDetective.Rules;
using CacheDetective.Verification;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;
using System.Data.Common;

namespace CacheDetective.Mcp;

/// <summary>Asking a live cache and a live database what one finding's key actually looks like right now.
/// <para>Verification observes and never suppresses: a run that agrees with a finding, disagrees with it, or
/// cannot be carried out at all leaves the finding exactly where it was. See <c>docs/adr/0012</c>.</para>
/// <para>Named <c>VerificationRunner</c> and not <c>FindingVerification</c> or <c>FindingVerifier</c>,
/// because <see cref="CacheDetective.Verification"/> already declares both of those and
/// <c>CacheDetective.Mcp</c> imports it: a type of either name here would shadow rather than collide, and
/// <see cref="Empty"/> would quietly build the wrong one.</para>
/// <para>Nothing here holds the session. The graph, the configuration and the reader factory arrive as
/// arguments on each call, so a run always reads the state the session has now rather than the state it had
/// when a collaborator was built.</para>
/// </summary>
internal static class VerificationRunner
{
    /// <summary>
    /// The live run. Everything that stops it before a reading is taken comes back as a successful answer
    /// carrying <c>not_verifiable</c> and a code, because a caller asking for a verification is owed an
    /// account of why there is none rather than an error.
    /// </summary>
    internal static async Task<VerificationRun> RunVerificationAsync(CacheGraph graph, WorkspaceConfiguration? configuration,
                                                                     Func<IConnectionMultiplexer, ConfigurationOptions, string, RedisReader> openCacheReader,
                                                                     FindingSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (configuration?.Verify is not { } verify)
        {
            return Refused(VerificationFailure.NotConfigured);
        }

        if (verify.Redis is null)
        {
            return Refused(VerificationFailure.NotConfigured);
        }

        string connectionString;
        try
        {
            connectionString = verify.ResolveRedis();
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            // The message can name an environment variable but never a connection string, so it is the
            // fixed code that travels rather than the exception's own text.
            return Refused(VerificationFailure.NotConfigured);
        }

        // Whether verification begins at all is settled before anything is opened. A key whose role is not
        // cache, a store the workspace did not list, or a key that depends on no table is owed that reason
        // and not "the cache was unreachable" — and opening a connection to find out would send the
        // client's own housekeeping commands for a run that was never going to read anything.
        var (key, missing) = Subject(graph, snapshot.Item);
        if (key is null)
        {
            return Refused(missing ?? "the finding names no cache key to read");
        }

        string? refusal;
        try
        {
            // Refuse parses the connection string, so a malformed one throws here, before the try below.
            refusal = RedisReader.Refuse(connectionString);
        }
        catch (ArgumentException)
        {
            return Refused(VerificationFailure.NotConfigured);
        }

        var applicability = FindingVerifier.Assess(graph, key, verify, refusal);
        var tables = applicability.Tables.Select(table => table.Name).Order(StringComparer.Ordinal).ToArray();
        if (!applicability.Starts)
        {
            return new VerificationRun(Empty(applicability.Reason), tables);
        }

        // The database half is checked here, once the run is known to be one that would read something —
        // both that it resolves and that what it resolves to is a connection string at all. Its only
        // resolution used to happen deep inside the reading, where neither exception it throws is one
        // Classify knows, so they escaped verify_finding as an error rather than an account of why there is
        // no verification; and a malformed string was found only after the cache had been scanned, so the
        // sample already in hand was thrown away for an empty refusal that did not even say it was partial.
        // Both messages can quote the string or name an environment variable, so the fixed code travels.
        try
        {
            _ = new SqlConnectionStringBuilder(verify.ResolveDatabase());
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException
                                              or ArgumentException or FormatException)
        {
            return new VerificationRun(Empty(VerificationFailure.NotConfigured), tables);
        }

        try
        {
            await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(false);
            if (!multiplexer.IsConnected)
            {
                return Refused(VerificationFailure.CacheUnavailable);
            }

            if (RedisReader.UnsupportedTopology(multiplexer) is { } topology)
            {
                return Refused(topology);
            }

            return await ReadVerificationAsync(multiplexer, configuration, openCacheReader, verify, key, applicability, tables,
                                               snapshot.Item.Table, cancellationToken)
                       .ConfigureAwait(false);
        }
        catch (Exception error) when (error is RedisConnectionException or RedisTimeoutException)
        {
            return Refused(VerificationFailure.CacheUnavailable);
        }
        catch (RedisServerException)
        {
            // The server answered and refused — an ACL without SCAN, most often. The server's own text can
            // quote a key, so the fixed code travels instead of the message.
            return Refused(VerificationFailure.CacheUnavailable);
        }
        catch (ArgumentException)
        {
            // A connection string neither client would parse. Its text is the string itself, so it never
            // leaves this method.
            return Refused(VerificationFailure.NotConfigured);
        }
        catch (DbException error)
        {
            return Refused(VerificationReader.IsPermissionDenied(error)
                               ? VerificationFailure.PermissionDenied
                               : VerificationFailure.DatabaseUnavailable);
        }
    }

    /// <summary>
    /// The key a finding is actually about. For most rules that is the key the finding names, but a
    /// <c>STALE_PARENT_KEY</c> finding is a claim about the <em>parent</em>: the catalogue puts the child's
    /// template in <c>keyTemplate</c>, and reading that instead let a healthy child refute a stale parent —
    /// a refutation through a different key, which R8 forbids outright.
    /// <para>The parent is chosen by template <em>and</em> store, both of which the catalogue now carries.
    /// The rule groups a key's dependencies by store and template together and never requires parent and
    /// child to share a store, so a template on its own does not name a key: a workspace holding
    /// <c>basket:{id}</c> in memory and again in redis has two of them. Preferring the child's store and
    /// falling back to a lone candidate read whichever one happened to be there, and its fields agreeing
    /// then refuted a finding made about the other — the same wrong-subject refutation, one store
    /// along.</para>
    /// </summary>
    private static (CacheKey? Key, string? Reason) Subject(CacheGraph graph, FindingItem item)
    {
        if (item.Rule != StaleParentKeyFinding.Rule)
        {
            return (graph.CacheKeys.FirstOrDefault(candidate => candidate.Template == item.KeyTemplate &&
                                                                candidate.Store == item.Store), null);
        }

        if (item.ParentTemplate is not { } parent || item.ParentStore is not { } parentStore)
        {
            return (null, "the finding is about a parent key that the catalogue does not name, so there is nothing to read");
        }

        var chosen = graph.CacheKeys.FirstOrDefault(candidate => candidate.Template == parent &&
                                                                 candidate.Store == parentStore);
        return chosen is not null
                   ? (chosen, null)
                   : (null, $"the parent key '{parent}' in store '{parentStore}' this finding is about is not in the graph, " +
                            "so there is nothing to read");
    }

    /// <summary>The reading itself, once both ends are reachable.</summary>
    private static async Task<VerificationRun> ReadVerificationAsync(IConnectionMultiplexer multiplexer,
                                                                     WorkspaceConfiguration? configuration,
                                                                     Func<IConnectionMultiplexer, ConfigurationOptions, string, RedisReader> openCacheReader,
                                                                     VerifyConfiguration verify,
                                                                     CacheKey key, Applicability applicability,
                                                                     IReadOnlyList<string> tables, string? refutingTable,
                                                                     CancellationToken cancellationToken)
    {
        var reader = openCacheReader(multiplexer, ConfigurationOptions.Parse(verify.ResolveRedis()), verify.KeyPrefix);
        var scan = await reader.ScanAsync(key.Template).ConfigureAwait(false);

        // Every database reading is a later observation than the cache reading of the key it is about, and
        // the gap between those two moments is what brings them onto one timeline. The clock is the
        // reader's own, still running: each entry knows when it was read against it, so the gap is measured
        // per key rather than from the end of the whole sample.
        if (scan.NotVerifiableReason is not null)
        {
            return new VerificationRun(Empty(scan.NotVerifiableReason), tables);
        }

        SqlConnection connection;
        try
        {
            connection = VerificationReader.Connect(verify.ResolveDatabase());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (FindingVerifier.Classify(error) is { } code)
        {
            // The cache was read and the database could not be opened. The entries are already in hand, so
            // they are reported with the failure against them rather than discarded for an empty refusal.
            var unread = scan.Entries
                             .Select(entry => FindingVerifier.VerifyKey(entry.Key, [], NoTables(key, entry),
                                                                        configuration?.Sensitive,
                                                                        FindingVerifier.ReadPayload(entry.Payload).Length,
                                                                        entry.FailureCode ?? code, entry.Reason))
                             .ToArray();
            return new VerificationRun(FindingVerifier.Verify(unread, scan, FindingVerifier.AfterScan(applicability, scan)), tables);
        }

        await using (connection)
        {
            return await VerifySampleAsync(new VerificationReader(connection), verify, key, applicability, tables, scan,
                                           () => reader.ElapsedSeconds, configuration?.Sensitive, cancellationToken, refutingTable)
                       .ConfigureAwait(false);
        }
    }

    /// <summary>The sample against the database: the half of the working path that runs once both readers
    /// are in hand, so that a test can drive it with fake ones.</summary>
    /// <param name="now">The cache reader's clock, read afresh <em>after</em> each database reading returns.
    /// The gap for one key is this less that key's own <see cref="CacheEntry.ObservedSeconds"/>. It is a
    /// reading and not a stopwatch so that a test can state the moments it wants to check.</param>
    /// <param name="sensitive">The workspace's extra field-name masks, read from the configuration by the
    /// caller rather than here, because this runs below the session and holds none of it.</param>
    /// <param name="refutingTable">The one table the finding is about, when it names one, so that only
    /// agreement with that table's row can refute it.</param>
    internal static async Task<VerificationRun> VerifySampleAsync(VerificationReader database, VerifyConfiguration verify, CacheKey key,
                                                                  Applicability applicability, IReadOnlyList<string> tables,
                                                                  CacheScanResult scan, Func<double> now, string[]? sensitive,
                                                                  CancellationToken cancellationToken, string? refutingTable = null)
    {
        // What the sample turned out to be decides the rest of the applicability: only now is it known
        // whether the keys read could each be assigned to the template in exactly one way.
        var afterScan = FindingVerifier.AfterScan(applicability, scan);
        var verified = new List<KeyVerification>();
        foreach (var entry in scan.Entries)
        {
            verified.Add(await VerifyEntryAsync(database, verify, key, entry, tables, now, sensitive, refutingTable, cancellationToken)
                             .ConfigureAwait(false));
        }

        return new VerificationRun(FindingVerifier.Verify(verified, scan, afterScan), tables);
    }

    /// <summary>The declared tables, looked up without regard to case. Table names reach this from the
    /// code and the configuration is written by hand, so <c>dbo.Products</c> and <c>DBO.PRODUCTS</c> are
    /// the same table; the configuration reader refuses two keys that differ only in case, so the
    /// insensitive dictionary cannot lose an entry.</summary>
    private static Dictionary<string, VerifyTableConfiguration> Declared(VerifyConfiguration verify) =>
        new(verify.Tables ?? [], StringComparer.OrdinalIgnoreCase);

    private static AgeSignal NoTables(CacheKey key, CacheEntry entry) =>
        new(FindingVerifier.EntryAge(key, entry), null, VerificationOutcome.NotVerifiable, "the table's last write was not read");

    /// <summary>
    /// One entry against its tables. The last write is read for <em>every</em> dependent table, because
    /// <c>sys.dm_db_index_usage_stats</c> holds no row data and needs no <c>key</c>/<c>from</c> to be
    /// meaningful; only the row comparison is confined to the tables <c>verify.tables</c> names.
    /// </summary>
    private static async Task<KeyVerification> VerifyEntryAsync(VerificationReader database, VerifyConfiguration verify, CacheKey key,
                                                                CacheEntry entry, IReadOnlyList<string> tables, Func<double> now,
                                                                string[]? sensitive, string? refutingTable,
                                                                CancellationToken cancellationToken)
    {
        var (document, length) = FindingVerifier.ReadPayload(entry.Payload);
        using (document)
        {
            var comparisons = new List<TableRowComparison>();
            var ages = new List<TableAge>();
            var entryAge = FindingVerifier.EntryAge(key, entry);
            var elapsed = now() - entry.ObservedSeconds;

            // A key the cache reader could not read carries its own code, and it has to reach the answer:
            // without it the run reported the reason and still called itself whole, so a sample where a key
            // had vanished was indistinguishable from one where every key was read.
            var code = entry.FailureCode;
            try
            {
                foreach (var name in tables)
                {
                    var (schema, table) = Split(name);
                    var lastWrite = await database.ReadLastWriteAsync(schema, table, cancellationToken).ConfigureAwait(false);

                    // Taken after the query returns, not before it: the number the server reports was true
                    // when it computed it, and the query's own duration is part of the gap. Reading the
                    // clock first credited the entry with a reading it could not have had yet, and a slow
                    // DMV query could carry a pair of readings past the clock margin without saying so.
                    elapsed = now() - entry.ObservedSeconds;
                    ages.Add(Age(name, lastWrite, elapsed, verify.ClockMarginSeconds));
                    if (document is null)
                        continue;

                    // A table verify.tables does not name is a comparison that did not happen, and R7 says
                    // a step that was skipped is recorded with its reason rather than passed over. Without
                    // this the key came back carrying only the age signal's reason, which says something
                    // else entirely. The lookup ignores case because a table name comes out of the code
                    // and the configuration is written by hand.
                    if (!Declared(verify).TryGetValue(name, out var declared))
                    {
                        // Its columns are read even so. Which names two dependent tables share is a fact
                        // about the tables, and the workspace not saying how to look up this one's row is
                        // not evidence that it lacks the column — the value may have been built partly from
                        // here. Reporting no columns made a name it shares look like the declared table's
                        // alone, and that table's agreement could then refute the finding. Only the
                        // catalogue is read; the rows of an undeclared table stay unread.
                        comparisons.Add(new TableRowComparison(name,
                            RowComparison.NotCompared($"'{name}' is not declared in verify.tables with both 'key' and 'from'",
                                                      await database.MatchedColumnsAsync(schema, table, document.RootElement,
                                                                                         cancellationToken)
                                                                    .ConfigureAwait(false))));
                        continue;
                    }

                    comparisons.Add(new TableRowComparison(name,
                        await database.CompareRowAsync(schema, table, declared, key.Template,
                                                       entry.Key.Values, document.RootElement, cancellationToken)
                                      .ConfigureAwait(false)));
                }
            }
            catch (Exception error) when (FindingVerifier.Classify(error) is { } classified)
            {
                code = classified;
            }

            // A refused idle time is recorded beside whatever else the key has to say. It is a reason and
            // not a code on purpose: the entry was read, its payload and TTL are in hand, and one optional
            // observation being unavailable does not make the run partial.
            var reported = entry.IdleReason is null || entry.Reason is null
                               ? entry.Reason ?? entry.IdleReason
                               : $"{entry.Reason}; {entry.IdleReason}";
            return FindingVerifier.VerifyKey(entry.Key, comparisons, FindingVerifier.Aggregate(entryAge, ages),
                                             sensitive, length, code, reported, ages, elapsed,
                                             verify.ClockMarginSeconds, refutingTable);
        }
    }

    /// <summary>
    /// One table's last write, brought onto the cache reading's timeline. The three ways that can turn out
    /// are kept apart: the clock margin is announced only when it was actually exceeded — it used to be
    /// announced for a write that landed between the two readings as well, which is a signal rather than a
    /// gap, so a run forty seconds apart reported a sixty-second margin as broken.
    /// </summary>
    private static TableAge Age(string name, LastWrite lastWrite, double elapsed, double margin)
    {
        if (lastWrite.SecondsSinceLastWrite is not { } seconds)
            return new TableAge(name, null, lastWrite.Reason);

        var observed = FindingVerifier.AtObservation(seconds, elapsed, margin);
        return observed.Placement switch
        {
            FindingVerifier.Placement.BeyondMargin =>
                new TableAge(name, null,
                             lastWrite.Reason ?? $"the database was read {elapsed:F1}s after the cache, beyond the " +
                                                 $"{margin:F0}s clock margin, so the two readings cannot be placed on one timeline"),
            FindingVerifier.Placement.WrittenAfterObservation =>
                new TableAge(name, null, lastWrite.Reason ?? FindingVerifier.WrittenAfterObservationReason,
                             WrittenAfterObservation: true),
            _ => new TableAge(name, observed.SecondsAgo, lastWrite.Reason)
        };
    }

    private static (string Schema, string Table) Split(string qualified)
    {
        var separator = qualified.IndexOf('.', StringComparison.Ordinal);
        return separator < 0 ? ("dbo", qualified) : (qualified[..separator], qualified[(separator + 1)..]);
    }

    private static VerificationRun Refused(string reason) => new(Empty(reason), []);

    private static FindingVerification Empty(string? reason) =>
        new(VerificationOutcome.NotVerifiable, reason, [], 0, 0, 0, false, false);
}
