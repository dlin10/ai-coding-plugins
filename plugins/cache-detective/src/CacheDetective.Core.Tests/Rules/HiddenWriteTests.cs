using CacheDetective.Graph;
using CacheDetective.Rules;
using Xunit;

namespace CacheDetective.Tests.Rules;

/// <summary>A hidden write is a write the handler's own code does not contain: one performed by a
/// procedure it calls, or by a trigger that fires on a table it writes.</summary>
public sealed class HiddenWriteTests
{
    private const string DATABASE = "shop";

    [Fact]
    public void Reports_a_write_made_by_a_called_procedure_against_the_calling_handler()
    {
        var graph = NewGraph(out var products, out var key, out var reader);
        var writer = Handler("Prices.Put");
        var procedure = Procedure("ApplyDiscount");
        graph.AddEdge(new Calls(writer, procedure, Confidence.Confirmed, [Code(44)]));
        graph.AddEdge(new Writes(procedure, products, Confidence.Confirmed, [InDatabase("ApplyDiscount")],
            [WriteEvent.Update]));

        var finding = Assert.Single(new UnguardedWriteRule().Evaluate(graph));

        Assert.Equal(writer, finding.Handler);
        Assert.Equal(products.Name, finding.Table.Name);
        Assert.Equal(key.Template, finding.Key.Template);
        Assert.Equal(Confidence.Confirmed, finding.Confidence);
        Assert.Collection(finding.Chain,
            edge => Assert.IsType<Calls>(edge),
            edge => Assert.IsType<Writes>(edge),
            edge => Assert.IsType<Reads>(edge),
            edge => Assert.IsType<Caches>(edge));
        Assert.Equal(reader, Assert.IsType<Reads>(finding.Chain[2]).From);
    }

    [Fact]
    public void Reports_a_write_made_by_a_trigger_whose_events_the_write_answers()
    {
        var graph = NewGraph(out var products, out _, out _);
        var discounts = Table("Discounts");
        var writer = Handler("Discounts.Post");
        var trigger = Trigger("trg_Discounts_Audit", discounts, WriteEvent.Insert, WriteEvent.Update);
        graph.AddEdge(new Writes(writer, discounts, Confidence.Confirmed, [Code(60)], [WriteEvent.Insert]));
        graph.AddEdge(new Fires(discounts, trigger, Confidence.Confirmed, [InDatabase(trigger.Name)]));
        graph.AddEdge(new Writes(trigger, products, Confidence.Confirmed, [InDatabase(trigger.Name)],
            [WriteEvent.Insert, WriteEvent.Update, WriteEvent.Delete]));

        var finding = Assert.Single(new UnguardedWriteRule().Evaluate(graph));

        Assert.Equal(writer, finding.Handler);
        Assert.Collection(finding.Chain,
            edge => Assert.IsType<Writes>(edge),
            edge => Assert.IsType<Fires>(edge),
            edge => Assert.IsType<Writes>(edge),
            edge => Assert.IsType<Reads>(edge),
            edge => Assert.IsType<Caches>(edge));
    }

    [Fact]
    public void Leaves_a_trigger_declared_for_another_event_out_of_the_chain()
    {
        var graph = NewGraph(out var products, out _, out _);
        AddTriggerCascade(graph, products, [WriteEvent.Insert], WriteEvent.Delete);

        Assert.Empty(new UnguardedWriteRule().Evaluate(graph));
    }

    [Fact]
    public void Fires_no_trigger_on_a_truncate()
    {
        var graph = NewGraph(out var products, out _, out _);
        AddTriggerCascade(graph, products, [WriteEvent.Truncate],
            WriteEvent.Insert, WriteEvent.Update, WriteEvent.Delete);

        Assert.Empty(new UnguardedWriteRule().Evaluate(graph));
    }

    [Fact]
    public void Takes_a_write_whose_events_are_unknown_to_answer_every_trigger()
    {
        var graph = NewGraph(out var products, out _, out _);
        AddTriggerCascade(graph, products, [], WriteEvent.Delete);

        Assert.Single(new UnguardedWriteRule().Evaluate(graph));
    }

