using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A struct kept in heap storage is part of its holder: what its reference fields hold are the holder's slots below the path of
/// the struct (R1), and every copy, store, load, box and call moves what each reference field holds and nothing else, so an object a
/// struct field holds survives each of them and two copies of one struct change apart (R2).</summary>
public sealed class StructStoragePointsToTests
{
    private const string HOLDER = "di:Holder@Singleton";
    private const string ITEM = "alloc:SlotController.Post(Holder)#Item";
    private const string SECOND_ITEM = "alloc:SlotController.Post(Holder)#Item#2";
    private const string VALUE = "Fixture:Slot.Value";

    [Fact]
    public void An_object_in_a_struct_field_survives_a_store_into_a_field()
    {
        var run = Solve(Case("var s = new Slot(); s.Value = new Item(); holder.Kept = s;"));

        Assert.Equal([ITEM], run.Targets(run.Region(HOLDER), $"Fixture:Holder.Kept/{VALUE}"));
        Assert.Empty(run.Targets(run.Region(HOLDER), "Fixture:Holder.Kept"));
        Assert.DoesNotContain(run.Heap.Regions.Values, region => region.Display.EndsWith("#Slot", StringComparison.Ordinal));
    }

    [Fact]
    public void An_object_in_a_struct_field_survives_a_store_into_a_static_field()
    {
        var run = Solve(Case("var s = new Slot(); s.Value = new Item(); Holder.Shared = s;"));

        Assert.Equal([ITEM], run.Targets(run.Region("static:Holder"), $"Fixture:Holder.Shared/{VALUE}"));
    }

    [Fact]
    public void An_object_in_a_struct_field_survives_a_store_into_a_cell()
    {
        var run = Solve(Case("var s = new Slot(); s.Value = new Item(); holder.Cells[0] = s;"));

        Assert.Equal([ITEM], run.Targets(run.Region("alloc:Holder..ctor()#Slot[]"), $"[]/{VALUE}"));
        Assert.Empty(run.Targets(run.Region("alloc:Holder..ctor()#Slot[]"), "[]"));
    }

    [Fact]
    public void An_object_in_a_struct_field_survives_a_load_out_of_a_field_a_static_field_and_a_cell()
    {
        var run = Solve(Case("""
            var s = new Slot(); s.Value = new Item();
            holder.Kept = s; Holder.Shared = s; holder.Cells[1] = s;
            var fromField = holder.Kept; holder.Seen = fromField.Value;
            var fromStatic = Holder.Shared; holder.SeenStatic = fromStatic.Value;
            var fromCell = holder.Cells[0]; holder.SeenCell = fromCell.Value;
            """));

        var holder = run.Region(HOLDER);
        Assert.Equal([ITEM], run.Targets(holder, "Seen"));
        Assert.Equal([ITEM], run.Targets(holder, "SeenStatic"));
        Assert.Equal([ITEM], run.Targets(holder, "SeenCell"));
    }

    [Fact]
    public void An_object_in_a_struct_field_survives_a_by_value_argument()
    {
        var run = Solve(Case("var s = new Slot(); s.Value = new Item(); Take(holder, s);",
                             "private static void Take(Holder target, Slot slot) { target.Seen = slot.Value; slot.Value = null; }"));

        Assert.Equal([ITEM], run.Targets(run.Region(HOLDER), "Seen"));
    }

    [Fact]
    public void An_object_in_a_struct_field_survives_a_return()
    {
        var run = Solve(Case("var made = Make(); holder.Seen = made.Value; holder.SeenCell = Make().Value;",
                             "private static Slot Make() { var s = new Slot(); s.Value = new Item(); return s; }"));

        var made = "alloc:SlotController.Make()#Item";
        Assert.Equal([made], run.Targets(run.Region(HOLDER), "Seen"));
        Assert.Equal([made], run.Targets(run.Region(HOLDER), "SeenCell"));
    }

    [Fact]
    public void An_object_in_a_nested_struct_field_is_the_holder_slot_of_the_whole_path()
    {
        var run = Solve(Case("holder.Nested.Inner.Value = new Item(); var copy = holder.Nested; holder.Seen = copy.Inner.Value;"));

        var holder = run.Region(HOLDER);
        Assert.Equal([ITEM], run.Targets(holder, $"Fixture:Holder.Nested/Fixture:Outer.Inner/{VALUE}"));
        Assert.Equal([ITEM], run.Targets(holder, "Seen"));
    }

    [Fact]
    public void A_box_holds_a_copy_of_the_struct_and_an_unboxing_copies_it_out()
    {
        var run = Solve(Case("var s = new Slot(); s.Value = new Item(); object boxed = s; holder.Boxed = boxed; holder.Seen = ((Slot)boxed).Value;"));

        var box = run.Region("alloc:SlotController.Post(Holder)#Slot");
        Assert.Equal("Fixture:Slot", box.TypeKey);
        Assert.Equal([box.Display], run.Targets(run.Region(HOLDER), "Boxed"));
        Assert.Equal([ITEM], run.Targets(box, VALUE));
        Assert.Equal([ITEM], run.Targets(run.Region(HOLDER), "Seen"));
    }

    [Fact]
    public void Two_copies_of_one_struct_change_apart()
    {
        var run = Solve(Case("var a = new Slot(); a.Value = new Item(); var b = a; b.Value = new Item(); holder.Seen = a.Value; holder.SeenCell = b.Value;"));

        var holder = run.Region(HOLDER);
        Assert.Equal([ITEM], run.Targets(holder, "Seen"));
        // b's field is overwritten before anything uses b, so b holds only what it was given there (R2).
        Assert.Equal([SECOND_ITEM], run.Targets(holder, "SeenCell"));
    }

    [Fact]
    public void A_store_through_a_ref_parameter_and_a_member_called_on_storage_write_the_holder_slot()
    {
        var run = Solve(Case("Fill(ref holder.Kept, new Item()); holder.Nested.Inner.Put(new Item());",
                             "private static void Fill(ref Slot slot, Item item) => slot.Value = item;"));

        var holder = run.Region(HOLDER);
        Assert.Equal([ITEM], run.Targets(holder, $"Fixture:Holder.Kept/{VALUE}"));
        Assert.Equal([SECOND_ITEM], run.Targets(holder, $"Fixture:Holder.Nested/Fixture:Outer.Inner/{VALUE}"));
    }

    [Fact]
    public void A_whole_struct_stored_through_a_reference_is_copied_into_the_storage_it_names()
    {
        var run = Solve(Case("var s = new Slot(); s.Value = new Item(); ref var kept = ref holder.Kept; kept = s; ref var cell = ref holder.Cells[0]; cell = s;"));

        Assert.Equal([ITEM], run.Targets(run.Region(HOLDER), $"Fixture:Holder.Kept/{VALUE}"));
        Assert.Equal([ITEM], run.Targets(run.Region("alloc:Holder..ctor()#Slot[]"), $"[]/{VALUE}"));
    }

    private static string Case(string post, string members = "") => Usings + $$"""
        public sealed class Item { }
        public struct Slot { public Item? Value; public void Put(Item item) { Value = item; } }
        public struct Outer { public Slot Inner; }
        public sealed class Holder
        {
            public Slot Kept;
            public static Slot Shared;
            public readonly Slot[] Cells = new Slot[2];
            public Outer Nested;
            public Item? Seen;
            public Item? SeenStatic;
            public Item? SeenCell;
            public object? Boxed;
        }
        public class SlotController : ControllerBase
        {
            public void Post([FromServices] Holder holder)
            {
                {{post}}
            }

            {{members}}
        }
        """ + Startup("services.AddSingleton<Holder>();");
}
