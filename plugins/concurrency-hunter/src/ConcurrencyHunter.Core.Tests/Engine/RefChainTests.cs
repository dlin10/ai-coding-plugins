using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class RefChainTests
{
    [Fact]
    public void Ref_parameter_forwarded_nine_times_reaches_its_field()
    {
        var methods = string.Join(Environment.NewLine, Enumerable.Range(1, 9).Select(index => index == 9
            ? "public static void Step9(ref int value) => value++;"
            : $"public static void Step{index}(ref int value) => Step{index + 1}(ref value);"));
        var run = Run("State.Step1(ref _state.Cell);", methods);

        Assert.Contains(Writes(run), access => access.Resource.Member.Name == "Cell");
        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.Member.Name == "Cell");
    }

    [Fact]
    public void Ref_returned_through_nine_calls_reaches_its_field()
    {
        var methods = string.Join(Environment.NewLine, Enumerable.Range(1, 9).Select(index => index == 9
            ? "public ref int Step9() => ref Cell;"
            : $"public ref int Step{index}() => ref Step{index + 1}();"));
        var run = Run("_state.Step1() = 1;", methods);

        Assert.Contains(Writes(run), access => access.Resource.Member.Name == "Cell");
        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.Member.Name == "Cell");
    }

    [Fact]
    public void Recursive_ref_forwarding_terminates()
    {
        var run = Run("State.F(ref _state.Cell, _state.Depth);",
                      "public static void F(ref int value, int n) { if (n == 0) value++; else F(ref value, n - 1); }");

        Assert.Contains(Writes(run), access => access.Resource.Member.Name == "Cell");
    }

    [Fact]
    public void Recursive_ref_return_terminates()
    {
        var run = Run("_state.F(_state.Depth) = 1;",
                      "public ref int F(int n) { if (n == 0) return ref Cell; return ref F(n - 1); }");

        Assert.Contains(Writes(run), access => access.Resource.Member.Name == "Cell");
    }

    [Fact]
    public void Mutual_ref_return_recursion_terminates()
    {
        var run = Run("_state.F(_state.Depth) = 1;", """
            public ref int F(int n) { if (n == 0) return ref Cell; return ref G(n - 1); }
            public ref int G(int n) => ref F(n);
            """);

        Assert.Contains(Writes(run), access => access.Resource.Member.Name == "Cell");
    }

    [Fact]
    public void Recursive_ref_return_with_swapped_arguments_reaches_both_locations()
    {
        var run = Run("State.F(ref _state.Cell, ref _state.Other, _state.Depth) = 1;", """
            public static ref int F(ref int a, ref int b, int n)
            { if (n == 0) return ref a; return ref F(ref b, ref a, n - 1); }
            """);

        Assert.Contains(Writes(run), access => access.Resource.Member.Name == "Cell");
        Assert.Contains(Writes(run), access => access.Resource.Member.Name == "Other");
    }

    [Fact]
    public void Recursive_ref_return_of_a_cell_indexed_by_its_depth_is_the_unknown_cell()
    {
        var run = Run("_state.F(_state.Depth) = 1;",
                      "public ref int F(int n) { if (n == 0) return ref Slots[n]; return ref F(n - 1); }");

        AssertUnknown(Cells(run));
    }

    [Fact]
    public void Recursive_ref_return_of_a_constant_cell_keeps_its_index()
    {
        var run = Run("_state.F(_state.Depth) = 1;",
                      "public ref int F(int n) { if (n == 0) return ref Slots[0]; return ref F(n - 1); }");

        AssertExact(Cells(run), "[0]");
    }

    [Fact]
    public void Recursive_ref_return_of_a_constant_element_of_a_parameter_array_keeps_its_index()
    {
        var run = Run("State.F(_state.Slots, _state.Depth) = 1;",
                      "public static ref int F(int[] a, int n) { if (n == 0) return ref a[0]; return ref F(a, n - 1); }");

        AssertExact(Cells(run), "[0]");
    }

    [Fact]
    public void Recursive_return_of_a_parameter_element_is_the_unknown_cell_of_the_bound_collection()
    {
        var run = Run("State.F(_state.Slots, _state.Depth) = 1;",
                      "public static ref int F(int[] a, int n) { if (n == 0) return ref a[n]; return ref F(a, n - 1); }");

        AssertUnknown(Cells(run));
    }

    [Fact]
    public void Recursive_ref_return_through_a_sliced_span_ends_with_the_unknown_cell()
    {
        var run = Span("a.Slice(1)");

        AssertUnknown(Cells(run));
    }

    [Fact]
    public void Recursive_ref_return_through_a_slice_of_known_length_is_a_range_of_the_outer_collection()
    {
        var run = Span("a.Slice(1, 2)");
        var cells = Cells(run);

        Assert.NotEmpty(cells);
        Assert.All(cells, access =>
        {
            Assert.NotEqual(ElementSelector.Unknown, access.Resource.Selector);
            Assert.True(access.Resource.Selector!.IsProven);
            if (access.Resource.Selector.IsExact)
            {
                if (access.SelectorTerm is { } term)
                {
                    var constant = Assert.IsType<ConstantTerm>(term);
                    Assert.Equal(ElementSelector.Exact(constant.Value), access.Resource.Selector);
                }
            }
            else
            {
                Assert.Matches(@"^\[-?\d+\.\.-?\d+\]$", access.Resource.Selector.Text);
                Assert.Null(access.SelectorTerm);
            }
        });
        Assert.Contains(cells, access => access.Resource.Selector!.MayOverlap(ElementSelector.Exact(0)));
        Assert.Contains(cells, access => access.Resource.Selector!.MayOverlap(ElementSelector.Exact(1)));
    }

    [Fact]
    public void Recursive_ref_return_through_a_slice_from_zero_keeps_its_constant_index()
    {
        var run = Span("a.Slice(0, 2)");

        AssertExact(Cells(run), "[0]");
    }

    [Fact]
    public void Constant_shift_outside_a_cycle_keeps_the_index_exact()
    {
        var run = Run("State.F(_state.Slots, _state.Depth) = 1;", """
            public static ref int F(Span<int> a, int n)
            { if (n == 0) return ref H(a.Slice(1)); return ref F(a, n - 1); }
            public static ref int H(Span<int> s) => ref s[0];
            """);

        AssertExact(Cells(run), "[1]");
    }

    [Fact]
    public void Recursive_ref_return_through_a_collection_a_call_returns_is_the_unknown_cell()
    {
        var run = Run("State.F(_state.Slots, _state.Depth) = 1;", """
            public static ref int F(int[] a, int n) { if (n == 0) return ref a[0]; return ref F(Same(a), n - 1); }
            public static int[] Same(int[] b) => b;
            """);

        AssertUnknown(Cells(run));
        Assert.Contains(Cells(run), access => access.Resource.Selector == ElementSelector.Exact(0));
    }

    [Fact]
    public void Recursive_ref_return_through_a_call_on_a_call_receiver_widens_as_today()
    {
        var run = Run("_state.F(_state.Depth) = 1;", """
            public ref int F(int n) { if (n == 0) return ref Slots[0]; return ref Self().F(n - 1); }
            public State Self() => this;
            public ref int G() => ref Slots[0];
            """, "_state.Slots[1] = 2;");
        var plain = Run("_state.Self().G() = 1;", "public State Self() => this; public ref int G() => ref Slots[0];",
                        "_state.Slots[1] = 2;");

        var plainCell = Assert.Single(Cells(plain));
        Assert.Equal(new ConstantTerm(0, 32, true), plainCell.SelectorTerm);
        Assert.Equal(ElementSelector.Exact(0), plainCell.Resource.Selector);
        var cells = Cells(run);
        Assert.Contains(cells, access => access.Resource.Selector == plainCell.Resource.Selector);
        Assert.All(cells, access => Assert.Equal(plainCell.SelectorTerm, access.SelectorTerm));
        foreach (var result in new[] { plain, run })
            Assert.DoesNotContain(result.Pairs.Pairs, pair => pair.Resource.Member.Name == "Slots" &&
                                                            pair.First.Root.Symbol != pair.Second.Root.Symbol);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("Slots")]
    public void Recursive_ref_return_of_a_parameter_cell_on_a_call_receiver_keeps_its_constant_term(string argument)
    {
        var run = Run("_state.F(_state.Slots, _state.Depth) = 1;", $$"""
            public ref int F(int[] a, int n) { if (n == 0) return ref a[2]; return ref Self().F({{argument}}, n - 1); }
            public State Self() => this;
            """, "_state.Slots[1] = 2; _state.Slots[2] = 2;");
        var cells = Cells(run);

        Assert.NotEmpty(cells);
        Assert.All(cells, access => Assert.Equal(new ConstantTerm(2, 32, true), access.SelectorTerm));
        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.Member.Name == "Slots" &&
                                               pair.First.Root.Symbol != pair.Second.Root.Symbol);
        Assert.DoesNotContain(run.Pairs.Pairs, pair => pair.Resource.Member.Name == "Slots" &&
                                                     pair.First.Root.Symbol != pair.Second.Root.Symbol &&
                                                     (pair.First.Resource.Selector == ElementSelector.Exact(1) ||
                                                      pair.Second.Resource.Selector == ElementSelector.Exact(1)));
    }

    [Fact]
    public void Widened_cell_drops_its_term_and_its_pair_stays()
    {
        var run = Run("State.F(_state.Slots, _state.Depth) = 1;",
                      "public static ref int F(int[] a, int n) { if (n == 0) return ref a[n]; return ref F(a, n - 1); }");

        AssertUnknown(Cells(run));
        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.Member.Name == "Slots" &&
                                               pair.First.Root.Symbol != pair.Second.Root.Symbol);
    }

    [Fact]
    public void Ref_returned_through_an_intermediate_call_binds_its_index_in_its_own_frame()
    {
        var run = Run("State.Outer(_state, 7, 2) = 1;", """
            public static ref int Outer(State s, int ignored, int j) => ref Inner(s, j);
            public static ref int Inner(State s, int i) => ref s.Slots[i];
            """);

        Assert.NotEmpty(Cells(run));
        Assert.All(Cells(run), access => Assert.Equal(new ConstantTerm(2, 32, true), access.SelectorTerm));
    }

    [Fact]
    public void Field_path_past_the_depth_limit_still_collapses_to_a_wildcard()
    {
        var run = Solve("""
            public sealed class Node { public Node Next; public object Value; }
            public class ChainController(Node node) : ControllerBase
            { public void Post() { GC.KeepAlive(node.Next.Next.Next.Next.Next.Next.Next.Next.Next.Value); } }
            """ + Startup("services.AddSingleton<Node>();"));
        var instance = Assert.Single(run.Instances("body:Fixture:M:ChainController.Post"));

        Assert.Contains(instance.Summary.OpaqueCalls.SelectMany(call => call.Arguments).SelectMany(argument => argument.Values),
                        value => value is PathValue { IsWildcard: true });
    }

    private static EngineRun Span(string slice) => Run("State.F(_state.Slots, _state.Depth) = 1;",
        $"public static ref int F(Span<int> a, int n) {{ if (n == 0) return ref a[0]; return ref F({slice}, n - 1); }}");

    private static IReadOnlyList<Access> Writes(EngineRun run) => run.Collection.Accesses
        .Where(access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                         access.Operation is AccessOperation.Write or AccessOperation.ReadModifyWrite).ToArray();

    private static IReadOnlyList<Access> Cells(EngineRun run) => Writes(run).Where(access => access.Resource.Member.Name == "Slots").ToArray();

    private static void AssertUnknown(IReadOnlyList<Access> cells)
    {
        var unknown = cells.Where(access => access.Resource.Selector == ElementSelector.Unknown).ToArray();
        Assert.NotEmpty(unknown);
        Assert.Contains(unknown, access => access.SelectorTerm is null);
    }

    private static void AssertExact(IReadOnlyList<Access> cells, string selector)
    {
        Assert.NotEmpty(cells);
        Assert.All(cells, access => Assert.Equal(selector, access.Resource.Selector?.Text));
    }

    private static EngineRun Run(string first, string members, string second = "_state.Cell = 2; _state.Slots[0] = 2;") => Analyze(Usings + $$"""
        public sealed class State
        {
            public int Cell;
            public int Other;
            public int Depth;
            public int[] Slots = new int[16];
            {{members}}
        }
        public sealed class FirstWorker(State _state) : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            { {{first}} return Task.CompletedTask; }
        }
        public sealed class SecondWorker(State _state) : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            { {{second}} return Task.CompletedTask; }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();"));
}
