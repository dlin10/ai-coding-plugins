using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
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
                public object this[string i] { get => _value; init => _value = value; }
                public object Guarded { get => _value; internal set => _value = value; }
                public event Action Changed { add { _value = value; } remove { _value = null; } }
                public static event Action StaticChanged { add { } remove { } }
                public int Count => 1;
                public static int Total => 1;
            }

            public sealed class Seeded { public object Hidden { get; private set; } public object Frozen { get; init; } public object Fixed { get; } }
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
    [InlineData("M:Lib.Store.set_Init(System.Object)", "Recv_Call.Init = Arg_value_Call;")]
    [InlineData("M:Lib.Store.set_Item(System.String,System.Object)", "Recv_Call[Arg_i_Call] = Arg_value_Call;")]
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
    public void An_init_setter_is_called_on_the_prepared_receiver()
    {
        var synthesis = Synthesize("M:Lib.Store.set_Init(System.Object)");

        var driver = Assert.IsType<Driver>(synthesis.Driver);
        Assert.Null(synthesis.Reason);
        Assert.Contains("Recv_Call.Init = Arg_value_Call;", driver.Source);
        Assert.All(driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                   diagnostic => Assert.Equal("CS8852", diagnostic.Id));
        var answer = ModelGenerator.Trace(new GenerationRequest("Fixture.Library", "1.0", "M:Lib.Store.set_Init(System.Object)", null, null),
                                          Library.Value, CancellationToken.None).Answer;
        Assert.NotEqual(GenerationReasons.DRIVER_NOT_SYNTHESIZED, answer.Reason);
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
    public void A_setter_only_the_opening_declares_is_member_not_found_and_no_trigger()
    {
        const string SETTER = "M:Lib.Seeded.set_Fixed(System.Object)";
        // The opening gave the get-only seed auto-property a setter for seeding: only the copy declares it.
        Assert.Contains("P:Lib.Seeded.Fixed", Library.Value.OpenedFields);
        Assert.Contains(DocumentationCommentId.GetSymbolsForDeclarationId(SETTER, Library.Value.Compilation!),
                        symbol => SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, Library.Value.Compilation!.Assembly));

        var answer = ModelGenerator.Trace(new GenerationRequest("Fixture.Library", "1.0", SETTER, null, null), Library.Value, CancellationToken.None).Answer;
        var holder = Assert.IsType<Driver>(Synthesize("M:Lib.Api.MakeSeeded(System.Action)").Driver);

        Assert.Equal(GenerationReasons.MEMBER_NOT_FOUND, answer.Reason);
        Assert.Null(DriverSynthesizer.FindMember(Library.Value.Compilation!, SETTER));
        Assert.Contains(holder.Triggers, trigger => trigger.Member == "M:Lib.Seeded.get_Fixed");
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
    public void A_seed_auto_property_init_accessor_the_opening_turned_into_set_is_called_and_is_no_trigger()
    {
        const string SETTER = "M:Lib.Seeded.set_Frozen(System.Object)";
        Assert.False(((IMethodSymbol)Opened(SETTER)).IsInitOnly);

        var synthesis = Synthesize(SETTER);
        var holder = Assert.IsType<Driver>(Synthesize("M:Lib.Api.MakeSeeded(System.Action)").Driver);

        Assert.IsType<Driver>(synthesis.Driver);
        Assert.Null(synthesis.Reason);
        Assert.DoesNotContain(holder.Triggers, trigger => trigger.Member == SETTER);
    }

    [Theory]
    [InlineData("property", "CS8852", true)]
    [InlineData("indexer", "CS8852", true)]
    [InlineData("indexer", "CS0619", false)]
    [InlineData("setup", "CS8852", false)]
    [InlineData("property", "CS0619", false)]
    [InlineData("trigger", "CS8852", true)]
    [InlineData("trigger", "CS0619", true)]
    public void Only_CS8852_at_the_target_assignment_is_tolerated(string place, string diagnostic, bool accepted)
    {
        // Obsolete supplies a compiler error at a generated use without changing the driver's source. Its custom id
        // lets the trigger row exercise CS8852 even though init accessors themselves are never holder triggers.
        var obsolete = diagnostic == "CS8852" ? "[System.Obsolete(\"gone\", true, DiagnosticId = \"CS8852\")]"
                                               : "[System.Obsolete(\"gone\", true)]";
        var attribute = diagnostic == "CS0619" && place is "property" or "indexer" ? obsolete : "";
        var property = place == "indexer" ? $"{attribute} public object this[int i] {{ get => _value; init => _value = value; }}"
                                           : $"{attribute} public object X {{ get => _value; init => _value = value; }}";
        var extra = place == "setup" ? "public Seed Other;" : "";
        var trigger = place == "trigger" ? "void IOld.Fire() { }" : "";
        var contract = place == "trigger" ? $"{obsolete} public interface IOld {{ void Fire(); }}" : "";
        var implements = place == "trigger" ? ": IOld" : "";
        var source = $$"""
            namespace Lib
            {
                public sealed class Seed { public object Frozen { get; init; } }
                {{contract}}
                [System.Obsolete("old")] public sealed class Store {{implements}} { private object _value; {{property}} {{extra}} {{trigger}} }
            }
            """;
        // Keep auto-property init in this copy: opening it for seeding would turn init into set and hide the setup error.
        var library = CSharpCompilation.Create("Fixture.Library",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))], EmittedAssemblies.RuntimeReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(library.GetDiagnostics().Where(error => error.Severity == DiagnosticSeverity.Error));
        var id = place == "indexer" ? "M:Lib.Store.set_Item(System.Int32,System.Object)" : "M:Lib.Store.set_X(System.Object)";
        var member = Assert.IsAssignableFrom<ISymbol>(DriverSynthesizer.FindMember(library, id));

        var synthesis = DriverSynthesizer.Synthesize(library, member, new HashSet<string>(), CancellationToken.None);

        if (!accepted)
        {
            Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, synthesis.Reason);
            Assert.Contains(diagnostic, synthesis.Detail);
            return;
        }

        var driver = Assert.IsType<Driver>(synthesis.Driver);
        Assert.Null(synthesis.Reason);
        var tree = driver.Compilation.SyntaxTrees.Single();
        var model = driver.Compilation.GetSemanticModel(tree);
        var call = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.Text == DriverSynthesizer.CALL);
        var assignment = Assert.Single(model.GetOperation(call)!.Descendants().OfType<ISimpleAssignmentOperation>());
        var invalid = Assert.IsAssignableFrom<IInvalidOperation>(assignment.Target);
        var reference = Assert.IsAssignableFrom<IPropertyReferenceOperation>(Assert.Single(invalid.ChildOperations));
        Assert.True(reference.Property.SetMethod!.IsInitOnly);
        Assert.Equal(id, reference.Property.SetMethod.GetDocumentationCommentId());
        Assert.All(driver.Compilation.GetDiagnostics().Where(error => error.Severity == DiagnosticSeverity.Error),
                   error => Assert.Equal("CS8852", error.Id));
        if (place == "trigger")
        {
            Assert.DoesNotContain(driver.Triggers, candidate => candidate.Member == "M:Lib.IOld.Fire");
            Assert.Contains(driver.Uncovered[FateClassifier.THIS], why => why.StartsWith("M:Lib.IOld.Fire: the driver's call does not compile: ", StringComparison.Ordinal) &&
                                                                         why.Contains(diagnostic, StringComparison.Ordinal));
            Assert.DoesNotContain(".Fire()", driver.Source);
        }
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
