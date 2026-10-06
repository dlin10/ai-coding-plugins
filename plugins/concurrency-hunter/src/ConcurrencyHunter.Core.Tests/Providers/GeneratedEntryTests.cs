using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>Whole generated entries and their closed model reasons (SPEC TD-034b).</summary>
public sealed class GeneratedEntryTests
{
    [Theory]
    [InlineData("Set(ref cells[0], p);", "private static void Set(ref object cell, object value) => cell = value;", "arg:p")]
    [InlineData("Forward(ref cells[0], p);", "private static void Forward(ref object cell, object value) => Set(ref cell, value); private static void Set(ref object cell, object value) => cell = value;", "arg:p")]
    [InlineData("cells[0] = Choice.Value ? p : q;", "", "vocabulary")]
    [InlineData("Set(ref cells[0], Choice.Value ? p : q);", "private static void Set(ref object cell, object value) => cell = value;", "arg:p,arg:q")]
    [InlineData("cells[0] = p;", "", "arg:p")]
    [InlineData("Alias(ref cells[0]) = p;", "private static ref object Alias(ref object cell) => ref cell;", "arg:p")]
    [InlineData("Set(p, ref cells[0]);", "private static void Set(object value, ref object cell) => cell = value;", "arg:p")]
    [InlineData("if (Choice.Value) cells[0] = p; else cells[0] = q;", "", "arg:p,arg:q")]
    [InlineData("Set(ref Identity(cells)[0], p);", "private static object[] Identity(object[] cells) => cells; private static void Set(ref object cell, object value) => cell = value;", "arg:p")]
    public void Array_cell_stores_are_complete_or_refused_when_the_operation_is_unresolved(string body, string helpers, string expected)
    {
        var answer = Answer("public static class Choice { public static bool Value; } public static class Api { " +
                            $"public static void Run(object p, object q, object[] cells) {{ {body} }} {helpers} }}",
                            "M:Lib.Api.Run(System.Object,System.Object,System.Object[])");

        if (expected == ModelReasons.VOCABULARY)
        {
            AssertNoModel(answer, ModelReasons.VOCABULARY);
            return;
        }
        AssertPassesReader(answer);
        Assert.Contains(answer.Model!.Effects, effect => effect.Kind == LibraryEffectKind.WriteCells && effect.Parameter == "cells");
        Assert.Equal(expected.Split(','), Text(answer.Model.Stores["cells"]));
    }

    [Theory]
    [InlineData("cells[0] = Unknown.Make();")]
    [InlineData("if (Choice.Value) cells[0] = p; else cells[0] = Unknown.Make();")]
    [InlineData("Set(ref cells[0], Unknown.Make());")]
    [InlineData("Set(ref cells[0], Choice.Value ? p : Unknown.Make());")]
    public void An_unknown_array_store_alternative_refuses_the_whole_entry(string body)
    {
        var answer = Answer("public static class Choice { public static bool Value; } public static class Unknown { public static extern object Make(); } " +
                            $"public static class Api {{ public static void Run(object p, object[] cells) {{ {body} }} private static void Set(ref object cell, object value) => cell = value; }}",
                            "M:Lib.Api.Run(System.Object,System.Object[])");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
    }

    [Theory]
    [InlineData("return Choice.Value ? p : Unknown.Make();")]
    [InlineData("if (Choice.Value) return p; return Unknown.Make();")]
    public void An_unknown_return_alternative_refuses_the_whole_entry(string body)
    {
        var answer = Answer("public static class Choice { public static bool Value; } public static class Unknown { public static extern object Make(); } " +
                            $"public static class Api {{ public static object Run(object p) {{ {body} }} }}",
                            "M:Lib.Api.Run(System.Object)");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
    }

    [Theory]
    [InlineData("out", "Unknown.Make()")]
    [InlineData("out", "Choice.Value ? p : Unknown.Make()")]
    [InlineData("ref", "Choice.Value ? p : Unknown.Make()")]
    public void An_unknown_output_alternative_refuses_the_whole_entry(string modifier, string value)
    {
        var answer = Answer("public static class Choice { public static bool Value; } public static class Unknown { public static extern object Make(); } " +
                            $"public static class Api {{ public static void Run(object p, {modifier} object result) {{ result = {value}; }} }}",
                            "M:Lib.Api.Run(System.Object,System.Object@)");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
    }

