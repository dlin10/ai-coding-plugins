using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The matrix of <c>task(…)</c> and <c>completion(…)</c> in the model vocabulary (R4): every form, declared return,
/// companion and destination, run through the entry step and then the member step. Accept and refuse have no safe side, so every
/// cell is exact.</summary>
public sealed class TaskResultVocabularyMatrixTests
{
    private const string PARAMETERS = "Box item, IEnumerable<Box> items, string key, Box value, Task<Box> task, ValueTask<Box> vtask, Task plain, " +
                                      "Task<Task<Box>> nested, Task<List<Box>> listTask";

    private static readonly string?[] Forms =
    [
        null, "task(new)", "task(sequence(arg:items))", "task(collection(arg:items))", "task(dictionary(arg:key,arg:value))", "task([arg:item])",
        "task([completion(arg:task)])", "task([completion(arg:vtask)])", "task([completion(completion(arg:nested))])",
        "task(collection(elements(completion(arg:listTask))))", "task(task(new))", "new", "sequence(arg:items)", "[arg:task]",
        "[completion(arg:item)]", "[completion(arg:plain)]"
    ];

    private static readonly string[] Returns =
    [
        "Task<Box>", "ValueTask<Box>", "Task<IEnumerable<Box>>", "Task<List<Box>>", "Task<Dictionary<string, Box>>", "Task<Task<Box>>",
        "Task<int>", "Task", "ValueTask", "Box", "IEnumerable<Box>"
    ];

    /// <summary>The type each task return completes with, where its twin returns that type.</summary>
    private static readonly Dictionary<string, string> Completions = new(StringComparer.Ordinal)
    {
        ["Task<Box>"] = "Box",
        ["ValueTask<Box>"] = "Box",
        ["Task<IEnumerable<Box>>"] = "IEnumerable<Box>",
        ["Task<List<Box>>"] = "List<Box>",
        ["Task<Dictionary<string, Box>>"] = "Dictionary<string, Box>",
        ["Task<Task<Box>>"] = "Task<Box>",
        ["Task<int>"] = "int"
    };

    /// <summary>The task returns whose innermost completion type is a reference type, where a holder of the result may stand.</summary>
    private static readonly HashSet<string> ReferenceInnermost = new(StringComparer.Ordinal)
    {
        "Task<Box>", "ValueTask<Box>", "Task<IEnumerable<Box>>", "Task<List<Box>>", "Task<Dictionary<string, Box>>", "Task<Task<Box>>"
    };

    private static readonly string[] Members = [.. Returns, "List<Box>", "Dictionary<string, Box>", "int"];

