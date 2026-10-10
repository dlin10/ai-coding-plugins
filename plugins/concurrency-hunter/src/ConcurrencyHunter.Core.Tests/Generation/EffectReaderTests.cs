using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The effects, state stores and refusal evidence read from a generated driver's accesses.</summary>
public sealed class EffectReaderTests
{
    [Fact]
    public void A_field_read_gives_reads_deep()
    {
        var reader = Read("public abstract class Node { public object Value; } public static class Api { public static void Read(Node node, Action done) { _ = node.Value; done(); } }",
                          "M:Lib.Api.Read(Lib.Node,System.Action)");

        Assert.True(reader.HasEffect("node", EffectReader.READS_DEEP));
    }

    [Fact]
    public void A_write_to_the_arguments_field_gives_writes_arg()
    {
        var reader = Read("public abstract class Node { public object Value; } public static class Api { public static void Write(Node node, Action done) { node.Value = new object(); done(); } }",
                          "M:Lib.Api.Write(Lib.Node,System.Action)");

        Assert.True(reader.HasEffect("node", EffectReader.WRITES_ARGUMENT));
    }

    [Fact]
    public void A_write_to_a_subobjects_field_gives_vocabulary()
    {
        var reader = Read("public sealed class Child { public int Value; } public abstract class Node { public Child Child = new(); } " +
                          "public static class Api { public static void Write(Node node, Action done) { node.Child.Value = 1; done(); } }",
                          "M:Lib.Api.Write(Lib.Node,System.Action)");

        Assert.Equal(GenerationReasons.VOCABULARY, reader.Reason);
    }

    [Fact]
    public void A_write_to_an_array_elements_field_gives_vocabulary_not_writes_arg()
    {
        var reader = Read("public abstract class Node { public object Value; } public static class Api { public static void Write(Node[] nodes, Action done) { nodes[0].Value = new object(); done(); } }",
                          "M:Lib.Api.Write(Lib.Node[],System.Action)");

        Assert.Equal(GenerationReasons.VOCABULARY, reader.Reason);
        Assert.False(reader.HasEffect("nodes", EffectReader.WRITES_ARGUMENT));
    }

    [Fact]
    public void A_read_from_an_object_the_probe_reaches_gives_reads_deep()
    {
        var reader = Read("public sealed class Child { public object Value; } public abstract class Node { public Child Child = new(); } " +
                          "public static class Api { public static void Read(Node node, Action done) { _ = node.Child.Value; done(); } }",
                          "M:Lib.Api.Read(Lib.Node,System.Action)");

        Assert.True(reader.HasEffect("node", EffectReader.READS_DEEP));
    }

    [Fact]
    public void An_array_cell_write_gives_writes_cells()
    {
        var reader = Read("public static class Api { public static void Write(object[] values, Action done) { values[0] = new object(); done(); } }",
                          "M:Lib.Api.Write(System.Object[],System.Action)");

        Assert.True(reader.HasEffect("values", EffectReader.WRITES_CELLS));
    }

    [Fact]
    public void List_add_on_an_argument_gives_vocabulary()
    {
        var reader = Read("public static class Api { public static void Add(List<object> values, Action done) { values.Add(new object()); done(); } }",
                          "M:Lib.Api.Add(System.Collections.Generic.List{System.Object},System.Action)");

        Assert.Equal(GenerationReasons.VOCABULARY, reader.Reason);
    }

    [Fact]
    public void A_probe_handed_to_an_extern_library_method_gives_unknown_touch()
    {
        var reader = Read("public abstract class Node { } public static class Api { public static void Send(Node node, Action done) { Sink.Take(node); done(); } }",
                          "M:Lib.Api.Send(Lib.Node,System.Action)");

        Assert.Equal(GenerationReasons.UNKNOWN_TOUCH, reader.Reason);
    }

    [Fact]
    public void A_value_handed_to_a_witness_member_gives_reads_deep()
    {
        var reader = Read("public abstract class Value { public object Item; } public abstract class Consumer { public abstract void Take(Value value); } " +
                          "public static class Api { public static void Send(Consumer consumer, Value value, Action done) { consumer.Take(value); done(); } }",
                          "M:Lib.Api.Send(Lib.Consumer,Lib.Value,System.Action)");

        Assert.True(reader.HasEffect("value", EffectReader.READS_DEEP));
    }

    [Fact]
    public void An_argument_whose_seeded_member_is_called_gives_reads_deep()
    {
        var reader = Read("public abstract class Converter { public abstract void Convert(); } public sealed class Options { public Converter Converter; } " +
                          "public static class Api { public static void Run(Options options, Action done) { options.Converter.Convert(); done(); } }",
                          "M:Lib.Api.Run(Lib.Options,System.Action)");

        Assert.True(reader.HasEffect("options", EffectReader.READS_DEEP));
    }