    [Theory]
    [InlineData("current = Choice.Value ? p : Unknown.Make();")]
    [InlineData("if (Choice.Value) current = p; else current = Unknown.Make();")]
    public void An_unknown_kept_alternative_refuses_the_whole_entry(string body)
    {
        var answer = Answer("public static class Choice { public static bool Value; } public static class Unknown { public static extern object Make(); } " +
                            $"public sealed class Host {{ private object current; public void Run(object p) {{ {body} }} }}",
                            "M:Lib.Host.Run(System.Object)");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
    }

    [Fact]
    public void A_confirmed_result_holder_keeps_values_without_an_explicit_result()
    {
        var answer = Answer("public sealed class Result { private object value; private Action done; public Result(object value, Action done) { this.value = value; this.done = done; } public void Fire() => done(); } " +
                            "public static class Api { public static Result Make(object value, Action done) => new Result(value, done); }",
                            "M:Lib.Api.Make(System.Object,System.Action)");

        Assert.Equal(FateClassifier.HOLDER, answer.Classified!["done"].Fate);
        Assert.Equal(FateClassifier.RESULT, answer.Classified["done"].Holder);
        AssertPassesReader(answer);
        Assert.Null(answer.Model!.Result);
        Assert.Equal(["arg:value"], Text(answer.Model.Keeps["result"]));
    }

    [Fact]
    public void An_enumeration_only_effect_beside_an_argument_result_has_vocabulary_reason()
    {
        var trace = Trace("public abstract class Node { public object Value; } public sealed class Source : IEnumerable<int> { public Node Node; public IEnumerator<int> GetEnumerator() { _ = Node.Value; yield return 1; } System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); } " +
                          "public static class Api { public static IEnumerable<int> Echo(Source source) => source; }",
                          "M:Lib.Api.Echo(Lib.Source)");

        AssertNoModel(trace.Answer, ModelReasons.VOCABULARY);
        var effects = new EffectReader(trace.Driver!, trace.Run!);
        var values = new ValueProvenance(trace.Driver!, trace.Run!, trace.Answer.Classified!, trace.Confirmation);
        Assert.Null(effects.Reason);
        Assert.Null(values.Reason);
        Assert.NotEqual(LibraryResultKind.Sequence, values.Result!.Kind);
        Assert.Contains(effects.Effects["source"], effect => effect.Roots.Contains(DriverSynthesizer.ENUMERATE));
    }

    [Fact]
    public void An_enumeration_only_effect_beside_a_sequence_result_is_written()
    {
        var answer = Answer("public abstract class Node { public object Value; } public static class Api { public static IEnumerable<Node> Read(Node node) { _ = node.Value; yield return node; } }",
                            "M:Lib.Api.Read(Lib.Node)");

        AssertPassesReader(answer);
        Assert.Equal(LibraryResultKind.Sequence, answer.Model!.Result!.Kind);
        Assert.Contains(answer.Model.Effects, effect => effect == LibraryEffect.DeepReadOf("node"));
    }

    [Fact]
    public void A_member_gets_an_entry_that_passes_the_project_reader()
    {
        var answer = Answer("public static class Api { public static object Echo(object value) => value; }",
                            "M:Lib.Api.Echo(System.Object)");

        AssertPassesReader(answer);
        Assert.Equal("[arg:value]", answer.Model!.Result?.ToString());
    }

    [Fact]
    public void A_probe_handed_to_an_unknown_call_has_unknown_touch_model_reason()
    {
        var answer = Answer("public abstract class Node { } public static class Api { public static void Run(Node node, Action done) { Sink.Take(node); done(); } }",
                            "M:Lib.Api.Run(Lib.Node,System.Action)");

        AssertNoModel(answer, ModelReasons.UNKNOWN_TOUCH);
    }

    [Fact]
    public void A_loaded_unreached_seed_has_incomplete_model_reason()
    {
        var answer = Answer("public abstract class Value { } public sealed class Child { public Value Value; } public sealed class Options { public Child Child; } " +
                            "public static class Api { public static void Read(Options options, Action done) { options.Child ??= new Child(); _ = options.Child.Value; done(); } }",
                            "M:Lib.Api.Read(Lib.Options,System.Action)");

        AssertNoModel(answer, ModelReasons.INCOMPLETE);
    }