    [Fact]
    public void Terminates_on_a_cyclic_trigger_cascade()
    {
        var graph = NewGraph(out var products, out _, out _);
        var other = Table("Audit");
        var writer = Handler("Products.Put");
        var onProducts = Trigger("trg_Products", products, WriteEvent.Insert);
        var onOther = Trigger("trg_Audit", other, WriteEvent.Insert);

        graph.AddEdge(new Writes(writer, products, Confidence.Confirmed, [Code(70)], [WriteEvent.Insert]));
        graph.AddEdge(new Fires(products, onProducts, Confidence.Confirmed, [InDatabase(onProducts.Name)]));
        graph.AddEdge(new Fires(other, onOther, Confidence.Confirmed, [InDatabase(onOther.Name)]));
        // The cascade closes on itself: the trigger on Products writes Audit, and the trigger on Audit
        // writes Products.
        graph.AddEdge(new Writes(onProducts, other, Confidence.Confirmed, [InDatabase(onProducts.Name)],
            [WriteEvent.Insert]));
        graph.AddEdge(new Writes(onOther, products, Confidence.Confirmed, [InDatabase(onOther.Name)],
            [WriteEvent.Insert]));

        var findings = new UnguardedWriteRule().Evaluate(graph);

        // The handler's own write and the one the cascade brings back round to Products, once each.
        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.Equal(writer, finding.Handler));
    }

    [Fact]
    public void Finds_nothing_when_the_head_handler_of_a_procedure_write_invalidates_the_key()
    {
        var graph = NewGraph(out var products, out var key, out _);
        var writer = Handler("Prices.Put");
        var procedure = Procedure("ApplyDiscount");
        graph.AddEdge(new Calls(writer, procedure, Confidence.Confirmed, [Code(44)]));
        graph.AddEdge(new Writes(procedure, products, Confidence.Confirmed, [InDatabase("ApplyDiscount")],
            [WriteEvent.Update]));
        graph.AddEdge(new Invalidates(writer, Removal(key), Confidence.Confirmed, [Code(45)]));

        Assert.Empty(new UnguardedWriteRule().Evaluate(graph));
    }

    [Fact]
    public void Finds_nothing_when_the_head_handler_of_a_trigger_write_invalidates_the_key()
    {
        var graph = NewGraph(out var products, out var key, out _);
        var discounts = Table("Discounts");
        var writer = Handler("Discounts.Post");
        var trigger = Trigger("trg_Discounts_Audit", discounts, WriteEvent.Insert);
        graph.AddEdge(new Writes(writer, discounts, Confidence.Confirmed, [Code(60)], [WriteEvent.Insert]));
        graph.AddEdge(new Fires(discounts, trigger, Confidence.Confirmed, [InDatabase(trigger.Name)]));
        graph.AddEdge(new Writes(trigger, products, Confidence.Confirmed, [InDatabase(trigger.Name)],
            [WriteEvent.Insert]));
        graph.AddEdge(new Invalidates(writer, Removal(key), Confidence.Confirmed, [Code(61)]));

        Assert.Empty(new UnguardedWriteRule().Evaluate(graph));
    }

    [Fact]
    public void Reports_nothing_for_a_procedure_no_indexed_code_calls()
    {
        var graph = NewGraph(out var products, out _, out _);
        var procedure = Procedure("NightlyRepricing");
        graph.AddEdge(new Writes(procedure, products, Confidence.Confirmed, [InDatabase(procedure.Name)],
            [WriteEvent.Update]));

        Assert.Empty(new UnguardedWriteRule().Evaluate(graph));
        // The write is in the graph; it simply has nobody to fix it.
        Assert.Single(graph.Edges.OfType<Writes>());
    }

    /// <summary>A handler caching a key that reads one table, which is what a write has to threaten.</summary>
    private static CacheGraph NewGraph(out Table products, out CacheKey key, out Handler reader)
    {
        var graph = new CacheGraph();
        products = Table("Products");
        key = new CacheKey("product:{id}", "memory", null, [], "cache");
        reader = Handler("Products.Get");
        graph.AddEdge(new Caches(reader, key, Confidence.Confirmed, [Code(14)]));
        graph.AddEdge(new Reads(reader, products, Confidence.Confirmed, [Code(12)]));
        return graph;
    }

    private static void AddTriggerCascade(CacheGraph graph, Table written, WriteEvent[] writeEvents,
                                          params WriteEvent[] triggerEvents)
    {
        var discounts = Table("Discounts");
        var writer = Handler("Discounts.Post");
        var trigger = Trigger("trg_Discounts_Audit", discounts, triggerEvents);
        graph.AddEdge(new Writes(writer, discounts, Confidence.Confirmed, [Code(60)], writeEvents));
        graph.AddEdge(new Fires(discounts, trigger, Confidence.Confirmed, [InDatabase(trigger.Name)]));
        graph.AddEdge(new Writes(trigger, written, Confidence.Confirmed, [InDatabase(trigger.Name)],
            [WriteEvent.Insert, WriteEvent.Update, WriteEvent.Delete]));
    }

    private static CacheKey Removal(CacheKey key) => new(key.Template, key.Store, null, [], null);

    private static Table Table(string name) => new("dbo", name, DATABASE);

    private static StoredProcedure Procedure(string name) => new("dbo", name, DATABASE);

    private static Trigger Trigger(string name, Table table, params WriteEvent[] events) =>
        new("dbo", name, table.Name, events, DATABASE);

    private static Handler Handler(string symbol) => new("fixture", symbol, "controller", "fixture.cs", 1);

    private static Evidence Code(int line) => new("fixture.cs", line);

    private static Evidence InDatabase(string name) =>
        Evidence.InDatabase(name.Contains('.', StringComparison.Ordinal) ? name : $"dbo.{name}", DATABASE);
}
