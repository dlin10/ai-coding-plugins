using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The atomic operations of TD-082: the <c>Interlocked</c> and <c>Volatile</c> members that name one cell, and reads and
/// writes of a <c>volatile</c> field. Their pairs conflict only when a side is a non-atomic write or read-modify-write (TD-072).</summary>
public sealed class AtomicOperationTests
{
    [Fact]
    public void Interlocked_increment_is_an_atomic_read_modify_write()
    {
        var run = Analyze("""
            public class TicketController : ControllerBase
            {
                private static int _issued;
                public void Post() => Interlocked.Increment(ref _issued);
            }
            """ + Startup());

        var access = Assert.Single(run.Of("_issued"));
        Assert.Equal(AccessOperation.AtomicReadModifyWrite, access.Operation);
        Assert.Equal("atomic-read-modify-write", access.Operation.ToWireName());
        Assert.Equal("static:TicketController", access.Resource.Region);
    }

    [Fact]
    public void Interlocked_add_is_an_atomic_read_modify_write()
    {
        var run = Analyze("""
            public class TotalController : ControllerBase
            {
                private static int _total;
                public void Post(int sample) => Interlocked.Add(ref _total, sample);
            }
            """ + Startup());

        Assert.Equal(AccessOperation.AtomicReadModifyWrite, Assert.Single(run.Of("_total")).Operation);
    }

    [Fact]
    public void Interlocked_exchange_is_an_atomic_read_modify_write_of_the_value_it_is_given()
    {
        var run = Analyze("""
            public sealed class Note { public string? Text; }
            public class SlotController : ControllerBase
            {
                private static Note? _slot;
                public void Post() => Interlocked.Exchange(ref _slot, new Note());
            }
            """ + Startup());

        Assert.Equal(AccessOperation.AtomicReadModifyWrite, Assert.Single(run.Of("_slot")).Operation);
    }

    /// <summary>The compare-and-swap loop: the stale read the swap is given feeds it, so it is folded into the one atomic
    /// read-modify-write as its read source, the way any read-modify-write's loads are.</summary>
    [Fact]
    public void Interlocked_compare_exchange_is_an_atomic_read_modify_write()
    {
        var run = Analyze("""
            public class RunningTotalController : ControllerBase
            {
                private static int _total;
                public void Post(int sample)
                {
                    var current = _total;
                    Interlocked.CompareExchange(ref _total, current + sample, current);
                }
            }
            """ + Startup());

        var access = Assert.Single(run.Of("_total"));
        Assert.Equal(AccessOperation.AtomicReadModifyWrite, access.Operation);
        Assert.Single(access.ReadSources);
    }

    [Fact]
    public void Interlocked_read_is_an_atomic_read()
    {
        var run = Analyze("""
            public class ClockController : ControllerBase
            {
                private static long _ticks;
                public long Get() => Interlocked.Read(ref _ticks);
            }
            """ + Startup());

        Assert.Equal(AccessOperation.AtomicRead, Assert.Single(run.Of("_ticks")).Operation);
        Assert.Equal("atomic-read", AccessOperation.AtomicRead.ToWireName());
    }

    [Fact]
    public void Volatile_read_is_an_atomic_read()
    {
        var run = Analyze("""
            public class FlagController : ControllerBase
            {
                private static bool _stop;
                public bool Get() => Volatile.Read(ref _stop);
            }
            """ + Startup());

        Assert.Equal(AccessOperation.AtomicRead, Assert.Single(run.Of("_stop")).Operation);
    }

    [Fact]
    public void Volatile_write_is_an_atomic_write()
    {
        var run = Analyze("""
            public class StopController : ControllerBase
            {
                private static bool _stop;
                public void Post() => Volatile.Write(ref _stop, true);
            }
            """ + Startup());

        Assert.Equal(AccessOperation.AtomicWrite, Assert.Single(run.Of("_stop")).Operation);
        Assert.Equal("atomic-write", AccessOperation.AtomicWrite.ToWireName());
    }