    [Fact]
    public void A_write_to_unkept_preexisting_state_has_library_state_model_reason()
    {
        var answer = Answer("public sealed class State { public int Value; } public static class Shared { public static readonly State Value = new(); } " +
                            "public static class Api { public static void Write(Action done) { Shared.Value.Value++; done(); } }",
                            "M:Lib.Api.Write(System.Action)");

        AssertNoModel(answer, ModelReasons.LIBRARY_STATE);
    }

    [Fact]
    public void A_read_from_a_delegate_return_has_vocabulary_model_reason()
    {
        var answer = Answer("public abstract class Node { public object Value; } public static class Api { public static void Read(Func<Node> make) { _ = make().Value; } }",
                            "M:Lib.Api.Read(System.Func{Lib.Node})");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
    }

    [Fact]
    public void An_effects_only_member_has_empty_classified_and_an_entry()
    {
        var answer = Answer("public abstract class Node { public object Value; } public static class Api { public static void Read(Node node) { _ = node.Value; } }",
                            "M:Lib.Api.Read(Lib.Node)");

        Assert.Empty(answer.Classified!);
        Assert.Contains(answer.Model!.Effects, effect => effect == LibraryEffect.DeepReadOf("node"));
    }

    [Fact]
    public void A_confirmed_result_holder_without_observed_delegate_inputs_has_an_entry()
    {
        var answer = Answer("public sealed class Quiet { private Action _done; public Quiet(Action done) { _done = done; } public void Fire() => _done(); } " +
                            "public static class Api { public static Quiet Keep(Action done) => new Quiet(done); }",
                            "M:Lib.Api.Keep(System.Action)");

        AssertPassesReader(answer);
        Assert.Null(answer.Model!.Result);
        Assert.Equal(FateClassifier.HOLDER, answer.Classified!["done"].Fate);
    }

    [Fact]
    public void Enumeration_named_by_an_invoke_now_input_drops_reads_deep()
    {
        var answer = Answer("public abstract class Node { } public static class Api { public static bool All(IEnumerable<Node> source, Func<Node, bool> predicate) { foreach (var item in source) if (!predicate(item)) return false; return true; } }",
                            "M:Lib.Api.All(System.Collections.Generic.IEnumerable{Lib.Node},System.Func{Lib.Node,System.Boolean})");

        AssertPassesReader(answer);
        Assert.DoesNotContain(answer.Model!.Effects, effect => effect.Parameter == "source" && effect.Kind == LibraryEffectKind.DeepRead);
    }

    [Fact]
    public void Enumeration_named_by_an_invoke_now_input_does_not_hide_another_deep_read()
    {
        var answer = Answer("public abstract class Node { public object Value; } public static class Api { public static bool All(IEnumerable<Node> source, Func<Node, bool> predicate) { foreach (var item in source) { _ = item.Value; if (!predicate(item)) return false; } return true; } }",
                            "M:Lib.Api.All(System.Collections.Generic.IEnumerable{Lib.Node},System.Func{Lib.Node,System.Boolean})");

        AssertPassesReader(answer);
        Assert.Contains(answer.Model!.Effects, effect => effect == LibraryEffect.DeepReadOf("source"));
    }

    [Fact]
    public void A_carried_probe_gets_an_entry_without_a_fate_for_its_carrier()
    {
        var answer = Answer("public sealed class Parser { private readonly Action _parse; public Parser(Action parse) { _parse = parse; } public void Parse() => _parse(); } " +
                            "public static class Api { public static Parser Echo(Parser parser) => parser; }",
                            "M:Lib.Api.Echo(Lib.Parser)");

        AssertPassesReader(answer);
        Assert.Empty(answer.Model!.Fates);
        Assert.Contains("parser", answer.Classified!.Keys);
    }

    [Fact]
    public void Keeping_into_the_receivers_array_and_bumping_count_has_no_library_state()
    {
        var answer = Answer("public sealed class Bag { private readonly object[] _items = new object[2]; private int _count; public void Add(object value) { _items[_count++] = value; } }",
                            "M:Lib.Bag.Add(System.Object)");

        Assert.Equal(["arg:value"], Text(answer.Model!.Keeps["this"]));
        Assert.Null(answer.ModelReason);
    }

    [Fact]
    public void Keeping_into_the_receiver_and_writing_other_state_has_library_state()
    {
        var answer = Answer("public sealed class State { public int Count; } public sealed class Bag { private object _value; private readonly State _other = new(); public void Add(object value) { _value = value; _other.Count++; } }",
                            "M:Lib.Bag.Add(System.Object)");

        AssertNoModel(answer, ModelReasons.LIBRARY_STATE);
    }

