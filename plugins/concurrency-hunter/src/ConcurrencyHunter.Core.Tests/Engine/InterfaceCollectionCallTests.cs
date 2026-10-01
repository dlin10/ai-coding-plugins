using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A call through an interface on a collection of the table, a view, a snapshot or an array is decided by the object its receiver
/// points to (question 31, R4, R5): on each object the heap knows, it is what a direct call of the member the object's type implements
/// the interface member with is, and it stays unresolved, with its unknown effect and its gap, on every other object.</summary>
public sealed class InterfaceCollectionCallTests
{
    [Fact]
    public void Allocated_list_snapshot_and_live_view_are_each_decided_by_what_they_are()
    {
        var run = Analyze(ActionSource("_ = Mixed.Count;"));

        var reads = OnCollections(run).Where(access => access.Resource.Selector is null && access.Operation == AccessOperation.Read).ToArray();
        Assert.Equal(["Map", "Mixed", "Names"], reads.Select(access => Path(access.Resource)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Equal(3, reads.Select(access => access.Resource.CollectionId).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void Snapshot_and_live_view_decide_only_Count_and_enumeration()
    {
        var run = Analyze(ActionSource("Mixed.Add(\"b\"); _ = Mixed.Contains(\"a\");"));

        var names = Assert.Single(OnCollections(run).Where(access => Path(access.Resource) == "Names").Select(access => access.Resource.CollectionId).Distinct());
        Assert.Contains(OnCollections(run), access => access.Resource.CollectionId == names && access.Operation == AccessOperation.Write);
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Resource.CollectionId == names && access.Operation.IsUnknownEffect());
        // The snapshot and the dictionary the live view stands for keep the unknown effect of both calls, which stay gaps.
        Assert.Equal(2, run.Collection.Accesses.Where(access => access.Operation.IsUnknownEffect() && access.Resource.CollectionId is not null)
                           .Select(access => access.Resource.CollectionId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Callee.Contains(".Add(", StringComparison.Ordinal));
        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Callee.Contains(".Contains(", StringComparison.Ordinal));
    }

    [Fact]
    public void Count_through_IReadOnlyCollection_reads_only_the_structure()
    {
        var run = Analyze(ActionSource("_ = ReadOnly.Count;"));

        Assert.Equal(["Items read structure"], Describe(OnCollections(run)));
        Assert.Equal(OnCollectionIdentities(Analyze(ActionSource("_ = Items.Count;"))), OnCollectionIdentities(run));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void Add_through_ICollection_writes_the_structure_and_a_cell() =>
        Assert.Equal(["Items write structure", "Items.[?] write cell"], AssertSameAsDirect("AsCollection.Add(new Item());", "Items.Add(new Item());"));

    [Fact]
    public void Indexer_through_IList_reads_and_writes_the_cell_it_names()
    {
        var accesses = AssertSameAsDirect("AsList[0] = AsList[0];", "Items[0] = Items[0];");

        Assert.Contains("Items.[0] compound-operation cell", accesses);
        Assert.Contains("Items write structure", accesses);
    }

    [Fact]
    public void TryGetValue_through_IDictionary_hands_out_the_value()
    {
        var run = Analyze(ActionSource("if (AsDictionary.TryGetValue(\"a\", out var item)) item.Value = 1;"));

        Assert.Single(run.Collection.Accesses, access => Path(access.Resource) == "Value" && access.Operation == AccessOperation.Write);
        Assert.Equal(Everything(Analyze(ActionSource("if (Lookup.TryGetValue(\"a\", out var item)) item.Value = 1;"))), Everything(run));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
    }

    /// <summary>With a type of the run's own implementing the interface the call is a dispatch, and a list it may be on was an unresolved
    /// receiver with an unknown effect and a gap until the object decided it.</summary>
    [Fact]
    public void Foreach_over_IEnumerable_on_the_dispatch_route_has_no_unknown_effect()
    {
        var run = Analyze(ActionSource("foreach (var item in Sequence) item.Value = 1;", Chain));

        // The enumeration reads the list's cells where the field the loop took it from names them.
        Assert.Contains(run.Collection.Accesses, access => Path(access.Resource) == "Value" && access.Operation == AccessOperation.Write);
        Assert.Contains(OnCollections(run), access => access.Resource.Selector == ElementSelector.Unknown && access.Operation == AccessOperation.Read &&
                                                      access.Resource.CollectionId!.Contains("List<Fixture:Item>", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void Explicit_GetEnumerator_is_the_public_GetEnumerator()
    {
        var accesses = AssertSameAsDirect("_ = Sequence.GetEnumerator();", "_ = Items.GetEnumerator();");

        Assert.Equal(["Items read structure", "Items.[?] read cell"], accesses);
    }

    [Fact]
    public void Pair_Add_through_ICollection_of_pairs_is_Add_at_an_unnamed_cell()
    {
        var run = Analyze(ActionSource("Pairs.Add(new KeyValuePair<string, int>(\"b\", 1));"));

        Assert.Equal(["Map write structure", "Map.[?] write cell"], Describe(OnCollections(run)));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void Keys_through_IDictionary_is_the_live_view()
    {
        var accesses = AssertSameAsDirect("_ = AsMap.Keys.Count;", "_ = Map.Keys.Count;");

        Assert.Equal(["Map read structure"], accesses);
    }

    [Fact]
    public void Snapshot_kept_in_a_field_is_counted_as_its_own_list()
    {
        var run = Analyze(ActionSource("Kept = Concurrent.Keys; _ = Kept.Count;"));

        var count = Assert.Single(OnCollections(run), access => Path(access.Resource) == "Kept");
        Assert.Equal((AccessOperation.Read, (ElementSelector?)null), (count.Operation, count.Resource.Selector));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void Snapshot_handed_on_as_ICollection_is_counted_as_its_own_list()
    {
        var run = Analyze(ActionSource("Kept = Concurrent.Keys; _ = CountOf(Kept); _ = CountOf(Concurrent.Keys);"));

        var count = Assert.Single(OnCollections(run), access => access.Symbol == "Store.CountOf(ICollection<string>)");
        Assert.Equal(("Kept", AccessOperation.Read, (ElementSelector?)null), (Path(count.Resource), count.Operation, count.Resource.Selector));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void Live_view_cast_to_ICollection_counts_its_dictionary()
    {
        var run = Analyze(ActionSource("_ = ((ICollection<string>)Map.Keys).Count;"));

        Assert.Equal(["Map read structure"], Describe(OnCollections(run)));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void Interface_member_the_table_does_not_model_stays_opaque()
    {
        var run = Analyze(ActionSource("var target = new Item[4]; AsCollection.CopyTo(target, 0);"));

        Assert.Contains(run.Collection.Accesses, access => access.Operation.IsUnknownEffect() && Path(access.Resource).StartsWith("Items", StringComparison.Ordinal));
        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Callee.Contains(".CopyTo(", StringComparison.Ordinal) &&
                                                             gap.Kind == SemanticGapKinds.UNKNOWN_LIBRARY);
    }

    [Fact]
    public void Array_indexer_through_IList_is_the_element_access()
    {
        var accesses = AssertSameAsDirect("CellList[1] = CellList[1] + 1;", "Cells[1] = Cells[1] + 1;");

        Assert.Equal(["Cells.[1] read-modify-write cell"], accesses);
    }

    [Fact]
    public void Array_Count_through_ICollection_makes_no_access() => AssertNothingOnCells("_ = CellCollection.Count; _ = CellCollection.IsReadOnly;");

    [Fact]
    public void Array_Add_through_ICollection_makes_no_access() =>
        AssertNothingOnCells("CellCollection.Add(1); CellCollection.Remove(1); CellCollection.Clear(); CellList.Insert(0, 1); CellList.RemoveAt(0);");

    [Fact]
    public void Array_Contains_through_ICollection_reads_every_cell()
    {
        var run = Analyze(ActionSource("_ = CellCollection.Contains(1); _ = CellList.IndexOf(1);"));

        // As a list's Contains and a foreach over the array read it: the structure and every cell.
        Assert.Equal(["Cells read structure", "Cells.[?] read cell"], Describe(OnCollections(run)));
        Assert.Equal(OnCollectionIdentities(Analyze(ActionSource("foreach (var cell in Cells) { }"))), OnCollectionIdentities(run));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void Array_CopyTo_through_ICollection_is_the_direct_call()
    {
        var run = Analyze(ActionSource("ThingCollection.CopyTo(Copies, 0);"));
        var direct = Analyze(ActionSource("Things.CopyTo(Copies, 0);"));

        // Both forms copy the elements and write the destination's cells, while reading the receiver's cells.
        foreach (var form in new[] { run, direct })
        {
            Assert.Contains(form.Collection.Accesses, access => access.Operation == AccessOperation.Write && Path(access.Resource) == "Copies.[?]");
            Assert.Contains(form.Collection.Accesses, access => access.Operation == AccessOperation.Read && Path(access.Resource) == "Things.[?]");
            Assert.DoesNotContain(form.Collection.Accesses, access => access.Operation.IsUnknownEffect());
            Assert.Empty(form.Collection.Coverage.Gaps);
        }

        Assert.Equal(Everything(direct), Everything(run));
    }

    [Fact]
    public void Multidimensional_array_through_IList_is_refused_but_enumerated()
    {
        var source = ActionSource("""
            _ = GridList[0];
            _ = GridList.Contains(1);
            _ = GridList.IndexOf(1);
            _ = GridList.Count;
            foreach (var cell in GridList) { }
            """);
        var run = Analyze(source);

        var onGrid = OnCollections(run).Where(access => access.Resource.CollectionId!.Contains("int[,]", StringComparison.Ordinal)).ToArray();
        Assert.Contains(onGrid, access => access.Resource.Selector == ElementSelector.Unknown && access.Operation == AccessOperation.Read);
        Assert.All(onGrid, access => Assert.Equal(Line(source, "foreach (var cell in GridList)"), access.Source.StartLine));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void Receiver_that_may_be_a_list_or_a_source_implementation_runs_both()
    {
        var run = Analyze(ActionSource("Either.Add(new Item());", Bag));

        Assert.Contains(OnCollections(run), access => Path(access.Resource) == "Items" && access.Operation == AccessOperation.Write);
        Assert.Contains(run.Collection.Accesses, access => Path(access.Resource) == "Last" && access.Symbol == "Bag.Add(Item)");
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void Receiver_that_may_be_a_list_or_an_unmodelled_collection_keeps_the_unknown_effect_on_the_other_only()
    {
        var run = Analyze(ActionSource("ListOrSorted.Add(Shared);"));

        AssertListDecidedAndSortedSetUnknown(run);
        var gap = Assert.Single(run.Collection.Coverage.Gaps);
        Assert.Equal(SemanticGapKinds.UNKNOWN_LIBRARY, gap.Kind);
        Assert.Contains(".Add(", gap.Callee, StringComparison.Ordinal);
        Assert.Equal(1, gap.CallSites);
    }

    [Fact]
    public void Receiver_that_may_be_a_list_or_an_unmodelled_collection_keeps_the_unknown_effect_on_the_other_only_on_the_dispatch_route()
    {
        var run = Analyze(ActionSource("ListSortedOrBag.Add(Shared);", Bag));

        AssertListDecidedAndSortedSetUnknown(run);
        Assert.Contains(run.Collection.Accesses, access => Path(access.Resource) == "Last" && access.Symbol == "Bag.Add(Item)");
        var gap = Assert.Single(run.Collection.Coverage.Gaps);
        Assert.Equal(SemanticGapKinds.UNRESOLVED_DISPATCH, gap.Kind);
        Assert.Contains(".Add(", gap.Callee, StringComparison.Ordinal);
    }

    [Fact]
    public void Receiver_with_no_object_stays_unresolved_with_its_gap()
    {
        var run = Analyze(ActionSource("Missing!.Add(Shared);"));

        Assert.Empty(OnCollections(run));
        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Callee.Contains(".Add(", StringComparison.Ordinal) &&
                                                             gap.Kind == SemanticGapKinds.UNKNOWN_LIBRARY);
    }

    [Fact]
    public void Decided_call_makes_no_gap_and_counts_as_the_direct_call()
    {
        var run = Analyze(ActionSource("_ = ReadOnly.Count;"));
        var direct = Analyze(ActionSource("_ = Items.Count;"));

        Assert.Equal(0, run.Counter(CoverageCounters.SEMANTIC_GAP));
        foreach (var counter in new[] { CoverageCounters.OPAQUE_CALL, CoverageCounters.KNOWN_CALL, CoverageCounters.SEMANTIC_GAP, CoverageCounters.NO_RECEIVER_OBJECT })
            Assert.Equal(direct.Counter(counter), run.Counter(counter));
    }

    [Fact]
    public void Source_type_deriving_from_List_runs_the_table_member_it_inherits()
    {
        var accesses = AssertSameAsDirect("TeamCollection.Add(new Item());", "Team.Add(new Item());");

        Assert.Equal(["Team write structure", "Team.[?] write cell"], accesses);
    }

    /// <summary>The list is exactly its table member and has no unknown effect; the sorted set, whose own state is no resource, keeps
    /// the unknown effect, which reaches the object it is handed.</summary>
    /// <summary>A pair added through <c>ICollection&lt;KeyValuePair&lt;TKey, TValue&gt;&gt;</c> puts its key among the dictionary's keys and its
    /// value into its cells, as <c>Add(key, value)</c> does, and never the pair itself (review F-0010).</summary>
    [Fact]
    public void Pair_Add_through_ICollection_of_pairs_holds_the_key_and_the_value()
    {
        const string then = " foreach (var held in Tags.Keys) held.V = 1; Tags[Probe].Value = 2;";
        var run = Analyze(ActionSource("var tag = new Tag(); var item = new Item(); TagPairs.Add(new KeyValuePair<Tag, Item>(tag, item));" + then));
        var direct = Analyze(ActionSource("var tag = new Tag(); var item = new Item(); Tags.Add(tag, item);" + then));

        Assert.Contains(Writes(run), write => write == "alloc:Store.Act()#Tag V");
        Assert.Contains(Writes(run), write => write == "alloc:Store.Act()#Item Value");
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Resource.Region.Contains("KeyValuePair", StringComparison.Ordinal));
        Assert.Equal(Writes(direct), Writes(run));
    }

    /// <summary>An object of a type of the run's own that implements the interface member itself runs that body alone, whatever collection
    /// of the table it derives from (review F-0011).</summary>
    [Fact]
    public void Source_type_reimplementing_the_interface_runs_its_own_body_alone()
    {
        var run = Analyze(ActionSource("CustomCollection.Add(Shared);", Custom));

        Assert.Contains(run.Collection.Accesses, access => Path(access.Resource) == "Last" && access.Operation == AccessOperation.Write);
        Assert.DoesNotContain(OnCollections(run), access => access.Resource.CollectionId!.Contains(":Custom|", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    /// <summary>The view an interface call hands out is the one the object's own member hands out: a <c>Dictionary</c> has no snapshot to
    /// count (review F-0012).</summary>
    [Fact]
    public void Values_through_IDictionary_of_a_Dictionary_is_its_live_view_alone()
    {
        var accesses = AssertSameAsDirect("KeptValues = AsDictionary.Values; _ = KeptValues.Count;", "KeptValues = Lookup.Values; _ = KeptValues.Count;");

        Assert.Equal(["Lookup read structure"], accesses);
    }

    /// <summary>The indexer of a snapshot stays undecided: it hands out nothing a write could land on, and is a gap (review F-0013).</summary>
    [Fact]
    public void Snapshot_indexer_through_IList_hands_out_nothing()
    {
        var run = Analyze(ActionSource("var values = (IList<Item>)Held.Values; values[0].Value = 1;"));

        Assert.DoesNotContain(run.Collection.Accesses, access => Path(access.Resource) == "Value" && access.Operation == AccessOperation.Write);
        Assert.NotEmpty(run.Collection.Coverage.Gaps);
    }

    /// <summary>A member an array refuses by throwing holds nothing and hands out nothing (review F-0014).</summary>
    [Fact]
    public void Member_an_array_refuses_holds_and_hands_out_nothing()
    {
        var added = Analyze(ActionSource("var fresh = new Item(); ThingCollection.Add(fresh); fresh.Value = 1;"));
        var indexed = Analyze(ActionSource("(ItemGridList[0] as Item)!.Value = 1;"));

        Assert.DoesNotContain(added.Collection.Accesses, access => Path(access.Resource) == "Value" && access.Ownership == OwnershipKind.Escaped);
        Assert.DoesNotContain(indexed.Collection.Accesses, access => Path(access.Resource) == "Value" && access.Operation == AccessOperation.Write);
    }

    /// <summary>A write of an array's element through an interface is fed by every read it depends on, as the same write on the array
    /// directly is: a read of the cell in the body, and one a helper makes (review F-0016).</summary>
    [Fact]
    public void Array_indexer_write_through_IList_folds_the_reads_it_depends_on()
    {
        Assert.Equal(["Cells.[0] read-modify-write cell"], AssertSameAsDirect("CellList[0] = Cells[0] + 1;", "Cells[0] = Cells[0] + 1;"));
        Assert.Equal(["Cells.[0] read-modify-write cell"], AssertSameAsDirect("CellList[0] = ReadCell() + 1;", "Cells[0] = ReadCell() + 1;"));
    }

    /// <summary>An index a helper takes as a parameter names, through the interface, the cell its caller's argument names (review F-0017).</summary>
    [Fact]
    public void Array_indexer_through_IList_in_a_helper_names_the_cell_its_argument_gives() =>
        Assert.Equal(["Cells.[0] read-modify-write cell"], AssertSameAsDirect("BumpAt(Cells, 0);", "BumpAtArray(Cells, 0);"));

    /// <summary>Reading a mutable field that holds an array is an access of its own, through an interface as on the array directly (review
    /// F-0018).</summary>
    [Fact]
    public void Array_indexer_through_IList_keeps_the_read_of_the_field_holding_the_array()
    {
        var run = Analyze(ActionSource("((IList<int>)Mutable)[0] = 1;"));
        var direct = Analyze(ActionSource("Mutable[0] = 1;"));

        Assert.Contains(OfAct(direct), access => access.StartsWith("Mutable read ", StringComparison.Ordinal));
        Assert.Equal(OfAct(direct), OfAct(run));
    }

    /// <summary>A decided call counts as its direct equivalent does, on the dispatch route as on the opaque one: a member of the table as an
    /// opaque call, an element of an array as an element operation (review F-0019).</summary>
    [Fact]
    public void Decided_call_counts_as_its_direct_equivalent_on_both_routes()
    {
        foreach (var (throughInterface, direct, types) in new[]
                 {
                     ("AsCollection.Add(Shared);", "Items.Add(Shared);", Bag),
                     ("CellList[0] = 1;", "Cells[0] = 1;", "")
                 })
        {
            var run = Analyze(ActionSource(throughInterface, types));
            var expected = Analyze(ActionSource(direct, types));
            foreach (var counter in new[] { CoverageCounters.OPAQUE_CALL, CoverageCounters.KNOWN_CALL, CoverageCounters.ELEMENT_OPERATION })
                Assert.True(expected.Counter(counter) == run.Counter(counter), $"{throughInterface}: {counter} {run.Counter(counter)}, directly {expected.Counter(counter)}");
        }
    }

    /// <summary>A dictionary enumerated through <c>IEnumerable&lt;KeyValuePair&lt;TKey, TValue&gt;&gt;</c> hands out pairs holding the keys and the
    /// values it holds, as its own enumeration does (review F-0024).</summary>
    [Fact]
    public void Pairs_enumerated_through_IEnumerable_hold_the_keys_and_values_the_dictionary_holds()
    {
        foreach (var dictionary in new[] { "Tags", "ConcurrentTags" })
        {
            const string loop = " { p.Key.V = 1; p.Value.Value = 2; }";
            var fill = $"{dictionary}.TryAdd(new Tag(), new Item()); ";
            var run = Analyze(ActionSource(fill + $"foreach (var p in (IEnumerable<KeyValuePair<Tag, Item>>){dictionary})" + loop));
            var direct = Analyze(ActionSource(fill + $"foreach (var p in {dictionary})" + loop));

            Assert.Contains("alloc:Store.Act()#Tag V", Writes(run));
            Assert.Contains("alloc:Store.Act()#Item Value", Writes(run));
            Assert.Equal(Writes(direct), Writes(run));
        }
    }

    /// <summary>A delegate handed to a member the call decides is held, never run: a list's <c>Add</c> holds it and an array refuses it,
    /// as the same member called directly does (review F-0025).</summary>
    [Fact]
    public void Delegate_handed_to_a_decided_interface_call_is_not_run()
    {
        foreach (var body in new[] { "ActionCollection.Add(() => Counter++);", "ActionArrayCollection.Add(() => Counter++);", "Actions.Add(() => Counter++);" })
        {
            var run = Analyze(ActionSource(body));

            Assert.DoesNotContain(run.Collection.Accesses, access => Path(access.Resource) == "Counter");
            Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        }
    }

    /// <summary>A change on a field decided by a check through a parameter is one compound operation where the heap says the parameter is
    /// the same collection, and none where it is another (review F-0026).</summary>
    [Fact]
    public void Check_through_a_parameter_and_change_through_the_field_are_one_compound_operation_on_one_collection()
    {
        var run = Analyze(ActionSource("CheckThenSet(Map);"));
        var direct = Analyze(ActionSource("if (!Map.ContainsKey(\"a\")) Map[\"a\"] = 1;"));
        var other = Analyze(ActionSource("CheckThenSet(Other);"));

        Assert.Contains("Map.[\"a\"] compound-operation cell", Describe(OnCollections(direct)));
        Assert.Equal(OnCollectionIdentities(direct), OnCollectionIdentities(run));
        Assert.DoesNotContain(OnCollections(other), access => Path(access.Resource).StartsWith("Map", StringComparison.Ordinal) &&
                                                             access.Operation == AccessOperation.CompoundOperation);
        Assert.Contains("Map.[\"a\"] write cell", Describe(OnCollections(other)));
    }

    /// <summary>A check through an interface reads the collection only on the objects whose member reads it: one whose type implements the
    /// member itself runs that body, which may read nothing, and then decides nothing about the list (review F-0027).</summary>
    [Fact]
    public void Check_through_an_interface_is_a_read_only_on_the_objects_it_decides()
    {
        var own = Analyze(ActionSource("if (((ICollection<int>)Numbers).Count == 0) Numbers.Add(1);", Counted));
        var unrelated = Analyze(ActionSource("if (Zero() == 0) Numbers.Add(1);", Counted));

        Assert.DoesNotContain(OnCollections(own), access => access.Operation == AccessOperation.CompoundOperation);
        Assert.Equal(OnCollectionIdentities(unrelated), OnCollectionIdentities(own));
        // On a list of the table it is the read the same check on the list directly is.
        Assert.Contains("Names compound-operation structure",
                        AssertSameAsDirect("if (((ICollection<string>)Names).Count == 0) Names.Add(\"b\");", "if (Names.Count == 0) Names.Add(\"b\");"));
    }

    /// <summary>A pair that is a dictionary's value is held whole, as any value is: only the pair argument of
    /// <c>ICollection&lt;KeyValuePair&lt;TKey, TValue&gt;&gt;.Add</c> is the dictionary's key and value (review F-0035).</summary>
    [Fact]
    public void Pair_that_is_a_dictionary_value_is_held_whole()
    {
        var run = Analyze(ActionSource("var item = new Item(); NestedPairs.Add(\"outer\", new KeyValuePair<string, Item>(\"inner\", item)); " +
                                       "NestedPairs[\"outer\"].Value.Value = 1;"));

        Assert.Contains("alloc:Store.Act()#Item Value", Writes(run));
    }

    /// <summary>The argument of a generic <c>ICollection&lt;T&gt;.Add</c> is the pair on a dictionary whatever type the call declares it as:
    /// the dictionary holds its key and its value, as the call through <c>ICollection&lt;KeyValuePair&lt;TKey, TValue&gt;&gt;</c> does (review
    /// F-0036).</summary>
    [Fact]
    public void Pair_added_through_a_generic_helper_holds_the_key_and_the_value()
    {
        const string then = " Lookup[\"a\"].Value = 1;";
        var run = Analyze(ActionSource("var item = new Item(); Put(Lookup, new KeyValuePair<string, Item>(\"a\", item));" + then));
        var direct = Analyze(ActionSource("var item = new Item(); ((ICollection<KeyValuePair<string, Item>>)Lookup).Add(new KeyValuePair<string, Item>(\"a\", item));" + then));

        Assert.Contains("alloc:Store.Act()#Item Value", Writes(run));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Resource.Region.Contains("KeyValuePair", StringComparison.Ordinal));
        Assert.Equal(Writes(direct), Writes(run));
    }

    /// <summary>What an opaque call returns and an interface call puts into a shared collection is fed to it there, as the same member
    /// called directly feeds it: the gap of the opaque call stays (review F-0037).</summary>
    [Fact]
    public void Opaque_result_held_through_an_interface_keeps_its_gap()
    {
        foreach (var (throughInterface, direct) in new[]
                 {
                     ("AsCollection.Add(Activator.CreateInstance<Item>());", "Items.Add(Activator.CreateInstance<Item>());"),
                     ("ThingList[0] = Activator.CreateInstance<Item>();", "Things[0] = Activator.CreateInstance<Item>();")
                 })
        {
            var expected = GapCallees(Analyze(ActionSource(direct)));

            Assert.Contains(expected, callee => callee.Contains("Activator", StringComparison.Ordinal));
            Assert.Equal(expected, GapCallees(Analyze(ActionSource(throughInterface))));
        }
    }

    /// <summary>What an interface call is handed is held only by the objects whose member holds it: an array refuses <c>Add</c>, and a type
    /// of the run's own whose <c>Add</c> holds nothing runs that body, so an opaque result handed to either feeds nothing shared, as the
    /// same code on the array or on the type directly does (review F-0040).</summary>
    [Fact]
    public void Opaque_result_handed_to_a_member_that_holds_nothing_is_no_gap()
    {
        foreach (var (throughInterface, direct) in new[]
                 {
                     ("ThingCollection.Add(Activator.CreateInstance<Item>());", "_ = Activator.CreateInstance<Item>();"),
                     ("MutedCollection.Add(Activator.CreateInstance<Item>());", "((Quiet)MutedCollection).Add(Activator.CreateInstance<Item>());")
                 })
        {
            var expected = GapCallees(Analyze(ActionSource(direct, Quiet)));

            Assert.DoesNotContain(expected, callee => callee.Contains("Activator", StringComparison.Ordinal));
            Assert.Equal(expected, GapCallees(Analyze(ActionSource(throughInterface, Quiet))));
        }
    }

    /// <summary>Enumerated through an interface, a dictionary hands out its pairs alone and never the values its cells hold beside them: a
    /// pair that is a value stays the value of the pair it is in, as the dictionary's own enumeration keeps it (review F-0039).</summary>
    [Fact]
    public void Pairs_enumerated_through_IEnumerable_are_not_mixed_with_the_values_they_hold()
    {
        const string fill = "NestedTags[new Tag()] = new KeyValuePair<Item, Item>(new Item(), new Item()); ";
        var run = Analyze(ActionSource(fill + "foreach (var p in (IEnumerable<KeyValuePair<Tag, KeyValuePair<Item, Item>>>)NestedTags) p.Key.V = 1;"));
        var direct = Analyze(ActionSource(fill + "foreach (var p in NestedTags) p.Key.V = 1;"));

        Assert.Contains("alloc:Store.Act()#Tag V", Writes(run));
        Assert.DoesNotContain(Writes(run), write => write.EndsWith(" V", StringComparison.Ordinal) && !write.Contains("#Tag", StringComparison.Ordinal));
        Assert.Equal(Writes(direct), Writes(run));
    }

    /// <summary>Enumerated through the non-generic <c>IDictionary</c>, a dictionary hands out a <c>DictionaryEntry</c> for each of its pairs,
    /// whose <c>Key</c> and <c>Value</c> read what a pair's do: what is written through them lands on the objects the dictionary holds, as
    /// through its own enumeration, and neither is a gap (review F-0043).</summary>
    [Fact]
    public void Entries_enumerated_through_IDictionary_hold_the_keys_and_values_the_dictionary_holds()
    {
        foreach (var dictionary in new[] { "Tags", "ConcurrentTags" })
        {
            var fill = $"{dictionary}.TryAdd(new Tag(), new Item()); ";
            var run = Analyze(ActionSource(fill + $"foreach (System.Collections.DictionaryEntry e in (System.Collections.IDictionary){dictionary}) " +
                                           "{ ((Tag)e.Key).V = 1; ((Item)e.Value!).Value = 2; }"));
            var direct = Analyze(ActionSource(fill + $"foreach (var p in {dictionary}) {{ p.Key.V = 1; p.Value.Value = 2; }}"));

            Assert.Contains("alloc:Store.Act()#Tag V", Writes(run));
            Assert.Contains("alloc:Store.Act()#Item Value", Writes(run));
            Assert.Equal(Writes(direct), Writes(run));
            Assert.Equal(GapCallees(direct), GapCallees(run));
        }
    }

    private static IReadOnlyList<string> GapCallees(EngineRun run) =>
        run.Collection.Coverage.Gaps.Select(gap => gap.Callee).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> Writes(EngineRun run) =>
        run.Collection.Accesses.Where(access => access.Operation == AccessOperation.Write && access.Resource.CollectionId is null)
           .Select(access => $"{access.Resource.Region} {Path(access.Resource)}")
           .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> OfAct(EngineRun run) =>
        run.Collection.Accesses.Where(access => access.Symbol == "Store.Act()" && !access.IsConstructionLocal)
           .Select(access => $"{Path(access.Resource)} {access.Operation.ToWireName()} {access.Resource.Identity}")
           .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static void AssertListDecidedAndSortedSetUnknown(EngineRun run)
    {
        var items = Assert.Single(OnCollections(run).Where(access => Path(access.Resource) == "Items").Select(access => access.Resource.CollectionId).Distinct());
        Assert.Equal(["Items write structure", "Items.[?] write cell"],
                     Describe(OnCollections(run).Where(access => access.Resource.CollectionId == items && !access.Operation.IsUnknownEffect())));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Resource.CollectionId == items && access.Operation.IsUnknownEffect());
        Assert.Contains(run.Collection.Accesses, access => Path(access.Resource) == "Value" && access.Operation.IsUnknownEffect());
    }

    private static void AssertNothingOnCells(string body)
    {
        var run = Analyze(ActionSource(body));

        Assert.DoesNotContain(OnCollections(run), access => Path(access.Resource).StartsWith("Cells", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    /// <summary>Asserts that the call through the interface makes the collection accesses the direct call makes, by identity and
    /// operation, with no unknown effect and no gap, and hands them back described.</summary>
    private static IReadOnlyList<string> AssertSameAsDirect(string throughInterface, string direct)
    {
        var run = Analyze(ActionSource(throughInterface));
        var expected = Analyze(ActionSource(direct));

        Assert.NotEmpty(OnCollections(expected));
        Assert.Equal(OnCollectionIdentities(expected), OnCollectionIdentities(run));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Empty(run.Collection.Coverage.Gaps);
        return Describe(OnCollections(run));
    }

    private static IReadOnlyList<Access> OnCollections(EngineRun run) =>
        run.Collection.Accesses.Where(access => access.Resource.CollectionId is not null && !access.IsConstructionLocal).ToArray();

    private static IReadOnlyList<string> OnCollectionIdentities(EngineRun run) =>
        OnCollections(run).Select(access => $"{access.Resource.Identity} {access.Operation.ToWireName()}").Distinct(StringComparer.Ordinal)
                          .Order(StringComparer.Ordinal).ToArray();

    /// <summary>Accesses on collections and on every object but the singleton, by identity, operation and ownership.</summary>
    private static IReadOnlyList<string> Everything(EngineRun run) =>
        run.Collection.Accesses.Where(access => !access.IsConstructionLocal &&
                                                (access.Resource.CollectionId is not null || access.Resource.Region != "di:Store@Singleton"))
           .Select(access => $"{Path(access.Resource)} {access.Operation.ToWireName()} {access.Resource.Identity} {access.Ownership}")
           .Distinct(StringComparer.Ordinal)
           .Order(StringComparer.Ordinal)
           .ToArray();

    private static IReadOnlyList<string> Describe(IEnumerable<Access> accesses) =>
        accesses.Select(access => $"{Path(access.Resource)} {access.Operation.ToWireName()} {(access.Resource.Selector is null ? "structure" : "cell")}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

    private static string Path(AccessResource resource) => string.Join(".", resource.AccessPath);

    private static int Line(string text, string fragment) =>
        Array.FindIndex((Usings + text).Split('\n'), line => line.Contains(fragment, StringComparison.Ordinal)) + 1;

    /// <summary>A type of the run's own implementing <c>IEnumerable&lt;Item&gt;</c>, which makes a call of its members a dispatch.</summary>
    private const string Chain = """

        public sealed class Chain : IEnumerable<Item>
        {
            public IEnumerator<Item> GetEnumerator() { yield break; }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
        """;

    /// <summary>A list of the run's own that implements <c>ICollection&lt;Item&gt;.Add</c> itself.</summary>
    private const string Custom = """

        public sealed class Custom : List<Item>, ICollection<Item>
        {
            public Item? Last;
            void ICollection<Item>.Add(Item item) => Last = item;
        }
        """;

    /// <summary>A list of the run's own whose <c>ICollection&lt;int&gt;.Count</c> reads nothing.</summary>
    private const string Counted = """

        public sealed class Counted : List<int>, ICollection<int>
        {
            int ICollection<int>.Count => 0;
        }
        """;

    /// <summary>A list of the run's own whose <c>ICollection&lt;Item&gt;.Add</c> holds nothing.</summary>
    private const string Quiet = """

        public sealed class Quiet : List<Item>, ICollection<Item>
        {
            public new void Add(Item item) { }
        }
        """;

    /// <summary>A type of the run's own implementing <c>ICollection&lt;Item&gt;</c>, whose <c>Add</c> writes a field of its own.</summary>
    private const string Bag = """

        public sealed class Bag : ICollection<Item>
        {
            public Item? Last;
            public int Count => 0;
            public bool IsReadOnly => false;
            public void Add(Item item) => Last = item;
            public void Clear() { }
            public bool Contains(Item item) => false;
            public void CopyTo(Item[] array, int arrayIndex) { }
            public bool Remove(Item item) => false;
            public IEnumerator<Item> GetEnumerator() { yield break; }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
        """;

    private static string ActionSource(string body, string types = "") => $$"""
        using System.Collections.Concurrent;
        using System.Collections.Generic;

        public sealed class Item
        {
            public int Value;
        }

        public sealed class Roster : List<Item>
        {
        }

        public sealed class Tag
        {
            public int V;
        }

        public sealed class Store
        {
            public readonly List<string> Names = new() { "a" };
            public readonly Dictionary<string, int> Map = new() { ["a"] = 0 };
            public readonly ConcurrentDictionary<string, int> Concurrent = new();
            public readonly List<Item> Items = new() { new Item() };
            public readonly Item Shared = new();
            public readonly SortedSet<Item> Sorted = new();
            public readonly Dictionary<string, Item> Lookup = new();
            public readonly Roster Team = new();
            public readonly int[] Cells = new int[4];
            public readonly Item[] Things = new Item[4];
            public readonly Item[] Copies = new Item[4];
            public readonly int[,] Grid = new int[2, 2];
            public readonly ICollection<string> Mixed;
            public readonly IReadOnlyCollection<Item> ReadOnly;
            public readonly ICollection<Item> AsCollection;
            public readonly IList<Item> AsList;
            public readonly IEnumerable<Item> Sequence;
            public readonly IDictionary<string, Item> AsDictionary;
            public readonly ICollection<KeyValuePair<string, int>> Pairs;
            public readonly IDictionary<string, int> AsMap;
            public readonly IList<int> CellList;
            public readonly ICollection<int> CellCollection;
            public readonly ICollection<Item> ThingCollection;
            public readonly System.Collections.IList GridList;
            public readonly ICollection<Item> ListOrSorted;
            public readonly ICollection<Item> TeamCollection;
            public ICollection<Item>? Missing;
            public ICollection<string>? Kept;
            public ICollection<Item>? KeptValues;
            public readonly Dictionary<Tag, Item> Tags = new();
            public readonly ICollection<KeyValuePair<Tag, Item>> TagPairs;
            public readonly Tag Probe = new();
            public readonly ConcurrentDictionary<string, Item> Held = new();
            public int[] Mutable = new int[4];
            public readonly Item[,] ItemGrid = new Item[1, 1];
            public readonly System.Collections.IList ItemGridList;
            public readonly ConcurrentDictionary<Tag, Item> ConcurrentTags = new();
            public readonly Dictionary<string, int> Other = new();
            public readonly List<Action> Actions = new();
            public readonly ICollection<Action> ActionCollection;
            public readonly Action[] ActionArray = new Action[1];
            public readonly ICollection<Action> ActionArrayCollection;
            public int Counter;
            public readonly Dictionary<string, KeyValuePair<string, Item>> NestedPairs = new();
            public readonly IList<Item> ThingList;
            public readonly Dictionary<Tag, KeyValuePair<Item, Item>> NestedTags = new();
            {{(types.Contains("class Custom", StringComparison.Ordinal) ? "public readonly ICollection<Item> CustomCollection = new Custom();" : "")}}
            {{(types.Contains("class Quiet", StringComparison.Ordinal) ? "public readonly ICollection<Item> MutedCollection = new Quiet();" : "")}}
            {{(types.Contains("class Counted", StringComparison.Ordinal) ? "public readonly Counted Numbers = new(); public static int Zero() => 0;" : "")}}
            {{(types.Contains("class Bag", StringComparison.Ordinal) ? "public readonly ICollection<Item> Either; public readonly ICollection<Item> ListSortedOrBag;" : "")}}

            public Store()
            {
                Mixed = Environment.ProcessorCount switch { 1 => Names, 2 => Concurrent.Keys, _ => Map.Keys };
                ReadOnly = Items;
                AsCollection = Items;
                AsList = Items;
                Sequence = Items;
                Lookup["a"] = new Item();
                AsDictionary = Lookup;
                Pairs = Map;
                AsMap = Map;
                CellList = Cells;
                CellCollection = Cells;
                Things[0] = new Item();
                Copies[0] = new Item();
                ThingCollection = Things;
                GridList = Grid;
                ListOrSorted = Environment.ProcessorCount > 1 ? Items : Sorted;
                TeamCollection = Team;
                TagPairs = Tags;
                Held["a"] = new Item();
                ItemGrid[0, 0] = new Item();
                ItemGridList = ItemGrid;
                ActionCollection = Actions;
                ActionArrayCollection = ActionArray;
                ThingList = Things;
                {{(types.Contains("class Bag", StringComparison.Ordinal)
                    ? "Either = Environment.ProcessorCount > 1 ? Items : new Bag(); ListSortedOrBag = Environment.ProcessorCount switch { 1 => Items, 2 => Sorted, _ => new Bag() };"
                    : "")}}
            }

            public static int CountOf(ICollection<string> keys) => keys.Count;

            public int ReadCell() => Cells[0];

            public static void BumpAt(IList<int> cells, int index) => cells[index]++;

            public static void BumpAtArray(int[] cells, int index) => cells[index]++;

            public static void Put<T>(ICollection<T> items, T item) => items.Add(item);

            public void CheckThenSet(Dictionary<string, int> map)
            {
                if (!map.ContainsKey("a"))
                    Map["a"] = 1;
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
        {{types}}
        """ + Startup("services.AddSingleton<Store>();");
}
