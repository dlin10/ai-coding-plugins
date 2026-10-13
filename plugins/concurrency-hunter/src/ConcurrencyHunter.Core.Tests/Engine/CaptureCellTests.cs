using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A variable a lambda or a local function captures is a location (R4): a slot of the closure region of its declaring member,
/// named after the member as an allocation region is and after the variable, <c>name#n</c> for the n-th of that name. Loads and stores of
/// it are accesses on that slot, the declaring member's own fresh and a nested body's not, a captured struct's fields continue the path,
/// and the owner's reads before and after a nested body may have run keep apart what they read.</summary>
public sealed class CaptureCellTests
{
    [Fact]
    public void The_closure_is_a_region_of_the_declaring_member_and_the_captured_variable_its_slot()
    {
        var run = Analyze("""
            public class CounterController : ControllerBase
            {
                public async Task Post()
                {
                    var count = 0;
                    var first = Task.Run(() => { count++; });
                    var second = Task.Run(() => { count++; });
                    await Task.WhenAll(first, second);
                }
            }
            """ + Startup());

        var accesses = run.Accesses("count");
        Assert.NotEmpty(accesses);
        Assert.All(accesses, access => Assert.Equal("closure:CounterController.Post()", access.Resource.Region));
        var closure = Assert.Single(run.Execution.Heap.Heap.Regions.Values, region => region.Kind == HeapRegionKind.Closure);
        Assert.Equal("closure:CounterController.Post()", closure.Display);
        Assert.Contains(run.PairsOn("count"), pair => pair.First.Operation == AccessOperation.ReadModifyWrite &&
                                                      pair.Second.Operation == AccessOperation.ReadModifyWrite);
    }

    [Fact]
    public void The_nth_variable_of_one_name_the_member_declares_is_its_slot_with_hash_n_in_declaration_order()
    {
        var run = Analyze("""
            public class SumController : ControllerBase
            {
                public void Post(System.Collections.Generic.List<int> items)
                {
                    {
                        var n = 1;
                        items.ForEach(i => n += i);
                    }
                    {
                        var n = 2;
                        items.ForEach(i => n -= i);
                    }
                }
            }
            """ + Startup());

        var owner = Assert.Single(Instances(run, "SumController.Post(System.Collections.Generic.List{System.Int32})"));
        Assert.Equal(["n", "n#2"], owner.Summary.Delegates.SelectMany(created => created.Delegate.CapturedCells).Select(cell => cell.Slot));
        Assert.All(owner.Summary.Delegates.SelectMany(created => created.Delegate.CapturedCells),
                   cell => Assert.Equal("SumController.Post(List<int>)", cell.Member));
        Assert.NotEmpty(run.Accesses("n"));
        Assert.NotEmpty(run.Accesses("n#2"));
        Assert.All(run.Accesses("n").Concat(run.Accesses("n#2")),
                   access => Assert.Equal("closure:SumController.Post(List<int>)", access.Resource.Region));
    }