    [Fact]
    public void Keeping_into_one_receiver_array_and_writing_a_second_has_library_state()
    {
        var answer = Answer("public sealed class Bag { private readonly object[] _items = new object[1]; private readonly object[] _other = new object[1]; public void Add(object value) { _items[0] = value; _other[0] = new object(); } }",
                            "M:Lib.Bag.Add(System.Object)");

        AssertNoModel(answer, ModelReasons.LIBRARY_STATE);
    }

    [Fact]
    public void Keeping_what_a_delegate_returned_and_bumping_a_counter_is_keeps_this()
    {
        var answer = Answer("public sealed class Bag { private object _value; private int _count; public void Add(Func<object> make) { _value = make(); _count++; } }",
                            "M:Lib.Bag.Add(System.Func{System.Object})");

        Assert.Equal(["returns:make"], Text(answer.Model!.Keeps["this"]));
        Assert.Null(answer.ModelReason);
    }

    [Fact]
    public void Keeping_a_seed_and_bumping_a_counter_is_vocabulary_not_library_state()
    {
        var answer = Answer("public abstract class Hook { } public sealed class Bag { private Hook _hook; private object _kept; private int _count; public void Keep(Action done) { _kept = _hook; _count++; done(); } }",
                            "M:Lib.Bag.Keep(System.Action)");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
    }

    [Fact]
    public void A_kept_value_two_preexisting_objects_deep_has_no_library_state()
    {
        var answer = Answer("public sealed class Link { public Link Next; public object Value; public Link(int depth) { if (depth > 0) Next = new Link(depth - 1); } } " +
                            "public sealed class Bag { private readonly Link _root = new Link(2); public void Add(object value) { _root.Next.Value = value; } }",
                            "M:Lib.Bag.Add(System.Object)");

        Assert.Equal(["arg:value"], Text(answer.Model!.Keeps["this"]));
        Assert.Null(answer.ModelReason);
    }

    [Fact]
    public void Writing_a_library_base_field_of_a_sub_receiver_without_keeping_has_library_state()
    {
        var answer = Answer("public abstract class Base { protected object Value; public void Write(Action done) { Value = new object(); done(); } }",
                            "M:Lib.Base.Write(System.Action)");

        AssertNoModel(answer, ModelReasons.LIBRARY_STATE);
    }

    [Fact]
    public void A_sub_receiver_that_keeps_an_item_and_bumps_version_has_keeps_this()
    {
        var answer = Answer("public abstract class Base { protected object Value; protected int Version; public void Add(object value) { Value = value; Version++; } }",
                            "M:Lib.Base.Add(System.Object)");

        Assert.Equal(["arg:value"], Text(answer.Model!.Keeps["this"]));
        Assert.Null(answer.ModelReason);
    }

    [Fact]
    public void An_unknown_value_linked_into_a_written_receiver_has_library_state()
    {
        var answer = Answer("public static class External { public static extern object Make(); } public sealed class Bag { private object _value; private int _version; public void Change(Action done) { _value = External.Make(); _version++; done(); } }",
                            "M:Lib.Bag.Change(System.Action)");

        AssertNoModel(answer, ModelReasons.LIBRARY_STATE);
    }

    [Fact]
    public void A_list_indexer_read_whose_element_is_kept_keeps_reads_deep()
    {
        // Node.Value is no seed field: the driver's list argument is empty, so a seed into its elements would store into no object
        // and make the read incomplete (task 6), which is not what this case is about.
        var answer = Answer("public abstract class Node { public int Value; } public sealed class Bag { private Node _value; public void Add(List<Node> nodes) { _value = nodes[0]; _ = nodes[0].Value; } }",
                            "M:Lib.Bag.Add(System.Collections.Generic.List{Lib.Node})");

        Assert.Contains(answer.Model!.Effects, effect => effect == LibraryEffect.DeepReadOf("nodes"));
        Assert.Equal(["elements(arg:nodes)"], Text(answer.Model.Keeps["this"]));
    }

    [Fact]
    public void A_lazy_iterator_that_writes_its_argument_and_yields_integers_is_vocabulary()
    {
        var answer = Answer("public abstract class Node { public object Value; } public static class Api { public static IEnumerable<int> Lazy(Node node, Action done) { node.Value = new object(); done(); yield return 1; } }",
                            "M:Lib.Api.Lazy(Lib.Node,System.Action)");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
    }

