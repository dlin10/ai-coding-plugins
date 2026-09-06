using System.Text.Json;
using CacheDetective.Graph;
using CacheDetective.Rules;
using CacheDetective.Verification;
using StackExchange.Redis;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// What the readings add up to. Two orders of precedence, one for a key and one for a finding, and the
/// asymmetry between them is the whole design: one key that plainly disagrees carries the finding whatever
/// else was incomplete, while a refutation needs the sample to have been everything there was.
/// <para>Nothing here changes the finding. Verification observes and never suppresses; see
/// <c>docs/adr/0012</c>.</para>
/// </summary>
public sealed class FindingVerifierResultTests
{
    private const string Template = "product:{id}";
    private const string Marker = "cd-leak-marker-7f3a91";

    [Fact]
    public void All_fields_equal_refutes_the_key()
    {
        var key = FindingVerifier.VerifyKey(Match(), [Row("dbo.Products", Equal("price"), Equal("name"))], NoSignal, null);

        Assert.Equal(VerificationOutcome.Refuted, key.Outcome);
        Assert.Equal(2, key.Fields.Count(field => field.Comparable));
    }

    [Fact]
    public void One_field_that_differs_makes_the_key_possible()
    {
        var key = FindingVerifier.VerifyKey(Match(), [Row("dbo.Products", Equal("name"), Different("price"))], NoSignal, null);

        Assert.Equal(VerificationOutcome.Possible, key.Outcome);
        Assert.True(Assert.Single(key.Fields, field => field.Field == "price").Differs);
    }

    /// <summary>
    /// Field agreement does not outrank the age signal, and this reverses what this file used to assert.
    /// The old reading was that agreement is a statement about now while the age is about a moment that
    /// may not be the one the entry was written at — true, but it cuts the other way: the fields compared
    /// are a subset of what the value was built from, so a table written after the entry may have changed
    /// something this comparison never looked at. Agreement then is not proof the entry was never stale,
    /// and <c>docs/adr/0012</c> has verification observe rather than suppress.
    /// </summary>
    [Fact]
    public void An_age_signal_keeps_agreeing_fields_from_refuting()
    {
        var key = FindingVerifier.VerifyKey(Match(), [Row("dbo.Products", Equal("price"))], Signalling, null);

        Assert.Equal(VerificationOutcome.Possible, key.Outcome);
        Assert.Equal(VerificationBasis.Age, key.Basis);
    }

    /// <summary>With no age signal on it, agreement is still what refutes.</summary>
    [Fact]
    public void Agreeing_fields_refute_when_the_age_says_nothing()
    {
        var key = FindingVerifier.VerifyKey(Match(), [Row("dbo.Products", Equal("price"))], NoSignal, null);

        Assert.Equal(VerificationOutcome.Refuted, key.Outcome);
        Assert.Null(key.Basis);
    }