    [Fact]
    public void A_captured_struct_is_the_storage_of_its_cell_and_its_fields_continue_the_path()
    {
        var run = Analyze("""
            public struct State { public int Count; }
            public class StateController : ControllerBase
            {
                public async Task Post()
                {
                    State state = default;
                    var first = Task.Run(() => { state.Count++; });
                    var second = Task.Run(() => { state.Count++; });
                    await Task.WhenAll(first, second);
                }
            }
            """ + Startup());

        var fields = run.Collection.Accesses.Where(access => access.Resource.AccessPath.SequenceEqual(["state", "Count"])).ToArray();
        Assert.NotEmpty(fields);
        Assert.All(fields, access => Assert.Equal("closure:StateController.Post()", access.Resource.Region));
        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.AccessPath.SequenceEqual(["state", "Count"]));
        // The owner's assignment of the whole struct writes the whole of the cell's storage.
        Assert.Contains(run.Accesses("state"), access => access.Operation == AccessOperation.Write && access.IsFresh);
    }

    [Fact]
    public void A_reference_variable_a_lambda_reassigns_holds_every_object_in_its_cell()
    {
        var run = Analyze("""
            public sealed class Box { public int N; }
            public class BoxController : ControllerBase
            {
                public void Post()
                {
                    var box = new Box();
                    Action swap = () => box = new Box();
                    swap();
                    box.N = 1;
                }
            }
            """ + Startup());

        var heap = run.Execution.Heap;
        var owner = Assert.Single(Instances(run, "BoxController.Post"));
        var cell = Assert.Single(owner.Summary.Delegates.SelectMany(created => created.Delegate.CapturedCells));
        Assert.Equal("box", cell.Slot);
        var closure = heap.Heap.Closure(owner.Id)!;
        Assert.Equal(["alloc:BoxController.Post()#Box", "alloc:BoxController.Post()#Box#2"],
                     heap.Heap.PointsTo(closure, WholeProgram.ClosureSlot(cell)).Select(id => heap.Heap.Regions[id].Display).Order(StringComparer.Ordinal));
        var writes = run.Accesses("box").Where(access => access.Operation == AccessOperation.Write).ToArray();
        Assert.Contains(writes, access => access.BodyId.Contains("#lambda", StringComparison.Ordinal));
        Assert.Contains(writes, access => !access.BodyId.Contains("#lambda", StringComparison.Ordinal));
        // The field written after the call is that of either object the cell holds.
        Assert.Equal(["alloc:BoxController.Post()#Box", "alloc:BoxController.Post()#Box#2"],
                     run.Accesses("N").Select(access => access.Resource.Region).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_local_function_reaches_the_cell_of_its_method_through_the_capture()
    {
        var run = Analyze("""
            public class HitController : ControllerBase
            {
                public int Post()
                {
                    var hits = 0;
                    Bump();
                    Bump();
                    return hits;

                    void Bump() => hits++;
                }
            }
            """ + Startup());

        var accesses = run.Accesses("hits");
        Assert.All(accesses, access => Assert.Equal("closure:HitController.Post()", access.Resource.Region));
        var local = accesses.Where(access => access.BodyId.Contains("#local:Bump", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(local);
        Assert.All(local, access => Assert.False(access.IsFresh));
        var local1 = Assert.Single(Instances(run, "HitController.Post#local:Bump"));
        var owner = Assert.Single(Instances(run, "HitController.Post"));
        Assert.Equal([owner.Id], local1.CellOwners);
    }

    [Fact]
    public void A_variable_a_lambda_declares_and_a_nested_lambda_captures_is_in_the_closure_of_the_ordinary_member()
    {
        var run = Analyze("""
            public class NestController : ControllerBase
            {
                public void Post()
                {
                    Action outer = () =>
                    {
                        var inner = 0;
                        Action nested = () => inner++;
                        nested();
                    };
                    outer();
                }
            }
            """ + Startup());

        var accesses = run.Accesses("inner");
        Assert.Contains(accesses, access => access.Operation == AccessOperation.ReadModifyWrite);
        Assert.Contains(accesses, access => access.Operation == AccessOperation.Write);
        Assert.All(accesses, access => Assert.Equal("closure:NestController.Post()", access.Resource.Region));
        // The outer lambda declares the variable, so its run creates the closure and its own write is fresh; the nested lambda's is not.
        Assert.All(accesses, access => Assert.Equal(access.Operation == AccessOperation.Write, access.IsFresh));
        var nested = run.Execution.Heap.Heap.Instances.Values.Where(instance => instance.Summary.Accesses.Any(access => access.Field.Name == "inner"))
                        .SelectMany(instance => instance.Summary.Accesses.Where(access => access.Field.Name == "inner"))
                        .Select(access => access.Field.ContainingType).Distinct();
        Assert.Equal(["closure"], nested);
    }

    [Fact]
    public void The_owner_reads_its_own_version_before_a_nested_body_may_have_run_and_the_cell_after()
    {
        var run = Analyze("""
            public sealed class Holder { public object? Seen; }
            public class FindController : ControllerBase
            {
                public void Post()
                {
                    var before = new Holder();
                    var after = new Holder();
                    object current = new object();
                    before.Seen = current;
                    Replace();
                    after.Seen = current;

                    void Replace() { current = new System.Collections.Generic.List<int>(); }
                }
            }
            """ + Startup());

        var heap = run.Execution.Heap;
        Assert.Equal(["alloc:FindController.Post()#object"], heap.Targets(heap.Region("alloc:FindController.Post()#Holder"), "Seen"));
        Assert.Equal(["alloc:FindController.Post()#System.Collections.Generic.List<int>", "alloc:FindController.Post()#object"],
                     heap.Targets(heap.Region("alloc:FindController.Post()#Holder#2"), "Seen"));
        // Both reads are reads of the cell, whatever each one sees in it.
        var reads = run.Accesses("current").Where(access => access.Operation == AccessOperation.Read &&
                                                             !access.BodyId.Contains("#local:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, reads.Select(access => access.OperationId).Distinct().Count());
    }

    [Fact]
    public void The_declaring_members_accesses_to_its_closure_are_fresh_and_a_lambdas_are_not()
    {
        var run = Analyze("""
            public class TallyController : ControllerBase
            {
                public async Task<int> Post()
                {
                    var count = 0;
                    count++;
                    await Task.Run(() => count++);
                    return count;
                }
            }
            """ + Startup());

        var accesses = run.Accesses("count");
        var own = accesses.Where(access => !access.BodyId.Contains("#lambda", StringComparison.Ordinal)).ToArray();
        var lambda = accesses.Where(access => access.BodyId.Contains("#lambda", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(own);
        Assert.NotEmpty(lambda);
        Assert.All(own, access => Assert.True(access.IsFresh, $"{access.Operation} at {access.OperationId}"));
        Assert.All(lambda, access => Assert.False(access.IsFresh, $"{access.Operation} at {access.OperationId}"));
        Assert.Contains(lambda, access => access.Operation == AccessOperation.ReadModifyWrite);
        // Two accesses of the member's own invocation never pair, whichever invocations they are.
        Assert.DoesNotContain(run.PairsOn("count"), pair => pair.First.IsFresh && pair.Second.IsFresh);
    }

    [Fact]
    public void A_variable_no_nested_body_captures_is_no_location()
    {
        var run = Analyze("""
            public class PlainController : ControllerBase
            {
                public int Post()
                {
                    var plain = 0;
                    plain++;
                    Action other = () => { };
                    other();
                    return plain;
                }
            }
            """ + Startup());

        Assert.Empty(run.Accesses("plain"));
        Assert.DoesNotContain(run.Execution.Heap.Heap.Regions.Values, region => region.Kind == HeapRegionKind.Closure);
    }

    [Fact]
    public void A_variable_a_parallel_body_declares_is_its_own_in_every_run_of_the_body_and_does_not_pair_across_iterations()
    {
        var run = Analyze("""
            public class SumController : ControllerBase
            {
                public void Post(System.Collections.Generic.List<int> items)
                {
                    Parallel.ForEach(items, x =>
                    {
                        var sum = 0;
                        Action add = () => sum += x;
                        add();
                    });
                }
            }
            """ + Startup());

        var accesses = run.Accesses("sum");
        Assert.Contains(accesses, access => access.Operation == AccessOperation.ReadModifyWrite && !access.IsFresh);
        Assert.All(accesses, access => Assert.Equal("closure:SumController.Post(List<int>)", access.Resource.Region));
        Assert.Empty(run.PairsOn("sum"));
        // The closure is the parallel body's, made by each run of it.
        var closure = Assert.Single(run.Execution.Heap.Heap.Regions.Values, region => region.Kind == HeapRegionKind.Closure);
        Assert.Contains("#lambda", closure.SiteBodyId, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variable_the_member_declares_and_a_parallel_body_writes_still_pairs_across_iterations()
    {
        var run = Analyze("""
            public class SumController : ControllerBase
            {
                public int Post(System.Collections.Generic.List<int> items)
                {
                    var sum = 0;
                    Parallel.ForEach(items, x =>
                    {
                        Action add = () => sum += x;
                        add();
                    });
                    return sum;
                }
            }
            """ + Startup());

        Assert.Contains(run.PairsOn("sum"), pair => pair.First.Operation == AccessOperation.ReadModifyWrite &&
                                                    pair.Second.Operation == AccessOperation.ReadModifyWrite);
        // The member's closure holds the variable; the parallel body's own closure holds only its parameter the nested lambda captures.
        var heap = run.Execution.Heap.Heap;
        var closure = Assert.Single(run.Accesses("sum").Select(access => heap.Regions[access.Resource.RegionId!]).Distinct());
        Assert.DoesNotContain("#lambda", closure.SiteBodyId, StringComparison.Ordinal);
        Assert.Contains(heap.Regions.Values, region => region.Kind == HeapRegionKind.Closure && region.SiteBodyId!.Contains("#lambda", StringComparison.Ordinal));
    }

    [Fact]
    public void A_variable_a_local_function_declares_is_its_own_in_every_call_and_does_not_pair_across_iterations()
    {
        var run = Analyze("""
            public class SeenController : ControllerBase
            {
                public void Post(System.Collections.Generic.List<int> items)
                {
                    Parallel.ForEach(items, x => Count(x));

                    void Count(int x)
                    {
                        var seen = 0;
                        Action add = () => seen += x;
                        add();
                    }
                }
            }
            """ + Startup());

        var accesses = run.Accesses("seen");
        Assert.Contains(accesses, access => access.Operation == AccessOperation.ReadModifyWrite);
        Assert.All(accesses, access => Assert.Equal("closure:SeenController.Post(List<int>)", access.Resource.Region));
        Assert.Empty(run.PairsOn("seen"));
        var closure = Assert.Single(run.Execution.Heap.Heap.Regions.Values, region => region.Kind == HeapRegionKind.Closure);
        Assert.Contains("#local:Count", closure.SiteBodyId, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variable_a_lambda_declares_that_two_tasks_it_starts_write_pairs_within_one_run_of_the_lambda()
    {
        var run = Analyze("""
            public class TallyController : ControllerBase
            {
                public async Task Post()
                {
                    Func<Task> work = async () =>
                    {
                        var count = 0;
                        var first = Task.Run(() => { count++; });
                        var second = Task.Run(() => { count++; });
                        await Task.WhenAll(first, second);
                    };
                    await work();
                }
            }
            """ + Startup());

        Assert.Contains(run.PairsOn("count"), pair => pair.First.Operation == AccessOperation.ReadModifyWrite &&
                                                      pair.Second.Operation == AccessOperation.ReadModifyWrite);
        Assert.All(run.Accesses("count"), access => Assert.Equal("closure:TallyController.Post()", access.Resource.Region));
    }

    [Theory]
    [InlineData("Bump(holder.State);", "static void Bump(S x) { Action a = () => x.Ref.Count++; a(); }")]
    [InlineData("Bump(holder.State);", "static void Bump(S x) { x.Ref.Count++; }")]
    [InlineData("Bump(holder.State);", "static void Bump(S x) { S local = x; Action a = () => local.Ref.Count++; a(); }")]
    [InlineData("Bump(holder.State);", "static void Bump(S x) { void Inner() => x.Ref.Count++; Inner(); }")]
    [InlineData("Bump(holder.State.Ref);", "static void Bump(Item x) { Action a = () => x.Count++; a(); }")]
    public void A_captured_parameters_cell_starts_holding_what_the_argument_holds(string call, string bump)
    {
        // The cell of a captured parameter is filled from the argument when the closure is made: a struct's reference fields keep the
        // objects the caller's copy held, as the same parameter uncaptured does (R2, R4).
        var run = Analyze($$"""
            public sealed class Item { public int Count; }
            public struct S { public Item Ref; }
            public sealed class Holder { public S State = new S { Ref = new Item() }; }
            public class BumpController : ControllerBase
            {
                public void Post([FromServices] Holder holder) { {{call}} }
                {{bump}}
            }
            """ + Startup("services.AddSingleton<Holder>();"));

        var counts = run.Accesses("Count");
        Assert.True(counts.Any(access => access.Operation == AccessOperation.ReadModifyWrite &&
                                         access.Resource.Region.EndsWith("#Item", StringComparison.Ordinal)),
                    string.Join("; ", run.Collection.Accesses.Select(access => $"{access.Symbol} {access.Resource.Region} {string.Join(".", access.Resource.AccessPath)} {access.Operation}")));
        Assert.Contains(run.PairsOn("Count"), pair => pair.First.Operation == AccessOperation.ReadModifyWrite &&
                                                      pair.Second.Operation == AccessOperation.ReadModifyWrite);
    }

    /// <summary>The instances of the body whose id ends with the member's documentation id and the nested suffix given.</summary>
    /// <param name="run">The engine run.</param>
    /// <param name="body">The member's documentation id without its kind prefix, and any nested suffix.</param>
    private static IReadOnlyList<MethodInstance> Instances(EngineRun run, string body) =>
        run.Execution.Heap.Heap.Instances.Values.Where(instance => instance.BodyId.EndsWith(":" + body, StringComparison.Ordinal)).ToArray();
}
