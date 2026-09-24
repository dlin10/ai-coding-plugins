using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The deep read of a known call (R3): every field of every object the argument reaches, read where the call stands, as the
/// ordinary read of that field would be.</summary>
public sealed class DeepReadTests
{
    private const string SERIALIZE = "System.Text.Json.JsonSerializer.Serialize(_state);";

    // ---- the pair a deep read makes ----

    [Fact]
    public async Task Serialize_of_a_singleton_against_a_field_write_is_DCA1001_on_that_field()
    {
        var finding = Assert.Single(await Findings(SERIALIZE, "_state.Count = 1;"));

        Assert.Equal("DCA1001", finding.RuleId);
        Assert.Equal(["Count"], finding.Resource.AccessPath);
        AssertTwoSides(finding);
    }

    [Fact]
    public async Task Nested_field_two_levels_down_is_the_resource_an_ordinary_read_gives()
    {
        var deep = Assert.Single(await Findings(SERIALIZE, "_state.Inner.Deep.Value = 1;"));
        var ordinary = Assert.Single(await Findings("_ = _state.Inner.Deep.Value;", "_state.Inner.Deep.Value = 1;"));

        Assert.Equal(ordinary.Resource.Identity, deep.Resource.Identity);
        Assert.Equal(["Value"], deep.Resource.AccessPath);
        AssertTwoSides(deep);
    }

    [Fact]
    public async Task Backing_field_of_an_automatic_property_is_read()
    {
        var finding = Assert.Single(await Findings(SERIALIZE, "_state.Total = 1;"));

        Assert.Equal(["Total"], finding.Resource.AccessPath);
        AssertTwoSides(finding);
    }

    [Fact]
    public async Task Field_of_a_source_base_type_is_read()
    {
        var finding = Assert.Single(await Findings(SERIALIZE, "_state.Inherited = 1;"));

        Assert.Equal(["Inherited"], finding.Resource.AccessPath);
        AssertTwoSides(finding);
    }

