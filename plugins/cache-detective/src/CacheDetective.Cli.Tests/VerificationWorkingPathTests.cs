using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using CacheDetective.Configuration;
using CacheDetective.Graph;
using CacheDetective.Mcp;
using CacheDetective.Serialization;
using CacheDetective.Verification;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// The working path, driven with fake readers rather than the helpers underneath it. Every one of these
/// used to pass at the helper level and fail in the path that calls them: the applicability was decided
/// before there was a sample to decide it from, the elapsed time between the two readings was passed as
/// zero, and the age signal of one table was overwritten by the next.
/// </summary>
public sealed class VerificationWorkingPathTests
{
    private const string Template = "product:{id}";
    private const string First = "dbo.Products";
    private const string Second = "dbo.Prices";

    /// <summary>An exhaustive sample of keys each matched one way, whose every comparable field agrees.
    /// This is the case <c>refuted</c> exists for, and it was unreachable.</summary>
    [Fact]
    public async Task An_exhaustive_sample_of_agreeing_fields_is_refuted()
    {
        var database = new FakeDatabase { Columns = { "Id", "price" }, Row = [10m] };

        var run = await VerifyAsync(database, Scan(Entry("product:42", """{ "price": 10 }""")));

        Assert.Equal(VerificationOutcome.Refuted, run.Verification.Outcome);
        Assert.Equal(1, run.Verification.Refuted);
    }

    /// <summary>The same sample with one key the reader could not assign to the template in exactly one
    /// way: the readings are still taken and refutation is withheld.</summary>
    [Fact]
    public async Task An_ambiguous_key_in_the_sample_withholds_refutation()
    {
        // The table was written long before the entry, so no age signal can carry the finding and the
        // outcome turns on the ambiguity alone.
        var database = new FakeDatabase { Columns = { "Id", "price" }, Row = [10m], Seconds = 3000 };
        var ambiguous = new CacheEntry(KeyMatch.Failed(Template, "product:1:2", "the match is ambiguous"), """{ "price": 10 }""",
                                       60, null, null);

        var run = await VerifyAsync(database, Scan(Entry("product:42", """{ "price": 10 }"""), ambiguous));

        Assert.NotEqual(VerificationOutcome.Refuted, run.Verification.Outcome);
        Assert.Contains("ambiguous", run.Verification.Reason, StringComparison.Ordinal);
    }

    /// <summary>A database asked forty seconds after the cache, answering "95 seconds ago", was saying 55
    /// at the moment that mattered. The working path used to pass zero and report 95.</summary>
    [Fact]
    public async Task The_gap_between_the_two_readings_is_subtracted()
    {
        var database = new FakeDatabase { Seconds = 95 };

        var run = await VerifyAsync(database, Scan(Entry("product:42", """{ "price": 10 }""")), now: () => 40);

        var key = Assert.Single(run.Verification.Keys);
        Assert.Equal(55, Assert.Single(key.TableAges!, table => table.Table == First).SecondsSinceLastWrite);
        Assert.Equal(40, key.ElapsedSeconds);
    }

    /// <summary>
    /// Each key is dated from when <em>it</em> was read, and the gap is closed after the database query
    /// returns rather than before it is sent. The gap used to run from the end of the whole sample to the
    /// start of each query, which lost the time spent reading the other keys for every early key and the
    /// query's own duration for all of them — both of which shift the age signal and can hide a pair of
    /// readings that has drifted past the clock margin.
    /// </summary>
    [Fact]
    public async Task Each_key_is_dated_from_its_own_reading_and_the_gap_includes_the_query()
    {
        var now = 25d;
        var database = new FakeDatabase { Seconds = 600, OnStalenessRead = () => now += 5 };
        var early = Entry("product:1", """{ "price": 10 }""") with { ObservedSeconds = 1 };
        var late = Entry("product:2", """{ "price": 10 }""") with { ObservedSeconds = 20 };

        var run = await VerifyAsync(database, Scan(early, late), now: () => now);

        // The first key was read 19 seconds before the second, so its readings are further apart.
        var first = run.Verification.Keys[0];
        var second = run.Verification.Keys[1];
        Assert.Equal(29, first.ElapsedSeconds);
        Assert.Equal(15, second.ElapsedSeconds);
        Assert.True(first.ElapsedSeconds > second.ElapsedSeconds, "the earlier key was not dated earlier");

        // 24 seconds is the gap to the moment the query was sent; the extra 5 is the query itself.
        Assert.True(first.ElapsedSeconds > 24, "the query's own duration was left out of the gap");
    }

