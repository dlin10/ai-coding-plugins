using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

public sealed class GenerationRecordTests
{
    private static readonly GenerationRequest REQUEST = new(ASSEMBLY, "1.0", "M:Lib.Api.Run(System.Action)", null, null);

    private static LibraryCompilationResult Library() => Compile(PRELUDE +
        "public static class Api { public static void Run(Action a) => a(); } }");

    [Fact]
    public void Stage_seconds_add_up_to_the_generation_seconds()
    {
        var clock = new FakeClock();
        var trace = ModelGenerator.TraceWithin(REQUEST, Library(), TimeSpan.FromHours(1), clock,
            checkReported: (_, _) => clock.Advance(TimeSpan.FromSeconds(0.1)));

        Assert.NotNull(trace.Answer.Model);
        Assert.True(trace.Answer.Generation.Seconds > 0);
        AssertTotal(trace.Answer.Generation);
        Assert.Contains("engineClaims", trace.Answer.Generation.Stages.Keys);
        Assert.Contains("buildModel", Assert.Single(trace.Answer.Generation.Attempts).Stages.Keys);
    }

    [Fact]
    public void An_answer_after_a_retry_records_both_attempts()
    {
        var trace = ModelGenerator.Trace(REQUEST, Library(), CancellationToken.None, initializers: TypeInitializerTreatment.Off);

        Assert.Equal("off", trace.Answer.Generation.Mode);
        Assert.Equal(new[] { "inert", "off" }, trace.Answer.Generation.Attempts.Select(attempt => attempt.Mode));
        Assert.All(trace.Answer.Generation.Attempts, attempt =>
        {
            Assert.True(attempt.ReachedBodies > 0);
            Assert.Null(attempt.ConfirmationBodies);
            Assert.Contains("buildModel", attempt.Stages.Keys);
        });
        AssertTotal(trace.Answer.Generation);
    }

    [Fact]
    public void Top_level_reached_bodies_are_those_of_the_answers_attempt()
    {
        var library = Compile(PRELUDE + """
            public static class Extra { public static object Make() => new object(); }
            public static class Api {
                private static object X = Extra.Make();
                public static void Run(Action a) => a();
            } }
            """);
        var trace = ModelGenerator.Trace(REQUEST, library, CancellationToken.None, initializers: TypeInitializerTreatment.Off);
        var record = trace.Answer.Generation;

        Assert.Equal(2, record.Attempts.Count);
        Assert.NotEqual(record.Attempts[0].ReachedBodies, record.Attempts[1].ReachedBodies);
        Assert.Equal(record.Attempts[1].ReachedBodies, record.ReachedBodies);
        Assert.Equal(trace.Run!.Reachable.ReachedBodies.Count, record.ReachedBodies);
    }

    [Fact]
    public void A_refusal_before_any_attempt_has_no_mode()
    {
        var trace = ModelGenerator.Trace(REQUEST with { MemberId = "M:Lib.Api.Missing" }, Library(), CancellationToken.None);

        Assert.Equal(GenerationReasons.MEMBER_NOT_FOUND, trace.Answer.Reason);
        Assert.Null(trace.Answer.Generation.Mode);
        Assert.Empty(trace.Answer.Generation.Attempts);
        Assert.NotEmpty(trace.Answer.Generation.Stages);
        AssertTotal(trace.Answer.Generation);
    }

    [Fact]
    public void A_timeout_answer_carries_the_stages_up_to_the_cut()
    {
        var clock = new FakeClock();
        var trace = ModelGenerator.TraceWithin(REQUEST, Library(), TimeSpan.FromSeconds(2), clock,
            checkReported: (stage, _) =>
            {
                if (stage == GenerationStage.BuildModel)
                    clock.Advance(TimeSpan.FromSeconds(1));
            });

        Assert.Equal(GenerationReasons.TIMEOUT, trace.Answer.Reason);
        Assert.Null(trace.Answer.Model);
        Assert.NotNull(trace.Driver);
        Assert.Equal("inert", trace.Answer.Generation.Mode);
        Assert.Equal(2, trace.Answer.Generation.Seconds);
        Assert.Equal(2, Assert.Single(trace.Answer.Generation.Attempts).Stages["buildModel"]);
        AssertTotal(trace.Answer.Generation);
    }

    [Fact]
    public void The_member_limit_covers_both_attempts()
    {
        var clock = new FakeClock();
        var complete = ModelGenerator.TraceWithin(REQUEST, Library(), TimeSpan.FromHours(1), clock,
            checkReported: (_, _) => clock.Advance(TimeSpan.FromSeconds(0.1)), initializers: TypeInitializerTreatment.Off);
        var full = complete.Answer.Generation;
        var beforeOff = full.Stages.Values.Sum() + full.Attempts[0].Stages.Values.Sum();
        var limit = TimeSpan.FromSeconds(beforeOff + full.Attempts[1].Stages.Values.Sum() / 2);
        Assert.All(full.Attempts, attempt => Assert.True(attempt.Stages.Values.Sum() < limit.TotalSeconds));

        clock = new FakeClock();
        var trace = ModelGenerator.TraceWithin(REQUEST, Library(), limit, clock,
            checkReported: (_, _) => clock.Advance(TimeSpan.FromSeconds(0.1)), initializers: TypeInitializerTreatment.Off);
        var record = trace.Answer.Generation;

        Assert.Equal(GenerationReasons.TIMEOUT, trace.Answer.Reason);
        Assert.Equal("off", record.Mode);
        Assert.Equal(2, record.Attempts.Count);
        Assert.Contains("buildModel", record.Attempts[0].Stages.Keys);
        Assert.Equal(full.Attempts[0].ReachedBodies, record.Attempts[0].ReachedBodies);
        Assert.True(record.Attempts[1].Stages.Values.Sum() < full.Attempts[1].Stages.Values.Sum());
        Assert.Equal(Math.Round(limit.TotalSeconds, 1), record.Seconds);
        AssertTotal(record);
    }

    [Fact]
    public void An_outside_cancellation_is_not_a_timeout()
    {
        using var outside = new CancellationTokenSource();
        var clock = new FakeClock();
        Assert.ThrowsAny<OperationCanceledException>(() => ModelGenerator.TraceWithin(REQUEST, Library(), TimeSpan.FromSeconds(2), clock,
            outside.Token, (stage, _) =>
            {
                if (stage == GenerationStage.Analyze)
                {
                    outside.Cancel();
                    clock.Advance(TimeSpan.FromSeconds(2));
                }
            }));
    }

    [Fact]
    public void Repeated_and_parallel_checks_of_one_stage_do_not_split_its_time()
    {
        var clock = new FakeClock();
        var checks = new GenerationChecks(CancellationToken.None, time: clock);
        checks.Check(GenerationStage.CompileLibrary);
        clock.Advance(TimeSpan.FromSeconds(1));
        Parallel.For(0, 20, _ => checks.Check(GenerationStage.CompileLibrary));
        clock.Advance(TimeSpan.FromSeconds(1));
        checks.Check(GenerationStage.EngineClaims);
        clock.Advance(TimeSpan.FromSeconds(1));

        var stages = Assert.Single(checks.Snapshot());
        Assert.Equal(2, stages["compileLibrary"]);
        Assert.Equal(1, stages["engineClaims"]);
        Assert.Equal(3, checks.Seconds);
    }

    private static void AssertTotal(GenerationRecord record) =>
        Assert.InRange(Math.Abs(record.Stages.Values.Sum() + record.Attempts.Sum(attempt => attempt.Stages.Values.Sum()) - record.Seconds), 0, 0.001);
}