    [Fact]
    public async Task Past_the_depth_limit_the_object_is_one_wildcard_read_and_the_pair_stays()
    {
        var finding = Assert.Single(await Findings("System.Text.Json.JsonSerializer.Serialize(_state.Chain);", "Holder.Shared.Value = 1;"));

        Assert.True(finding.Resource.IsWildcard);
        AssertTwoSides(finding);
        var run = Run("System.Text.Json.JsonSerializer.Serialize(_state.Chain);", "Holder.Shared.Value = 1;");
        Assert.Contains(AtCall(run), access => access.Resource.AccessPath is ["Next"] && access.Resource.Region.Contains("Level7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Past_the_depth_limit_the_objects_further_on_are_read_too()
    {
        // One level deeper than the chain alone, the limit falls on an object that still leads to the shared one.
        var finding = Assert.Single(await Findings("System.Text.Json.JsonSerializer.Serialize(new[] { _state.Chain });", "Holder.Shared.Value = 1;"));

        Assert.True(finding.Resource.IsWildcard);
        Assert.Contains("Level8", finding.Resource.Region, StringComparison.Ordinal);
        AssertTwoSides(finding);
    }

    [Fact]
    public void Reference_cycle_ends_and_reads_each_field_once()
    {
        var run = Run("System.Text.Json.JsonSerializer.Serialize(_state.Loop);", "");
        var reads = AtCall(run).Select(access => access.Resource.Identity).ToArray();

        Assert.Equal(reads.Distinct().Count(), reads.Length);
        Assert.Contains(AtCall(run), access => access.Resource.AccessPath is ["Other"]);
        Assert.Contains(AtCall(run), access => access.Resource.AccessPath is ["Back"]);
    }

    // ---- collections, arrays and slices ----

    [Fact]
    public async Task List_field_is_its_structure_and_cells_then_the_fields_of_its_objects()
    {
        var run = Run(SERIALIZE, "");
        var items = AtCall(run).Where(access => access.Resource.AccessPath[0] == "Items").ToArray();

        Assert.Contains(items, access => access.Resource.AccessPath.Count == 1 && access.Resource.CollectionId is not null);
        Assert.Contains(items, access => access.Resource.AccessPath is [_, "[?]"]);
        var finding = Assert.Single(await Findings(SERIALIZE, "_state.First.Value = 1;"));
        Assert.Equal(["Value"], finding.Resource.AccessPath);
        AssertTwoSides(finding);
    }

    [Fact]
    public async Task Enumerable_Count_over_a_shared_List_conflicts_with_Add_on_the_structure()
    {
        var finding = Assert.Single(await Findings("System.Linq.Enumerable.Count(_state.Items);", "_state.Items.Add(new Item());"),
                                    item => item.Resource.Selector is null);

        Assert.Equal("DCA1001", finding.RuleId);
        Assert.NotNull(finding.Resource.CollectionId);
        AssertTwoSides(finding);
    }

    [Fact]
    public async Task Enumerable_Count_over_a_shared_ConcurrentDictionary_has_no_structural_pair_with_TryAdd()
    {
        var findings = await Findings("System.Linq.Enumerable.Count(_state.Map);", "_state.Map.TryAdd(2, new Item());");

        Assert.DoesNotContain(findings, finding => finding.Resource.AccessPath[0] == "Map");
        var run = Run("System.Linq.Enumerable.Count(_state.Map);", "");
        Assert.Contains(AtCall(run), access => access.Resource.AccessPath[0] == "Map" && access.Operation == AccessOperation.AtomicRead);
    }

    [Fact]
    public async Task Field_of_an_object_in_a_ConcurrentDictionary_pairs_with_a_write_of_it()
    {
        var finding = Assert.Single(await Findings("System.Linq.Enumerable.Count(_state.Map);", "_state.Mapped.Value = 1;"));

        Assert.Equal(["Value"], finding.Resource.AccessPath);
        AssertTwoSides(finding);
    }

    [Fact]
    public async Task Ready_slice_over_a_singleton_array_field_pairs_on_the_cell()
    {
        var finding = Assert.Single(await Findings("System.ReadOnlySpan<string> names = _state.Names; string.Concat(names);",
                                                   "_state.Names[0] = \"x\";"));

        Assert.Equal("Names", finding.Resource.AccessPath[0]);
        Assert.NotNull(finding.Resource.Selector);
        AssertTwoSides(finding);
    }

    [Fact]
    public void Params_slice_of_strings_makes_no_access()
    {
        var run = Run("_ = string.Concat(\"a\", \"b\", \"c\", \"d\", _state.Text);", "");
        var without = Run("_ = _state.Text;", "");

        Assert.Equal(Shapes(without), Shapes(run));
        // The slice the call creates is its elements, not a slice handed over ready whose storage nothing proves.
        Assert.Equal(without.Counter(CoverageCounters.UNPROVEN_REFERENCE), run.Counter(CoverageCounters.UNPROVEN_REFERENCE));
    }

    [Fact]
    public void Ready_slice_without_proven_storage_reads_nothing_and_is_an_unproven_reference()
    {
        var run = Run("System.ReadOnlySpan<string> local = new[] { \"a\", \"b\" }; _ = string.Concat(local);", "");
        var without = Run("System.ReadOnlySpan<string> local = new[] { \"a\", \"b\" };", "");

        Assert.Empty(AtCall(run));
        Assert.Equal(1, run.Counter(CoverageCounters.UNPROVEN_REFERENCE) - without.Counter(CoverageCounters.UNPROVEN_REFERENCE));
    }

    // ---- what is not read ----

    [Fact]
    public void Object_of_a_type_from_metadata_is_one_wildcard_read()
    {
        var run = Run("System.Text.Json.JsonSerializer.Serialize(_state.Builder);", "");

        // The load of the field the argument comes from is the reader's own; the call adds one wildcard read and nothing else.
        var access = Assert.Single(AtCall(run), access => access.Resource.AccessPath is not ["Builder"]);
        Assert.True(access.Resource.IsWildcard);
        Assert.Contains("StringBuilder", access.Resource.Region, StringComparison.Ordinal);
    }

    [Fact]
    public void Immutable_argument_or_object_makes_no_access()
    {
        var run = Run("System.Text.Json.JsonSerializer.Serialize(_state.Address);", "");
        var without = Run("_ = _state.Address;", "");

        Assert.Equal(Shapes(without), Shapes(run));
        // Reached through a field, the object is not read either: only the field that holds it is.
        Assert.DoesNotContain(AtCall(Run(SERIALIZE, "")), access => access.Resource.Region.Contains("Uri", StringComparison.Ordinal));
    }

    [Fact]
    public void Queryable_operators_make_no_access()
    {
        var run = Run("_ = System.Linq.Queryable.Where(_state.Query, value => value > 0);", "");
        var without = Run("_ = _state.Query;", "");

        Assert.Equal(Shapes(without), Shapes(run));
    }

    [Fact]
    public void Getter_that_writes_a_field_is_not_run()
    {
        var run = Run(SERIALIZE, "");

        Assert.Contains(AtCall(run), access => access.Resource.AccessPath is ["_hits"] && access.Operation == AccessOperation.Read);
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Resource.AccessPath is ["_hits"] && access.Operation != AccessOperation.Read);
    }

    // ---- the elements of params and explicit arrays ----

    [Fact]
    public async Task Params_elements_of_LogInformation_are_each_read_deep()
    {
        var findings = await Findings("Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(Log, \"{A} {B}\", _state.Inner, _state.First);",
                                      "_state.Inner.Deep.Value = 1; _state.First.Value = 2;", Packages);

        Assert.Equal(2, findings.Count);
        Assert.All(findings, AssertTwoSides);
    }

    [Fact]
    public async Task Explicit_array_given_to_LogInformation_is_read_through_its_cells()
    {
        var run = Run("Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(Log, \"{A}\", _state.Arguments);", "", Packages);

        Assert.Contains(AtCall(run), access => access.Resource.AccessPath is ["Arguments", "[?]"]);
        var finding = Assert.Single(await Findings("Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(Log, \"{A}\", _state.Arguments);",
                                                   "_state.First.Value = 1;", Packages));
        Assert.Equal(["Value"], finding.Resource.AccessPath);
    }

    [Fact]
    public async Task JsonConvert_SerializeObject_reads_deep_as_JsonSerializer_does()
    {
        var finding = Assert.Single(await Findings("Newtonsoft.Json.JsonConvert.SerializeObject(_state);", "_state.Count = 1;", Packages));

        Assert.Equal("DCA1001", finding.RuleId);
        Assert.Equal(["Count"], finding.Resource.AccessPath);
        AssertTwoSides(finding);
    }

    // ---- protection, ownership and the other side ----

    [Fact]
    public async Task Read_under_the_callers_lock_is_protected()
    {
        Assert.Empty(await Findings($"lock (_state) {{ {SERIALIZE} }}", "lock (_state) { _state.Count = 1; }"));
    }

    [Fact]
    public async Task Fresh_local_argument_is_owned_and_makes_no_pair()
    {
        var findings = await Findings("", "", controller: """
            [ApiController]
            [Route("items")]
            public sealed class ItemsController : ControllerBase
            {
                [HttpGet] public string Get() { var item = new Item(); item.Value = 1; return System.Text.Json.JsonSerializer.Serialize(item); }
            }
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task Deep_read_against_a_read_is_no_finding()
    {
        Assert.Empty(await Findings(SERIALIZE, "_ = _state.Count;"));
    }

    [Fact]
    public void Access_stands_at_the_call_in_the_member_that_makes_it()
    {
        var run = Run(SERIALIZE, "");
        var access = Assert.Single(AtCall(run), access => access.Resource.AccessPath is ["Count"]);

        Assert.StartsWith("Reader.ExecuteAsync", access.Symbol, StringComparison.Ordinal);
        Assert.Equal(ReaderLine(SERIALIZE), access.Source.StartLine);
    }

    // ---- helpers ----

    private static readonly FixtureOptions Packages = new() { MetadataReferences = LibraryPackages.BesideStubs };

    private static void AssertTwoSides(Finding finding)
    {
        Assert.NotEqual(finding.AccessA.Symbol, finding.AccessB.Symbol);
        Assert.Contains(new[] { finding.AccessA, finding.AccessB }, access => access.Symbol.StartsWith("Reader.", StringComparison.Ordinal));
    }

    /// <summary>The accesses the reader makes at its known call: everything it does on the line the call stands on.</summary>
    private static Access[] AtCall(EngineRun run) =>
        run.Collection.Accesses.Where(access => access.Symbol.StartsWith("Reader.", StringComparison.Ordinal) &&
                                                access.Source.StartLine == ReaderLine("") && access.Operation is AccessOperation.Read or AccessOperation.AtomicRead &&
                                                access.Resource.AccessPath is not (["_state"] or ["Log"]))
           .ToArray();

    private static string[] Shapes(EngineRun run) =>
        run.Collection.Accesses.Select(access => $"{access.Symbol} {access.Operation} {access.Resource.Region}.{string.Join('.', access.Resource.AccessPath)}")
           .Order(StringComparer.Ordinal)
           .ToArray();

    /// <summary>The line of the reader's work in <see cref="Source"/>, whatever the work is: it stands on a line of its own.</summary>
    private static int ReaderLine(string work) =>
        Source(work, "", "").Split('\n').Select((line, index) => (line, index)).First(pair => pair.line.Contains("// reader-work", StringComparison.Ordinal)).index + 1;

    private static async Task<IReadOnlyList<Finding>> Findings(string read, string write, FixtureOptions? options = null, string controller = "")
    {
        var solution = FixtureSolution.Create(options ?? Packages, ("Case.cs", Source(read, write, controller)));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);
        return result.Findings;
    }

    private static EngineRun Run(string read, string write, FixtureOptions? options = null) =>
        AnalyzeScope(FixtureSolution.Create(options ?? Packages, ("Case.cs", Source(read, write, ""))), "scope:Fixture");

    /// <summary>A singleton <c>State</c> one worker hands to a known call while another changes it.</summary>
    private static string Source(string read, string write, string controller) => Usings + $$"""
        using System.Collections.Generic;
        using System.Collections.Concurrent;
        using System.Linq;

        public sealed class Item { public int Value; }
        public sealed class Deep { public int Value; }
        public sealed class Inner { public Deep Deep = new(); }
        public class BaseState { public int Inherited; }
        public sealed class LoopA { public LoopB? Other; }
        public sealed class LoopB { public LoopA? Back; }
        public sealed class Level8 { public int Value; }
        public static class Holder { public static Level8 Shared = new(); }
        public sealed class Level7 { public Level8 Next = Holder.Shared; }
        public sealed class Level6 { public Level7 Next = new(); }
        public sealed class Level5 { public Level6 Next = new(); }
        public sealed class Level4 { public Level5 Next = new(); }
        public sealed class Level3 { public Level4 Next = new(); }
        public sealed class Level2 { public Level3 Next = new(); }
        public sealed class Level1 { public Level2 Next = new(); }
        public sealed class Chain { public Level1 Next = new(); }

        /// <summary>A query that is an object of the run's own: a deep read of it would read its field.</summary>
        public sealed class NumberQuery : IQueryable<int>
        {
            public int Seen;
            public Type ElementType => typeof(int);
            public System.Linq.Expressions.Expression Expression => null!;
            public IQueryProvider Provider => null!;
            public IEnumerator<int> GetEnumerator() => null!;
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null!;
        }

        public sealed class State : BaseState
        {
            public int Count;
            public string Text = "";
            public Uri Address = new("http://localhost/");
            public int Total { get; set; }
            public Inner Inner = new();
            public Item First = new();
            public Item Mapped = new();
            public List<Item> Items = new();
            public ConcurrentDictionary<int, Item> Map = new();
            public string[] Names = new string[4];
            public object[] Arguments = new object[1];
            public System.Text.StringBuilder Builder = new();
            public IQueryable<int> Query = new NumberQuery();
            public Chain Chain = new();
            public LoopA Loop = new();
            private int _hits;
            public int Hits { get { _hits++; return _hits; } }

            public State()
            {
                Items.Add(First);
                Map.TryAdd(1, Mapped);
                Arguments[0] = First;
                Loop.Other = new LoopB { Back = Loop };
            }
        }

        public sealed class Reader : BackgroundService
        {
            private static readonly Microsoft.Extensions.Logging.ILogger Log = null!;
            private readonly State _state;
            public Reader(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{read}} // reader-work
                return Task.CompletedTask;
            }
        }

        public sealed class Writer : BackgroundService
        {
            private readonly State _state;
            public Writer(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{write}}
                return Task.CompletedTask;
            }
        }

        {{controller}}
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Reader>(); services.AddHostedService<Writer>();");
}
