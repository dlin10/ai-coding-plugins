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

    private const string REDUCED_REASON = "this record was reduced to stay under the response limit";

    /// <summary>
    /// What the shell may weigh with the reason in it and an empty envelope beside it. The rest of the
    /// limit is what a page of records has to live in, so a reason can never leave a page with no room for
    /// a record at all.
    /// <para>The reason used to be bounded at 1024 <em>characters</em>, which is not a bound on anything
    /// that matters: 1024 characters of Cyrillic escape to roughly 6 KB of JSON, so the shell alone could
    /// take three quarters of the response and the envelope would answer with empty pages and a notice.
    /// </para>
    /// </summary>
    private const int MAXIMUM_REASONED_SHELL_BYTES = ResponseEnvelope.MaximumSerializedBytes / 2;

    private const string REASON_TRUNCATION_MARKER = "…";

    /// <summary>
    /// The records of one run, decided once for the snapshot and never per page. The reduction used to be
    /// chosen by measuring the result built <em>for the requested page</em>, so one page could be answered
    /// from full records and another from reduced ones — two pages of one snapshot that overlapped and
    /// disagreed about how many pages there were.
    /// </summary>
    private static IReadOnlyList<VerificationKeyItem> Records(string findingId, VerificationRun run)
    {
        var items = run.Verification.Keys.Select(key => Fit(Describe(key, run.Tables))).ToArray();

        // The worst case any page can present: the shell around an envelope carrying the largest single
        // record. It does not depend on which page was asked for, so neither does the answer.
        //
        // The envelope's own bytes are part of it. Present hands the shell to ResponseEnvelope as the
        // reserve, and what has to fit in what is left is the envelope — counts, notice and all — not the
        // record alone. Leaving that out let a record just under the remainder pass here and then fail to
        // fit even one to a page, so Partition gave up and every page came back empty with a notice.
        var shell = Shell(findingId, run);
        var largest = items.Length == 0 ? 0 : items.Max(item => Size(item, CacheDetectiveJsonContext.Default.VerificationKeyItem));
        if (shell + ENVELOPE_OVERHEAD + largest <= ResponseEnvelope.MaximumSerializedBytes)
            return items;

        // Falling back to identities is not on its own enough: an identity was only ever measured against a
        // budget of its own, and a shell that is nearly the whole limit leaves less room than that budget
        // assumed. What is left after this particular shell is what each identity has to fit, or the
        // envelope answers with an empty page and the observations reach nobody.
        var remaining = ResponseEnvelope.MaximumSerializedBytes - shell - ENVELOPE_OVERHEAD;
        return items.Select(item => Identity(item, remaining)).ToArray();
    }

    /// <summary>What an envelope carrying one record costs around it, measured at its worst — the counts
    /// and the notice <see cref="ResponseEnvelope"/> writes when it has had to reduce a page.</summary>
    private static readonly int ENVELOPE_OVERHEAD =
        Size(new ListEnvelope<VerificationKeyItem>(int.MaxValue, int.MaxValue, int.MaxValue, [],
                                                   "Page size was reduced to stay under the response limit."),
             CacheDetectiveJsonContext.Default.ListEnvelopeVerificationKeyItem);

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
        Size(Build(findingId, run, EmptyEnvelope()), CacheDetectiveJsonContext.Default.VerifyFindingResult);

    private static VerifyFindingResult Build(string findingId, VerificationRun run, ListEnvelope<VerificationKeyItem> keys) =>
        Build(findingId, run, keys, FittedReason(findingId, run));

    private static VerifyFindingResult Build(string findingId, VerificationRun run, ListEnvelope<VerificationKeyItem> keys,
                                             string? reason)
    {
        var verification = run.Verification;
        return new VerifyFindingResult(findingId, Name(verification.Outcome), reason,
                                       verification.Refuted, verification.Possible, verification.NotVerifiable,
                                       verification.MatchesDiscarded, verification.Partial, keys, verification.Basis);
    }

    /// <summary>The reason, cut until the shell carrying it and an empty envelope come in under half the
    /// response limit — measured by serializing that shell, not by counting the reason's characters.
    /// </summary>
    private static string? FittedReason(string findingId, VerificationRun run) =>
        ResponseText.Fitted(run.Verification.Reason,
                            candidate => Size(Build(findingId, run, EmptyEnvelope(), candidate),
                                              CacheDetectiveJsonContext.Default.VerifyFindingResult),
                            MAXIMUM_REASONED_SHELL_BYTES, REASON_TRUNCATION_MARKER);

    private static ListEnvelope<VerificationKeyItem> EmptyEnvelope() =>
        ResponseEnvelope.Create(Array.Empty<VerificationKeyItem>(), null,
                                CacheDetectiveJsonContext.Default.ListEnvelopeVerificationKeyItem);

    private static string Name(VerificationOutcome outcome) => outcome switch
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

    private static VerificationKeyItem Identity(VerificationKeyItem item) => Identity(item, ITEM_BUDGET);

    /// <summary>
    /// The record with nothing but what tells it apart, and the reason it says nothing else, cut to the
    /// room <paramref name="budget"/> actually leaves. The template is the only part of an identity that
    /// can grow, and shortening it to a fixed 256 characters was a guess at what would fit rather than a
    /// measurement of it: against a heavy shell even that was too much, and a page came back empty.
    /// <para>The hash is never cut, so the record stays distinguishable however little of the template
    /// survives.</para>
    /// </summary>
    private static VerificationKeyItem Identity(VerificationKeyItem item, int budget)
    {
        var identity = item with
        {
            PayloadLength = null,
            Reason = REDUCED_REASON,
            Fields = [],
            FieldsHidden = item.Fields.Count + item.FieldsHidden,
            Tables = [],
            TablesHidden = item.Tables.Count + item.TablesHidden,
            TableAges = []
        };

        // The template is cut to a modest length first, so that several identities still share a page
        // rather than one filling it. Then, and only if that is still too much for the room this shell
        // leaves, it is cut further by weight — the guarantee the character count alone could not give.
        var shortened = item.Template.Length <= TEMPLATE_LIMIT
                            ? identity
                            : identity with { Template = ResponseText.Truncate(item.Template, TEMPLATE_LIMIT, "…") };
        if (Size(shortened, CacheDetectiveJsonContext.Default.VerificationKeyItem) <= budget)
            return shortened;

        var fitted = ResponseText.Fitted(shortened.Template,
                                         candidate => Size(shortened with { Template = candidate! },
                                                           CacheDetectiveJsonContext.Default.VerificationKeyItem),
                                         budget, "…");
        return shortened with { Template = fitted ?? string.Empty };
    }

    private static bool Fits(VerificationKeyItem item) =>
        Size(item, CacheDetectiveJsonContext.Default.VerificationKeyItem) <= ITEM_BUDGET;

    private static int Size<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(value, typeInfo).Length;
}
