using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A <c>Nullable</c> boxes as C# boxes it (R2): a full one into one object of the struct it holds — never of <c>Nullable</c> —
/// whose reference field holds the object the value's did, and on which an interface member reaches the box; an empty one into
/// <c>null</c>, no object and no access. An unboxing, to the struct or to its <c>Nullable</c>, reads the box's whole storage, the empty
/// path, and keeps the object in the copy it takes; of an empty one it reads nothing. An empty one unboxed to the struct itself throws,
/// so that row is not here.</summary>
public sealed class NullableBoxingTests
{
    public enum Presence { Full, Empty }

    public enum Destination { Object, Interface }

    public enum Unboxing { None, Struct, Nullable }

    private const string BOX = "alloc:PairController.Post(Holder)#S";
    private const string ITEM = "alloc:PairController.Post(Holder)#Item";

    public static TheoryData<Presence, Destination, Unboxing> Rows()
    {
        var rows = new TheoryData<Presence, Destination, Unboxing>();
        foreach (var presence in Enum.GetValues<Presence>())
        foreach (var destination in Enum.GetValues<Destination>())
        foreach (var unboxing in Enum.GetValues<Unboxing>())
        {
            if (presence == Presence.Empty && unboxing == Unboxing.Struct)
                continue;
            rows.Add(presence, destination, unboxing);
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Nullable_boxes_and_unboxes_as_csharp_does(Presence presence, Destination destination, Unboxing unboxing)
    {
        var run = Analyze(Case(presence, destination, unboxing));
        var heap = run.Execution.Heap.Heap;
        var boxes = heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Allocation && region.TypeKey is "Fixture:S" or "System.Private.CoreLib:System.Nullable<Fixture:S>")
                        .ToArray();
        var post = Assert.Single(heap.Instances.Values, instance => instance.BodyId.Contains("PairController.Post", StringComparison.Ordinal));
        // The copies an unboxing takes are those a copy of the body takes out of a box.
        var unboxed = post.Summary.StructCopies.Where(copy => copy.Sources.OfType<AllocationValue>().Any())
                          .SelectMany(copy => copy.Targets).SelectMany(target => heap.Resolve(post.Id, target))
                          .Where(region => heap.Regions[region].Kind == HeapRegionKind.StructCopy)
                          .ToArray();
        var wholeReads = run.Collection.Accesses.Where(access => access.Resource.AccessPath.Count == 0).ToArray();

        if (presence == Presence.Full)
        {
            var box = Assert.Single(boxes);
            Assert.Equal(BOX, box.Display);
            Assert.Equal("Fixture:S", box.TypeKey);
            Assert.Equal([ITEM], heap.PointsTo(box.Identity, "Fixture:S.Value").Select(region => heap.Regions[region].Display));
            Assert.Contains(run.Collection.Accesses, access => access.Resource.Region == BOX && access.Resource.AccessPath.SequenceEqual(["Count"]));
            if (unboxing == Unboxing.None)
            {
                Assert.Empty(wholeReads);
            }
            else
            {
                var read = Assert.Single(wholeReads);
                Assert.Equal(BOX, read.Resource.Region);
                Assert.Equal(AccessOperation.Read, read.Operation);
                var copy = Assert.Single(unboxed);
                Assert.Equal([ITEM], heap.PointsTo(copy, "Fixture:S.Value").Select(region => heap.Regions[region].Display));
            }
        }
        else
        {
            Assert.Empty(boxes);
            Assert.DoesNotContain(run.Collection.Accesses, access => access.Resource.AccessPath is [] or ["Count"] or ["Value"]);
            Assert.All(unboxed, copy => Assert.Empty(heap.PointsTo(copy, "Fixture:S.Value")));
        }
    }

    [Theory]
    [InlineData("S? held = new S { Value = new Item() }; holder.Seen = held.Value.Value;")]
    [InlineData("S? held = new S { Value = new Item() }; holder.Seen = held.GetValueOrDefault().Value;")]
    [InlineData("S? held = new S { Value = new Item() }; holder.Seen = held.GetValueOrDefault(new S()).Value;")]
    [InlineData("S? held = new S { Value = new Item() }; holder.Seen = ((S)held).Value;")]
    [InlineData("var held = new S?(new S { Value = new Item() }); holder.Seen = held.Value.Value;")]
    [InlineData("holder.Again = new S { Value = new Item() }; holder.Seen = holder.Again.Value.Value;")]
    public void What_a_nullable_gives_back_holds_what_its_value_holds(string body)
    {
        // A Nullable holds its value as a struct holds its fields (R1, R2): Value, GetValueOrDefault and the conversion give the struct back,
        // and its reference field the object it was made with.
        var run = Analyze(Usings + $$"""
            public sealed class Item { }
            public struct S { public Item? Value; }
            public sealed class Holder { public Item? Seen; public S? Again; }
            public class PairController : ControllerBase
            {
                public void Post([FromServices] Holder holder)
                {
                    {{body}}
                }
            }
            """ + Startup("services.AddSingleton<Holder>();"));
        var heap = run.Execution.Heap.Heap;

        var holder = Assert.Single(heap.Regions.Values, region => region.TypeKey == "Fixture:Holder");
        Assert.Equal([ITEM], heap.PointsTo(holder.Identity, "Fixture:Holder.Seen").Select(region => heap.Regions[region].Display));
    }

    [Theory]
    [InlineData("static S? Make() => null;", "object boxed = Make();", false)]
    [InlineData("static S? Make() => new S { Value = new Item() };", "object boxed = Make();", true)]
    [InlineData("static S? Make() => null;", "S? held = Make(); object boxed = held;", false)]
    [InlineData("static S? Make() => new S { Value = new Item() };", "S? held = Make(); object boxed = held;", true)]
    [InlineData("static object Box(S? value) => value;", "object boxed = Box(null);", false)]
    [InlineData("static object Box(S? value) => value;", "object boxed = Box(new S { Value = new Item() });", true)]
    public void A_nullable_boxes_only_where_a_struct_reaches_the_boxing(string helper, string body, bool full)
    {
        // Whether a Nullable the body cannot tell empty — a call's result, a copy of one, a parameter — holds a struct is what reaches it
        // in the heap: an empty one boxes to null and makes no box (R2).
        var run = Analyze(Usings + $$"""
            public sealed class Item { }
            public struct S { public Item? Value; }
            public sealed class Holder { public object? Kept; }
            public class PairController : ControllerBase
            {
                {{helper}}
                public void Post([FromServices] Holder holder)
                {
                    {{body}}
                    holder.Kept = boxed;
                }
            }
            """ + Startup("services.AddSingleton<Holder>();"));
        var heap = run.Execution.Heap.Heap;

        var boxes = heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Allocation && region.TypeKey == "Fixture:S").ToArray();
        Assert.Equal(full ? 1 : 0, boxes.Length);
        var holder = Assert.Single(heap.Regions.Values, region => region.TypeKey == "Fixture:Holder");
        Assert.Equal(full, heap.PointsTo(holder.Identity, "Fixture:Holder.Kept").Count != 0);
    }