    [Fact]
    public void An_extern_call_handed_a_seed_and_its_argument_gives_reads_deep()
    {
        var reader = Read("public static class External { public static extern void Pair(object first, object second); } " +
                          "public abstract class Converter { } public sealed class Options { public Converter Converter; } " +
                          "public static class Api { public static void Run(Options options, Action done) { External.Pair(options.Converter, options); done(); } }",
                          "M:Lib.Api.Run(Lib.Options,System.Action)");

        Assert.True(reader.HasEffect("options", EffectReader.READS_DEEP));
        Assert.NotEqual(GenerationReasons.UNKNOWN_TOUCH, reader.Reason);
    }

    [Fact]
    public void Setup_writes_give_no_effect()
    {
        var reader = Read("public abstract class Converter { } public sealed class Options { public Converter Converter; } " +
                          "public static class Api { public static void Run(Options options, Action done) => done(); }",
                          "M:Lib.Api.Run(Lib.Options,System.Action)");

        Assert.False(reader.HasEffect("options", EffectReader.WRITES_ARGUMENT));
        Assert.False(reader.HasEffect("options", EffectReader.WRITES_CELLS));
    }

    [Fact]
    public void A_load_of_an_unseeded_struct_field_gives_incomplete()
    {
        var reader = Read("public struct Part { public object Value; } public sealed class Options { public Part Part; } " +
                          "public static class Api { public static void Read(Options options, Action done) { _ = options.Part.Value; done(); } }",
                          "M:Lib.Api.Read(Lib.Options,System.Action)");

        Assert.Equal(GenerationReasons.INCOMPLETE, reader.Reason);
    }

    [Fact]
    public void Awaiting_a_task_of_object_in_the_call_gives_its_synchronous_twins_reason()
    {
        // The heap carries what the task completes with, so the await leaves nothing incomplete: the twin reads the value directly.
        var reader = Read("public static class Api { public static async Task Run(Task<object> value, Action done) { _ = await value; done(); } }",
                          "M:Lib.Api.Run(System.Threading.Tasks.Task{System.Object},System.Action)");
        var twin = Read("public static class Api { public static void Run(object value, Action done) { _ = value; done(); } }",
                        "M:Lib.Api.Run(System.Object,System.Action)");

        Assert.Null(twin.Reason);
        Assert.Equal(twin.Reason, reader.Reason);
    }

    [Fact]
    public void A_seeded_field_mixed_with_an_unknown_value_and_a_probe_gives_unknown_touch()
    {
        var reader = Read("public static class External { public static extern object Make(); public static extern void Pair(object a, object b); } " +
                          "public sealed class Options { public object Value; } public abstract class Node { } " +
                          "public static class Api { public static void Run(Options options, Node node, Action done) { options.Value = External.Make(); External.Pair(options.Value, node); done(); } }",
                          "M:Lib.Api.Run(Lib.Options,Lib.Node,System.Action)");

        Assert.Equal(GenerationReasons.UNKNOWN_TOUCH, reader.Reason);
    }

    [Fact]
    public void A_probe_handed_to_an_extern_method_only_in_setup_gives_no_unknown_touch()
    {
        var reader = Read("public abstract class Node { protected Node() { Sink.Take(this); } } " +
                          "public static class Api { public static void Run(Node node, Action done) => done(); }",
                          "M:Lib.Api.Run(Lib.Node,System.Action)");

        Assert.NotEqual(GenerationReasons.UNKNOWN_TOUCH, reader.Reason);
    }

    [Fact]
    public void Probe_lambda_input_stores_are_not_argument_writes()
    {
        var reader = Read("public abstract class Node { public object Value; } public static class Api { public static void Run(Node node, Action<Node> callback) => callback(node); }",
                          "M:Lib.Api.Run(Lib.Node,System.Action{Lib.Node})");

        Assert.False(reader.HasEffect("node", EffectReader.WRITES_ARGUMENT));
    }

    [Fact]
    public void A_probe_touched_in_an_unknown_execution_gives_unknown_touch()
    {
        var reader = Read("public abstract class Node { public object Value; } public static class Api { public static void Later(Node node, Action done) { Sink.Take(new Action(() => { _ = node.Value; })); done(); } }",
                          "M:Lib.Api.Later(Lib.Node,System.Action)");

        Assert.Equal(GenerationReasons.UNKNOWN_TOUCH, reader.Reason);
    }

    [Fact]
    public void A_probe_captured_by_an_escaped_library_lambda_gives_unknown_touch()
    {
        var reader = Read("public abstract class Node { } public static class Api { public static void Later(Node node, Action done) { Sink.Take(new Action(() => GC.KeepAlive(node))); done(); } }",
                          "M:Lib.Api.Later(Lib.Node,System.Action)");

        Assert.Equal(GenerationReasons.UNKNOWN_TOUCH, reader.Reason);
    }

