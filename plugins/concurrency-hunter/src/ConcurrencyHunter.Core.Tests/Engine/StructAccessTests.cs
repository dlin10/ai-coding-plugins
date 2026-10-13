using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>An access to a field of a struct kept in heap storage is an access to the holder's region with the path through every struct
/// field (R1), and each decider that used to compare one field slot reads that path: the depth bound, construction, a settled field, one
/// operation reaching several paths, a cell's rebinding, the holder's type, an array two fields hold, a known call's collection, the
/// receiver load an interface call folds, and what an unknown effect reaches and may write.</summary>
public sealed class StructAccessTests
{
    private const string HOLDER = "di:Holder@Singleton";

    [Fact]
    public void A_struct_path_past_the_depth_bound_is_the_wildcard_of_the_holder_region_struct_segments_counted()
    {
        var run = Analyze(Source("private Outer _outer; private State _flat; public void Deep() => _outer.Inner.Count++; public void Flat() => _flat.Count++;",
                                 "_holder.Deep(); _holder.Flat();", "_holder.Deep(); _holder.Flat();"),
                          new AnalysisLimits(MaxAccessPathDepth: 2));

        var deep = Of(run, "Holder.Deep");
        Assert.NotEmpty(deep);
        Assert.All(deep, access => Assert.Equal((true, HOLDER, "*"), (access.Resource.IsWildcard, access.Resource.Region, Path(access))));
        Assert.All(Of(run, "Holder.Flat"), access => Assert.Equal((false, "_flat.Count"), (access.Resource.IsWildcard, Path(access))));
        Assert.NotEmpty(Of(run, "Holder.Flat"));
    }

    [Fact]
    public void A_struct_inside_a_struct_is_its_holders_resource_with_the_path_through_both()
    {
        // The storage of the inner struct is named only when the access is resolved, after the solve, on the outer one's.
        var run = Run("private Outer _outer; public void Deep() => _outer.Inner.Count++;", "_holder.Deep();", "_holder.Deep();");

        var deep = Of(run, "Holder.Deep");
        Assert.NotEmpty(deep);
        Assert.All(deep, access => Assert.Equal((HOLDER, "_outer.Inner.Count"), (access.Resource.Region, Path(access))));
        Assert.Contains(run.Pairs.Pairs, pair => string.Join(".", pair.Resource.AccessPath) == "_outer.Inner.Count");
    }

    [Fact]
    public void A_constructor_whole_struct_write_is_construction_local_for_every_field_below_it()
    {
        var run = Run("private State _state; public Holder() { _state = new State(1); _state.Count = 2; _state.Increment(); } " +
                      "public int Read() => _state.Count; public void Bump() => _state.Count++;",
                      "_ = _holder.Read();", "_holder.Bump();");

        var built = run.Collection.Accesses.Where(access => access.Symbol.Contains("Holder..ctor", StringComparison.Ordinal) ||
                                                            access.Symbol.Contains("State.Increment", StringComparison.Ordinal))
                       .Where(access => access.Resource.Region == HOLDER)
                       .ToArray();
        Assert.Contains(built, access => Path(access) == "_state" && access.Operation == AccessOperation.Write);
        Assert.Contains(built, access => Path(access) == "_state.Count" && access.Symbol.Contains("Holder..ctor", StringComparison.Ordinal));
        Assert.Contains(built, access => Path(access) == "_state.Count" && access.Symbol.Contains("State.Increment", StringComparison.Ordinal));
        Assert.All(built, access => Assert.True(access.IsConstructionLocal, $"{access.Symbol} {Path(access)}"));
        Assert.Contains(run.Pairs.Pairs, pair => string.Join(".", pair.Resource.AccessPath) == "_state.Count");
        Assert.DoesNotContain(run.Pairs.Pairs, pair => pair.First.IsConstructionLocal || pair.Second.IsConstructionLocal);
    }