    [Theory]
    [InlineData("", "holder.State", false)]
    [InlineData("holder.Fill();", "holder.State", true)]
    [InlineData("holder.Clear();", "holder.State", false)]
    [InlineData("", "Holder.Kept", false)]
    [InlineData("Holder.Kept = new S();", "Holder.Kept", true)]
    [InlineData("holder.Cells[0] = new S();", "holder.Cells[0]", true)]
    [InlineData("", "holder.Cells[0]", false)]
    [InlineData("holder.Other = holder.State;", "holder.Other", false)]
    [InlineData("holder.Fill(); holder.Other = holder.State;", "holder.Other", true)]
    [InlineData("if (holder.Flag) holder.Fill(); holder.Other = holder.State;", "holder.Other", true)]
    [InlineData("var copy = holder.State; holder.Other = copy;", "holder.Other", false)]
    [InlineData("holder.Other = holder.Flag ? holder.State : null;", "holder.Other", false)]
    [InlineData("holder.Other = holder.Flag ? holder.State : new S();", "holder.Other", true)]
    [InlineData("Holder.Kept = holder.State;", "Holder.Kept", false)]
    [InlineData("holder.Cells[1] = holder.State;", "holder.Cells[0]", false)]
    [InlineData("holder.Cells[1] = holder.Cells[2]; holder.Other = holder.Cells[1];", "holder.Other", false)]
    [InlineData("holder.Cells[1] = new S(); holder.Other = holder.Cells[2];", "holder.Other", true)]
    public void A_nullable_read_from_storage_boxes_only_where_a_struct_was_stored_there(string setup, string read, bool full)
    {
        // Storage a Nullable occupies holds its default, an empty one, until a struct is stored there: boxing what it holds makes no box and
        // runs no member where nothing was, and does where something was (R2). A copy carries the presence of what it is taken from, so
        // storage a copy of empty storage was stored into is as empty, through a local, a conditional or another storage.
        var run = Analyze(Usings + $$"""
            public interface IInc { void Inc(); }
            public static class Counter { public static int N; }
            public struct S : IInc { public void Inc() { Counter.N++; } }
            public sealed class Holder
            {
                public static S? Kept;
                public S? State;
                public S? Other;
                public bool Flag;
                public readonly S?[] Cells = new S?[4];
                public void Fill() { State = new S(); }
                public void Clear() { State = null; }
            }
            public class PairController : ControllerBase
            {
                public void Post([FromServices] Holder holder)
                {
                    {{setup}}
                    object box = {{read}};
                    if (box is IInc i) i.Inc();
                }
            }
            """ + Startup("services.AddSingleton<Holder>();"));
        var heap = run.Execution.Heap.Heap;

        Assert.Equal(full ? 1 : 0, heap.Regions.Values.Count(region => region.Kind == HeapRegionKind.Allocation && region.TypeKey == "Fixture:S"));
        Assert.Equal(full, run.Accesses("N").Any());
        Assert.Equal(full, run.PairsOn("N").Any());
    }

    private static string Case(Presence presence, Destination destination, Unboxing unboxing)
    {
        var value = presence == Presence.Full ? "new S { Value = new Item() }" : "null";
        var (type, call) = destination == Destination.Object ? ("object", "((IFace)boxed).Touch();") : ("IFace", "boxed.Touch();");
        // Nothing here boxes again: the unboxed copy is kept in a field of its type.
        var unbox = unboxing switch
        {
            Unboxing.Struct => "var copy = (S)boxed; holder.Seen = copy.Value;",
            Unboxing.Nullable => "var copy = (S?)boxed; holder.Again = copy;",
            _ => ""
        };
        return Usings + $$"""
            public sealed class Item { }
            public interface IFace { void Touch(); }
            public struct S : IFace { public Item? Value; public int Count; public void Touch() { Count++; } }
            public sealed class Holder { public object? Kept; public Item? Seen; public S? Again; }
            public class PairController : ControllerBase
            {
                public void Post([FromServices] Holder holder)
                {
                    S? held = {{value}};
                    {{type}} boxed = held;
                    holder.Kept = boxed;
                    {{call}}
                    {{unbox}}
                }
            }
            """ + Startup("services.AddSingleton<Holder>();");
    }
}
