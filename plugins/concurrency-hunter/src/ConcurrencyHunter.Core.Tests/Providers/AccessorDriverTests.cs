using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>Drivers of property, indexer and event accessors named by their <c>M:</c> ids (R6), whether a member was public
/// before the copy was opened for seeding, and which event accessors are holder triggers (R8).</summary>
public sealed class AccessorDriverTests
{
    private const string LIBRARY = """
        using System;

        namespace Lib
        {
            public sealed class Store
            {
                private object _value;
                private object[] _items = new object[2];
                public object Value { get => _value; set => _value = value; }
                public object this[int i] { get => _items[i]; set => _items[i] = value; }
                public static object Shared { get; set; }
                public object Init { get => _value; init => _value = value; }
                public object Guarded { get => _value; internal set => _value = value; }
                public event Action Changed { add { _value = value; } remove { _value = null; } }
                public static event Action StaticChanged { add { } remove { } }
                public int Count => 1;
                public static int Total => 1;
            }

            public sealed class Seeded { public object Hidden { get; private set; } public object Frozen { get; init; } }
            public sealed class Outer { private sealed class Inner { public object Value { get; set; } } }
            public class Virtual { public virtual event Action Changed; }
            public class Overriding : Virtual { public override event Action Changed; }
            public sealed class Plain { public event Action Changed; }
            public sealed class External { public extern event Action Changed; }

            public static class Api
            {
                public static Seeded MakeSeeded(Action a) => new Seeded();
                public static Virtual MakeVirtual(Action a) => new Virtual();
                public static Overriding MakeOverriding(Action a) => new Overriding();
                public static Plain MakePlain(Action a) => new Plain();
                public static External MakeExternal(Action a) => new External();
            }
        }
        """;

    private static readonly Lazy<LibraryCompilationResult> Library = new(() =>
    {
        var library = LibraryCompilation.CompileTrees("Fixture.Library",
                                                      [CSharpSyntaxTree.ParseText(LIBRARY, new CSharpParseOptions(LanguageVersion.Preview), "Fixture.Library/Library.cs")],
                                                      EmittedAssemblies.RuntimeReferences, CancellationToken.None);
        Assert.True(library.Reason is null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        return library;
    });

    [Theory]
    [InlineData("M:Lib.Store.get_Value", "var r = Recv_Call.Value;")]
    [InlineData("M:Lib.Store.set_Value(System.Object)", "Recv_Call.Value = Arg_value_Call;")]
    [InlineData("M:Lib.Store.get_Item(System.Int32)", "var r = Recv_Call[Arg_i_Call];")]
    [InlineData("M:Lib.Store.set_Item(System.Int32,System.Object)", "Recv_Call[Arg_i_Call] = Arg_value_Call;")]
    [InlineData("M:Lib.Store.set_Shared(System.Object)", "global::Lib.Store.Shared = Arg_value_Call;")]
    [InlineData("M:Lib.Store.add_Changed(System.Action)", "Recv_Call.Changed += L_value_0_Call();")]
    [InlineData("M:Lib.Store.remove_Changed(System.Action)", "Recv_Call.Changed -= L_value_0_Call();")]
    [InlineData("M:Lib.Store.add_StaticChanged(System.Action)", "global::Lib.Store.StaticChanged += L_value_0_Call();")]
    public void An_accessor_driver_compiles_binds_and_calls_the_accessor(string id, string call)
    {
        var synthesis = Synthesize(id);

        Assert.True(synthesis.Driver is not null, $"{synthesis.Reason}: {synthesis.Detail}");
        Assert.Equal(id, synthesis.Driver.Member.GetDocumentationCommentId());
        Assert.Contains(call, synthesis.Driver.Source);
    }

    [Fact]
    public void A_getter_keeps_its_result_as_a_method_does()
    {
        var driver = Assert.IsType<Driver>(Synthesize("M:Lib.Store.get_Value").Driver);

        Assert.Contains("Keep.R = r;", driver.Source);
    }

    [Fact]
    public void An_init_setter_is_driver_not_synthesized_as_an_init_accessor()
    {
        var synthesis = Synthesize("M:Lib.Store.set_Init(System.Object)");

        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, synthesis.Reason);
        Assert.Contains("is an init accessor", synthesis.Detail);
    }

