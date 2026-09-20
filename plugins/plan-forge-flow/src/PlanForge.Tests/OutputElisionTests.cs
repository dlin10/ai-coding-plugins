using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// A vendor turn that outgrows the output bound is trimmed, not destroyed. Run
/// 20260919-160244-77fd30 lost two <c>forge.review.fix</c> turns to the old fatal cap: the builder
/// had written 92 files and the turn died on the way to reporting them, so the edits were on disk
/// and the summary, the verification, the file list and the gate run were not — the work done and
/// nothing to show that it had been. The output that did it was ordinary for the act: `dotnet test
/// -v n` prints the whole csc command line once per run and a fix pass is a long row of mutation
/// checks. So 8 MB is a size a working turn reaches, and it cannot be the size at which one dies.
/// </summary>
public sealed class OutputElisionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public OutputElisionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The reported failure, at the size it was reported at and against the bounds the server
    /// really runs with: more than 8 MB of stdout, and the call comes back instead of throwing.
    /// </summary>
    [Fact]
    public async Task A_process_past_the_bound_finishes_instead_of_killing_the_call()
    {
        var lines = await StreamingProcess.CollectAsync(Replaying(await FloodAsync(9 * 1024 * 1024)),
                                                        TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Contains(lines, line => line.StartsWith("[... ", StringComparison.Ordinal));
        Assert.Equal(Result, lines[^1]);
    }

    /// <summary>
    /// What survives, and in what order: the head as it streamed, one marker saying how much went,
    /// and the end of the output whole. The tail is the half that matters — a vendor writes its
    /// structured result last, which is exactly what the killed turns never delivered.
    /// </summary>
    [Fact]
    public async Task The_head_and_the_tail_survive_with_a_marker_between_them()
    {
        var bounds = new OutputBounds(ElideAfterBytes: 4096, TailChars: 1024, HardCapBytes: 1024 * 1024);

        var lines = await StreamingProcess.CollectAsync(Replaying(await FloodAsync(64 * 1024)),
                                                        TimeSpan.FromMinutes(1), CancellationToken.None,
                                                        bounds: bounds);

        var marker = lines.Single(line => line.StartsWith("[... ", StringComparison.Ordinal));
        Assert.Equal(Head, lines[0]);
        Assert.Equal(Result, lines[^1]);
        Assert.EndsWith(" bytes elided ...]", marker, StringComparison.Ordinal);

        // Nothing is counted twice: what the marker claims to have dropped, plus everything
        // delivered, is everything the process wrote.
        var delivered = lines.Where(line => line != marker).Sum(line => (long)line.Length);
        Assert.Equal(Written(64 * 1024), delivered + Dropped(marker));
    }

    /// <summary>
    /// The point of keeping the tail rather than the head. A claude turn's answer is the last
    /// <c>StructuredOutput</c> call in the stream, and a session reading an elided stream still
    /// finds it — which is what makes the act finish with a result instead of a file list.
    /// </summary>
    [Fact]
    public async Task A_claude_session_still_reads_its_result_out_of_an_elided_stream()
    {
        var bounds = new OutputBounds(ElideAfterBytes: 4096, TailChars: 1024, HardCapBytes: 1024 * 1024);
        var lines = await StreamingProcess.CollectAsync(Replaying(await FloodAsync(64 * 1024)),
                                                        TimeSpan.FromMinutes(1), CancellationToken.None,
                                                        bounds: bounds);

        var session = new ClaudeCliSession(new RoleSpec(VendorRole.Builder, "prompt"), new Selection("model", null), null);
        JsonElement? structured = null;
        foreach (var line in lines)
        {
            if (!TryParse(line, out var document)) continue;
            using (document)
            {
                structured = session.Observe(document.RootElement) ?? structured;
            }
        }

        Assert.NotNull(structured);
        Assert.Equal("done", structured.Value.GetProperty("status").GetString());
    }

    /// <summary>The run log says a stream was trimmed; a short one leaves no such line.</summary>
    [Fact]
    public async Task An_elided_stream_says_so_in_the_run_log()
    {
        var path = Path.Combine(_directory, "forge.log");
        var bounds = new OutputBounds(ElideAfterBytes: 4096, TailChars: 1024, HardCapBytes: 1024 * 1024);

        using (RunLog.Use(new RunLog(path)))
        {
            await StreamingProcess.CollectAsync(Replaying(await FloodAsync(64 * 1024)),
                                                TimeSpan.FromMinutes(1), CancellationToken.None, bounds: bounds);
        }

        var elided = Entries(path).Single(entry => entry.GetProperty("event").GetString() == "process.output.eliding");
        Assert.Equal("4096", elided.GetProperty("fields").GetProperty("after").GetString());
    }

    /// <summary>A stream inside its bound is delivered as it always was, marker and all absent.</summary>
    [Fact]
    public async Task A_stream_inside_the_bound_is_untouched()
    {
        var bounds = new OutputBounds(ElideAfterBytes: 4096, TailChars: 1024, HardCapBytes: 1024 * 1024);

        var lines = await StreamingProcess.CollectAsync(Replaying(await FloodAsync(1024)),
                                                        TimeSpan.FromMinutes(1), CancellationToken.None,
                                                        bounds: bounds);

        Assert.DoesNotContain(lines, line => line.StartsWith("[... ", StringComparison.Ordinal));
        Assert.Equal(Head, lines[0]);
        Assert.Equal(Result, lines[^1]);
    }

    private const string Head = """{"type":"system","subtype":"init","session_id":"chat-1"}""";

    private const string Result =
        """{"type":"assistant","session_id":"chat-1","message":{"content":[{"type":"tool_use","name":"StructuredOutput","input":{"status":"done"}}]}}""";

    /// <summary>
    /// A stream shaped like a vendor's: an init line, filler that stands for the tool traffic, and
    /// the structured result last. Returns the path to replay.
    /// </summary>
    /// <param name="filler">Roughly how much of the middle to write, in characters.</param>
    private async Task<string> FloodAsync(int filler)
    {
        var path = Path.Combine(_directory, $"flood-{filler}.jsonl");
        var line = $$$"""{"type":"user","message":{"content":[{"type":"text","text":"{{{new string('x', 1020)}}}"}]}}""";
        string[] lines = [Head, .. Enumerable.Repeat(line, Math.Max(1, filler / line.Length)), Result];
        await File.WriteAllLinesAsync(path, lines);
        return path;
    }

    /// <summary>What <see cref="FloodAsync"/> wrote, counted the way the runner counts it.</summary>
    private long Written(int filler) =>
        File.ReadAllLines(Path.Combine(_directory, $"flood-{filler}.jsonl")).Sum(line => (long)line.Length);

    private static long Dropped(string marker) => long.Parse(marker.Split(' ')[1]);

    /// <summary>
    /// Replays a file to stdout verbatim. Piping a file through type/cat sidesteps the shell
    /// quoting that inline JSON would need.
    /// </summary>
    private ProcessSpec Replaying(string path)
    {
        return OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd.exe", ["/c", "type", path], _directory, string.Empty)
            : new ProcessSpec("/bin/sh", ["-c", $"cat '{path}'"], _directory, string.Empty);
    }

    private static IReadOnlyList<JsonElement> Entries(string path) =>
        AtomicFile.Read(path)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

    private static bool TryParse(string line, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }
}
