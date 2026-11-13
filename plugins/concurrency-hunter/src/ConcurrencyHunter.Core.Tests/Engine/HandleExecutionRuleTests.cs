using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class HandleExecutionRuleTests
{
    [Fact]
    public void Own_start_awaited_in_the_same_stretch_names_the_waiting_execution() =>
        Check("var own = Task.Run(Write); await own;", accepted: true);

    [Fact]
    public void A_suspension_between_start_and_wait_names_no_execution() =>
        Check("var own = Task.Run(Write); await Task.Yield(); await own;", accepted: false);

    [Fact]
    public void A_wait_reached_again_in_an_inner_loop_names_no_execution() =>
        Check("var own = Task.Run(Write); do { await own; } while (Again());", accepted: false);

    [Fact]
    public void A_wait_reached_again_after_a_caught_exception_names_no_execution() =>
        Check("var own = Task.Run(Write); while (true) { try { await own; break; } catch (InvalidOperationException) { } }", accepted: false);

    [Fact]
    public void A_handle_read_from_a_field_names_no_execution() =>
        Check("_own = Task.Run(Write); await _own;", accepted: false);

    private static void Check(string body, bool accepted)
    {
        var run = Analyze(Startup("services.AddHostedService<Worker>();") + $$"""

            public sealed class Worker : BackgroundService
            {
                public int Value;
                private Task? _own;
                protected override Task ExecuteAsync(CancellationToken token) => LoopAsync();
                private async Task LoopAsync()
                {
                    for (var pass = 0; pass < 3; pass++) { {{body}} }
                }
                private void Write() { Value = 1; }
                private bool Again() => Environment.TickCount % 2 == 0;
            }
            """);
        var accesses = run.Of("Value");
        Assert.Equal(2, accesses.Count);
        var analysis = run.Execution.Analysis;
        var work = accesses.Select(access => analysis.Execution(access.ExecutionId)).ToArray();
        Assert.All(work, execution => Assert.Equal("Task.Run", execution.Origin?.Api));
        Assert.Single(work.Select(execution => execution.TreeRootId).Distinct());
        Assert.Equal(2, work.Select(execution => execution.ParentId).Distinct().Count());
        var heap = run.Execution.Heap.Heap;
        var spawn = Assert.Single(heap.Spawns);
        Assert.Single(spawn.Handles);
        var continued = Assert.Single(accesses, access => analysis.Execution(analysis.Execution(access.ExecutionId).ParentId!).Origin?.Api == "async-call");
        var instance = heap.Instances[spawn.CallerInstance];
        var join = Assert.Single(instance.Summary.Joins, join => join.Handles.Any(handle =>
            handle.Values.SelectMany(value => heap.Resolve(instance.Id, value)).Intersect(spawn.Handles).Any()));
        var proofs = new[]
        {
            analysis.OrderProof(accesses[0], accesses[0]),
            analysis.OrderProof(accesses[1], accesses[1]),
            analysis.OrderProof(accesses[0], accesses[1])
        };
        var frames = proofs.SelectMany(proof => proof.Frames).Where(frame => frame.Execution == continued.ExecutionId).ToArray();
        Assert.NotEmpty(frames);
        if (accepted)
            Assert.All(frames, frame => Assert.Contains(frame.Joins, item => item.Instance == instance.Id && item.Operation == join.OperationId));
        else
        {
            Assert.All(frames, frame => Assert.DoesNotContain(frame.Joins, item => item.Instance == instance.Id && item.Operation == join.OperationId));
            Assert.Contains(frames.SelectMany(frame => frame.Rejected), item =>
                item.Instance == instance.Id && item.Operation == join.OperationId && item.Reason == "source");
        }
    }
}
