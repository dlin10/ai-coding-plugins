using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>Delegate values and delegate slots in generated entries (task 11, R5, R6): a delegate parameter is never a named value, and
/// the slot a handler waits in is no library state (TD-034a).</summary>
public sealed class DelegateSlotEntryTests
{
    private const string PUBLISHER = "public void Fire() { _h?.Invoke(); }";

    [Fact]
    public void A_receiver_setter_storing_its_Action_with_a_public_Fire_gets_an_entry_holding_it_in_this_with_no_keeps()
    {
        var trace = Trace($"public sealed class Pub {{ private Action _h; public Action H {{ set {{ _h = value; }} }} {PUBLISHER} }}",
                          "M:Lib.Pub.set_H(System.Action)");

        var model = Entry(trace);
        Assert.Equal(new LibraryFate("value", LibraryFateKind.Holder, LibraryHolderKind.This, null), Assert.Single(model.Fates));
        Assert.Empty(model.Keeps);
    }

    [Fact]
    public void A_custom_add_combining_into_its_field_with_a_public_Fire_gets_an_entry_holding_it_in_this()
    {
        var trace = Trace("public sealed class Pub { private Action _h; " +
                          $"public event Action Changed {{ add {{ _h += value; }} remove {{ }} }} {PUBLISHER} }}",
                          "M:Lib.Pub.add_Changed(System.Action)");

        var model = Entry(trace);
        Assert.Equal(new LibraryFate("value", LibraryFateKind.Holder, LibraryHolderKind.This, null), Assert.Single(model.Fates));
        Assert.Empty(model.Keeps);
    }

    [Fact]
    public void A_custom_remove_from_its_field_gets_an_entry_with_not_run()
    {
        var trace = Trace("public sealed class Pub { private Action _h; " +
                          $"public event Action Changed {{ add {{ }} remove {{ _h -= value; }} }} {PUBLISHER} }}",
                          "M:Lib.Pub.remove_Changed(System.Action)");

        Assert.Equal(new LibraryFate("value", LibraryFateKind.NotRun, null, null), Assert.Single(Entry(trace).Fates));
    }

    [Fact]
    public void A_custom_remove_that_also_writes_another_field_is_library_state()
    {
        var trace = Trace("public sealed class Pub { private Action _h; private int _mode; " +
                          $"public event Action Changed {{ add {{ }} remove {{ _h -= value; _mode = 1; }} }} {PUBLISHER} }}",
                          "M:Lib.Pub.remove_Changed(System.Action)");

        Assert.Null(trace.Answer.Model);
        Assert.Equal(ModelReasons.LIBRARY_STATE, trace.Answer.ModelReason);
        Assert.NotEmpty(trace.StateStores);
    }

    [Fact]
    public void A_field_like_events_add_with_a_public_Fire_gets_an_entry_holding_it_in_this()
    {
        var trace = Trace("public sealed class Pub { public event Action Changed; public void Fire() { Changed?.Invoke(); } }",
                          "M:Lib.Pub.add_Changed(System.Action)");

        var model = Entry(trace);
        Assert.Equal(new LibraryFate("value", LibraryFateKind.Holder, LibraryHolderKind.This, null), Assert.Single(model.Fates));
        Assert.Empty(trace.StateStores);
    }

    [Fact]
    public void A_field_like_events_remove_gets_an_entry_with_not_run()
    {
        var trace = Trace("public sealed class Pub { public event Action Changed; public void Fire() { Changed?.Invoke(); } }",
                          "M:Lib.Pub.remove_Changed(System.Action)");

        Assert.Equal(new LibraryFate("value", LibraryFateKind.NotRun, null, null), Assert.Single(Entry(trace).Fates));
        Assert.Empty(trace.StateStores);
    }

    [Theory]
    [InlineData("public sealed class Pub<T> { public event Action Changed; public void Fire() { Changed?.Invoke(); } }",
                "M:Lib.Pub`1.add_Changed(System.Action)")]
    [InlineData("public sealed class Pub<T> { private Action _h; " +
                "public event Action Changed { add { _h += value; } remove { } } public void Fire() { _h?.Invoke(); } }",
                "M:Lib.Pub`1.add_Changed(System.Action)")]
    [InlineData("public sealed class Pub<T> { private Action _h; public Action H { set { _h = value; } } public void Fire() { _h?.Invoke(); } }",
                "M:Lib.Pub`1.set_H(System.Action)")]
    [InlineData("public sealed class Outer<T> { public sealed class Pub<U, V> { public event Action Changed; public void Fire() { Changed?.Invoke(); } } }",
                "M:Lib.Outer`1.Pub`2.add_Changed(System.Action)")]
    public void A_generic_types_delegate_slot_store_gets_the_entry_its_non_generic_twin_gets(string source, string member)
    {
        var trace = Trace(source, member);

        var model = Entry(trace);
        Assert.Equal(new LibraryFate("value", LibraryFateKind.Holder, LibraryHolderKind.This, null), Assert.Single(model.Fates));
        Assert.Empty(trace.StateStores);
    }