    [Fact]
    public void Escape_artefact_enumeration_gives_no_unknown_touch()
    {
        var reader = Read("public abstract class Node { public object Value; } public static class Api { public static IEnumerable<Node> Lazy(Node node, Action done) { _ = node.Value; done(); yield return node; } }",
                          "M:Lib.Api.Lazy(Lib.Node,System.Action)");

        Assert.NotEqual(GenerationReasons.UNKNOWN_TOUCH, reader.Reason);
    }

    [Fact]
    public void A_write_into_a_preexisting_library_object_is_a_state_store()
    {
        var reader = Read("public sealed class State { public int Value; } public static class Shared { public static readonly State Value = new(); } " +
                          "public static class Api { public static void Write(Action done) { Shared.Value.Value = 1; done(); } }",
                          "M:Lib.Api.Write(System.Action)");

        Assert.NotEmpty(reader.StateStores);
    }

    [Fact]
    public void A_write_into_a_region_the_call_created_is_not_a_state_store()
    {
        var reader = Read("public sealed class State { public int Value; } public static class Api { public static void Write(Action done) { var state = new State(); state.Value = 1; done(); } }",
                          "M:Lib.Api.Write(System.Action)");

        Assert.Empty(reader.StateStores);
    }

    [Fact]
    public void A_constructor_members_object_is_not_a_state_store()
    {
        var reader = Read("public sealed class Made { public int Value; public Made(Action done) { Value = 1; done(); } }",
                          "M:Lib.Made.#ctor(System.Action)");

        Assert.Empty(reader.StateStores);
    }

    [Fact]
    public void A_library_argument_object_built_in_setup_is_state()
    {
        var reader = Read("public sealed class Made { public int Value; } " +
                          "public static class Api { public static void Write(Made value, Action done) { value.Value = 1; done(); } }",
                          "M:Lib.Api.Write(Lib.Made,System.Action)");

        Assert.NotEmpty(reader.StateStores);
    }

    [Fact]
    public void A_write_to_a_library_base_field_of_a_sub_receiver_is_state()
    {
        var reader = Read("public abstract class Base { protected object Value; public void Write(Action done) { Value = new object(); done(); } }",
                          "M:Lib.Base.Write(System.Action)");

        Assert.NotEmpty(reader.StateStores);
    }

    [Fact]
    public void A_library_read_of_a_sub_argument_gives_reads_deep()
    {
        var reader = Read("public abstract class Node { public object Value; } public static class Api { public static void Read(Node node, Action done) { _ = node.Value; done(); } }",
                          "M:Lib.Api.Read(Lib.Node,System.Action)");

        Assert.True(reader.HasEffect("node", EffectReader.READS_DEEP));
    }

    [Fact]
    public void A_read_of_a_probe_delegates_returned_object_gives_vocabulary()
    {
        var reader = Read("public abstract class Node { public object Value; } public static class Api { public static void Read(Func<Node> make) { var node = make(); _ = node.Value; } }",
                          "M:Lib.Api.Read(System.Func{Lib.Node})");

        Assert.Equal(GenerationReasons.VOCABULARY, reader.Reason);
    }

    [Fact]
    public void A_write_to_a_probe_delegates_returned_object_gives_vocabulary()
    {
        var reader = Read("public abstract class Node { public object Value; } public static class Api { public static void Write(Func<Node> make) { make().Value = new object(); } }",
                          "M:Lib.Api.Write(System.Func{Lib.Node})");

        Assert.Equal(GenerationReasons.VOCABULARY, reader.Reason);
    }

    [Fact]
    public void A_probe_delegates_returned_object_handed_to_extern_gives_unknown_touch()
    {
        var reader = Read("public abstract class Node { } public static class Api { public static void Send(Func<Node> make) => Sink.Take(make()); }",
                          "M:Lib.Api.Send(System.Func{Lib.Node})");

        Assert.Equal(GenerationReasons.UNKNOWN_TOUCH, reader.Reason);
    }

    [Fact]
    public void Foreach_over_an_array_is_an_enumeration_read()
    {
        var reader = Read("public abstract class Node { public object Value; } public static class Api { public static void Read(Node[] nodes, Action done) { foreach (var node in nodes) _ = node.Value; done(); } }",
                          "M:Lib.Api.Read(Lib.Node[],System.Action)");

        Assert.Contains(Effect(reader, "nodes", EffectReader.READS_DEEP).EnumerationRoots, root => root == DriverSynthesizer.CALL);
    }

    [Fact]
    public void A_list_indexer_read_is_not_an_enumeration_read()
    {
        var reader = Read("public abstract class Node { public object Value; } public static class Api { public static void Read(List<Node> nodes, Action done) { _ = nodes[0].Value; done(); } }",
                          "M:Lib.Api.Read(System.Collections.Generic.List{Lib.Node},System.Action)");

        Assert.Empty(Effect(reader, "nodes", EffectReader.READS_DEEP).EnumerationRoots);
    }

