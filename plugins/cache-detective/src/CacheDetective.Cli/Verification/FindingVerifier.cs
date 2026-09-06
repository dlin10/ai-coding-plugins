using System.Data.Common;
using System.Text.Json;
using CacheDetective.Configuration;
using CacheDetective.Graph;
using StackExchange.Redis;

namespace CacheDetective.Verification;

/// <summary>The three things verification can say. There is no fourth, and none of them changes whether
/// a finding is reported or how confident it is; see <c>docs/adr/0012</c>.</summary>
internal enum VerificationOutcome
{
    Refuted,
    Possible,
    NotVerifiable
}

/// <summary>
/// Whether a finding can be verified at all, and — separately — whether what is verified is allowed to
/// refute it. The two are different questions and were once one, which threw a finding away before any
/// reading was taken in cases where a plain disagreement of fields would have been visible.
/// </summary>
/// <param name="Starts">Whether anything is read at all. False means <c>not_verifiable</c> outright.</param>
/// <param name="RefutationAllowed">Whether field agreement may be treated as a refutation. False means
/// the readings are still taken and still reported, but the strong half is withheld.</param>
internal sealed record Applicability(bool Starts, bool RefutationAllowed, string? Reason, IReadOnlyList<Table> Tables)
{
    internal static Applicability DoesNotStart(string reason) => new(false, false, reason, []);
}

/// <summary>What the age of an entry is worth. Never <see cref="VerificationOutcome.Refuted"/>.</summary>
internal sealed record AgeSignal(double? EntryAgeSeconds, double? TableAgeSeconds, VerificationOutcome Outcome, string? Reason);

/// <summary>How long ago one dependent table was written, brought back to the moment the cache was read,
/// or why that could not be said. One per dependent table, kept apart so that a later table cannot erase
/// what an earlier one established.</summary>
/// <param name="WrittenAfterObservation">Whether the write landed between the cache reading and this one.
/// It carries no number — the write is later than the moment being measured from — and it is a signal in
/// its own right rather than an unknown age.</param>
internal sealed record TableAge(string Table, double? SecondsSinceLastWrite, string? Reason,
                                bool WrittenAfterObservation = false);

/// <summary>The fixed codes a partial failure is reported under. They are codes and not sentences so that
/// a reader can count them across a report; the observations gathered before the failure are kept.</summary>
internal static class VerificationFailure
{
    internal const string DatabaseUnavailable = "database_unavailable";
    internal const string KeyVanished = "key_vanished";
    internal const string WrongType = "wrong_type";
    internal const string ParameterConversion = "parameter_conversion";

    /// <summary>One key of the sample could not be read at all — a timeout, or a server that refused the
    /// command. Told apart from <see cref="WrongType"/>, which is a key that was read and turned out to
    /// hold something this verification does not read, and from <see cref="KeyVanished"/>, which is a key
    /// that was there when the traversal listed it and gone when it was read.</summary>
    internal const string ReadFailed = "read_failed";

    /// <summary>Reasons a whole run never began. They are codes for the same reason the four above are:
    /// so that a reader can count them across a report.</summary>
    internal const string NotConfigured = "verify_not_configured";

    internal const string CacheUnavailable = "cache_unavailable";

    internal const string PermissionDenied = "permission_denied";
}

/// <summary>One row comparison and the table it came from.</summary>
internal sealed record TableRowComparison(string Table, RowComparison Comparison);

/// <summary>
/// What is said about one field, and the most that is ever said about it: its name and whether it
/// differs. The value itself never leaves, and a field whose name matches a sensitive mask is marked so
/// that a reader knows nothing further will be said about it.
/// </summary>
internal sealed record FieldObservation(string Field, FieldVerdict Verdict, bool Redacted, string? Reason)
{
    internal bool Differs => Verdict == FieldVerdict.Different;

    internal bool Comparable => Verdict is FieldVerdict.Equal or FieldVerdict.Different;
}

