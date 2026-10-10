using System.Reflection;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The cases an answer names against a model entry (SPEC TD-034b, G-6): every case found, one per code, the model reason the
/// first in check order among them, and none for a member with a model.</summary>
public sealed class GenerationCauseTests
{
    private static readonly IReadOnlySet<string> CODES = typeof(ModelCauses).GetFields(BindingFlags.Public | BindingFlags.Static)
                                                                            .Select(field => (string)field.GetValue(null)!)
                                                                            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void A_probe_handed_to_an_extern_method_names_the_call()
    {
        var answer = Answer("public abstract class Node { } public static class Api { public static void Send(Node node, Action done) { Sink.Take(node); done(); } }",
                            "M:Lib.Api.Send(Lib.Node,System.Action)");

        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Assert.Contains("Lib.Sink.Take", Cause(answer, ModelCauses.PROBE_HANDED_TO_UNSEEN).Detail);
    }

    [Theory]
    [InlineData("public static string Describe(Node node, Action done) { done(); return $\"node {node}\"; }", "M:Lib.Api.Describe(Lib.Node,System.Action)",
                "Interpolation")]
    [InlineData("public static object Pair(Node node, Action done) { done(); return (node, 1); }", "M:Lib.Api.Pair(Lib.Node,System.Action)", "Tuple")]
    public void A_probe_given_to_an_operation_the_lowering_does_not_express_names_the_operation(string member, string id, string kind)
    {
        // An interpolation runs node.ToString(), user code on the probe; a tuple keeps the probe where the heap does not see it.
        var answer = Answer($"public abstract class Node {{ }} public static class Api {{ {member} }}", id);

        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Assert.Contains(kind, Cause(answer, ModelCauses.PROBE_IN_UNSUPPORTED_OPERATION).Detail);
        Assert.DoesNotContain(answer.Causes, cause => cause.Code == ModelCauses.PROBE_HANDED_TO_UNSEEN);
    }

    [Fact]
    public void A_probe_handed_to_an_extern_method_and_an_unsupported_operation_names_only_the_call()
    {
        // The lowering of the tuple would not lift the refusal: the extern call still sees the probe.
        var answer = Answer("public abstract class Node { } public static class Api { public static object Pair(Node node, Action done) { Sink.Take(node); return (node, 1); } }",
                            "M:Lib.Api.Pair(Lib.Node,System.Action)");

        Cause(answer, ModelCauses.PROBE_HANDED_TO_UNSEEN);
        Assert.DoesNotContain(answer.Causes, cause => cause.Code == ModelCauses.PROBE_IN_UNSUPPORTED_OPERATION);
    }

    [Fact]
    public void An_operation_the_lowering_does_not_express_that_no_probe_reaches_leaves_the_model()
    {
        var answer = Answer("public static class Api { public static string Describe(int count, Action done) { done(); return $\"count {count}\"; } }",
                            "M:Lib.Api.Describe(System.Int32,System.Action)");

        Assert.NotNull(answer.Model);
        Assert.Empty(answer.Causes);
    }

    [Fact]
    public void A_load_of_an_unseeded_field_names_the_field()
    {
        var answer = Answer("public struct Part { public object Value; } public sealed class Options { public Part Part; } " +
                            "public static class Api { public static void Read(Options options, Action done) { _ = options.Part.Value; done(); } }",
                            "M:Lib.Api.Read(Lib.Options,System.Action)");

        Assert.Equal(ModelReasons.INCOMPLETE, answer.ModelReason);
        Assert.Contains("Lib.Part.Value", Cause(answer, ModelCauses.UNSEEDED_READ).Detail);
    }

    [Fact]
    public void A_store_into_a_library_static_names_the_store()
    {
        var answer = Answer("public static class Api { public static void Run(Action done) { Cache.Last = new object(); done(); } }",
                            "M:Lib.Api.Run(System.Action)");

        Assert.Equal(ModelReasons.LIBRARY_STATE, answer.ModelReason);
        Assert.Contains("Lib.Cache", Cause(answer, ModelCauses.LIBRARY_STATE_STORE).Detail);
    }

