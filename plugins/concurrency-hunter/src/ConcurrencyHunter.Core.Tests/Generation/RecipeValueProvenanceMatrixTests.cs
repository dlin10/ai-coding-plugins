using System.Collections.Concurrent;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

public sealed class RecipeValueProvenanceMatrixTests
{
    private enum Path { RecipeOnly, OrdinaryOnly, RecipeAndSetup, RecipeAndCall }
    private enum Creators { Separate, Merged }
    private enum Depth { Top, Nested }
    private enum Source { LibraryCode, DriverNew }
    private sealed record Cell(Path Path, Creators Creators, Depth Depth, Source Source);
    private static IEnumerable<Cell> Cells() =>
        (from path in Enum.GetValues<Path>() from creators in Enum.GetValues<Creators>() from depth in Enum.GetValues<Depth>()
         from source in Enum.GetValues<Source>() select new Cell(path, creators, depth, source)).Where(cell => Allowed(cell) is null);

    private static string? Allowed(Cell cell) => cell.Creators == Creators.Merged && cell.Source == Source.DriverNew
        ? "Two new operations in the driver's text are distinct allocation sites." : null;

    [Fact]
    public void Every_cell_answers_as_its_run_twin()
    {
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(Cells(), CellParallelism, cell =>
        {
            try
            {
                var off = Generate(cell, TypeInitializerTreatment.Off);
                var running = Generate(cell, TypeInitializerTreatment.Run);
                var origins = Origins(off);
                var twin = Origins(running);
                Assert.Equal(twin, origins);
                if (cell.Path == Path.RecipeOnly)
                    Assert.All(origins, origin => Assert.Equal("LibraryBefore/None//False/False/", origin));
                if (cell.Path == Path.RecipeAndCall)
                {
                    Assert.Equal(Answer(running), Answer(off));
                    Assert.True(FateOf(off, "f").Fate == FateClassifier.HOLDER,
                        System.Text.Json.JsonSerializer.Serialize(off.Answer));
                    Assert.Equal(FateClassifier.RESULT, FateOf(off, "f").Holder);
                    Assert.NotNull(off.Answer.Model);
                    Assert.All(Slot(off, DriverSynthesizer.KEEP_TYPE, "R"), region => Assert.True(AllocationReader(off).CreatedByCall(region)));
                }
                if (cell.Source == Source.DriverNew && cell.Path == Path.RecipeAndSetup)
                {
                    Assert.Contains(origins, origin => origin.StartsWith("LibraryBefore/None/", StringComparison.Ordinal));
                    Assert.Contains(origins, origin => origin.StartsWith("Driver/", StringComparison.Ordinal));
                }
                if (cell.Creators == Creators.Merged && cell.Path is Path.RecipeAndCall or Path.RecipeAndSetup)
                {
                    var regions = off.Run!.Heap!.Regions.Values.Where(region => region.TypeKey == ASSEMBLY + ":Lib.Item").ToArray();
                    Assert.Single(regions);
                }
            }
            catch (Exception error) { failures.Add($"{cell}: {error}"); }
        });
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Order().Take(12)));
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        var cells = Cells().ToArray();
        Assert.Equal(Enum.GetValues<Path>(), cells.Select(cell => cell.Path).Distinct().Order());
        Assert.Equal(Enum.GetValues<Creators>(), cells.Select(cell => cell.Creators).Distinct().Order());
        Assert.Equal(Enum.GetValues<Depth>(), cells.Select(cell => cell.Depth).Distinct().Order());
        Assert.Equal(Enum.GetValues<Source>(), cells.Select(cell => cell.Source).Distinct().Order());
    }

    private static string[] Origins(GenerationTrace trace)
    {
        Assert.True(trace.Run is { Stopped: false }, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        var run = trace.Run;
        var allocations = AllocationReader(trace);
        var regions = run.Heap!.Regions.Values.Where(region => region.TypeKey == ASSEMBLY + ":Lib.Item").ToArray();
        Assert.NotEmpty(regions);
        return regions.Select(region =>
        {
            var allocation = allocations.Of(region.Identity);
            return $"{allocation.Kind}/{allocation.Role}/{allocation.Action}/{allocations.CreatedByCall(region.Identity)}/" +
                   $"{allocations.CreatedInSetup(region.Identity)}/{allocations.CreatedByTrigger(region.Identity)?.Action}";
        }).Order(StringComparer.Ordinal).ToArray();
    }

    private static Allocations AllocationReader(GenerationTrace trace)
    {
        var run = trace.Run!;
        var reachability = new HeapReachability(run.Heap!);
        var executions = new DriverExecutions(trace.Driver!, run.Executions!, new HashSet<string>(StringComparer.Ordinal));
        return new Allocations(trace.Driver!, run.Heap!, run.Executions!, executions, reachability, default, run.Reachable.Bodies);
    }

    private static string Answer(GenerationTrace trace) =>
        string.Join(";", trace.Answer.Classified!.OrderBy(pair => pair.Key).Select(pair => pair.Key + ":" + pair.Value)) + "|" +
        trace.Answer.ModelReason + "|" + (trace.Answer.Model is { } model ? ModelEntryWriter.Write(model).ToJsonString() : "null");

    private static GenerationTrace Generate(Cell cell, TypeInitializerTreatment treatment)
    {
        var made = cell.Source == Source.DriverNew ? "new Item()" : "Item.Make()";
        var valueType = cell.Depth == Depth.Top ? "Item" : "Outer";
        var value = cell.Depth == Depth.Top ? made : "new Outer(" + made + ")";
        var hasRecipe = cell.Path != Path.OrdinaryOnly;
        var ordinary = cell.Path is Path.OrdinaryOnly or Path.RecipeAndSetup;
        var from = cell.Path switch { Path.OrdinaryOnly => "input", Path.RecipeAndCall => value, _ => "State" };
        var item = cell.Depth == Depth.Top ? "value" : "value.Value";
        // Static-call contexts are per call site: Allocate is called at one shared site from Make for the shared-creator cells.
        var factory = cell.Source == Source.DriverNew ? "public Item() { }" :
            "private Item() { } public static Item Make() => " + (cell.Creators == Creators.Merged ? "Allocate(); private static Item Allocate() => " : "") + "new Item();";
        var source = $$"""
            using System;
            namespace Lib {
                public sealed class Item { {{factory}} }
                public sealed class Outer { public readonly Item Value; public Outer(Item value) { Value = value; } }
                public sealed class Result { private readonly Item _value; private readonly Action _f;
                    public Result(Item value, Action f) { _value = value; _f = f; } public void Fire() { _f(); } }
                public static class Api {
                    {{(hasRecipe ? "public static readonly " + valueType + " State = " + value + ";" : "")}}
                    public static Result Run({{(ordinary ? valueType + " input, " : "")}}Action f) {
                        {{(hasRecipe ? "_ = State;" : "")}}
                        var value = {{from}}; return new Result({{item}}, f);
                    }
                }
            }
            """;
        var member = "M:Lib.Api.Run(" + (ordinary ? "Lib." + valueType + "," : "") + "System.Action)";
        return ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", member, null, null), Compile(source), CancellationToken.None,
            initializers: treatment);
    }
}