    private static readonly Lazy<Compilation> Fixture = new(() =>
    {
        var methods = string.Join("\n", Members.Select((type, index) =>
            $"    public static {type} R{index}({PARAMETERS}) => default!;\n" +
            $"    public static {type} R{index}F({PARAMETERS}, Func<Box> f) => default!;\n" +
            $"    public static void O{index}({PARAMETERS}, out {type} output) {{ output = default!; }}"));
        var compilation = CSharpCompilation.Create("TaskVocabulary", [CSharpSyntaxTree.ParseText(
            "using System; using System.Collections.Generic; using System.Threading.Tasks;\nnamespace TaskVocabulary;\n" +
            "public class Box { }\npublic static class Lib\n{\n" + methods + "\n}\n")],
            StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return compilation;
    });

    public enum Companion { None, KeepsResult, HolderResult, Iterator }

    public enum Destination { Result, Output }

    private sealed record Cell(string? Form, string Return, Companion Companion, Destination Destination);

    private static IEnumerable<Cell> Cells() =>
        Forms.SelectMany(form => Returns.SelectMany(type =>
            Enum.GetValues<Companion>().Select(companion => new Cell(form, type, companion, Destination.Result))
                .Append(new Cell(form, type, Companion.None, Destination.Output))));

    /// <summary>The start commit's answer for each form that is neither <c>task(…)</c> nor <c>completion(…)</c> as the result of a
    /// member returning <c>Box</c> or <c>IEnumerable&lt;Box&gt;</c> (what must not change), by form, return and companion
    /// (none, keeps.result, holder result, iterator).</summary>
    private static readonly Dictionary<(string Form, string Return), bool[]> ResultToday = new()
    {
        [("none", "Box")] = [true, false, true, false],
        [("none", "IEnumerable<Box>")] = [true, false, true, false],
        [("new", "Box")] = [true, true, false, false],
        [("new", "IEnumerable<Box>")] = [true, true, false, false],
        [("sequence(arg:items)", "Box")] = [false, false, false, false],
        [("sequence(arg:items)", "IEnumerable<Box>")] = [true, false, false, true],
        [("[arg:task]", "Box")] = [false, false, false, false],
        // Task<Box> is no sealed type, so an explicit reference conversion to IEnumerable<Box> exists.
        [("[arg:task]", "IEnumerable<Box>")] = [true, false, false, false]
    };

    /// <summary>The start commit's answer for each such form as an <c>outputs</c> entry on an <c>out</c> parameter of each declared
    /// type, which checks it against the parameter's type with no task rule (what must not change), in the order of
    /// <see cref="Returns"/>.</summary>
    private static readonly Dictionary<string, bool[]> OutputToday = new(StringComparer.Ordinal)
    {
        ["none"] = [true, true, true, true, true, true, true, true, true, true, true],
        ["new"] = [true, true, true, true, true, true, true, true, true, true, true],
        ["sequence(arg:items)"] = [false, false, false, false, false, false, false, false, false, false, true],
        ["[arg:task]"] = [true, false, false, false, false, false, false, true, false, false, true]
    };

    [Fact]
    public void There_are_880_cells_and_every_axis_value_occurs()
    {
        var cells = Cells().ToArray();

        Assert.Equal(880, cells.Length);
        Assert.Equal(880, cells.Distinct().Count());
        Assert.Equal(16, Forms.Length);
        Assert.Equal(11, Returns.Length);
        Assert.All(Forms, form => Assert.Contains(cells, cell => cell.Form == form));
        Assert.All(Returns, type => Assert.Contains(cells, cell => cell.Return == type));
        Assert.All(Enum.GetValues<Companion>(), companion => Assert.Contains(cells, cell => cell.Companion == companion));
        Assert.All(Enum.GetValues<Destination>(), destination => Assert.Contains(cells, cell => cell.Destination == destination));
    }

    [Fact]
    public void Every_cell_is_answered_exactly()
    {
        var wrong = Cells().Select(cell => (Cell: cell, Expected: Expected(cell), Actual: Accepted(cell.Form, cell.Return, cell.Companion, cell.Destination)))
                           .Where(answer => answer.Expected != answer.Actual)
                           .Select(answer => $"{answer.Cell.Destination} {answer.Cell.Form ?? "none"} on {answer.Cell.Return} with {answer.Cell.Companion}: " +
                                             $"expected {(answer.Expected ? "accepted" : "refused")}")
                           .ToArray();

        Assert.True(wrong.Length == 0, string.Join("\n", wrong));
    }

    /// <summary>Cells whose answer the twin rule alone would not pin: a vocabulary that refused every <c>task(…)</c> and its twin
    /// alike would pass the matrix.</summary>
    /// <param name="form">The result form, or null for none.</param>
    /// <param name="type">The member's declared return.</param>
    /// <param name="companion">What stands beside the result.</param>
    /// <param name="accepted">Whether the entry is expected to be accepted.</param>
    [Theory]
    [InlineData("task(new)", "Task<Box>", Companion.None, true)]
    [InlineData("task(new)", "ValueTask<Box>", Companion.KeepsResult, true)]
    [InlineData("task(task(new))", "Task<Task<Box>>", Companion.KeepsResult, true)]
    [InlineData("task(sequence(arg:items))", "Task<IEnumerable<Box>>", Companion.Iterator, true)]
    [InlineData("task(dictionary(arg:key,arg:value))", "Task<Dictionary<string, Box>>", Companion.None, true)]
    [InlineData("task([arg:item])", "Task<Box>", Companion.None, true)]
    [InlineData("task([completion(arg:task)])", "Task<Box>", Companion.None, true)]
    [InlineData("task([completion(arg:vtask)])", "ValueTask<Box>", Companion.None, true)]
    [InlineData("task([completion(completion(arg:nested))])", "Task<Box>", Companion.None, true)]
    [InlineData("task(collection(elements(completion(arg:listTask))))", "Task<List<Box>>", Companion.None, true)]
    [InlineData("task([arg:item])", "Task<int>", Companion.None, false)]
    [InlineData(null, "Task<Task<Box>>", Companion.HolderResult, true)]
    [InlineData(null, "Task<int>", Companion.HolderResult, false)]
    [InlineData(null, "Task", Companion.HolderResult, false)]
    // completion(v) needs a v of a Task<T> or ValueTask<T> type; a source with no type the entry can show is no task (R4).
    [InlineData("task([completion(sequence(arg:items))])", "Task<Box>", Companion.None, false)]
    [InlineData("task([completion(grouping(arg:key,arg:items))])", "Task<Box>", Companion.None, false)]
    [InlineData("task([completion(kept:item)])", "Task<Box>", Companion.None, false)]
    [InlineData("[completion(kept:item)]", "Box", Companion.None, false)]
    [InlineData("task([completion(elements(arg:items))])", "Task<Box>", Companion.None, false)]
    public void Anchored_cells(string? form, string type, Companion companion, bool accepted) =>
        Assert.Equal(accepted, Accepted(form, type, companion, Destination.Result));

    /// <summary>The answer R4 gives a cell.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Expected(Cell cell)
    {
        var form = cell.Form ?? "none";
        // A value that is no task, or a task with no value, has no completion value.
        if (form is "[completion(arg:item)]" or "[completion(arg:plain)]")
            return false;
        if (cell.Destination == Destination.Output)
            return !form.StartsWith("task(", StringComparison.Ordinal) && OutputToday[form][Array.IndexOf(Returns, cell.Return)];
        var isTask = cell.Return.StartsWith("Task", StringComparison.Ordinal) || cell.Return.StartsWith("ValueTask", StringComparison.Ordinal);
        if (form.StartsWith("task(", StringComparison.Ordinal))
            return Completions.TryGetValue(cell.Return, out var completion) &&
                   Accepted(form["task(".Length..^1], completion, cell.Companion, Destination.Result);
        if (!isTask)
            return ResultToday[(form, cell.Return)][(int)cell.Companion];
        return (form, cell.Companion) switch
        {
            ("new" or "sequence(arg:items)", _) => false,
            ("[arg:task]", _) => cell.Return == "Task<Box>" && cell.Companion == Companion.None,
            ("none", Companion.None) => true,
            ("none", Companion.HolderResult) => ReferenceInnermost.Contains(cell.Return),
            // keeps.result and an iterator each need a result, which form none does not give.
            _ => false
        };
    }

    /// <summary>Whether an entry of <paramref name="form"/> with <paramref name="companion"/> passes the entry step and then the
    /// member step of the fixture member that returns, or outputs, <paramref name="type"/>.</summary>
    /// <param name="form">The result form, or null for none.</param>
    /// <param name="type">The member's declared return, or its <c>out</c> parameter's type.</param>
    /// <param name="companion">What stands beside the result.</param>
    /// <param name="destination">Whether the form is the member's result or an <c>outputs</c> entry.</param>
    private static bool Accepted(string? form, string type, Companion companion, Destination destination)
    {
        var index = Array.IndexOf(Members, type);
        var name = destination == Destination.Output ? $"O{index}" : companion is Companion.HolderResult or Companion.Iterator ? $"R{index}F" : $"R{index}";
        var method = Fixture.Value.GetTypeByMetadataName("TaskVocabulary.Lib")!.GetMembers(name).OfType<IMethodSymbol>().Single();
        RawFate[] fates = companion switch
        {
            Companion.HolderResult => [new RawFate("f", "holder", "result", null)],
            Companion.Iterator => [new RawFate("f", "iterator", null, null)],
            _ => []
        };
        var keeps = companion == Companion.KeepsResult ? new Dictionary<string, string[]> { ["result"] = ["arg:item"] } : null;
        var outputs = destination == Destination.Output && form is not null ? new Dictionary<string, string> { ["output"] = form } : null;
        try
        {
            var (result, parsed, stores, outs, kept) = LibraryVocabulary.Entry(destination == Destination.Result ? form : null, fates, [], null, outputs, keeps);
            return LibraryVocabulary.Member(result, parsed, method, Fixture.Value, [], stores, outs, kept) is null;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
