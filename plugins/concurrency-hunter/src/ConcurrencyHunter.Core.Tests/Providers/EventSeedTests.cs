using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>Seeds for field-like event storage (R6): the driver subscribes a witness to a field-like event as it seeds a field,
/// with TD-034b's depths and static selection.</summary>
public sealed class EventSeedTests
{
    [Fact]
    public void An_instance_field_like_event_of_the_receiver_is_seeded_by_a_subscription_in_V_Setup_and_reached()
    {
        const string PATH = "this/E:Lib.Publisher.Changed";
        var (_, trace) = Run("public sealed class Publisher { public event Action Changed; public void Raise(object p) { Changed?.Invoke(); } }",
                             "M:Lib.Publisher.Raise(System.Object)");

        // One receiver per variant, each seeded.
        var seeds = trace.Driver!.SeedStatements.Where(seed => seed.Path == PATH).ToArray();
        Assert.NotEmpty(seeds);
        Assert.All(seeds, seed => Assert.True(seed.Operation is IEventAssignmentOperation { Adds: true }, seed.Operation.Syntax.ToString()));
        Assert.Contains("Recv_Call).Changed += Seed_", Setup(trace.Driver));
        Assert.DoesNotContain(PATH, trace.Driver.Unseeded);
        Assert.DoesNotContain(PATH, Effects(trace).UnreachedSeeds);
    }

    [Fact]
    public void A_private_field_like_event_is_opened_and_seeded_and_its_accessors_are_no_triggers()
    {
        var (library, trace) = Run("public sealed class Quiet { private event Action Changed; public void Raise(object p) { Changed?.Invoke(); } }",
                                   "M:Lib.Quiet.Raise(System.Object)");

        Assert.Contains("E:Lib.Quiet.Changed", library.OpenedFields);
        Assert.Contains(trace.Driver!.SeedStatements, seed => seed.Path == "this/E:Lib.Quiet.Changed");
        Assert.DoesNotContain(trace.Driver.Triggers, trigger => trigger.Member.Contains("_Changed(", StringComparison.Ordinal));
        Assert.DoesNotContain(trace.Driver.Uncovered[FateClassifier.THIS], why => why.Contains("_Changed(", StringComparison.Ordinal));
        Assert.DoesNotContain("this/E:Lib.Quiet.Changed", Effects(trace).UnreachedSeeds);
    }

    [Fact]
    public void A_custom_event_gets_no_subscription_seed_while_its_backing_field_is_seeded_as_a_field()
    {
        var (_, trace) = Run("public sealed class Custom { private Action _handlers; " +
                             "public event Action Changed { add { _handlers += value; } remove { _handlers -= value; } } " +
                             "public void Raise(object p) { _handlers?.Invoke(); } }",
                             "M:Lib.Custom.Raise(System.Object)");

        var seeds = trace.Driver!.SeedStatements.Where(seed => seed.Path == "this/F:Lib.Custom._handlers").ToArray();
        Assert.NotEmpty(seeds);
        Assert.All(seeds, seed => Assert.IsAssignableFrom<ISimpleAssignmentOperation>(seed.Operation));
        Assert.DoesNotContain(trace.Driver.SeedStatements, statement => statement.Path.Contains("/E:", StringComparison.Ordinal));
        Assert.DoesNotContain(".Changed += ", Setup(trace.Driver));
    }

    [Fact]
    public void A_static_field_like_event_a_reached_body_raises_is_seeded()
    {
        const string PATH = "static/E:Lib.Bus.Raised";
        var (_, trace) = Run("public static class Bus { public static event Action Raised; public static void Fire(object p) { Raised?.Invoke(); } }",
                             "M:Lib.Bus.Fire(System.Object)");

        var seed = Assert.Single(trace.Driver!.SeedStatements, seed => seed.Path == PATH);
        Assert.IsAssignableFrom<IEventAssignmentOperation>(seed.Operation);
        Assert.Contains("global::Lib.Bus.Raised += Seed_", Setup(trace.Driver));
        Assert.DoesNotContain(PATH, Effects(trace).UnreachedSeeds);
    }

    [Fact]
    public void A_static_field_like_event_no_reached_body_loads_is_neither_seeded_nor_listed_unseeded()
    {
        var (_, trace) = Run("public static class Bus { public static event Action Raised; public static event Action Idle; " +
                             "public static void Fire(object p) { Raised?.Invoke(); } }",
                             "M:Lib.Bus.Fire(System.Object)");

        Assert.Contains(trace.Driver!.SeedStatements, seed => seed.Path == "static/E:Lib.Bus.Raised");
        Assert.DoesNotContain(trace.Driver.SeedStatements, seed => seed.Path == "static/E:Lib.Bus.Idle");
        Assert.DoesNotContain("static/E:Lib.Bus.Idle", trace.Driver.Unseeded);
    }