    [Fact]
    public void A_field_of_a_struct_a_readonly_field_keeps_is_one_value_in_every_execution()
    {
        var run = Run("public readonly State Ro = new State(1);",
                      "if (_holder.Ro.Mode == 1) _holder.Hits = 1;", "if (_holder.Ro.Mode != 1) _holder.Hits = 2;");

        var conditions = run.Collection.Accesses.Where(access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal))
                            .SelectMany(access => access.Conditions).ToArray();
        Assert.NotEmpty(conditions);
        Assert.All(conditions, condition => Assert.StartsWith("canon|", condition.Subject, StringComparison.Ordinal));
        Assert.DoesNotContain(run.Pairs.Pairs, pair => string.Join(".", pair.Resource.AccessPath) == "Hits");
    }

    [Fact]
    public void A_readonly_field_below_a_struct_field_the_holder_can_replace_is_a_value_of_each_execution()
    {
        var run = Run("public State Rw = new State(1);",
                      "if (_holder.Rw.Fixed == 1) _holder.Hits = 1;", "if (_holder.Rw.Fixed != 1) _holder.Hits = 2;");

        var conditions = run.Collection.Accesses.Where(access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal))
                            .SelectMany(access => access.Conditions).ToArray();
        Assert.NotEmpty(conditions);
        Assert.All(conditions, condition => Assert.StartsWith("local|", condition.Subject, StringComparison.Ordinal));
        Assert.Contains(run.Pairs.Pairs, pair => string.Join(".", pair.Resource.AccessPath) == "Hits");
    }

    [Fact]
    public void One_operation_reaching_two_paths_of_one_holder_gives_two_accesses()
    {
        var run = Run("private State _a; private State _b; public void Either(bool flag) { ref State r = ref _a; if (flag) r = ref _b; r.Count = 1; }",
                      "_holder.Either(true);", "_holder.Either(false);");

        var writes = Of(run, "Holder.Either").Where(access => access.Operation == AccessOperation.Write).ToArray();
        Assert.Equal(["_a.Count", "_b.Count"], writes.Select(Path).Distinct().Order(StringComparer.Ordinal));
        Assert.Single(writes.Select(access => access.OperationId).Distinct());
        var holder = run.Execution.Heap.Region(HOLDER).Identity;
        var collected = run.Execution.Analysis.Accesses.Where(access => access.RegionId == holder && access.Access.OperationId == writes[0].OperationId &&
                                                                        access.InstanceId == writes[0].InstanceId)
                           .Select(access => access.Slot).Distinct().ToArray();
        Assert.Equal(2, collected.Length);
        Assert.All(collected, slot => Assert.EndsWith("/Fixture:State.Count", slot, StringComparison.Ordinal));
    }

    [Fact]
    public void A_cell_its_term_binds_to_a_constant_keeps_the_struct_fields_after_it()
    {
        var run = Run("private readonly Slot[] _slots = new Slot[4]; public void Hit(int i) => _slots[i].Hits++;",
                      "_holder.Hit(0);", "_holder.Hit(1);");

        var hits = Of(run, "Holder.Hit").Where(access => access.Resource.CollectionId is not null).ToArray();
        Assert.Equal(["_slots.[0].Hits", "_slots.[1].Hits"], hits.Select(Path).Distinct().Order(StringComparer.Ordinal));
        Assert.All(hits, access => Assert.Equal(["Hits"], access.Resource.StructPath.Select(member => member.Name)));
        Assert.DoesNotContain(run.Pairs.Pairs, pair => pair.Resource.Member.Name == "_slots");
    }

    [Fact]
    public void A_holder_region_whose_type_cannot_declare_the_first_field_of_the_path_is_skipped()
    {
        var run = Run("", "Shared.Target = new Item(); ((Box)Shared.Target!).State.Count = 1;", "Shared.Target = new Box();");

        var heap = run.Execution.Heap.Heap;
        var item = run.Execution.Heap.Region("alloc:FirstWorker.ExecuteAsync(CancellationToken)#Item").Identity;
        var writes = run.Collection.Accesses.Where(access => Path(access) == "State.Count").ToArray();
        var write = Assert.Single(writes);
        Assert.Equal("alloc:SecondWorker.ExecuteAsync(CancellationToken)#Box", write.Resource.Region);
        // The cast does not narrow what the static field holds: the storage of the struct is named on the other object too.
        var store = heap.Instances[write.InstanceId].Summary.Accesses.Single(access => access.OperationId == write.OperationId);
        Assert.Contains(store.Bases.SelectMany(value => heap.Resolve(write.InstanceId, value)),
                        region => heap.Regions[region] is { Kind: HeapRegionKind.StructStorage } storage && storage.Holder == item);
    }

    [Fact]
    public void Two_fields_holding_one_array_reach_one_cells_struct_field_as_one_resource()
    {
        var run = Run("private readonly Slot[] _slots = new Slot[4]; private readonly Slot[] _alias; public Holder() { _alias = _slots; } " +
                      "public void HitSlots() => _slots[0].Hits++; public void HitAlias() => _alias[0].Hits++;",
                      "_holder.HitSlots();", "_holder.HitAlias();");

        var pair = Assert.Single(run.Pairs.Pairs, pair => pair.Resource.Selector?.Text == "[0]");
        Assert.Equal(["Hits"], pair.Resource.StructPath.Select(member => member.Name));
        Assert.Equal(["Holder.HitAlias()", "Holder.HitSlots()"], new[] { pair.First.Symbol, pair.Second.Symbol }.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_known_call_on_a_collection_a_struct_field_holds_names_the_whole_path()
    {
        var run = Run("public State State = new State(1);", "_holder.State.List.Add(1);", "_ = _holder.State.List.Count;");

        // The member writes the list's structure and a cell of it, as on a class field.
        var add = run.Collection.Accesses.Where(access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                          access.Resource.CollectionId is not null)
                     .ToArray();
        Assert.Equal(["State.List", "State.List.[?]"], add.Select(Path).Order(StringComparer.Ordinal));
        Assert.All(add, access => Assert.Equal(HOLDER, access.Resource.Region));
        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.CollectionId == add[0].Resource.CollectionId);
    }

    [Fact]
    public void An_interface_collection_call_on_a_collection_a_struct_field_holds_folds_the_receiver_load()
    {
        var run = Run("public State State = new State(1); public ICollection<int> Plain = new List<int>();",
                      "_holder.State.Items.Add(1); _holder.Plain.Add(1);", "");

        var first = run.Collection.Accesses.Where(access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal)).ToArray();
        // The call on the collection is the access; the load of the field that holds it is no access of its own, as on a class field.
        Assert.Contains(first, access => Path(access) == "State.Items" && access.Resource.CollectionId is not null);
        Assert.Contains(first, access => Path(access) == "Plain" && access.Resource.CollectionId is not null);
        Assert.DoesNotContain(first, access => Path(access) is "State.Items" or "Plain" && access.Resource.CollectionId is null);
    }

    [Fact]
    public void An_unknown_effect_on_a_holder_reaches_the_fields_of_its_structs_and_what_they_hold()
    {
        var run = Run("public State State = new State(1);", "Opaque.Lib.Touch(_holder);", "_holder.State.Count = 5;");

        var effects = UnknownEffects(run);
        Assert.Contains(effects, access => (access.Resource.Region, Path(access)) == (HOLDER, "State.Count"));
        Assert.Contains(effects, access => (access.Resource.Region, Path(access)) == (HOLDER, "State.Mode"));
        Assert.DoesNotContain(effects, access => (access.Resource.Region, Path(access)) == (HOLDER, "State"));
        Assert.Contains(effects, access => Path(access) == "N" && access.Resource.Region.EndsWith("#Item", StringComparison.Ordinal));
        Assert.Contains(run.Pairs.Pairs, pair => string.Join(".", pair.Resource.AccessPath) == "State.Count");
    }

    [Fact]
    public void An_unknown_effect_only_reads_a_readonly_struct_field_while_what_its_reference_fields_hold_gets_the_whole_effect()
    {
        var run = Run("public readonly State State = new State(1);", "Opaque.Lib.Touch(_holder);", "");

        var first = run.Collection.Accesses.Where(access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal)).ToArray();
        Assert.Contains(first, access => (access.Resource.Region, Path(access), access.Operation) == (HOLDER, "State.Count", AccessOperation.Read));
        Assert.DoesNotContain(first, access => access.Resource is { Region: HOLDER, CollectionId: null } &&
                                               Path(access).StartsWith("State", StringComparison.Ordinal) && access.Operation.IsUnknownEffect());
        // The objects its reference fields hold, an item and two lists, are objects of their own.
        Assert.Contains(first, access => Path(access) == "N" && access.Operation == AccessOperation.UnknownEffect);
        Assert.Contains(first, access => Path(access) == "State.List" && access.Resource.CollectionId is not null &&
                                         access.Operation == AccessOperation.UnknownEffect);
    }

    [Fact]
    public void An_unknown_effect_writes_a_readonly_field_of_a_struct_the_holder_can_replace()
    {
        var run = Run("public State State = new State(1);", "Opaque.Lib.Touch(_holder);", "");

        Assert.Contains(UnknownEffects(run), access => (access.Resource.Region, Path(access)) == (HOLDER, "State.Fixed"));
    }

    [Fact]
    public void Reflection_writes_readonly_struct_storage_as_it_writes_any_readonly_field()
    {
        var run = Run("public readonly State State = new State(1);", "typeof(Holder).GetField(\"State\")!.SetValue(_holder, new State(2));", "");

        Assert.Contains(UnknownEffects(run), access => (access.Resource.Region, Path(access)) == (HOLDER, "State.Count"));
    }

    [Theory]
    [InlineData("var copy = this; return copy.Count;")]
    [InlineData("return Snapshot.Take(this);")]
    [InlineData("return Snapshot.Count(Self());")]
    [InlineData("object boxed = this; return boxed.GetHashCode();")]
    public void A_whole_copy_of_this_in_a_member_called_on_heap_storage_reads_the_whole_storage(string body)
    {
        var run = Run("private Copied _copied; public int Read() => _copied.Read(); public void Bump() => _copied.Count++;",
                      "_ = _holder.Read();", "_holder.Bump();", body);

        var whole = Of(run, "Copied.Read").Concat(Of(run, "Copied.Self"))
                                          .Where(access => (access.Resource.Region, Path(access)) == (HOLDER, "_copied")).ToArray();
        Assert.NotEmpty(whole);
        Assert.All(whole, access => Assert.Equal(AccessOperation.Read, access.Operation));
        Assert.Contains(run.Pairs.Pairs, pair => string.Join(".", pair.Resource.AccessPath) == "_copied.Count" &&
                                                 new[] { pair.First, pair.Second }.Any(side => side.Resource.AccessPath.Count == 1));
    }

    [Fact]
    public void A_member_working_on_this_through_its_fields_reads_no_whole_storage()
    {
        var run = Run("private Copied _copied; public int Read() => _copied.Read(); public void Bump() => _copied.Count++;",
                      "_ = _holder.Read();", "_holder.Bump();", "Count++; Touch(ref this); return Count;");

        Assert.Contains(Of(run, "Copied.Read"), access => Path(access) == "_copied.Count");
        Assert.DoesNotContain(Of(run, "Copied.Read"), access => Path(access) == "_copied");
    }

    [Fact]
    public void A_whole_copy_of_this_in_a_member_called_on_a_copy_is_no_access()
    {
        const string HOLDER_MEMBERS = "public int Read() { var local = new Copied(); return local.Read(); }";
        var run = Run(HOLDER_MEMBERS, "_ = _holder.Read();", "", "var copy = this; return copy.Count;");
        var plain = Run(HOLDER_MEMBERS, "_ = _holder.Read();", "");

        Assert.DoesNotContain(Of(run, "Copied.Read"), access => access.Resource.Region == HOLDER);
        Assert.Equal(plain.Counter(CoverageCounters.UNRESOLVED_BASE), run.Counter(CoverageCounters.UNRESOLVED_BASE));
    }

    [Theory]
    [InlineData("private readonly Bucket[] _buckets = Bucket.Many(); public void Hit() => _buckets[0].Hit(1);", HOLDER, "_buckets.Arr.[1]")]
    [InlineData("private readonly Bucket[] _buckets = new Bucket[4]; public Holder() { _buckets[0].Init(); } public void Hit() => _buckets[0].Hit(1);",
                HOLDER, "_buckets.Arr.[1]")]
    [InlineData("private Bucket _bucket = Bucket.One(); public void Hit() => _bucket.Hit(1);", HOLDER, "_bucket.Arr.[1]")]
    [InlineData("private Bucket _bucket; public Holder() { _bucket.Init(); } public void Hit() => _bucket.Hit(1);", HOLDER, "_bucket.Arr.[1]")]
    [InlineData("private static Bucket s_bucket = Bucket.One(); public void Hit() => s_bucket.Hit(1);", "static:Holder", "s_bucket.Arr.[1]")]
    [InlineData("private static Bucket s_bucket; static Holder() { s_bucket.Init(); } public void Hit() => s_bucket.Hit(1);", "static:Holder", "s_bucket.Arr.[1]")]
    [InlineData("private Outer2 _outer = Outer2.One(); public void Hit() => _outer.Bucket.Hit(1);", HOLDER, "_outer.Bucket.Arr.[1]")]
    [InlineData("private Outer2 _outer; public Holder() { _outer.Bucket.Init(); } public void Hit() => _outer.Bucket.Hit(1);", HOLDER, "_outer.Bucket.Arr.[1]")]
    [InlineData("private readonly Bucket[] _buckets = Bucket.Many(); public void Hit() { ref Bucket bucket = ref _buckets[0]; bucket.Hit(1); }",
                HOLDER, "_buckets.Arr.[1]")]
    [InlineData("private Bucket _bucket = Bucket.One(); public void Hit() => Through(ref _bucket); private static void Through(ref Bucket bucket) => bucket.Hit(1);",
                HOLDER, "_bucket.Arr.[1]")]
    public void An_element_of_an_array_a_struct_field_holds_reached_through_this_is_the_holders_resource(string holder, string region, string path)
    {
        var run = Run(holder, "_holder.Hit();", "_holder.Hit();");

        var hits = Of(run, "Bucket.Hit");
        Assert.True(hits.Any(access => (access.Resource.Region, Path(access)) == (region, path) && access.Operation == AccessOperation.ReadModifyWrite),
                    string.Join("; ", run.Collection.Accesses.Select(access => $"{access.Symbol} {access.Resource.Region} {Path(access)} {access.Operation}")) +
                    $" unresolved={run.Counter(CoverageCounters.UNRESOLVED_BASE)}");
        Assert.Contains(run.Pairs.Pairs, pair => string.Join(".", pair.Resource.AccessPath) == path);
    }

    [Theory]
    [InlineData("var a = Arr; a[i]++;", "_buckets")]
    [InlineData("var a = Arr; a[i]++;", "_bucket")]
    [InlineData("Bucket.Bump(Arr, i);", "_buckets")]
    [InlineData("Bucket.Bump(Arr, i);", "_bucket")]
    [InlineData("ref int cell = ref Arr[i]; cell++;", "_buckets")]
    [InlineData("ref int cell = ref Arr[i]; cell++;", "_bucket")]
    public void An_element_of_an_array_a_struct_field_holds_reached_through_this_by_any_route_is_kept(string hit, string field)
    {
        var holder = field == "_buckets"
            ? "private readonly Bucket[] _buckets = Bucket.Many(); public void Hit() => _buckets[0].Hit(1);"
            : "private Bucket _bucket = Bucket.One(); public void Hit() => _bucket.Hit(1);";
        var run = Run(holder, "_holder.Hit();", "_holder.Hit();", hit: hit);

        var writes = run.Collection.Accesses.Where(access => access.Resource.Region == HOLDER && access.Resource.CollectionId is not null &&
                                                             Path(access).StartsWith(field + ".", StringComparison.Ordinal) &&
                                                             Path(access).EndsWith("Arr.[1]", StringComparison.Ordinal) &&
                                                             access.Operation != AccessOperation.Read).ToArray();
        Assert.True(writes.Length != 0,
                    string.Join("; ", run.Collection.Accesses.Select(access => $"{access.Symbol} {access.Resource.Region} {Path(access)} {access.Operation}")) +
                    $" unresolved={run.Counter(CoverageCounters.UNRESOLVED_BASE)} unproven={run.Counter(CoverageCounters.UNPROVEN_REFERENCE)}");
        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.CollectionId == writes[0].Resource.CollectionId);
    }

    [Theory]
    [InlineData("private Copied _copied; public void Hit() => _copied.Bump();", "_copied.Count", true)]
    [InlineData("private Copied _copied; public void Hit() => _copied.Self().Bump();", "_copied.Count", false)]
    [InlineData("private static Copied s_copied; public void Hit() => s_copied.Self().Bump();", "s_copied.Count", false)]
    [InlineData("private readonly Copied _copied; public void Hit() => _copied.Wrapper();", "_copied.Count", false)]
    [InlineData("private Copied _copied; public void Hit() => _copied.Wrapper();", "_copied.Count", false)]
    [InlineData("public void Hit() { var c = new Copied(); System.Func<Copied> get = () => c; get().Bump(); }", "c.Count", false)]
    [InlineData("private Copied _copied; private ref Copied Slot => ref _copied; public void Hit() => Slot.Bump();", "_copied.Count", true)]
    [InlineData("private Copied _copied; private ref readonly Copied Slot => ref _copied; public void Hit() => Slot.Bump();", "_copied.Count", false)]
    [InlineData("private static Copied s_copied; private static ref readonly Copied Slot => ref s_copied; public void Hit() => Slot.Bump();",
                "s_copied.Count", false)]
    [InlineData("private Copied _copied; private ref readonly Copied Get() => ref _copied; public void Hit() => Get().Bump();", "_copied.Count", false)]
    public void A_copy_of_this_a_member_hands_back_or_makes_defensively_is_no_place(string holder, string path, bool writes)
    {
        // `this` returned by value, a captured variable returned by value, and `this` a readonly member calls a member that is not readonly
        // on are copies: the storage is read whole and what is done to the copy never writes it (R2).
        var run = Run(holder, "_holder.Hit();", "_holder.Hit();");

        var bumps = Of(run, "Copied.Bump").Where(access => Path(access) == path && access.Operation != AccessOperation.Read).ToArray();
        Assert.True(writes == (bumps.Length != 0),
                    string.Join("; ", run.Collection.Accesses.Select(access => $"{access.Symbol} {access.Resource.Region} {Path(access)} {access.Operation}")));
        Assert.Equal(writes, run.Pairs.Pairs.Any(pair => string.Join(".", pair.Resource.AccessPath) == path));
        if (!writes && !path.StartsWith("c.", StringComparison.Ordinal))
            Assert.Contains(run.Collection.Accesses, access => Path(access) == path[..path.IndexOf('.')] && access.Operation == AccessOperation.Read);
    }

    [Theory]
    [InlineData("private Cell _state; private ref Cell Slot => ref _state; public void Hit() { ref var x = ref Slot; x.X++; }", HOLDER, "_state.X")]
    [InlineData("private Cell _state; private ref Cell Slot => ref _state; public void Hit() => Slot.X++;", HOLDER, "_state.X")]
    [InlineData("private Cell _state; private ref Cell Slot { get { return ref _state; } } public void Hit() => Through(ref Slot); " +
                "private static void Through(ref Cell cell) => cell.X++;", HOLDER, "_state.X")]
    [InlineData("private static Cell s_state; private static ref Cell Slot => ref s_state; public void Hit() { ref var x = ref Slot; x.X++; }",
                "static:Holder", "s_state.X")]
    [InlineData("private Cell _state; private ref Cell Get() => ref _state; public void Hit() { ref var x = ref Get(); x.X++; }", HOLDER, "_state.X")]
    [InlineData("private Counter _s; public void Hit() => Bump(ref _s); private static void Bump<T>(ref T x) where T : struct, IInc => x.Inc();",
                HOLDER, "_s.X")]
    [InlineData("private static Counter s_s; public void Hit() => Bump(ref s_s); private static void Bump<T>(ref T x) where T : struct, IInc { x.Inc(); }",
                "static:Holder", "s_s.X")]
    [InlineData("private Indexed _state = Indexed.One(); public void Hit() => _state[0].X++;", HOLDER, "_state.Count")]
    [InlineData("private Indexed _state = Indexed.One(); public void Hit() => _state[0].X++;", HOLDER, "_state.Items.[?].X")]
    [InlineData("private static Indexed s_state = Indexed.One(); public void Hit() { ref Cell cell = ref s_state[0]; cell.X++; }",
                "static:Holder", "s_state.Count")]
    public void A_reference_a_member_hands_back_or_takes_to_struct_storage_reaches_the_holder(string holder, string region, string path)
    {
        // A `ref`-returning property is an address as a `ref`-returning method is, the receiver of a struct's `ref`-returning indexer is the
        // holder's storage, and a `ref T` of a type parameter constrained to `struct` is that storage: every access lands on the holder (R1).
        var run = Run(holder, "_holder.Hit();", "_holder.Hit();");

        Assert.True(run.Collection.Accesses.Any(access => (access.Resource.Region, Path(access)) == (region, path) &&
                                                          access.Operation == AccessOperation.ReadModifyWrite),
                    string.Join("; ", run.Collection.Accesses.Select(access => $"{access.Symbol} {access.Resource.Region} {Path(access)} {access.Operation}")) +
                    $" unresolved={run.Counter(CoverageCounters.UNRESOLVED_BASE)} unproven={run.Counter(CoverageCounters.UNPROVEN_REFERENCE)}");
        Assert.Contains(run.Pairs.Pairs, pair => string.Join(".", pair.Resource.AccessPath) == path);
    }

    [Fact]
    public void A_by_value_type_parameter_constrained_to_struct_is_a_copy()
    {
        // `T x` is a copy whatever T is: the call reads the whole storage it copies, and what Inc does to the copy writes nothing there (R2).
        var run = Run("private Counter _s; public void Hit() => Bump(_s); private static void Bump<T>(T x) where T : struct, IInc => x.Inc();",
                      "_holder.Hit();", "_holder.Hit();");

        Assert.DoesNotContain(run.Collection.Accesses, access => Path(access) == "_s.X");
        Assert.Contains(run.Collection.Accesses, access => (access.Resource.Region, Path(access), access.Operation) == (HOLDER, "_s", AccessOperation.Read));
        Assert.DoesNotContain(run.Pairs.Pairs, pair => string.Join(".", pair.Resource.AccessPath).StartsWith("_s", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("private Rack _rack = Rack.One(); public void Hit() => _rack.Items[0].X++;", HOLDER, "_rack.Items.[0].X")]
    [InlineData("private Shelf _shelf = Shelf.One(); public void Hit() => _shelf.Rack.Items[0].X++;", HOLDER, "_shelf.Rack.Items.[0].X")]
    [InlineData("private static Rack s_rack = Rack.One(); public void Hit() => s_rack.Items[0].X++;", "static:Holder", "s_rack.Items.[0].X")]
    [InlineData("private Rack _rack = Rack.One(); public void Hit() => _rack.Touch();", HOLDER, "_rack.Items.[?].X")]
    [InlineData("private readonly Cell[] _cells = new Cell[4]; public void Hit() => _cells[0].X++;", HOLDER, "_cells.[0].X")]
    public void A_cell_of_a_collection_struct_storage_holds_is_named_on_the_outermost_holder(string holder, string region, string path)
    {
        // A collection a struct's field holds is named by that field on the holder the struct is part of, with the whole path (R1): no
        // struct's storage is ever a resource's region.
        var run = Run(holder, "_holder.Hit();", "_holder.Hit();");

        var heap = run.Execution.Heap.Heap;
        Assert.True(Of(run, "Holder.Hit").Concat(Of(run, "Rack.Touch"))
                                          .Any(access => (access.Resource.Region, Path(access)) == (region, path) &&
                                                         access.Operation == AccessOperation.ReadModifyWrite),
                    string.Join("; ", run.Collection.Accesses.Select(access => $"{access.Symbol} {access.Resource.Region} {Path(access)} {access.Operation}")));
        Assert.All(run.Collection.Accesses.Where(access => access.Resource.RegionId is not null),
                   access => Assert.NotEqual(HeapRegionKind.StructStorage, heap.Regions[access.Resource.RegionId!].Kind));
        Assert.Contains(run.Pairs.Pairs, pair => string.Join(".", pair.Resource.AccessPath) == path);
    }

    [Theory]
    [InlineData("private readonly Item[] _a = { new Item() }; private readonly Item[] _b = { new Item() }; " +
                "public void Hit(bool flag) { ref Item r = ref (flag ? ref _a[0] : ref _b[0]); r.N++; }", 2)]
    [InlineData("private static readonly Item[] s_a = { new Item() }; private static readonly Item[] s_b = { new Item() }; " +
                "public void Hit(bool flag) { ref Item r = ref (flag ? ref s_a[0] : ref s_b[0]); r.N++; }", 2)]
    [InlineData("private readonly Item[] _a = { new Item() }; private readonly Item[] _b = { new Item() }; " +
                "public void Hit(bool flag) { ref Item r = ref (flag ? ref _a[0] : ref _a[0]); r.N++; }", 1)]
    public void A_reference_to_a_cell_keeps_the_collection_it_is_in(string holder, int objects)
    {
        // `ref _a[0]` and `ref _b[0]` are two locations, whatever cell each names: the object each holds is reached (R1).
        var run = Run(holder, "_holder.Hit(true);", "_holder.Hit(false);");

        var writes = Of(run, "Holder.Hit").Where(access => Path(access) == "N" && access.Operation == AccessOperation.ReadModifyWrite).ToArray();
        Assert.Equal(objects, writes.Select(access => access.Resource.RegionId).Distinct().Count());
        Assert.Equal(objects, run.Pairs.Pairs.Where(pair => string.Join(".", pair.Resource.AccessPath) == "N").Select(pair => pair.Resource.RegionId).Distinct().Count());
    }

    [Theory]
    [InlineData("private readonly Cell[] _cells = new Cell[4]; " +
                "public void Hit(bool more, bool change) { ref Cell r = ref _cells[0]; while (more) { if (change) r = ref _cells[1]; r.X++; more = change; } }",
                HOLDER, "_cells.[?].X")]
    [InlineData("private static readonly Cell[] s_cells = new Cell[4]; " +
                "public void Hit(bool more, bool change) { ref Cell r = ref s_cells[0]; while (more) { if (change) r = ref s_cells[1]; r.X++; more = change; } }",
                "static:Holder", "s_cells.[?].X")]
    [InlineData("private readonly Cell[] _cells = new Cell[4]; " +
                "public void Hit(bool more, bool change) { ref Cell r = ref _cells[0]; while (more) { r.X++; if (change) r = ref _cells[0]; more = change; } }",
                HOLDER, "_cells.[0].X")]
    public void A_ref_local_reassigned_in_a_loop_names_the_cell_every_alternative_names(string holder, string region, string path)
    {
        // The phi of `r` at the loop's head takes itself back: an alternative already followed names nothing new, so the walk ends, and
        // a field access through `r` stands in the one cell every alternative names, or in an unknown cell of the collection (R1).
        var run = Run(holder, "_holder.Hit(true, true);", "_holder.Hit(true, false);");

        var hits = Of(run, "Holder.Hit").Where(access => access.Operation == AccessOperation.ReadModifyWrite).ToArray();
        Assert.True(hits.Length != 0 && hits.All(access => (access.Resource.Region, Path(access)) == (region, path)),
                    string.Join("; ", run.Collection.Accesses.Select(access => $"{access.Symbol} {access.Resource.Region} {Path(access)} {access.Operation}")));
        Assert.Contains(run.Pairs.Pairs, pair => string.Join(".", pair.Resource.AccessPath) == path);
    }

    [Theory]
    [InlineData("ref Cell r = ref _cells[0]; while (more) { if (change) r = ref _cells[1]; r = new Cell(); more = change; }",
                "ref Cell r = ref _cells[0]; if (change) r = ref _cells[1]; r = new Cell();", "_cells.[0]", "_cells.[1]")]
    [InlineData("ref int x = ref _cells[0].X; while (more) { if (change) x = ref _cells[1].X; x++; more = change; }",
                "ref int x = ref _cells[0].X; if (change) x = ref _cells[1].X; x++;", "_cells.[0].X", "_cells.[1].X")]
    public void A_reference_reassigned_in_a_loop_reaches_every_cell_it_may_name(string looped, string straight, string first, string second)
    {
        // A write through a reference whose phi takes itself back names each cell its other inputs name, as the same code without the loop
        // does: the input that comes back round is no alternative of its own (R1).
        const string CELLS = "private readonly Cell[] _cells = new Cell[4]; ";
        var run = Run(CELLS + $"public void Hit(bool more, bool change) {{ {looped} }}", "_holder.Hit(true, true);", "_holder.Hit(true, false);");
        var plain = Run(CELLS + $"public void Hit(bool more, bool change) {{ {straight} }}", "_holder.Hit(true, true);", "_holder.Hit(true, false);");

        var paths = Of(run, "Holder.Hit").Where(access => access.Operation != AccessOperation.Read).Select(Path).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal([first, second], paths);
        Assert.Equal(Of(plain, "Holder.Hit").Where(access => access.Operation != AccessOperation.Read).Select(Path).Distinct().Order(StringComparer.Ordinal),
                     paths);
        Assert.Contains(run.Pairs.Pairs, pair => string.Join(".", pair.Resource.AccessPath) == first);
    }

    private static string Path(Access access) => string.Join(".", access.Resource.AccessPath);

    private static Access[] Of(EngineRun run, string member) =>
        run.Collection.Accesses.Where(access => access.Symbol.Contains($"{member}(", StringComparison.Ordinal)).ToArray();

    private static Access[] UnknownEffects(EngineRun run) =>
        run.Collection.Accesses.Where(access => access.Operation.IsUnknownEffect()).ToArray();

    private static EngineRun Run(string holder, string first, string second, string copied = "return Count;", string hit = "Arr[i]++;") =>
        AnalyzeScope(FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value] },
                                            ("Case.cs", Source(holder, first, second, copied, hit))),
                     "scope:Fixture");

    /// <summary>A singleton <c>Holder</c> whose members are <paramref name="holder"/>, and two workers that run <paramref name="first"/>
    /// and <paramref name="second"/> on it.</summary>
    /// <param name="holder">The members of <c>Holder</c>.</param>
    /// <param name="first">What the first worker runs.</param>
    /// <param name="second">What the second worker runs.</param>
    /// <param name="copied">The body of <c>Copied.Read</c>.</param>
    /// <param name="hit">The body of <c>Bucket.Hit</c>.</param>
    private static string Source(string holder, string first, string second, string copied = "return Count;", string hit = "Arr[i]++;") =>
        Usings + $$"""
        using System.Collections.Generic;

        public sealed class Item { public int N; }
        public struct Inner { public int Count; }
        public struct Outer { public Inner Inner; }
        public struct Slot { public int Hits; }

        public struct Copied
        {
            public int Count;
            public int Read() { {{copied}} }
            public Copied Self() => this;
            public void Bump() { Count++; }
            public readonly void Wrapper() { Bump(); }
            public static void Touch(ref Copied copied) { }
        }

        public struct Cell { public int X; }

        public interface IInc { void Inc(); }

        public struct Counter : IInc { public int X; public void Inc() => X++; }

        public struct Indexed
        {
            public int Count;
            public Cell[] Items;
            public ref Cell this[int i] { get { Count++; return ref Items[i]; } }
            public static Indexed One() { var indexed = new Indexed(); indexed.Items = new Cell[4]; return indexed; }
        }

        public struct Rack
        {
            public Cell[] Items;
            public void Touch() => Items[System.Environment.TickCount & 3].X++;
            public static Rack One() { var rack = new Rack(); rack.Items = new Cell[4]; return rack; }
        }

        public struct Shelf
        {
            public Rack Rack;
            public static Shelf One() { var shelf = new Shelf(); shelf.Rack = Rack.One(); return shelf; }
        }

        public static class Snapshot
        {
            public static int Take(Copied copied) => copied.Count;
            public static int Count(Copied copied) => copied.Count;
        }

        public struct Bucket
        {
            public int[] Arr;
            public void Hit(int i) { {{hit}} }
            public static void Bump(int[] array, int i) => array[i]++;
            public void Init() => Arr = new int[4];
            public static Bucket One() { var bucket = new Bucket(); bucket.Arr = new int[4]; return bucket; }
            public static Bucket[] Many() { var all = new Bucket[4]; all[0].Arr = new int[4]; return all; }
        }

        public struct Outer2
        {
            public Bucket Bucket;
            public static Outer2 One() { var outer = new Outer2(); outer.Bucket = Bucket.One(); return outer; }
        }

        public struct State
        {
            public int Count;
            public int Mode;
            public readonly int Fixed;
            public Item? Ref;
            public List<int> List;
            public ICollection<int> Items;

            public State(int value)
            {
                Count = 0;
                Mode = value;
                Fixed = value;
                Ref = new Item();
                List = new List<int>();
                Items = new List<int>();
            }

            public void Increment() { Count++; }
        }

        public sealed class Other { public int Count; }

        public sealed class Box { public State State; }

        public static class Shared { public static object? Target; }

        public sealed class Holder
        {
            public int Hits;
            {{holder}}
        }

        public sealed class FirstWorker : BackgroundService
        {
            private readonly Holder _holder;
            private readonly Other _other;
            public FirstWorker(Holder holder, Other other) { _holder = holder; _other = other; }
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{first}}
                return Task.CompletedTask;
            }
        }

        public sealed class SecondWorker : BackgroundService
        {
            private readonly Holder _holder;
            private readonly Other _other;
            public SecondWorker(Holder holder, Other other) { _holder = holder; _other = other; }
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{second}}
                return Task.CompletedTask;
            }
        }

        """ + Startup("services.AddSingleton<Holder>(); services.AddSingleton<Other>(); " +
                      "services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();");

    /// <summary>A library the run has no source of: every member is an opaque call the library table does not describe.</summary>
    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() =>
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            namespace Opaque;
            public static class Lib
            {
                public static void Touch(object value) { }
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
