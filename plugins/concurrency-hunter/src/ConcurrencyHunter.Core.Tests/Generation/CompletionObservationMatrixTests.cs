using System.Text;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The generator's answer for a member whose result comes through a task, against its synchronous twin (R2, R6): every arm of
/// <c>ValueObservation</c>'s walk of what completed a task. A cell's member builds a task with one producer from one input and consumes
/// it by <c>await</c> or <c>.Result</c>; its twin returns the input directly, through the same unseen alternative where the cell has
/// one. Each cell's generated result is exactly the twin's, inside <c>task(…)</c> for an <c>await</c> member: what the twin names for a
/// known input and a fully seen producer, no result naming the input otherwise.</summary>
public sealed class CompletionObservationMatrixTests
{
    private const string DEPENDENCY = "Fixture.Dependency";
    private const string MEMBER = "M:Lib.Cell.Run(Lib.Box,System.Boolean)";

    private const string TYPES = """
        public static class Externals
        {
            public static extern Box MakeBox();
            public static extern Maker MakeMaker();
            public static extern Func<Box> MakeWork();
            public static extern Task<Box> MakeTask();
            public static extern Task<Task<Box>> MakeTaskTask();
        }
        public class Maker
        {
            public virtual async Task<Box> Make(Box p) { await Task.Yield(); return p; }
            public virtual Box MakeNow(Box p) => p;
        }

        """;

    public enum Producer { AsyncBody, TaskRun, FromResult, ValueTask, SetResult, SetFromTask, WaitAsync, Unwrap, WhenAllElement, WhenAny, ProjectModel }

    public enum Input { Known, Unseen, KnownOrUnseen }

    public enum Consumer { Await, Result }

    /// <summary>One cell: a producer handed an input, or, for <see cref="Visibility"/>, a producer whose callee, work or wrapped task is
    /// seen beside one the analysis does not see, handed the known input. <c>Maker</c>'s members are virtual, so an unseen receiver
    /// may run a body the analysis does not have.</summary>
    /// <param name="Producer">What completes the task.</param>
    /// <param name="Input">What the producer is handed or returns; <c>null</c> for a visibility cell.</param>
    /// <param name="Consumer">How the member consumes the task.</param>
    public sealed record Cell(Producer Producer, Input? Input, Consumer Consumer)
    {
        public bool Visibility => Input is null;

        public override string ToString() => $"{Producer}/{(Visibility ? "Visibility" : Input.ToString())}/{Consumer}";
    }

    private static readonly Producer[] VisibilityProducers =
        [Producer.AsyncBody, Producer.TaskRun, Producer.SetFromTask, Producer.WaitAsync, Producer.Unwrap, Producer.WhenAllElement, Producer.WhenAny];

    private static readonly Dictionary<string, string?> Twins = new(StringComparer.Ordinal);

    private static readonly Lazy<MetadataReference> Dependency = new(CompileDependency);

    public static IEnumerable<Cell> Cells() =>
        Enum.GetValues<Producer>().SelectMany(producer => Enum.GetValues<Input>().Select(input => (Producer: producer, Input: (Input?)input)))
            .Concat(VisibilityProducers.Select(producer => (Producer: producer, Input: (Input?)null)))
            .SelectMany(pair => Enum.GetValues<Consumer>().Select(consumer => new Cell(pair.Producer, pair.Input, consumer)));

    public static TheoryData<string> CellNames() => new(Cells().Select(cell => cell.ToString()));

