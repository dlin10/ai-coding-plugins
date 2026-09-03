using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The stream shapes the claude CLI emits that this session does not construct itself. Run
/// 20260902-224201-7bf03b lost two builder rounds to one of them: an event whose <c>message</c> is
/// a string rather than an object, read as an object, throwing
/// <c>InvalidOperationException</c> out through <c>RunAsync</c> and ending a builder that had
/// already applied most of its edits.
/// </summary>
public sealed class ClaudeStreamShapeTests
{
    [Fact]
    public void A_string_message_is_skipped_rather_than_read_as_an_object()
    {
        using var log = new ScopedLog();
        var session = NewSession();
        using var line = JsonDocument.Parse("""{ "type": "user", "message": "rate limit reached" }""");

        Assert.Null(session.Observe(line.RootElement));
    }

    /// <summary>
    /// Which events carry the other shape is still unmeasured, so the payload has to reach the log:
    /// this is what makes the next occurrence name itself.
    /// </summary>
    [Fact]
    public void A_skipped_message_reaches_the_log_with_its_payload()
    {
        using var log = new ScopedLog();
        var session = NewSession();
        using var line = JsonDocument.Parse("""{ "type": "user", "message": "rate limit reached" }""");

        session.Observe(line.RootElement);

        var entry = log.Single("vendor.skipped-message");
        Assert.Equal("user", entry.GetProperty("fields").GetProperty("type").GetString());
        Assert.Contains("rate limit reached",
                        entry.GetProperty("fields").GetProperty("payload").GetString()!,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// The same unguarded read one frame deeper: a content array whose elements are not blocks.
    /// The blocks that are well formed still have to come through.
    /// </summary>
    [Fact]
    public async Task A_content_element_that_is_not_a_block_costs_that_element_and_no_more()
    {
        using var log = new ScopedLog();
        var session = NewSession();

        using (var line = JsonDocument.Parse(
            """
            {
              "type": "assistant",
              "message": { "content": [
                "not a block",
                { "type": "tool_use", "id": "call_1", "name": "Bash", "input": { "command": "dotnet test" } }
              ] }
            }
            """))
        {
            session.Observe(line.RootElement);
        }

        await session.DisposeAsync();
        var events = new List<VendorEvent>();
        await foreach (var raised in session.Events) events.Add(raised);

        var use = Assert.Single(events, raised => raised.Kind is VendorEventKind.ToolUse);
        Assert.Equal("Bash", use.Text);
        Assert.Contains(("command", "dotnet test"), use.Fields!);
    }

    [Fact]
    public void A_root_that_is_not_an_object_is_still_skipped()
    {
        using var log = new ScopedLog();
        var session = NewSession();
        using var line = JsonDocument.Parse("\"a bare string\"");

        Assert.Null(session.Observe(line.RootElement));
    }

    private static ClaudeCliSession NewSession() =>
        new(new RoleSpec(VendorRole.Builder, "prompt"), new Selection("model", null), null);

    /// <summary>
    /// A log of this flow's own, read back. <see cref="RunLog.Use"/> keeps it off the process-wide
    /// fallback, so no parallel test can interleave entries into what these assert on.
    /// </summary>
    private sealed class ScopedLog : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

        private readonly IDisposable _scope;
        private readonly string _path;

        public ScopedLog()
        {
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "forge.log");
            _scope = RunLog.Use(new RunLog(_path));
        }

        public JsonElement Single(string name)
        {
            var matches = File.ReadAllLines(_path)
                              .Select(line => JsonDocument.Parse(line).RootElement)
                              .Where(entry => entry.GetProperty("event").GetString() == name)
                              .ToList();

            return Assert.Single(matches);
        }

        public void Dispose()
        {
            _scope.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }
}
