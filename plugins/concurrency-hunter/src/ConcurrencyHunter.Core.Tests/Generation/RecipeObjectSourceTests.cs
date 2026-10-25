using System.Text;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

public sealed class RecipeObjectSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_object_from_other_setup_or_another_recipe_is_not_a_candidates_own_source(bool otherRecipe)
    {
        var source = $$"""
            using System;
            namespace Lib {
            public sealed class Box {
                private Box() { } public Action A;
                internal static Box Fresh() => new Box();
                public static Box Make() => Api.State;
            }
            public sealed class Input { public Input() { Api.State = Box.Fresh(); } }
            public static class Api {
                internal static Box State = Box.Fresh();
                {{(otherRecipe ? "private static Input Other = new Input();" : "")}}
                public static void Run({{(otherRecipe ? "" : "Input input, ")}}Action a) {
                    {{(otherRecipe ? "_ = Other;" : "")}} State.A = a;
                }
            } }
            """;
        var member = "M:Lib.Api.Run(" + (otherRecipe ? "" : "Lib.Input,") + "System.Action)";
        var trace = ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", member, null, null), Compile(source),
            CancellationToken.None, initializers: TypeInitializerTreatment.Off);
        Assert.Null(trace.Answer.Model);
        Assert.Contains(trace.Answer.Causes, cause => cause.Code == ModelCauses.UNSEEDED_READ);
        Assert.Contains(trace.Driver!.UnseededRecipeStatics, storage => storage.Name == "State");
        Assert.DoesNotContain("global::Lib.Box.Make()", trace.Driver.Source);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void A_seed_is_not_the_object_source_of_a_recipe(bool automatic, bool nested, bool confirmation)
    {
        var source = $$"""
            using System;
            namespace Lib {
            public interface IBox { void Store(Action a); }
            public sealed class Outer { public IBox Value; public Outer(IBox value) { Value = value; } }
            public static class Api {
                private sealed class Hidden : IBox { private Action _a; public void Store(Action a) { _a = a; } }
                private static IBox State = new Hidden();
                private static Outer Nested = new Outer(State);
                public static IBox Make() => State;
                {{(automatic ? "static Api() { Huge.All(); }" : "")}}
                public static void Run(Action a) => {{(nested ? "Nested.Value" : "State")}}.Store(a);
            }
            public sealed class Parser {
                private Action _a; public Parser(Action a) { _a = a; }
                public void Fire() { Api.Run(_a); _a(); }
            }
            {{(automatic ? Huge() : "")}}
            }
            """;
        var member = confirmation ? "M:Lib.Parser.#ctor(System.Action)" : "M:Lib.Api.Run(System.Action)";
        var request = new GenerationRequest(ASSEMBLY, "1.0", member, null, null);
        var library = Compile(source);
        var trace = ModelGenerator.Trace(request, library, CancellationToken.None,
            initializers: automatic ? null : TypeInitializerTreatment.Off);
        if (nested)
        {
            // With no inner value the interface target is unresolved too; that refusal has precedence over incomplete.
            Assert.Equal(ModelReasons.UNKNOWN_TOUCH, trace.Answer.ModelReason);
            Assert.Contains(trace.Answer.Causes, cause => cause.Code == ModelCauses.UNSEEDED_READ);
            Assert.Null(trace.Answer.Model);
        }
        else
            AssertUnseeded(trace);
        Assert.DoesNotContain("global::Lib.Api.Make()", trace.Driver!.Source);
        if (automatic)
            Assert.True(trace.Answer.Generation.Attempts[0].ReachedBodies > 1500);
        if (!automatic && !confirmation)
            foreach (var mode in new[] { TypeInitializerTreatment.Run, TypeInitializerTreatment.Inert })
            {
                var twin = ModelGenerator.Trace(request, library, CancellationToken.None, initializers: mode);
                Assert.Equal(ModelReasons.LIBRARY_STATE, twin.Answer.ModelReason);
                Assert.Null(twin.Answer.Model);
            }
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void Another_assembly_shared_static_is_a_recipe_source_through_the_final_answer(bool automatic, bool nested, bool confirmation,
                                                                                        bool sharedField)
    {
        var valueType = sharedField ? "System.Reflection.Missing" : "StringComparer";
        var shared = sharedField ? valueType + ".Value" : "StringComparer.Ordinal";
        var read = (nested ? "X.Value" : "X") + (sharedField ? "" : ".Equals(null, null)");
        var source = $$"""
            using System;
            namespace Lib {
            public sealed class Outer { public {{valueType}} Value; public Outer({{valueType}} value) { Value = value; } }
            public static class Api {
                private static {{(nested ? "Outer X = new Outer(" + shared + ")" : valueType + " X = " + shared)}};
                {{(automatic ? "static Api() { Huge.All(); }" : "")}}
                public static void Run(Action a) { a(); _ = {{read}}; }
            }
            public sealed class Parser {
                private Action _a; public Parser(Action a) { _a = a; }
                public void Fire() { Api.Run(_a); }
            }
            {{(automatic ? Huge() : "")}}
            }
            """;
        var member = confirmation ? "M:Lib.Parser.#ctor(System.Action)" : "M:Lib.Api.Run(System.Action)";
        var request = new GenerationRequest(ASSEMBLY, "1.0", member, null, null);
        var library = Compile(source);
        var trace = ModelGenerator.Trace(request, library, CancellationToken.None,
            initializers: automatic ? null : TypeInitializerTreatment.Off);
        Assert.Equal("off", trace.Answer.Generation.Mode);
        Assert.Null(trace.Answer.Reason);
        Assert.Null(trace.Answer.ModelReason);
        Assert.Empty(trace.Answer.Causes);
        Assert.NotNull(trace.Answer.Model);
        Assert.Contains(sharedField ? "global::System.Reflection.Missing.Value" : "global::System.StringComparer.InvariantCulture", trace.Driver!.Source);
        Assert.Empty(trace.Driver.UnseededRecipeStatics);
        if (automatic)
            Assert.True(trace.Answer.Generation.Attempts[0].ReachedBodies > 1500);
        else
            foreach (var mode in new[] { TypeInitializerTreatment.Run, TypeInitializerTreatment.Inert })
            {
                var twin = ModelGenerator.Trace(request, library, CancellationToken.None, initializers: mode);
                Assert.Equal(twin.Answer.Classified!.OrderBy(pair => pair.Key), trace.Answer.Classified!.OrderBy(pair => pair.Key));
                Assert.Equal(twin.Answer.ModelReason, trace.Answer.ModelReason);
                Assert.Equal(ModelEntryWriter.Write(twin.Answer.Model!).ToJsonString(), ModelEntryWriter.Write(trace.Answer.Model!).ToJsonString());
            }
    }

    [Theory]
    [InlineData("new Box()", false)]
    [InlineData("State", true)]
    [InlineData("null", true)]
    [InlineData("choose ? new Box() : State", false)]
    public void A_factory_recipe_needs_an_object_source(string result, bool unseeded)
    {
        var trace = Generate(result);
        Assert.Equal(unseeded, trace.Answer.Causes.Any(cause => cause.Code == ModelCauses.UNSEEDED_READ));
        Assert.Equal(unseeded, trace.Driver!.UnseededRecipeStatics.Any(storage => storage.Name == "s_box"));
        if (unseeded)
            AssertUnseeded(trace);
        else
        {
            Assert.Equal(ModelReasons.LIBRARY_STATE, trace.Answer.ModelReason);
            Assert.DoesNotContain(trace.Answer.Causes, cause => cause.Code == ModelCauses.UNSEEDED_READ);
        }
        Assert.Null(trace.Answer.Model);
    }

    [Fact]
    public void A_factory_static_cycle_is_unseeded_after_the_automatic_switch()
    {
        var trace = Generate("State", automatic: true);
        Assert.True(trace.Answer.Generation.Attempts[0].ReachedBodies > 1500);
        Assert.True(trace.Answer.Generation.Attempts[1].ReachedBodies < 1500);
        AssertUnseeded(trace);
    }

    [Fact]
    public void A_nested_factory_static_cycle_leaves_the_outer_recipe_unseeded()
    {
        var trace = Generate("State", nested: true);
        AssertUnseeded(trace);
        Assert.Contains(trace.Driver!.UnseededRecipeStatics, storage => storage.Name == "s_outer");
    }

    [Fact]
    public void A_factory_static_cycle_discovered_by_confirmation_is_unseeded()
    {
        var trace = Generate("State", confirmation: true);
        Assert.Equal(3, trace.ConfirmationPasses);
        AssertUnseeded(trace);
        Assert.Contains(trace.Driver!.UnseededRecipeStatics, storage => storage.Name == "s_box");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_factory_without_an_object_is_rolled_back_before_the_next_source(bool internalConstructor, bool nested)
    {
        var source = $$"""
            using System;
            namespace Lib {
            public sealed class Marker { public Marker() { } }
            public sealed class Box {
                private Box() { }
                {{(internalConstructor ? "internal Box(int number) { }" : "")}}
                private static readonly Box State = new Box();
                public Action A;
                public static Box Empty(Marker marker) => State;
                {{(internalConstructor ? "" : "public static Box Fresh(int number) => new Box();")}}
            }
            public sealed class Outer { public Box Inner; public Outer(Box inner) { Inner = inner; } }
            public static class Api {
                private static readonly {{(nested ? "Outer" : "Box")}} Value;
                public static void Run(Action a) { {{(nested ? "Value.Inner" : "Value")}}.A = a; }
            } }
            """;
        var trace = ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", "M:Lib.Api.Run(System.Action)", null, null), Compile(source),
            CancellationToken.None, initializers: TypeInitializerTreatment.Off);
        Assert.Equal(ModelReasons.LIBRARY_STATE, trace.Answer.ModelReason);
        Assert.DoesNotContain(trace.Answer.Causes, cause => cause.Code == ModelCauses.UNSEEDED_READ);
        Assert.Null(trace.Answer.Model);
        Assert.DoesNotContain("global::Lib.Marker", trace.Driver!.Source);
        Assert.DoesNotContain("global::Lib.Box.Empty(", trace.Driver.Source);
        Assert.Contains(internalConstructor ? "new global::Lib.Box(default(global::System.Int32))" : "global::Lib.Box.Fresh(default(global::System.Int32))", trace.Driver.Source);
        Assert.Empty(trace.Driver.UnseededRecipeStatics);
    }

    [Fact]
    public void Confirmation_recipe_checks_do_not_read_unseeded_seed_paths_of_other_objects()
    {
        const string SOURCE = """
            using System;
            using System.Collections.Generic;
            namespace Lib {
            public sealed class Registry {
                private Registry(Dictionary<string, Descriptor> items) { Items = items; }
                internal readonly Dictionary<string, Descriptor> Items;
                public static Registry Empty { get; } = new Registry(new Dictionary<string, Descriptor>());
            }
            public sealed class Descriptor { public Parser Parser { get; } }
            public sealed class Parser {
                internal object Extensions { get; }
                private Action _a;
                public Parser(Action a) { _a = a; }
                public void Configure(Registry registry) { }
                public void Fire() { _ = Extensions; _a(); }
            } }
            """;
        var trace = ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", "M:Lib.Parser.#ctor(System.Action)", null, null), Compile(SOURCE),
            CancellationToken.None, initializers: TypeInitializerTreatment.Off);
        Assert.Contains(trace.Driver!.UnseededMembers, member => member.Name == "Extensions");
        Assert.Contains(trace.Driver.UnseededRecipeStatics, storage => storage.Name == "Empty");
        Assert.Equal(new ClassifiedFate(FateClassifier.HOLDER, FateClassifier.RESULT), FateOf(trace, "a"));
        Assert.DoesNotContain(trace.Answer.Causes, cause => cause.Code == ModelCauses.UNSEEDED_READ);
        Assert.NotNull(trace.Answer.Model);
    }

    private static void AssertUnseeded(GenerationTrace trace)
    {
        Assert.Equal("off", trace.Answer.Generation.Mode);
        Assert.Null(trace.Answer.Reason);
        Assert.Equal(ModelReasons.INCOMPLETE, trace.Answer.ModelReason);
        Assert.Contains(trace.Answer.Causes, cause => cause.Code == ModelCauses.UNSEEDED_READ);
        Assert.Null(trace.Answer.Model);
    }

    private static GenerationTrace Generate(string result, bool automatic = false, bool nested = false, bool confirmation = false)
    {
        var source = $$"""
            using System;
            namespace Lib {
            public sealed class Box {
                private Box() { }
                private static readonly Box State = new Box();
                public Action A;
                public static Box Make(bool choose) => {{result}};
            }
            public sealed class Outer { public Box Inner; public Outer(Box inner) { Inner = inner; } }
            public static class Api {
                private static readonly Box s_box = Box.Make(false);
                private static readonly Outer s_outer = new Outer(Box.Make(false));
                {{(automatic ? "static Api() { Huge.All(); }" : "")}}
                public static void Run(Action a) { {{(nested ? "s_outer.Inner" : "s_box")}}.A = a; }
            }
            public sealed class Parser {
                private Action _a;
                public Parser(Action a) { _a = a; }
                public void Fire() { Api.Run(_a); _a(); }
            }
            {{(automatic ? Huge() : "")}}
            }
            """;
        var member = confirmation ? "M:Lib.Parser.#ctor(System.Action)" : "M:Lib.Api.Run(System.Action)";
        return ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", member, null, null), Compile(source),
            CancellationToken.None, initializers: automatic ? null : TypeInitializerTreatment.Off);
    }

    private static string Huge()
    {
        var source = new StringBuilder("public static class Huge { public static void All() {");
        for (var i = 0; i < 1501; i++)
            source.Append($" M{i}();");
        source.Append(" }");
        for (var i = 0; i < 1501; i++)
            source.Append($" private static void M{i}() {{ }}");
        return source.Append(" }").ToString();
    }
}