    [Fact]
    public void The_matrix_has_every_axis_value_and_eighty_cells()
    {
        var cells = Cells().ToArray();

        Assert.Equal(80, cells.Length);
        Assert.Equal(80, cells.Select(cell => cell.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(66, cells.Count(cell => !cell.Visibility));
        Assert.Equal(14, cells.Count(cell => cell.Visibility));
        Assert.All(Enum.GetValues<Producer>(), producer => Assert.Contains(cells, cell => cell.Producer == producer));
        Assert.All(Enum.GetValues<Input>(), input => Assert.Contains(cells, cell => cell.Input == input));
        Assert.All(Enum.GetValues<Consumer>(), consumer => Assert.Contains(cells, cell => cell.Consumer == consumer));
        Assert.All(VisibilityProducers, producer => Assert.Contains(cells, cell => cell.Visibility && cell.Producer == producer));
    }

    [Theory]
    [MemberData(nameof(CellNames))]
    public void A_cell_generates_exactly_its_synchronous_twins_result(string name)
    {
        var cell = Cells().Single(candidate => candidate.ToString() == name);
        var twin = TwinResult(cell);
        var expected = twin is null ? null : cell.Consumer == Consumer.Await ? $"task({twin})" : twin;

        var trace = Generate(MemberSource(cell), cell.Producer == Producer.ProjectModel);
        var result = trace.Answer.Model?.Result?.ToString();

        Assert.True(expected == result, $"{cell}: the twin gives {expected ?? "no result"}, the cell {result ?? "no result"} " +
                                        $"({trace.Answer.Reason ?? trace.Answer.ModelReason}: {trace.Answer.Detail})");
        if (cell.Input == Input.Known)
            Assert.Equal(cell.Consumer == Consumer.Await ? "task([arg:x])" : "[arg:x]", result);
        else
            Assert.DoesNotContain("arg:x", result ?? "", StringComparison.Ordinal);
    }

    /// <summary>The value the producer of a cell is handed, as an expression of the member's <c>x</c> and <c>flag</c>.</summary>
    /// <param name="input">The input.</param>
    private static string Value(Input input) => input switch
    {
        Input.Known => "x",
        Input.Unseen => "Externals.MakeBox()",
        Input.KnownOrUnseen => "flag ? x : Externals.MakeBox()",
        _ => throw new ArgumentOutOfRangeException(nameof(input), input, null)
    };

    /// <summary>The member of a cell: the statements that build its task, and the task consumed once — twice for <c>WhenAny</c>, whose
    /// task completes with the task it picked — and indexed for <c>WhenAll</c>, whose task completes with an array.</summary>
    /// <param name="cell">The cell.</param>
    private static string MemberSource(Cell cell)
    {
        var value = cell.Input is { } input ? Value(input) : "x";
        // The task a wrapping producer wraps: one completing with the value, or beside it an unseen task of its type.
        var wrapped = cell.Visibility ? "(flag ? Task.FromResult(x) : Externals.MakeTask())" : $"Task.FromResult({value})";
        var (prefix, task) = cell.Producer switch
        {
            Producer.AsyncBody => ("", cell.Visibility ? "(flag ? new Maker() : Externals.MakeMaker()).Make(x)" : $"new Maker().Make({value})"),
            Producer.TaskRun => cell.Visibility
                ? ("Func<Box> work = () => x; ", "Task.Run(flag ? work : Externals.MakeWork())")
                : ("", $"Task.Run(() => {value})"),
            Producer.FromResult => ("", $"Task.FromResult({value})"),
            Producer.ValueTask => ("", $"new ValueTask<Box>({value})"),
            Producer.SetResult => ($"var source = new TaskCompletionSource<Box>(); source.SetResult({value}); ", "source.Task"),
            Producer.SetFromTask => ($"var source = new TaskCompletionSource<Box>(); source.SetFromTask({wrapped}); ", "source.Task"),
            Producer.WaitAsync => ("", $"{wrapped}.WaitAsync(CancellationToken.None)"),
            Producer.Unwrap => ("", cell.Visibility
                                        ? "(flag ? Task.FromResult(Task.FromResult(x)) : Externals.MakeTaskTask()).Unwrap()"
                                        : $"Task.FromResult(Task.FromResult({value})).Unwrap()"),
            Producer.WhenAllElement => ("", $"Task.WhenAll({wrapped})"),
            Producer.WhenAny => ("", $"Task.WhenAny({wrapped})"),
            Producer.ProjectModel => ("", $"Dependency.Tasks.Pass({value})"),
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        string Consume(string consumed) => cell.Consumer == Consumer.Await ? $"(await {consumed})" : $"({consumed}).Result";
        var final = cell.Producer switch
        {
            Producer.WhenAllElement => Consume(task) + "[0]",
            Producer.WhenAny => Consume(Consume(task)),
            _ => Consume(task)
        };
        var signature = cell.Consumer == Consumer.Await ? "public static async Task<Box> Run(Box x, bool flag)" : "public static Box Run(Box x, bool flag)";
        return $"public static class Cell {{ {signature} {{ {prefix}return {final}; }} }}";
    }

    /// <summary>The twin of a cell: the member returning <c>Box</c> and handing the value directly — the producer's input, or for a
    /// visibility cell the known input through the same unseen alternative: the receiver, the work, or an unseen value where the cell
    /// wraps an unseen task.</summary>
    /// <param name="cell">The cell.</param>
    private static string TwinSource(Cell cell)
    {
        var returned = cell.Input is { } input ? Value(input) : cell.Producer switch
        {
            Producer.AsyncBody => "(flag ? new Maker() : Externals.MakeMaker()).MakeNow(x)",
            Producer.TaskRun => "(flag ? work : Externals.MakeWork())()",
            _ => "flag ? x : Externals.MakeBox()"
        };
        var prefix = cell is { Visibility: true, Producer: Producer.TaskRun } ? "Func<Box> work = () => x; " : "";
        return $"public static class Cell {{ public static Box Run(Box x, bool flag) {{ {prefix}return {returned}; }} }}";
    }

    private static string? TwinResult(Cell cell)
    {
        var source = TwinSource(cell);
        lock (Twins)
        {
            if (!Twins.TryGetValue(source, out var result))
                Twins[source] = result = Generate(source, dependency: false).Answer.Model?.Result?.ToString();
            return result;
        }
    }

    /// <summary>The generator's trace for <see cref="MEMBER"/> of a library of the prelude, <see cref="TYPES"/> and the member's class;
    /// with <paramref name="dependency"/>, the library references a dependency whose project model says <c>Pass</c>'s task completes
    /// with its argument.</summary>
    /// <param name="cell">The member's class.</param>
    /// <param name="dependency">Whether the library calls the modelled dependency.</param>
    private static GenerationTrace Generate(string cell, bool dependency)
    {
        var source = GenerationRuns.PRELUDE + TYPES + cell + "\n}\n";
        IReadOnlyList<MetadataReference> references = dependency ? [.. EmittedAssemblies.RuntimeReferences, Dependency.Value] : EmittedAssemblies.RuntimeReferences;
        var library = LibraryCompilation.CompileTrees(GenerationRuns.ASSEMBLY,
                                                      [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), $"{GenerationRuns.ASSEMBLY}/Library.cs")],
                                                      references, CancellationToken.None);
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        var models = dependency ? DependencyModels(library.Compilation) : null;
        var trace = ModelGenerator.Trace(new GenerationRequest(GenerationRuns.ASSEMBLY, "1.0", MEMBER, null, null), library, CancellationToken.None, models);
        Assert.True(trace.Answer.Reason is null, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        return trace;
    }

    private static MetadataReference CompileDependency()
    {
        const string SOURCE = """
            using System.Threading.Tasks;
            namespace Dependency
            {
                public static class Tasks { public static Task<T> Pass<T>(T x) => Task.FromResult(x); }
            }
            """;
        var compilation = CSharpCompilation.Create(DEPENDENCY, [CSharpSyntaxTree.ParseText(SOURCE)], EmittedAssemblies.RuntimeReferences,
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    /// <summary>The built-in models and the dependency's project model, <c>Pass</c>'s result <c>task([arg:x])</c>.</summary>
    /// <param name="compilation">The library's compilation, which references the dependency.</param>
    private static LibraryModels DependencyModels(Compilation compilation)
    {
        const string MODELS = "{\"schemaVersion\":1,\"assemblies\":[\"" + DEPENDENCY + "\"],\"models\":[" +
                              "{\"member\":\"M:Dependency.Tasks.Pass``1(``0)\",\"effects\":{},\"result\":\"task([arg:x])\"}]}";
        var root = Directory.CreateTempSubdirectory("ch-completion-matrix-").FullName;
        try
        {
            var folder = Path.Combine(root, ".concurrency-hunter", "models");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "dependency.json"), MODELS, new UTF8Encoding(false));
            var files = ProjectModelFiles.Read(root);
            var (resolved, rejections) = ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(root, files));
            Assert.True(rejections.Count == 0, string.Join("\n", rejections));
            return resolved;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