    [Fact]
    public void A_read_of_what_a_user_delegate_returned_names_the_field()
    {
        var answer = Answer("public sealed class Item { public object Value; } " +
                            "public static class Api { public static object Run(Func<Item> make) => make().Value; }",
                            "M:Lib.Api.Run(System.Func{Lib.Item})");

        Assert.Equal(ModelReasons.VOCABULARY, answer.ModelReason);
        Assert.Contains("Lib.Item.Value", Cause(answer, ModelCauses.CALLBACK_VALUE_READ).Detail);
    }

    [Fact]
    public void A_member_with_a_model_names_no_cause()
    {
        var answer = Answer("public static class Api { public static void Run(Action done) { done(); } }", "M:Lib.Api.Run(System.Action)");

        Assert.NotNull(answer.Model);
        Assert.Empty(answer.Causes);
    }

    [Fact]
    public void A_case_of_a_later_reason_is_named_beside_the_first()
    {
        // The probe is handed to code the analysis cannot follow, and a field of what the delegate returned is read too: unknown-touch
        // comes first, and the vocabulary case stays named.
        var answer = Answer("public abstract class Node { } public sealed class Item { public object Value; } public static class Api { " +
                            "public static object Send(Node node, Func<Item> make) { Sink.Take(node); return make().Value; } }",
                            "M:Lib.Api.Send(Lib.Node,System.Func{Lib.Item})");

        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Cause(answer, ModelCauses.PROBE_HANDED_TO_UNSEEN);
        Cause(answer, ModelCauses.CALLBACK_VALUE_READ);
    }

    [Fact]
    public void Library_state_is_not_looked_for_once_unknown_touch_is_found()
    {
        // Its check walks the keeping paths of every state store, which the generation spends only while the reason is still open.
        var answer = Answer("public abstract class Node { } public static class Api { " +
                            "public static void Send(Node node, Action done) { Sink.Take(node); Cache.Last = new object(); done(); } }",
                            "M:Lib.Api.Send(Lib.Node,System.Action)");

        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Assert.DoesNotContain(answer.Causes, cause => cause.Code == ModelCauses.LIBRARY_STATE_STORE);
    }

    [Fact]
    public void A_case_met_again_formats_its_evidence_once()
    {
        var causes = new GenerationCauses();
        var formatted = 0;
        string Evidence() => $"evidence {++formatted}";

        causes.Add(ModelReasons.VOCABULARY, ModelCauses.VALUE_UNNAMED, $"{Evidence()}");
        causes.Add(ModelReasons.VOCABULARY, ModelCauses.VALUE_UNNAMED, $"{Evidence()}");

        Assert.Equal(1, formatted);
        Assert.Equal("value-unnamed: evidence 1", Assert.Single(causes.All).ToString());
    }

    /// <summary>The answer for one member of a fixture library, checked against what every answer with causes holds: each code is
    /// one of <see cref="ModelCauses"/>, the causes are sorted by code and one per code, and the model reason is the first reason in
    /// check order among them.</summary>
    /// <param name="types">Declarations inside namespace <c>Lib</c>.</param>
    /// <param name="memberId">The member's declaration id.</param>
    private static GeneratedAnswer Answer(string types, string memberId)
    {
        var answer = GenerationRuns.Trace(types, memberId).Answer;
        Assert.All(answer.Causes, cause => Assert.Contains(cause.Code, CODES));
        Assert.Equal(answer.Causes.Select(cause => cause.Code).Order(StringComparer.Ordinal).Distinct(), answer.Causes.Select(cause => cause.Code));
        Assert.Equal(ModelReasons.Ordered.FirstOrDefault(reason => answer.Causes.Any(cause => cause.Reason == reason)), answer.ModelReason);
        return answer;
    }

    /// <summary>The one cause of a code an answer names.</summary>
    /// <param name="answer">The answer.</param>
    /// <param name="code">The code.</param>
    private static GenerationCause Cause(GeneratedAnswer answer, string code) =>
        Assert.Single(answer.Causes, cause => cause.Code == code);
}