    [Fact]
    public void Read_of_a_volatile_field_is_an_atomic_read()
    {
        var run = Analyze("""
            public class StateController : ControllerBase
            {
                private static volatile string? _state;
                public string? Get() => _state;
                public void Post(string state) => _state = state;
            }
            """ + Startup());

        Assert.Equal(AccessOperation.AtomicRead, run.Single("_state", AccessOperation.AtomicRead).Operation);
    }

    [Fact]
    public void Write_of_a_volatile_field_is_an_atomic_write()
    {
        var run = Analyze("""
            public class OwnerController : ControllerBase
            {
                private static volatile string? _owner;
                public void Post(string owner) => _owner = owner;
            }
            """ + Startup());

        Assert.Equal(AccessOperation.AtomicWrite, Assert.Single(run.Of("_owner")).Operation);
    }

    /// <summary>`volatile` orders each read and each write, but a read-modify-write is two of them, so it is not atomic.</summary>
    [Fact]
    public void Read_modify_write_of_a_volatile_field_is_not_atomic()
    {
        var run = Analyze("""
            public class HitCountController : ControllerBase
            {
                private static volatile int _hits;
                public void Post() => _hits++;
            }
            """ + Startup());

        Assert.Equal(AccessOperation.ReadModifyWrite, Assert.Single(run.Of("_hits")).Operation);
    }

    [Fact]
    public async Task Two_atomic_operations_on_one_cell_are_no_finding()
    {
        var result = await Findings("""
            public sealed class Gauge { public int Level; }
            public sealed class RaiseWorker : BackgroundService
            {
                private readonly Gauge _gauge;
                public RaiseWorker(Gauge gauge) => _gauge = gauge;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    Interlocked.Increment(ref _gauge.Level);
                    return Task.CompletedTask;
                }
            }
            public class GaugeController : ControllerBase
            {
                private readonly Gauge _gauge;
                public GaugeController(Gauge gauge) => _gauge = gauge;
                public void Post() => Interlocked.Decrement(ref _gauge.Level);
            }
            """ + Startup("services.AddSingleton<Gauge>(); services.AddHostedService<RaiseWorker>();"));

        Assert.Empty(result.Findings);
        Assert.True(result.Pairs.Skips.GetValueOrDefault(InterproceduralPairing.SKIP_NO_CONFLICTING_OPERATION) > 0);
    }

    [Fact]
    public async Task An_atomic_operation_against_a_plain_write_is_a_finding()
    {
        var result = await Findings("""
            public sealed class Gauge { public int Level; }
            public sealed class ResetWorker : BackgroundService
            {
                private readonly Gauge _gauge;
                public ResetWorker(Gauge gauge) => _gauge = gauge;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _gauge.Level = 0;
                    return Task.CompletedTask;
                }
            }
            public class GaugeController : ControllerBase
            {
                private readonly Gauge _gauge;
                public GaugeController(Gauge gauge) => _gauge = gauge;
                public void Post() => Interlocked.Increment(ref _gauge.Level);
            }
            """ + Startup("services.AddSingleton<Gauge>(); services.AddHostedService<ResetWorker>();"));

        var finding = Assert.Single(result.Findings);
        Assert.Equal("DCA1001", finding.RuleId);
        Assert.Equal([AccessOperation.Write, AccessOperation.AtomicReadModifyWrite],
                     new[] { finding.AccessA.Operation, finding.AccessB.Operation }.Order().ToArray());
    }

    /// <summary>A memory barrier names no cell: inventing an access or a protection for it would be a defect, so the unprotected
    /// write beside it is still reported.</summary>
    [Fact]
    public async Task Interlocked_memory_barrier_is_neither_an_access_nor_a_protection()
    {
        const string source = """
            public class BarrierController : ControllerBase
            {
                private static int _flag;
                public void Post()
                {
                    Interlocked.MemoryBarrier();
                    _flag = 1;
                    Interlocked.MemoryBarrierProcessWide();
                }
            }
            """;

        var run = Analyze(source + Startup());
        var access = Assert.Single(run.Of("_flag"));
        Assert.Equal(AccessOperation.Write, access.Operation);
        Assert.Empty(access.HeldProtection);

        var result = await Findings(source + Startup());
        Assert.Equal("DCA1001", Assert.Single(result.Findings).RuleId);
    }

