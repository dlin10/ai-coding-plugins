using ConcurrencyHunter.Analysis;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

public sealed class MetricsGenerationOptionMatrixTests
{
    private const string TARGET = "Fixture.slnx";
    private const string OUT = "metrics.json";
    private const string CENSUS = "scratch/census";

    private enum Generation { Absent, On, Read, Off }
    private sealed record Cell(Generation Generation, bool Recording, bool Census, bool Settle, bool Offline);

    private static IEnumerable<Cell> Cells() =>
        from generation in Enum.GetValues<Generation>()
        from recording in new[] { false, true }
        from census in new[] { false, true }
        from settle in new[] { false, true }
        from offline in new[] { false, true }
        select new Cell(generation, recording, census, settle, offline);

    private static string[] Arguments(Cell cell)
    {
        var arguments = new List<string> { "--target", TARGET, "--out", OUT };
        if (cell.Generation != Generation.Absent)
            arguments.AddRange(["--generation", cell.Generation.ToString().ToLowerInvariant()]);
        if (cell.Recording) arguments.Add("--recording");
        if (cell.Census) arguments.AddRange(["--census", CENSUS]);
        if (cell.Settle) arguments.Add("--settle");
        if (cell.Offline) arguments.Add("--offline");
        return arguments.ToArray();
    }

    private static IEnumerable<string> BrokenRules(Cell cell)
    {
        if (cell.Census && (cell.Generation != Generation.On || cell.Recording))
            yield return "--census needs a folder and --generation on, and does not go with --recording.";
        if (cell.Recording && cell.Generation != Generation.On)
            yield return "--recording needs --generation on.";
        if (cell.Generation == Generation.On && !cell.Recording && !cell.Census)
            yield return "--generation on needs --recording or --census.";
        if (cell.Settle && !cell.Census)
            yield return "--settle needs --census.";
    }

    [Fact]
    public void Every_cell_is_exact()
    {
        foreach (var cell in Cells())
        {
            var accepted = (cell.Generation == Generation.On ? cell.Recording != cell.Census : !cell.Recording && !cell.Census) &&
                           (!cell.Settle || cell.Census);
            MetricsCommand.Options? options = null;
            var error = Record.Exception(() => { options = MetricsCommand.Options.Parse(Arguments(cell)); });
            if (!accepted)
            {
                var refusal = Assert.IsType<ArgumentException>(error);
                Assert.True(BrokenRules(cell).Contains(refusal.Message, StringComparer.Ordinal),
                            $"{cell}: refusal does not name a broken rule: {refusal.Message}");
                continue;
            }

            Assert.True(error is null, $"{cell}: unexpected refusal: {error}");
            Assert.NotNull(options);
            Assert.Equal(TARGET, options.Target);
            Assert.Equal(OUT, options.Out);
            var generation = cell.Generation switch
            {
                Generation.On => ModelGeneration.Generate,
                Generation.Off => ModelGeneration.None,
                _ => ModelGeneration.ReadOnly
            };
            Assert.Equal(generation, options.Generation);
            GenerationBudget? budget = cell.Recording ? GenerationBudget.Recording : cell.Census ? GenerationBudget.Census : null;
            Assert.Equal(budget, options.Budget);
            Assert.Equal(cell.Census ? CENSUS : null, options.Census);
            Assert.Equal(cell.Settle, options.Settle);
            Assert.Equal(cell.Offline, options.Offline);
        }
    }

    [Fact]
    public void Every_value_of_every_axis_occurs_and_the_cells_are_counted()
    {
        var cells = Cells().ToArray();
        Assert.Equal(64, cells.Length);
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<Generation>(), cells.Select(cell => cell.Generation).Distinct().Order());
        Assert.Equal(new[] { false, true }, cells.Select(cell => cell.Recording).Distinct().Order());
        Assert.Equal(new[] { false, true }, cells.Select(cell => cell.Census).Distinct().Order());
        Assert.Equal(new[] { false, true }, cells.Select(cell => cell.Settle).Distinct().Order());
        Assert.Equal(new[] { false, true }, cells.Select(cell => cell.Offline).Distinct().Order());
    }
}
