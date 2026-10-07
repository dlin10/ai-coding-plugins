using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The names of every value a generated entry observes, and the entry facts derived from those names.</summary>
public sealed class ValueProvenanceTests
{

    [Fact]
    public void A_fresh_graph_with_an_unknown_scalar_field_can_be_new()
    {
        var trace = Trace("public static class Unknown { public static extern int Number(); } public sealed class Made { public int Number; public Made() { Number = Unknown.Number(); } } public static class Api { public static Made Run(object p) => new Made(); }",
                          "M:Lib.Api.Run(System.Object)");

        Assert.Null(Read(trace).Reason);
        Assert.Equal("new", trace.Answer.Model!.Result?.ToString());
    }

    [Fact]
    public void An_unknown_yield_alternative_refuses_the_whole_sequence()
    {
        var trace = Trace("public static class Unknown { public static extern object Make(); } public static class Api { public static IEnumerable<object> Run(object p) { yield return p; yield return Unknown.Make(); } }",
                          "M:Lib.Api.Run(System.Object)");

        Assert.Equal(GenerationReasons.VOCABULARY, Read(trace).Reason);
        Assert.Null(trace.Answer.Model);
    }

    [Theory]
    [InlineData("out", "result = new Wrap();", "System.Object,Lib.Wrap@")]
    [InlineData("input", "take(new Wrap());", "System.Object,System.Action{Lib.Wrap}")]
    [InlineData("result", "return new Wrap();", "System.Object")]
    public void An_unknown_value_in_a_fresh_observed_graph_refuses_the_entry(string place, string body, string id)
    {
        var parameters = place == "out" ? "object p, out Wrap result" : place == "input" ? "object p, Action<Wrap> take" : "object p";
        var returned = place == "result" ? "Wrap" : "void";
        var trace = Trace("public static class Unknown { public static extern object Make(); } public sealed class Wrap { public object Value; public Wrap() { Value = Unknown.Make(); } } " +
                          $"public static class Api {{ public static {returned} Run({parameters}) {{ {body} }} }}", $"M:Lib.Api.Run({id})");

        Assert.Equal(GenerationReasons.VOCABULARY, Read(trace).Reason);
        Assert.Null(trace.Answer.Model);
    }

    [Theory]
    [InlineData("take(Choice.Value ? p : Unknown.Make());")]
    [InlineData("var value = Choice.Value ? p : Unknown.Make(); Forward(value, take);")]
    public void An_unknown_delegate_input_alternative_has_vocabulary_reason(string body)
    {
        var trace = Trace("public static class Choice { public static bool Value; } public static class Unknown { public static extern object Make(); } " +
                          $"public static class Api {{ public static void Run(object p, Action<object> take) {{ {body} }} private static void Forward(object value, Action<object> take) => take(value); }}",
                          "M:Lib.Api.Run(System.Object,System.Action{System.Object})");

        Assert.Equal(GenerationReasons.VOCABULARY, Read(trace).Reason);
        Assert.Null(trace.Answer.Model);
    }

    [Theory]
    [InlineData("Action<Wrap> take", "take(new Wrap(this));", "System.Action{Lib.Wrap}")]
    [InlineData("object p, out Wrap result", "result = new Wrap(this);", "System.Object,Lib.Wrap@")]
    public void A_fresh_wrapper_reaching_the_constructed_receiver_is_not_new(string parameters, string body, string id)
    {
        var trace = Trace($"public sealed class Wrap {{ private Host host; public Wrap(Host host) {{ this.host = host; }} }} public sealed class Host {{ public Host({parameters}) {{ {body} }} }}",
                          $"M:Lib.Host.#ctor({id})");

        Assert.Equal(GenerationReasons.VOCABULARY, Read(trace).Reason);
        Assert.Null(trace.Answer.Model);
    }

    [Theory]
    [InlineData("return Choice.Value ? p : Run(new Leaf());")]
    [InlineData("if (Choice.Value) return p; return Run(new Leaf());")]
    public void Recursive_arguments_made_by_the_library_are_not_setup_arguments(string body)
    {
        var trace = Trace("public static class Choice { public static bool Value; } public sealed class Leaf { } " +
                          $"public static class Api {{ public static object Run(object p) {{ {body} }} }}", "M:Lib.Api.Run(System.Object)");

        Assert.Equal(GenerationReasons.VOCABULARY, Read(trace).Reason);
        Assert.Null(trace.Answer.Model);
    }

    [Theory]
    [InlineData("Assign(ref result, value);", "private static void Assign(ref object target, object value) => target = value;")]
    [InlineData("Forward(ref result, value);", "private static void Forward(ref object target, object value) => Assign(ref target, value); private static void Assign(ref object target, object value) => target = value;")]
    [InlineData("Alias(ref result) = value;", "private static ref object Alias(ref object target) => ref target;")]
    [InlineData("Assign(value, ref result);", "private static void Assign(object value, ref object target) => target = value;")]
    [InlineData("Assign(ref result, value);", "private static void Assign(ref object target, object value) { ref var alias = ref target; alias = value; }")]
    public void A_ref_store_through_source_helpers_is_an_output(string body, string helpers)
    {
        var trace = Trace($"public static class Api {{ public static void Run(ref object result, object value) {{ {body} }} {helpers} }}",
                          "M:Lib.Api.Run(System.Object@,System.Object)");

        Assert.True(trace.Answer.Model is not null, $"{trace.Answer.Reason} / {trace.Answer.ModelReason}: {trace.Answer.Detail}");
        Assert.Contains("arg:value", trace.Answer.Model!.Outputs["result"].ToString());
    }