    /// <summary>What one call does atomically and what a sequence of them does are two questions (R1): an exchange is handed a
    /// value an earlier read produced, writes it whatever the cell holds by then, and so loses every update made in between.
    /// Only a compare-and-swap, which is told the value it expects to find, keeps the sequence as atomic as the call.</summary>
    [Fact]
    public async Task An_exchange_of_a_value_an_earlier_read_produced_is_a_lost_update()
    {
        var source = """
            public class SequenceController : ControllerBase
            {
                private static int _total;
                public void Post()
                {
                    var current = Volatile.Read(ref _total);
                    Interlocked.Exchange(ref _total, current + 1);
                }
            }
            """;

        var access = Assert.Single(Analyze(source + Startup()).Of("_total"), candidate => candidate.Operation != AccessOperation.AtomicRead);
        Assert.Equal(AccessOperation.ReadModifyWrite, access.Operation);

        var result = await Findings(source + Startup());
        Assert.Contains("DCA1002", result.Findings.Select(finding => finding.RuleId));
    }

    /// <summary>A compare-and-swap keeps a sequence atomic only by verifying the read that sequence was built from. Handed
    /// another read of the same cell as its comparand, it checks a value nothing was computed from: the write between the two
    /// reads is overwritten exactly as by any other read-modify-write (R1).</summary>
    [Fact]
    public async Task A_compare_exchange_that_checks_another_read_is_a_lost_update()
    {
        var source = """
            public class DriftController : ControllerBase
            {
                private static int _total;
                public void Post()
                {
                    var old = Volatile.Read(ref _total);
                    Interlocked.CompareExchange(ref _total, old + 1, Volatile.Read(ref _total));
                }
            }
            """;

        var access = Assert.Single(Analyze(source + Startup()).Of("_total"),
                                   candidate => candidate.Operation is not AccessOperation.AtomicRead);
        Assert.Equal(AccessOperation.ReadModifyWrite, access.Operation);

        var result = await Findings(source + Startup());
        Assert.Contains("DCA1002", result.Findings.Select(finding => finding.RuleId));
    }

    /// <summary>The comparand has to be the value the sequence observed, not a value computed from it: `old + 2` depends on the
    /// read of `old` as plainly as `old` does and is a number nobody ever saw in the cell, so a swap checking it proves
    /// nothing about what happened in between (R1).</summary>
    [Fact]
    public async Task A_compare_exchange_that_checks_a_value_computed_from_its_read_is_a_lost_update()
    {
        var source = """
            public class SkewController : ControllerBase
            {
                private static int _total;
                public void Post()
                {
                    var old = Volatile.Read(ref _total);
                    Interlocked.CompareExchange(ref _total, old + 1, old + 2);
                }
            }
            """;

        var access = Assert.Single(Analyze(source + Startup()).Of("_total"),
                                   candidate => candidate.Operation is not AccessOperation.AtomicRead);
        Assert.Equal(AccessOperation.ReadModifyWrite, access.Operation);

        var result = await Findings(source + Startup());
        Assert.Contains("DCA1002", result.Findings.Select(finding => finding.RuleId));
    }

