using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>
/// What the paths of two accesses decide about their pair, with no solver (TD-090, TD-092): the bounded predicates of the
/// branches that lead to each access, compared as constants, ranges and negations of one subject. A field read is one and the
/// same value in every execution only where everything is proven at once, and a parallel loop's iteration number is the one
/// thing two overlapping iterations of one run never share (TD-068).
/// </summary>
public sealed class PathConditionTests
{
    [Fact]
    public void Mutually_exclusive_boolean_branches_take_the_pair_away() =>
        Assert.Empty(Pairs(Workers("if (_options.IsPrimary) _state.Text = \"first\";", "if (!_options.IsPrimary) _state.Text = \"second\";")));

    /// <summary>What reaches an access is part of the condition it runs under: two helpers called from exclusive branches of one
    /// proven guard never run together, however unconditional each helper's own body is (R8).</summary>
    [Fact]
    public void Helpers_called_from_mutually_exclusive_branches_take_the_pair_away() =>
        Assert.Empty(Pairs(Workers("if (_options.IsPrimary) Write(); void Write() => _state.Text = \"first\";",
                                   "if (!_options.IsPrimary) Write(); void Write() => _state.Text = \"second\";")));

    /// <summary>The same two helpers under one guard do run together, so carrying the call site's guards takes away the pairs
    /// that cannot happen and no others.</summary>
    [Fact]
    public void Helpers_called_from_one_branch_leave_the_pair() =>
        Assert.Equal(["Text"], Pairs(Workers("if (_options.IsPrimary) Write(); void Write() => _state.Text = \"first\";",
                                             "if (_options.IsPrimary) Write(); void Write() => _state.Text = \"second\";")));

    [Fact]
    public void Two_cases_of_one_switch_over_an_enum_take_the_pair_away() =>
        Assert.Empty(Pairs(Workers("switch (_options.Shard) { case Shard.Left: _state.Text = \"first\"; break; }",
                                   "switch (_options.Shard) { case Shard.Right: _state.Text = \"second\"; break; }")));

    /// <summary>Two enum members of one value are one value, however differently they are spelled: a member reaches a predicate
    /// as the number it is, and a name is no proof that two values differ (TD-090).</summary>
    [Fact]
    public void Two_enum_members_of_one_value_leave_the_pair() =>
        Assert.Equal(["Text"], Pairs(Workers("switch (_options.Shard) { case Shard.Left: _state.Text = \"first\"; break; }",
                                             "switch (_options.Shard) { case Shard.Same: _state.Text = \"second\"; break; }")));

    [Fact]
    public void Numeric_ranges_that_do_not_meet_take_the_pair_away() =>
        Assert.Empty(Pairs(Workers("if (_options.Limit < 5) _state.Text = \"first\";", "if (_options.Limit >= 5) _state.Text = \"second\";")));

    [Fact]
    public void Numeric_ranges_that_meet_leave_the_pair() =>
        Assert.Equal(["Text"], Pairs(Workers("if (_options.Limit < 7) _state.Text = \"first\";",
                                             "if (_options.Limit >= 5) _state.Text = \"second\";")));

    /// <summary>A guard is decided in the width of the value it constrains: read as a 32-bit number, these two bounds are an
    /// empty range and the conflict disappears, although the program runs them over a <c>long</c> that satisfies both
    /// (TD-094).</summary>
    [Fact]
    public async Task Ranges_that_meet_only_beyond_32_bits_leave_the_pair()
    {
        var result = await Analyzed(WorkerSource("if (_options.Window > 2147483647) _state.Text = \"first\";",
                                                 "if (_options.Window < 2147483650) _state.Text = \"second\";"));

        Assert.Contains("Text", result.Findings.Select(finding => string.Join(".", finding.Resource.AccessPath)));
    }