    [Fact]
    public void A_ref_forwarded_to_a_reader_is_not_an_output()
    {
        var trace = Trace("public static class Api { public static void Run(ref object result, object value) => Read(ref result); private static object Read(ref object target) => target; }",
                          "M:Lib.Api.Run(System.Object@,System.Object)");

        Assert.NotNull(trace.Answer.Model);
        Assert.Empty(trace.Answer.Model!.Outputs);
    }

    [Fact]
    public void An_unknown_value_written_through_a_returned_ref_is_not_dropped_from_the_output()
    {
        var trace = Trace("public static class Api { public static void Run(ref object result, object value) { Alias(ref result) = Make(); } private static ref object Alias(ref object target) => ref target; private static extern object Make(); }",
                          "M:Lib.Api.Run(System.Object@,System.Object)");

        Assert.Null(trace.Answer.Model);
        Assert.Equal(GenerationReasons.VOCABULARY, trace.Answer.ModelReason);
    }

    [Theory]
    [InlineData("Action", "_callback()")]
    [InlineData("Action<object>", "_callback(new object())")]
    public void Holder_triggers_record_executed_members_independently_of_input_count(string type, string invocation)
    {
        var trace = Trace($"public sealed class Receiver {{ private {type} _callback; public Receiver({type} callback) {{ _callback = callback; }} public void Fire() => {invocation}; public void Quiet() {{ }} }} " +
                          $"public static class Api {{ public static Receiver Keep({type} callback) => new Receiver(callback); }}",
                          type == "Action" ? "M:Lib.Api.Keep(System.Action)" : "M:Lib.Api.Keep(System.Action{System.Object})");
        var values = Read(trace);

        Assert.Equal(["M:Lib.Receiver.Fire"], values.HolderTriggers["callback"]);
    }

    [Theory]
    [InlineData("public static void Run(Action callback, Action<Carrier> take) => take(new Carrier(callback));", "M:Lib.Api.Run(System.Action,System.Action{Lib.Carrier})")]
    [InlineData("public static void Run(Action callback, out Carrier output) => output = new Carrier(callback);", "M:Lib.Api.Run(System.Action,Lib.Carrier@)")]
    public void A_fresh_observed_object_containing_a_probe_delegate_has_no_new_name(string member, string id)
    {
        var trace = Trace("public sealed class Carrier { public Action Callback; public Carrier(Action callback) { Callback = callback; } } public static class Api { " + member + " }", id);

        Assert.Null(trace.Answer.Model);
        Assert.Equal(GenerationReasons.VOCABULARY, trace.Answer.ModelReason);
    }

    [Fact]
    public void A_fresh_delegate_input_with_only_fresh_library_fields_is_new()
    {
        var trace = Trace("public sealed class Leaf { } public sealed class Carrier { public Leaf Value = new Leaf(); } public static class Api { public static void Run(Action<Carrier> take) => take(new Carrier()); }",
                          "M:Lib.Api.Run(System.Action{Lib.Carrier})");

        Assert.NotNull(trace.Answer.Model);
        Assert.Equal("new", Assert.Single(Assert.Single(trace.Answer.Model!.Fates).Inputs!.Single()).ToString());
    }

    [Fact]
    public void An_unnamed_value_in_an_iterator_delegate_input_has_vocabulary_reason()
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