    [Fact]
    public void A_lazy_iterator_write_with_sequence_arg_result_is_writes_arg()
    {
        var answer = Answer("public abstract class Node { public object Value; } public static class Api { public static IEnumerable<Node> Lazy(Node node, Action done) { node.Value = new object(); done(); yield return node; } }",
                            "M:Lib.Api.Lazy(Lib.Node,System.Action)");

        Assert.Equal("sequence(arg:node)", answer.Model!.Result?.ToString());
        Assert.Contains(answer.Model.Effects, effect => effect == LibraryEffect.WriteOf("node"));
    }

    [Fact]
    public void A_seed_statement_that_reaches_no_object_is_listed_as_unseeded()
    {
        var answer = Answer("public abstract class Value { } public sealed class Child { public Value Value; } public sealed class Options { public Child Child; } " +
                            "public static class Api { public static void Read(Options options, Action done) { options.Child ??= new Child(); _ = options.Child.Value; done(); } }",
                            "M:Lib.Api.Read(Lib.Options,System.Action)");

        Assert.NotEmpty(answer.Generation.Unseeded);
        Assert.Equal(ModelReasons.INCOMPLETE, answer.ModelReason);
    }

    [Fact]
    public void A_probe_passed_through_Unsafe_As_is_not_handed_and_keeps_its_name()
    {
        // Unsafe.As<T>(object) hands back the object it is given: no hand-off (user decision in the B2 code review).
        var answer = Answer("public static class Api { public static void Run(object p, Action<object> take) => take(System.Runtime.CompilerServices.Unsafe.As<object>(p)); }",
                            "M:Lib.Api.Run(System.Object,System.Action{System.Object})");

        AssertPassesReader(answer);
        Assert.Equal("arg:p", Assert.Single(Assert.Single(Assert.Single(answer.Model!.Fates).Inputs!)).Canonical);
    }

    [Theory]
    [InlineData("Helper(Extern.Make(), p);")]
    [InlineData("Helper(DateTime.Now.Ticks == 0 ? Extern.Make() : new LibSink(), p);")]
    public void A_probe_handed_on_a_parameter_bound_to_an_unknown_calls_result_gives_unknown_touch(string body)
    {
        var answer = Answer("public interface ISink { void Accept(object p); } public sealed class LibSink : ISink { public object Last; public void Accept(object p) { Last = p; } } " +
                            "public static class Extern { public static extern ISink Make(); } " +
                            $"public static class Api {{ public static void Run(object p) {{ {body} }} static void Helper(ISink sink, object p) => sink.Accept(p); }}",
                            "M:Lib.Api.Run(System.Object)");

        AssertNoModel(answer, ModelReasons.UNKNOWN_TOUCH);
    }

    [Fact]
    public void The_same_probe_handed_to_an_extern_method_gives_unknown_touch()
    {
        var answer = Answer("public static class Api { public static void Run(object p, Action<object> take) { Sink.Take(p); take(p); } }",
                            "M:Lib.Api.Run(System.Object,System.Action{System.Object})");

        AssertNoModel(answer, ModelReasons.UNKNOWN_TOUCH);
    }

    [Fact]
    public void An_entry_the_final_reader_check_rejects_gives_vocabulary_with_the_rejection_in_the_detail()
    {
        var answer = Answer("public static class Api { public static void Keep(Action[] actions) { } }", "M:Lib.Api.Keep(System.Action[])");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
        Assert.Contains("array of delegates", answer.Detail);
    }

    [Fact]
    public void A_user_object_stored_into_a_struct_field_of_the_receiver_has_vocabulary_reason()
    {
        // The heap carries no region for a struct inside an object, so what the receiver keeps there has no name (R5).
        var answer = Answer("public struct State { public object Value; } public sealed class Host { private State _current; public void Run(object p) { _current.Value = p; } }",
                            "M:Lib.Host.Run(System.Object)");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
    }

    [Fact]
    public void A_counter_in_a_struct_field_of_the_receiver_is_library_state()
    {
        var answer = Answer("public struct State { public int Count; } " +
                            "public sealed class Host { private State _current; public void Run(object p) { _current.Count++; } }",
                            "M:Lib.Host.Run(System.Object)");

        AssertNoModel(answer, ModelReasons.LIBRARY_STATE);
    }