    [Fact]
    public void A_list_count_read_is_not_an_enumeration_read()
    {
        var reader = Read("public abstract class Node { } public static class Api { public static void Read(List<Node> nodes, Action done) { _ = nodes.Count; done(); } }",
                          "M:Lib.Api.Read(System.Collections.Generic.List{Lib.Node},System.Action)");

        Assert.Empty(Effect(reader, "nodes", EffectReader.READS_DEEP).EnumerationRoots);
    }

    [Fact]
    public void An_unreached_seed_whose_field_is_later_loaded_gives_incomplete()
    {
        var reader = Read("public abstract class Value { } public sealed class Child { public Value Value; } public sealed class Options { public Child Child; } " +
                          "public static class Api { public static void Read(Options options, Action done) { options.Child ??= new Child(); _ = options.Child.Value; done(); } }",
                          "M:Lib.Api.Read(Lib.Options,System.Action)");

        Assert.Equal(GenerationReasons.INCOMPLETE, reader.Reason);
        Assert.NotEmpty(reader.UnreachedSeeds);
    }

    [Fact]
    public void A_seed_member_receiving_another_argument_reads_that_argument_deep()
    {
        var reader = Read("public abstract class Value { } public abstract class Converter { public abstract void Convert(Value value); } " +
                          "public sealed class Options { public Converter Converter; } public static class Api { public static void Run(Options options, Value value, Action done) { options.Converter.Convert(value); done(); } }",
                          "M:Lib.Api.Run(Lib.Options,Lib.Value,System.Action)");

        Assert.True(reader.HasEffect("value", EffectReader.READS_DEEP));
    }

    [Fact]
    public void Allocations_created_by_call_is_false_when_the_site_also_ran_in_setup()
    {
        var trace = Trace("public sealed class Made { private Made() { } public static Made Create() => new Made(); } " +
                          "public static class Api { public static void Run(Made made, Action done) { _ = Made.Create(); done(); } }",
                          "M:Lib.Api.Run(Lib.Made,System.Action)");
        var (allocations, _) = AllocationContext(trace);

        Assert.Contains(trace.Run!.Heap!.Regions.Values.Where(region => region.TypeKey == $"{ASSEMBLY}:Lib.Made"),
                        region => !allocations.CreatedByCall(region.Identity));
    }

    [Fact]
    public void Allocations_created_by_call_is_true_for_a_call_tree_allocation()
    {
        var trace = Trace("public sealed class Made { } public static class Api { public static void Run(Action done) { Cache.Last = new Made(); done(); } }",
                          "M:Lib.Api.Run(System.Action)");
        var (allocations, _) = AllocationContext(trace);
        var made = trace.Run!.Heap!.Regions.Values.Single(region => region.TypeKey == $"{ASSEMBLY}:Lib.Made");

        Assert.True(allocations.CreatedByCall(made.Identity));
    }

    [Fact]
    public void An_object_a_seed_member_returned_and_handed_to_extern_gives_vocabulary()
    {
        var reader = Read("public abstract class Converter { public abstract object Value { get; } } public sealed class Options { public Converter Converter; } " +
                          "public static class Api { public static void Run(Options options, Action done) { Sink.Take(options.Converter.Value); done(); } }",
                          "M:Lib.Api.Run(Lib.Options,System.Action)");

        Assert.Equal(GenerationReasons.VOCABULARY, reader.Reason);
    }

    [Fact]
    public void A_write_made_only_in_v_enum_keeps_that_root()
    {
        var reader = Read("public abstract class Node { public object Value; } public static class Api { public static IEnumerable<int> Lazy(Node node, Action done) { node.Value = new object(); done(); yield return 1; } }",
                          "M:Lib.Api.Lazy(Lib.Node,System.Action)");

        Assert.Equal([DriverSynthesizer.ENUMERATE], Effect(reader, "node", EffectReader.WRITES_ARGUMENT).Roots);
    }

    [Fact]
    public void A_write_into_a_ref_struct_parameters_own_reference_field_gives_vocabulary()
    {
        var reader = Read("public struct Item { public object Value; } public static class Api { public static void Write(ref Item item, Action done) { item.Value = new object(); done(); } }",
                          "M:Lib.Api.Write(Lib.Item@,System.Action)");

        Assert.Equal(GenerationReasons.VOCABULARY, reader.Reason);
    }

    [Fact]
    public void A_write_into_an_out_struct_parameters_storage_gives_vocabulary()
    {
        var reader = Read("public struct Item { public object Value; } public static class Api { public static void Write(out Item item, Action done) { item = default; item.Value = new object(); done(); } }",
                          "M:Lib.Api.Write(Lib.Item@,System.Action)");

        Assert.Equal(GenerationReasons.VOCABULARY, reader.Reason);
    }