    [Theory]
    [InlineData("public sealed class Pub { public event Action Changed = () => { }; public void Fire() { Changed?.Invoke(); } }",
                "M:Lib.Pub.add_Changed(System.Action)")]
    [InlineData("public sealed class Pub { public Pub() { Changed = () => { }; } public event Action Changed; public void Fire() { Changed?.Invoke(); } }",
                "M:Lib.Pub.add_Changed(System.Action)")]
    [InlineData("public sealed class Pub { private Action _h = () => { }; " +
                "public event Action Changed { add { _h += value; } remove { } } public void Fire() { _h?.Invoke(); } }",
                "M:Lib.Pub.add_Changed(System.Action)")]
    [InlineData("public sealed class Pub<T> { public event Action Changed = () => { }; public void Fire() { Changed?.Invoke(); } }",
                "M:Lib.Pub`1.add_Changed(System.Action)")]
    public void A_slot_holding_a_delegate_the_library_created_is_library_state_when_combined_into(string source, string member)
    {
        var trace = Trace(source, member);

        Assert.Null(trace.Answer.Model);
        Assert.Equal(ModelReasons.LIBRARY_STATE, trace.Answer.ModelReason);
        Assert.NotEmpty(trace.StateStores);
    }

    [Fact]
    public void A_member_keeping_an_Item_and_a_delegate_keeps_only_the_item()
    {
        var trace = Trace("public sealed class Item { } " +
                          $"public sealed class Pub {{ private Item _item; private Action _h; public void Put(Item item, Action a) {{ _item = item; _h = a; }} {PUBLISHER} }}",
                          "M:Lib.Pub.Put(Lib.Item,System.Action)");

        var model = Entry(trace);
        var kept = Assert.Single(model.Keeps);
        Assert.Equal("this", kept.Key);
        Assert.Equal("arg:item", Assert.Single(kept.Value).Canonical);
    }

    [Fact]
    public void A_member_returning_its_delegate_parameter_is_vocabulary()
    {
        var trace = Trace("public static class Api { public static Action Echo(Action a) => a; }", "M:Lib.Api.Echo(System.Action)");

        AssertVocabulary(trace);
    }

    [Fact]
    public void A_member_combining_into_its_ref_Action_is_vocabulary()
    {
        var trace = Trace("public static class Api { public static void Add(ref Action a, Action h) { a += h; } }",
                          "M:Lib.Api.Add(System.Action@,System.Action)");

        AssertVocabulary(trace);
    }

    [Fact]
    public void A_member_assigning_its_delegate_parameter_to_an_out_Action_is_vocabulary()
    {
        var trace = Trace("public static class Api { public static void Give(Action a, out Action o) { o = a; } }",
                          "M:Lib.Api.Give(System.Action,System.Action@)");

        AssertVocabulary(trace);
    }

    /// <summary>The entry a trace's answer carries, failing with the answer's reasons when it carries none.</summary>
    /// <param name="trace">The trace.</param>
    private static LibraryModel Entry(GenerationTrace trace)
    {
        Assert.True(trace.Answer.Model is not null,
                    $"no entry: {trace.Answer.Reason ?? trace.Answer.ModelReason} {trace.Answer.Detail}; state stores: " +
                    string.Join(", ", trace.StateStores.Select(store => store.Region)));
        return trace.Answer.Model;
    }

    /// <summary>Asserts that a trace's answer is classified and has no entry, for the reason <c>vocabulary</c>.</summary>
    /// <param name="trace">The trace.</param>
    private static void AssertVocabulary(GenerationTrace trace)
    {
        Assert.True(trace.Answer.Classified is not null, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        Assert.Null(trace.Answer.Model);
        Assert.Equal(ModelReasons.VOCABULARY, trace.Answer.ModelReason);
    }
}
