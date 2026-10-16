using System.Collections;
using System.Reflection;
using Common.Roslyn;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class WorklistTests
{
    private const BindingFlags MEMBERS = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string SIMPLE = "public class WorkController : ControllerBase { public void Post() { } }";

    [Fact]
    public async Task Demo_skips_are_sound_under_audit() => await AssertSolution(DemoPath());

    [RequiresEShopFact]
    public async Task EShop_skips_are_sound_under_audit() => await AssertSolution(EShopPath());

    [Fact]
    public void Clean_instance_is_skipped_and_a_dirty_one_is_processed()
    {
        var program = Reach("""
            public static class Steps { public static object Make() => new object(); }
            public class WorkController : ControllerBase { public void Post() { GC.KeepAlive(Steps.Make()); } }
            """ + Startup());
        var actual = SolveHeap(program, new(Audit: true));
        Assert.True(actual.Counters[HeapCounters.INSTANCE_PROCESSINGS] <
                    actual.Counters[HeapCounters.PROPAGATE_PASSES] * actual.Instances.Count);
        var solver = NewSolver(SIMPLE + Startup());
        Invoke(solver, "Run");
        Invoke(solver, "Propagate");
        var histories = (IDictionary)Field(solver, "_history").GetValue(solver)!;
        var states = (IDictionary)Field(solver, "_instances").GetValue(solver)!;
        var clean = Entries(histories).First(entry => !(bool)Invoke(solver, "Dirty", entry.Value!)!);
        var counters = (Dictionary<string, int>)Field(solver, "_counters").GetValue(solver)!;
        var processed = counters[HeapCounters.INSTANCE_PROCESSINGS];
        Invoke(solver, "ProcessOrSkip", states[clean.Key]!);
        Assert.Equal(processed, counters[HeapCounters.INSTANCE_PROCESSINGS]);
        var reads = (HashSet<StateKey>)clean.Value!.GetType().GetProperty("Reads")!.GetValue(clean.Value)!;
        Invoke(solver, "WroteState", reads.First());
        Invoke(solver, "ProcessOrSkip", states[clean.Key]!);
        Assert.Equal(processed + 1, counters[HeapCounters.INSTANCE_PROCESSINGS]);
        AssertFixture("""
            public sealed class Box { public object? Value; public void Set(object value) => Value = value; }
            public class WorkController(Box box) : ControllerBase { public void Post() { box.Set(new object()); GC.KeepAlive(box.Value); } }
            """ + Startup("services.AddSingleton<Box>();"));
    }

    [Fact]
    public async Task Full_pass_after_convergence_changes_nothing() => await AssertSolution(DemoPath(), verify: true);

    [Fact]
    public void Write_through_a_stored_reference_dirties_its_readers()
    {
        var solver = NewSolver(SIMPLE + Startup());
        var heap = (HeapSolution)Invoke(solver, "Run")!;
        var instances = (IDictionary)Field(solver, "_instances").GetValue(solver)!;
        var callee = instances[heap.Instances.Values.First(instance => instance.BodyId == "body:Fixture:M:WorkController.Post").Id]!;
        var requests = (TrackedSet<string>)callee.GetType().GetProperty("Requests", MEMBERS)!.GetValue(callee)!;
        var reads = new HashSet<StateKey>();
        Field(solver, "_reads").SetValue(solver, reads);
        _ = requests.Count;
        Field(solver, "_reads").SetValue(solver, null);
        var started = (long)Field(solver, "_writeClock").GetValue(solver)!;
        // The stored reference is passed to Add from elsewhere, just as Bind passes callee.Requests.
        Invoke(solver, "Add", requests, new TrackedSet<string>(StringComparer.Ordinal) { "new-request" });
        Assert.Contains(reads, key => Writes(solver).GetValueOrDefault(key) > started);
    }

    [Fact]
    public void Unchanging_mutation_writes_no_key()
    {
        var reads = new HashSet<StateKey>();
        var writes = new List<StateKey>();
        var set = new TrackedSet<string>(StringComparer.Ordinal) { "request" };
        set.Attach(new StateKey("InstanceState.Requests").Child("callee"), key => reads.Add(key), key => writes.Add(key));
        Assert.False(set.Add("request"));
        set.UnionWith(new[] { "request" });
        var map = new TrackedMap<string, int>(StringComparer.Ordinal) { ["key"] = 1 };
        map.Attach(new StateKey("_contextCounts"), key => reads.Add(key), key => writes.Add(key));
        map["key"] = 1;
        Assert.False(map.TryAdd("key", 2));
        Assert.False(map.Remove("absent"));
        var value = new TrackedValue<bool>(true);
        value.Attach(new StateKey("RegistrationState.Defaulted"), key => reads.Add(key), key => writes.Add(key));
        value.Value = true;
        Assert.Empty(writes);
        Assert.NotEmpty(reads);
    }

    [Fact]
    public void Set_add_reads_nothing_and_a_membership_test_reads_only_its_item()
    {
        var reads = new List<StateKey>();
        var writes = new List<StateKey>();
        var key = new StateKey("_mergedBodies");
        var set = new TrackedSet<string>(StringComparer.Ordinal);
        set.Attach(key, reads.Add, writes.Add);
        Assert.True(set.IsEmpty);
        Assert.True(set.Add("first"));
        set.UnionWith(["second"]);
        Assert.Equal(new[] { key.Child(StateKey.Emptiness) }, reads);
        Assert.Equal(new[] { key, key.Child(StateKey.Emptiness), key }, writes);
        reads.Clear();
        writes.Clear();
        var found = set.Contains("third");
        Assert.False(found);
        Assert.True(set.Add("fourth"));
        Assert.Equal(new[] { key.Child("third") }, reads);
        Assert.Equal(new[] { key.Child("fourth"), key }, writes);
        writes.Clear();
        // An add that read nothing cannot be redone by its reader, so a removal makes every history dirty.
        Assert.True(set.Remove("fourth"));
        Assert.Equal(new[] { StateKey.Wildcard }, writes);
    }

    [Fact]
    public void Map_entry_is_read_apart_from_the_contents_of_its_container()
    {
        var reads = new List<StateKey>();
        var writes = new List<StateKey>();
        var key = new StateKey("_fields");
        var map = new TrackedMap<string, TrackedSet<string>>(StringComparer.Ordinal);
        map.Attach(key, reads.Add, writes.Add);
        Assert.True(map.IsEmpty);
        map.Add("slot", new TrackedSet<string>(StringComparer.Ordinal));
        Assert.Equal(new[] { key.Child(StateKey.Emptiness), key.Child("slot") }, reads);
        Assert.Equal(new[] { key.Child("slot"), key.Child(StateKey.Emptiness) }, writes);
        reads.Clear();
        writes.Clear();
        Assert.True(map.TryGetValue("slot", out var set));
        set.Add("region");
        _ = set.Count;
        var contents = key.Child("slot").Child(StateKey.Contents);
        Assert.Equal(new[] { key.Child("slot"), contents.All }, reads);
        Assert.Equal(new[] { contents, contents.Child(StateKey.Emptiness) }, writes);
    }

    [Fact]
    public void Wildcard_write_makes_every_instance_dirty()
    {
        var solver = NewSolver(SIMPLE + Startup());
        var heap = (HeapSolution)Invoke(solver, "Run")!;
        Invoke(solver, "Propagate");
        var history = (IDictionary)Field(solver, "_history").GetValue(solver)!;
        Assert.All(heap.Instances.Keys, id => Assert.False((bool)Invoke(solver, "Dirty", history[id]!)!));
        Invoke(solver, "WroteState", StateKey.Wildcard);
        Assert.All(heap.Instances.Keys, id => Assert.True((bool)Invoke(solver, "Dirty", history[id]!)!));
    }

    [Fact]
    public void Skipped_instance_keeps_its_contribution_to_the_rebuilt_collections()
    {
        var source = """
            public sealed class Box { public void Touch() { } }
            public class WorkController : ControllerBase { public void Post() { Box? box = null; box!.Touch(); } }
            """ + Startup();
        var heap = AssertFixture(source);
        Assert.True(heap.Counters[HeapCounters.NO_RECEIVER_OBJECT] > 0);
        var solver = NewSolver(source);
        Invoke(solver, "Run");
        Invoke(solver, "Propagate");
        var before = (HeapSolution)Invoke(solver, "Result")!;
        var counters = (Dictionary<string, int>)Field(solver, "_counters").GetValue(solver)!;
        var processed = counters[HeapCounters.INSTANCE_PROCESSINGS];
        Invoke(solver, "Propagate");
        var after = (HeapSolution)Invoke(solver, "Result")!;
        Assert.Equal(processed, counters[HeapCounters.INSTANCE_PROCESSINGS]);
        Assert.Equal(before.NoReceiverObjects, after.NoReceiverObjects);
        Assert.Equal(heap.Counters[HeapCounters.NO_RECEIVER_OBJECT], after.Counters[HeapCounters.NO_RECEIVER_OBJECT]);
    }

    [Fact]
    public void After_loop_contributions_are_rebuilt_every_pass()
    {
        var heap = AssertFixture(Holders());
        Assert.NotEmpty(heap.Holders);
        Assert.NotEmpty(heap.DelegateHandoffs);
        var solver = NewSolver(Holders());
        Invoke(solver, "Run");
        Invoke(solver, "Propagate");
        var before = (HeapSolution)Invoke(solver, "Result")!;
        var counters = (Dictionary<string, int>)Field(solver, "_counters").GetValue(solver)!;
        var processed = counters[HeapCounters.INSTANCE_PROCESSINGS];
        Invoke(solver, "Propagate");
        var after = (HeapSolution)Invoke(solver, "Result")!;
        Assert.Equal(processed, counters[HeapCounters.INSTANCE_PROCESSINGS]);
        Assert.Equal(ExecutionObservation.Serialize(before.DelegateHandoffs), ExecutionObservation.Serialize(after.DelegateHandoffs));
        Assert.Equal(before.NoReceiverObjects, after.NoReceiverObjects);
    }

    [Fact]
    public void Rebuilt_collections_keep_their_order_when_a_late_instance_reruns()
    {
        var work = NewSolver(Reach(Holders()), new());
        Invoke(work, "Run");
        var histories = (IDictionary)Field(work, "_history").GetValue(work)!;
        Invoke(work, "Propagate");
        var order = ((IEnumerable<string>)Field(work, "_instanceOrder").GetValue(work)!).ToArray();
        var handoffs = Handoffs(work);
        var keys = handoffs.Keys.Cast<object>().ToArray();
        var serialized = ExecutionObservation.Serialize(handoffs);
        var late = order.Last(id => ((HashSet<StateKey>)histories[id]!.GetType().GetProperty("Reads")!.GetValue(histories[id])!).Count > 1);
        var read = ((HashSet<StateKey>)histories[late]!.GetType().GetProperty("Reads")!.GetValue(histories[late])!).First();
        Invoke(work, "WroteState", read);
        Invoke(work, "Propagate");
        handoffs = Handoffs(work);
        Assert.NotEmpty(handoffs);
        Assert.Equal(keys, handoffs.Keys.Cast<object>());
        Assert.Equal(serialized, ExecutionObservation.Serialize(handoffs));
        Assert.Equal(order, (IEnumerable<string>)Field(work, "_instanceOrder").GetValue(work)!);
    }

    [Fact]
    public void Reach_index_is_built_where_a_full_pass_builds_it()
    {
        var solver = NewSolver(Holders());
        Invoke(solver, "Run");
        Invoke(solver, "Propagate");
        var histories = (IDictionary)Field(solver, "_history").GetValue(solver)!;
        var first = ((IEnumerable<string>)Field(solver, "_instanceOrder").GetValue(solver)!).First(id =>
            histories[id]!.GetType().GetProperty("ReachSnapshot")!.GetValue(histories[id]) is not null &&
            !(bool)Invoke(solver, "Dirty", histories[id]!)!);
        var states = (IDictionary)Field(solver, "_instances").GetValue(solver)!;
        Field(solver, "_reachIndex").SetValue(solver, null);
        var count = ((Dictionary<string, int>)Field(solver, "_counters").GetValue(solver)!)[HeapCounters.INSTANCE_PROCESSINGS];
        Invoke(solver, "ProcessOrSkip", states[first]!);
        Assert.NotNull(Field(solver, "_reachIndex").GetValue(solver));
        Assert.Equal(count, ((Dictionary<string, int>)Field(solver, "_counters").GetValue(solver)!)[HeapCounters.INSTANCE_PROCESSINGS]);
        var fields = (IDictionary)Field(solver, "_fields").GetValue(solver)!;
        fields[FieldKey(solver, "snapshot-owner", "slot")] = RegionSet(solver, "snapshot-value");
        Assert.False((bool)Invoke(solver, "Dirty", histories[first]!)!);
        Field(solver, "_reachIndex").SetValue(solver, null);
        Invoke(solver, "ProcessOrSkip", states[first]!);
        Assert.Equal(count + 1, ((Dictionary<string, int>)Field(solver, "_counters").GetValue(solver)!)[HeapCounters.INSTANCE_PROCESSINGS]);
        AssertFixture(Holders());
    }

    [Fact]
    public void Captures_grown_after_the_snapshot_are_still_followed()
    {
        var solver = NewSolver(Holders());
        var heap = (HeapSolution)Invoke(solver, "Run")!;
        var snapshot = Field(solver, "_reachIndex").GetValue(solver);
        var delegates = (IDictionary)Field(solver, "_delegates").GetValue(solver)!;
        var entry = Entries(delegates).First();
        var receivers = entry.Value!.GetType().GetProperty("CapturedReceivers", MEMBERS)!.GetValue(entry.Value)!;
        var extra = heap.Regions.Keys.First(region => !Identities(receivers).Contains(region) && region != entry.Key.ToString());
        var starts = Array.CreateInstance(RegionRefType(), 1);
        starts.SetValue(entry.Key, 0);
        var before = Identities(Invoke(solver, "Reached", starts)!);
        if (before.Contains(extra)) extra = "extra-capture";
        AddTo(receivers, Region(solver, extra));
        var after = Identities(Invoke(solver, "Reached", starts)!);
        Assert.Contains(extra, after);
        Assert.DoesNotContain(extra, before);
        Assert.Same(snapshot, Field(solver, "_reachIndex").GetValue(solver));
        AssertFixture(Holders());
    }

    [Fact]
    public void Scc_budget_of_one_merges_a_cycle_and_every_skip_is_sound()
    {
        var heap = AssertFixture(Recursive(), new(MaxSccIterations: 1));
        Assert.True(heap.Counters[HeapCounters.SCC_BUDGET_EXCEEDED] > 0);
    }

    [Fact]
    public void Merged_body_makes_its_readers_dirty()
    {
        var solver = NewSolver(Reach(Recursive()), new(), new(MaxSccIterations: 1));
        var heap = (HeapSolution)Invoke(solver, "Run")!;
        Assert.True(heap.Counters[HeapCounters.SCC_BUDGET_EXCEEDED] > 0);
        Assert.Contains(Writes(solver).Keys, key => key.State == "_mergedBodies");
        Invoke(solver, "Propagate");
        var histories = (IDictionary)Field(solver, "_history").GetValue(solver)!;
        var cyclic = heap.Instances.Values.First(instance => instance.IsMerged && instance.BodyId.Contains("Ping.", StringComparison.Ordinal));
        // A membership test reads the key of its item: the readers of a merge are those that tested the merged body.
        var reader = Entries(histories).First(entry => ((HashSet<StateKey>)entry.Value!.GetType().GetProperty("Reads")!.GetValue(entry.Value)!)
                                                      .Any(key => key.State == "_mergedBodies" && Equals(key.Entry, cyclic.BodyId)) &&
                                                  !(bool)Invoke(solver, "Dirty", entry.Value!)!);
        var before = (int)Field(solver, "_changes").GetValue(solver)!;
        var merged = (TrackedSet<string>)Field(solver, "_mergedBodies").GetValue(solver)!;
        merged.Remove(cyclic.BodyId);
        ((HashSet<string>)Field(solver, "_sccHandled").GetValue(solver)!).Clear();
        var oldHistory = reader.Value!;
        var cleanHistory = Activator.CreateInstance(oldHistory.GetType(),
            [(long)Field(solver, "_writeClock").GetValue(solver)!, oldHistory.GetType().GetProperty("Reads")!.GetValue(oldHistory)!,
             oldHistory.GetType().GetProperty("Contribution")!.GetValue(oldHistory)!, oldHistory.GetType().GetProperty("ReachSnapshot")!.GetValue(oldHistory)])!;
        Assert.False((bool)Invoke(solver, "Dirty", cleanHistory)!);
        var states = (IDictionary)Field(solver, "_instances").GetValue(solver)!;
        Invoke(solver, "CountRound", states[cyclic.Id]!);
        Assert.Contains(cyclic.BodyId, (IReadOnlySet<string>)merged);
        Assert.Equal(before, (int)Field(solver, "_changes").GetValue(solver)!);
        Assert.True((bool)Invoke(solver, "Dirty", cleanHistory)!);
        AssertFixture(Recursive(), new(MaxSccIterations: 1));
    }

    /// <summary>Mutable solver facts track their readers; append-only scheduling data read only between propagation rounds is named
    /// explicitly alongside the existing caches and identity tables.</summary>
    [Fact]
    public void Every_solver_field_is_tracked_or_named_untracked()
    {
        var solver = NewSolver(SIMPLE + Startup());
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "_scope", "_program", "_limits", "_typeSafety", "_cancellationToken",
            "_counters", "_changes", "_changedRounds", "_sccHandled", "_reachIndex",
            "_worklistOptions", "_writes", "_history", "_reads", "_writeClock", "_suspendReads", "_auditing", "_rebuilding",
            "_named", "_queries",
            // Only the outer loop reads these starts. Recording a start during Process decides no heap fact and invalidates no reader.
            "_unseenStarts",
            // Memos of a union and of a copy: each use still reads the tracked sets they were made from and checks them grown or not.
            "_loadedUnions", "_copiedSlots",
            // Names, not facts: the interned region identities, a region's links read only after the region itself, and whether a query
            // named a region.
            "_interned", "_queryInterned", "_queryIdentities", "_links", "_anyNamed", "_unresolvedDelegate"
        };
        var checkedTypes = new HashSet<Type>();
        void Check(Type type)
        {
            if (!checkedTypes.Add(type)) return;
            foreach (var field in type.GetFields(MEMBERS))
            {
                if (type == solver.GetType() && allowed.Contains(field.Name)) continue;
                if (typeof(TrackedContainer).IsAssignableFrom(field.FieldType))
                {
                    if (type == solver.GetType()) Assert.NotNull(((TrackedContainer)field.GetValue(solver)!).Key);
                    foreach (var argument in field.FieldType.GetGenericArguments()) Follow(argument);
                    continue;
                }
                Assert.True(field.IsInitOnly && Immutable(field.FieldType), $"{type.Name}.{field.Name} is mutable and untracked.");
                Follow(field.FieldType);
            }
        }
        void Follow(Type type)
        {
            if (type.DeclaringType == typeof(WholeProgram) || type.DeclaringType == solver.GetType()) Check(type);
            foreach (var argument in type.GetGenericArguments()) Follow(argument);
        }
        Check(solver.GetType());
        bool Immutable(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) ||
            type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Nullable<>) ||
                                  type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ||
                                  type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)) ||
            type.GetMethod("<Clone>$") is not null;
    }

    [Fact]
    public void Write_during_a_heap_query_throws()
    {
        var solver = NewSolver(SIMPLE + Startup());
        Invoke(solver, "Run");
        Field(solver, "_queries").SetValue(solver, 1);

        var error = Assert.Throws<TargetInvocationException>(() => Invoke(solver, "WroteState", StateKey.Wildcard));

        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public void A_kept_union_sees_what_its_group_gained_and_reads_what_a_union_reads()
    {
        // A load over several regions of a group keeps its union between loads: it still sees an object a field set gained since, a
        // region the group gained since, and reads every set it joins as the union it stands for reads them.
        var solver = NewSolver(SIMPLE + Startup());
        Invoke(solver, "Run");
        AddRegion(solver, "load-a", "load-group", open: true);
        AddRegion(solver, "load-b", "load-group");
        AddTo(Slot(solver, "load-a"), Region(solver, "x"));
        AddTo(Slot(solver, "load-b"), Region(solver, "y"));

        var union = Loaded(solver, query: true);
        Assert.Equal(["x", "y"], Loaded(solver).Objects);
        var kept = Loaded(solver);
        Assert.Equal(["x", "y"], kept.Objects);
        Assert.Equal(union.Reads, kept.Reads);

        AddTo(Slot(solver, "load-b"), Region(solver, "z"));
        var grown = Loaded(solver);
        Assert.Equal(["x", "y", "z"], grown.Objects);
        Assert.Equal(Loaded(solver, query: true).Reads, grown.Reads);

        AddRegion(solver, "load-c", "load-group");
        AddTo(Slot(solver, "load-c"), Region(solver, "w"));
        var joined = Loaded(solver);
        Assert.Equal(["w", "x", "y", "z"], joined.Objects);
        Assert.Equal(Loaded(solver, query: true).Reads, joined.Reads);

        // What a caller does to what it loaded is its own.
        AddTo(Invoke(solver, "Load", Region(solver, "load-a"), "slot")!, Region(solver, "caller"));
        Assert.DoesNotContain("caller", Loaded(solver).Objects);
    }

    [Fact]
    public void A_struct_copy_adds_again_only_what_its_source_slot_gained()
    {
        // A copy run again adds what its source's slot gained since it last copied, into each of its targets, and nothing else.
        var solver = NewSolver(SIMPLE + Startup());
        Invoke(solver, "Run");
        AddRegion(solver, "copy-source", "copy-source", HeapRegionKind.StructCopy);
        AddRegion(solver, "copy-target", "copy-target", HeapRegionKind.StructCopy);
        AddRegion(solver, "copy-other", "copy-other", HeapRegionKind.StructCopy);
        var instance = Entries((IDictionary)Field(solver, "_instances").GetValue(solver)!).First().Value!;
        StructCopy Copy(string target) =>
            new(-1, new HashSet<AbstractValue> { new RegionValue(target) }, new HashSet<AbstractValue> { new RegionValue("copy-source") });

        AddTo(Slot(solver, "copy-source"), Region(solver, "x"));
        Invoke(solver, "CopyStruct", instance, Copy("copy-target"));
        Assert.Equal(["x"], Identities(Slot(solver, "copy-target")).Order());
        var changes = (int)Field(solver, "_changes").GetValue(solver)!;
        Invoke(solver, "CopyStruct", instance, Copy("copy-target"));
        Assert.Equal(changes, (int)Field(solver, "_changes").GetValue(solver)!);

        AddTo(Slot(solver, "copy-source"), Region(solver, "y"));
        Invoke(solver, "CopyStruct", instance, Copy("copy-target"));
        Assert.Equal(["x", "y"], Identities(Slot(solver, "copy-target")).Order());
        Invoke(solver, "CopyStruct", instance, Copy("copy-other"));
        Assert.Equal(["x", "y"], Identities(Slot(solver, "copy-other")).Order());
    }

    /// <summary>Adds a region of a group to a solved solver, as the solve adds one.</summary>
    /// <param name="solver">The solver.</param>
    /// <param name="identity">The region's identity.</param>
    /// <param name="group">The group it joins.</param>
    /// <param name="kind">The region's kind.</param>
    /// <param name="open">Whether the region stands for a type parameter's constructions, which joins every region of its group.</param>
    private static void AddRegion(object solver, string identity, string group, HeapRegionKind kind = HeapRegionKind.Allocation, bool open = false)
    {
        var regions = (IDictionary)Field(solver, "_regions").GetValue(solver)!;
        var groups = (IDictionary)Field(solver, "_groups").GetValue(solver)!;
        regions.Add(Region(solver, identity), new HeapRegion(identity, kind, identity, null, "", group, open, false));
        var key = Region(solver, group);
        if (!groups.Contains(key))
            groups.Add(key, Activator.CreateInstance(typeof(TrackedList<>).MakeGenericType(RegionRefType()), MEMBERS, null, [], null)!);
        var members = groups[key]!;
        members.GetType().GetMethod("Add", [RegionRefType()])!.Invoke(members, [Region(solver, identity)]);
    }

    /// <summary>The field set of the slot the tests use on a region, made where there is none.</summary>
    /// <param name="solver">The solver.</param>
    /// <param name="region">The region.</param>
    private static object Slot(object solver, string region) => Invoke(solver, "Field", Region(solver, region), "slot")!;

    /// <summary>What a load of the tests' slot on <c>load-a</c> gives, sorted, and the state keys it reads.</summary>
    /// <param name="solver">The solver.</param>
    /// <param name="query">Whether the load runs as a query of the solved heap, which joins every set afresh and keeps nothing.</param>
    private static (string[] Objects, HashSet<StateKey> Reads) Loaded(object solver, bool query = false)
    {
        var region = Region(solver, "load-a");
        var reads = new HashSet<StateKey>();
        Field(solver, "_reads").SetValue(solver, reads);
        if (query)
            Field(solver, "_queries").SetValue(solver, 1);
        try
        {
            return (Identities(Invoke(solver, "Load", region, "slot")!).Order(StringComparer.Ordinal).ToArray(), reads);
        }
        finally
        {
            Field(solver, "_reads").SetValue(solver, null);
            Field(solver, "_queries").SetValue(solver, 0);
        }
    }

    /// <summary>The solver's type of an interned region identity.</summary>
    private static Type RegionRefType() => typeof(WholeProgram).GetNestedType("RegionRef", BindingFlags.NonPublic)!;

    /// <summary>The solver's one reference to a region identity, as the solve interns it.</summary>
    /// <param name="solver">The solver.</param>
    /// <param name="identity">The region identity.</param>
    private static object Region(object solver, string identity) => Invoke(solver, "Intern", identity)!;

    /// <summary>A new set of the solver's region references.</summary>
    /// <param name="solver">The solver.</param>
    /// <param name="identities">The identities of the regions it holds.</param>
    private static object RegionSet(object solver, params string[] identities)
    {
        var set = Activator.CreateInstance(typeof(TrackedSet<>).MakeGenericType(RegionRefType()), MEMBERS, null, [], null)!;
        foreach (var identity in identities)
            AddTo(set, Region(solver, identity));
        return set;
    }

    /// <summary>The key of a field set of the solver: a region reference and a slot.</summary>
    /// <param name="solver">The solver.</param>
    /// <param name="region">The region identity.</param>
    /// <param name="slot">The slot.</param>
    private static object FieldKey(object solver, string region, string slot) =>
        Activator.CreateInstance(typeof(ValueTuple<,>).MakeGenericType(RegionRefType(), typeof(string)), Region(solver, region), slot)!;

    /// <summary>Adds a region reference to a set of them.</summary>
    /// <param name="set">The set.</param>
    /// <param name="item">The region reference.</param>
    private static void AddTo(object set, object item) => set.GetType().GetMethod("Add", [item.GetType()])!.Invoke(set, [item]);

    /// <summary>The identities a set of region references holds, in its order.</summary>
    /// <param name="set">The set.</param>
    private static string[] Identities(object set) => ((IEnumerable)set).Cast<object>().Select(item => item.ToString()!).ToArray();

    private static string Holders() => """
        using System.Collections.Generic;
        public sealed class Box { public object Value = new(); }
        public static class External { public static extern void Send(Action action); public static extern void Consume(object value); }
        public class WorkController(Box first, Box second) : ControllerBase
        {
            public void Post()
            {
                var order = Comparer<object>.Create((a, b) => { first.Value = second.Value; return 0; });
                External.Send(() => External.Consume(order));
                External.Send(() => External.Consume(order));
            }
        }
        """ + Startup("services.AddSingleton<Box>();");

    private static string Recursive() => """
        public sealed class Box { public object? Value; }
        public static class Ping
        {
            public static object Left(Box box, object value, int depth) { if (depth > 0) value = Right(box, value, depth - 1); box.Value = value; return value; }
            public static object Right(Box box, object value, int depth) { if (depth > 0) return Left(box, value, depth - 1); return value; }
        }
        public class WorkController : ControllerBase { public void Post() { Ping.Left(new Box(), new object(), 4); } }
        """ + Startup();

    // The audit runs every processing the worklist skips and fails the solve on a read or write the map missed.
    private static HeapSolution AssertFixture(string source, AnalysisLimits? limits = null, bool verify = false) =>
        WholeProgram.Solve(Scope(Reach(source), limits), limits ?? AnalysisLimits.Default, CancellationToken.None,
                           new(Audit: true, VerifyConvergence: verify));

    private static ScopeProgram Scope(WholeProgramRun program, AnalysisLimits? limits = null) =>
        new(program.ScopeId, program.Input.Roots, program.Result, new SummaryCache(program.Result.Bodies, program.Input.Program, limits ?? AnalysisLimits.Default),
            program.Input.Program, program.Input.DiIndex, program.Input.InjectionBindings) { MetadataSupertypes = program.MetadataSupertypes };

    private static HeapSolution SolveHeap(WholeProgramRun program, SolverWorklistOptions options) =>
        WholeProgram.Solve(Scope(program), AnalysisLimits.Default, CancellationToken.None, options);

    private static object NewSolver(string source) => NewSolver(Reach(source), new());
    private static object NewSolver(WholeProgramRun program, SolverWorklistOptions options, AnalysisLimits? limits = null) =>
        Activator.CreateInstance(typeof(WholeProgram).GetNestedType("Solver", BindingFlags.NonPublic)!,
                                MEMBERS, null, [Scope(program, limits), limits ?? AnalysisLimits.Default, CancellationToken.None, options], null)!;
    private static FieldInfo Field(object solver, string name) => solver.GetType().GetField(name, MEMBERS)!;
    private static object? Invoke(object solver, string name, params object[] arguments)
    {
        var method = solver.GetType().GetMethod(name, MEMBERS)!;
        // A generic member takes its type argument from the elements of its first argument.
        if (method.ContainsGenericParameters)
            method = method.MakeGenericMethod(arguments[0].GetType().GetGenericArguments()[0]);
        return method.Invoke(solver, arguments);
    }
    private static Dictionary<StateKey, long> Writes(object solver) => (Dictionary<StateKey, long>)Field(solver, "_writes").GetValue(solver)!;
    private static IDictionary Handoffs(object solver)
    {
        var rebuilding = Field(solver, "_rebuilding").GetValue(solver)!;
        return (IDictionary)rebuilding.GetType().GetProperty("Handoffs", MEMBERS)!.GetValue(rebuilding)!;
    }
    private static IEnumerable<DictionaryEntry> Entries(IDictionary map)
    {
        var entries = map.GetEnumerator();
        while (entries.MoveNext()) yield return entries.Entry;
    }
    private static string DemoPath() => RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx");
    private static string EShopPath() => Path.Combine(Environment.GetEnvironmentVariable(RequiresEShopFactAttribute.VARIABLE)!, "src", "eShopOnContainers-ServicesAndWebApps.sln");

    private static async Task AssertSolution(string path, bool verify = false)
    {
        if (path == DemoPath()) await DemoWorkspace.EnsureRestoredAsync();
        using var loaded = await new MsBuildSolutionLoader().LoadAsync(path);
        Assert.True(loaded.Coverage.LoadComplete, string.Join(", ", loaded.Coverage.MissingProjects));
        var root = Path.GetDirectoryName(path)!;
        var repository = RepositoryRoot.Find(root);
        var projectModels = ProjectModelFiles.Read(repository);
        var modelLock = ModelLock.Read(repository, projectModels);
        var cache = new Dictionary<(Compilation Compilation, string BodyId, LibraryModels Models), IrLoweredMethod?>();
        var scopes = ProcessScopes.Discover(loaded.Solution, root).Scopes;
        Assert.NotEmpty(scopes);
        foreach (var scoped in scopes)
        {
            var compiled = new List<(Compilation Compilation, string? ProjectFilePath)>();
            foreach (var project in scoped.Projects) compiled.Add(((await project.GetCompilationAsync())!, project.FilePath));
            var (models, _) = ProjectModelResolver.Resolve(projectModels, compiled.Select(item => item.Compilation).ToArray(), modelLock);
            var run = ScopePipeline.Run(scoped.Scope.Id, compiled.Select(item => item.Compilation).ToArray(), compiled, root,
                ProviderRegistry.BuiltIn, models, AnalysisLimits.Default, cache, null, CancellationToken.None);
            Assert.False(run.Stopped);
            var scope = run.ScopeProgram!;
            WholeProgram.Solve(scope, AnalysisLimits.Default, CancellationToken.None, new(Audit: true, VerifyConvergence: verify));
        }
    }
}