    [Fact]
    public void A_struct_receiver_writing_its_own_field_gives_vocabulary()
    {
        var reader = Read("public struct Item { public object Value; public void Write(Action done) { Value = new object(); done(); } }",
                          "M:Lib.Item.Write(System.Action)");

        Assert.Equal(GenerationReasons.VOCABULARY, reader.Reason);
    }

    [Fact]
    public void A_by_value_struct_parameter_writing_its_own_field_gives_no_effect()
    {
        var reader = Read("public struct Item { public object Value; } public static class Api { public static void Write(Item item, Action done) { item.Value = new object(); done(); } }",
                          "M:Lib.Api.Write(Lib.Item,System.Action)");

        Assert.Null(reader.Reason);
        Assert.False(reader.HasEffect("item", EffectReader.WRITES_ARGUMENT));
    }

    [Fact]
    public void An_array_cell_write_is_not_a_state_store()
    {
        var reader = Read("public static class Api { public static void Write(object[] values, Action done) { values[0] = new object(); done(); } }",
                          "M:Lib.Api.Write(System.Object[],System.Action)");

        Assert.True(reader.HasEffect("values", EffectReader.WRITES_CELLS));
        Assert.Empty(reader.StateStores);
    }

    [Fact]
    public void A_library_store_through_a_witness_ref_return_gives_vocabulary()
    {
        // The receiver's own member, which no argument brought.
        var reader = Read("public abstract class Provider { public abstract ref object Get(); public void Write(Action done) { Get() = new object(); done(); } }",
                          "M:Lib.Provider.Write(System.Action)");

        Assert.Equal(GenerationReasons.VOCABULARY, reader.Reason);
    }

    [Fact]
    public void A_read_effect_keeps_call_and_enumeration_roots_separately()
    {
        var reader = Read("public abstract class Node { public object Value; } public static class Api { public static IEnumerable<int> Both(Node node, Action done) { _ = node.Value; done(); yield return 1; } }",
                          "M:Lib.Api.Both(Lib.Node,System.Action)");

        Assert.Contains(DriverSynthesizer.ENUMERATE, Effect(reader, "node", EffectReader.READS_DEEP).Roots);
    }

    [Fact]
    public void A_plain_library_argument_field_load_is_not_an_effect()
    {
        var reader = Read("public sealed class Plain { public int Count; } public static class Api { public static void Read(Plain value, Action done) { _ = value.Count; done(); } }",
                          "M:Lib.Api.Read(Lib.Plain,System.Action)");

        Assert.False(reader.HasEffect("value", EffectReader.READS_DEEP));
    }

    [Theory]
    [InlineData("public object Value { get; set; }", "options.Part.Value")]
    [InlineData("public Nested Value { get; set; }", "options.Part.Value.Item")]
    public void Reading_reference_storage_of_a_struct_auto_property_gives_incomplete(string declaration, string expression)
    {
        var trace = Trace($"public struct Nested {{ public object Item {{ get; set; }} }} public struct Part {{ {declaration} }} " +
                          $"public sealed class Options {{ public Part Part; }} public static class Api {{ public static void Run(Options options, Action done) {{ _ = {expression}; done(); }} }}",
                          "M:Lib.Api.Run(Lib.Options,System.Action)");

        Assert.Null(trace.Answer.Model);
        Assert.Equal(GenerationReasons.INCOMPLETE, trace.Answer.ModelReason);
    }

    [Fact]
    public void Unread_struct_auto_property_storage_does_not_refuse_a_model()
    {
        var trace = Trace("public struct Part { public object Value { get; set; } } public sealed class Options { public Part Part; } " +
                          "public static class Api { public static void Run(Options options, Action done) => done(); }",
                          "M:Lib.Api.Run(Lib.Options,System.Action)");

        Assert.NotNull(trace.Answer.Model);
    }

