using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CacheDetective.Serialization;
using CacheDetective.Verification;

namespace CacheDetective.Mcp;

/// <summary>One field of one key: its name, whether it differs, and nothing else. A sensitive name is
/// marked so that a reader knows nothing further will ever be said about it.</summary>
internal sealed record VerificationFieldItem(string Field, bool Differs, bool Redacted, string? Reason);

/// <summary>How long ago one dependent table was written, or why that is unknown. It carries no row data:
/// the number comes from the index usage statistics, which hold none.</summary>
internal sealed record VerificationTableItem(string Table, double? SecondsSinceLastWrite, string? Reason);

/// <summary>
/// One key of the sample. <paramref name="Template"/> and <paramref name="KeyHash"/> are the record's
/// identity and survive every reduction; the collections below them are what gets cut when the response
/// would otherwise not fit.
/// <para>The numbers — the entry's age, each table's, and the gap the two readings were placed on one
/// timeline across — are what a report quotes. None of them is a key or a value.</para>
/// </summary>
internal sealed record VerificationKeyItem(string Template, string KeyHash, string Observation, bool Prefixed,
                                           int? PayloadLength, string? Reason, string? FailureCode,
                                           IReadOnlyList<VerificationFieldItem> Fields, int FieldsHidden,
                                           IReadOnlyList<string> Tables, int TablesHidden,
                                           double? EntryAgeSeconds, IReadOnlyList<VerificationTableItem> TableAges,
                                           double? ElapsedSeconds, double? ClockMarginSeconds);

/// <param name="Basis">What a <c>possible</c> rests on — <c>field_difference</c>, <c>age</c>, or
/// <c>both</c> — and absent for every other observation. A reader acts on the two differently: a field
/// that differs is a disagreement it can go and look at, an age signal only says a write landed where it
/// might matter.</param>
internal sealed record VerifyFindingResult(string FindingId, string Observation, string? Reason,
                                           int Refuted, int Possible, int NotVerifiable,
                                           bool MatchesDiscarded, bool Partial,
                                           ListEnvelope<VerificationKeyItem> Keys, string? Basis = null);

/// <summary>What one run of verification produced, with the tables it compared against.</summary>
internal sealed record VerificationRun(FindingVerification Verification, IReadOnlyList<string> Tables);

internal static class VerificationQueries
{
    /// <summary>What one record may weigh. The reserve leaves room for the envelope and the result around
    /// it, so an item that fits here fits in the response it will be carried in.</summary>
    private const int ITEM_BUDGET = ResponseEnvelope.MaximumSerializedBytes - 1536;

    /// <summary>How much of a template survives when even a record with no collections is too large. The
    /// hash is what keeps such a record distinguishable, and it is never cut.</summary>
    private const int TEMPLATE_LIMIT = 256;

    private const string ReducedReason = "this record was reduced to stay under the response limit";

    /// <summary>What the finding's own reason may weigh. It is the one field of the shell that has no
    /// bound of its own, and an unbounded one there is what used to push a whole result over the limit
    /// after every record had already been made to fit.</summary>
    private const int REASON_LIMIT = 1024;

    /// <summary>
    /// The records of one run, decided once for the snapshot and never per page. The reduction used to be
    /// chosen by measuring the result built <em>for the requested page</em>, so one page could be answered
    /// from full records and another from reduced ones — two pages of one snapshot that overlapped and
    /// disagreed about how many pages there were.
    /// </summary>
    internal static IReadOnlyList<VerificationKeyItem> Records(string findingId, VerificationRun run)
    {
        var items = run.Verification.Keys.Select(key => Fit(Describe(key, run.Tables))).ToArray();

        // The worst case any page can present: the shell around an envelope carrying the largest single
        // record. It does not depend on which page was asked for, so neither does the answer.
        var largest = items.Length == 0 ? 0 : items.Max(item => Size(item, CacheDetectiveJsonContext.Default.VerificationKeyItem));
        return Shell(findingId, run) + largest <= ResponseEnvelope.MaximumSerializedBytes
                   ? items
                   : items.Select(Identity).ToArray();
    }

    /// <summary>
    /// The page, with the shell around it reserved. The envelope fills itself to the whole limit unless it
    /// is told what wraps it, so a page of several middling records fitted the envelope and overflowed the
    /// result; the single-record check above only ever caught the case of one enormous record per page.
    /// </summary>
    internal static VerifyFindingResult Present(string findingId, VerificationRun run, PageArguments? page) =>
        Build(findingId, run, ResponseEnvelope.Create(Records(findingId, run), page,
                                                      CacheDetectiveJsonContext.Default.ListEnvelopeVerificationKeyItem,
                                                      Shell(findingId, run)));

