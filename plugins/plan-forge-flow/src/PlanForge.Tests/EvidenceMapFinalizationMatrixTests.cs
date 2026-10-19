using System.Text.Json.Nodes;
using PlanForge.Infrastructure;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Caller cancellation outranks store failure, and neither record failure changes the outcome.</summary>
public sealed class EvidenceMapFinalizationMatrixTests
{
    /// <summary>The Cartesian product of cancellation and three independent finalization failures.</summary>
    private static IEnumerable<(bool Cancel, bool StoreFails, bool FlowFails, bool TelemetryFails)> Cells =>
        from cancel in new[] { false, true }
        from store in new[] { false, true }
        from flow in new[] { false, true }
        from telemetry in new[] { false, true }
        select (cancel, store, flow, telemetry);

    /// <summary>All sixteen outcomes preserve accepted progress and attempt each record exactly once.</summary>
    [Fact]
    public async Task Every_cell_gets_its_expected_answer()
    {
        foreach (var cell in Cells)
        {
            using var run = new EvidenceToolRun();
            using var cancellation = new CancellationTokenSource();
            run.SeedMap();
            AtomicFile.Write(run.Run.FlowLogPath, "prior flow\n");
            AtomicFile.Write(run.Run.TelemetryPath, "[]");
            var priorStore = AtomicFile.Read(run.Run.EvidenceMapPath);
            run.Advisor.Handler = (request, _) =>
            {
                // The final answer arrives before cancellation, with no elapsed second to trigger an interim write.
                var answer = FakeAdvisor.Answer(request);
                if (cell.StoreFails) run.Lock(run.Run.EvidenceMapPath);
                if (cell.FlowFails) run.Lock(run.Run.FlowLogPath);
                if (cell.TelemetryFails) run.Lock(run.Run.TelemetryPath);
                if (cell.Cancel) cancellation.Cancel();
                return Task.FromResult(answer);
            };
            string? result = null;
            var error = await Record.ExceptionAsync(async () => result = await run.Call(cancellation.Token));
            run.Unlock();
            if (cell.Cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
            else if (cell.StoreFails) Assert.True(error is IOException or UnauthorizedAccessException, $"{cell}: {error}");
            else
            {
                Assert.Null(error);
                Assert.True(JsonNode.Parse(result!)!["complete"]!.GetValue<bool>());
            }
            run.AssertRecords(cell.FlowFails, cell.TelemetryFails);
            Assert.Single(run.Advisor.Requests);
            if (cell.StoreFails) Assert.Equal(priorStore, AtomicFile.Read(run.Run.EvidenceMapPath));
            else Assert.Equal(2, PlanForge.Evidence.EvidenceStore.Load(run.Run.EvidenceMapPath).Judgments.Count);
            if (cell.FlowFails) Assert.Equal("prior flow\n", AtomicFile.Read(run.Run.FlowLogPath));
            if (cell.TelemetryFails) Assert.Equal("[]", AtomicFile.Read(run.Run.TelemetryPath));
        }
    }

    /// <summary>Every independent branch occurs in the matrix, including all simultaneous failures.</summary>
    [Fact]
    public void Every_axis_value_occurs_in_some_cell()
    {
        var cells = Cells.ToArray();
        Assert.Equal(16, cells.Distinct().Count());
        foreach (var axis in new Func<(bool Cancel, bool StoreFails, bool FlowFails, bool TelemetryFails), bool>[]
                 { cell => cell.Cancel, cell => cell.StoreFails, cell => cell.FlowFails, cell => cell.TelemetryFails })
            Assert.Equal([false, true], cells.Select(axis).Distinct().Order());
    }
}