    [Fact]
    public void A_non_public_setter_of_a_public_property_is_driver_not_synthesized()
    {
        var synthesis = Synthesize("M:Lib.Store.set_Guarded(System.Object)");

        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, synthesis.Reason);
        Assert.Contains("is not public", synthesis.Detail);
    }

    [Fact]
    public void A_private_setter_the_opening_made_public_is_driver_not_synthesized_and_no_trigger()
    {
        const string SETTER = "M:Lib.Seeded.set_Hidden(System.Object)";
        // The opening stripped the setter's modifier for seeding: in the copy it is public.
        Assert.Contains("P:Lib.Seeded.Hidden", Library.Value.OpenedFields);
        Assert.Equal(Accessibility.Public, Opened(SETTER).DeclaredAccessibility);

        var synthesis = Synthesize(SETTER);
        var holder = Assert.IsType<Driver>(Synthesize("M:Lib.Api.MakeSeeded(System.Action)").Driver);

        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, synthesis.Reason);
        Assert.Contains("is not public", synthesis.Detail);
        Assert.Contains(holder.Triggers, trigger => trigger.Member == "M:Lib.Seeded.get_Hidden");
        Assert.DoesNotContain(holder.Triggers, trigger => trigger.Member == SETTER);
        Assert.DoesNotContain(holder.Uncovered[FateClassifier.RESULT], why => why.StartsWith(SETTER, StringComparison.Ordinal));
    }

    [Fact]
    public void A_public_accessor_of_a_private_nested_type_the_opening_made_internal_is_driver_not_synthesized_and_no_trigger()
    {
        const string SETTER = "M:Lib.Outer.Inner.set_Value(System.Object)";
        var setter = Opened(SETTER);
        // The opening made the nested type internal; the accessor itself was public all along.
        Assert.Equal(Accessibility.Internal, setter.ContainingType.DeclaredAccessibility);
        Assert.Equal(Accessibility.Public, setter.DeclaredAccessibility);

        var synthesis = Synthesize(SETTER);

        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, synthesis.Reason);
        Assert.Contains("is not public", synthesis.Detail);
        // A trigger's member, and the type that declares it, must have been public before the opening.
        Assert.False(DriverSynthesizer.OriginalAccessibility(Library.Value.Compilation!, setter));
        Assert.False(DriverSynthesizer.OriginalAccessibility(Library.Value.Compilation!, setter.ContainingType));
    }

    [Fact]
    public void A_seed_auto_property_init_accessor_the_opening_turned_into_set_is_still_an_init_accessor()
    {
        const string SETTER = "M:Lib.Seeded.set_Frozen(System.Object)";
        Assert.False(((IMethodSymbol)Opened(SETTER)).IsInitOnly);

        var synthesis = Synthesize(SETTER);
        var holder = Assert.IsType<Driver>(Synthesize("M:Lib.Api.MakeSeeded(System.Action)").Driver);

        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, synthesis.Reason);
        Assert.Contains("is an init accessor", synthesis.Detail);
        Assert.DoesNotContain(holder.Triggers, trigger => trigger.Member == SETTER);
    }

    [Fact]
    public void An_instance_getter_with_no_parameter_is_a_candidate_through_its_receiver()
    {
        var synthesis = Synthesize("M:Lib.Store.get_Count");

        Assert.True(synthesis.Driver is not null, $"{synthesis.Reason}: {synthesis.Detail}");
        Assert.Contains("var r = Recv_Call.Count;", synthesis.Driver.Source);
    }

    [Fact]
    public void A_static_getter_of_an_int_property_is_not_a_candidate()
    {
        Assert.Equal(GenerationReasons.NOT_A_CANDIDATE, Synthesize("M:Lib.Store.get_Total").Reason);
    }

    [Fact]
    public void A_virtual_field_like_events_accessors_are_triggers()
    {
        var driver = Assert.IsType<Driver>(Synthesize("M:Lib.Api.MakeVirtual(System.Action)").Driver);

        AssertEventTriggers(driver, "Virtual");
    }

    [Fact]
    public void An_override_field_like_events_accessors_are_triggers()
    {
        var driver = Assert.IsType<Driver>(Synthesize("M:Lib.Api.MakeOverriding(System.Action)").Driver);

        AssertEventTriggers(driver, "Overriding");
    }

    [Fact]
    public void An_extern_events_accessors_are_triggers()
    {
        // An extern event is not field-like: its accessors are not the compiler's, so they are members to observe.
        var driver = Assert.IsType<Driver>(Synthesize("M:Lib.Api.MakeExternal(System.Action)").Driver);

        AssertEventTriggers(driver, "External");
    }

    [Fact]
    public void A_non_virtual_field_like_events_accessors_are_not_triggers()
    {
        var driver = Assert.IsType<Driver>(Synthesize("M:Lib.Api.MakePlain(System.Action)").Driver);

        Assert.NotEmpty(driver.Triggers);
        Assert.DoesNotContain(driver.Triggers, trigger => trigger.Member.Contains("_Changed(", StringComparison.Ordinal));
        Assert.True(driver.Covers(FateClassifier.RESULT), string.Join("; ", driver.Uncovered[FateClassifier.RESULT]));
    }

    /// <summary>Asserts a holder's event accessors are triggers that subscribe and unsubscribe.</summary>
    /// <param name="driver">The driver whose result holds the event.</param>
    /// <param name="type">The holder type's name in <c>Lib</c>.</param>
    private static void AssertEventTriggers(Driver driver, string type)
    {
        var add = Assert.Single(driver.Triggers, trigger => trigger.Member == $"M:Lib.{type}.add_Changed(System.Action)");
        var remove = Assert.Single(driver.Triggers, trigger => trigger.Member == $"M:Lib.{type}.remove_Changed(System.Action)");
        Assert.Equal(FateClassifier.RESULT, add.Holder);
        Assert.Equal(FateClassifier.RESULT, remove.Holder);
        Assert.Contains("r.Changed += TArg_", driver.Source);
        Assert.Contains("r.Changed -= TArg_", driver.Source);
    }

    /// <summary>The library member with a declaration id, in the opened copy.</summary>
    /// <param name="id">The declaration id.</param>
    private static ISymbol Opened(string id) =>
        Assert.IsAssignableFrom<ISymbol>(DriverSynthesizer.FindMember(Library.Value.Compilation!, id));

    /// <summary>The driver of a library member, or why there is none.</summary>
    /// <param name="id">The member's declaration id.</param>
    private static DriverSynthesis Synthesize(string id)
    {
        var library = Library.Value;
        return DriverSynthesizer.Synthesize(library.Compilation!, Opened(id), library.ExternMembers, library.OpenedFields, CancellationToken.None);
    }
}
