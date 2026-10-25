using System.Collections.Concurrent;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

public sealed class RecipeValueUnseededMatrixTests
{
    private enum Closure { Built, Failed }
    private enum Access { SetupOnly, CallFirst, CallMerged }
    private enum Channel { Direct, RefParameter, RefLocal, RefReturn }
    private enum Kind { Read, Write }
    private sealed record Cell(Closure First, Closure Second, Access Access, Channel Channel, Kind Kind);

    private static IEnumerable<Cell> Cells() =>
        (from first in Enum.GetValues<Closure>() from second in Enum.GetValues<Closure>() from access in Enum.GetValues<Access>()
         from channel in Enum.GetValues<Channel>() from kind in Enum.GetValues<Kind>() select new Cell(first, second, access, channel, kind))
        .Where(cell => Allowed(cell) is null);

    private static string? Allowed(Cell cell) => cell.Channel != Channel.Direct && cell.Access == Access.CallMerged
        ? "A reference projection already merges closed storage; merging its caller adds no case." : null;

    [Fact]
    public void Every_cell_reads_unseeded_as_expected()
    {
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(Cells(), CellParallelism, cell =>
        {
            try
            {
                var trace = Generate(cell);
                Assert.NotNull(trace.Run);
                Assert.Equal("off", trace.Answer.Generation.Mode);
                var expected = cell.Access != Access.SetupOnly && (cell.First == Closure.Failed ||
                    (cell.Access == Access.CallMerged || cell.Channel != Channel.Direct) && cell.Second == Closure.Failed);
                Assert.Equal(expected, trace.Answer.Causes.Any(cause => cause.Code == ModelCauses.UNSEEDED_READ));
                if (expected)
                {
                    Assert.Equal(ModelReasons.INCOMPLETE, trace.Answer.ModelReason);
                    Assert.Null(trace.Answer.Model);
                }
                Assert.Equal((cell.First == Closure.Failed ? 1 : 0) + (cell.Second == Closure.Failed ? 1 : 0),
                    trace.Driver!.UnseededRecipeStatics.Count(storage => storage.Type.Contains("Registry<", StringComparison.Ordinal)));
            }
            catch (Exception error) { failures.Add($"{cell}: {error}"); }
        });
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Order().Take(20)));
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        var cells = Cells().ToArray();
        Assert.Equal(Enum.GetValues<Closure>(), cells.Select(cell => cell.First).Distinct().Order());
        Assert.Equal(Enum.GetValues<Closure>(), cells.Select(cell => cell.Second).Distinct().Order());
        Assert.Equal(Enum.GetValues<Access>(), cells.Select(cell => cell.Access).Distinct().Order());
        Assert.Equal(Enum.GetValues<Channel>(), cells.Select(cell => cell.Channel).Distinct().Order());
        Assert.Equal(Enum.GetValues<Kind>(), cells.Select(cell => cell.Kind).Distinct().Order());
    }

    private static GenerationTrace Generate(Cell cell)
    {
        var operation = cell.Kind == Kind.Read ? "_ = " : "";
        var end = cell.Kind == Kind.Read ? ";" : " = default;";
        var direct = operation + "Registry<First>.s_value" + end;
        var body = cell.Channel switch
        {
            Channel.Direct => direct,
            Channel.RefParameter => "Use(ref Registry<First>.s_value);",
            Channel.RefLocal => "ref var place = ref Registry<First>.s_value; " + operation + "place" + end,
            _ => "ref var place = ref Registry<First>.Ref(); " + operation + "place" + end
        };
        if (cell.Access == Access.SetupOnly)
            body = "";
        if (cell.Access == Access.CallMerged)
            body = string.Join(" ", Enumerable.Range(0, 20).Select(index => "Merged<" + (index % 2 == 0 ? "First" : "Second") + ">();"));
        var source = $$"""
            using System;
            namespace Lib {
                public sealed class First { {{(cell.First == Closure.Built ? "public" : "private")}} First() { } }
                public sealed class Second { {{(cell.Second == Closure.Built ? "public" : "private")}} Second() { } }
                public static class Registry<T> { public static T s_value; public static T Read() => s_value;
                    public static ref T Ref() => ref s_value; }
                public sealed class Input { public Input() { _ = Registry<First>.Read(); _ = Registry<Second>.Read(); } }
                public static class Api {
                    private static void Use(ref First place) { {{operation}}place{{end}} }
                    private static void Merged<T>() { {{operation}}Registry<T>.s_value{{end}} }
                    public static void Run(Input input, Action action) { {{body}} action(); }
                }
            }
            """;
        return ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", "M:Lib.Api.Run(Lib.Input,System.Action)", null, null),
            Compile(source), CancellationToken.None, initializers: TypeInitializerTreatment.Off);
    }
}
