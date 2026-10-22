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
    [InlineData("cells[0] = Choice.Value ? p : q;", "", "arg:p,arg:q")]
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

    [Theory]
    [InlineData("public static object[] Wrap(Node p) => new object[] { p };", "M:Lib.Api.Wrap(Lib.Node)", "collection(arg:p)")]
    [InlineData("public static HashSet<object> Wrap(Node p, Node q) => new HashSet<object> { p, q };", "M:Lib.Api.Wrap(Lib.Node,Lib.Node)", "collection(arg:p,arg:q)")]
    [InlineData("public static Dictionary<Node, Node> Wrap(Node k, Node v) => new Dictionary<Node, Node> { [k] = v };", "M:Lib.Api.Wrap(Lib.Node,Lib.Node)",
                "dictionary(arg:k,arg:v)")]
    public void A_fresh_collection_of_named_values_names_them_and_keeps_nothing(string member, string id, string expected)
    {
        var answer = Answer($"public abstract class Node {{ }} public static class Api {{ {member} }}", id);

        AssertPassesReader(answer);
        Assert.Equal(expected, answer.Model!.Result?.ToString());
        Assert.Empty(answer.Model.Keeps);
        Assert.Empty(answer.Model.Effects);
    }

    // TD-034b: a collection(…) result is no place that drops the read of an enumerated argument — ToList keeps reads-deep — whether the
    // call returns the collection itself or a task of it.
    [Theory]
    [InlineData("public static List<object> Wrap(IEnumerable<object> source) { var r = new List<object>(); foreach (var x in source) r.Add(x); return r; }",
                "M:Lib.Api.Wrap(System.Collections.Generic.IEnumerable{System.Object})", "collection(elements(arg:source))")]
    [InlineData("public static Task<List<object>> Wrap(IEnumerable<object> source) { var r = new List<object>(); foreach (var x in source) r.Add(x); return Task.FromResult(r); }",
                "M:Lib.Api.Wrap(System.Collections.Generic.IEnumerable{System.Object})", "task(collection(elements(arg:source)))")]
    [InlineData("public static object[] Wrap(IEnumerable<object> source) { var r = new object[1]; foreach (var x in source) r[0] = x; return r; }",
                "M:Lib.Api.Wrap(System.Collections.Generic.IEnumerable{System.Object})", "collection(elements(arg:source))")]
    public void A_fresh_collection_of_an_arguments_elements_keeps_the_read_of_the_argument(string member, string id, string expected)
    {
        var answer = Answer($"public abstract class Node {{ }} public static class Api {{ {member} }}", id);

        AssertPassesReader(answer);
        Assert.Equal(expected, answer.Model!.Result?.ToString());
        Assert.Equal([LibraryEffect.DeepReadOf("source")], answer.Model.Effects);
    }

    [Fact]
    public void A_read_the_call_makes_beside_a_collected_result_is_written_once()
    {
        var answer = Answer("public abstract class Node { public object Value; } public static class Api { public static List<object> Pick(Node[] items) { _ = items[1].Value; return new List<object> { items[0] }; } }",
                            "M:Lib.Api.Pick(Lib.Node[])");

        AssertPassesReader(answer);
        Assert.Equal("collection(elements(arg:items))", answer.Model!.Result?.ToString());
        Assert.Equal([LibraryEffect.DeepReadOf("items")], answer.Model.Effects);
    }

    [Theory]
    [InlineData("public static List<object> Wrap(Node p) => new List<object> { p, new Leaf() };", "M:Lib.Api.Wrap(Lib.Node)")]
    [InlineData("public static Dictionary<Node, Leaf> Wrap(Node k) => new Dictionary<Node, Leaf> { [k] = new Leaf() };", "M:Lib.Api.Wrap(Lib.Node)")]
    public void A_fresh_collection_with_an_unnamed_value_beside_a_named_one_has_vocabulary_reason(string member, string id)
    {
        var answer = Answer($"public abstract class Node {{ }} public sealed class Leaf {{ }} public static class Api {{ {member} }}", id);

        AssertNoModel(answer, ModelReasons.VOCABULARY);
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
        var answer = Answer("public abstract class Node { public object Child; } public sealed class Bag { private Node _value; public void Add(List<Node> nodes) { _value = nodes[0]; _ = nodes[0].Child; } }",
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

    [Fact]
    public void A_probe_passed_through_a_libraries_own_Unsafe_As_is_not_handed_and_keeps_its_name()
    {
        // CoreLib declares Unsafe itself, As a placeholder that only throws; without its body it is the cast every other library sees.
        var answer = TraceOf(PRELUDE + """
                public static class Api { public static void Run(object p, Action<object> take) => take(System.Runtime.CompilerServices.Unsafe.As<object>(p)); }
            }

            namespace System.Runtime.CompilerServices
            {
                public static class Unsafe { public static T As<T>(object o) where T : class { throw new PlatformNotSupportedException(); } }
            }
            """, "M:Lib.Api.Run(System.Object,System.Action{System.Object})").Answer;

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
    public void A_user_object_stored_into_a_struct_field_of_the_receiver_is_kept_by_the_receiver()
    {
        // The struct's storage is part of the receiver, so the receiver keeps what is stored there, as it keeps what is stored into a
        // class it holds (R7).
        var answer = Answer("public struct State { public object Value; } public sealed class Host { private State _current; public void Run(object p) { _current.Value = p; } }",
                            "M:Lib.Host.Run(System.Object)");

        AssertPassesReader(answer);
        Assert.Equal(["arg:p"], Text(answer.Model!.Keeps["this"]));
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
    public void A_receiver_seed_handed_to_an_extern_method_touches_the_receiver()
    {
        // The seed stands for a user object a program stored in the receiver; unseen code may write it as well as read it, as an
        // argument's seed (question 161).
        var answer = Answer("public class Item { public object Value; } " +
                            "public sealed class Host { private Item _item; public void Run(object p) { Sink.Take(_item); } }",
                            "M:Lib.Host.Run(System.Object)");

        AssertNoModel(answer, ModelReasons.UNKNOWN_TOUCH);
    }

    [Fact]
    public void A_receiver_seed_whose_ToString_the_member_calls_is_a_deep_read_of_this()
    {
        var answer = Answer("public class Item { public object Value; } " +
                            "public sealed class Host { private Item _item; public void Run(object p) { _ = _item.ToString(); } }",
                            "M:Lib.Host.Run(System.Object)");

        AssertPassesReader(answer);
        Assert.Contains(LibraryEffect.DeepReadOf("this"), answer.Model!.Effects);
    }

    [Fact]
    public void A_receiver_seed_reached_through_a_field_of_the_receiver_handed_to_an_extern_method_touches_the_receiver()
    {
        var answer = Answer("public class Item { public object Value; } public sealed class Slot { public Item Item; } " +
                            "public sealed class Host { private Slot _slot = new Slot(); public void Run(object p) { Sink.Take(_slot); } }",
                            "M:Lib.Host.Run(System.Object)");

        AssertNoModel(answer, ModelReasons.UNKNOWN_TOUCH);
    }

    [Fact]
    public void A_receiver_seed_read_below_a_field_the_setter_then_stores_its_value_into_is_a_deep_read_of_this()
    {
        // The heap does not follow order: the seed's Child setup stored lands in the kept value too, so an argument reaches it; the
        // read of the previously held object's Child is still a read of a user object the receiver held.
        var answer = Answer("public class Item { public object Child; } " +
                            "public sealed class Host { private Item _item; public Item P { set { _item.Child.ToString(); _item = value; } } }",
                            "M:Lib.Host.set_P(Lib.Item)");

        AssertPassesReader(answer);
        Assert.Contains(LibraryEffect.DeepReadOf("this"), answer.Model!.Effects);
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
    public void A_read_of_the_value_a_setter_stores_is_a_read_of_its_argument()
    {
        // The heap does not follow order, so the seed setup stored in _item and the value the setter keeps there share their Child; the
        // read's own path starts at value, so it reads the argument and no receiver seed (R7).
        var answer = Answer("public class Item { public object Child; } " +
                            "public sealed class Host { private Item _item; public Item P { set { _item = value; value.Child.ToString(); } } }",
                            "M:Lib.Host.set_P(Lib.Item)");

        AssertPassesReader(answer);
        Assert.Contains(LibraryEffect.DeepReadOf("value"), answer.Model!.Effects);
        Assert.DoesNotContain(LibraryEffect.DeepReadOf("this"), answer.Model!.Effects);
    }

    /// <summary>A subscription to a field-like event combines the handler with what the event's storage held and stores the combination
    /// back: it keeps nothing new and writes no library state, however many receivers the driver makes. With enough public members the
    /// heap merges the seed factory's and the accessor's contexts, so the seed setup subscribed reaches the accessor only through what
    /// setup passed it, the read of the storage names every receiver, and the call's store lands on each trigger's receiver too; none of
    /// that is the call's (R8).</summary>
    /// <param name="declaration">The class's modifiers: an unsealed receiver is the driver's subclass, a sealed one the library's type.</param>
    /// <param name="members">How many public members beside <c>Start</c> the class declares, each a trigger with a receiver of its own.</param>
    [Theory]
    [InlineData("public class", 0)]
    [InlineData("public class", 16)]
    [InlineData("public sealed class", 16)]
    public void A_field_like_event_subscription_keeps_nothing_and_writes_no_state(string declaration, int members)
    {
        var answer = Answer($"{declaration} Worker {{ public event Action Work; public void Start() {{ ThreadPool.QueueUserWorkItem(_ => Work?.Invoke()); }} " +
                            $"{Triggers(members)} }}", "M:Lib.Worker.add_Work(System.Action)");

        AssertPassesReader(answer);
        Assert.Equal(FateClassifier.UNKNOWN_EXECUTION, answer.Classified!["value"].Fate);
        Assert.Empty(answer.Model!.Keeps);
        Assert.Empty(answer.Model.Effects);
    }

    /// <summary>An event accessor that also counts writes its own receiver's state, which the receivers the heap merges into the accessor
    /// leave a state store (R8).</summary>
    [Fact]
    public void An_event_accessor_that_also_counts_has_library_state_however_many_receivers_merge()
    {
        var answer = Answer("public class Worker { private Action _work; private int _count; " +
                            "public event Action Work { add { _work += value; _count++; } remove { _work -= value; } } " +
                            $"public void Start() {{ ThreadPool.QueueUserWorkItem(_ => _work?.Invoke()); }} {Triggers(16)} }}",
                            "M:Lib.Worker.add_Work(System.Action)");

        AssertNoModel(answer, ModelReasons.LIBRARY_STATE);
    }

    [Fact]
    public void A_receiver_field_holding_an_object_its_constructor_made_handed_to_an_extern_method_touches_the_receiver()
    {
        // No seed is in it, but the object handed out is the receiver's own state, which the extern may write: a write the analysis
        // sees there is library-state (question 161).
        var answer = Answer("public sealed class Leaf { public int Value; } " +
                            "public sealed class Host { private readonly Leaf _leaf = new Leaf(); public void Run(object p) { Sink.Take(_leaf); } }",
                            "M:Lib.Host.Run(System.Object)");

        AssertNoModel(answer, ModelReasons.UNKNOWN_TOUCH);
    }

    [Theory]
    [InlineData("public static Task<Box> Echo(Task<Box> p) => p;", "M:Lib.Api.Echo(System.Threading.Tasks.Task{Lib.Box})", "[arg:p]")]
    [InlineData("public static async Task<Box> Pass(Task<Box> p) => await p;", "M:Lib.Api.Pass(System.Threading.Tasks.Task{Lib.Box})",
                "task([completion(arg:p)])")]
    [InlineData("public static ValueTask<Box> EchoValue(ValueTask<Box> p) => p;", "M:Lib.Api.EchoValue(System.Threading.Tasks.ValueTask{Lib.Box})",
                "[arg:p]")]
    [InlineData("public static async ValueTask<Box> PassValue(ValueTask<Box> p) => await p;",
                "M:Lib.Api.PassValue(System.Threading.Tasks.ValueTask{Lib.Box})", "task([completion(arg:p)])")]
    [InlineData("public static async Task<Box> PassTwice(Task<Task<Box>> p) => await await p;",
                "M:Lib.Api.PassTwice(System.Threading.Tasks.Task{System.Threading.Tasks.Task{Lib.Box}})", "task([completion(completion(arg:p))])")]
    [InlineData("public static Task<Task<Box>> Make(Box x) => Task.FromResult(Task.FromResult(x));", "M:Lib.Api.Make(Lib.Box)", "task(task([arg:x]))")]
    // A task argument the member returns inside a task of its own is named by itself at that level: its join stays the argument's (R6).
    [InlineData("public static Task<Task<Box>> Wrap(Task<Box> p) => Task.FromResult(p);",
                "M:Lib.Api.Wrap(System.Threading.Tasks.Task{Lib.Box})", "task([arg:p])")]
    [InlineData("public static ValueTask<Task<Box>> WrapValue(Task<Box> p) => new ValueTask<Task<Box>>(p);",
                "M:Lib.Api.WrapValue(System.Threading.Tasks.Task{Lib.Box})", "task([arg:p])")]
    [InlineData("public static Task<Task<Task<Box>>> WrapTwice(Task<Box> p) => Task.FromResult(Task.FromResult(p));",
                "M:Lib.Api.WrapTwice(System.Threading.Tasks.Task{Lib.Box})", "task(task([arg:p]))")]
    [InlineData("public static async Task<Task<Box>> WrapAsync(Task<Box> p) { await Task.Yield(); return p; }",
                "M:Lib.Api.WrapAsync(System.Threading.Tasks.Task{Lib.Box})", "task([arg:p])")]
    [InlineData("public static Task<Task<Box>> Any(Task<Box> p) => Task.WhenAny(new[] { p });",
                "M:Lib.Api.Any(System.Threading.Tasks.Task{Lib.Box})", "task([arg:p])")]
    public void A_task_result_is_named_through_the_task_argument_it_returns_or_completes_with(string member, string id, string expected)
    {
        // The argument's task itself is arg:p; what it completes with, handed to a task of the member's own, is completion(arg:p) (R6).
        var answer = Answer($"public static class Api {{ {member} }}", id);

        AssertPassesReader(answer);
        Assert.Equal(expected, answer.Model!.Result?.ToString());
    }

    private const string CARRIER = "public sealed class Inner { public Box Slot; } " +
                                   "public sealed class Carrier { public Box Slot; public Box[] Items = new Box[1]; public Inner Inner = new Inner(); } ";

    /// <summary>The synchronous twin: a direct store into a carrier argument is a keep of the argument.</summary>
    [Fact]
    public void A_store_into_a_carrier_argument_is_kept_by_the_argument()
    {
        var answer = Answer(CARRIER + "public static class Api { public static void Keep(Carrier p, Box d) { p.Slot = d; } }",
                            "M:Lib.Api.Keep(Lib.Carrier,Lib.Box)");

        AssertPassesReader(answer);
        Assert.Contains("arg:d", Text(answer.Model!.Keeps.GetValueOrDefault("p") ?? []));
    }

    /// <summary>The synchronous twin of a store into an element or a further field of a carrier argument has no name for its keep.</summary>
    /// <param name="store">The store, into <c>p</c>.</param>
    [Theory]
    [InlineData("p.Items[0] = d;")]
    [InlineData("p.Inner.Slot = d;")]
    public void A_store_below_a_carrier_argument_has_vocabulary_reason(string store)
    {
        var answer = Answer(CARRIER + $"public static class Api {{ public static void Keep(Carrier p, Box d) {{ {store} }} }}",
                            "M:Lib.Api.Keep(Lib.Carrier,Lib.Box)");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
    }

    /// <summary>A store into what a task argument completes with is read as the twin's store into the argument; <c>keeps.p</c> would keep
    /// into the task, not into what it completes with, so the vocabulary has no name for it and the member has no model (R6, R7) —
    /// never an entry that leaves the keep out.</summary>
    /// <param name="parameter">The task parameter's type.</param>
    /// <param name="id">The type's documentation id.</param>
    /// <param name="carrier">The expression giving the carrier from <c>p</c>.</param>
    /// <param name="store">The store, into <c>c</c>.</param>
    [Theory]
    [InlineData("Task<Carrier>", "System.Threading.Tasks.Task{Lib.Carrier}", "await p", "c.Slot = d;")]
    [InlineData("Task<Carrier>", "System.Threading.Tasks.Task{Lib.Carrier}", "await p", "c.Items[0] = d;")]
    [InlineData("Task<Carrier>", "System.Threading.Tasks.Task{Lib.Carrier}", "await p", "c.Inner.Slot = d;")]
    [InlineData("ValueTask<Carrier>", "System.Threading.Tasks.ValueTask{Lib.Carrier}", "await p", "c.Slot = d;")]
    [InlineData("Task<Task<Carrier>>", "System.Threading.Tasks.Task{System.Threading.Tasks.Task{Lib.Carrier}}", "await await p", "c.Slot = d;")]
    public void A_store_into_what_a_task_argument_completes_with_has_vocabulary_reason(string parameter, string id, string carrier, string store)
    {
        var answer = Answer(CARRIER + $"public static class Api {{ public static async Task Keep({parameter} p, Box d) {{ var c = {carrier}; {store} }} }}",
                            $"M:Lib.Api.Keep({id},Lib.Box)");

        AssertNoModel(answer, ModelReasons.VOCABULARY);
    }

    [Fact]
    public void A_task_argument_awaited_without_a_store_into_what_it_completes_with_keeps_its_entry()
    {
        var answer = Answer(CARRIER + "public static class Api { public static async Task<Box> Pass(Task<Carrier> p, Box d) { await p; return d; } }",
                            "M:Lib.Api.Pass(System.Threading.Tasks.Task{Lib.Carrier},Lib.Box)");

        AssertPassesReader(answer);
        Assert.Empty(answer.Model!.Keeps);
    }

    /// <summary>A struct constructor's stores into its own fields reach the storage the caller constructs into, the struct receiver's own,
    /// whatever route reaches them — directly, below a struct field, after <c>this = default</c>, through a chained constructor — so the
    /// answer is <c>vocabulary</c>, never a model that leaves the store out or keeps the argument in itself (R7).</summary>
    /// <param name="source">The library source declaring the struct.</param>
    /// <param name="member">The documentation id of the constructor asked for.</param>
    [Theory]
    [InlineData("public struct Pair { private object _value; public Pair(object value) { _value = value; } }", "M:Lib.Pair.#ctor(System.Object)")]
    [InlineData("public struct One<T> { public T Item1; public One(T item1) { Item1 = item1; } }", "M:Lib.One`1.#ctor(`0)")]
    [InlineData("public sealed class Sb { } public struct Handler { private readonly Sb _sb; private int _length; " +
                "public Handler(int length, Sb sb) { _sb = sb; _length = length; } }", "M:Lib.Handler.#ctor(System.Int32,Lib.Sb)")]
    [InlineData("public struct Inner { public object Ref; } public struct Outer { private Inner _inner; public Outer(object value) { _inner.Ref = value; } }",
                "M:Lib.Outer.#ctor(System.Object)")]
    [InlineData("public struct Reset { private object _value; public Reset(object value) { this = default; _value = value; } }", "M:Lib.Reset.#ctor(System.Object)")]
    [InlineData("public struct Chained { private object _value; private int _n; public Chained(object value) : this(value, 0) { } " +
                "public Chained(object value, int n) { _value = value; _n = n; } }", "M:Lib.Chained.#ctor(System.Object)")]
    public void A_struct_constructor_storing_into_its_own_fields_is_vocabulary(string source, string member)
    {
        var answer = Answer(source, member);

        AssertNoModel(answer, ModelReasons.VOCABULARY);
        Assert.Contains(answer.Causes, cause => cause.Code == ModelCauses.STRUCT_STORAGE_WRITE);
    }

    [Fact]
    public void A_class_constructor_storing_its_argument_keeps_it_in_this()
    {
        var answer = Answer("public sealed class Kept { private object _value; public Kept(object value) { _value = value; } }", "M:Lib.Kept.#ctor(System.Object)");

        AssertPassesReader(answer);
        Assert.Equal(["arg:value"], Text(answer.Model!.Keeps["this"]));
        Assert.DoesNotContain(answer.Causes, cause => cause.Code == ModelCauses.STRUCT_STORAGE_WRITE);
    }

    /// <summary>A pattern that matches an object argument as a struct and names it unboxes it: the copy it takes reads the box's whole
    /// storage, a read of the user's object, as the cast of a class twin reads it (R2).</summary>
    /// <param name="source">The library source declaring the member.</param>
    /// <param name="member">The documentation id of the member asked for.</param>
    /// <param name="parameter">The name of the object parameter the pattern unboxes.</param>
    [Theory]
    [InlineData("public struct D : IComparable { private int _n; public int CompareTo(object value) { if (value == null) return 1; " +
                "if (value is not D d) throw new ArgumentException(); return _n.CompareTo(d._n); } }", "M:Lib.D.CompareTo(System.Object)", "value")]
    [InlineData("public struct D : IComparable { private int _n; public int CompareTo(object obj) { if (obj is D other) return _n.CompareTo(other._n); " +
                "if (obj == null) return 1; throw new ArgumentException(); } }", "M:Lib.D.CompareTo(System.Object)", "obj")]
    [InlineData("public struct D { private int _n; public int Read(object value) { switch (value) { case D d: return d._n; default: return 0; } } }",
                "M:Lib.D.Read(System.Object)", "value")]
    [InlineData("public struct D { public int N; } public static class Api { public static int Read(object value) => value is D { N: > 0 } d ? d.N : 0; }",
                "M:Lib.Api.Read(System.Object)", "value")]
    [InlineData("public sealed class C : IComparable { private int _n; public int CompareTo(object value) { if (value == null) return 1; " +
                "if (value is not C c) throw new ArgumentException(); return _n.CompareTo(c._n); } }", "M:Lib.C.CompareTo(System.Object)", "value")]
    public void A_pattern_unboxing_an_object_argument_reads_it(string source, string member, string parameter)
    {
        var answer = Answer(source, member);

        AssertPassesReader(answer);
        Assert.Equal([LibraryEffect.DeepReadOf(parameter)], answer.Model!.Effects);
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

    private static string Triggers(int count) => string.Concat(Enumerable.Range(0, count).Select(index => $"public void M{index}() {{ }} "));

    private static string[] Text(IEnumerable<LibraryValue> values) =>
        values.Select(value => value.ToString()).Order(StringComparer.Ordinal).ToArray();
}
