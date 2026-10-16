using System.Collections.Concurrent;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>
/// The capture cell of a variable is its declaring body's, whatever nested body captures it (R5, TD-070, ADR 0020). The declarer — the
/// action itself, a lambda it runs once or a local function it calls once — declares a counter or a variable it assigns from a singleton's
/// list, and a capturer reaches it: a lambda run by <c>Task.Run</c>, a local function the declarer calls twice in sequence, the local
/// function run by <c>Task.Run</c> as a method group, or a lambda calling it run by <c>Task.Run</c>. Every capturer's access to the variable
/// is an access on the slot of the closure of the ordinary member; a counter pairs within one run of its declarer where two executions of
/// that run capture it and never across requests, and the singleton's list a capturer adds to pairs across requests, the action against
/// itself. One HTTP action runs the declarer once per request, so the closure's creator is one execution.
/// </summary>
public sealed class CaptureOwnerMatrixTests
{
    public enum Declarer
    {
        Method,
        Lambda,
        LocalFunction
    }

    public enum Capturer
    {
        DelegateLambda,
        DirectLocalFunction,
        LocalFunctionDelegate,
        LambdaCallingLocalFunction
    }

    public enum Variable
    {
        Counter,
        SharedObject
    }

    public sealed record Cell(Declarer Declarer, Capturer Capturer, Variable Variable)
    {
        public override string ToString() => $"{Declarer}/{Capturer}/{Variable}";
    }

    private const string CLOSURE = "closure:BoxController.Post()";

    private static readonly ConcurrentDictionary<Cell, EngineRun> RUNS = new();

    public static IEnumerable<Cell> Cells() =>
        from declarer in Enum.GetValues<Declarer>()
        from capturer in Enum.GetValues<Capturer>()
        from variable in Enum.GetValues<Variable>()
        select new Cell(declarer, capturer, variable);

    /// <summary>Whether two executions of one run of the declarer capture the variable.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Concurrent(Cell cell) => cell.Capturer != Capturer.DirectLocalFunction;

