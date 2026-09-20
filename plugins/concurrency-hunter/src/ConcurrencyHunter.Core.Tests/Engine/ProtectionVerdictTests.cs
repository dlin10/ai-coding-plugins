using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The five-valued protection verdict of SPEC 7, the rule it classifies a pair into, and R2: what protects an operation
/// spanning a read and the write depending on it is only a lock section that covers the whole span.</summary>
public sealed class ProtectionVerdictTests
{
    private const string Shared = """
        public static class Gates { public static readonly object First = new(); public static readonly object Second = new(); }
        public sealed class Tally { public int Value; }

        """;

    private const string Registrations = "services.AddSingleton<Tally>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();";

    [Fact]
    public async Task Two_unprotected_writes_are_unprotected()
    {
        var result = await Analyze(Worker("First", "_tally.Value = 1;") + Worker("Second", "_tally.Value = 2;"));

        Assert.Equal(PairProtection.UNPROTECTED, Assert.Single(result.Findings).ProtectionResult);
    }

    [Fact]
    public async Task A_protected_side_against_an_unprotected_side_is_partial()
    {
        var result = await Analyze(Worker("First", "lock (Gates.First) { _tally.Value = 1; }") + Worker("Second", "_tally.Value = 2;"));

        var finding = Assert.Single(result.Findings);
        Assert.Equal((PairProtection.PARTIAL, "DCA1003"), (finding.ProtectionResult, finding.RuleId));
    }

    [Fact]
    public async Task Two_different_static_locks_are_different_identity()
    {
        var result = await Analyze(Worker("First", "lock (Gates.First) { _tally.Value = 1; }") +
                                   Worker("Second", "lock (Gates.Second) { _tally.Value = 2; }"));

        var finding = Assert.Single(result.Findings);
        Assert.Equal((PairProtection.DIFFERENT_IDENTITY, "DCA1003"), (finding.ProtectionResult, finding.RuleId));
    }

    /// <summary>Two readers of one lock may hold it at the same time, so they do not exclude each other although the object is
    /// one. Nothing lowers a mode other than exclusive yet, so the verdict is asked directly.</summary>
    [Fact]
    public void Two_readers_of_one_lock_are_incompatible_mode()
    {
        var first = Holding(IrLockMode.Read);
        var second = Holding(IrLockMode.Read);

        Assert.Equal(PairProtection.INCOMPATIBLE_MODE, PairProtection.Of(first, second));
    }

    [Fact]
    public void A_writer_of_one_lock_excludes_a_reader_of_it()
    {
        var first = Holding(IrLockMode.Write);
        var second = Holding(IrLockMode.Read);

        Assert.Equal(PairProtection.SUFFICIENT, PairProtection.Of(first, second));
    }

    [Fact]
    public async Task One_lock_held_by_both_sides_is_sufficient()
    {
        var result = await Analyze(Worker("First", "lock (Gates.First) { _tally.Value = 1; }") +
                                   Worker("Second", "lock (Gates.First) { _tally.Value = 2; }"));

        Assert.Equal(0, result.Pairs.Candidates);
        Assert.Equal(1, result.Pairs.Suppressed);
    }

    /// <summary>The verdict is carried by the accesses, so the mode of a lock statement has to reach them for a later primitive
    /// with modes of its own to be judged at all.</summary>
    [Fact]
    public async Task The_mode_of_a_lock_statement_reaches_the_access()
    {
        var result = await Analyze(Worker("First", "lock (Gates.First) { _tally.Value = 1; }") + Worker("Second", "_tally.Value = 2;"));

        var locked = Assert.Single(result.Accesses, access => access.Resource.Member.Name == "Value" && access.HeldProtectionIds.Count == 1);
        Assert.Equal(new HeldProtection(IrSynchronizationPrimitive.Monitor, IrLockMode.Exclusive, true),
                     Assert.Single(locked.HeldProtections[locked.HeldProtectionIds[0]]));
    }

    [Fact]
    public async Task A_sufficient_verdict_takes_the_candidate_away()
    {
        var result = await Analyze(Worker("First", "lock (Gates.First) { _tally.Value = 1; }") +
                                   Worker("Second", "lock (Gates.First) { _tally.Value = 2; }"));

        Assert.Empty(result.Findings);
        Assert.Equal(0, result.Pairs.Candidates);
    }

    [Fact]
    public async Task An_unprotected_pair_stays_DCA1001()
    {
        var result = await Analyze(Worker("First", "_tally.Value = 1;") + Worker("Second", "_tally.Value = 2;"));

        var finding = Assert.Single(result.Findings);
        Assert.Equal(("DCA1001", PairProtection.UNPROTECTED), (finding.RuleId, finding.ProtectionResult));
    }