    /// <summary>Two runs of one action were handed two indices, not one. Naming both of them <c>index</c> would let the solver
    /// answer a run against itself and decide that a write of <c>Slots[index]</c> can never meet a write of
    /// <c>Slots[index + 1]</c>, although the other run may have been handed exactly the index this one wrote past (TD-092).</summary>
    [Fact]
    public async Task Two_runs_of_one_action_do_not_share_the_index_they_were_handed()
    {
        var result = await PhaseOneAnalyzer.AnalyzeAsync(
            FixtureSolution.Create(("Case.cs", Usings + Shared + """
                public class IndexController : ControllerBase
                {
                    private readonly Board _board;
                    public IndexController(Board board) => _board = board;

                    public void Post(int index)
                    {
                        _board.Slots[index] = "first";
                        _board.Slots[index + 1] = "second";
                    }
                }
                """ + Startup("services.AddSingleton<Board>();"))),
            ROOT_DIRECTORY, ProviderRegistry.BuiltIn, CancellationToken.None);

        Assert.Contains("Slots.[?]", result.Findings.Select(finding => string.Join(".", finding.Resource.AccessPath)));
        Assert.Contains(result.Findings, finding => finding.AccessA.Source.StartLine != finding.AccessB.Source.StartLine);
    }

    /// <summary>A guard the analysis cannot read is abstracted away rather than believed, and the finding says so (TD-095).</summary>
    [Fact]
    public void A_predicate_the_analysis_does_not_support_leaves_the_pair_and_is_an_uncertainty()
    {
        var run = Workers("if (_options.Name.StartsWith(\"x\")) _state.Text = \"first\";",
                          "if (!_options.Name.StartsWith(\"x\")) _state.Text = \"second\";");

        Assert.Equal(["Text"], Pairs(run));
        Assert.Contains(PathConditions.UnsupportedUncertainty("string.StartsWith(string)"),
                        run.Collection.Accesses.SelectMany(access => access.Uncertainties));
        Assert.All(Conditions(run, "FirstWorker.ExecuteAsync(CancellationToken)"), condition => Assert.False(condition.IsSupported));
    }

    /// <summary>The four proofs together: one object per process, a field that cannot change after construction, nothing
    /// writing it outside that construction, and a read once the construction is over.</summary>
    [Fact]
    public void A_readonly_field_of_a_singleton_region_is_one_value_in_every_execution()
    {
        var run = Workers("if (_options.Flag) _state.Text = \"first\";", "if (!_options.Flag) _state.Text = \"second\";");

        Assert.All(Conditions(run, "FirstWorker.ExecuteAsync(CancellationToken)"),
                   condition => Assert.StartsWith("canon|", condition.Subject, StringComparison.Ordinal));
        Assert.Empty(Pairs(run));
    }

    [Fact]
    public void A_get_only_property_of_a_singleton_region_is_one_value_in_every_execution()
    {
        var run = Workers("if (_options.IsPrimary) _state.Text = \"first\";", "if (!_options.IsPrimary) _state.Text = \"second\";");

        Assert.All(Conditions(run, "SecondWorker.ExecuteAsync(CancellationToken)"),
                   condition => Assert.StartsWith("canon|", condition.Subject, StringComparison.Ordinal));
        Assert.Empty(Pairs(run));
    }

    /// <summary>A region that stands for more than one object says nothing about what another execution read from it.</summary>
    [Fact]
    public void A_readonly_field_of_a_region_of_several_objects_is_a_value_of_each_execution()
    {
        var run = Workers("if (_options.Flag) _state.Text = \"first\";", "if (!_options.Flag) _state.Text = \"second\";",
                          "services.AddTransient<RoleOptions>();");

        Assert.All(Conditions(run, "FirstWorker.ExecuteAsync(CancellationToken)"),
                   condition => Assert.StartsWith("local|", condition.Subject, StringComparison.Ordinal));
        Assert.Equal(["Text"], Pairs(run));
    }

    [Fact]
    public void A_settable_field_is_a_value_of_each_execution()
    {
        var run = Workers("if (_options.Mutable) _state.Text = \"first\";", "if (!_options.Mutable) _state.Text = \"second\";");

        Assert.All(Conditions(run, "FirstWorker.ExecuteAsync(CancellationToken)"),
                   condition => Assert.StartsWith("local|", condition.Subject, StringComparison.Ordinal));
        Assert.Equal(["Text"], Pairs(run));
    }