    /// <summary>Beyond the margin the two readings cannot be placed on one timeline, so there is no age
    /// signal at all rather than a fudged one.</summary>
    [Fact]
    public async Task A_gap_wider_than_the_margin_leaves_no_age()
    {
        var database = new FakeDatabase { Seconds = 95 };

        var run = await VerifyAsync(database, Scan(Entry("product:42", "not json")), now: () => 3600);

        var age = Assert.Single(Assert.Single(run.Verification.Keys).TableAges!);
        Assert.Null(age.SecondsSinceLastWrite);
        Assert.Contains("clock margin", age.Reason, StringComparison.Ordinal);
    }

    /// <summary>Two dependent tables, the first written after the entry and the second with no statistics
    /// at all. The <c>possible</c> the first established used to be erased by the second.</summary>
    [Fact]
    public async Task A_later_table_without_statistics_does_not_erase_the_possible_signal()
    {
        var database = new FakeDatabase { Seconds = 30, SecondsByTable = { [Second] = null } };

        var run = await VerifyAsync(database, Scan(Entry("product:42", "not json")), tables: [First, Second]);

        Assert.Equal(VerificationOutcome.Possible, run.Verification.Outcome);
        var ages = Assert.Single(run.Verification.Keys).TableAges!;
        Assert.Equal(2, ages.Count);
        Assert.Equal(30, ages.Single(table => table.Table == First).SecondsSinceLastWrite);
        Assert.Null(ages.Single(table => table.Table == Second).SecondsSinceLastWrite);
    }

    /// <summary>The last write is read for every dependent table, because the index usage statistics carry
    /// no row data; only the row comparison is confined to the tables verify.tables names.</summary>
    [Fact]
    public async Task The_last_write_is_read_for_tables_outside_verify_tables()
    {
        var database = new FakeDatabase { Columns = { "Id", "price" }, Row = [10m] };

        await VerifyAsync(database, Scan(Entry("product:42", """{ "price": 10 }""")), tables: [First, Second]);

        Assert.Equal(2, database.StalenessReads.Count);
        Assert.Contains("Products", database.StalenessReads);
        Assert.Contains("Prices", database.StalenessReads);
        Assert.DoesNotContain(database.CommandTexts, text => text.Contains("[Prices]", StringComparison.Ordinal));
    }

    /// <summary>A difference already observed survives a failure that comes after it: the finding is
    /// possible, and the failure is reported beside it rather than in place of it.</summary>
    [Fact]
    public async Task A_difference_survives_a_later_failure()
    {
        var database = new FakeDatabase { Columns = { "Id", "price" }, Row = [99m], FailStalenessFor = Second };

        var run = await VerifyAsync(database, Scan(Entry("product:42", """{ "price": 10 }""")), tables: [First, Second]);

        Assert.Equal(VerificationOutcome.Possible, run.Verification.Outcome);
        Assert.True(run.Verification.Partial);
        Assert.Equal(VerificationFailure.DatabaseUnavailable, Assert.Single(run.Verification.Keys).FailureCode);
    }