    /// <summary>A cell of an array is a cell like any other, and nothing else in the program says this call changes it at all
    /// (R1, ADR 0010).</summary>
    [Fact]
    public async Task An_interlocked_increment_of_an_element_conflicts_with_a_plain_write_of_that_element()
    {
        var source = """
            public sealed class Counters { public readonly int[] Slots = new int[4]; }
            public sealed class BumpWorker : BackgroundService
            {
                private readonly Counters _counters;
                public BumpWorker(Counters counters) => _counters = counters;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    Interlocked.Increment(ref _counters.Slots[0]);
                    return Task.CompletedTask;
                }
            }
            public sealed class ResetWorker : BackgroundService
            {
                private readonly Counters _counters;
                public ResetWorker(Counters counters) => _counters = counters;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _counters.Slots[0] = 0;
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Counters>(); services.AddHostedService<BumpWorker>(); " +
                          "services.AddHostedService<ResetWorker>();");

        var run = Analyze(source);
        Assert.Equal(AccessOperation.AtomicReadModifyWrite,
                     Assert.Single(run.Collection.Accesses, access => access.Symbol.StartsWith("BumpWorker", StringComparison.Ordinal) &&
                                                                       access.Resource.AccessPath.Count == 2).Operation);

        var result = await Findings(source);
        Assert.Contains("Slots.[0]",
                        result.Findings.Select(finding => string.Join(".", finding.Resource.AccessPath)));
    }

    /// <summary>A <c>Span</c> is indexed by a ref-returning indexer and not by the array element reference an array gets, so an
    /// atomic call on one of its cells is an atomic access of that cell all the same, and its conflict with a plain write of the
    /// same cell is the same conflict (R1, TD-043).</summary>
    [Fact]
    public async Task An_interlocked_increment_of_a_span_element_conflicts_with_a_plain_write_of_that_element()
    {
        var source = """
            public sealed class Counters { public readonly int[] Slots = new int[4]; }
            public sealed class SpanBumpWorker : BackgroundService
            {
                private readonly Counters _counters;
                public SpanBumpWorker(Counters counters) => _counters = counters;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var cells = _counters.Slots.AsSpan();
                    Interlocked.Increment(ref cells[0]);
                    return Task.CompletedTask;
                }
            }
            public sealed class SpanResetWorker : BackgroundService
            {
                private readonly Counters _counters;
                public SpanResetWorker(Counters counters) => _counters = counters;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _counters.Slots[0] = 0;
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Counters>(); services.AddHostedService<SpanBumpWorker>(); " +
                          "services.AddHostedService<SpanResetWorker>();");

        var run = Analyze(source);
        Assert.Equal(AccessOperation.AtomicReadModifyWrite,
                     Assert.Single(run.Collection.Accesses, access => access.Symbol.StartsWith("SpanBumpWorker", StringComparison.Ordinal) &&
                                                                       access.Resource.AccessPath.Count == 2).Operation);

        var result = await Findings(source);
        Assert.Contains("Slots.[0]", result.Findings.Select(finding => string.Join(".", finding.Resource.AccessPath)));
    }

    /// <summary>The same for a <c>Volatile</c> member, which names its cell the same way: every member that names one cell
    /// names a cell of a span as well as a cell of an array (R1, TD-043).</summary>
    [Fact]
    public void A_volatile_write_through_a_span_index_is_an_atomic_write_of_that_cell()
    {
        var run = Analyze("""
            public sealed class Levels { public readonly int[] Marks = new int[4]; }
            public sealed class MarkWorker : BackgroundService
            {
                private readonly Levels _levels;
                public MarkWorker(Levels levels) => _levels = levels;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var cells = _levels.Marks.AsSpan();
                    Volatile.Write(ref cells[0], 5);
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Levels>(); services.AddHostedService<MarkWorker>();"));

        var access = Assert.Single(run.Collection.Accesses, candidate => candidate.Resource.AccessPath.Count == 2);
        Assert.Equal((AccessOperation.AtomicWrite, "[0]"), (access.Operation, access.Resource.Selector!.Text));
    }

    /// <summary>An argument is bound to the parameter it names, not to the place it stands in: a call that writes the cell
    /// second is the same call, and reading by position would take the value for the cell and model no access at all (R9).</summary>
    [Fact]
    public async Task An_interlocked_call_with_named_arguments_out_of_order_still_names_its_cell()
    {
        var source = """
            public class NamedController : ControllerBase
            {
                private static int _total;
                public void Post() => Interlocked.Exchange(value: 1, location1: ref _total);
            }
            """;

        var access = Assert.Single(Analyze(source + Startup()).Of("_total"));
        Assert.Equal(AccessOperation.AtomicReadModifyWrite, access.Operation);

        var result = await Findings(source + Startup());
        Assert.Empty(result.Findings);
    }

    private static async Task<AnalysisResult> Findings(string source) =>
        await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Usings + source)), ROOT_DIRECTORY,
                                            ProviderRegistry.BuiltIn, CancellationToken.None);
}