    [Fact]
    public void Every_cell_accesses_as_expected()
    {
        var failures = new List<string>();
        foreach (var cell in Cells())
        {
            var run = Run(cell);
            var name = Name(cell.Variable);
            var accesses = run.Accesses(name);
            failures.AddRange(accesses.Where(access => access.Resource.Region != CLOSURE)
                                      .Select(access => $"{cell}: {access.Operation} of '{name}' at line {access.Source.StartLine} on {access.Resource.Region}"));
            // The capturer reads the cell, or reads and writes it; the declarer writes it fresh, its own run having created the closure.
            var capturer = accesses.Where(access => access.Source.StartLine == Line(cell, Access(cell.Variable))).ToArray();
            var expected = cell.Variable == Variable.Counter ? AccessOperation.ReadModifyWrite : AccessOperation.Read;
            if (capturer.Length == 0 || capturer.Any(access => access.Operation != expected || access.IsFresh))
                failures.Add($"{cell}: capturer's accesses [{Describe(capturer)}], expected {expected} not fresh");
            var declarer = accesses.Where(access => access.Source.StartLine == Line(cell, Declaration(cell.Variable))).ToArray();
            if (declarer.Length == 0 || declarer.Any(access => access.Operation != AccessOperation.Write || !access.IsFresh))
                failures.Add($"{cell}: declarer's accesses [{Describe(declarer)}], expected a fresh write");
            // Only a counter two executions of one run increment pairs, and only its increment with itself.
            var pairs = run.PairsOn(name).Select(pair => Key(cell, pair)).Distinct().Order(StringComparer.Ordinal).ToArray();
            string[] pairsExpected = cell.Variable == Variable.Counter && Concurrent(cell) ? ["access x access"] : [];
            if (!pairs.SequenceEqual(pairsExpected))
                failures.Add($"{cell}: pairs on '{name}' [{string.Join(", ", pairs)}], expected [{string.Join(", ", pairsExpected)}]");
            if (cell.Variable == Variable.SharedObject && !SharedListPairs(cell, run))
                failures.Add($"{cell}: no pair of the action against itself on the singleton list ({Describe(Writes(cell, run))})");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void No_cell_loses_an_access()
    {
        var failures = new List<string>();
        foreach (var cell in Cells())
        {
            var run = Run(cell);
            var name = Name(cell.Variable);
            if (!run.Accesses(name).Any(access => access.Source.StartLine == Line(cell, Access(cell.Variable))))
                failures.Add($"{cell}: the capturer's access to '{name}' is lost");
            if (!run.Accesses(name).Any(access => access.Source.StartLine == Line(cell, Declaration(cell.Variable))))
                failures.Add($"{cell}: the declarer's write of '{name}' is lost");
            if (cell.Variable == Variable.SharedObject && Writes(cell, run).Count == 0)
                failures.Add($"{cell}: the capturer's write of the singleton list is lost");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void No_counter_pairs_across_runs_of_its_declarer()
    {
        var failures = new List<string>();
        foreach (var cell in Cells().Where(cell => cell.Variable == Variable.Counter))
        {
            var run = Run(cell);
            // Another request's increment would meet this request's declaration; the closure of one run is that run's alone.
            failures.AddRange(run.PairsOn(Name(cell.Variable))
                                 .Where(pair => pair.First.Source.StartLine == Line(cell, Declaration(cell.Variable)) ||
                                                pair.Second.Source.StartLine == Line(cell, Declaration(cell.Variable)))
                                 .Select(pair => $"{cell}: {pair.First.Operation} at {pair.First.Source.StartLine} x {pair.Second.Operation} at {pair.Second.Source.StartLine}"));
            failures.AddRange(run.Accesses(Name(cell.Variable)).Where(access => access.Ownership is not (OwnershipKind.ThreadConfined or OwnershipKind.TreeConfined))
                                 .Select(access => $"{cell}: the closure is {access.Ownership} at line {access.Source.StartLine}"));
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Every_value_of_every_axis_occurs()
    {
        var cells = Cells().ToArray();

        Assert.Equal(Enum.GetValues<Declarer>(), cells.Select(cell => cell.Declarer).Distinct());
        Assert.Equal(Enum.GetValues<Capturer>(), cells.Select(cell => cell.Capturer).Distinct());
        Assert.Equal(Enum.GetValues<Variable>(), cells.Select(cell => cell.Variable).Distinct());
        Assert.Equal(3 * 4 * 2, cells.Length);
        Assert.Contains(cells, Concurrent);
        Assert.Contains(cells, cell => !Concurrent(cell));
    }

    // ---- the fixture of a cell ----

    private static EngineRun Run(Cell cell) => RUNS.GetOrAdd(cell, _ => Analyze(Source(cell)));

    /// <summary>A pair's sides as the marks <c>access</c> and <c>declaration</c>, or their line where neither mark holds it.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="pair">The pair.</param>
    private static string Key(Cell cell, AccessPair pair)
    {
        string Side(Access access) =>
            access.Source.StartLine == Line(cell, Access(cell.Variable)) ? "access"
            : access.Source.StartLine == Line(cell, Declaration(cell.Variable)) ? "declaration"
            : $"line {access.Source.StartLine}";
        var sides = new[] { Side(pair.First), Side(pair.Second) }.Order(StringComparer.Ordinal).ToArray();
        return $"{sides[0]} x {sides[1]}";
    }

    /// <summary>The capturer's writes of the singleton list, outside every closure.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="run">Its engine run.</param>
    private static IReadOnlyList<Access> Writes(Cell cell, EngineRun run) =>
        run.Collection.Accesses.Where(access => access.Source.StartLine == Line(cell, Access(cell.Variable)) &&
                                                !access.Resource.Region.StartsWith("closure:", StringComparison.Ordinal) &&
                                                access.Operation == AccessOperation.Write)
           .ToArray();

    /// <summary>Whether the capturer's write of the singleton list pairs with itself: the action against itself, across requests.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="run">Its engine run.</param>
    private static bool SharedListPairs(Cell cell, EngineRun run)
    {
        var line = Line(cell, Access(cell.Variable));
        return run.Pairs.Pairs.Any(pair => !pair.Resource.Region.StartsWith("closure:", StringComparison.Ordinal) &&
                                           pair.First.Source.StartLine == line && pair.Second.Source.StartLine == line &&
                                           pair.First.Operation == AccessOperation.Write && pair.Second.Operation == AccessOperation.Write);
    }

    private static string Describe(IEnumerable<Access> accesses) =>
        string.Join("; ", accesses.Select(access => $"{access.Operation}{(access.IsFresh ? " fresh" : "")} on {access.Resource.Region} " +
                                                    $"{string.Join(".", access.Resource.AccessPath)} at {access.Source.StartLine}"));

    private static int Line(Cell cell, string text) =>
        Array.FindIndex((Usings + Source(cell)).Split('\n'), line => line.Contains(text, StringComparison.Ordinal)) + 1 is var found and > 0
            ? found
            : throw new InvalidOperationException($"no line holds '{text}'");

    private static string Name(Variable variable) => variable == Variable.Counter ? "n" : "list";

    private static string Declaration(Variable variable) => variable == Variable.Counter ? "var n = 0;" : "var list = keeper.Items;";

    private static string Access(Variable variable) => variable == Variable.Counter ? "n++;" : "list.Add(1);";

    /// <summary>The declarer's body: the variable, and the capturer reaching it.</summary>
    /// <param name="cell">The cell.</param>
    private static string Body(Cell cell)
    {
        var access = Access(cell.Variable);
        var capturer = cell.Capturer switch
        {
            Capturer.DelegateLambda => $$"""
                Action work = () => { {{access}} };
                await Task.WhenAll(Task.Run(work), Task.Run(work));
                """,
            Capturer.DirectLocalFunction => $$"""
                Local();
                Local();
                await Task.CompletedTask;

                void Local() { {{access}} }
                """,
            Capturer.LocalFunctionDelegate => $$"""
                await Task.WhenAll(Task.Run(Local), Task.Run(Local));

                void Local() { {{access}} }
                """,
            _ => $$"""
                await Task.WhenAll(Task.Run(() => Local()), Task.Run(() => Local()));

                void Local() { {{access}} }
                """
        };
        return $"""
            {Declaration(cell.Variable)}
            {capturer}
            """;
    }

    private static string Source(Cell cell)
    {
        var post = cell.Declarer switch
        {
            Declarer.Method => Body(cell),
            Declarer.Lambda => $$"""
                Func<Task> declarer = async () =>
                {
                    {{Body(cell)}}
                };
                await declarer();
                """,
            _ => $$"""
                await Declarer();

                async Task Declarer()
                {
                    {{Body(cell)}}
                }
                """
        };
        return $$"""
            public sealed class Keeper { public System.Collections.Generic.List<int> Items = new(); }
            public class BoxController(Keeper keeper) : ControllerBase
            {
                public async Task Post()
                {
                    {{post}}
                }
            }
            """ + Startup("services.AddSingleton<Keeper>();");
    }
}