    /// <summary>
    /// A dependent table verify.tables does not name. R7 asks that a step which did not happen be
    /// recorded with its reason; without this the key came back carrying only the age signal's reason,
    /// which says something else entirely.
    /// </summary>
    [Fact]
    public async Task An_undeclared_table_is_recorded_as_not_compared()
    {
        // The undeclared table has no column of the cached value's, so it takes no part in the comparison
        // and nothing about it is ambiguous. A_column_shared_with_an_undeclared_table_does_not_refute poses
        // the other case, where it does hold the column and the agreement stops being decisive.
        var database = new FakeDatabase
        {
            ColumnsByTable = { ["Products"] = ["Id", "price"], ["Prices"] = ["Id", "amount"] },
            RowByTable = { ["Products"] = [10m] }
        };

        var run = await VerifyAsync(database, Scan(Entry("product:42", """{ "price": 10 }""")), tables: [First, Second]);

        var key = Assert.Single(run.Verification.Keys);
        Assert.Contains("not declared in verify.tables", key.Reason, StringComparison.Ordinal);

        // The declared table still refutes on its own fields; what the undeclared one costs is recorded
        // beside that verdict rather than in place of it.
        Assert.Equal(VerificationOutcome.Refuted, key.Outcome);
    }

    /// <summary>When no dependent table is declared at all there is nothing to compare against, so
    /// refutation is out of reach however healthy the entry looks.</summary>
    [Fact]
    public async Task A_finding_whose_only_table_is_undeclared_cannot_be_refuted()
    {
        var database = new FakeDatabase { Columns = { "Id", "price" }, Row = [10m], Seconds = 3000 };

        var run = await VerifyAsync(database, Scan(Entry("product:42", """{ "price": 10 }""")), declared: "dbo.Somewhere.Else");

        Assert.NotEqual(VerificationOutcome.Refuted, run.Verification.Outcome);
        Assert.Contains("not declared in verify.tables", Assert.Single(run.Verification.Keys).Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A table declared in another case. The names come out of the code and the configuration is written
    /// by hand, so an ordinal lookup silently skipped the comparison the workspace had asked for.
    /// </summary>
    [Fact]
    public async Task A_table_declared_in_another_case_is_still_compared()
    {
        var database = new FakeDatabase { Columns = { "Id", "price" }, Row = [10m] };

        var run = await VerifyAsync(database, Scan(Entry("product:42", """{ "price": 10 }""")),
                                    declared: "DBO.PRODUCTS");

        Assert.Equal(VerificationOutcome.Refuted, run.Verification.Outcome);
    }

    /// <summary>
    /// A finding about a write to one table, verified against a key that depends on two. Agreement with
    /// the <em>other</em> table is not evidence about this finding: R8 forbids refuting a claim through a
    /// dependency it was never made about, and the subject used to be only the key, so a finding about
    /// dbo.Prices could be refuted by the fields of dbo.Products agreeing.
    /// </summary>
    [Fact]
    public async Task A_finding_about_one_table_is_not_refuted_by_another_tables_fields_agreeing()
    {
        var database = new FakeDatabase
        {
            ColumnsByTable = { ["Products"] = ["Id", "price"], ["Prices"] = ["Id", "amount"] },
            RowByTable = { ["Products"] = [10m], ["Prices"] = null }
        };

        var run = await VerifyAsync(database, Scan(Entry("product:42", """{ "price": 10, "amount": 3 }""")),
                                    tables: [First, Second], refutingTable: Second, declaredTables: [First, Second]);

        // The reading of dbo.Products still happened and is still reported; what it cannot do is settle a
        // finding about dbo.Prices.
        Assert.NotEqual(VerificationOutcome.Refuted, run.Verification.Outcome);
        var key = Assert.Single(run.Verification.Keys);
        Assert.Equal(FieldVerdict.Equal, Assert.Single(key.Fields, field => field.Field == "price").Verdict);
        Assert.Contains(Second, key.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two dependent tables that both have a <c>price</c> column, one of which finds no row. Whether a
    /// field name belongs to two tables is a fact about the tables; deciding it from the comparisons that
    /// came back let the table with no row drop out, made <c>price</c> look like the sole property of the
    /// other one, and refuted the finding on a field task 8 forbids comparing at all.
    /// </summary>
    [Fact]
    public async Task A_field_two_tables_share_is_not_compared_even_when_one_finds_no_row()
    {
        var database = new FakeDatabase
        {
            ColumnsByTable = { ["Products"] = ["Id", "price"], ["Prices"] = ["Id", "price"] },
            RowByTable = { ["Products"] = [10m], ["Prices"] = null },
            Seconds = 3000
        };

        var run = await VerifyAsync(database, Scan(Entry("product:42", """{ "price": 10 }""")),
                                    tables: [First, Second], declaredTables: [First, Second]);

        var price = Assert.Single(Assert.Single(run.Verification.Keys).Fields, field => field.Field == "price");
        Assert.Equal(FieldVerdict.NotCompared, price.Verdict);
        Assert.Contains("more than one dependent table", price.Reason, StringComparison.Ordinal);
        Assert.NotEqual(VerificationOutcome.Refuted, run.Verification.Outcome);
    }

    /// <summary>
    /// The same shared column, but the second table bows out before the row lookup: its configured
    /// <c>from</c> is not a placeholder of this template. That refusal came back carrying no columns at
    /// all, so <c>price</c> looked like the sole property of the table that did answer and could refute the
    /// finding — the ambiguity rule was reinstated for one refusal and stepped around by every other.
    /// </summary>
    [Fact]
    public async Task A_shared_column_is_not_compared_when_the_other_table_bows_out_early()
    {
        var database = new FakeDatabase
        {
            ColumnsByTable = { ["Products"] = ["Id", "price"], ["Prices"] = ["Id", "price"] },
            RowByTable = { ["Products"] = [10m], ["Prices"] = [10m] },
            Seconds = 3000
        };

        var run = await VerifyAsync(database, Scan(Entry("product:42", """{ "price": 10 }""")),
                                    tables: [First, Second], declaredTables: [First, Second], from: "sku");

        var price = Assert.Single(Assert.Single(run.Verification.Keys).Fields, field => field.Field == "price");
        Assert.Equal(FieldVerdict.NotCompared, price.Verdict);
        Assert.Contains("more than one dependent table", price.Reason, StringComparison.Ordinal);
        Assert.NotEqual(VerificationOutcome.Refuted, run.Verification.Outcome);
    }

    /// <summary>
    /// A finding about T2, where T1 compared and agreed, T2 offered no comparable field, and T2's own clock
    /// says it was written after the entry. The agreement belongs to a dependency the finding was never
    /// made about, and it was being allowed to silence the target table's own signal: the "fields agree,
    /// but not from this table" branch answered <c>not_verifiable</c> before the age was ever consulted.
    /// </summary>
    [Fact]
    public async Task An_unrelated_tables_agreement_does_not_silence_the_target_tables_age()
    {
        var database = new FakeDatabase
        {
            ColumnsByTable = { ["Products"] = ["Id", "price"], ["Prices"] = ["Id", "amount"] },
            RowByTable = { ["Products"] = [10m], ["Prices"] = null },
            SecondsByTable = { [First] = 3000, [Second] = 5 }
        };

        // The entry is 300s old by its TTL, so dbo.Prices at 5s was written well after it.
        var run = await VerifyAsync(database, Scan(Entry("product:42", """{ "price": 10 }""")),
                                    tables: [First, Second], refutingTable: Second, declaredTables: [First, Second]);

        Assert.Equal(VerificationOutcome.Possible, run.Verification.Outcome);
        var key = Assert.Single(run.Verification.Keys);
        Assert.Equal(VerificationBasis.Age, key.Basis);
        Assert.Contains("written after", key.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a <c>possible</c> rests on travels with it. The three cases read differently to an agent: a
    /// field that differs is a disagreement it can go and look at, an age signal only says a write landed
    /// where it might matter, and one sentence covering both told it to look for a difference that had
    /// never been observed.
    /// </summary>
    [Theory]
    [InlineData(99, 3000, VerificationBasis.FieldDifference, "differ from the row")]
    [InlineData(10, 5, VerificationBasis.Age, "written after")]
    [InlineData(99, 5, VerificationBasis.Both, "differ from the row")]
    public async Task A_possible_says_what_it_rests_on(int row, int seconds, string basis, string expected)
    {
        var database = new FakeDatabase { Columns = { "Id", "price" }, Row = [(decimal)row], Seconds = seconds };

        var run = await VerifyAsync(database, Scan(Entry("product:42", """{ "price": 10 }""")));
        var json = JsonSerializer.Serialize(VerificationQueries.Present("f:1", run, null),
                                            CacheDetectiveJsonContext.Default.VerifyFindingResult);

        Assert.Equal(VerificationOutcome.Possible, run.Verification.Outcome);
        Assert.Equal(basis, run.Verification.Basis);
        Assert.Contains(expected, Assert.Single(run.Verification.Keys).Reason, StringComparison.Ordinal);
        Assert.Equal(basis, JsonDocument.Parse(json).RootElement.GetProperty("basis").GetString());
    }

    /// <summary>
    /// A dependent table the workspace did not declare, holding the same column as the declared one. Its
    /// row is not read — there is no key to look one up by — but its <em>columns</em> are, because whether
    /// two tables share a field name is a fact about the tables. Reporting none for it made <c>price</c>
    /// look like the declared table's alone, and that table's agreement then refuted a finding whose value
    /// may have been built partly from the undeclared one.
    /// </summary>
    [Fact]
    public async Task A_column_shared_with_an_undeclared_table_does_not_refute()
    {
        var database = new FakeDatabase
        {
            ColumnsByTable = { ["Products"] = ["Id", "price"], ["Prices"] = ["Id", "price"] },
            RowByTable = { ["Products"] = [10m] },
            Seconds = 3000
        };

        var run = await VerifyAsync(database, Scan(Entry("product:42", """{ "price": 10 }""")),
                                    tables: [First, Second], declaredTables: [First]);

        var key = Assert.Single(run.Verification.Keys);
        var price = Assert.Single(key.Fields, field => field.Field == "price");
        Assert.Equal(FieldVerdict.NotCompared, price.Verdict);
        Assert.Contains("more than one dependent table", price.Reason, StringComparison.Ordinal);
        Assert.NotEqual(VerificationOutcome.Refuted, run.Verification.Outcome);
        Assert.Contains("not declared in verify.tables", key.Reason, StringComparison.Ordinal);

        // The catalogue was read for the undeclared table; its rows were not.
        Assert.DoesNotContain(database.CommandTexts, text => text.Contains("[Prices]", StringComparison.Ordinal));
    }

    /// <summary>A refused permission and a value the server would not convert are different things, and
    /// neither is the database being unavailable.</summary>
    [Fact]
    public void A_provider_error_is_classified_by_what_it_was()
    {
        Assert.Equal(VerificationFailure.PermissionDenied,
                     FindingVerifier.Classify(new VerificationReaderTests.FakeDbException("denied", 297)));
        Assert.Equal(VerificationFailure.ParameterConversion,
                     FindingVerifier.Classify(new VerificationReaderTests.FakeDbException("conversion failed", 245)));
        Assert.Equal(VerificationFailure.DatabaseUnavailable,
                     FindingVerifier.Classify(new VerificationReaderTests.FakeDbException("gone", 4060)));
    }

    /// <summary>The reader's own reason for a key reaches the result. It used to be replaced by
    /// <c>key_vanished</c>, which said something else entirely.</summary>
    [Fact]
    public async Task An_entry_reason_is_carried_through_rather_than_renamed()
    {
        var database = new FakeDatabase();
        var unreadable = new CacheEntry(KeyMatch.Failed(Template, "product:1:2", "the match is ambiguous"), null, null, null,
                                        "the entry is of a type verification does not read");

        var run = await VerifyAsync(database, Scan(unreadable));

        var key = Assert.Single(run.Verification.Keys);
        Assert.NotEqual(VerificationFailure.KeyVanished, key.FailureCode);
        Assert.Contains("type verification does not read", key.Reason, StringComparison.Ordinal);
    }

    /// <summary>The numbers a report quotes travel in the response, and none of them is a key or a
    /// value.</summary>
    [Fact]
    public async Task The_numbers_reach_the_serialized_response()
    {
        var database = new FakeDatabase { Seconds = 30 };

        var run = await VerifyAsync(database, Scan(Entry("product:42", "not json")));
        var result = VerificationQueries.Present("f:1", run, null);
        var json = JsonSerializer.Serialize(result, CacheDetectiveJsonContext.Default.VerifyFindingResult);

        using var document = JsonDocument.Parse(json);
        var key = document.RootElement.GetProperty("keys").GetProperty("items")[0];
        Assert.Equal(240, key.GetProperty("entryAgeSeconds").GetDouble());
        Assert.Equal(60, key.GetProperty("clockMarginSeconds").GetDouble());
        Assert.True(key.TryGetProperty("elapsedSeconds", out _));
        Assert.Equal(30, key.GetProperty("tableAges")[0].GetProperty("secondsSinceLastWrite").GetDouble());
        Assert.DoesNotContain("product:42", json, StringComparison.Ordinal);
    }

    private static async Task<VerificationRun> VerifyAsync(FakeDatabase database, CacheScanResult scan,
                                                            IReadOnlyList<string>? tables = null, Func<double>? now = null,
                                                            string declared = First, string? refutingTable = null,
                                                            IReadOnlyList<string>? declaredTables = null,
                                                            string? from = null)
    {
        var names = tables ?? [First];
        var graph = new CacheGraph();
        var handler = new Handler("App", "App.Get", "method", "App.cs", 1) { Project = "App" };
        var key = new CacheKey(Template, "redis", TimeSpan.FromSeconds(300), [], "cache");
        graph.AddEdge(new Caches(handler, key, Confidence.Confirmed));
        foreach (var name in names)
            graph.AddEdge(new Reads(handler, new Table("dbo", name.Split('.')[1], "shop"), Confidence.Confirmed));

        var verify = new VerifyConfiguration
        {
            // `from`, when given, applies to the last declared table only: the case worth posing is one
            // table bowing out on its placeholder while another still compares.
            Tables = (declaredTables ?? [declared]).ToDictionary(name => name,
                                                                 name => new VerifyTableConfiguration
                                                                 {
                                                                     Key = "Id",
                                                                     From = from is not null && name == (declaredTables ?? [declared])[^1]
                                                                                ? from
                                                                                : "id"
                                                                 },
                                                                 StringComparer.Ordinal)
        };
        var applicability = FindingVerifier.Assess(graph, graph.CacheKeys.Single(), verify, null);
        Assert.True(applicability.Starts);

        return await new WorkspaceSession().VerifySampleAsync(new VerificationReader(database.Connect()), verify,
                                                               graph.CacheKeys.Single(), applicability, names, scan,
                                                               now ?? (() => 0), default, refutingTable);
    }

    private static CacheScanResult Scan(params CacheEntry[] entries) => new(entries, true, false, false, false, null);

    private static CacheEntry Entry(string key, string payload) =>
        new(RedisReader.Match(key, Template, string.Empty), payload, 60, null, null);

    /// <summary>Enough of a database to answer the two statements the reader issues, per table.</summary>
    private sealed class FakeDatabase
    {
        internal List<string> CommandTexts { get; } = [];
        internal List<string> StalenessReads { get; } = [];
        internal List<string> Columns { get; } = [];
        internal Dictionary<string, double?> SecondsByTable { get; } = new(StringComparer.Ordinal);

        /// <summary>Columns per bare table name, for the cases where two dependent tables have to differ.
        /// A table not named here answers with <see cref="Columns"/>.</summary>
        internal Dictionary<string, string[]> ColumnsByTable { get; } = new(StringComparer.Ordinal);

        /// <summary>The row per bare table name, <c>null</c> for a table whose lookup finds none.</summary>
        internal Dictionary<string, object?[]?> RowByTable { get; } = new(StringComparer.Ordinal);
        internal object?[]? Row { get; init; }
        /// <summary>Older than any entry these tests build, so a fixture carries no age signal unless it
        /// asks for one. It used to default to younger than the entry, which meant the tests about fields
        /// agreeing were quietly also testing that agreement outranked a table written after the entry —
        /// which it does not.</summary>
        internal double? Seconds { get; init; } = 3000;
        internal string? FailStalenessFor { get; init; }

        /// <summary>Run when the staleness query answers, so a test can make the query take time.</summary>
        internal Action? OnStalenessRead { get; init; }

        internal DbConnection Connect() => new FakeConnection(this);

        internal IReadOnlyList<object?[]> Read(string sql, Dictionary<string, object?> parameters)
        {
            CommandTexts.Add(sql);
            if (sql.Contains("dm_db_index_usage_stats", StringComparison.Ordinal))
            {
                var name = (string)parameters["@name"]!;
                StalenessReads.Add(name);
                if (FailStalenessFor is { } failing && failing.EndsWith(name, StringComparison.Ordinal))
                    throw new VerificationReaderTests.FakeDbException("the connection is gone", 4060);
                var seconds = SecondsByTable.TryGetValue($"dbo.{name}", out var declared) ? declared : Seconds;
                OnStalenessRead?.Invoke();
                return [[seconds]];
            }

            if (sql.Contains("sys.columns", StringComparison.Ordinal))
            {
                IReadOnlyList<string> declared = ColumnsByTable.TryGetValue((string)parameters["@name"]!, out var columns)
                                                     ? columns
                                                     : Columns;
                return declared.Select(column => new object?[] { column }).ToArray();
            }

            // The row statement names its table rather than parameterising it, so which table is being
            // asked for is read off the text.
            foreach (var (name, row) in RowByTable)
            {
                if (sql.Contains($"[{name}]", StringComparison.Ordinal))
                    return row is null ? [] : [row];
            }

            return Row is null ? [] : [Row];
        }
    }

    private sealed class FakeConnection(FakeDatabase database) : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new FakeCommand(database, this);
    }

    private sealed class FakeCommand(FakeDatabase database, DbConnection connection) : DbCommand
    {
        private readonly DbParameterCollection _parameters = new FakeParameters();
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FakeParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DbParameter parameter in _parameters)
                values[parameter.ParameterName] = parameter.Value;
            return new FakeReader(database.Read(CommandText, values));
        }
    }

    private sealed class FakeParameters : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => _items;
        public override int Add(object? value) { _items.Add((DbParameter)value!); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var value in values) Add(value!); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object? value) => _items.Contains((DbParameter)value!);
        public override bool Contains(string value) => _items.Any(item => item.ParameterName == value);
        public override void CopyTo(Array array, int index) => _items.ToArray().CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object? value) => _items.IndexOf((DbParameter)value!);
        public override int IndexOf(string parameterName) => _items.FindIndex(item => item.ParameterName == parameterName);
        public override void Insert(int index, object? value) => _items.Insert(index, (DbParameter)value!);
        public override void Remove(object? value) => _items.Remove((DbParameter)value!);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    private sealed class FakeParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FakeReader(IReadOnlyList<object?[]> rows) : DbDataReader
    {
        private int _index = -1;
        public override bool Read() => ++_index < rows.Count;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override object GetValue(int ordinal) => rows[_index][ordinal]!;
        public override bool IsDBNull(int ordinal) => rows[_index][ordinal] is null;
        public override string GetString(int ordinal) => (string)rows[_index][ordinal]!;
        public override int FieldCount => rows.Count == 0 ? 0 : rows[0].Length;
        public override bool HasRows => rows.Count > 0;
        public override int Depth => 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => throw new NotSupportedException();
        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
        public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => (char)GetValue(ordinal);
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => 0;
        public override string GetDataTypeName(int ordinal) => "fake";
        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
        public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
        public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
        public override Type GetFieldType(int ordinal) => GetValue(ordinal).GetType();
        public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
        public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
        public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
        public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
        public override string GetName(int ordinal) => $"c{ordinal}";
        public override int GetOrdinal(string name) => throw new NotSupportedException();
        public override int GetValues(object[] values) => 0;
        public override bool NextResult() => false;
        public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
    }
}