    /// <summary>What the result weighs around an empty envelope, which is what a page must leave room
    /// for.</summary>
    private static int Shell(string findingId, VerificationRun run) =>
        Size(Build(findingId, run, ResponseEnvelope.Create(Array.Empty<VerificationKeyItem>(), null,
                                                           CacheDetectiveJsonContext.Default.ListEnvelopeVerificationKeyItem)),
             CacheDetectiveJsonContext.Default.VerifyFindingResult);

    private static VerifyFindingResult Build(string findingId, VerificationRun run, ListEnvelope<VerificationKeyItem> keys)
    {
        var verification = run.Verification;
        return new VerifyFindingResult(findingId, Name(verification.Outcome), Shorten(verification.Reason),
                                       verification.Refuted, verification.Possible, verification.NotVerifiable,
                                       verification.MatchesDiscarded, verification.Partial, keys, verification.Basis);
    }

    private static string? Shorten(string? reason) =>
        reason is null || reason.Length <= REASON_LIMIT ? reason : string.Concat(reason.AsSpan(0, REASON_LIMIT), "…");

    internal static string Name(VerificationOutcome outcome) => outcome switch
    {
        VerificationOutcome.Refuted => "refuted",
        VerificationOutcome.Possible => "possible",
        _ => "not_verifiable"
    };

    private static VerificationKeyItem Describe(KeyVerification key, IReadOnlyList<string> tables) =>
        new(key.Template, key.KeyHash, Name(key.Outcome), key.Prefixed, key.PayloadLength, key.Reason, key.FailureCode,
            key.Fields.Select(field => new VerificationFieldItem(field.Field, field.Differs, field.Redacted, field.Reason)).ToArray(),
            0, tables, 0, key.EntryAgeSeconds,
            (key.TableAges ?? []).Select(age => new VerificationTableItem(age.Table, age.SecondsSinceLastWrite, age.Reason)).ToArray(),
            key.ElapsedSeconds, key.ClockMarginSeconds);

    /// <summary>
    /// Cuts a record down until it fits, in the order that loses the least: the fields first, then the
    /// dependent tables, each saying how many were hidden. The template and the hash are not cut, because
    /// a record nobody can tell apart from another is worth nothing at all.
    /// </summary>
    private static VerificationKeyItem Fit(VerificationKeyItem item)
    {
        if (Fits(item))
        {
            return item;
        }

        for (var kept = item.Fields.Count - 1; kept >= 0; kept--)
        {
            var candidate = item with { Fields = item.Fields.Take(kept).ToArray(), FieldsHidden = item.Fields.Count - kept };
            if (Fits(candidate))
            {
                return candidate;
            }
        }

        var withoutFields = item with { Fields = [], FieldsHidden = item.Fields.Count };
        for (var kept = item.Tables.Count - 1; kept >= 0; kept--)
        {
            var candidate = withoutFields with { Tables = item.Tables.Take(kept).ToArray(), TablesHidden = item.Tables.Count - kept };
            if (Fits(candidate))
            {
                return candidate;
            }
        }

        // The per-table ages are the last collection to go, after the fields and the table names.
        var withoutTables = withoutFields with { Tables = [], TablesHidden = item.Tables.Count };
        for (var kept = item.TableAges.Count - 1; kept >= 0; kept--)
        {
            var candidate = withoutTables with { TableAges = item.TableAges.Take(kept).ToArray() };
            if (Fits(candidate))
            {
                return candidate;
            }
        }

        return Identity(item);
    }

    /// <summary>The record with nothing but what tells it apart, and the reason it says nothing else.</summary>
    private static VerificationKeyItem Identity(VerificationKeyItem item)
    {
        var identity = item with
        {
            PayloadLength = null,
            Reason = ReducedReason,
            Fields = [],
            FieldsHidden = item.Fields.Count + item.FieldsHidden,
            Tables = [],
            TablesHidden = item.Tables.Count + item.TablesHidden,
            TableAges = []
        };

        // A template long enough to break the budget on its own is shortened; the hash still tells the
        // record apart from every other one.
        return Fits(identity) || item.Template.Length <= TEMPLATE_LIMIT
                   ? identity
                   : identity with { Template = string.Concat(item.Template.AsSpan(0, TEMPLATE_LIMIT), "…") };
    }

    private static bool Fits(VerificationKeyItem item) =>
        Size(item, CacheDetectiveJsonContext.Default.VerificationKeyItem) <= ITEM_BUDGET;

    private static int Size<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(value, typeInfo).Length;
}