    [Theory]
    [InlineData("List<Child>", "new() { new Child() }", "options.Children[0].Value", null)]
    [InlineData("IEnumerable<Child>", "new List<Child> { new Child() }", "First(options.Children).Value", GenerationReasons.VOCABULARY)]
    [InlineData("Child[]", "new[] { new Child() }", "options.Children[0].Value", null)]
    public void Seeding_a_container_also_covers_the_elements_its_constructor_created(string type, string initializer, string expression,
                                                                                    string? reason)
    {
        var trace = Trace("public sealed class Child { public object Value; } " +
                          $"public sealed class Options {{ public {type} Children = {initializer}; }} public static class Api {{ " +
                          "private static Child First(IEnumerable<Child> values) { foreach (var value in values) return value; return null; } " +
                          $"public static void Run(Options options, Action done) {{ {expression}.ToString(); done(); }} }}",
                          "M:Lib.Api.Run(Lib.Options,System.Action)");

        // A user enumerable seed in the argument is the argument's code: enumerating it refuses first, and the case of what it yielded
        // stays named.
        if (reason is null)
            Assert.Null(trace.Answer.ModelReason);
        else
            Assert.Contains(trace.Answer.Causes, cause => cause.Reason == reason);
        var heap = trace.Run!.Heap!;
        var reachability = new HeapReachability(heap);
        var children = heap.Regions.Values.Where(region => region.TypeKey == $"{ASSEMBLY}:Lib.Child").ToArray();
        Assert.Contains(children, region => region.SiteBodyId!.StartsWith($"body:{ASSEMBLY}:", StringComparison.Ordinal));
        Assert.All(children, child => Assert.Contains(reachability.From([child.Identity]), region =>
            heap.Regions[region].TypeKey?.StartsWith($"{DriverSynthesizer.ASSEMBLY}:Seed_", StringComparison.Ordinal) == true));
        Assert.True(new EffectReader(trace.Driver!, trace.Run).HasEffect("options", EffectReader.READS_DEEP));
    }

    [Theory]
    [InlineData("Action<object>")]
    [InlineData("Func<object, object>")]
    public void An_unknown_call_handed_a_delegate_seed_and_its_argument_reads_the_argument_deep(string type)
    {
        var trace = Trace($"public sealed class Options {{ public {type} Callback; }} " +
                          "public static class External { public static extern void Pair(object a, object b); } " +
                          "public static class Api { public static void Run(Options options) => External.Pair(options.Callback, options); }",
                          "M:Lib.Api.Run(Lib.Options)");

        Assert.NotNull(trace.Answer.Model);
        Assert.True(new EffectReader(trace.Driver!, trace.Run!).HasEffect("options", EffectReader.READS_DEEP));
    }

    [Fact]
    public void A_delegate_seed_handed_to_unknown_code_only_in_setup_adds_no_call_effect()
    {
        var trace = Trace("public sealed class Options { public Action<object> Callback; public Options() { Sink.Take(this); } } " +
                          "public static class Api { public static void Run(Options options) { } }", "M:Lib.Api.Run(Lib.Options)");

        Assert.NotNull(trace.Answer.Model);
        Assert.False(new EffectReader(trace.Driver!, trace.Run!).HasEffect("options", EffectReader.READS_DEEP));
    }

    [Theory]
    [InlineData("_ = make().Value;")]
    [InlineData("make().Value = new object();")]
    public void Touching_a_generic_probe_lambdas_return_gives_vocabulary(string body)
    {
        var trace = Trace($"public abstract class Node {{ public object Value; }} public static class Api {{ public static void Run<T>(Func<T> make) where T : Node {{ {body} }} }}",
                          "M:Lib.Api.Run``1(System.Func{``0})");

        Assert.Null(trace.Answer.Model);
        Assert.Equal(GenerationReasons.VOCABULARY, trace.Answer.ModelReason);
    }

    [Fact]
    public void Touching_a_probe_object_returned_by_a_witness_gives_vocabulary()
    {
        var trace = Trace("public abstract class Node { public object Value; } public interface IUser<T> { T Made { get; } } " +
                          "public static class Api { public static void Run<T>(IUser<T> user) where T : Node { _ = user.Made.Value; } }",
                          "M:Lib.Api.Run``1(Lib.IUser{``0})");

        Assert.Null(trace.Answer.Model);
        Assert.Equal(GenerationReasons.VOCABULARY, trace.Answer.ModelReason);
    }

    [Fact]
    public void A_generic_iterator_with_unobserved_delegate_inputs_gets_no_partial_entry()
    {
        var trace = Trace("""
            public static class Api
            {
                public static IEnumerable<R> Run<T, U, R>(IEnumerable<T> first, IEnumerable<U> second, Func<T, U, R> selector)
                {
                    using var a = first.GetEnumerator();
                    using var b = second.GetEnumerator();
                    while (a.MoveNext() && b.MoveNext()) yield return selector(a.Current, b.Current);
                }
            }
            """, "M:Lib.Api.Run``3(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``1},System.Func{``0,``1,``2})");

        if (trace.Answer.Model is { } model)
        {
            var fate = Assert.Single(model.Fates);
            Assert.NotNull(fate.Inputs);
            Assert.Equal(2, fate.Inputs.Count);
            Assert.Contains(fate.Inputs[0], value => value.Canonical == "elements(arg:first)");
            Assert.Contains(fate.Inputs[1], value => value.Canonical == "elements(arg:second)");
        }
        else
            Assert.Equal(GenerationReasons.VOCABULARY, trace.Answer.ModelReason);
    }

    [Fact]
    public void A_delegate_explicitly_handed_null_keeps_an_entry_with_no_input_objects()
    {
        var trace = Trace("public static class Api { public static void Run(Action<object> action) => action(null); }",
                          "M:Lib.Api.Run(System.Action{System.Object})");

        Assert.NotNull(trace.Answer.Model);
        Assert.Equal(FateClassifier.INVOKE_NOW, FateOf(trace, "action").Fate);
    }