    /// <summary>A native integer is as wide as the process, and reading it as 32 bits truncates a guard that holds: the two
    /// bounds below meet only past `int.MaxValue`, and a range the program satisfies must not come back empty (R9).</summary>
    [Fact]
    public async Task Ranges_over_a_native_integer_that_meet_only_beyond_32_bits_leave_the_pair()
    {
        var result = await Analyzed(WorkerSource("if (_options.Span > 2147483647) _state.Text = \"first\";",
                                                 "if (_options.Span < 2147483650) _state.Text = \"second\";"));

        Assert.Contains("Text", result.Findings.Select(finding => string.Join(".", finding.Resource.AccessPath)));
    }

    /// <summary>Two runs of one action read a mutable field twice, not once. Naming the read after the instance makes both runs
    /// say the same name, and the cheap filter then reads two runs taking opposite branches as one run that cannot take both —
    /// and drops the pair before the solver is ever asked. Only a canonical subject is one value on both sides (TD-092).</summary>
    [Fact]
    public async Task Two_runs_of_one_action_do_not_share_a_value_neither_proved_canonical()
    {
        // Its own fixture, with nothing else writing the field: a pair between two branches is the whole question, and a
        // constructor that also writes it would answer the assertion without ever forming that pair.
        var result = await Analyzed("""
            public sealed class Switchable { public bool Mutable { get; set; } }
            public sealed class Written { public string? Text { get; set; } }

            public class SwitchController : ControllerBase
            {
                private readonly Switchable _switch;
                private readonly Written _written;
                public SwitchController(Switchable value, Written written)
                {
                    _switch = value;
                    _written = written;
                }

                public void Post()
                {
                    if (_switch.Mutable)
                        _written.Text = "first";
                    else
                        _written.Text = "second";
                }
            }
            """ + Startup("services.AddSingleton<Switchable>(); services.AddSingleton<Written>();"));

        Assert.Contains(result.Findings, finding => finding.Resource.AccessPath.SequenceEqual(["Text"]) &&
                                                    finding.AccessA.Source.StartLine != finding.AccessB.Source.StartLine);
    }

    /// <summary>One field of two objects is two values, however alike the two guards read.</summary>
    [Fact]
    public void The_same_field_of_another_region_is_another_value() =>
        Assert.Equal(["Text"], Pairs(Workers("if (_options.IsPrimary) _state.Text = \"first\";",
                                             "if (!_other.IsPrimary) _state.Text = \"second\";")));

    [Fact]
    public void A_field_something_writes_outside_a_constructor_is_a_value_of_each_execution()
    {
        var run = Workers("if (_options.Configured) _state.Text = \"first\";",
                          "_options.Configure(); if (!_options.Configured) _state.Text = \"second\";");

        Assert.All(Conditions(run, "FirstWorker.ExecuteAsync(CancellationToken)"),
                   condition => Assert.StartsWith("local|", condition.Subject, StringComparison.Ordinal));
        Assert.Contains("Text", Pairs(run));
    }

    /// <summary>A read while the object is still being built sees a field its construction has not finished writing.</summary>
    [Fact]
    public void A_read_during_construction_is_a_value_of_that_construction()
    {
        var run = Workers("if (_options.IsPrimary) _state.Text = \"first\";", "if (!_options.IsPrimary) _state.Text = \"second\";");

        Assert.All(Conditions(run, "RoleOptions..ctor(PathState)"),
                   condition => Assert.StartsWith("local|", condition.Subject, StringComparison.Ordinal));
    }

    [Fact]
    public void Accesses_under_no_condition_at_all_keep_their_pair()
    {
        var run = Workers("_state.Text = \"first\";", "_state.Text = \"second\";");

        var accesses = run.Collection.Accesses.Where(access => access.Symbol == "FirstWorker.ExecuteAsync(CancellationToken)").ToArray();
        Assert.NotEmpty(accesses);
        Assert.All(accesses, access => Assert.Empty(access.Conditions));
        Assert.Equal(["Text"], Pairs(run));
    }

