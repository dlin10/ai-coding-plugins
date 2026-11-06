using System.Collections.Concurrent;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The recording's workers and the member limit on the system clock (ADR 0033): how many workers a run has, a worker that waits for
/// what a member in flight queues, a worker that throws ending every worker, and a member that waits without spending CPU time.</summary>
public sealed partial class RunGenerationTests
{
    [Fact]
    public void The_recording_drains_its_queue_on_four_workers_and_every_other_run_on_one()
    {
        Assert.Equal(4, RunGeneration.WorkersOf(GenerationBudget.Recording));
        Assert.Equal(1, RunGeneration.WorkersOf(GenerationBudget.Run));
    }

    [Fact]
    public void A_worker_that_finds_the_queue_empty_waits_for_what_the_member_in_flight_queues()
    {
        var project = Project();
        var alpha = Candidate(project, "Alpha");
        var (gamma, delta) = (Target(project, "Gamma"), Target(project, "Delta"));
        using var both = new Barrier(2);
        var together = new ConcurrentBag<string>();
        var subject = OnTheSystemClock((request, read, _) =>
        {
            if (Name(request) == "Alpha")
                return Seeing(request, read, gamma, delta);
            // Alpha's answer queued Gamma and Delta. The worker that found the queue empty while Alpha was in flight waited, so both are
            // generated at once.
            if (both.SignalAndWait(TimeSpan.FromSeconds(10)))
                together.Add(Name(request));
            return Seeing(request, read);
        }, workers: 2);

        subject.Generate(LibraryModels.BuiltIn, [alpha]);

        Assert.Equal(["Delta", "Gamma"], together.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_worker_that_throws_ends_the_waiting_workers_and_the_round_throws_its_exception()
    {
        var project = Project();
        var (alpha, beta) = (Candidate(project, "Alpha"), Candidate(project, "Beta"));
        using var cancellation = new CancellationTokenSource();
        using var betaDone = new ManualResetEventSlim();
        var subject = OnTheSystemClock((request, read, token) =>
        {
            if (Name(request) == "Beta")
            {
                betaDone.Set();
                return Seeing(request, read);
            }

            // Alpha is still in flight when the other worker, done with Beta, finds the queue empty and waits; then the run is cancelled.
            betaDone.Wait(TimeSpan.FromSeconds(10));
            Thread.Sleep(200);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Seeing(request, read);
        }, workers: 2, token: cancellation.Token);

        var round = Task.Run(() => subject.Generate(LibraryModels.BuiltIn, [alpha, beta]));

        Assert.True(((IAsyncResult)round).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)), "the waiting worker never ended");
        Assert.IsAssignableFrom<OperationCanceledException>(round.Exception?.InnerException);
    }

    [Fact]
    public void A_member_that_waits_past_its_limit_without_spending_cpu_time_is_not_cut()
    {
        var alpha = Candidate(Project(), "Alpha");
        var subject = OnTheSystemClock((request, read, token) =>
        {
            // Twice the limit passes in a wait that spends no CPU time; a cut would end the wait and the generation.
            token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(600));
            token.ThrowIfCancellationRequested();
            return Seeing(request, read);
        }, workers: 1, memberLimit: TimeSpan.FromMilliseconds(300));

        subject.Generate(LibraryModels.BuiltIn, [alpha]);

        Assert.Equal(GenerationOutcomeKind.Model, subject.AnswerOf(alpha.Target)!.Outcome.Kind);
    }

    /// <summary>A run's generation over this fixture's repository and calls on the system clock, so that the member limit reads each
    /// member's thread's own CPU time; every assembly ranks 0.</summary>
    /// <param name="generate">The generator call.</param>
    /// <param name="workers">The threads that drain a round's queue.</param>
    /// <param name="memberLimit">The member's limit; a minute when omitted.</param>
    /// <param name="token">The run's token.</param>
    private RunGeneration OnTheSystemClock(Func<GenerationRequest, DependencyAnswers, CancellationToken, GeneratedAnswer> generate, int workers,
                                           TimeSpan? memberLimit = null, CancellationToken token = default) =>
        new(_repository, token, Seconds(1000), memberLimit ?? Seconds(60), TimeProvider.System, (request, dependencies, cancellation) =>
        {
            lock (_calls)
                _calls.Add(request);
            return generate(request, dependencies, cancellation);
        }, (_, _) => 0, workers);
}
