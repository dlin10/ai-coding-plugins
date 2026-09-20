using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A collection has two resources (ADR 0010): its structure, which is the path to the collection itself, and the storage
/// of its cells. Each modelled member touches both as the table says, a thread-safe collection performs each member atomically on
/// both, and a change decided by an earlier read of the same collection is one compound operation over whichever of the two the
/// dependency crosses.</summary>
public sealed class CollectionResourceTests
{
    [Fact]
    public void The_structure_and_the_element_storage_are_two_resources()
    {
        var run = Workers("Map.Add(\"a\", 1);", "Map.Add(\"a\", 2);");

        var accesses = Of(run, "Store.First()");
        Assert.Equal(["Map", "Map.[\"a\"]"], accesses.Select(Path).Order(StringComparer.Ordinal));
        Assert.Equal(2, accesses.Select(access => access.Resource.Identity).Distinct(StringComparer.Ordinal).Count());
        Assert.Single(accesses.Select(access => access.Resource.StructuralIdentity).Distinct(StringComparer.Ordinal));
    }

    /// <summary>Two keys proven apart are two cells and never meet, but both insertions rewrite the one structure.</summary>
    [Fact]
    public void Two_insertions_under_keys_that_differ_conflict_on_the_structure()
    {
        var pairs = Pairs(Workers("Map.Add(\"a\", 1);", "Map.Add(\"b\", 2);"));

        Assert.Contains("Map write/write", pairs);
        Assert.DoesNotContain(pairs, pair => pair.StartsWith("Map.[", StringComparison.Ordinal));
    }

    [Fact]
    public void A_lookup_of_one_key_against_an_insertion_of_another_conflicts_on_the_structure()
    {
        var pairs = Pairs(Workers("_ = Map[\"a\"];", "Map.Add(\"b\", 2);"));

        Assert.Contains("Map read/write", pairs);
        Assert.DoesNotContain(pairs, pair => pair.StartsWith("Map.[", StringComparison.Ordinal));
    }

    [Fact]
    public void A_lookup_against_an_overwrite_of_the_same_key_conflicts_on_the_cell() =>
        Assert.Contains("Map.[\"a\"] read/write", Pairs(Workers("_ = Map[\"a\"];", "Map[\"a\"] = 2;")));

    /// <summary>A removal writes the cell it removes, so a sequence that reads a cell and writes it back loses the removal.</summary>
    [Fact]
    public void A_compound_update_against_a_removal_of_the_same_key_conflicts_on_the_cell() =>
        Assert.Contains(Pairs(Workers("if (Safe.TryGetValue(\"a\", out var value)) Safe[\"a\"] = value + 1;", "Safe.TryRemove(\"a\", out _);")),
                        pair => pair.StartsWith("Safe.[\"a\"] ", StringComparison.Ordinal));

    [Fact]
    public void Clearing_the_collection_conflicts_with_a_read_of_a_cell() =>
        Assert.Contains("Map.[\"a\"] read/write", Pairs(Workers("Map.Clear();", "_ = Map[\"a\"];")));

    /// <summary>A member that shifts its neighbours changes cells it never named, so it takes the unknown cell, which meets every
    /// cell of the collection.</summary>
    [Fact]
    public void Removing_at_an_index_conflicts_with_a_compound_update_of_a_later_one() =>
        Assert.Contains(Pairs(Workers("Items.RemoveAt(0);", "Items[1] = Items[1] + 1;")),
                        pair => pair.StartsWith("Items.[1] ", StringComparison.Ordinal));

    [Fact]
    public void Inserting_conflicts_with_a_read_of_a_later_index() =>
        Assert.Contains("Items.[3] read/write", Pairs(Workers("Items.Insert(0, 5);", "_ = Items[3];")));

    /// <summary>A scan over the values reads every cell, unlike a key lookup, which never reaches one.</summary>
    [Fact]
    public void Scanning_a_list_for_a_value_conflicts_with_a_write_of_an_element() =>
        Assert.Contains("Items.[0] read/write", Pairs(Workers("_ = Items.Contains(5);", "Items[0] = 7;")));

    [Fact]
    public void A_key_lookup_does_not_conflict_with_a_write_of_that_key_on_the_cell()
    {
        var pairs = Pairs(Workers("_ = Map.ContainsKey(\"a\");", "Map[\"a\"] = 1;"));

        Assert.Contains("Map read/write", pairs);
        Assert.DoesNotContain(pairs, pair => pair.StartsWith("Map.[", StringComparison.Ordinal));
    }