    /// <summary>Every iteration of one run writes the cell its own iteration number names, and no two of them hold the same
    /// number, so the array is partitioned rather than shared (TD-068).</summary>
    [Fact]
    public void Two_iterations_of_one_parallel_for_take_the_pair_away()
    {
        var run = Loop("Parallel.For(0, 8, index => _board.Slots[index] = \"filled\");");

        Assert.Empty(Pairs(run));
        Assert.Contains(run.Collection.Accesses, access => access.IsIterationIndexed);
    }

    /// <summary>Two runs of a loop are two sets of iteration numbers, and nothing says the two do not meet.</summary>
    [Fact]
    public void Two_runs_of_a_parallel_for_keep_their_pair() =>
        Assert.Equal(["Slots.[?]"], Pairs(Workers("Parallel.For(0, 8, index => _board.Slots[index] = \"first\");",
                                                  "Parallel.For(0, 8, index => _board.Slots[index] = \"second\");")));

    [Fact]
    public void The_indexed_overload_of_parallel_foreach_takes_the_pair_away()
    {
        var run = Loop("Parallel.ForEach(new[] { 5, 6 }, (item, state, index) => _board.Slots[(int)index] = \"filled\");");

        Assert.Empty(Pairs(run));
        Assert.Contains(run.Collection.Accesses, access => access.IsIterationIndexed);
    }