    [Fact]
    public void A_reached_seed_of_a_field_does_not_hide_an_unreached_seed_of_the_same_field_through_another_path()
    {
        var reader = Read("public sealed class Child { public object Value; } public sealed class Parent { public Child Good = new Child(); public Child Bad; } " +
                          "public static class Api { public static object Run(Parent arg) => arg.Bad.Value; }",
                          "M:Lib.Api.Run(Lib.Parent)");

        Assert.Equal(GenerationReasons.INCOMPLETE, reader.Reason);
        var unreached = Assert.Single(reader.UnreachedSeeds);
        Assert.Contains("F:Lib.Parent.Bad", unreached);
        Assert.DoesNotContain(reader.UnreachedSeeds, path => path.Contains("F:Lib.Parent.Good", StringComparison.Ordinal));
    }

    [Fact]
    public void Seeds_reached_on_every_path_leave_no_unreached_seed()
    {
        var reader = Read("public sealed class Child { public object Value; } public sealed class Parent { public Child Good = new Child(); public Child Other = new Child(); } " +
                          "public static class Api { public static object Run(Parent arg) => arg.Other.Value; }",
                          "M:Lib.Api.Run(Lib.Parent)");

        Assert.Empty(reader.UnreachedSeeds);
        Assert.NotEqual(GenerationReasons.INCOMPLETE, reader.Reason);
    }

