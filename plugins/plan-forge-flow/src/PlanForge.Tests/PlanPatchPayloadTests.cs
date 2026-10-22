using System.Text;
using System.Text.Json;
using PlanForge.Acts;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;
using PlanForge.Mcp;
using PlanForge.Run;
using Xunit;
using Xunit.Abstractions;

namespace PlanForge.Tests;

public sealed class PlanPatchPayloadTests(ITestOutputHelper output) : IDisposable
{
    private const string Header = "# Plan: incremental order export\n\n" +
                                  "Builder: codex / test-model / high\n\n" +
                                  "## Requirements\n\n" +
                                  "Preserve order identifiers, retry failed batches, and export UTF-8 labels: café, שלום, 🚚.\n\n" +
                                  "```json\n{\"batchSize\": 100, \"encoding\": \"UTF-8\"}\n```\n\n" +
                                  "## Approach\n\n";
    private const string Gates = "## Gates\n\n" +
                                 "G1. Export tests pass and the retry fixture preserves every order identifier.\n";
    private const string ChangedMarker = "The revised snapshot body has its own private marker sentence.";
    private const string InsertedMarker = "The inserted audit body has its own private marker sentence.";

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_small_revision_of_a_small_plan_is_measured() => await Compare(5, 1100);

    [Fact]
    public async Task A_small_revision_of_a_large_plan_is_measured() => await Compare(30, 2700);

    private async Task Compare(int taskCount, int bodySize)
    {
        var bodies = Enumerable.Range(1, taskCount).Select(number => Body(number, bodySize)).ToArray();
        var plan = Draft(bodies);
        if (taskCount == 5) Assert.InRange(plan.Length, 5500, 7000);
        else Assert.True(plan.Length >= 80000);
        Assert.Equal(taskCount, PlanTasks.Parse(plan).Count);

        var revisedBodies = bodies.ToList();
        revisedBodies[1] = bodies[1].Replace(Marker(2), ChangedMarker, StringComparison.Ordinal);
        revisedBodies.Insert(taskCount - 1, "Audit exported orders.\n" + InsertedMarker + "\n" +
                                            "Compare the checkpoint with the published batch before acknowledging it.\n\n");
        var revised = Draft(revisedBodies);
        Assert.Equal(taskCount + 1, PlanTasks.Parse(revised).Count);

        var whole = NewRun("whole");
        var patched = NewRun("patched");
        await Write(whole, plan);
        var written = await Write(patched, plan);
        var outline = Assert.IsType<PlanOutline>(written.Outline);
        var targets = outline.Targets.Where(target => target.Kind == "task").ToArray();
        Assert.Equal(taskCount, targets.Length);
        IReadOnlyList<PlanEdit?> edits =
        [
            new PlanEdit("replace", targets[1].Id, targets[1].Hash, TaskText(2, revisedBodies[1])),
            new PlanEdit("insertBefore", targets[^1].Id, targets[^1].Hash,
                         TaskText(taskCount, revisedBodies[taskCount - 1])),
            new PlanEdit("replace", targets[^1].Id, targets[^1].Hash,
                         TaskText(taskCount + 1, bodies[^1]))
        ];

        await Write(whole, revised);
        var json = await ForgeTools.PatchPlan(SessionRoots.None, _workspace, patched.RunId, outline.PlanHash,
                                              edits, CancellationToken.None);
        Assert.Equal(revised, whole.ReadPlan());
        Assert.Equal(File.ReadAllBytes(whole.PlanPath), File.ReadAllBytes(patched.PlanPath));
        var fullDraftSize = new PlanPatchSize(revised.Length, Encoding.UTF8.GetByteCount(revised));
        var sizes = PlanPatchToolTests.LoggedSizes(patched);
        Assert.True(sizes.PayloadSize.Characters < fullDraftSize.Characters);
        Assert.True(sizes.PayloadSize.Utf8Bytes < fullDraftSize.Utf8Bytes);
        Assert.Equal(PlanPatchSizes.Payload(edits).PayloadSize, sizes.PayloadSize);
        Assert.Equal(PlanPatchSizes.Payload(edits).Before(plan).Built(
            TaskText(2, bodies[1]) + TaskText(taskCount, bodies[^1]),
            string.Concat(edits.Select(edit => edit!.Content)), revised), sizes);

        var entry = Assert.Single(AtomicFile.Read(patched.DiagnosticLogPath)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize(line, DiagnosticJson.Default.LogEntry)!),
            record => record.Event == "plan.patch");
        Assert.Equal("applied", entry.Fields!["outcome"]);

        foreach (var marker in Enumerable.Range(1, taskCount).Select(Marker).Append(ChangedMarker).Append(InsertedMarker))
            Assert.DoesNotContain(marker, json, StringComparison.Ordinal);

        Report("full draft", fullDraftSize);
        foreach (var (name, size) in sizes.Measurements()) Report(name, size);
        Report("answer", new PlanPatchSize(json.Length, Encoding.UTF8.GetByteCount(json)));
    }

    private RunDirectory NewRun(string id)
    {
        var run = RunDirectory.Create(_workspace, id);
        run.WriteState(new RunState(id, _workspace, "Incremental order export", DateTimeOffset.Now, 0, 5));
        return run;
    }

    private async Task<PlanWriteResult> Write(RunDirectory run, string draft) =>
        JsonSerializer.Deserialize(await ForgeTools.WritePlan(SessionRoots.None, _workspace, run.RunId, draft,
                                                              CancellationToken.None),
                                   ForgeToolJson.Default.PlanWriteResult)!;

    private static string Draft(IEnumerable<string> bodies) =>
        Header + string.Concat(bodies.Select((body, index) => TaskText(index + 1, body))) + Gates;

    private static string TaskText(int number, string body) => $"{number}. {body}";

    private static string Marker(int number) => $"Private body marker for original export task {number}: preserve this sentence.";

    private static string Body(int number, int minimumSize)
    {
        var body = new StringBuilder($"Implement export stage {number}.\n{Marker(number)}\n");
        body.Append("Keep customer labels intact: café, שלום, 🚚.\n");
        body.Append("```csharp\nawait exporter.FlushAsync(cancellationToken);\n```\n");
        while (body.Length < minimumSize)
            body.Append("Read the checkpoint, snapshot the pending orders, publish a bounded batch, and verify retry coverage.\n");
        return body.Append('\n').ToString();
    }

    private void Report(string name, PlanPatchSize size) =>
        output.WriteLine($"{name}: {size.Characters} characters, {size.Utf8Bytes} UTF-8 bytes");
}