/// <summary>One key of the sample. <paramref name="PayloadLength"/> is filled only when the value was not
/// a JSON object, because then the length is the only thing that can be said about it.</summary>
/// <param name="EntryAgeSeconds">How long the entry has been there, when that could be worked out.</param>
/// <param name="TableAges">Per dependent table, seconds since its last write or why that is unknown.</param>
/// <param name="ElapsedSeconds">How long after the cache reading the database was asked.</param>
/// <param name="ClockMarginSeconds">The margin those two readings were placed on one timeline under.</param>
/// <param name="Basis">What a <c>possible</c> rests on, and <c>null</c> for every other outcome. The two
/// are different evidence and a reader acts on them differently: a field that plainly differs is a
/// disagreement it can go and look at, while an age signal only says a write landed where it might matter.
/// One sentence covering both said every possible key "disagrees with the rows", which was untrue whenever
/// nothing had been compared at all.</param>
internal sealed record KeyVerification(string Template, string KeyHash, VerificationOutcome Outcome,
                                       IReadOnlyList<FieldObservation> Fields, bool Prefixed, int? PayloadLength,
                                       string? Reason, string? FailureCode, double? EntryAgeSeconds = null,
                                       IReadOnlyList<TableAge>? TableAges = null, double? ElapsedSeconds = null,
                                       double? ClockMarginSeconds = null, string? Basis = null);

/// <summary>The fixed names for what a <c>possible</c> rests on.</summary>
internal static class VerificationBasis
{
    internal const string FieldDifference = "field_difference";
    internal const string Age = "age";
    internal const string Both = "both";
}

/// <summary>
/// The answer for one finding. It carries how many keys landed in each state and whether the sample was
/// trimmed, because a refutation is only ever a statement about the keys that existed for the whole of
/// the traversal.
/// </summary>
/// <param name="Basis">What the finding's own <c>possible</c> rests on, gathered from the keys that carried
/// it: <c>field_difference</c>, <c>age</c>, or <c>both</c> when different keys rest on different evidence.
/// </param>
internal sealed record FindingVerification(VerificationOutcome Outcome, string? Reason, IReadOnlyList<KeyVerification> Keys,
                                           int Refuted, int Possible, int NotVerifiable, bool MatchesDiscarded, bool Partial,
                                           string? Basis = null);

internal static class FindingVerifier
{
    internal const string CacheRole = "cache";

    /// <summary>
    /// The whole assessment in one call, for a sample already in hand. The working path asks the two
    /// halves separately — <see cref="Assess(CacheGraph, CacheKey, VerifyConfiguration, string?)"/> before
    /// it opens any connection, and <see cref="AfterScan"/> once it knows what the sample turned out to
    /// be — because the second half cannot be answered before the keys have been read.
    /// </summary>
    internal static Applicability Assess(CacheGraph graph, CacheKey key, VerifyConfiguration configuration,
                                         string? connectionRefusal, KeyMatch? match)
    {
        var applicability = Assess(graph, key, configuration, connectionRefusal);
        return !applicability.Starts || match is { Values: not null }
                   ? applicability
                   : Withhold(applicability, [match?.Reason ?? AmbiguousMatch]);
    }