    [Fact]
    public void No_comparable_field_and_an_age_signal_is_possible()
    {
        var key = FindingVerifier.VerifyKey(Match(), [Row("dbo.Products", NotCompared("price", "a composite"))], Signalling, null);

        Assert.Equal(VerificationOutcome.Possible, key.Outcome);
        Assert.Contains("written after", key.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void No_comparable_field_and_no_signal_is_not_verifiable()
    {
        var key = FindingVerifier.VerifyKey(Match(), [Row("dbo.Products", NotCompared("price", "a composite"))], NoSignal, null);

        Assert.Equal(VerificationOutcome.NotVerifiable, key.Outcome);
    }

    [Fact]
    public void An_incomplete_traversal_does_not_stop_a_differing_field()
    {
        var result = Verify([Possible()], Scan(exhausted: false), Allowed);

        Assert.Equal(VerificationOutcome.Possible, result.Outcome);
    }

    [Fact]
    public void A_dependency_through_another_key_does_not_stop_a_differing_field()
    {
        var result = Verify([Possible()], Scan(), Withheld("the key depends on another cache key"));

        Assert.Equal(VerificationOutcome.Possible, result.Outcome);
    }

    [Fact]
    public void An_unresolved_on_a_branch_does_not_stop_a_differing_field()
    {
        var result = Verify([Possible()], Scan(), Withheld("2 unresolved item(s) lie on branches the walk visited"));

        Assert.Equal(VerificationOutcome.Possible, result.Outcome);
    }

    [Fact]
    public void An_exhausted_traversal_with_every_key_refuted_refutes()
    {
        var result = Verify([Refuted(), Refuted()], Scan(), Allowed);

        Assert.Equal(VerificationOutcome.Refuted, result.Outcome);
        Assert.Contains("existed throughout the traversal", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_traversal_that_stopped_short_is_not_verifiable_with_the_reason()
    {
        var result = Verify([Refuted()], Scan(exhausted: false), Allowed);

        Assert.Equal(VerificationOutcome.NotVerifiable, result.Outcome);
        Assert.Contains("did not reach the end of the keyspace", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Discarded_matches_are_not_verifiable_with_the_reason()
    {
        var result = Verify([Refuted()], Scan(discarded: true), Allowed);

        Assert.Equal(VerificationOutcome.NotVerifiable, result.Outcome);
        Assert.Contains("trimmed to the match limit", result.Reason, StringComparison.Ordinal);
        Assert.True(result.MatchesDiscarded);
    }

    [Fact]
    public void A_prefixed_key_does_not_refute()
    {
        var result = Verify([Refuted(prefixed: true)], Scan(), Allowed);

        Assert.Equal(VerificationOutcome.NotVerifiable, result.Outcome);
        Assert.Contains("prefix the workspace did not declare", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_withheld_refutation_names_what_prevented_it()
    {
        var result = Verify([Refuted()], Scan(), Withheld("the walk stopped at its depth limit"));

        Assert.Equal(VerificationOutcome.NotVerifiable, result.Outcome);
        Assert.Contains("agree with their rows, but this is not a refutation because", result.Reason, StringComparison.Ordinal);
        Assert.Contains("depth limit", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_incomplete_sample_with_one_possible_key_is_possible()
    {
        var result = Verify([Refuted(), Possible(), NotVerifiable()], Scan(exhausted: false, discarded: true), Allowed);

        Assert.Equal(VerificationOutcome.Possible, result.Outcome);
    }

    [Fact]
    public void Every_key_not_verifiable_gives_not_verifiable()
    {
        var result = Verify([NotVerifiable(), NotVerifiable()], Scan(), Allowed);

        Assert.Equal(VerificationOutcome.NotVerifiable, result.Outcome);
        Assert.DoesNotContain("agree with their rows", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_counts_and_the_discarded_flag_are_carried_out()
    {
        var result = Verify([Refuted(), Possible(), NotVerifiable(), NotVerifiable()], Scan(discarded: true), Allowed);

        Assert.Equal(1, result.Refuted);
        Assert.Equal(1, result.Possible);
        Assert.Equal(2, result.NotVerifiable);
        Assert.True(result.MatchesDiscarded);
        Assert.Equal(4, result.Keys.Count);
    }

    [Fact]
    public void Fields_are_not_compared_without_key_and_from()
    {
        var table = new TableRowComparison("dbo.Products",
            RowComparison.NotCompared("'dbo.Products' is not declared in verify.tables with both 'key' and 'from'"));

        var key = FindingVerifier.VerifyKey(Match(), [table], NoSignal, null);

        Assert.Empty(key.Fields);
        Assert.Equal(VerificationOutcome.NotVerifiable, key.Outcome);
        Assert.Contains("'key' and 'from'", key.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_comparison_runs_over_every_dependent_table()
    {
        var fields = FindingVerifier.Observe([Row("dbo.Products", Equal("price")), Row("dbo.Brands", Different("brand"))], null);

        Assert.Equal(["brand", "price"], fields.Select(field => field.Field));
        Assert.True(Assert.Single(fields, field => field.Field == "brand").Differs);
    }

    /// <summary>Which of the two rows it should have come from is undecided, and choosing one would be a
    /// guess dressed up as a measurement.</summary>
    [Fact]
    public void A_field_name_that_is_a_column_of_two_tables_is_not_compared()
    {
        var fields = FindingVerifier.Observe([Row("dbo.Products", Equal("name")), Row("dbo.Brands", Different("name"))], null);

        var observation = Assert.Single(fields);
        Assert.Equal(FieldVerdict.NotCompared, observation.Verdict);
        Assert.False(observation.Differs);
        Assert.Contains("more than one dependent table", observation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sensitive_field_is_marked_and_carries_no_value()
    {
        var fields = FindingVerifier.Observe([Row("dbo.Users", Different("email"), Different("CustomerPasswordHash"), Different("city"))],
                                             ["*apikey*"]);

        Assert.True(Assert.Single(fields, field => field.Field == "email").Redacted);
        Assert.True(Assert.Single(fields, field => field.Field == "CustomerPasswordHash").Redacted);
        Assert.False(Assert.Single(fields, field => field.Field == "city").Redacted);
        // Nothing is redacted by removal, because the record has no member that could hold a value: what
        // an observation says is its name and whether it differs, and there is nowhere else to look.
        Assert.Equal(["Field", "Reason", "Redacted", "Verdict"],
                     typeof(FieldObservation).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_value_that_is_not_JSON_gives_only_its_length()
    {
        var (document, length) = FindingVerifier.ReadPayload("not json at all");
        document?.Dispose();

        Assert.Null(document);
        Assert.Equal(15, length);
        var key = FindingVerifier.VerifyKey(Match(), [], NoSignal, null, payloadLength: length);
        Assert.Equal(15, key.PayloadLength);
        Assert.Empty(key.Fields);
    }

    [Fact]
    public void An_unavailable_database_is_a_partial_failure() =>
        AssertPartial(VerificationFailure.DatabaseUnavailable,
                      FindingVerifier.Classify(new VerificationReaderTests.FakeDbException("no route to host", -2)));

    [Fact]
    public void A_key_that_vanished_is_a_partial_failure() =>
        // Not an exception: TYPE answers "none" for a key that expired between the scan and the read.
        AssertPartial(VerificationFailure.KeyVanished, VerificationFailure.KeyVanished);

    [Fact]
    public void A_WRONGTYPE_reply_is_a_partial_failure() =>
        AssertPartial(VerificationFailure.WrongType,
                      FindingVerifier.Classify(new RedisServerException("WRONGTYPE Operation against a key holding the wrong kind of value")));

    [Fact]
    public void A_parameter_that_would_not_convert_is_a_partial_failure() =>
        AssertPartial(VerificationFailure.ParameterConversion, FindingVerifier.Classify(new InvalidCastException()));

    [Fact]
    public void The_marker_does_not_leak_from_a_successful_read() =>
        AssertNoLeak($$"""{ "note": "{{Marker}}", "price": 10 }""", null);

    [Fact]
    public void The_marker_does_not_leak_when_the_value_is_not_JSON() =>
        AssertNoLeak($"raw {Marker} bytes", null);

    [Fact]
    public void The_marker_does_not_leak_from_a_late_failure() =>
        AssertNoLeak($$"""{ "note": "{{Marker}}" }""", VerificationFailure.WrongType);

    /// <summary>Verification observes. The rule is re-evaluated after it and says exactly what it said
    /// before, because nothing verification produces reaches the graph.</summary>
    [Fact]
    public void Verification_does_not_change_the_confidence()
    {
        var graph = Planted();
        var before = graph.CacheKeys.Single();
        var confidence = Assert.Single(new UnguardedWriteRule().Evaluate(graph)).Confidence;

        Verify([Refuted()], Scan(), Allowed);

        Assert.Equal(confidence, Assert.Single(new UnguardedWriteRule().Evaluate(graph)).Confidence);
        Assert.Equal(before, graph.CacheKeys.Single());
    }

    [Fact]
    public void Verification_does_not_change_whether_the_finding_is_reported()
    {
        var graph = Planted();
        var suppressed = Assert.Single(new UnguardedWriteRule().Evaluate(graph)).Suppressed;

        Verify([Refuted(), Refuted()], Scan(), Allowed);

        var after = Assert.Single(new UnguardedWriteRule().Evaluate(graph));
        Assert.Equal(suppressed, after.Suppressed);
        Assert.False(after.Suppressed);
    }

    private static void AssertPartial(string expectedCode, string? code)
    {
        Assert.Equal(expectedCode, code);
        var key = FindingVerifier.VerifyKey(Match(), [Row("dbo.Products", Equal("price"))], NoSignal, null, failureCode: code);

        Assert.Equal(VerificationOutcome.NotVerifiable, key.Outcome);
        Assert.Equal(expectedCode, key.FailureCode);
        // What was already observed before the failure is kept.
        Assert.Equal("price", Assert.Single(key.Fields).Field);
        Assert.True(Verify([key], Scan(), Allowed).Partial);
    }

    /// <summary>The marker sits in the key and in the value at once, so anything that carried either of
    /// them into the tool's own channels would show it.</summary>
    private static void AssertNoLeak(string payload, string? failureCode)
    {
        string serialized = string.Empty;
        var console = Capture(() =>
        {
            var (document, length) = FindingVerifier.ReadPayload(payload);
            using (document)
            {
                var match = RedisReader.Match($"shop:product:{Marker}", Template, "shop:");
                var key = FindingVerifier.VerifyKey(match, [Row("dbo.Products", Equal("price"))], Signalling, null,
                                                    length, failureCode);
                serialized = JsonSerializer.Serialize(Verify([key], Scan(), Allowed));
            }
        });

        Assert.DoesNotContain(Marker, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Marker, console, StringComparison.OrdinalIgnoreCase);
    }

    private static string Capture(Action action)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var previousOutput = Console.Out;
        var previousError = Console.Error;
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }

        return output + error.ToString();
    }

    /// <summary>One unguarded write, so the rule has something to say before and after.</summary>
    private static CacheGraph Planted()
    {
        var graph = new CacheGraph();
        var handler = new Handler("app", "App.Get", "http", "C.cs", 1);
        graph.AddEdge(new Writes(handler, new Table("dbo.Products", "shop"), Confidence.Confirmed));
        graph.AddEdge(new Caches(handler, new CacheKey(Template, "redis", null, [], "cache"), Confidence.Confirmed));
        graph.AddEdge(new Reads(handler, new Table("dbo.Products", "shop"), Confidence.Confirmed));
        return graph;
    }

    private static FindingVerification Verify(IReadOnlyList<KeyVerification> keys, CacheScanResult scan, Applicability applicability) =>
        FindingVerifier.Verify(keys, scan, applicability);

    private static KeyVerification Refuted(bool prefixed = false) =>
        FindingVerifier.VerifyKey(Match(prefixed), [Row("dbo.Products", Equal("price"))], NoSignal, null);

    private static KeyVerification Possible() =>
        FindingVerifier.VerifyKey(Match(), [Row("dbo.Products", Different("price"))], NoSignal, null);

    private static KeyVerification NotVerifiable() =>
        FindingVerifier.VerifyKey(Match(), [Row("dbo.Products", NotCompared("price", "a composite"))], NoSignal, null);

    private static TableRowComparison Row(string table, params FieldComparison[] fields) =>
        new(table, new RowComparison(fields, null));

    private static FieldComparison Equal(string field) => new(field, FieldVerdict.Equal, null);

    private static FieldComparison Different(string field) => new(field, FieldVerdict.Different, null);

    private static FieldComparison NotCompared(string field, string reason) => FieldComparison.NotCompared(field, reason);

    private static KeyMatch Match(bool prefixed = false) =>
        new(Template, "0badc0de", new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = "42" }, prefixed, null);

    private static AgeSignal NoSignal => new(null, null, VerificationOutcome.NotVerifiable, "the entry's age is unknown");

    private static AgeSignal Signalling =>
        new(240, 30, VerificationOutcome.Possible, "the table was written after this entry was, so the entry may be stale");

    private static CacheScanResult Scan(bool exhausted = true, bool discarded = false) =>
        new([], exhausted, false, false, discarded, null);

    private static Applicability Allowed => new(true, true, null, [new Table("dbo.Products", "shop")]);

    private static Applicability Withheld(string reason) => new(true, false, reason, [new Table("dbo.Products", "shop")]);
}