        Assert.Equal(GenerationReasons.VOCABULARY, Read(trace).Reason);
        Assert.Null(trace.Answer.Model);
        Assert.Equal(GenerationReasons.VOCABULARY, trace.Answer.ModelReason);
    }

    [Fact]
    public void An_argument_forwarded_through_an_out_identity_cast_keeps_its_name_in_a_delegate_input()
    {
        var trace = Trace("""
            public static class Api
            {
                public static void Run(object value, Action<object> take) { Copy(value, out var alias); take(alias); }
                private static void Copy(object value, out object alias) => alias = System.Runtime.CompilerServices.Unsafe.As<object>(value);
            }
            """, "M:Lib.Api.Run(System.Object,System.Action{System.Object})");
        var values = Read(trace);

        Assert.Null(values.Reason);
        Assert.NotNull(trace.Answer.Model);
        Assert.Equal("arg:value", Assert.Single(Assert.Single(values.Inputs["take"])).Canonical);
    }

    [Fact]
    public void An_unknown_call_result_in_a_delegate_input_has_vocabulary_reason()
    {
        var values = Read("public static class Api { public static void Run(Action<object> take) => take(Make()); public static extern object Make(); }",
                          "M:Lib.Api.Run(System.Action{System.Object})");

        Assert.Equal(GenerationReasons.VOCABULARY, values.Reason);
    }

    [Theory]
    [InlineData("object", "System.Object")]
    [InlineData("Item", "Lib.Item")]
    public void A_setup_built_array_element_forwarded_through_an_out_identity_cast_keeps_its_name(string type, string id)
    {
        var trace = Trace("public sealed class Item { } public static class Api { " +
                          $"public static void Run({type}[] values, Action<{type}> take) {{ Copy(values, out var alias); take(alias[0]); }} " +
                          $"private static void Copy({type}[] values, out {type}[] alias) => alias = System.Runtime.CompilerServices.Unsafe.As<{type}[]>(values); }}",
                          $"M:Lib.Api.Run({id}[],System.Action{{{id}}})");
        var values = Read(trace);

        Assert.Null(values.Reason);
        Assert.NotNull(trace.Answer.Model);
        Assert.Equal("elements(arg:values)", Assert.Single(Assert.Single(values.Inputs["take"])).Canonical);
    }

    [Fact]
    public void An_argument_returned_is_arg()
    {
        var values = Read("public static class Api { public static object Echo(object x) => x; }", "M:Lib.Api.Echo(System.Object)");

        Assert.Equal("[arg:x]", values.Result?.ToString());
    }

    [Fact]
    public void An_element_of_an_element_of_a_nested_list_argument_is_named_recursively()
    {
        var values = Read("public static class Api { public static object Pick(List<List<object>> x) => x[0][0]; }",
                          "M:Lib.Api.Pick(System.Collections.Generic.List{System.Collections.Generic.List{System.Object}})");

        Assert.Equal("[elements(elements(arg:x))]", values.Result?.ToString());
    }

    [Fact]
    public void A_recipe_built_library_argument_returned_is_arg()
    {
        var values = Read("public sealed class Item { } public static class Api { public static Item Echo(Item x) => x; }",
                          "M:Lib.Api.Echo(Lib.Item)");

        Assert.Equal("[arg:x]", values.Result?.ToString());
    }

    [Fact]
    public void One_of_a_sources_elements_is_elements_of_the_argument()
    {
        var values = Read("public static class Api { public static object First(IEnumerable<object> source) { foreach (var x in source) return x; return null; } }",
                          "M:Lib.Api.First(System.Collections.Generic.IEnumerable{System.Object})");

        Assert.Equal("[elements(arg:source)]", values.Result?.ToString());
    }

    [Fact]
    public void A_new_list_of_the_sources_elements_is_a_collection()
    {
        var values = Read("public static class Api { public static List<object> Copy(IEnumerable<object> source) { var r = new List<object>(); foreach (var x in source) r.Add(x); return r; } }",
                          "M:Lib.Api.Copy(System.Collections.Generic.IEnumerable{System.Object})");

        Assert.Equal("collection(elements(arg:source))", values.Result?.ToString());
    }

    [Fact]
    public void A_lazy_filter_is_a_sequence_and_its_predicate_receives_elements()
    {
        var values = Read("public static class Api { public static IEnumerable<object> Filter(IEnumerable<object> source, Func<object, bool> predicate) { foreach (var x in source) if (predicate(x)) yield return x; } }",
                          "M:Lib.Api.Filter(System.Collections.Generic.IEnumerable{System.Object},System.Func{System.Object,System.Boolean})");

        Assert.Equal("sequence(elements(arg:source))", values.Result?.ToString());
        Assert.Equal("elements(arg:source)", Assert.Single(Assert.Single(values.Inputs["predicate"])).ToString());
    }

    [Fact]
    public void A_delegate_input_from_a_foreach_over_an_argument_with_library_enumerators_is_elements()
    {
        var trace = Trace("""
            public sealed class Many<T> : IEnumerable<T>
            {
                private readonly T[] _items = new T[0];
                public IEnumerator<T> GetEnumerator() { foreach (var item in _items) yield return item; }
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            }
            public static class Api
            {
                public static bool All<T>(IEnumerable<T> source, Func<T, bool> predicate)
                {
                    foreach (var x in source) if (!predicate(x)) return false;
                    return true;
                }
            }
            """, "M:Lib.Api.All``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})");
        var values = Read(trace);

        Assert.Null(values.Reason);
        Assert.NotNull(trace.Answer.Model);
        Assert.Equal("elements(arg:source)", Assert.Single(Assert.Single(values.Inputs["predicate"])).Canonical);
    }

    [Fact]
    public void A_current_whose_enumeration_the_engine_did_not_decide_has_vocabulary_reason()
    {
        var trace = Trace("""
            public sealed class Crate { }
            public sealed class CrateWalker : IEnumerator<object>
            {
                private object _value;
                public object Current => _value;
                object System.Collections.IEnumerator.Current => Current;
                public bool MoveNext() => false;
                public void Reset() { }
                public void Dispose() { }
            }
            public static class CrateEnumeration { public static CrateWalker GetEnumerator(this Crate crate) => new CrateWalker(); }
            public static class Api { public static void Run(Crate crate, Action<object> take) { foreach (var x in crate) take(x); } }
            """, "M:Lib.Api.Run(Lib.Crate,System.Action{System.Object})");

        Assert.Equal(GenerationReasons.VOCABULARY, Read(trace).Reason);
        Assert.Null(trace.Answer.Model);
        Assert.Equal(GenerationReasons.VOCABULARY, trace.Answer.ModelReason);
    }

    [Fact]
    public void An_element_of_a_span_a_helper_hands_out_through_an_out_parameter_has_vocabulary_reason()
    {
        var trace = Trace("""
            public static class Api
            {
                public static void Run<T>(IEnumerable<T> source, Action<T> take)
                {
                    if (TryGetSpan(source, out var span))
                        for (var i = 0; i < span.Length; i++) take(span[i]);
                }
                private static bool TryGetSpan<T>(IEnumerable<T> source, out ReadOnlySpan<T> span)
                {
                    if (source.GetType() == typeof(T[])) { span = System.Runtime.CompilerServices.Unsafe.As<T[]>(source); return true; }
                    if (source.GetType() == typeof(List<T>)) { span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(System.Runtime.CompilerServices.Unsafe.As<List<T>>(source)); return true; }
                    span = default;
                    return false;
                }
            }
            """, "M:Lib.Api.Run``1(System.Collections.Generic.IEnumerable{``0},System.Action{``0})");

        Assert.Equal(GenerationReasons.VOCABULARY, Read(trace).Reason);
        Assert.Null(trace.Answer.Model);
        Assert.Equal(GenerationReasons.VOCABULARY, trace.Answer.ModelReason);
    }

    [Fact]
    public void A_fresh_library_object_is_new()
    {
        var values = Read("public sealed class Made { } public static class Api { public static Made Make(object x) => new Made(); }",
                          "M:Lib.Api.Make(System.Object)");

        Assert.Equal("new", values.Result?.ToString());
    }

    [Fact]
    public void A_fresh_result_holding_the_argument_is_new_and_keeps_the_argument()
    {
        var values = Read("public sealed class Made { public object Value; public Made(object value) { Value = value; } } public static class Api { public static Made Make(object x) => new Made(x); }",
                          "M:Lib.Api.Make(System.Object)");

        Assert.Equal("new", values.Result?.ToString());
        Assert.Equal(["arg:x"], Text(values.Keeps["result"]));
    }

    [Fact]
    public void A_fresh_result_holding_a_recipe_built_library_argument_keeps_that_argument()
    {
        var values = Read("public sealed class Item { } public sealed class Made { public Item Value; public Made(Item value) { Value = value; } } public static class Api { public static Made Make(Item item) => new Made(item); }",
                          "M:Lib.Api.Make(Lib.Item)");

        Assert.Equal("new", values.Result?.ToString());
        Assert.Equal(["arg:item"], Text(values.Keeps["result"]));
    }

    [Fact]
    public void An_object_of_a_probe_class_the_library_makes_has_no_name()
    {
        var values = Read("public static class Api { public static T Make<T>(object marker) where T : new() => new T(); }",
                          "M:Lib.Api.Make``1(System.Object)");

        Assert.Equal(GenerationReasons.VOCABULARY, values.Reason);
    }

    [Fact]
    public void The_receiver_keeps_an_item_the_call_assigns()
    {
        var values = Read("public sealed class Bag { private object _value; public void Add(object value) { _value = value; } }",
                          "M:Lib.Bag.Add(System.Object)");

        Assert.Equal(["arg:value"], Text(values.Keeps["this"]));
    }

    [Fact]
    public void The_receiver_keeps_an_item_through_a_fresh_node_it_links_in()
    {
        var values = Read("public sealed class Node { public object Value; public Node(object value) { Value = value; } } public sealed class Bag { private Node _node; public void Add(object value) { _node = new Node(value); } }",
                          "M:Lib.Bag.Add(System.Object)");

        Assert.Equal(["arg:value"], Text(values.Keeps["this"]));
    }

    [Fact]
    public void The_receiver_keeps_what_a_delegate_returned()
    {
        var values = Read("public sealed class Bag { private object _value; public void Add(Func<object> make) { _value = make(); } }",
                          "M:Lib.Bag.Add(System.Func{System.Object})");

        Assert.Equal(["returns:make"], Text(values.Keeps["this"]));
    }

    [Fact]
    public void A_receiver_that_keeps_a_seed_has_vocabulary_reason()
    {
        var values = Read("public abstract class Hook { } public sealed class Bag { private Hook _hook; private object _value; public void Keep(Action done) { _value = _hook; done(); } }",
                          "M:Lib.Bag.Keep(System.Action)");

        Assert.Equal(GenerationReasons.VOCABULARY, values.Reason);
    }

    [Fact]
    public void A_sealed_library_receiver_can_be_a_keeper()
    {
        var values = Read("public sealed class Bag { private object _value; public void Set(object value) { _value = value; } }",
                          "M:Lib.Bag.Set(System.Object)");

        Assert.Contains("this", values.Keeps.Keys);
    }

    [Fact]
    public void A_library_typed_argument_keeps_the_other_argument()
    {
        var values = Read("public sealed class Bag { public object Value; } public static class Api { public static void Put(Bag bag, object value) { bag.Value = value; } }",
                          "M:Lib.Api.Put(Lib.Bag,System.Object)");

        Assert.Equal(["arg:value"], Text(values.Keeps["bag"]));
    }

    [Fact]
    public void A_holder_result_keeps_a_value_but_not_its_probe_delegate()
    {
        var values = Read("public sealed class Result { private readonly object _value; private readonly Action _done; public Result(object value, Action done) { _value = value; _done = done; } public void Fire() { _done(); } } public static class Api { public static Result Make(object value, Action done) => new Result(value, done); }",
                          "M:Lib.Api.Make(System.Object,System.Action)");

        Assert.Equal(["arg:value"], Text(values.Keeps["result"]));
        Assert.DoesNotContain(values.Keeps["result"], value => value.ToString().Contains("done", StringComparison.Ordinal));
    }

    [Fact]
    public void A_constructor_keeps_its_argument_on_this_and_has_no_result()
    {
        var values = Read("public sealed class Bag { private readonly object _value; public Bag(object value) { _value = value; } }",
                          "M:Lib.Bag.#ctor(System.Object)");

        Assert.Null(values.Result);
        Assert.Equal(["arg:value"], Text(values.Keeps["this"]));
    }

    [Fact]
    public void A_constructor_that_holds_its_delegate_has_holder_result_and_no_result()
    {
        var trace = Trace("public sealed class Result { private readonly Action _done; public Result(Action done) { _done = done; } public void Fire() { _done(); } }",
                          "M:Lib.Result.#ctor(System.Action)");
        var values = Read(trace);

        Assert.Null(values.Result);
        Assert.Equal(FateClassifier.RESULT, FateOf(trace, "done").Holder);
    }

    [Fact]
    public void A_keeping_chain_lists_the_keeper_and_two_pre_existing_objects()
    {
        var values = Read("public sealed class Link { public Link Next; public object Value; public Link(int depth) { if (depth > 0) Next = new Link(depth - 1); } } public sealed class Bag { private readonly Link _root = new Link(2); public void Add(object value) { _root.Next.Value = value; } }",
                          "M:Lib.Bag.Add(System.Object)");

        Assert.True(Assert.Single(values.KeepingChain).Value.Count >= 2);
    }

    [Theory]
    [InlineData("_a.Value = value; _b.Value = value;")]
    [InlineData("_b.Value = value; _a.Value = value;")]
    [InlineData("var kept = value; _a.Value = kept; _b.Value = kept;")]
    public void A_value_kept_through_two_pre_existing_objects_keeps_both_on_the_chain(string body)
    {
        var source = "public sealed class Node { public object Value; } " +
                     $"public sealed class Bag {{ private readonly Node _a = new Node(); private readonly Node _b = new Node(); public void Add(object value) {{ {body} }} }}";
        var trace = Trace(source, "M:Lib.Bag.Add(System.Object)");
        var values = Read(trace);

        Assert.Equal(["arg:value"], Text(values.Keeps["this"]));
        Assert.True(Assert.Single(values.KeepingChain).Value.Count >= 3);
        Assert.NotEqual(ModelReasons.LIBRARY_STATE, trace.Answer.ModelReason);
        Assert.NotNull(trace.Answer.Model);
    }

    [Fact]
    public void A_write_into_a_pre_existing_object_on_no_path_to_a_kept_value_gives_library_state()
    {
        var trace = Trace("public sealed class Node { public object Value; public int Count; } " +
                          "public sealed class Bag { private readonly Node _a = new Node(); private readonly Node _b = new Node(); public void Add(object value) { _a.Value = value; _b.Count = 1; } }",
                          "M:Lib.Bag.Add(System.Object)");

        Assert.Null(trace.Answer.Model);
        Assert.Equal(ModelReasons.LIBRARY_STATE, trace.Answer.ModelReason);
    }

    [Theory]
    [InlineData("items[0] = items[0]; cells[0] = items[0];")]
    [InlineData("var item = items[0]; items[0] = item; cells[0] = item;")]
    public void An_element_the_call_writes_back_in_place_keeps_its_element_name(string body)
    {
        var values = Read($"public static class Api {{ public static void Run(object[] items, object[] cells) {{ {body} }} }}",
                          "M:Lib.Api.Run(System.Object[],System.Object[])");

        Assert.Null(values.Reason);
        Assert.Equal(["elements(arg:items)"], Text(values.Stores["cells"]));
        Assert.Equal(["elements(arg:items)"], Text(values.Stores["items"]));
    }

    [Fact]
    public void An_argument_the_call_writes_into_an_arrays_cell_is_not_an_element_of_that_array()
    {
        var values = Read("public static class Api { public static void Run(object value, object[] items) { items[0] = value; } }",
                          "M:Lib.Api.Run(System.Object,System.Object[])");

        Assert.Equal(["arg:value"], Text(values.Stores["items"]));
    }

    [Fact]
    public void An_out_argument_assigned_the_argument_names_only_that_argument()
    {
        var values = Read("public static class Api { public static void Assign(object x, out object output) { output = x; } }",
                          "M:Lib.Api.Assign(System.Object,System.Object@)");

        Assert.Equal("[arg:x]", values.Outputs["output"].ToString());
    }

    [Fact]
    public void An_out_argument_assigned_a_fresh_library_object_is_new()
    {
        var values = Read("public sealed class Made { } public static class Api { public static void Assign(object x, out Made output) { output = new Made(); } }",
                          "M:Lib.Api.Assign(System.Object,Lib.Made@)");

        Assert.Equal("new", values.Outputs["output"].ToString());
    }

    [Fact]
    public void A_ref_argument_reassigned_to_another_argument_names_both_possible_values()
    {
        var values = Read("public static class Api { public static void Assign(object x, ref object output) { output = x; } }",
                          "M:Lib.Api.Assign(System.Object,System.Object@)");

        Assert.Equal(["arg:output", "arg:x"], Text(values.Outputs["output"].Values));
    }

    [Fact]
    public void A_ref_argument_the_member_never_assigns_has_no_output()
    {
        var values = Read("public static class Api { public static void Read(object x, ref object output) { _ = x; } }",
                          "M:Lib.Api.Read(System.Object,System.Object@)");

        Assert.DoesNotContain("output", values.Outputs.Keys);
    }

    [Fact]
    public void An_item_written_to_an_array_cell_is_named_by_stores()
    {
        var trace = Trace("public static class Api { public static void Put(object value, object[] target) { target[0] = value; } }",
                          "M:Lib.Api.Put(System.Object,System.Object[])");
        var values = Read(trace);

        Assert.True(new EffectReader(trace.Driver!, trace.Run!).HasEffect("target", EffectReader.WRITES_CELLS));
        Assert.Equal(["arg:value"], Text(values.Stores["target"]));
    }

    [Fact]
    public void A_fresh_library_object_stored_in_an_array_cell_has_no_store_name()
    {
        var values = Read("public sealed class Made { } public static class Api { public static void Put(object x, Made[] target) { target[0] = new Made(); } }",
                          "M:Lib.Api.Put(System.Object,Lib.Made[])");

        Assert.Equal(GenerationReasons.VOCABULARY, values.Reason);
    }

    [Fact]
    public void A_holders_input_is_holder_arg_and_names_the_trigger_member()
    {
        var values = Read("public sealed class Result { private readonly Action<object> _take; public Result(Action<object> take) { _take = take; } public void Fire(object value) { _take(value); } } public static class Api { public static Result Make(Action<object> take) => new Result(take); }",
                          "M:Lib.Api.Make(System.Action{System.Object})");

        Assert.Equal("holder-arg:0", Assert.Single(Assert.Single(values.Inputs["take"])).ToString());
        Assert.Contains("M:Lib.Result.Fire(System.Object)", values.HolderTriggers["take"]);
    }

    [Fact]
    public void A_fresh_event_args_like_input_made_by_a_holder_member_is_new()
    {
        var values = Read("public sealed class Args { } public sealed class Result { private readonly Action<Args> _take; public Result(Action<Args> take) { _take = take; } public void Fire() { _take(new Args()); } } public static class Api { public static Result Make(Action<Args> take) => new Result(take); }",
                          "M:Lib.Api.Make(System.Action{Lib.Args})");

        Assert.Equal("new", Assert.Single(Assert.Single(values.Inputs["take"])).ToString());
    }

    [Fact]
    public void A_fresh_delegate_input_holding_an_argument_has_vocabulary_reason()
    {
        var values = Read("public sealed class Args { public object Value; public Args(object value) { Value = value; } } public sealed class Result { private readonly object _value; private readonly Action<Args> _take; public Result(object value, Action<Args> take) { _value = value; _take = take; } public void Fire() { _take(new Args(_value)); } } public static class Api { public static Result Make(object value, Action<Args> take) => new Result(value, take); }",
                          "M:Lib.Api.Make(System.Object,System.Action{Lib.Args})");

        Assert.Equal(GenerationReasons.VOCABULARY, values.Reason);
    }

    [Fact]
    public void An_awaited_result_that_would_need_a_name_is_named_as_its_synchronous_twins_inside_task()
    {
        var values = Read("public static class Api { public static async Task<object> Make(object x) { await Task.Delay(1); return new object(); } }",
                          "M:Lib.Api.Make(System.Object)");
        var twin = Read("public static class Api { public static object Make(object x) => new object(); }", "M:Lib.Api.Make(System.Object)");

        Assert.Null(twin.Reason);
        Assert.Equal("new", twin.Result?.ToString());
        Assert.Equal(twin.Reason, values.Reason);
        Assert.Equal($"task({twin.Result})", values.Result?.ToString());
    }

    [Fact]
    public void A_carried_probe_fired_in_task_run_has_vocabulary_reason()
    {
        var values = Read("public sealed class Carrier { public Action Work; public Carrier(Action work) { Work = work; } } public static class Api { public static void Start(Carrier carrier) { Task.Run(carrier.Work); } }",
                          "M:Lib.Api.Start(Lib.Carrier)");

        Assert.Equal(GenerationReasons.VOCABULARY, values.Reason);
    }

    [Fact]
    public void A_trigger_members_extra_state_and_keep_do_not_enter_the_original_entry()
    {
        var values = Read("public sealed class Result { private readonly object _value; private readonly Action _done; public Result(object value, Action done) { _value = value; _done = done; } public void Fire(object other) { Cache.Last = other; _done(); } } public static class Api { public static Result Make(object value, Action done) => new Result(value, done); }",
                          "M:Lib.Api.Make(System.Object,System.Action)");

        Assert.Null(values.Reason);
        Assert.Null(values.Result);
        Assert.Equal(["arg:value"], Text(values.Keeps["result"]));
    }

    [Fact]
    public void A_fresh_result_wrapping_the_pre_existing_receiver_has_vocabulary_reason()
    {
        var values = Read("public sealed class Made { public Api Value; public Made(Api value) { Value = value; } } public sealed class Api { public Made Wrap(object x) => new Made(this); }",
                          "M:Lib.Api.Wrap(System.Object)");

        Assert.Equal(GenerationReasons.VOCABULARY, values.Reason);
    }

    [Fact]
    public void A_fresh_delegate_input_holding_a_pre_existing_library_object_has_vocabulary_reason()
    {
        var values = Read("public sealed class Args { public Result Owner; public Args(Result owner) { Owner = owner; } } public sealed class Result { private readonly Action<Args> _take; public Result(Action<Args> take) { _take = take; } public void Fire() { _take(new Args(this)); } } public static class Api { public static Result Make(Action<Args> take) => new Result(take); }",
                          "M:Lib.Api.Make(System.Action{Lib.Args})");

        Assert.Equal(GenerationReasons.VOCABULARY, values.Reason);
    }

    [Fact]
    public void A_result_whose_allocation_site_also_ran_in_setup_has_vocabulary_reason()
    {
        var values = Read("public sealed class Made { } public static class Factory { public static Made Make() => new Made(); } public sealed class Carrier { public Made Value; public Carrier() { Value = Factory.Make(); } } public static class Api { public static Made Run(Carrier carrier) => carrier.Value; }",
                          "M:Lib.Api.Run(Lib.Carrier)");

        Assert.Equal(GenerationReasons.VOCABULARY, values.Reason);
    }

    [Fact]
    public void A_value_linked_only_in_an_unknown_execution_is_not_kept()
    {
        var values = Read("public sealed class Bag { private object _value; public void Later(object value, Action done) { Sink.Take(new Action(() => _value = value)); done(); } }",
                          "M:Lib.Bag.Later(System.Object,System.Action)");

        Assert.DoesNotContain("this", values.Keeps.Keys);
    }

    [Fact]
    public void The_same_fresh_object_returned_and_handed_to_invoke_now_has_vocabulary_reason()
    {
        var values = Read("public sealed class Made { } public static class Api { public static Made Run(Action<Made> take, object x) { var value = new Made(); take(value); return value; } }",
                          "M:Lib.Api.Run(System.Action{Lib.Made},System.Object)");

        Assert.Equal(GenerationReasons.VOCABULARY, values.Reason);
    }

    [Fact]
    public void The_same_fresh_object_returned_and_assigned_to_out_has_vocabulary_reason()
    {
        var values = Read("public sealed class Made { } public static class Api { public static Made Run(object x, out Made output) { var value = new Made(); output = value; return value; } }",
                          "M:Lib.Api.Run(System.Object,Lib.Made@)");

        Assert.Equal(GenerationReasons.VOCABULARY, values.Reason);
    }

    [Theory]
    [InlineData("public static Wrapper Run(Action<Part> take, object x) { var part = new Part(); take(part); return new Wrapper(part); }",
                "M:Lib.Api.Run(System.Action{Lib.Part},System.Object)")]
    [InlineData("public static Wrapper Run(object x, out Part output) { var part = new Part(); output = part; return new Wrapper(part); }",
                "M:Lib.Api.Run(System.Object,Lib.Part@)")]
    [InlineData("public static Wrapper Run(Action<Wrapper> take, object x) { var part = new Part(); take(new Wrapper(part)); return new Wrapper(part); }",
                "M:Lib.Api.Run(System.Action{Lib.Wrapper},System.Object)")]
    public void A_fresh_object_reached_from_a_new_result_and_shown_again_elsewhere_has_vocabulary_reason(string member, string id)
    {
        var values = Read("public sealed class Part { } public sealed class Wrapper { public Part Value; public Wrapper(Part value) { Value = value; } } " +
                          $"public static class Api {{ {member} }}", id);

        Assert.Equal(GenerationReasons.VOCABULARY, values.Reason);
    }

    [Fact]
    public void A_new_result_and_a_new_input_that_share_no_object_are_both_new()
    {
        var values = Read("public sealed class Part { } public sealed class Wrapper { public Part Value; public Wrapper(Part value) { Value = value; } } " +
                          "public static class Api { public static Wrapper Run(Action<Part> take, object x) { take(new Part()); return new Wrapper(new Part()); } }",
                          "M:Lib.Api.Run(System.Action{Lib.Part},System.Object)");

        Assert.Null(values.Reason);
        Assert.Equal(ConcurrencyHunter.Providers.LibraryModels.LibraryResultKind.New, values.Result?.Kind);
        Assert.Equal(["new"], Text(Assert.Single(values.Inputs["take"])));
    }

    [Fact]
    public void A_task_result_holding_the_delegate_is_a_holder_result_with_no_result_as_its_synchronous_twin()
    {
        const string RESULT = "public sealed class Result { private readonly Action _done; public Result(Action done) { _done = done; } public void Fire() { _done(); } } ";
        // The confirmation run's trigger fires on the awaited object, which the heap carries: the holder stands, and a holder of the
        // result describes the innermost completion value, so the entry carries no result.
        var values = Read(RESULT + "public static class Api { public static async Task<Result> Make(Action done) { await Task.Yield(); return new Result(done); } }",
                          "M:Lib.Api.Make(System.Action)");
        var twinTrace = Trace(RESULT + "public static class Api { public static Result Make(Action done) => new Result(done); }", "M:Lib.Api.Make(System.Action)");
        var twin = Read(twinTrace);

        Assert.Equal(new ClassifiedFate(FateClassifier.HOLDER, FateClassifier.RESULT), twinTrace.Answer.Classified!["done"]);
        Assert.Null(twin.Result);
        Assert.Equal(twin.Reason, values.Reason);
        Assert.Null(values.Result);
    }

    [Fact]
    public void A_task_member_completing_with_a_box_or_an_unseen_object_has_no_task_result_naming_only_the_box()
    {
        var trace = Trace("public static class Externals { public static extern Box Opaque(); } " +
                          "public static class Api { public static async Task<Box> Pick(Box box, bool flag) { await Task.Yield(); return flag ? box : Externals.Opaque(); } }",
                          "M:Lib.Api.Pick(Lib.Box,System.Boolean)");

        Assert.Equal(GenerationReasons.VOCABULARY, Read(trace).Reason);
        Assert.Null(trace.Answer.Model?.Result);
    }

    [Fact]
    public void A_task_member_awaiting_a_stored_task_of_an_unseen_object_has_no_task_result()
    {
        // The field's task is Inner's, which completes with its parameter, bound to what Opaque returned: the stored task completes with
        // an object the heap does not name, however many regions it resolves to.
        var trace = Trace("public static class Externals { public static extern Box Opaque(); } " +
                          "public sealed class Api { private Task<Box> _pending; " +
                          "public async Task<Box> Run(Box box) { _pending = Inner(Externals.Opaque()); return await _pending; } " +
                          "private static async Task<Box> Inner(Box p) => p; }",
                          "M:Lib.Api.Run(Lib.Box)");

        Assert.Equal(GenerationReasons.VOCABULARY, Read(trace).Reason);
        Assert.Null(trace.Answer.Model?.Result);
    }

    [Fact]
    public void A_holder_input_the_constructor_made_once_has_vocabulary_reason()
    {
        var values = Read("public sealed class Made { } public sealed class Result { private readonly Made _value = new Made(); private readonly Action<Made> _take; public Result(Action<Made> take) { _take = take; } public void Fire() { _take(_value); } } public static class Api { public static Result Make(Action<Made> take) => new Result(take); }",
                          "M:Lib.Api.Make(System.Action{Lib.Made})");

        Assert.Equal(GenerationReasons.VOCABULARY, values.Reason);
    }

    [Fact]
    public void A_library_argument_kept_by_the_receiver_is_arg_and_its_seed_is_not_separately_kept()
    {
        var values = Read("public abstract class Hook { } public sealed class Item { public Hook Hook; } public sealed class Bag { private Item _item; public void Set(Item item) { _item = item; } }",
                          "M:Lib.Bag.Set(Lib.Item)");

        Assert.Equal(["arg:item"], Text(values.Keeps["this"]));
    }

    [Fact]
    public void A_seeded_storage_array_keeps_only_the_item_the_call_adds()
    {
        var values = Read("public sealed class Bag { private readonly object[] _items = new object[2]; public void Add(object value) { _items[0] = value; } }",
                          "M:Lib.Bag.Add(System.Object)");

        Assert.Equal(["arg:value"], Text(values.Keeps["this"]));
    }

    [Fact]
    public void A_receiver_keeps_nothing_through_a_seeded_field_the_call_did_not_write()
    {
        var values = Read("public abstract class Hook { } public sealed class Bag { private Hook _hook; private object _value; public void Set(object value) { _value = value; } }",
                          "M:Lib.Bag.Set(System.Object)");

        Assert.Equal(["arg:value"], Text(values.Keeps["this"]));
    }

    [Fact]
    public void A_kept_probe_delegate_is_left_to_its_fate_not_the_keeps_set()
    {
        var values = Read("public sealed class Result { private readonly Action _done; public Result(Action done) { _done = done; } public void Fire() { _done(); } } public static class Api { public static Result Make(Action done) => new Result(done); }",
                          "M:Lib.Api.Make(System.Action)");

        Assert.DoesNotContain("result", values.Keeps.Keys);
    }

    [Fact]
    public void A_fresh_object_is_not_new_at_a_kept_value_place()
    {
        var values = Read("public sealed class Made { } public sealed class Bag { private Made _value; public void Add(object x) { _value = new Made(); } }",
                          "M:Lib.Bag.Add(System.Object)");

        Assert.DoesNotContain("this", values.Keeps.Keys);
    }

    [Fact]
    public void A_constructor_created_object_is_named_this()
    {
        var trace = Trace("public sealed class Bag { public Bag(object value) { } }", "M:Lib.Bag.#ctor(System.Object)");
        var values = Read(trace);
        var created = Assert.Single(Slot(trace, DriverSynthesizer.KEEP_TYPE, "R"));

        Assert.Equal("this", values.Name(created, ValuePlace.KeptValue)?.ToString());
    }

    private static ValueProvenance Read(string source, string member) => Read(Trace(source, member));

    private static ValueProvenance Read(GenerationTrace trace)
    {
        Assert.True(trace.Run is { Stopped: false }, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        Assert.NotNull(trace.Answer.Classified);
        return new ValueProvenance(trace.Driver!, trace.Run!, trace.Answer.Classified!, trace.Confirmation);
    }

    private static string[] Text(IEnumerable<ConcurrencyHunter.Providers.LibraryModels.LibraryValue> values) =>
        values.Select(value => value.ToString()).Order(StringComparer.Ordinal).ToArray();
}