    /// <summary>
    /// The gate before any reading, and the only part of the assessment that can be made before a
    /// connection is opened: the role, the store, the refusal a connection string earns on sight, and
    /// whether the key depends on a table at all. Everything here stops verification from beginning; the
    /// rest only limits what a reading is allowed to conclude.
    /// </summary>
    internal static Applicability Assess(CacheGraph graph, CacheKey key, VerifyConfiguration configuration,
                                         string? connectionRefusal)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!string.Equals(key.Role, CacheRole, StringComparison.Ordinal))
        {
            return Applicability.DoesNotStart($"the key's role is '{key.Role ?? "unknown"}' and verification reads cache keys");
        }

        if (!configuration.Verifies(key.Store))
        {
            return Applicability.DoesNotStart($"the store '{key.Store}' is not listed in verify.stores");
        }

        if (connectionRefusal is not null)
        {
            return Applicability.DoesNotStart(connectionRefusal);
        }

        var walk = graph.WalkDependencies(key);
        var tables = walk.Dependencies.Select(dependency => dependency.Target).OfType<Table>().Distinct().ToArray();
        if (tables.Length == 0)
        {
            return Applicability.DoesNotStart("the key depends on no table, so there is no row to compare it with");
        }

        // Everything below leaves the reading in place and takes only the right to refute. What they have
        // in common is that the graph could not see all of what the value was built from, so field
        // agreement over what it could see proves nothing about the rest.
        var withheld = new List<string>();
        if (walk.Dependencies.Any(dependency => dependency.Target is ExternalSource))
        {
            withheld.Add("the key depends on an external source that no serves edge joins to an endpoint");
        }

        if (walk.Unresolved.Count > 0)
        {
            // Named, not just counted. A reader told only that something was unresolved cannot tell which
            // branch of the chain the graph could not see, and the derived gaps — a procedure whose
            // dependencies are unknown, most often — are exactly the ones that do not appear anywhere in
            // the finding's own evidence.
            var named = walk.Unresolved.Select(item => item.Snippet)
                            .Where(snippet => !string.IsNullOrWhiteSpace(snippet))
                            .Distinct(StringComparer.Ordinal)
                            .Take(3)
                            .ToArray();
            withheld.Add($"{walk.Unresolved.Count} unresolved item(s) lie on branches the walk visited" +
                         (named.Length == 0 ? string.Empty : $": {string.Join(", ", named)}"));
        }

        if (walk.DepthLimitReached)
        {
            withheld.Add("the walk stopped at its depth limit and did not see the whole chain");
        }

        if (walk.Dependencies.Any(dependency => dependency.Target is CacheKey))
        {
            withheld.Add("the key depends on another cache key, whose own freshness is not read here");
        }

        return new Applicability(true, withheld.Count == 0, withheld.Count == 0 ? null : string.Join("; ", withheld), tables);
    }

    private const string AmbiguousMatch = "the key could not be matched to the template in exactly one way";

    /// <summary>
    /// What the sample turned out to be. Only a key the reader could assign to the template in exactly one
    /// way carries placeholder values that can be trusted, and that is not knowable until the keys have
    /// been read — asking it beforehand, with no sample to look at, withheld refutation from every run and
    /// put <c>refuted</c> out of reach entirely.
    /// <para>The traversal's own completeness is not repeated here: <see cref="Verify"/> reads it off the
    /// scan result directly.</para>
    /// </summary>
    internal static Applicability AfterScan(Applicability applicability, CacheScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(applicability);
        ArgumentNullException.ThrowIfNull(scan);
        if (!applicability.Starts)
        {
            return applicability;
        }

        var ambiguous = scan.Entries.Where(entry => entry.Key.Values is null)
                            .Select(entry => entry.Key.Reason ?? AmbiguousMatch)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
        return ambiguous.Length == 0 ? applicability : Withhold(applicability, ambiguous);
    }

    private static Applicability Withhold(Applicability applicability, IReadOnlyList<string> reasons) =>
        applicability with
        {
            RefutationAllowed = false,
            Reason = string.Join("; ", (applicability.Reason is null ? [] : new[] { applicability.Reason }).Concat(reasons))
        };

    /// <summary>
    /// How long the entry has been there: the declared TTL less what is left of it. It needs a remaining
    /// TTL to subtract from, and it needs the sites to have agreed on the declared one — a key two
    /// writers set with different expiries has no single declared TTL to reason from.
    /// <para><c>OBJECT IDLETIME</c> takes no part in this. Idle time is time since the last <em>read</em>,
    /// which a single reader resets and says nothing about when the value was written.</para>
    /// </summary>
    internal static double? EntryAge(double? declaredTtlSeconds, double? remainingTtlSeconds, bool ttlAgreed)
    {
        if (!ttlAgreed || declaredTtlSeconds is not { } declared || remainingTtlSeconds is not { } remaining)
        {
            return null;
        }

        var age = declared - remaining;
        return age >= 0 ? age : null;
    }

    internal static double? EntryAge(CacheKey key, CacheEntry entry) =>
        EntryAge(key.TtlSeconds, entry.TtlSeconds, key.TtlAgreed);

    /// <summary>What bringing a database reading back to the moment of the cache reading produced.</summary>
    internal enum Placement
    {
        /// <summary>The two readings are on one timeline and <c>SecondsAgo</c> holds the reduced age.</summary>
        Placed,

        /// <summary>The reduction came out negative, which is not missing data: the write happened
        /// <em>after</em> the cache was observed, so it lies between the two readings.</summary>
        WrittenAfterObservation,

        /// <summary>The two readings are too far apart to be placed on one timeline at all.</summary>
        BeyondMargin
    }

    internal readonly record struct Observed(double? SecondsAgo, Placement Placement);

    /// <summary>
    /// Brings a later reading back to the moment of the first one by <em>subtraction</em>: a database
    /// asked forty seconds after the cache was read, answering "95 seconds ago", was saying "55 seconds
    /// ago" at the moment that mattered. Beyond the configured margin the two readings are too far apart
    /// to be placed on one timeline and the age signal is dropped rather than fudged.
    /// <para>A negative result is a third thing, and used to be folded into the second. A database asked
    /// forty seconds later answering "five seconds ago" is not saying nothing — it is saying the table was
    /// written <em>after</em> the cache entry was observed, thirty-five seconds after in fact, which is a
    /// signal of its own that the entry may be stale. Reporting that as an unknown age lost the signal and,
    /// worse, made the caller announce a clock margin that had not been exceeded at all.</para>
    /// </summary>
    internal static Observed AtObservation(double reportedSecondsAgo, double secondsAfterFirstRead, double clockMarginSeconds)
    {
        if (Math.Abs(secondsAfterFirstRead) > clockMarginSeconds)
        {
            return new Observed(null, Placement.BeyondMargin);
        }

        var reduced = reportedSecondsAgo - secondsAfterFirstRead;
        return reduced >= 0
                   ? new Observed(reduced, Placement.Placed)
                   : new Observed(null, Placement.WrittenAfterObservation);
    }

    /// <summary>
    /// What the two ages together are worth, which is never a refutation.
    /// <para>A table written more recently than the entry is real evidence that staleness is
    /// <em>possible</em>. The other direction proves nothing at all: <c>EXPIRE</c> may have extended the
    /// entry's deadline long after it was written, so a young-looking entry may be an hour old; and a
    /// handler may read a row, wait while the table changes, and only then write the value it already
    /// had, so an entry written after the table can still hold what the table no longer says. Neither gap
    /// is bounded, so no clock margin closes them.</para>
    /// </summary>
    internal static AgeSignal Signal(double? entryAgeSeconds, double? tableAgeSeconds)
    {
        if (entryAgeSeconds is not { } entryAge || tableAgeSeconds is not { } tableAge)
        {
            return new AgeSignal(entryAgeSeconds, tableAgeSeconds, VerificationOutcome.NotVerifiable,
                                 "the entry's age or the table's last write is unknown");
        }

        return tableAge < entryAge
                   ? new AgeSignal(entryAge, tableAge, VerificationOutcome.Possible,
                                   "the table was written after this entry was, so the entry may be stale")
                   : new AgeSignal(entryAge, tableAge, VerificationOutcome.NotVerifiable,
                                   "the table was written before this entry was, which does not settle anything: the entry's " +
                                   "deadline may have been extended after it was written, and a handler may write a value it " +
                                   "read before the table changed");
    }

    /// <summary>
    /// The signal over every dependent table at once. The one that matters is the table written most
    /// recently — that is, the smallest number of seconds since a write — because <em>any</em> table
    /// younger than the entry makes staleness possible. Folding the tables one at a time into a single
    /// running signal let the last table read erase what an earlier one had established, so a
    /// <c>possible</c> from the first dependency vanished if the last was older or unknown.
    /// </summary>
    internal const string WrittenAfterObservationReason =
        "the table was written after the cache entry was observed, so the entry may be stale";

    internal static AgeSignal Aggregate(double? entryAgeSeconds, IReadOnlyList<TableAge> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        // A write that landed between the two readings settles it on its own, and needs no entry age to do
        // it: whatever was in the cache when it was read, the table has changed since. It is checked first
        // because it does not depend on the entry's TTL being knowable, which the comparison below does.
        if (tables.Any(table => table.WrittenAfterObservation))
            return new AgeSignal(entryAgeSeconds, null, VerificationOutcome.Possible, WrittenAfterObservationReason);

        var youngest = tables.Select(table => table.SecondsSinceLastWrite).OfType<double>().ToArray();
        return Signal(entryAgeSeconds, youngest.Length == 0 ? null : youngest.Min());
    }

    /// <summary>Reads a cached value as JSON, or gives up and reports only how long it was. A value that
    /// is not an object has no fields to compare, and its content is not something to guess at.</summary>
    internal static (JsonDocument? Document, int? Length) ReadPayload(string? payload)
    {
        if (payload is null)
        {
            return (null, null);
        }

        try
        {
            var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return (document, null);
            }

            document.Dispose();
        }
        catch (JsonException)
        {
            // Not JSON at all. The length is the only fact about it that is safe to carry out.
        }

        return (null, payload.Length);
    }

    /// <summary>
    /// The observations for one key, gathered across every dependent table the workspace named. A field
    /// name that is a column of two of those tables is not compared at all: which table's row it should
    /// have been read from is undecided, and picking one would be a guess dressed as a measurement.
    /// <para>Ambiguity is read off the tables' <em>columns</em> and not off the comparisons that came back.
    /// A table that matched a column and then found no row produces no field of its own, and deciding from
    /// the comparisons alone made the same name look like the sole property of the one table that did
    /// answer — so a field two tables share could be refuted against one of them.</para>
    /// </summary>
    internal static IReadOnlyList<FieldObservation> Observe(IReadOnlyList<TableRowComparison> tables,
                                                            IEnumerable<string>? sensitiveMasks)
    {
        var masks = sensitiveMasks?.ToArray();
        var shared = tables.SelectMany(table => (table.Comparison.MatchedColumns ?? [])
                                          .Select(column => (table.Table, Column: column)))
                           .GroupBy(entry => entry.Column, StringComparer.Ordinal)
                           .Where(group => group.Select(entry => entry.Table).Distinct(StringComparer.Ordinal).Count() > 1)
                           .Select(group => group.Key)
                           .ToHashSet(StringComparer.Ordinal);
        return tables.SelectMany(table => table.Comparison.Fields.Select(field => (table.Table, Field: field)))
                     .GroupBy(entry => entry.Field.Field, StringComparer.Ordinal)
                     .Select(group =>
                     {
                         var redacted = SensitiveFields.IsSensitive(group.Key, masks);
                         if (shared.Contains(group.Key) ||
                             group.Select(entry => entry.Table).Distinct(StringComparer.Ordinal).Count() > 1)
                         {
                             return new FieldObservation(group.Key, FieldVerdict.NotCompared, redacted,
                                                         "the field name is a column of more than one dependent table, so which " +
                                                         "row it came from is undecided");
                         }

                         var comparison = group.First().Field;
                         return new FieldObservation(group.Key, comparison.Verdict, redacted, comparison.Reason);
                     })
                     .OrderBy(observation => observation.Field, StringComparer.Ordinal)
                     .ToArray();
    }

    /// <summary>
    /// One key's verdict, in order: a comparable field that differs makes staleness <em>possible</em>;
    /// otherwise every comparable field agreeing <em>refutes</em>, and that direct comparison outranks
    /// whatever the age said; otherwise the age's own signal is all there is; otherwise nothing can be
    /// said.
    /// </summary>
    /// <param name="refutingTable">The one dependent table this finding is about, when it names one. Only
    /// agreement with <em>that</em> table's row can refute it: a finding about a write to T2 says nothing
    /// about T1, and letting the fields of T1 agree their way to a refutation would refute a claim through
    /// a dependency it was never made about. The other tables are still read and still reported; their
    /// agreement simply is not evidence here.</param>
    internal static KeyVerification VerifyKey(KeyMatch match, IReadOnlyList<TableRowComparison> tables, AgeSignal age,
                                              IEnumerable<string>? sensitiveMasks, int? payloadLength = null,
                                              string? failureCode = null, string? failureReason = null,
                                              IReadOnlyList<TableAge>? tableAges = null, double? elapsedSeconds = null,
                                              double? clockMarginSeconds = null, string? refutingTable = null)
    {
        var fields = Observe(tables, sensitiveMasks);
        var tableReasons = tables.Select(table => table.Comparison.Reason).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();

        // A table that was not compared is a step that did not happen, and R7 asks for it to be recorded
        // whatever the outcome turned out to be. Reporting it only in the branch that had nothing else to
        // say meant a refutation earned on the declared tables silently swallowed the fact that another
        // dependent table was never looked at.
        var skipped = tableReasons.Length == 0 ? null : string.Join("; ", tableReasons);
        KeyVerification Key(VerificationOutcome outcome, string? reason, string? basis = null) =>
            new(match.Template, match.KeyHash, outcome, fields, match.Prefixed, payloadLength,
                Join(Join(reason, skipped), failureReason ?? failureCode), failureCode, age.EntryAgeSeconds, tableAges,
                elapsedSeconds, clockMarginSeconds, basis);

        // The age of the table this finding is about, which is not the age over every table it depends on.
        // A finding about T2 is not answered by T1's clock any more than it is by T1's fields.
        var targetAge = refutingTable is null || tableAges is null
                            ? age
                            : Aggregate(age.EntryAgeSeconds,
                                        tableAges.Where(table => string.Equals(table.Table, refutingTable,
                                                                               StringComparison.OrdinalIgnoreCase)).ToArray());
        var agePossible = targetAge.Outcome == VerificationOutcome.Possible;

        // A difference is evidence a partial failure cannot take away: if the first table showed a field
        // that plainly differs and reading the second one failed, the finding is still possible and the
        // failure is reported alongside it rather than in place of it.
        var comparable = fields.Where(field => field.Comparable).ToArray();
        var differing = comparable.Where(field => field.Differs).ToArray();
        if (differing.Length > 0)
        {
            var named = string.Join(", ", differing.Select(field => field.Field));
            return agePossible
                       ? Key(VerificationOutcome.Possible,
                             $"the cached value's field(s) {named} differ from the row they were built from, and " +
                             targetAge.Reason, VerificationBasis.Both)
                       : Key(VerificationOutcome.Possible,
                             $"the cached value's field(s) {named} differ from the row they were built from",
                             VerificationBasis.FieldDifference);
        }

        if (failureCode is not null)
        {
            // What was already observed is kept: a failure halfway through is still a reading of what came
            // before it, and throwing that away would lose the only evidence the run produced. Agreement,
            // unlike difference, does not survive it — the tables left unread might have disagreed.
            return Key(VerificationOutcome.NotVerifiable, null);
        }

        // The target table's own clock comes before any verdict drawn from fields. Another dependency's
        // fields agreeing is not evidence about this finding — it cannot refute it, and it must not be
        // allowed to withhold a signal the target table did give. Putting the "agreement, but not from the
        // finding's table" branch first meant an unrelated table's agreement silenced the target's own age.
        if (agePossible)
        {
            return Key(VerificationOutcome.Possible, targetAge.Reason, VerificationBasis.Age);
        }

        // Only the fields that came from the table the finding is about may carry a refutation, and only
        // when that table's own age said nothing — which the branch above has already established.
        var refuting = refutingTable is null
                           ? comparable
                           : comparable.Where(field => tables.Any(table => Names(table, refutingTable) &&
                                                                           table.Comparison.Fields.Any(compared =>
                                                                               string.Equals(compared.Field, field.Field,
                                                                                             StringComparison.Ordinal))))
                                       .ToArray();
        if (refuting.Length > 0)
        {
            return Key(VerificationOutcome.Refuted,
                       $"all {refuting.Length} comparable field(s) agree with the row as it stands now");
        }

        if (comparable.Length > 0)
        {
            return Key(VerificationOutcome.NotVerifiable,
                       $"{comparable.Length} comparable field(s) agree, but none of them was read from " +
                       $"'{refutingTable}', which is the table this finding is about");
        }

        // The skipped-step reasons are already appended by Key, so this branch supplies only what is left:
        // the age's account, or the plain statement that nothing could be compared.
        return Key(VerificationOutcome.NotVerifiable,
                   skipped is not null ? null : targetAge.Reason ?? "no field of the cached value could be compared with its row");
    }

    /// <summary>Table names reach this from the code on one side and the finding catalogue on the other,
    /// so they are compared the way the rest of the verification compares them: without regard to case.
    /// </summary>
    private static bool Names(TableRowComparison comparison, string table) =>
        string.Equals(comparison.Table, table, StringComparison.OrdinalIgnoreCase);

    private static string? Join(string? reason, string? failure) =>
        reason is null ? failure : failure is null ? reason : $"{reason}; {failure}";

    /// <summary>
    /// The finding's verdict, in order. A single possible key carries the whole finding, whatever the
    /// sample was worth and whatever the walk was not allowed to conclude — a field that plainly differs
    /// is evidence no incompleteness can take away. A refutation is the opposite: it needs the sample to
    /// be everything there was, and everything there was to agree.
    /// </summary>
    internal static FindingVerification Verify(IReadOnlyList<KeyVerification> keys, CacheScanResult scan,
                                               Applicability applicability)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(applicability);

        var possible = keys.Count(key => key.Outcome == VerificationOutcome.Possible);
        var refuted = keys.Count(key => key.Outcome == VerificationOutcome.Refuted);
        var notVerifiable = keys.Count(key => key.Outcome == VerificationOutcome.NotVerifiable);
        var partial = keys.Any(key => key.FailureCode is not null) || scan.InterruptedReason is not null;
        FindingVerification Answer(VerificationOutcome outcome, string? reason, string? basis = null) =>
            new(outcome, reason, keys, refuted, possible, notVerifiable, scan.MatchesDiscarded, partial, basis);

        if (possible > 0)
        {
            // What the answer rests on travels with it. Saying that every possible key "disagrees with the
            // rows" was false whenever nothing had been compared and the signal came from a table's clock
            // alone — a reader chasing a field difference that was never observed.
            var bases = keys.Where(key => key.Outcome == VerificationOutcome.Possible)
                            .Select(key => key.Basis)
                            .OfType<string>()
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
            var basis = bases.Length == 1 ? bases[0] : bases.Length == 0 ? null : VerificationBasis.Both;
            return Answer(VerificationOutcome.Possible,
                          $"{possible} of {keys.Count} sampled key(s) may be stale: " + basis switch
                          {
                              VerificationBasis.FieldDifference => "a field of the cached value differs from the row it was built from",
                              VerificationBasis.Age => "the table was written where it may matter, though no field was compared",
                              _ => "a field differs from its row, or the table was written where it may matter"
                          },
                          basis);
        }

        var withheld = new List<string>();
        if (scan.InterruptedReason is { } interrupted)
        {
            withheld.Add(interrupted);
        }

        if (!scan.ScanExhausted)
        {
            withheld.Add("the traversal did not reach the end of the keyspace");
        }

        if (scan.MatchesDiscarded)
        {
            withheld.Add("the sample was trimmed to the match limit, so keys were left unread");
        }

        if (!applicability.RefutationAllowed)
        {
            withheld.Add(applicability.Reason ?? "the walk could not see the whole of what the key was built from");
        }

        if (keys.Count == 0)
        {
            withheld.Add("no key of this template was found to read");
        }

        if (keys.Any(key => key.Prefixed))
        {
            withheld.Add("a sampled key carries a prefix the workspace did not declare, so it may belong to another application");
        }

        if (withheld.Count == 0 && refuted == keys.Count)
        {
            return Answer(VerificationOutcome.Refuted,
                          $"every one of the {refuted} key(s) that existed throughout the traversal agrees with its row");
        }

        if (refuted == keys.Count && keys.Count > 0)
        {
            // The distinction worth reporting: the readings all agreed and something else is why that is
            // not a refutation.
            return Answer(VerificationOutcome.NotVerifiable,
                          $"all {refuted} sampled key(s) agree with their rows, but this is not a refutation because " +
                          string.Join("; ", withheld));
        }

        return Answer(VerificationOutcome.NotVerifiable,
                      withheld.Count > 0 ? string.Join("; ", withheld) : "no sampled key could be compared with its row");
    }

    /// <summary>
    /// The fixed code a failure is reported under, or <c>null</c> when it is not one this verification
    /// knows how to survive. The provider's own errors are told apart rather than swept into one code: a
    /// refused permission is a thing the operator can grant, and a parameter the server would not convert
    /// is a thing the configuration can fix, and neither is the database being unavailable.
    /// </summary>
    internal static string? Classify(Exception error) => error switch
    {
        DbException database when VerificationReader.IsPermissionDenied(database) => VerificationFailure.PermissionDenied,
        DbException database when VerificationReader.IsParameterOrNameError(database) => VerificationFailure.ParameterConversion,
        DbException => VerificationFailure.DatabaseUnavailable,
        RedisServerException server when server.Message.Contains("WRONGTYPE", StringComparison.OrdinalIgnoreCase) =>
            VerificationFailure.WrongType,
        RedisConnectionException or RedisTimeoutException => VerificationFailure.CacheUnavailable,
        InvalidCastException or FormatException or OverflowException => VerificationFailure.ParameterConversion,
        _ => null
    };
}
