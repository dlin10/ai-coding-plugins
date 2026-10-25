using System.Collections.Concurrent;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>Sharing a helper with an inert initializer changes neither the generation's effects nor its handoffs, witnesses or placed values.</summary>
/// <param name="cache">The matrix's cached answers.</param>
public sealed class InertInitializerSharingMatrixTests(ClassCache cache) : IClassFixture<ClassCache>
{
    public enum Sharer { Setup, Call }
    public enum HelperDoes { StoresIntoInput, HandsProbeToUnseen, ReadsUnseededField, PlacesValue, ConventionCall }
    public enum Initializer { CallsHelper, DoesNot }

    /// <summary>One combination of the sharing and helper behavior axes.</summary>
    /// <param name="Sharer">The ordinary caller of the helper.</param>
    /// <param name="HelperDoes">The helper's operation.</param>
    /// <param name="Initializer">Whether the initializer also calls it.</param>
    public sealed record Cell(Sharer Sharer, HelperDoes HelperDoes, Initializer Initializer);

    private IReadOnlyDictionary<Cell, GeneratedAnswer> Results => cache.Get("results", Run);

    [Fact]
    public void Every_cell_answers_as_without_the_initializer()
    {
        foreach (var cell in Cells().Where(cell => cell.Initializer == Initializer.CallsHelper))
        {
            var with = Results[cell];
            var without = Results[cell with { Initializer = Initializer.DoesNot }];
            Assert.True(Answer(without) == Answer(with), $"{cell}: without {Answer(without)}; with {Answer(with)}");
        }
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        var cells = Cells().ToArray();
        Assert.Equal(Enum.GetValues<Sharer>(), cells.Select(cell => cell.Sharer).Distinct().Order());
        Assert.Equal(Enum.GetValues<HelperDoes>(), cells.Select(cell => cell.HelperDoes).Distinct().Order());
        Assert.Equal(Enum.GetValues<Initializer>(), cells.Select(cell => cell.Initializer).Distinct().Order());
        Assert.Equal(20, cells.Length);
    }

    private static IEnumerable<Cell> Cells() =>
        from sharer in Enum.GetValues<Sharer>()
        from does in Enum.GetValues<HelperDoes>()
        from initializer in Enum.GetValues<Initializer>()
        select new Cell(sharer, does, initializer);

    private static IReadOnlyDictionary<Cell, GeneratedAnswer> Run()
    {
        var answers = new ConcurrentDictionary<Cell, GeneratedAnswer>();
        Parallel.ForEach(Cells(), CellParallelism, cell =>
        {
            var trace = Trace(Source(cell), "M:Lib.Api.Run(System.Action)");
            Assert.True(trace.Run is { Stopped: false }, $"{cell}: {trace.Answer.Reason}: {trace.Answer.Detail}");
            var helpers = trace.Run.Heap!.Instances.Values.Where(instance => instance.BodyId.EndsWith(":M:Lib.Api.Helper", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(helpers);
            var roles = new DriverExecutions(trace.Driver!, trace.Run.Executions!, new HashSet<string>(StringComparer.Ordinal));
            var action = cell.Sharer == Sharer.Setup ? DriverSynthesizer.SETUP : DriverSynthesizer.CALL;
            var memberships = helpers.Select(helper => DriverExecutions.ForInstance(trace.Run.Executions, helper.Id, includeInert: true)).ToArray();
            Assert.Contains(memberships, membership => membership.Any(execution => roles.InTree(execution, action)) &&
                (cell.Initializer != Initializer.CallsHelper || membership.Any(execution => trace.Run.Executions!.IsInert(execution))));
            if (cell.Initializer == Initializer.DoesNot)
                Assert.DoesNotContain(memberships.SelectMany(membership => membership), execution => trace.Run.Executions!.IsInert(execution));
            if (cell.HelperDoes == HelperDoes.ReadsUnseededField)
                Assert.Contains(trace.Answer.Generation.Unseeded, field => field.EndsWith(".Value", StringComparison.Ordinal));
            answers[cell] = trace.Answer;
        });
        return answers;
    }

    /// <summary>The observable generation answer, leaving out bodies, timings and implementation details.</summary>
    /// <param name="answer">The answer.</param>
    private static string Answer(GeneratedAnswer answer) =>
        string.Join(";", answer.Classified?.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}") ?? []) + "|" +
        answer.Reason + "|" + answer.ModelReason + "|" + (answer.Model is { } model ? ModelEntryWriter.Write(model).ToJsonString() : "null") + "|" +
        string.Join(";", answer.Causes.Select(cause => cause.Code));

    /// <summary>The same helper instance belongs to setup or the call, and optionally to the initializer as well.</summary>
    /// <param name="cell">The matrix cell.</param>
    private static string Source(Cell cell)
    {
        var operation = cell.HelperDoes switch
        {
            HelperDoes.StoresIntoInput => "_input.Value = _input.Other;",
            HelperDoes.HandsProbeToUnseen => "Sink.Take(_input.Probe);",
            HelperDoes.ReadsUnseededField => "_ = _input.Part.Value;",
            HelperDoes.PlacesValue => "_placed = _input.Value;",
            _ => "_ = _input.User.ToString();"
        };
        return $$"""
            public struct Part { public object Value; }
            public abstract class User { public abstract override string ToString(); }
            public sealed class Input { public object Value; public object Other; public Action Probe; public Part Part { get; set; } public User User; }
            public sealed class Api
            {
                private static Api Current;
                private readonly Input _input;
                private object _placed;
                public Api(Input input) { _input = input; Current = this; {{(cell.Sharer == Sharer.Setup ? "Helper();" : "")}} }
                // Both fixtures load the same static storage, so seed discovery is identical.
                private static class Init { static Init() { _ = Current; {{(cell.Initializer == Initializer.CallsHelper ? "Current.Helper();" : "")}} } public static void Go() { } }
                private void Helper() { {{operation}} }
                public object Run(Action done) { Init.Go(); done(); {{(cell.Sharer == Sharer.Call ? "Helper();" : "")}} return {{(cell.HelperDoes == HelperDoes.PlacesValue ? "_placed" : "_input.Value")}}; }
            }
            """;
    }
}
