using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Ir;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ProducerCallTests
{
    [Fact]
    public void Receiver_stored_by_a_field_initializer_is_produced_by_its_factory_chain() =>
        Has("_policy.Execute(() => state.Value++);", "private Policy _policy = L.Make().Wrap();", "L.Make", "L.Wrap");

    [Fact]
    public void Extension_receiver_chain_admits_both_factories() =>
        Has("L.Make().Wrap().Execute(() => state.Value++);", "", "L.Make", "L.Wrap");

    [Fact]
    public void Helper_return_is_followed_to_its_factory() =>
        Has("CreatePolicy().Execute(() => state.Value++);", "private Policy CreatePolicy() => L.Make().Wrap();", "L.Make", "L.Wrap");

    [Fact]
    public void Auto_property_initializer_read_through_an_injected_holder_is_followed() =>
        Has("holder.Policy.Execute(() => state.Value++);", "", "L.Make", "L.Wrap",
            extra: "public class Holder { public Policy Policy { get; } = L.Make().Wrap(); }", injectHolder: true);

    [Fact]
    public void Object_creation_admits_its_constructor() =>
        Has("new Policy(L.Make()).Execute(() => state.Value++);", "", "Policy.#ctor(System.Object)", "L.Make");

    [Fact]
    public void Parameter_is_followed_to_its_callers_argument() =>
        Has("Use(L.Make());", "private void Use(Policy policy) { policy.Execute(() => state.Value++); }", "L.Make");

    [Fact]
    public void Captured_variable_is_followed_to_its_store() =>
        Has("var policy = L.Make(); System.Action run = () => policy.Execute(() => state.Value++); run();", "", "L.Make");

    [Fact]
    public void Framework_producer_is_followed_and_not_admitted()
    {
        var program = new CallSiteTestProgram("var values = new System.Collections.Generic.List<Policy>(); values.Add(L.Make()); values[0].Execute(() => state.Value++);");
        var run = program.Run();
        var producers = Producers(run);
        Assert.Contains(producers, call => call.Target.Contains("L.Make", StringComparison.Ordinal));
        using var compilations = new CallSiteCompilations();
        var selector = program.Selector(compilations);
        var framework = CallSiteTestProgram.Calls(run, "List").First(call => call.Target.Contains("get_Item", StringComparison.Ordinal));
        Assert.Contains(framework, producers);
        Assert.Equal("framework", selector.Target(run, framework, [program.Project]).Outcome);
    }

    [Fact]
    public void Dispatching_producer_is_not_admitted()
    {
        var program = new CallSiteTestProgram("L.Make().Virtual().Execute(() => state.Value++);");
        var run = program.Run();
        using var compilations = new CallSiteCompilations();
        var producer = Assert.Single(Producers(run), call => call.Target.Contains("Policy.Virtual", StringComparison.Ordinal));
        Assert.Equal("no-body", program.Selector(compilations).Target(run, producer, [program.Project]).Outcome);
    }

    [Fact]
    public void Eighth_hop_is_followed_and_ninth_is_not()
    {
        var program = new CallSiteTestProgram("L.Make()" + string.Concat(Enumerable.Repeat(".Wrap()", 9)) + ".Execute(() => state.Value++);");
        var producers = Producers(program.Run());
        Assert.Equal(9, producers.Count);
        Assert.DoesNotContain(producers, call => call.Target.Contains("L.Make", StringComparison.Ordinal));
    }

    [Fact]
    public void Factory_shared_by_deciding_calls_of_two_rounds_is_a_producer_of_both()
    {
        var program = new CallSiteTestProgram("_policy.Execute(() => _policy.Execute(() => state.Value++));", "private Policy _policy = L.Make();");
        var first = program.Run();
        var producer = Assert.Single(CallSiteTestProgram.Calls(first, "L.Make"));
        using var compilations = new CallSiteCompilations();
        var rounds = program.Rounds(compilations);
        Assert.True(rounds.Grow(first, CallSiteTestProgram.Pairs(first), 1));
        var second = program.Run(rounds.Admission);
        Assert.True(rounds.Grow(second, CallSiteTestProgram.Pairs(second), 2));
        Assert.Equal(2, rounds.Measurements.Count);
        Assert.All(rounds.Measurements, measurement =>
        {
            Assert.Contains($"{producer.Span.Path}:{producer.Span.StartLine}", measurement.Producers);
            Assert.True(measurement.BodyCount >= 3);
        });
    }

    private static IReadOnlySet<CallIdentity> Producers(ScopeRun run) =>
        ProducerCalls.Of(run.Heap!, Assert.Single(CallSiteTestProgram.Calls(run, "Policy.Execute")), IsProgram);

    private static bool IsProgram(string body) => body.StartsWith("body:Fixture:", StringComparison.Ordinal);

    private static void Has(string work, string members, string first, string? second = null, string extra = "", bool injectHolder = false)
    {
        var found = Producers(new CallSiteTestProgram(work, members, extra, injectHolder: injectHolder).Run());
        Assert.Contains(found, call => call.Target.Contains(first, StringComparison.Ordinal));
        if (second is not null)
            Assert.Contains(found, call => call.Target.Contains(second, StringComparison.Ordinal));
        Assert.DoesNotContain(found, call => call.Target.StartsWith("body:Fixture:", StringComparison.Ordinal));
    }
}