    [Theory]
    [InlineData("public sealed class Child { public Value Value { get; set; } }")]
    [InlineData("public class ChildBase { public virtual Value Value { get; set; } } public sealed class Child : ChildBase { }")]
    [InlineData("public sealed class Child { public Value Value; }")]
    public void An_unreached_seed_of_a_field_or_auto_property_the_member_later_loads_gives_incomplete(string child)
    {
        var trace = Trace($"public abstract class Value {{ }} {child} public sealed class Options {{ public Child Child; }} " +
                          "public static class Api { public static void Read(Options options, Action done) { options.Child ??= new Child(); _ = options.Child.Value; done(); } }",
                          "M:Lib.Api.Read(Lib.Options,System.Action)");

        Assert.Null(trace.Answer.Model);
        Assert.Equal(GenerationReasons.INCOMPLETE, trace.Answer.ModelReason);
        Assert.Contains(trace.Answer.Generation.Unseeded, path => path.Contains("Lib.Options.Child", StringComparison.Ordinal) &&
                                                                  path.EndsWith(".Value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("public sealed class Child { public Value Value { get; set; } }")]
    [InlineData("public class ChildBase { public virtual Value Value { get; set; } } public sealed class Child : ChildBase { }")]
    [InlineData("public sealed class Child { public Value Value; }")]
    public void A_reached_seed_of_a_field_or_auto_property_the_member_loads_gives_no_incomplete(string child)
    {
        var trace = Trace($"public abstract class Value {{ }} {child} public sealed class Options {{ public Child Child = new Child(); }} " +
                          "public static class Api { public static void Read(Options options, Action done) { options.Child ??= new Child(); _ = options.Child.Value; done(); } }",
                          "M:Lib.Api.Read(Lib.Options,System.Action)");

        Assert.NotEqual(GenerationReasons.INCOMPLETE, trace.Answer.ModelReason);
        Assert.DoesNotContain(trace.Answer.Generation.Unseeded, path => path.EndsWith(".Value", StringComparison.Ordinal));
    }

    [Fact]
    public void A_load_of_an_unseeded_field_in_a_body_an_unknown_execution_reaches_gives_incomplete()
    {
        var reader = Read("public struct State { public object Value; } public static class Runner { public static extern void Run(Action action); } " +
                          "public sealed class Host { private State _state; public void Run(object p) { Runner.Run(() => Consume()); } private void Consume() { _ = _state.Value; } }",
                          "M:Lib.Host.Run(System.Object)");

        Assert.Equal(GenerationReasons.INCOMPLETE, reader.Reason);
    }

    [Fact]
    public void An_unseeded_field_loaded_only_in_setup_gives_no_incomplete()
    {
        var reader = Read("public struct State { public object Value; } " +
                          "public sealed class Host { private State _state; public Host() { _ = _state.Value; } public void Run(object p) { } }",
                          "M:Lib.Host.Run(System.Object)");

        Assert.NotEqual(GenerationReasons.INCOMPLETE, reader.Reason);
    }

    [Fact]
    public void A_store_into_a_pre_existing_object_by_a_body_an_unknown_execution_reaches_is_a_state_store()
    {
        var reader = Read("public static class Runner { public static extern void Run(Action action); } " +
                          "public sealed class Host { private int _count; public void Run(object p) { Runner.Run(() => Change()); } private void Change() { _count++; } }",
                          "M:Lib.Host.Run(System.Object)");

        Assert.NotEmpty(reader.StateStores);
    }

    [Fact]
    public void A_store_by_a_body_an_unknown_execution_only_setup_handed_over_reaches_is_no_state_store()
    {
        var reader = Read("public static class Runner { public static extern void Run(Action action); } " +
                          "public sealed class Host { private int _count; public Host() { Runner.Run(() => Change()); } public void Run(object p) { } private void Change() { _count++; } }",
                          "M:Lib.Host.Run(System.Object)");

        Assert.Empty(reader.StateStores);
    }

    [Theory]
    [InlineData("_cells[0] = new object();")]
    [InlineData("Set(ref _cells[0]);")]
    [InlineData("Set(ref _value);")]
    public void Every_kind_of_store_an_unknown_execution_makes_into_a_pre_existing_object_gives_library_state(string change)
    {
        var source = "public static class Runner { public static extern void Run(Action action); } " +
                     "public sealed class Host { private object[] _cells = new object[1]; private object _value; " +
                     $"public void Run(object p) {{ Runner.Run(() => Change()); }} private void Change() {{ {change} }} " +
                     "private static void Set(ref object cell) { cell = new object(); } }";

        Assert.NotEmpty(Read(source, "M:Lib.Host.Run(System.Object)").StateStores);
        var trace = Trace(source, "M:Lib.Host.Run(System.Object)");
        Assert.Null(trace.Answer.Model);
        Assert.Equal(ModelReasons.LIBRARY_STATE, trace.Answer.ModelReason);
    }

    [Theory]
    [InlineData("_cells[0] = new object();")]
    [InlineData("Set(ref _cells[0]);")]
    [InlineData("Set(ref _value);")]
    public void A_store_of_any_kind_by_an_unknown_execution_only_setup_handed_over_is_no_state_store(string change)
    {
        var reader = Read("public static class Runner { public static extern void Run(Action action); } " +
                          "public sealed class Host { private object[] _cells = new object[1]; private object _value; " +
                          $"public Host() {{ Runner.Run(() => Change()); }} public void Run(object p) {{ }} private void Change() {{ {change} }} " +
                          "private static void Set(ref object cell) { cell = new object(); } }",
                          "M:Lib.Host.Run(System.Object)");

        Assert.Empty(reader.StateStores);
    }

    [Theory]
    [InlineData("List<object>", "_ = user.Make()[0];")]
    [InlineData("List<object>", "user.Make().Add(new object());")]
    [InlineData("List<object>", "Sink.Take(user.Make());")]
    [InlineData("Dictionary<string, object>", "_ = user.Make()[\"a\"];")]
    [InlineData("Dictionary<string, object>", "user.Make()[\"a\"] = new object();")]
    public void Touching_a_collection_a_witness_returned_gives_vocabulary(string type, string body)
    {
        var reader = Read($"public interface IUser {{ {type} Make(); }} public static class Api {{ public static void Run(IUser user) {{ {body} }} }}",
                          "M:Lib.Api.Run(Lib.IUser)");

        // Make is the argument's code, which refuses first; what it returned is a case of its own, still named.
        Assert.Contains(reader.Causes, cause => cause.Reason == GenerationReasons.VOCABULARY);
    }

    [Fact]
    public void An_object_of_a_probe_class_a_witness_returned_handed_to_an_extern_method_gives_vocabulary_not_unknown_touch()
    {
        var reader = Read("public abstract class Node { } public interface IUser<T> { T Made { get; } } " +
                          "public static class Api { public static void Run<T>(IUser<T> user) where T : Node { Sink.Take(user.Made); } }",
                          "M:Lib.Api.Run``1(Lib.IUser{``0})");

        Assert.Equal(GenerationReasons.VOCABULARY, reader.Reason);
    }

    private static EffectReader Read(string source, string member)
    {
        var trace = Trace(source, member);
        Assert.True(trace.Run is { Stopped: false }, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        return new EffectReader(trace.Driver!, trace.Run);
    }

    private static GeneratedEffect Effect(EffectReader reader, string parameter, string kind) =>
        Assert.Single(reader.Effects[parameter], effect => effect.Kind == kind);

    private static (Allocations Allocations, DriverExecutions Executions) AllocationContext(GenerationTrace trace)
    {
        Assert.True(trace.Run is { Stopped: false }, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        var reachability = new HeapReachability(trace.Run.Heap!);
        var result = reachability.StaticField(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.KEEP_TYPE, "R"));
        var executions = new DriverExecutions(trace.Driver!, trace.Run.Executions!, reachability.From(result is null ? [] : reachability.Targets(result)));
        return (new Allocations(trace.Driver!, trace.Run.Heap!, trace.Run.Executions!, executions, reachability), executions);
    }
}