    /// <summary>A list's indexer set moves the version counter every iterator watches, so it writes the structure as well.</summary>
    [Fact]
    public void Two_list_indexer_sets_at_indices_that_differ_conflict_on_the_structure()
    {
        var pairs = Pairs(Workers("Items[0] = 1;", "Items[1] = 2;"));

        Assert.Contains("Items write/write", pairs);
        Assert.DoesNotContain(pairs, pair => pair.StartsWith("Items.[", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_atomic_additions_conflict_on_neither_resource() =>
        Assert.Empty(Pairs(Workers("Safe.GetOrAdd(\"a\", 1);", "Safe.GetOrAdd(\"a\", 2);")));

    [Fact]
    public async Task An_atomic_addition_against_a_compound_update_of_that_key_is_DCA1004() =>
        Assert.Contains("DCA1004 Safe.[\"a\"]",
                        await Findings("Safe.GetOrAdd(\"a\", 1);", "if (!Safe.ContainsKey(\"a\")) Safe[\"a\"] = 2;"));

    [Fact]
    public async Task An_atomic_update_against_a_compound_update_of_that_key_is_DCA1004() =>
        Assert.Contains("DCA1004 Safe.[\"a\"]",
                        await Findings("Safe.AddOrUpdate(\"a\", 1, (key, value) => value + 1);",
                                       "if (!Safe.ContainsKey(\"a\")) Safe[\"a\"] = 2;"));

    /// <summary>An update in place changes no entry of the structure, only the value of one cell.</summary>
    [Fact]
    public void An_atomic_update_reads_the_structure_and_writes_the_cell()
    {
        var accesses = Of(Workers("Safe.TryUpdate(\"a\", 1, 0);", "Safe.TryUpdate(\"b\", 1, 0);"), "Store.First()");

        Assert.Equal("atomic-read", Single(accesses, "Safe").Operation.ToWireName());
        Assert.Equal("atomic-read-modify-write", Single(accesses, "Safe.[\"a\"]").Operation.ToWireName());
    }

    [Fact]
    public void A_sequence_whose_check_and_change_prove_one_key_sits_on_the_cell()
    {
        var accesses = Of(Action("if (!Map.ContainsKey(\"a\")) Map[\"a\"] = 1;"), "Store.Act()");

        Assert.Equal("Map.[\"a\"]", Path(Assert.Single(accesses, access => access.Operation == AccessOperation.CompoundOperation)));
    }

    /// <summary>A dependency that runs between two cells is only contained by the structure, so that is where it is reported, and
    /// two such sequences under crossed keys make a pair although no cell of either is the other's.</summary>
    [Fact]
    public void Sequences_whose_check_and_change_name_different_keys_sit_on_the_structure_and_pair()
    {
        var run = Workers("if (!Map.ContainsKey(\"a\")) Map[\"b\"] = 1;", "if (!Map.ContainsKey(\"b\")) Map[\"a\"] = 1;");

        Assert.Equal("Map", Path(Assert.Single(Of(run, "Store.First()"), access => access.Operation == AccessOperation.CompoundOperation)));
        Assert.Contains("Map compound-operation/compound-operation", Pairs(run));
    }

    [Fact]
    public void Enumerating_a_thread_safe_dictionary_while_it_is_written_conflicts_with_nothing() =>
        Assert.Empty(Pairs(Workers("foreach (var entry in Safe) _ = entry.Value;", "Safe[\"a\"] = 1;")));

    [Fact]
    public async Task Enumerating_a_plain_dictionary_while_it_is_written_is_DCA1001() =>
        Assert.Contains("DCA1001 Map", await Findings("foreach (var entry in Map) _ = entry.Value;", "Map.Add(\"a\", 1);"));

    [Fact]
    public async Task A_key_check_before_an_indexer_set_is_DCA1004_on_the_cell() =>
        Assert.Contains("DCA1004 Safe.[\"a\"]", await ActionFindings("if (!Safe.ContainsKey(\"a\")) Safe[\"a\"] = 1;"));

    /// <summary>A count decides an addition, and only the structure holds both ends of that dependency.</summary>
    [Fact]
    public async Task A_count_before_an_addition_is_DCA1004_on_the_structure() =>
        Assert.Contains("DCA1004 Queue", await ActionFindings("if (Queue.Count < 4) Queue.Enqueue(1);"));

    [Fact]
    public async Task An_indexer_increment_is_DCA1004_on_the_cell() =>
        Assert.Contains("DCA1004 Safe.[\"a\"]", await ActionFindings("Safe[\"a\"] = Safe[\"a\"] + 1;"));

    /// <summary>Protection of a sequence is the rule of R2: only a section that covers the check and the change protects it.</summary>
    [Fact]
    public void A_lock_around_the_change_alone_does_not_protect_the_sequence() =>
        Assert.Equal([PairProtection.UNPROTECTED],
                     Protections(Action("if (!Safe.ContainsKey(\"a\")) lock (Gate) { Safe[\"a\"] = 1; }"), "Safe.[\"a\"]"));

    [Fact]
    public void Two_sections_around_the_two_steps_do_not_protect_the_sequence() =>
        Assert.Equal([PairProtection.UNPROTECTED],
                     Protections(Action("""
                         bool missing;
                         lock (Gate) { missing = !Safe.ContainsKey("a"); }
                         if (missing) lock (Gate) { Safe["a"] = 1; }
                         """), "Safe.[\"a\"]"));

    [Fact]
    public void One_section_around_the_whole_sequence_protects_it() =>
        Assert.Empty(Protections(Action("lock (Gate) { if (!Safe.ContainsKey(\"a\")) Safe[\"a\"] = 1; }"), "Safe.[\"a\"]"));

    /// <summary>On a collection that is atomic on neither resource the sequence is still one compound operation on the cell it
    /// changes, while the structure it also rewrites keeps the ordinary write of an insertion.</summary>
    [Fact]
    public void A_sequence_on_a_plain_dictionary_is_compound_on_the_cell_and_an_ordinary_write_on_the_structure()
    {
        var accesses = Of(Action("if (!Map.ContainsKey(\"a\")) Map[\"a\"] = 1;"), "Store.Act()");

        Assert.Equal("compound-operation", Single(accesses, "Map.[\"a\"]").Operation.ToWireName());
        Assert.Equal(["read", "write"], accesses.Where(access => Path(access) == "Map").Select(access => access.Operation.ToWireName())
                                                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_sequence_is_reported_where_it_changes_the_collection_and_carries_the_read_it_depends_on()
    {
        var accesses = Of(Action("""
            if (!Map.ContainsKey("a"))
                        Map["a"] = 1;
            """), "Store.Act()");

        var compound = Assert.Single(accesses, access => access.Operation == AccessOperation.CompoundOperation);
        var check = Assert.Single(accesses, access => Path(access) == "Map" && access.Operation == AccessOperation.Read);
        var read = Assert.Single(compound.ReadSources);
        Assert.Equal(check.Source.StartLine, read.Source.StartLine);
        Assert.True(compound.Source.StartLine > check.Source.StartLine,
                    $"The sequence is reported at line {compound.Source.StartLine}, not after the check at {check.Source.StartLine}.");
    }

    /// <summary>A read takes no update away, however many steps the other side needs: only a change can be lost and only a
    /// change can take one, so a sequence racing a reader of the same cell is the plain conflict it is (TD-072).</summary>
    [Fact]
    public async Task A_sequence_against_an_atomic_read_of_that_cell_is_DCA1001() =>
        Assert.Contains("DCA1001 Safe.[\"a\"]",
                        await Findings("if (!Safe.ContainsKey(\"a\")) Safe[\"a\"] = 1;", "Safe.TryGetValue(\"a\", out _);"));

    /// <summary>A sequence against a plain write is the lost update it plainly is, whatever the collection guarantees.</summary>
    [Fact]
    public async Task A_sequence_against_a_plain_write_of_that_cell_is_a_lost_update() =>
        Assert.Contains("DCA1002 Map.[\"a\"]", await Findings("if (!Map.ContainsKey(\"a\")) Map[\"a\"] = 1;", "Map[\"a\"] = 2;"));

    [Fact]
    public async Task A_sequence_one_side_of_which_holds_a_lock_stays_DCA1004()
    {
        var findings = await Findings("lock (Gate) { if (!Safe.ContainsKey(\"a\")) Safe[\"a\"] = 1; }",
                                      "if (!Safe.ContainsKey(\"a\")) Safe[\"a\"] = 2;");

        Assert.Contains("DCA1004 Safe.[\"a\"]", findings);
        Assert.DoesNotContain("DCA1003 Safe.[\"a\"]", findings);
    }

    [Fact]
    public void An_addition_to_a_list_from_one_action_pairs_with_itself_on_the_structure()
    {
        var run = Action("Items.Add(1);");

        Assert.Contains("Items write/write", Pairs(run));
        var pair = Assert.Single(run.Pairs.Pairs, candidate => Path(candidate.First) == "Items");
        Assert.Equal(pair.First.Symbol, pair.Second.Symbol);
    }

    [Fact]
    public void The_default_comparer_tells_two_keys_apart() =>
        Assert.Empty(Pairs(Workers("if (Safe.TryGetValue(\"a\", out var value)) Safe[\"a\"] = value + 1;",
                                   "if (Safe.TryGetValue(\"b\", out var value)) Safe[\"b\"] = value + 1;")));

    [Fact]
    public void A_comparer_that_ignores_case_makes_two_keys_one_cell() =>
        Assert.Contains("Cased.[\"a\"] compound-operation/compound-operation",
                        Pairs(Workers("if (Cased.TryGetValue(\"a\", out var value)) Cased[\"a\"] = value + 1;",
                                      "if (Cased.TryGetValue(\"A\", out var value)) Cased[\"A\"] = value + 1;")));

    /// <summary>A culture-sensitive comparer decides by collation, and collation makes characters ignorable: under
    /// `InvariantCulture` a key and the same key with a soft hyphen in it are one cell, so two literals that differ prove
    /// nothing and the sequences over them still meet (R7).</summary>
    [Fact]
    public void A_culture_comparer_proves_no_two_keys_apart()
    {
        var run = Workers("if (Cultured.TryGetValue(\"a\", out var value)) Cultured[\"a\"] = value + 1;",
                          "if (Cultured.TryGetValue(\"b\", out var value)) Cultured[\"b\"] = value + 1;");

        Assert.Contains("Cultured.[?] compound-operation/compound-operation", Pairs(run));
        Assert.Contains(ConflictFindings.KEY_EQUALITY_UNCERTAINTY,
                        run.Collection.Accesses.SelectMany(access => access.Uncertainties));
    }

    [Fact]
    public void A_comparer_the_analysis_cannot_read_proves_no_two_keys_apart()
    {
        var run = Workers("if (Custom.TryGetValue(\"a\", out var value)) Custom[\"a\"] = value + 1;",
                          "if (Custom.TryGetValue(\"b\", out var value)) Custom[\"b\"] = value + 1;");

        Assert.Contains("Custom.[?] compound-operation/compound-operation", Pairs(run));
        Assert.Contains(ConflictFindings.KEY_EQUALITY_UNCERTAINTY,
                        run.Collection.Accesses.SelectMany(access => access.Uncertainties));
    }

    [Fact]
    public void An_enqueue_against_a_dequeue_conflicts_with_nothing() =>
        Assert.Empty(Pairs(Workers("Queue.Enqueue(1);", "Queue.TryDequeue(out _);")));

    /// <summary>The table names the collections it models; a member of any other type stays an ordinary call, which touches
    /// neither resource.</summary>
    [Fact]
    public void A_collection_the_table_does_not_name_has_neither_resource()
    {
        var run = Workers("Hashes.Add(1);", "Hashes.Add(2);");

        Assert.DoesNotContain(run.Collection.Accesses, access => Path(access).StartsWith("Hashes.", StringComparison.Ordinal));
        Assert.All(Of(run, "Store.First()"), access => Assert.Equal(AccessOperation.Read, access.Operation));
        Assert.Empty(Pairs(run));
    }

    /// <summary>A collection is the object it is, whatever a field calls it: two fields holding one dictionary hold one
    /// dictionary, and accesses through them meet on it (ADR 0010).</summary>
    [Fact]
    public async Task Two_fields_holding_one_dictionary_are_one_resource()
    {
        var findings = await Findings("Map.Add(\"a\", 1);", "Alias.Add(\"a\", 2);");

        Assert.Contains("DCA1001 Map", findings);
    }

    /// <summary>A member that only reads the structure still makes a sequence the structure is the container of: an update in
    /// place checked against another key crosses cells, and only the whole collection holds both ends of it (ADR 0010).</summary>
    [Fact]
    public async Task A_sequence_whose_update_reads_the_structure_and_checks_another_key_sits_on_the_structure()
    {
        var findings = await Findings("if (Safe.ContainsKey(\"a\")) Safe.TryUpdate(\"b\", 1, 0);",
                                      "if (Safe.ContainsKey(\"b\")) Safe.TryUpdate(\"a\", 1, 0);");

        Assert.Contains("DCA1004 Safe", findings);
    }

    /// <summary>Two members are one sequence when the collections they may work on meet: a receiver that may be either of two
    /// objects may be the one the other member holds, and not knowing which is no proof that they are different collections —
    /// the rule that closed the resource identity, applied where the sequence is formed (ADR 0010).</summary>
    [Fact]
    public void A_check_on_an_unproven_receiver_and_a_change_on_one_of_its_objects_are_one_sequence()
    {
        // Its own fixture: adding shelves to the shared one would give every other case two more collections to account for.
        var run = Analyze(CollectionUsings + """
            public sealed class Shelf { public readonly Dictionary<string, int> Map = new(); }

            public sealed class Shelves
            {
                public readonly Shelf First = new();
                public readonly Shelf Second = new();
                public bool Flag;

                public void Act()
                {
                    var either = Flag ? First : Second;
                    if (!either.Map.ContainsKey("a"))
                        First.Map["a"] = 1;
                }
            }

            public class ShelfController : ControllerBase
            {
                private readonly Shelves _shelves;
                public ShelfController(Shelves shelves) => _shelves = shelves;
                public void Post() => _shelves.Act();
            }
            """ + Startup("services.AddSingleton<Shelves>();"));

        Assert.Contains(run.Collection.Accesses, access => access.Symbol == "Shelves.Act()" &&
                                                           access.Operation == AccessOperation.CompoundOperation);
    }

    /// <summary>A field that may hold either of two dictionaries is an access on each of them, exactly as a base that may point
    /// to either of two objects is an access on each. Not knowing which one a field holds is no proof that it is not the one the
    /// other field holds (ADR 0010).</summary>
    [Fact]
    public async Task A_field_that_may_hold_either_collection_still_meets_the_one_that_holds_it()
    {
        var findings = await Findings("Selected.Add(\"a\", 1);", "Alias.Add(\"a\", 2);");

        Assert.Contains("DCA1001 Selected", findings);
    }

    [Fact]
    public async Task Two_fields_holding_one_array_are_one_resource()
    {
        var findings = await Findings("Cells[0] = 1;", "Mirror[0] = 2;");

        Assert.Contains(findings, finding => finding.EndsWith("Cells.[0]", StringComparison.Ordinal));
    }

    /// <summary>The sequence belongs to the resource every one of its reads is contained by: a count crosses every cell, so a
    /// check that reads one is still a sequence over the whole collection, and two of them under different keys pair on it.</summary>
    [Fact]
    public void A_sequence_whose_checks_cross_cells_sits_on_the_structure()
    {
        var run = Workers("if (Map.Count < 4 && !Map.ContainsKey(\"a\")) Map[\"a\"] = 1;",
                          "if (Map.Count < 4 && !Map.ContainsKey(\"b\")) Map[\"b\"] = 1;");

        Assert.Equal("Map", Path(Assert.Single(Of(run, "Store.First()"), access => access.Operation == AccessOperation.CompoundOperation)));
        Assert.Contains("Map compound-operation/compound-operation", Pairs(run));
    }

    private static string Path(Access access) => string.Join(".", access.Resource.AccessPath);

    private static string Path(AccessPair pair) => string.Join(".", pair.Resource.AccessPath);

    /// <summary>The pairs a run reports as resource and the two operations, ordered so that neither side's place matters.</summary>
    private static IReadOnlyList<string> Pairs(EngineRun run) =>
        run.Pairs.Pairs.Select(pair => $"{Path(pair)} " +
                                       string.Join("/", new[] { pair.First.Operation.ToWireName(), pair.Second.Operation.ToWireName() }
                                                            .Order(StringComparer.Ordinal)))
           .Order(StringComparer.Ordinal)
           .ToArray();

    /// <summary>The protection verdicts of the pairs on one resource; empty when the resource makes no pair at all.</summary>
    private static IReadOnlyList<string> Protections(EngineRun run, string path) =>
        run.Pairs.Pairs.Where(pair => Path(pair) == path).Select(pair => pair.Protection).Distinct(StringComparer.Ordinal)
           .Order(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<Access> Of(EngineRun run, string symbol) =>
        run.Collection.Accesses.Where(access => access.Symbol == symbol && !access.IsConstructionLocal).ToArray();

    private static Access Single(IReadOnlyList<Access> accesses, string path) => Assert.Single(accesses, access => Path(access) == path);

    private const string Registrations =
        "services.AddSingleton<Store>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();";

    private const string Fields = """
            public readonly object Gate = new();
            public readonly Dictionary<string, int> Map = new();
            public readonly Dictionary<string, int> Alias;
            public readonly Dictionary<string, int> Other = new();
            public readonly Dictionary<string, int> Selected;
            public readonly int[] Cells = new int[4];
            public readonly int[] Mirror;
            public readonly ConcurrentDictionary<string, int> Safe = new();
            public readonly ConcurrentDictionary<string, int> Cased = new(StringComparer.OrdinalIgnoreCase);
            public readonly ConcurrentDictionary<string, int> Cultured = new(StringComparer.InvariantCulture);
            public readonly ConcurrentDictionary<string, int> Custom = new(new FirstLetterComparer());
            public readonly List<int> Items = new();
            public readonly ConcurrentQueue<int> Queue = new();
            public readonly HashSet<int> Hashes = new();
        """;

    private const string Comparer = """
        public sealed class FirstLetterComparer : IEqualityComparer<string>
        {
            public bool Equals(string? first, string? second) => first?[..1] == second?[..1];
            public int GetHashCode(string value) => value[..1].GetHashCode(StringComparison.Ordinal);
        }

        """;

    private const string CollectionUsings = """
        using System.Collections.Concurrent;
        using System.Collections.Generic;

        """;

    /// <summary>Two workers of one singleton, each running its own member of it.</summary>
    private static EngineRun Workers(string first, string second) => Analyze(WorkerSource(first, second));

    private static string WorkerSource(string first, string second) => CollectionUsings + Comparer + $$"""
        public sealed class Store
        {
        {{Fields}}

            public Store()
            {
                Alias = Map;
                Mirror = Cells;
                Selected = Environment.ProcessorCount > 1 ? Map : Other;
            }

            public void First()
            {
                {{first}}
            }

            public void Second()
            {
                {{second}}
            }
        }

        public sealed class FirstWorker : BackgroundService
        {
            private readonly Store _store;
            public FirstWorker(Store store) => _store = store;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                _store.First();
                return Task.CompletedTask;
            }
        }

        public sealed class SecondWorker : BackgroundService
        {
            private readonly Store _store;
            public SecondWorker(Store store) => _store = store;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                _store.Second();
                return Task.CompletedTask;
            }
        }
        """ + Startup(Registrations);

    /// <summary>One action of a controller, which may run against itself.</summary>
    private static EngineRun Action(string body) => Analyze(ActionSource(body));

    private static string ActionSource(string body) => CollectionUsings + Comparer + $$"""
        public sealed class Store
        {
        {{Fields}}

            public Store()
            {
                Alias = Map;
                Mirror = Cells;
                Selected = Environment.ProcessorCount > 1 ? Map : Other;
            }

            public void Act()
            {
                {{body}}
            }
        }

        public class ActionController : ControllerBase
        {
            private readonly Store _store;
            public ActionController(Store store) => _store = store;
            public void Post() => _store.Act();
        }
        """ + Startup("services.AddSingleton<Store>();");

    private static async Task<IReadOnlyList<string>> Findings(string first, string second) =>
        await Findings(WorkerSource(first, second));

    private static async Task<IReadOnlyList<string>> ActionFindings(string body) => await Findings(ActionSource(body));

    /// <summary>The findings of a fixture as rule and resource.</summary>
    private static async Task<IReadOnlyList<string>> Findings(string source)
    {
        var result = await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Usings + source)), ROOT_DIRECTORY,
                                                         ProviderRegistry.BuiltIn, CancellationToken.None);
        return result.Findings.Select(finding => $"{finding.RuleId} {string.Join(".", finding.Resource.AccessPath)}")
                     .Order(StringComparer.Ordinal).ToArray();
    }
}