    /// <summary>A lost update is classified as one however protected the pair is: the protection rule only takes what is left.</summary>
    [Fact]
    public async Task A_lost_update_keeps_its_own_rule_under_partial_protection()
    {
        var result = await Analyze(Worker("First", "lock (Gates.First) { _tally.Value++; }") + Worker("Second", "_tally.Value = 0;"));

        var finding = Assert.Single(result.Findings);
        Assert.Equal(("DCA1002", PairProtection.PARTIAL), (finding.RuleId, finding.ProtectionResult));
    }

    /// <summary>One site pair reached under two protections is one finding, and the one it reports is the least protected.</summary>
    [Fact]
    public async Task A_folded_finding_reports_its_least_protected_occurrence()
    {
        var result = await Analyze(Shared + """
            public sealed class Ledger
            {
                private readonly Tally _tally;
                public Ledger(Tally tally) => _tally = tally;
                public void Bump() => _tally.Value = 1;
            }
            public sealed class FirstWorker : BackgroundService
            {
                private readonly Ledger _ledger;
                public FirstWorker(Ledger ledger) => _ledger = ledger;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    lock (Gates.First) { _ledger.Bump(); }
                    return Task.CompletedTask;
                }
            }
            public sealed class SecondWorker : BackgroundService
            {
                private readonly Ledger _ledger;
                public SecondWorker(Ledger ledger) => _ledger = ledger;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _ledger.Bump();
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Tally>(); services.AddSingleton<Ledger>(); services.AddHostedService<FirstWorker>();" +
                          "services.AddHostedService<SecondWorker>();"),
            registered: true);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(PairProtection.PARTIAL, finding.ProtectionResult);
        Assert.Equal("DCA1003", finding.RuleId);
    }

    /// <summary>Folding keeps the least protected occurrence, and a pair under two different locks is less protected than one
    /// where only one side holds anything.</summary>
    [Fact]
    public async Task Different_identity_outranks_partial_when_a_finding_folds_both()
    {
        var result = await Analyze(Shared + """
            public sealed class Ledger
            {
                private readonly Tally _tally;
                public Ledger(Tally tally) => _tally = tally;
                public void Bump() => _tally.Value = 1;
            }
            public sealed class FirstWorker : BackgroundService
            {
                private readonly Ledger _ledger;
                public FirstWorker(Ledger ledger) => _ledger = ledger;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    lock (Gates.First) { _ledger.Bump(); }
                    return Task.CompletedTask;
                }
            }
            public sealed class SecondWorker : BackgroundService
            {
                private readonly Ledger _ledger;
                public SecondWorker(Ledger ledger) => _ledger = ledger;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    lock (Gates.Second) { _ledger.Bump(); }
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Tally>(); services.AddSingleton<Ledger>(); services.AddHostedService<FirstWorker>();" +
                          "services.AddHostedService<SecondWorker>();"),
            registered: true);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(PairProtection.DIFFERENT_IDENTITY, finding.ProtectionResult);
    }

    /// <summary>A lock on an object the call allocates is a different object every time, so it excludes nobody.</summary>
    [Fact]
    public async Task A_lock_on_a_fresh_object_in_every_call_is_different_identity()
    {
        var result = await Analyze(Shared + """
            public sealed class FirstWorker : BackgroundService
            {
                private readonly Tally _tally;
                public FirstWorker(Tally tally) => _tally = tally;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    lock (new object()) { _tally.Value = 1; }
                    return Task.CompletedTask;
                }
            }
            public sealed class SecondWorker : BackgroundService
            {
                private readonly Tally _tally;
                public SecondWorker(Tally tally) => _tally = tally;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    lock (new object()) { _tally.Value = 2; }
                    return Task.CompletedTask;
                }
            }
            """ + Startup(Registrations), registered: true);

        var finding = Assert.Single(result.Findings);
        Assert.Equal((PairProtection.DIFFERENT_IDENTITY, "DCA1003"), (finding.ProtectionResult, finding.RuleId));
        Assert.Empty(finding.AccessA.HeldProtectionIds);
    }

    /// <summary>R2: the update is lost between the unprotected read and the protected write, so the lock around the write alone
    /// protects nothing.</summary>
    [Fact]
    public async Task A_lock_around_only_the_write_of_a_read_modify_write_is_not_sufficient()
    {
        var result = await Analyze(Worker("First", """
                                       var current = _tally.Value;
                                       lock (Gates.First) { _tally.Value = current + 1; }
                                       """) +
                                   Worker("Second", "lock (Gates.First) { _tally.Value = 0; }"));

        var finding = Assert.Single(result.Findings);
        Assert.Equal(PairProtection.PARTIAL, finding.ProtectionResult);
        Assert.Empty(Assert.Single(result.Accesses, access => access.Operation == AccessOperation.ReadModifyWrite).HeldProtection);
    }

    /// <summary>R2: two sections of one lock, one around the read and one around the write, leave the span between them open.</summary>
    [Fact]
    public async Task Separate_sections_around_the_read_and_around_the_write_are_not_sufficient()
    {
        var result = await Analyze(Worker("First", """
                                       int current;
                                       lock (Gates.First) { current = _tally.Value; }
                                       lock (Gates.First) { _tally.Value = current + 1; }
                                       """) +
                                   Worker("Second", "lock (Gates.First) { _tally.Value = 0; }"));

        var finding = Assert.Single(result.Findings);
        Assert.Equal(PairProtection.PARTIAL, finding.ProtectionResult);
        Assert.Empty(Assert.Single(result.Accesses, access => access.Operation == AccessOperation.ReadModifyWrite).HeldProtection);
    }

    /// <summary>R2: one section that covers the read and the write is the protection of the whole operation.</summary>
    [Fact]
    public async Task One_section_around_both_the_read_and_the_write_is_sufficient()
    {
        var result = await Analyze(Worker("First", """
                                       lock (Gates.First)
                                       {
                                           var current = _tally.Value;
                                           _tally.Value = current + 1;
                                       }
                                       """) +
                                   Worker("Second", "lock (Gates.First) { _tally.Value = 0; }"));

        Assert.Empty(result.Findings);
        Assert.Equal(1, result.Pairs.Suppressed);
    }

    /// <summary>R2: one acquisition inside a loop opens a new section on every iteration, so the read of one iteration and the
    /// write that depends on it in the next one are not under one section, however equal the two sites are.</summary>
    [Fact]
    public async Task A_read_and_the_write_of_the_next_iteration_are_not_one_section()
    {
        var result = await Analyze(Worker("First", """
                                       var carried = 0;
                                       for (var i = 0; i < 4; i++)
                                           lock (Gates.First)
                                           {
                                               _tally.Value = carried + 1;
                                               carried = _tally.Value;
                                           }
                                       """) +
                                   Worker("Second", "lock (Gates.First) { _tally.Value = 0; }"));

        Assert.Empty(Assert.Single(result.Accesses, access => access.Operation == AccessOperation.ReadModifyWrite).HeldProtection);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(PairProtection.PARTIAL, finding.ProtectionResult);
    }

    /// <summary>One hosted service that takes <c>Tally</c> and runs <paramref name="body"/>.</summary>
    private static string Worker(string name, string body) => $$"""
        public sealed class {{name}}Worker : BackgroundService
        {
            private readonly Tally _tally;
            public {{name}}Worker(Tally tally) => _tally = tally;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
                return Task.CompletedTask;
            }
        }
        """;

    /// <summary>An access holding one lock whose object is known, in a mode; the verdict reads nothing else.</summary>
    internal static Access Holding(IrLockMode mode, IrSynchronizationPrimitive primitive = IrSynchronizationPrimitive.Monitor,
                                   bool isExclusive = true) =>
        Holding(new HeldProtection(primitive, mode, isExclusive));

    /// <summary>An access holding one lock object by every one of the given mechanisms at once.</summary>
    internal static Access Holding(params HeldProtection[] holdings)
    {
        const string id = "scope:Fixture|static:Gates.First";
        var resource = FindingTestData.Resource("di:Tally@Singleton", "Value");
        return FindingTestData.Access(resource, AccessOperation.Write, "root", "Worker.ExecuteAsync(CancellationToken)",
                                      new SourceSpan("Case.cs", 1, 1, 1, 2), ["static:Gates.First"], [id]) with
        {
            HeldProtections = new Dictionary<string, IReadOnlyList<HeldProtection>>(StringComparer.Ordinal)
            {
                [id] = holdings
            }
        };
    }

    private static async Task<AnalysisResult> Analyze(string source, bool registered = false) =>
        await PhaseOneAnalyzer.AnalyzeAsync(
            FixtureSolution.Create(("Case.cs", Usings + (registered ? source : Shared + source + Startup(Registrations)))),
            ROOT_DIRECTORY, ProviderRegistry.BuiltIn, CancellationToken.None);
}
