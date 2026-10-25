using System.Collections.Concurrent;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

public sealed class RecipeValueDiscoveryMatrixTests
{
    private enum Novelty { NewField, NewClosureOfKnownField }
    private enum FoundBy { NextFatePass, Confirmation }
    private enum KnownClosure { Runs, DoesNot, None }
    private sealed record Cell(Novelty Novelty, FoundBy FoundBy, KnownClosure Known);
    private static IEnumerable<Cell> Cells() =>
        (from novelty in Enum.GetValues<Novelty>() from found in Enum.GetValues<FoundBy>() from known in Enum.GetValues<KnownClosure>()
         select new Cell(novelty, found, known)).Where(cell => Allowed(cell) is null);

    private static string? Allowed(Cell cell) => cell.Novelty switch
    {
        Novelty.NewField when cell.Known != KnownClosure.None => "A new field has no previously loaded closure of that field.",
        Novelty.NewClosureOfKnownField when cell.Known == KnownClosure.None => "A new closure requires a known closure of the same field.",
        _ => null
    };

    [Fact]
    public void Every_cell_gets_its_recipe_value_as_expected()
    {
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(Cells(), CellParallelism, cell =>
        {
            try
            {
                var trace = Generate(cell);
                Assert.Null(trace.Answer.Reason);
                var statements = trace.Driver!.RecipeStatements.Select(statement => statement.Operation.Syntax.ToString()).ToArray();
                Assert.Contains(statements, statement => statement.Contains("global::Lib.Registry<global::System.Int64>.s_box = new global::Lib.Value()"));
                if (cell.Known == KnownClosure.DoesNot)
                    Assert.Contains(statements, statement => statement.Contains("Registry<global::System.Int32>.s_box ="));
                if (cell.Known == KnownClosure.Runs)
                    Assert.DoesNotContain(statements, statement => statement.Contains("Probe_"));
                Assert.Contains(SeedableFields.LoadedClosedStatics(cell.FoundBy == FoundBy.Confirmation ? trace.Confirmation! : trace.Run!), storage => storage.Type.Contains("Registry<", StringComparison.Ordinal) &&
                    storage.Type.Contains(":long>", StringComparison.Ordinal));
                if (cell.FoundBy == FoundBy.Confirmation)
                {
                    Assert.Equal(2, trace.ConfirmationPasses);
                    Assert.Equal(FateClassifier.HOLDER, FateOf(trace, "a").Fate);
                }
            }
            catch (Exception error) { failures.Add($"{cell}: {error}"); }
        });
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Order()));
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        var cells = Cells().ToArray();
        Assert.Equal(Enum.GetValues<Novelty>(), cells.Select(cell => cell.Novelty).Distinct().Order());
        Assert.Equal(Enum.GetValues<FoundBy>(), cells.Select(cell => cell.FoundBy).Distinct().Order());
        Assert.Equal(Enum.GetValues<KnownClosure>(), cells.Select(cell => cell.Known).Distinct().Order());
    }

    private static GenerationTrace Generate(Cell cell)
    {
        var known = cell.Known switch { KnownClosure.Runs => "_ = Registry<T>.s_box;", KnownClosure.DoesNot => "_ = Registry<int>.s_box;", _ => "" };
        var generic = cell.Known == KnownClosure.Runs ? "<T>" : "";
        var source = "using System; namespace Lib { public sealed class Value { } public static class Registry<T> { public static Value s_box = new Value(); } " +
            (cell.FoundBy == FoundBy.NextFatePass
                ? "public class Starter { public Starter() { _ = Registry<long>.s_box; } public virtual void Touch() { } } " +
                  "public static class Api { public static Starter Start; public static void Run" + generic + "(Action a) { " + known + " Start.Touch(); a(); } }"
                : "public sealed class Parser" + generic + " { private Action _a; public Parser(Action a) { " + known + " _a = a; } " +
                  "public void Fire() { _ = Registry<long>.s_box; _a(); } }") + " }";
        var member = cell.FoundBy == FoundBy.NextFatePass
            ? "M:Lib.Api.Run" + (generic.Length == 0 ? "" : "``1") + "(System.Action)"
            : "M:Lib.Parser" + (generic.Length == 0 ? "" : "`1") + ".#ctor(System.Action)";
        return ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", member, null, null), Compile(source),
            CancellationToken.None, initializers: TypeInitializerTreatment.Off);
    }
}
