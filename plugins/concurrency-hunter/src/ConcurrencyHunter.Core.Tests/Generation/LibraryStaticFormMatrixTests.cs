using System.Collections.Concurrent;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The static forms keep the running answer in inert and off generation, except for missing recipe values and work not run.</summary>
/// <param name="cache">The matrix's cached answers.</param>
public sealed class LibraryStaticFormMatrixTests(ClassCache cache) : IClassFixture<ClassCache>
{
    private enum Form { RecipeValue, PathSeed, Unbuildable, GenericOverProbe, GenericOverLibrary, HandedInInitializer,
                        HandedInHelper, WorkInInitializer, WorkInHelper }
    private enum Access { Read, Write }
    private sealed record Cell(Form Form, TypeInitializerTreatment Mode, Access Access);

    private IReadOnlyDictionary<Cell, GeneratedAnswer> Results => cache.Get("results", Run);

    private static IEnumerable<Cell> Cells() =>
        from form in Enum.GetValues<Form>()
        from mode in Enum.GetValues<TypeInitializerTreatment>()
        from access in Enum.GetValues<Access>()
        select new Cell(form, mode, access);

    [Fact]
    public void Every_cell_gets_its_expected_answer()
    {
        var failures = new List<string>();
        foreach (var (cell, answer) in Results)
        {
            try
            {
                Assert.Equal(cell.Mode.ToString().ToLowerInvariant(), answer.Generation.Mode);
                Assert.Null(answer.Reason);
                if (cell.Mode == TypeInitializerTreatment.Off && cell.Form is Form.Unbuildable or Form.WorkInInitializer or Form.WorkInHelper)
                {
                    Assert.Null(answer.Model);
                    Assert.Equal(ModelReasons.INCOMPLETE, answer.ModelReason);
                    var code = cell.Form == Form.Unbuildable ? ModelCauses.UNSEEDED_READ : ModelCauses.INITIALIZER_WORK_NOT_RUN;
                    Assert.Equal([code], answer.Causes.Select(cause => cause.Code));
                    Assert.Contains(cell.Form == Form.Unbuildable ? "s_box" : "Api.#cctor", Assert.Single(answer.Causes).Detail);
                }
                else
                {
                    var running = Results[cell with { Mode = TypeInitializerTreatment.Run }];
                    Assert.Equal(Answer(running), Answer(answer));
                    if (cell.Form is Form.HandedInInitializer or Form.HandedInHelper)
                    {
                        Assert.Null(answer.Model);
                        Assert.Equal(ModelReasons.LIBRARY_STATE, answer.ModelReason);
                        Assert.Equal([ModelCauses.LIBRARY_STATE_STORE], answer.Causes.Select(cause => cause.Code));
                        // A write has two sources of this code; the answer keeps one witness per code.
                        if (cell.Access == Access.Read)
                            Assert.StartsWith($"static:{ASSEMBLY}:Lib.State in ", Assert.Single(answer.Causes).Detail);
                    }
                    if (cell.Access == Access.Write && cell.Form is Form.RecipeValue or Form.GenericOverProbe or Form.GenericOverLibrary)
                    {
                        Assert.Null(answer.Model);
                        Assert.Equal(ModelReasons.LIBRARY_STATE, answer.ModelReason);
                        Assert.Equal([ModelCauses.LIBRARY_STATE_STORE], answer.Causes.Select(cause => cause.Code));
                    }
                }
            }
            catch (Exception error) { failures.Add($"{cell}: {error}\nAnswer: {System.Text.Json.JsonSerializer.Serialize(answer)}"); }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Order()));
    }

    [Fact]
    public void No_cell_gives_a_model_where_running_initializers_refuse()
    {
        foreach (var (cell, answer) in Results.Where(pair => pair.Key.Mode != TypeInitializerTreatment.Run))
        {
            var running = Results[cell with { Mode = TypeInitializerTreatment.Run }];
            Assert.True(running.Model is not null || answer.Model is null,
                        $"{cell}: run {Answer(running)}; {cell.Mode} {Answer(answer)}");
        }
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        var cells = Cells().ToArray();
        Assert.Equal(Enum.GetValues<Form>(), cells.Select(cell => cell.Form).Distinct().Order());
        Assert.Equal(Enum.GetValues<TypeInitializerTreatment>(), cells.Select(cell => cell.Mode).Distinct().Order());
        Assert.Equal(Enum.GetValues<Access>(), cells.Select(cell => cell.Access).Distinct().Order());
        Assert.Equal(54, cells.Length);
        Assert.Equal(cells.Length, Results.Count);
    }

    [Fact]
    public void An_initializer_handing_a_region_that_holds_a_seed_differs_from_run_only_in_its_refusal()
    {
        var cell = new Cell(Form.HandedInInitializer, TypeInitializerTreatment.Run, Access.Read);
        var library = Compile(Source(cell, holdsSeed: true));
        Assert.NotNull(library.Compilation);
        Assert.Empty(library.Errors);
        Assert.All(library.ExternMembers, id => Assert.Contains("Ext.Take", id));
        GeneratedAnswer? running = null;
        foreach (var mode in Enum.GetValues<TypeInitializerTreatment>())
        {
            var trace = ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", "M:Lib.Api.Run(System.Action)", null, null),
                                             library, CancellationToken.None, initializers: mode);
            var answer = trace.Answer;
            Assert.True(trace.Run is { Stopped: false });
            Assert.Contains(trace.Driver!.SeedStatements, seed => seed.Path.EndsWith("s_box/F:Lib.Box.A", StringComparison.Ordinal));
            Assert.Equal(mode.ToString().ToLowerInvariant(), answer.Generation.Mode);
            Assert.Null(answer.Reason);
            Assert.Null(answer.Model);
            var cause = Assert.Single(answer.Causes);
            if (mode == TypeInitializerTreatment.Run)
            {
                running = answer;
                Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
                Assert.Equal(ModelCauses.PROBE_HANDED_TO_UNSEEN, cause.Code);
            }
            else
            {
                Assert.Equal(running!.Classified, answer.Classified);
                Assert.Equal(ModelReasons.LIBRARY_STATE, answer.ModelReason);
                Assert.Equal(ModelCauses.LIBRARY_STATE_STORE, cause.Code);
                Assert.StartsWith($"static:{ASSEMBLY}:Lib.Api in ", cause.Detail);
            }
        }
    }

    private static IReadOnlyDictionary<Cell, GeneratedAnswer> Run()
    {
        var answers = new ConcurrentDictionary<Cell, GeneratedAnswer>();
        Parallel.ForEach(Cells().GroupBy(cell => (cell.Form, cell.Access)), CellParallelism, group =>
        {
            var library = Compile(Source(group.First()));
            Assert.NotNull(library.Compilation);
            Assert.Empty(library.Errors);
            Assert.All(library.ExternMembers, id => Assert.Contains("Ext.Take", id));
            var generic = group.Key.Form == Form.GenericOverProbe;
            var member = "M:Lib.Api.Run" + (generic ? "``1" : "") + "(System.Action)";
            foreach (var cell in group)
            {
                var trace = ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", member, null, null), library,
                                                 CancellationToken.None, initializers: cell.Mode);
                Assert.True(trace.Run is { Stopped: false }, $"{cell}: {trace.Answer.Reason}: {trace.Answer.Detail}");
                Assert.All(trace.Answer.Generation.Attempts, attempt =>
                {
                    Assert.InRange(attempt.ReachedBodies, 1, 1500);
                    if (attempt.ConfirmationBodies is { } confirmation)
                        Assert.InRange(confirmation, 1, 1500);
                });
                if (cell.Form == Form.PathSeed)
                    Assert.Contains(trace.Driver!.SeedStatements, seed => seed.Path.Contains("s_opts/F:Lib.Options.Value", StringComparison.Ordinal));
                if (cell.Mode != TypeInitializerTreatment.Off && cell.Form is Form.WorkInInitializer or Form.WorkInHelper)
                    Assert.Contains(trace.Driver!.SeedStatements, seed => seed.Path.EndsWith("s_hook", StringComparison.Ordinal));
                answers[cell] = trace.Answer;
            }
        });
        return answers;
    }

    private static string Answer(GeneratedAnswer answer) =>
        string.Join(";", answer.Classified?.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}") ?? []) + "|" +
        answer.Reason + "|" + answer.ModelReason + "|" + (answer.Model is { } model ? ModelEntryWriter.Write(model).ToJsonString() : "null") + "|" +
        string.Join(";", answer.Causes.Select(cause => cause.Code));

    private static string Source(Cell cell, bool holdsSeed = false)
    {
        var storage = cell.Form switch
        {
            Form.PathSeed => cell.Access == Access.Read ? "s_opts.Count" : "s_opts.A",
            Form.Unbuildable => "s_box.Value",
            Form.GenericOverProbe => "Registry<T>.s_box." + (cell.Access == Access.Read ? "Value" : "A"),
            Form.GenericOverLibrary => "Registry<int>.s_box." + (cell.Access == Access.Read ? "Value" : "A"),
            _ => "s_box." + (cell.Access == Access.Read ? "Value" : "A")
        };
        var fields = cell.Form switch
        {
            Form.PathSeed => "private static Options s_opts = new Options();",
            Form.Unbuildable => "private static IBox s_box = new HiddenBox(); private sealed class HiddenBox : IBox { public int Value => 0; public Action A; public void Store(Action a) { A = a; } }",
            Form.GenericOverProbe or Form.GenericOverLibrary => "",
            _ => "private static Box s_box = new Box();"
        };
        var operation = cell.Form is Form.HandedInInitializer or Form.HandedInHelper
            ? "Ext.Take(ref " + (holdsSeed ? "s_state" : "State.s_state") + ");"
            : "_ = Task.Run(() => Hooks.s_hook());";
        var initialization = cell.Form switch
        {
            Form.HandedInInitializer or Form.WorkInInitializer => "static Api() { " + operation + " }",
            Form.HandedInHelper or Form.WorkInHelper => "static Api() { Helper(); } private static void Helper() { " + operation + " }",
            _ => ""
        };
        var access = cell.Access == Access.Read ? "a(); return " + storage + ";" : storage + " = a;";
        if (cell.Form == Form.Unbuildable && cell.Access == Access.Write)
            access = "s_box.Store(a);";
        // Both accesses traverse the interface field whose value is seeded by its static path.
        if (cell.Form == Form.PathSeed)
            access = "_ = s_opts.Value; " + access;
        return $$"""
            using System;
            using System.Threading.Tasks;
            namespace Lib {
                public sealed class Box { public int Value; public Action A; public Box() { } }
                public interface IValue { }
                public sealed class Options { public IValue Value; public int Count; public Action A; }
                public interface IBox { int Value { get; } void Store(Action a); }
                public static class Registry<T> { public static Box s_box = new Box(); }
                public static class Ext { public static extern void Take(ref int value); }
                public static class Hooks { public static Action s_hook; }
                public static class State { public static int s_state; }
                public static class Api {
                    {{fields}}
                    {{(holdsSeed ? "private static int s_state;" : "")}}
                    {{initialization}}
                    public static {{(cell.Access == Access.Read ? "int" : "void")}} Run{{(cell.Form == Form.GenericOverProbe ? "<T>" : "")}}(Action a) {
                        {{access}}
                    }
                }
            }
            """;
    }
}