    [Fact]
    public void A_counter_in_a_struct_field_of_an_object_the_call_made_is_no_state()
    {
        var answer = Answer("public struct State { public int Count; } public sealed class Fresh { public State Current; } " +
                            "public sealed class Host { public void Run(object p) { var fresh = new Fresh(); fresh.Current.Count++; } }",
                            "M:Lib.Host.Run(System.Object)");

        AssertPassesReader(answer);
    }

    [Fact]
    public void A_receiver_seed_handed_to_an_extern_method_has_vocabulary_reason()
    {
        // The seed stands for a user object a program stored in the receiver; unseen code touching it is a read `this` cannot carry.
        var answer = Answer("public class Item { public object Value; } " +
                            "public sealed class Host { private Item _item; public void Run(object p) { Sink.Take(_item); } }",
                            "M:Lib.Host.Run(System.Object)");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
    }

    [Fact]
    public void A_receiver_seed_whose_ToString_the_member_calls_has_vocabulary_reason()
    {
        var answer = Answer("public class Item { public object Value; } " +
                            "public sealed class Host { private Item _item; public void Run(object p) { _ = _item.ToString(); } }",
                            "M:Lib.Host.Run(System.Object)");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
    }

    [Fact]
    public void A_receiver_seed_reached_through_a_field_of_the_receiver_handed_to_an_extern_method_has_vocabulary_reason()
    {
        var answer = Answer("public class Item { public object Value; } public sealed class Slot { public Item Item; } " +
                            "public sealed class Host { private Slot _slot = new Slot(); public void Run(object p) { Sink.Take(_slot); } }",
                            "M:Lib.Host.Run(System.Object)");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
    }

    [Fact]
    public void A_receiver_seed_read_below_a_field_the_setter_then_stores_its_value_into_has_vocabulary_reason()
    {
        // The heap does not follow order: the seed's Child setup stored lands in the kept value too, so an argument reaches it; the
        // read of the previously held object's Child is still a read of a user object `this` cannot carry.
        var answer = Answer("public class Item { public object Child; } " +
                            "public sealed class Host { private Item _item; public Item P { set { _item.Child.ToString(); _item = value; } } }",
                            "M:Lib.Host.set_P(Lib.Item)");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
    }

    [Fact]
    public void A_receiver_seed_left_alone_by_a_setter_that_reads_its_value_keeps_its_entry()
    {
        // Where the rule stops: the setter reads only what it was handed and keeps it nowhere, so no receiver seed is read. (Keeping
        // it in _item would merge the seed's Child into the value's, and the read would be a seed read: vocabulary.)
        var answer = Answer("public class Item { public object Child; } " +
                            "public sealed class Host { private Item _item; public Item P { set { value.Child.ToString(); } } public Item Q => _item; }",
                            "M:Lib.Host.set_P(Lib.Item)");

        AssertPassesReader(answer);
    }

    [Fact]
    public void A_receiver_field_holding_an_object_its_constructor_made_handed_to_an_extern_method_keeps_its_entry()
    {
        // No seed is read: the object handed out is the library's own, so the receiver read stays the library's own state.
        var answer = Answer("public sealed class Leaf { public int Value; } " +
                            "public sealed class Host { private readonly Leaf _leaf = new Leaf(); public void Run(object p) { Sink.Take(_leaf); } }",
                            "M:Lib.Host.Run(System.Object)");

        AssertPassesReader(answer);
    }

    private static GeneratedAnswer Answer(string source, string member)
    {
        var answer = Trace(source, member).Answer;
        Assert.NotNull(answer.Classified);
        Assert.Null(answer.Reason);
        return answer;
    }

    private static void AssertNoModel(GeneratedAnswer answer, string reason)
    {
        Assert.Null(answer.Model);
        Assert.Equal(reason, answer.ModelReason);
    }

    private static void AssertPassesReader(GeneratedAnswer answer)
    {
        Assert.True(answer.Model is not null, $"{answer.Reason} / {answer.ModelReason}: {answer.Detail}");
        var model = Assert.IsType<LibraryModel>(answer.Model);
        var files = ProjectModelFiles.Read("generated.json", ModelEntryWriter.File(model));
        Assert.Empty(files.Rejections);
        Assert.Single(files.Entries);
    }

    private static string[] Text(IEnumerable<LibraryValue> values) =>
        values.Select(value => value.ToString()).Order(StringComparer.Ordinal).ToArray();
}
