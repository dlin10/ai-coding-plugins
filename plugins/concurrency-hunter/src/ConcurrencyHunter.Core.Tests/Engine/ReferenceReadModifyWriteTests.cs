using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A write through a reference whose value depends on a read of its own resource is one read-modify-write, whichever
/// route the read and the write took: the same reference, another reference to that place, or the field or element itself, in
/// one body or across calls (question 25, closed in the second run of phase 5b).</summary>
public sealed class ReferenceReadModifyWriteTests
{
    [Fact]
    public void Ref_local_read_then_dependent_write_is_one_read_modify_write()
    {
        var run = AnalyzeCase("ref int r = ref _state.Cell; var old = r; r = old + 1;", "_state.Cell = 2;");

        Assert.Equal([AccessOperation.ReadModifyWrite], FirstOperations(run, "Cell"));
    }

    [Fact]
    public void Read_then_dependent_write_through_an_out_parameter_is_one_read_modify_write()
    {
        var run = AnalyzeCase("_state.Set(out _state.Cell, _state.Cell + 1);", "_state.Cell = 2;",
                              "public void Set(out int target, int value) => target = value;");

        var write = Assert.Single(First(run, "Cell"));
        Assert.Equal(AccessOperation.ReadModifyWrite, write.Operation);
        Assert.StartsWith("State.Set(", write.Symbol, StringComparison.Ordinal);
        Assert.StartsWith("FirstWorker.", Assert.Single(write.ReadSources).Symbol, StringComparison.Ordinal);
    }

    [Fact]
    public void Field_read_then_dependent_write_through_a_ref_is_one_read_modify_write()
    {
        var run = AnalyzeCase("var old = _state.Cell; ref int r = ref _state.Cell; r = old + 1;", "_state.Cell = 2;");

        Assert.Equal([AccessOperation.ReadModifyWrite], FirstOperations(run, "Cell"));
    }

    [Fact]
    public void Ref_read_then_dependent_field_write_is_one_read_modify_write()
    {
        var run = AnalyzeCase("ref int r = ref _state.Cell; var old = r; _state.Cell = old + 1;", "_state.Cell = 2;");

        var write = Assert.Single(First(run, "Cell"));
        Assert.Equal(AccessOperation.ReadModifyWrite, write.Operation);
        Assert.False(write.IsReferenceAccess);
        Assert.Single(write.ReadSources);
    }

    [Fact]
    public void Two_refs_to_one_field_make_one_read_modify_write()
    {
        var run = AnalyzeCase("ref int a = ref _state.Cell; ref int b = ref _state.Cell; var old = a; b = old + 1;", "_state.Cell = 2;");

        Assert.Equal([AccessOperation.ReadModifyWrite], FirstOperations(run, "Cell"));
    }

    [Fact]
    public void Ref_return_read_then_dependent_write_is_one_read_modify_write()
    {
        var run = AnalyzeCase("var old = _state.Get(); _state.Get() = old + 1;", "_state.Cell = 2;",
                              "public ref int Get() => ref Cell;");

        Assert.Equal([AccessOperation.ReadModifyWrite], FirstOperations(run, "Cell"));
    }

    [Fact]
    public void Ref_parameter_read_then_dependent_write_in_the_callee_is_one_read_modify_write()
    {
        var run = AnalyzeCase("_state.Bump(ref _state.Cell);", "_state.Cell = 2;",
                              "public void Bump(ref int value) { var old = value; value = old + 1; }");

        var write = Assert.Single(First(run, "Cell"));
        Assert.Equal(AccessOperation.ReadModifyWrite, write.Operation);
        Assert.StartsWith("State.Bump(", write.Symbol, StringComparison.Ordinal);
    }

