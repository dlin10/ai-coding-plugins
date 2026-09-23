using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Analysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class UserSliceOffsetTests
{
    private const string WindowClass = """
        public sealed class Window
        {
            public readonly int[] Data;
            public readonly int Offset;
            public Window(int[] data, int offset) { Data = data; Offset = offset; }
            public Window Slice(int start, int length) => new(Data, Offset + start);
            public ref int this[int index] => ref Data[Offset + index];
        }
        """;

    [Fact]
    public void Disjoint_windows_name_different_cells()
    {
        var run = AnalyzeCase("_state.View.Slice(0, 8)[0] = 1;", "_state.View.Slice(8, 8)[0] = 2;");

        Assert.Contains(run.Collection.Accesses, access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[0]" &&
                                                        access.Operation == ConcurrencyHunter.Analysis.AccessOperation.Write);
        Assert.Contains(run.Collection.Accesses, access => access.Root.Symbol.Contains("SecondWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[8]" &&
                                                        access.Operation == ConcurrencyHunter.Analysis.AccessOperation.Write);
        Assert.DoesNotContain(run.Pairs.Pairs, pair => pair.Resource.Selector is not null);
    }

    [Fact]
    public void Readonly_struct_window_names_different_cells()
    {
        var window = WindowClass.Replace("sealed class Window", "readonly struct Window", StringComparison.Ordinal);
        var run = AnalyzeCase("new Window(_state.Slots, 0).Slice(0, 8)[0] = 1;",
                              "new Window(_state.Slots, 0).Slice(8, 8)[0] = 2;", window);
        Assert.Contains(run.Collection.Accesses, access => access.Resource.Selector?.Text == "[8]");
        Assert.DoesNotContain(run.Pairs.Pairs, pair => pair.Resource.Selector is not null);
    }

    [Fact]
    public void Overlapping_windows_pair_at_the_same_cell()
    {
        var run = AnalyzeCase("_state.View.Slice(0, 8)[4] = 1;", "_state.View.Slice(4, 8)[0] = 2;");
        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.Selector?.Text == "[4]" &&
                                                pair.First.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                pair.Second.Root.Symbol.Contains("SecondWorker", StringComparison.Ordinal));
    }

    [Fact]
    public void Window_pairs_with_direct_cell()
    {
        var run = AnalyzeCase("_state.View.Slice(5, 8)[0] = 1;", "_state.Slots[5] = 2;");
        Assert.Contains(run.Collection.Accesses, access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[5]");
        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.Selector?.Text == "[5]");
    }

    [Fact]
    public void Mutable_offset_is_unknown()
    {
        var window = WindowClass.Replace("readonly int Offset", "int Offset", StringComparison.Ordinal);
        var run = AnalyzeCase("_state.View.Slice(0, 8)[0] = 1;", "_state.Slots[0] = 2;", window);
        Assert.Contains(run.Collection.Accesses, access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[?]");
        Assert.Contains(run.Pairs.Pairs, pair => pair.First.Resource.Selector?.Text == "[?]" &&
                                                pair.Second.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Unknown_slice_argument_is_unknown()
    {
        var run = AnalyzeCase("_state.View.Slice(_state.Unknown, 8)[0] = 1;", "_state.Slots[3] = 2;");
        Assert.Contains(run.Collection.Accesses, access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[?]");
    }

    [Fact]
    public void Unknown_constructor_argument_is_unknown()
    {
        var run = AnalyzeCase("_state.View[0] = 1;", "_state.Slots[3] = 2;", stateInitializer: "new Window(Slots, Unknown)");
        Assert.Contains(run.Collection.Accesses, access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[?]");
    }

    [Fact]
    public void Slice_call_used_in_offset_is_unknown()
    {
        var window = WindowClass.Replace("Offset + start", "Offset + new OffsetSource().Slice(start)", StringComparison.Ordinal) +
                     "public sealed class OffsetSource { public int Slice(int start) => start; }";
        var run = AnalyzeCase("_state.View.Slice(3, 8)[0] = 1;", "_state.Slots[3] = 2;", window);
        Assert.Contains(run.Collection.Accesses, access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[?]");
    }

    [Fact]
    public void Constructor_reading_its_own_field_is_unknown()
    {
        var window = WindowClass.Replace("Offset = offset;", "Offset = Offset + offset;", StringComparison.Ordinal);
        var run = AnalyzeCase("_state.View[0] = 1;", "_state.Slots[0] = 2;", window);
        Assert.Contains(run.Collection.Accesses, access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[?]");
    }

    [Fact]
    public void Indexer_ignoring_offset_uses_its_body()
    {
        var window = WindowClass.Replace("Data[Offset + index]", "Data[index]", StringComparison.Ordinal);
        var run = AnalyzeCase("_state.View.Slice(8, 8)[0] = 1;", "_state.Slots[0] = 2;", window);
        Assert.Contains(run.Collection.Accesses, access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[0]");
        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.Selector?.Text == "[0]");
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[8]");
    }

    [Fact]
    public void Chained_user_slices_add_their_offsets()
    {
        var run = AnalyzeCase("_state.View.Slice(2, 8).Slice(3, 5)[0] = 1;", "_state.Slots[5] = 2;");
        Assert.Contains(run.Collection.Accesses, access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[5]");
        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.Selector?.Text == "[5]");
    }

    [Fact]
    public void Selector_depth_budget_widens_cell()
    {
        var window = WindowClass.Replace("Offset + index", "Offset + (index + 1 + 1 + 1 + 1 + 1)", StringComparison.Ordinal);
        var run = AnalyzeCase("_state.View[0] = 1;", "_state.Slots[5] = 2;", window);
        Assert.Contains(run.Collection.Accesses, access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[?]");
    }

    [Fact]
    public async Task Constructor_argument_guard_separates_windows()
    {
        var source = Source("var i = _state.Unknown; if (i < 4) new Window(_state.Slots, i)[0] = 1;",
                            "var j = _state.Other; if (j >= 4) new Window(_state.Slots, j)[0] = 2;");
        var solution = FixtureSolution.Create(("Case.cs", source));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, ProviderRegistry.BuiltIn, CancellationToken.None);
        Assert.Equal(2, result.Accesses.Where(access => access.Resource.Selector is not null &&
                                                        access.Root.Symbol.EndsWith("Worker.ExecuteAsync(CancellationToken)", StringComparison.Ordinal))
                                       .Select(access => access.ExecutionId).Distinct().Count());
        Assert.DoesNotContain(result.Findings, finding => finding.Resource.Selector is not null &&
                                                          finding.AccessA.ExecutionId != finding.AccessB.ExecutionId);
    }

    [Fact]
    public void User_slice_over_span_proves_its_offset()
    {
        var window = """
            public readonly ref struct Window
            {
                public readonly Span<int> Data;
                public readonly int Offset;
                public Window(Span<int> data, int offset) { Data = data; Offset = offset; }
                public Window Slice(int start, int length) => new(Data, Offset + start);
                public ref int this[int index] => ref Data[Offset + index];
            }
            """;
        var run = AnalyzeCase("new Window(_state.Slots.AsSpan(), 0).Slice(8, 8)[0] = 1;",
                              "_state.Slots[8] = 2;", window, includeView: false);
        Assert.Contains(run.Collection.Accesses, access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[8]");
    }

    private static EngineRun AnalyzeCase(string first, string second, string? window = null, string stateInitializer = "new Window(Slots, 0)", bool includeView = true) =>
        Analyze(Source(first, second, window, stateInitializer, includeView));

    private static string Source(string first, string second, string? window = null, string stateInitializer = "new Window(Slots, 0)", bool includeView = true) => Usings + $$"""
        public sealed class State
        {
            public readonly int[] Slots = new int[16];
            public int Unknown;
            public int Other;
            {{(includeView ? $"public readonly Window View; public State() => View = {stateInitializer};" : "")}}
        }

        {{window ?? WindowClass}}

        public sealed class FirstWorker : BackgroundService
        {
            private readonly State _state;
            public FirstWorker(State state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{first}}
                return Task.CompletedTask;
            }
        }

        public sealed class SecondWorker : BackgroundService
        {
            private readonly State _state;
            public SecondWorker(State state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{second}}
                return Task.CompletedTask;
            }
        }

        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();");
}