    [Fact]
    public void A_static_custom_events_backing_field_a_reached_body_loads_is_seeded_as_a_field()
    {
        var (_, trace) = Run("public static class CustomBus { private static Action _handlers; " +
                             "public static event Action Raised { add { _handlers += value; } remove { _handlers -= value; } } " +
                             "public static void Fire(object p) { _handlers?.Invoke(); } }",
                             "M:Lib.CustomBus.Fire(System.Object)");

        var seed = Assert.Single(trace.Driver!.SeedStatements, seed => seed.Path == "static/F:Lib.CustomBus._handlers");
        Assert.IsAssignableFrom<ISimpleAssignmentOperation>(seed.Operation);
        Assert.DoesNotContain(trace.Driver.SeedStatements, statement => statement.Path.Contains("/E:", StringComparison.Ordinal));
    }

    [Fact]
    public void An_event_seed_on_an_object_setup_left_null_is_unseeded_and_its_load_makes_the_answer_incomplete()
    {
        const string PATH = "arg:options/F:Lib.Options.Child/E:Lib.Child.Changed";
        var (_, trace) = Run("public sealed class Child { public event Action Changed; public void Raise() { Changed?.Invoke(); } } " +
                             "public sealed class Options { public Child Child; } " +
                             "public static class Api { public static void Read(Options options, Action done) { options.Child ??= new Child(); options.Child.Raise(); done(); } }",
                             "M:Lib.Api.Read(Lib.Options,System.Action)");

        // The subscription is written; it reaches no object setup made, so its path is unseeded.
        Assert.Contains(trace.Driver!.SeedStatements, seed => seed.Path == PATH);
        var effects = Effects(trace);
        Assert.Contains(PATH, effects.UnreachedSeeds);
        Assert.Equal(GenerationReasons.INCOMPLETE, effects.Reason);
    }

    [Fact]
    public void A_struct_receivers_instance_field_like_event_is_unseeded_and_a_member_raising_it_answers_incomplete()
    {
        var (_, trace) = Run("public struct Signal { public event Action Changed; public void Raise(object p) { Changed?.Invoke(); } }",
                             "M:Lib.Signal.Raise(System.Object)");

        Assert.Contains("this/E:Lib.Signal.Changed", trace.Driver!.Unseeded);
        Assert.DoesNotContain(trace.Driver.SeedStatements, seed => seed.Path.Contains("/E:", StringComparison.Ordinal));
        Assert.Equal(GenerationReasons.INCOMPLETE, Effects(trace).Reason);
    }

    [Fact]
    public void A_structs_static_field_like_event_a_reached_body_raises_is_seeded()
    {
        const string PATH = "static/E:Lib.Clock.Ticked";
        var (_, trace) = Run("public struct Clock { public object Value; public static event Action Ticked; public static void Tick(object p) { Ticked?.Invoke(); } }",
                             "M:Lib.Clock.Tick(System.Object)");

        Assert.Contains(trace.Driver!.SeedStatements, seed => seed.Path == PATH);
        Assert.DoesNotContain(PATH, trace.Driver.Unseeded);
        Assert.DoesNotContain(PATH, Effects(trace).UnreachedSeeds);
    }

    [Fact]
    public void A_member_raising_the_receivers_event_with_an_argument_value_reads_it_deep_through_the_seed_witness()
    {
        var (_, trace) = Run("public sealed class Feed { public event Action<object> Received; public void Push(object item) { Received?.Invoke(item); } }",
                             "M:Lib.Feed.Push(System.Object)");

        Assert.Contains(trace.Driver!.SeedStatements, seed => seed.Path == "this/E:Lib.Feed.Received");
        Assert.True(Effects(trace).HasEffect("item", EffectReader.READS_DEEP), string.Join(", ", Effects(trace).Effects.Keys));
    }

    [Fact]
    public void An_arguments_field_like_event_is_seeded_below_the_argument()
    {
        var (_, trace) = Run("public sealed class Source { public event Action Changed; public void Raise() { Changed?.Invoke(); } } " +
                             "public static class Api { public static void Use(Source source) { source.Raise(); } }",
                             "M:Lib.Api.Use(Lib.Source)");

        Assert.Contains(trace.Driver!.SeedStatements, seed => seed.Path == "arg:source/E:Lib.Source.Changed");
        Assert.DoesNotContain("arg:source/E:Lib.Source.Changed", Effects(trace).UnreachedSeeds);
        Assert.Empty(trace.Driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>The compiled library and the generator's trace for one member of a library of the prelude and
    /// <paramref name="types"/>; the trace has a driver and a completed run.</summary>
    /// <param name="types">Declarations inside namespace <c>Lib</c>.</param>
    /// <param name="memberId">The member's declaration id.</param>
    private static (LibraryCompilationResult Library, GenerationTrace Trace) Run(string types, string memberId)
    {
        var library = Compile(PRELUDE + types + "\n}\n");
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        var trace = ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", memberId, null, null), library, CancellationToken.None);
        Assert.True(trace.Driver is not null && trace.Run is { Stopped: false }, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        return (library, trace);
    }

    /// <summary>The effects read from a trace's run.</summary>
    /// <param name="trace">The trace, with a driver and a completed run.</param>
    private static EffectReader Effects(GenerationTrace trace) => new(trace.Driver!, trace.Run!);

    /// <summary>The body of a driver's <c>V_Setup</c>.</summary>
    /// <param name="driver">The driver.</param>
    private static string Setup(Driver driver) => driver.Compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
                                                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                                                        .Single(method => method.Identifier.Text == DriverSynthesizer.SETUP).Body!.ToString();
}