    /// <summary>A guard on the index is a guard on the cell: the bound each side proves about its own index is what decides
    /// whether the two cells can be one, so the predicates and the cell's expression have to name that index alike (TD-092).</summary>
    [Fact]
    public async Task Guards_that_hold_two_indices_to_disjoint_ranges_take_the_pair_away()
    {
        var result = await Analyzed("""
            public sealed class Register
            {
                private const int Rows = 10;
                private const int Split = 5;

                private readonly string?[] _slots = new string?[Rows];

                public void FillLow()
                {
                    for (var i = 0; i < Rows; i++)
                        if (i < Split)
                            _slots[i] = "low";
                }

                public void FillHigh()
                {
                    for (var j = 0; j < Rows; j++)
                        if (j >= Split)
                            _slots[j] = "high";
                }
            }

            public sealed class LowWorker : BackgroundService
            {
                private readonly Register _register;
                public LowWorker(Register register) => _register = register;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _register.FillLow();
                    return Task.CompletedTask;
                }
            }

            public sealed class HighWorker : BackgroundService
            {
                private readonly Register _register;
                public HighWorker(Register register) => _register = register;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _register.FillHigh();
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Register>(); services.AddHostedService<LowWorker>(); " +
                          "services.AddHostedService<HighWorker>();"));

        Assert.DoesNotContain("_slots.[?]", result.Findings.Select(finding => string.Join(".", finding.Resource.AccessPath)));
    }

    /// <summary>An iteration number converted narrower than the iterations it counts stops being one number: 512 iterations
    /// written through a <c>byte</c> name 256 cells, and each of them is named twice (TD-068, TD-094).</summary>
    [Fact]
    public void An_index_narrowed_below_the_iteration_number_keeps_the_pair()
    {
        var run = Loop("Parallel.For(0, 512, i => _board.Slots[(byte)i] = \"filled\");");

        Assert.Equal(["Slots.[?]"], Pairs(run));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.IsIterationIndexed);
    }

    /// <summary>A conversion that keeps every iteration apart keeps the proof with it: the cells stay as distinct as the
    /// numbers that name them.</summary>
    [Fact]
    public void An_index_widened_from_the_iteration_number_takes_the_pair_away()
    {
        var run = Loop("Parallel.For(0, 512, i => _board.Slots[(long)i] = \"filled\");");

        Assert.Empty(Pairs(run));
        Assert.Contains(run.Collection.Accesses, access => access.IsIterationIndexed);
    }

    /// <summary>The element a plain <c>ForEach</c> passes is no iteration number: the source it comes from may hold the same
    /// element twice, and then two iterations write one cell.</summary>
    [Fact]
    public void The_plain_overload_of_parallel_foreach_over_a_repeated_element_keeps_the_pair()
    {
        var run = Loop("Parallel.ForEach(new[] { 0, 0 }, item => _board.Slots[item] = \"filled\");");

        Assert.Equal(["Slots.[?]"], Pairs(run));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.IsIterationIndexed);
    }

    [Fact]
    public void The_plain_overload_of_parallel_foreach_over_a_source_nothing_proves_keeps_the_pair()
    {
        var run = Loop("Parallel.ForEach(_board.Order, item => _board.Slots[item] = \"filled\");");

        Assert.Contains("Slots.[?]", Pairs(run));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.IsIterationIndexed);
    }

    /// <summary>The predicates the accesses of one member run under; a member with no access has none at all, so the search
    /// itself is checked here rather than in every test.</summary>
    private static IReadOnlyList<PathPredicate> Conditions(EngineRun run, string symbol)
    {
        var conditions = run.Collection.Accesses.Where(access => access.Symbol == symbol)
                            .SelectMany(access => access.Conditions).ToArray();

        Assert.NotEmpty(conditions);
        return conditions;
    }

    private static IReadOnlyList<string> Pairs(EngineRun run) =>
        run.Pairs.Pairs.Select(pair => string.Join(".", pair.Resource.AccessPath)).Distinct(StringComparer.Ordinal)
           .Order(StringComparer.Ordinal).ToArray();

    private const string Shared = """
        public enum Shard { Left = 0, Right = 1, Same = 0 }

        public sealed class RoleOptions
        {
            public readonly bool Flag = true;

            public RoleOptions(PathState state)
            {
                if (IsPrimary)
                    state.Text = "constructed";
            }

            public bool IsPrimary { get; } = true;
            public Shard Shard { get; } = Shard.Left;
            public int Limit { get; } = 5;
            public long Window { get; } = 2147483649;
            public nint Span { get; } = (nint)2147483649L;
            public string Name { get; } = "node";
            public bool Mutable { get; set; }
            public bool Configured;

            public void Configure() => Configured = true;
        }

        public sealed class OtherOptions
        {
            public bool IsPrimary { get; } = true;
        }

        public sealed class PathState { public string? Text { get; set; } }

        public sealed class Board
        {
            public readonly string[] Slots = new string[8];
            public readonly int[] Order = new int[2];
        }

        """;

    private const string Registrations =
        "services.AddSingleton<RoleOptions>(); services.AddSingleton<OtherOptions>(); services.AddSingleton<PathState>(); " +
        "services.AddSingleton<Board>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();";

    /// <summary>Two workers of one process, each running its own body over the same shared objects.</summary>
    private static EngineRun Workers(string first, string second, string registrations = "") =>
        Analyze(WorkerSource(first, second, registrations));

    /// <summary>The same fixture run through the whole pipeline, which is the only way the solver is asked anything.</summary>
    private static async Task<AnalysisResult> Analyzed(string source) =>
        await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Usings + source)), ROOT_DIRECTORY,
                                            ProviderRegistry.BuiltIn, CancellationToken.None);

    private static string WorkerSource(string first, string second, string registrations = "") => Shared + $$"""
        public sealed class FirstWorker : BackgroundService
        {
            private readonly RoleOptions _options;
            private readonly OtherOptions _other;
            private readonly PathState _state;
            private readonly Board _board;
            public FirstWorker(RoleOptions options, OtherOptions other, PathState state, Board board)
            {
                _options = options;
                _other = other;
                _state = state;
                _board = board;
            }

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{first}}
                return Task.CompletedTask;
            }
        }

        public sealed class SecondWorker : BackgroundService
        {
            private readonly RoleOptions _options;
            private readonly OtherOptions _other;
            private readonly PathState _state;
            private readonly Board _board;
            public SecondWorker(RoleOptions options, OtherOptions other, PathState state, Board board)
            {
                _options = options;
                _other = other;
                _state = state;
                _board = board;
            }

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{second}}
                return Task.CompletedTask;
            }
        }
        """ + Startup(registrations.Length == 0 ? Registrations : Registrations.Replace("services.AddSingleton<RoleOptions>();", registrations, StringComparison.Ordinal));

    /// <summary>One worker running one parallel loop over the shared board.</summary>
    private static EngineRun Loop(string body) => Analyze(Shared + $$"""
        public sealed class LoopWorker : BackgroundService
        {
            private readonly Board _board;
            public LoopWorker(Board board) => _board = board;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<Board>(); services.AddHostedService<LoopWorker>();"));
}
