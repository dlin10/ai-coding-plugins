using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class CallSiteGuardTests
{
    [Fact]
    public async Task A_field_value_guard_names_the_index_in_its_body()
    {
        var result = await Analyze("var i = _state.Left; if (i < 4) _state.Slots[i] = 1;",
                                   "var j = _state.Right; if (j >= 4) _state.Slots[j] = 2;");
        AssertTwoWorkersAccess(result);
        Assert.Empty(CellFindings(result));
    }

    [Fact]
    public async Task A_call_site_guard_binds_the_value_passed_to_a_helper()
    {
        var result = await Analyze("var i = _state.Left; if (i < 4) _state.Write(i);",
                                   "var j = _state.Right; if (j >= 4) _state.Write(j);");
        AssertTwoWorkersAccess(result);
        Assert.Empty(CellFindings(result));
    }

    [Fact]
    public async Task Guards_bind_through_two_levels_of_calls()
    {
        var result = await Analyze("var i = _state.Left; if (i < 4) _state.Forward(i);",
                                   "var j = _state.Right; if (j >= 4) _state.Forward(j);",
                                   "public void Forward(int index) => Write(index);");
        AssertTwoWorkersAccess(result);
        Assert.Empty(CellFindings(result));
    }

    [Fact]
    public async Task Overlapping_guards_keep_a_pair_from_two_workers()
    {
        var result = await Analyze("var i = _state.Left; if (i < 6) _state.Write(i);",
                                   "var j = _state.Right; if (j >= 4) _state.Write(j);");
        AssertAcrossWorkers(result);
    }

    [Fact]
    public async Task Different_guards_of_two_executions_reaching_one_helper_stay_separate()
    {
        var result = await Analyze("var i = _state.Left; if (i < 4) _state.Write(i);",
                                   "var j = _state.Right; if (j >= 4) _state.Write(j);");
        Assert.Empty(CellFindings(result));
        Assert.Equal(2, CellAccesses(result).Select(access => access.ExecutionId).Distinct().Count());
    }

    [Fact]
    public async Task Two_calls_in_one_execution_keep_their_own_guards()
    {
        var result = await Analyze("var i = _state.Left; if (i < 4) _state.Write(i); if (i >= 4) _state.Write(i);",
                                   "var j = _state.Right; if (j < 4) _state.Write(j);");
        AssertAcrossWorkers(result);
        Assert.Contains(CellAccesses(result).GroupBy(access => access.ExecutionId), group => group.Count() == 2);
    }

    [Fact]
    public async Task Sixteen_paths_still_carry_their_call_site_guard()
    {
        var calls = string.Concat(Enumerable.Repeat("if (i < 4) _state.Write(i); ", 16));
        var result = await Analyze("var i = _state.Left; " + calls,
                                   "var j = _state.Right; if (j >= 4) _state.Write(j);");
        AssertTwoWorkersAccess(result);
        Assert.Empty(CellFindings(result));
    }

    [Fact]
    public async Task The_seventeenth_path_discards_guards_above_the_merge()
    {
        var calls = string.Concat(Enumerable.Repeat("if (i < 4) _state.Write(i); ", 17));
        var result = await Analyze("var i = _state.Left; " + calls,
                                   "var j = _state.Right; if (j >= 4) _state.Write(j);");
        AssertAcrossWorkers(result);
    }

    [Fact]
    public async Task The_same_parameter_name_in_two_executions_is_not_one_value()
    {
        var result = await Analyze("_state.Write(_state.Left);",
                                   "_state.Write(_state.Right + 1);");
        AssertAcrossWorkers(result);
    }

    [Fact]
    public async Task A_canonical_field_value_remains_shared_across_executions()
    {
        var run = EngineFixture.Analyze(Source("if (_state.Shared < 4) _state.Write(_state.Shared);",
                                               "if (_state.Shared >= 4) _state.Write(_state.Shared);"));
        Assert.NotEmpty(CellAccesses(run));
        Assert.All(CellAccesses(run), access => Assert.StartsWith(PathPredicate.CANONICAL,
            Assert.IsType<VariableTerm>(access.SelectorTerm).Identity, StringComparison.Ordinal));
        Assert.Empty(CellFindings(await Analyzed(Source("if (_state.Shared < 4) _state.Write(_state.Shared);",
                                                       "if (_state.Shared >= 4) _state.Write(_state.Shared);"))));
    }

    [Fact]
    public async Task A_guard_that_does_not_dominate_every_call_does_not_remove_the_pair()
    {
        var result = await Analyze("var i = _state.Left; if (i < 4) _state.Write(i); _state.Write(i);",
                                   "var j = _state.Right; if (j >= 4) _state.Write(j);");
        AssertAcrossWorkers(result);
    }

    [Fact]
    public async Task Reassignment_of_a_parameter_breaks_its_input_binding()
    {
        var result = await Analyze("var i = _state.Left; if (i < 4) _state.Reassigned(i);",
                                   "var j = _state.Right; if (j >= 4) _state.Reassigned(j);",
                                   "public void Reassigned(int index) { index = 2; Slots[index] = 1; }");
        AssertAcrossWorkers(result);
        Assert.Contains(CellFindings(result), finding => finding.Resource.Selector!.Text == "[2]");
    }

    [Fact]
    public async Task Ref_parameter_is_not_bound_to_its_callers_input_value()
    {
        var result = await Analyze("var i = _state.Left; if (i < 4) _state.ByRef(ref i);",
                                   "var j = _state.Right; if (j >= 4) _state.ByRef(ref j);",
                                   "public void ByRef(ref int index) => Slots[index] = 1;");
        AssertAcrossWorkers(result);
    }

    [Fact]
    public async Task Out_parameter_is_not_bound_to_its_callers_input_value()
    {
        var result = await Analyze("var i = _state.Left; if (i < 4) _state.ByOut(out i);",
                                   "var j = _state.Right; if (j >= 4) _state.ByOut(out j);",
                                   "public void ByOut(out int index) { index = 2; Slots[index] = 1; }");
        AssertAcrossWorkers(result);
        Assert.Contains(CellFindings(result), finding => finding.Resource.Selector!.Text == "[2]");
    }

    [Fact]
    public async Task In_parameter_is_not_bound_to_its_callers_input_value()
    {
        var result = await Analyze("var i = _state.Left; if (i < 4) _state.ByIn(in i);",
                                   "var j = _state.Right; if (j >= 4) _state.ByIn(in j);",
                                   "public void ByIn(in int index) => Slots[index] = 1;");
        AssertAcrossWorkers(result);
    }

    [Fact]
    public async Task A_value_past_the_call_depth_budget_stays_unknown()
    {
        var methods = string.Join(" ", Enumerable.Range(1, 5).Select(level =>
            level == 5 ? "public void Level5(int index) => Write(index);"
                       : $"public void Level{level}(int index) => Level{level + 1}(index);"));
        var result = await Analyze("var i = _state.Left; if (i < 4) _state.Level1(i);",
                                   "var j = _state.Right; if (j >= 4) _state.Level1(j);", methods);
        AssertAcrossWorkers(result);
    }

    [Fact]
    public async Task Constant_arguments_name_distinct_exact_cells_without_a_solver()
    {
        var result = await Analyzed(Source("_state.Write(3);", "_state.Write(4);"), unavailableSolver: true);
        Assert.Empty(CellFindings(result));
        var run = EngineFixture.Analyze(Source("_state.Write(3);", "_state.Write(4);"));
        Assert.Equal(["[3]", "[4]"], CellAccesses(run).Select(access => access.Resource.Selector!.Text).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_generic_helper_binds_its_index_parameter()
    {
        var result = await Analyze("var i = _state.Left; if (i < 4) _state.WriteGeneric<string>(i);",
                                   "var j = _state.Right; if (j >= 4) _state.WriteGeneric<string>(j);",
                                   "public void WriteGeneric<T>(int index) => Slots[index] = 1;");
        AssertTwoWorkersAccess(result);
        Assert.Empty(CellFindings(result));
    }

    [Fact]
    public async Task A_callee_guard_is_bound_to_the_callers_argument()
    {
        var result = await Analyze("var i = _state.Left; if (i >= 4) _state.WriteIfLow(i);",
                                   "_state.Write(_state.Right);",
                                   "public void WriteIfLow(int index) { if (index < 4) Slots[index] = 1; }");
        AssertTwoWorkersAccess(result);
        Assert.Empty(CellFindings(result));
    }

    [Fact]
    public async Task A_constant_argument_constrains_the_callees_guard()
    {
        var result = await Analyze("_state.WriteIfLow(5);", "_state.Write(5);",
                                   "public void WriteIfLow(int index) { if (index < 4) Slots[index] = 1; }");
        AssertTwoWorkersAccess(result);
        Assert.Empty(CellFindings(result));
    }

    [Fact]
    public async Task A_sum_of_constant_arguments_wraps_in_its_type()
    {
        const string methods = "public void WriteSum(int offset, int index) => Slots[unchecked(offset + index + 2)] = 1;";
        var run = EngineFixture.Analyze(Source("_state.WriteSum(int.MaxValue, int.MaxValue);", "_state.Slots[0] = 2;", methods));
        Assert.Equal(["[0]", "[0]"], CellAccesses(run).Select(access => access.Resource.Selector!.Text));
        var result = await Analyzed(Source("_state.WriteSum(int.MaxValue, int.MaxValue);", "_state.Slots[0] = 2;", methods),
                                    unavailableSolver: true);
        AssertAcrossWorkers(result);
    }

    [Fact]
    public async Task A_guard_over_a_constructed_readonly_field_names_the_same_constant_as_its_cell()
    {
        var result = await Analyze("var i = _state.Options.Index; if (i < 4) _state.Slots[i] = 1;", "_state.Slots[5] = 2;", OPTIONS);
        Assert.Contains(CellAccesses(result), access => access.Resource.Selector!.Text == "[5]" &&
                                                        access.Symbol.Contains("FirstWorker", StringComparison.Ordinal));
        Assert.Empty(CellFindings(result));
    }

    [Fact]
    public async Task A_guard_over_a_constructed_readonly_field_that_holds_keeps_the_pair()
    {
        var result = await Analyze("var i = _state.Options.Index; if (i < 6) _state.Slots[i] = 1;", "_state.Slots[5] = 2;", OPTIONS);
        AssertAcrossWorkers(result);
    }

    /// <summary>A readonly field of an object the singleton constructs, which its constructor sets to a constant: the one
    /// object per process whose value the analysis reads off its construction.</summary>
    private const string OPTIONS = "public readonly GuardOptions Options = new(); " +
                                   "public sealed class GuardOptions { public readonly int Index; public GuardOptions() { Index = 5; } }";

    [Fact]
    public async Task A_call_site_guard_binds_the_index_of_a_parameter_array()
    {
        var result = await Analyze("var i = _state.Left; if (i < 4) _state.WriteAt(_state.Slots, i);",
                                   "var j = _state.Right; if (j >= 4) _state.WriteAt(_state.Slots, j);", ARRAY_WRITERS);
        AssertTwoWorkersAccess(result);
        Assert.Empty(CellFindings(result));
    }

    [Fact]
    public async Task A_call_site_guard_binds_the_index_of_a_reference_into_a_parameter_array()
    {
        var result = await Analyze("var i = _state.Left; if (i < 4) _state.RefAt(_state.Slots, i);",
                                   "var j = _state.Right; if (j >= 4) _state.RefAt(_state.Slots, j);", ARRAY_WRITERS);
        AssertTwoWorkersAccess(result);
        Assert.Empty(CellFindings(result));
    }

    [Fact]
    public async Task Overlapping_guards_over_a_parameter_array_keep_the_pair()
    {
        var result = await Analyze("var i = _state.Left; if (i < 5) _state.WriteAt(_state.Slots, i);",
                                   "var j = _state.Right; if (j >= 4) _state.RefAt(_state.Slots, j);", ARRAY_WRITERS);
        AssertAcrossWorkers(result);
    }

    [Fact]
    public async Task Two_calls_of_one_helper_under_different_guards_keep_both_paths()
    {
        var result = await Analyze("if (_state.Shared < 4) _state.Touch(); if (_state.Shared >= 4) _state.Touch();",
                                   "if (_state.Shared >= 4) _state.Value = 2;", TOUCH);
        Assert.Contains(ValueFindings(result), finding => finding.AccessA.ExecutionId != finding.AccessB.ExecutionId);
    }

    [Fact]
    public async Task Two_calls_of_one_helper_under_guards_that_exclude_the_other_worker_keep_no_pair()
    {
        var result = await Analyze("if (_state.Shared < 4) _state.Touch(); if (_state.Shared < 3) _state.Touch();",
                                   "if (_state.Shared >= 4) _state.Value = 2;", TOUCH);
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                   access.Symbol.Contains("Touch", StringComparison.Ordinal));
        Assert.Empty(ValueFindings(result));
    }

    [Fact]
    public async Task A_later_path_under_fewer_guards_takes_the_place_of_one_under_more()
    {
        var result = await Analyze("if (_state.Shared < 4) _state.Touch(); _state.Touch();",
                                   "if (_state.Shared >= 4) _state.Value = 2;", TOUCH);
        var touch = Assert.Single(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]) &&
                                                             access.Symbol.Contains("Touch", StringComparison.Ordinal));
        Assert.Empty(touch.Conditions);
        Assert.Contains(ValueFindings(result), finding => finding.AccessA.ExecutionId != finding.AccessB.ExecutionId);
    }

    /// <summary>A helper writing a field, with no index for a guard to name.</summary>
    private const string TOUCH = "public int Value; public void Touch() => Value = 1;";

    private static IReadOnlyList<Finding> ValueFindings(AnalysisResult result) =>
        result.Findings.Where(finding => finding.Resource.AccessPath.SequenceEqual(["Value"])).ToArray();

    /// <summary>Writers of a cell of an array handed over by value, directly and through a reference.</summary>
    private const string ARRAY_WRITERS = "public void WriteAt(int[] array, int index) => array[index] = 1; " +
                                         "public void RefAt(int[] array, int index) { ref int r = ref array[index]; r = 1; }";

    private static async Task<AnalysisResult> Analyze(string first, string second, string methods = "") =>
        await Analyzed(Source(first, second, methods));

    private static async Task<AnalysisResult> Analyzed(string source, bool unavailableSolver = false)
    {
        var solution = FixtureSolution.Create(("Case.cs", Usings + source));
        return unavailableSolver
            ? await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, ProviderRegistry.BuiltIn, InterproceduralPairing.Pair,
                                                  AnalysisLimits.Default, () => new UnavailableSolver(UnavailableSolver.NOT_LOADED),
                                                  CancellationToken.None)
            : await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, ProviderRegistry.BuiltIn, CancellationToken.None);
    }

    private static IReadOnlyList<Finding> CellFindings(AnalysisResult result) =>
        result.Findings.Where(finding => finding.Resource.AccessPath.Contains("Slots")).ToArray();

    private static IReadOnlyList<Access> CellAccesses(EngineRun run) =>
        run.Collection.Accesses.Where(access => access.Resource.AccessPath.Contains("Slots") &&
                                                access.Resource.Selector is not null).ToArray();

    private static IReadOnlyList<Access> CellAccesses(AnalysisResult result) =>
        result.Accesses.Where(access => access.Resource.AccessPath.Contains("Slots") &&
                                        access.Resource.Selector is not null).ToArray();

    private static void AssertAcrossWorkers(AnalysisResult result) =>
        Assert.Contains(CellFindings(result), finding => finding.AccessA.ExecutionId != finding.AccessB.ExecutionId);

    private static void AssertTwoWorkersAccess(AnalysisResult result) =>
        Assert.Equal(2, CellAccesses(result).Select(access => access.ExecutionId).Distinct().Count());

    private static string Source(string first, string second, string methods = "") => $$"""
        public sealed class GuardState
        {
            public readonly int[] Slots = new int[16];
            public readonly int Shared = 5;
            public int Left;
            public int Right;
            public void Write(int index) => Slots[index] = 1;
            {{methods}}
        }

        public sealed class FirstWorker : BackgroundService
        {
            private readonly GuardState _state;
            public FirstWorker(GuardState state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{first}}
                return Task.CompletedTask;
            }
        }

        public sealed class SecondWorker : BackgroundService
        {
            private readonly GuardState _state;
            public SecondWorker(GuardState state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{second}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<GuardState>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();");
}
