using System.Collections.Concurrent;
using System.Text.Json;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>Each automatic accessor runs and gives the same whole answer as an explicit-field twin in the same library (R1).</summary>
/// <param name="cache">The answers shared by this class's tests.</param>
public sealed class AutoAccessorGenerationMatrixTests(ClassCache cache) : IClassFixture<ClassCache>
{
    private enum Accessor { Get, Set, Init }
    private enum Storage { Instance, Static }
    private enum DeclaringType { Class, Struct, GenericClass }
    private enum Initializer { None, Initialized }

    private sealed record Cell(Accessor Accessor, Storage Storage, DeclaringType Type, Initializer Initializer)
    {
        public string Name => $"{Accessor}/{Storage}/{Type}/{Initializer}";
        public string TypeName => $"C_{Accessor}_{Storage}_{Type}_{Initializer}";
    }

    private sealed record Answers(GeneratedAnswer Automatic, GeneratedAnswer Twin);

    private IReadOnlyDictionary<Cell, Answers> Results => cache.Get("results", Run);

    [Fact]
    public void Every_cell_matches_its_explicit_twin()
    {
        var failures = new List<string>();
        // bounded: the matrix contains exactly 30 allowed cells.
        foreach (var (cell, answers) in Results.OrderBy(pair => pair.Key.Name, StringComparer.Ordinal))
        {
            var automatic = Comparable(answers.Automatic);
            var twin = Comparable(answers.Twin, text => Normalize(text, answers));
            if (automatic != twin)
                failures.Add($"{cell.Name}: auto {automatic}; explicit twin {twin}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void No_cell_is_refused_before_its_run()
    {
        var failures = Results.SelectMany(pair => new[] { pair.Value.Automatic, pair.Value.Twin }
                              .Where(RefusedBeforeRun)
                              .Select(answer => $"{pair.Key.Name} {answer.Member}: {answer.Reason}: {answer.Detail}"))
                              .ToArray();
        Assert.True(failures.Length == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        var cells = Cells().ToArray();
        Assert.Equal(30, cells.Length);
        Assert.Equal(cells.OrderBy(cell => cell.Name, StringComparer.Ordinal), Results.Keys.OrderBy(cell => cell.Name, StringComparer.Ordinal));
        Assert.Equal(Enum.GetValues<Accessor>(), cells.Select(cell => cell.Accessor).Distinct().Order());
        Assert.Equal(Enum.GetValues<Storage>(), cells.Select(cell => cell.Storage).Distinct().Order());
        Assert.Equal(Enum.GetValues<DeclaringType>(), cells.Select(cell => cell.Type).Distinct().Order());
        Assert.Equal(Enum.GetValues<Initializer>(), cells.Select(cell => cell.Initializer).Distinct().Order());
        Assert.DoesNotContain(cells, cell => cell.Accessor == Accessor.Init && cell.Storage == Storage.Static);
    }

    private static IEnumerable<Cell> Cells() =>
        from accessor in Enum.GetValues<Accessor>()
        from storage in Enum.GetValues<Storage>()
        from type in Enum.GetValues<DeclaringType>()
        from initializer in Enum.GetValues<Initializer>()
        let cell = new Cell(accessor, storage, type, initializer)
        where Allowed(cell)
        select cell;

    // C# forbids init accessors on static properties (CS8856).
    private static bool Allowed(Cell cell) => cell.Accessor != Accessor.Init || cell.Storage != Storage.Static;

    private static IReadOnlyDictionary<Cell, Answers> Run()
    {
        var cells = Cells().ToArray();
        var source = "namespace Lib { public class Item { public object Value; }\n" +
                     string.Join("\n", cells.Select(Source)) + "\n}";
        var library = Compile(source);
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        Assert.Equal(0, library.ExternBodies);
        var answers = new ConcurrentDictionary<Cell, Answers>();
        Parallel.ForEach(cells, CellParallelism, cell =>
        {
            var type = library.Compilation.GetTypeByMetadataName($"Lib.{cell.TypeName}{(cell.Type == DeclaringType.GenericClass ? "`1" : "")}")!;
            GeneratedAnswer Generate(string name)
            {
                var property = type.GetMembers(name).OfType<IPropertySymbol>().Single();
                var accessor = cell.Accessor == Accessor.Get ? property.GetMethod! : property.SetMethod!;
                var request = new GenerationRequest(ASSEMBLY, "1.0", accessor.GetDocumentationCommentId()!, null, null);
                return ModelGenerator.Trace(request, library, CancellationToken.None).Answer;
            }
            answers[cell] = new Answers(Generate("P"), Generate("PTwin"));
        });
        return answers;
    }

    private static string Source(Cell cell)
    {
        var modifier = cell.Storage == Storage.Static ? "static " : "";
        var declaration = cell.Type == DeclaringType.Struct ? "struct" : "sealed class";
        var parameter = cell.Type == DeclaringType.GenericClass ? "<T>" : "";
        var initializer = cell.Initializer == Initializer.Initialized ? " = new Item();" : ";";
        var automaticInitializer = cell.Initializer == Initializer.Initialized ? " = new Item();" : "";
        var write = cell.Accessor == Accessor.Init ? "init" : "set";
        var automatic = cell.Accessor == Accessor.Get ? "get;" : $"get; {write};";
        var twin = cell.Accessor == Accessor.Get ? "get => _twin;" : $"get => _twin; {write} => _twin = value;";
        // A struct with field initializers must declare a constructor (CS8983).
        var constructor = cell.Type == DeclaringType.Struct && cell.Initializer == Initializer.Initialized ? $"public {cell.TypeName}() {{ }}" : "";
        return $$"""
            public {{declaration}} {{cell.TypeName}}{{parameter}}
            {
                private {{modifier}}Item _twin{{initializer}}
                public {{modifier}}Item P { {{automatic}} }{{automaticInitializer}}
                public {{modifier}}Item PTwin { {{twin}} }
                {{constructor}}
            }
            """;
    }

    private static bool RefusedBeforeRun(GeneratedAnswer answer) => answer.Reason is
        GenerationReasons.NOT_A_CANDIDATE or GenerationReasons.DRIVER_NOT_SYNTHESIZED or GenerationReasons.BODY_DOES_NOT_COMPILE or
        GenerationReasons.CLOSURE_BOUND or GenerationReasons.ANALYSIS_FAILED;

    private static (string? Reason, string? ModelReason, string? Classified, string? Entry) Comparable(GeneratedAnswer answer,
                                                                                                    Func<string, string>? normalize = null)
    {
        var classified = answer.Classified is { } fates ? JsonSerializer.Serialize(fates.OrderBy(pair => pair.Key, StringComparer.Ordinal)) : null;
        var entry = answer.Model is { } model ? ModelEntryWriter.Write(model).ToJsonString() : null;
        return (answer.Reason, answer.ModelReason,
                classified is null ? null : normalize?.Invoke(classified) ?? classified,
                entry is null ? null : normalize?.Invoke(entry) ?? entry);
    }

    private static string Normalize(string text, Answers answers) =>
        text.Replace(answers.Twin.Member, answers.Automatic.Member, StringComparison.Ordinal)
            .Replace("PTwin", "P", StringComparison.Ordinal)
            .Replace("_twin", "<P>k__BackingField", StringComparison.Ordinal);
}