    [Fact]
    public void Ref_to_an_element_read_then_dependent_write_is_one_read_modify_write()
    {
        var run = AnalyzeCase("ref int r = ref _state.Slots[0]; var old = r; r = old + 1;", "_state.Slots[0] = 2;");

        var write = Assert.Single(First(run, "Slots"), access => access.Resource.Selector is not null);
        Assert.Equal(AccessOperation.ReadModifyWrite, write.Operation);
        Assert.Equal("[0]", write.Resource.Selector!.Text);
        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Independent_write_through_a_ref_stays_a_write()
    {
        var run = AnalyzeCase("ref int r = ref _state.Cell; var old = r; r = 5; _state.Seen = old;", "_state.Cell = 2;",
                              "public int Seen;");

        Assert.Equal([AccessOperation.Read, AccessOperation.Write], FirstOperations(run, "Cell").Order());
    }

    [Fact]
    public void Read_of_another_resource_leaves_the_ref_write_a_write()
    {
        var run = AnalyzeCase("ref int other = ref _state.Other; ref int r = ref _state.Cell; var old = other; r = old + 1;",
                              "_state.Cell = 2; _state.Other = 3;", "public int Other;");

        Assert.Equal([AccessOperation.Write], FirstOperations(run, "Cell"));
        Assert.Equal([AccessOperation.Read], FirstOperations(run, "Other"));
    }

    [Fact]
    public void Ref_to_two_places_folds_only_the_read_of_the_same_place()
    {
        var run = AnalyzeCase("ref int r = ref (_state.Flag ? ref _state.Slots[0] : ref _state.Slots[1]); var old = _state.Slots[0]; r = old + 1;",
                              "_state.Slots[0] = 2; _state.Slots[1] = 3;", "public bool Flag;");

        var cells = First(run, "Slots").Where(access => access.Resource.Selector is not null)
                                       .Select(access => (access.Resource.Selector!.Text, access.Operation))
                                       .Order()
                                       .ToArray();
        Assert.Equal([("[0]", AccessOperation.ReadModifyWrite), ("[1]", AccessOperation.Write)], cells);
    }

    [Fact]
    public void Folded_ref_read_is_not_an_unproven_reference()
    {
        var proven = AnalyzeCase("ref int r = ref _state.Cell; var old = r; r = old + 1;", "_state.Cell = 2;");
        Assert.Equal(0, proven.Counter(CoverageCounters.UNPROVEN_REFERENCE));

        var run = AnalyzeCase("ref int r = ref (_state.Flag ? ref _state.Slots[0] : " +
                              "ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(_state.Other)); var old = r; r = old + 1;",
                              "_state.Slots[0] = 2;", "public bool Flag; public int[] Other = new int[8];");

        Assert.Equal([AccessOperation.ReadModifyWrite],
                     First(run, "Slots").Where(access => access.Resource.Selector is not null).Select(access => access.Operation));
        // Only the write counts the place nothing proves, as it does for `r += 1`.
        Assert.Equal(1, run.Counter(CoverageCounters.UNPROVEN_REFERENCE));
    }

    [Fact]
    public void Split_ref_read_modify_write_needs_a_section_over_its_read()
    {
        const string second = "lock (_state.Gate) { _state.Cell = 2; }";
        const string gate = "public readonly object Gate = new();";

        var around = AnalyzeCase("ref int r = ref _state.Cell; var old = r; lock (_state.Gate) { r = old + 1; }", second, gate);
        var pair = Assert.Single(around.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell");
        var first = FirstSide(pair);
        Assert.Equal(AccessOperation.ReadModifyWrite, first.Operation);
        Assert.Empty(first.HeldProtection);

        var over = AnalyzeCase("lock (_state.Gate) { ref int r = ref _state.Cell; var old = r; r = old + 1; }", second, gate);
        Assert.DoesNotContain(over.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell");
    }

    [Fact]
    public async Task Compound_ref_write_fed_by_an_earlier_read_needs_a_section_over_that_read()
    {
        // `r += old` reads its place itself inside the section, but the value it adds was read before the section: the span starts
        // there, that read is folded into the write, and a section over the compound write alone protects nothing.
        const string second = "lock (_state.Gate) { _state.Cell = 2; }";
        const string gate = "public readonly object Gate = new();";

        var around = AnalyzeCase("ref int r = ref _state.Cell; var old = r; lock (_state.Gate) { r += old; }", second, gate);
        var pair = Assert.Single(around.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell");
        var first = FirstSide(pair);
        Assert.Equal(AccessOperation.ReadModifyWrite, first.Operation);
        Assert.Empty(first.HeldProtection);
        Assert.Equal([AccessOperation.ReadModifyWrite], FirstOperations(around, "Cell"));

        var over = AnalyzeCase("lock (_state.Gate) { ref int r = ref _state.Cell; var old = r; r += old; }", second, gate);
        Assert.DoesNotContain(over.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell");

        var result = await AnalyzeWithRules("ref int r = ref _state.Cell; var old = r; lock (_state.Gate) { r += old; }", second, gate);
        Assert.Equal("DCA1002", Assert.Single(result.Findings, finding => AcrossWorkers(finding.AccessA, finding.AccessB) &&
                                                                          finding.Resource.Member.Name == "Cell").RuleId);
    }

    [Fact]
    public async Task Split_ref_read_modify_write_against_a_write_is_DCA1002()
    {
        var result = await AnalyzeWithRules("ref int r = ref _state.Cell; var old = r; r = old + 1;", "_state.Cell = 2;");

        var finding = Assert.Single(result.Findings, finding => AcrossWorkers(finding.AccessA, finding.AccessB) &&
                                                                finding.Resource.Member.Name == "Cell");
        Assert.Equal("DCA1002", finding.RuleId);
    }

    [Fact]
    public async Task Ref_read_in_one_method_and_dependent_write_in_another_is_DCA1002()
    {
        var result = await AnalyzeWithRules("var old = State.Peek(ref _state.Cell); ref int r = ref _state.Cell; r = old + 1;",
                                            "_state.Cell = 2;", "public static int Peek(ref int value) => value;");

        var finding = Assert.Single(result.Findings, finding => AcrossWorkers(finding.AccessA, finding.AccessB) &&
                                                                finding.Resource.Member.Name == "Cell");
        Assert.Equal("DCA1002", finding.RuleId);
        var write = FirstSide(finding.AccessA, finding.AccessB);
        Assert.Equal(AccessOperation.ReadModifyWrite, write.Operation);
        Assert.StartsWith("State.Peek(", Assert.Single(write.ReadSources).Symbol, StringComparison.Ordinal);
        // The helper's read is folded into the write, so the first worker makes no read of its own.
        Assert.DoesNotContain(result.Accesses, access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                         access.Resource.Member.Name == "Cell" && access.Operation == AccessOperation.Read);
    }

    [Fact]
    public void Ref_read_in_one_method_and_dependent_write_in_another_needs_a_section_over_both()
    {
        const string second = "lock (_state.Gate) { _state.Cell = 2; }";
        const string members = "public readonly object Gate = new(); public static int Peek(ref int value) => value;";

        var around = AnalyzeCase("var old = State.Peek(ref _state.Cell); ref int r = ref _state.Cell; lock (_state.Gate) { r = old + 1; }",
                                 second, members);
        var pair = Assert.Single(around.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell");
        var first = FirstSide(pair);
        Assert.Equal(AccessOperation.ReadModifyWrite, first.Operation);
        Assert.Empty(first.HeldProtection);

        var over = AnalyzeCase("lock (_state.Gate) { var old = State.Peek(ref _state.Cell); ref int r = ref _state.Cell; r = old + 1; }",
                               second, members);
        Assert.DoesNotContain(over.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell");
    }

    private static IReadOnlyList<Access> First(EngineRun run, string member) =>
        run.Of(member).Where(access => access.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal)).ToArray();

    private static IReadOnlyList<AccessOperation> FirstOperations(EngineRun run, string member) =>
        First(run, member).Select(access => access.Operation).ToArray();

    private static Access FirstSide(AccessPair pair) => FirstSide(pair.First, pair.Second);

    private static Access FirstSide(Access one, Access other) =>
        one.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) ? one : other;

    private static bool AcrossWorkers(AccessPair pair) => AcrossWorkers(pair.First, pair.Second);

    private static bool AcrossWorkers(Access one, Access other) =>
        one.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) && other.Root.Symbol.Contains("SecondWorker", StringComparison.Ordinal) ||
        other.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) && one.Root.Symbol.Contains("SecondWorker", StringComparison.Ordinal);

    private static EngineRun AnalyzeCase(string first, string second, string stateMembers = "") => Analyze(Usings + Source(first, second, stateMembers));

    private static async Task<AnalysisResult> AnalyzeWithRules(string first, string second, string stateMembers = "") =>
        await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Usings + Source(first, second, stateMembers))), ROOT_DIRECTORY,
                                            ProviderRegistry.BuiltIn, CancellationToken.None);

    private static string Source(string first, string second, string stateMembers) => $$"""
        public sealed class State
        {
            public int Cell;
            public int[] Slots = new int[8];
            {{stateMembers}}
        }

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
