using System.Diagnostics;
using System.IO;
using System.Text;
using CodexSessionPulse.App.Hooks;
using CodexSessionPulse.App.Ipc;
using CodexSessionPulse.App.Overlay;
using CodexSessionPulse.Core;
using CodexSessionPulse.Infrastructure.Settings;
using Xunit;

namespace CodexSessionPulse.App.Tests;

/// <summary>The hook payload reader, the pipe message, the channel names, in-process pipe delivery and the exit rule.</summary>
public sealed class HookPathTests
{
    private const string ThreadId = "019a5c3e-1b2f-7a10-9c4d-0123456789ab";
    private const string TranscriptPath = @"C:\Users\jdoe\.codex\sessions\2026\11\07\rollout-2026-11-07T10-00-00-" + ThreadId + ".jsonl";

    /// <summary>The four fields come out of a real-shaped payload; cwd and prompt are not among them.</summary>
    [Fact]
    public void Reader_TakesOnlyTheFourFields()
    {
        HookPayload payload = Read(UserPromptSubmit("secret prompt text"));

        Assert.Equal(new HookPayload("UserPromptSubmit", "s-1", TranscriptPath, null), payload);
    }

    /// <summary>A byte-at-a-time stream gives the same result as one read: the reader resumes across chunks.</summary>
    [Fact]
    public void Reader_ResumesAcrossTinyChunks()
    {
        byte[] json = Encoding.UTF8.GetBytes(SessionStart("resume"));

        HookPayload payload = HookPayloadReader.Read(new TrickleStream(json));

        Assert.Equal(new HookPayload("SessionStart", "s-1", TranscriptPath, "resume"), payload);
    }

    /// <summary>A prompt far larger than the first buffer is passed over; fields after it are still read.</summary>
    [Fact]
    public void Reader_LargePromptBeforeFields_StillFindsThem()
    {
        string json = "{\"prompt\":\"" + new string('p', 600 * 1024) + "\",\"hook_event_name\":\"UserPromptSubmit\",\"transcript_path\":"
                      + Quote(TranscriptPath) + "}";

        HookPayload payload = Read(json);

        Assert.Equal("UserPromptSubmit", payload.HookEventName);
        Assert.Equal(TranscriptPath, payload.TranscriptPath);
    }

    /// <summary>Fields inside nested objects are not the payload's own and are ignored.</summary>
    [Fact]
    public void Reader_NestedFields_AreIgnored()
    {
        HookPayload payload = Read("{\"tool_input\":{\"hook_event_name\":\"SessionEnd\",\"transcript_path\":\"x\"},\"hook_event_name\":\"SessionStart\"}");

        Assert.Equal(new HookPayload("SessionStart", null, null, null), payload);
    }

    /// <summary>Truncated JSON keeps the fields read before the cut; garbage and non-objects give nothing.</summary>
    /// <param name="json">The stdin text.</param>
    /// <param name="eventName">The expected <c>hook_event_name</c>.</param>
    /// <param name="sessionId">The expected <c>session_id</c>.</param>
    [Theory]
    [InlineData("{\"hook_event_name\":\"SessionStart\",\"session_id\":\"s-1\",\"transcript_pa", "SessionStart", "s-1")]
    [InlineData("{\"hook_event_name\":\"Sess", null, null)]
    [InlineData("not json at all", null, null)]
    [InlineData("", null, null)]
    [InlineData("[{\"hook_event_name\":\"SessionStart\"}]", null, null)]
    [InlineData("\"SessionStart\"", null, null)]
    public void Reader_BadInput_IsTolerated(string json, string? eventName, string? sessionId)
    {
        HookPayload payload = Read(json);

        Assert.Equal(eventName, payload.HookEventName);
        Assert.Equal(sessionId, payload.SessionId);
    }

    /// <summary>Input beyond the 1 MiB cap is not read: the reader stops at the cap and still returns.</summary>
    [Fact]
    public void Reader_StopsAtTheCap()
    {
        CountingStream stream = new(Encoding.UTF8.GetBytes("{\"hook_event_name\":\"UserPromptSubmit\",\"prompt\":\"" + new string('x', 3 * 1024 * 1024) + "\"}"));

        HookPayload payload = HookPayloadReader.Read(stream);

        Assert.Equal("UserPromptSubmit", payload.HookEventName);
        Assert.True(stream.Position <= HookPayloadReader.MaxPayloadBytes, $"read {stream.Position} bytes");
    }

    /// <summary>The message carries the event, the thread id from the file name, the source and the time, and nothing else.</summary>
    [Fact]
    public void Message_RoundTripsWithoutPayloadText()
    {
        DateTimeOffset at = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
        HookMessage message = HookMessage.From(Read(UserPromptSubmit("secret prompt text")), at);
        byte[] bytes = message.ToBytes();
        string text = Encoding.UTF8.GetString(bytes);

        Assert.True(HookMessage.TryParse(bytes, out HookMessage parsed));
        Assert.Equal(new HookMessage("UserPromptSubmit", ThreadId, null, at), parsed);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("jdoe", text, StringComparison.Ordinal);
        Assert.DoesNotContain("s-1", text, StringComparison.Ordinal);
    }

    /// <summary>A null transcript_path (an ephemeral suggestion thread) gives no thread id.</summary>
    [Fact]
    public void Message_NullTranscriptPath_HasNoThread()
    {
        HookMessage message = HookMessage.From(Read("{\"hook_event_name\":\"SessionStart\",\"source\":\"fork\",\"transcript_path\":null}"), DateTimeOffset.UtcNow);

        Assert.Null(message.ThreadId);
        Assert.Equal("fork", message.Source);
    }

    /// <summary>Another protocol version, a missing time, a thread id that is no GUID and oversized data are rejected or cleaned.</summary>
    [Fact]
    public void Message_Parse_RejectsForeignData()
    {
        Assert.False(HookMessage.TryParse("{\"v\":2,\"event\":\"SessionStart\",\"at\":\"2026-11-07T10:00:00Z\"}"u8, out _));
        Assert.False(HookMessage.TryParse("{\"v\":1,\"event\":\"SessionStart\"}"u8, out _));
        Assert.False(HookMessage.TryParse("{\"v\":1,\"event\":\"SessionSt"u8, out _));
        Assert.False(HookMessage.TryParse(new byte[HookMessage.MaxBytes + 1], out _));
        Assert.True(HookMessage.TryParse("{\"v\":1,\"event\":\"SessionEnd\",\"thread\":\"..\\\\x\",\"at\":\"2026-11-07T10:00:00Z\"}"u8, out HookMessage cleaned));
        Assert.Null(cleaned.ThreadId);
    }

    /// <summary>Event and source names from stdin and from the pipe are mapped to the fixed sets before anything logs or counts them.</summary>
    [Fact]
    public void Message_EventAndSource_AreMappedToFixedSets()
    {
        HookMessage known = HookMessage.From(new HookPayload("SessionStart", null, TranscriptPath, "resume"), DateTimeOffset.UtcNow);
        HookMessage foreign = HookMessage.From(new HookPayload("Stop\r\nINFO forged log line", null, TranscriptPath, new string('s', 5000)),
                                               DateTimeOffset.UtcNow);
        bool parsed = HookMessage.TryParse("{\"v\":1,\"event\":\"sessionstart\",\"source\":\"x\",\"at\":\"2026-11-07T10:00:00Z\"}"u8, out HookMessage piped);

        Assert.Equal(("SessionStart", "resume"), (known.Event, known.Source));
        Assert.Equal((HookMessage.Other, HookMessage.Other), (foreign.Event, foreign.Source));
        Assert.True(parsed);
        Assert.Equal((HookMessage.Other, HookMessage.Other), (piped.Event, piped.Source));
    }

    /// <summary>The names carry the user SID and the protocol major; a moved home gets its own channel.</summary>
    [Fact]
    public void Channel_NamesCarrySidVersionAndHome()
    {
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        PulseChannel real = PulseChannel.For(new PulsePaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-session-pulse")));
        PulseChannel moved = PulseChannel.For(new PulsePaths(@"C:\temp\csp-home"));

        Assert.Equal($@"Local\CodexSessionPulse.{sid}.{PulseChannel.ProtocolMajor}", real.MutexName);
        Assert.Equal($"CodexSessionPulse.{sid}.{PulseChannel.ProtocolMajor}", real.PipeName);
        Assert.StartsWith(real.PipeName + ".", moved.PipeName, StringComparison.Ordinal);
        Assert.NotEqual(real.MutexName, moved.MutexName);
    }

    /// <summary>Parallel hooks all reach one server, and each message arrives whole.</summary>
    [Fact]
    public async Task Pipe_ParallelSends_AllArrive()
    {
        PulseChannel channel = TestChannel();
        using HookPipeServer server = HookPipeServer.TryStart(channel.PipeName, _ => { })!;
        Assert.NotNull(server);
        Assert.Null(HookPipeServer.TryStart(channel.PipeName, _ => { }));

        // Real hooks are separate processes. Each client here gets a thread of its own rather than a pool thread: 16 blocked
        // pool threads starve the server's own continuations on a two-core CI runner, and sends then fail for a reason no
        // hook meets. The longer connect wait covers the clients queuing for pipe instances.
        DateTimeOffset at = DateTimeOffset.UtcNow;
        bool[] sent = new bool[16];
        Thread[] clients = Enumerable.Range(0, sent.Length).Select(index => new Thread(() =>
            sent[index] = HookPipeClient.TrySend(channel.PipeName, new HookMessage("UserPromptSubmit", ThreadId, null, at.AddSeconds(index)).ToBytes(),
                                                 connectTimeoutMs: 10_000))).ToArray();
        Array.ForEach(clients, client => client.Start());
        Array.ForEach(clients, client => client.Join());

        Assert.All(sent, Assert.True);
        List<HookMessage> received = [];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        while (received.Count < 16)
        {
            received.Add(await server.Messages.ReadAsync(timeout.Token));
        }

        Assert.Equal(16, received.Select(message => message.AtUtc).Distinct().Count());
        Assert.Equal(0, server.Rejected);
    }

    /// <summary>A client that sends garbage and leaves before the answer is one refused connection, not two.</summary>
    [Fact]
    public async Task Pipe_RefusedClientThatLeaves_CountsOnce()
    {
        const int Clients = 20;
        PulseChannel channel = TestChannel();
        using HookPipeServer server = HookPipeServer.TryStart(channel.PipeName, _ => { })!;

        for (int i = 0; i < Clients; i++)
        {
            using System.IO.Pipes.NamedPipeClientStream client = new(".", channel.PipeName, System.IO.Pipes.PipeDirection.InOut);
            await client.ConnectAsync(2000);
            await client.WriteAsync("garbage\n"u8.ToArray());
        }

        Stopwatch waited = Stopwatch.StartNew();

        while (server.Rejected < Clients && waited.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(20);
        }

        // Any double count would show within the server's 1 s read timeout.
        await Task.Delay(1500);
        Assert.Equal(Clients, server.Rejected);
    }

    /// <summary>Without an overlay a send gives up within its connect limit.</summary>
    [Fact]
    public void Pipe_NoServer_GivesUpWithinTheLimit()
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        bool sent = HookPipeClient.TrySend(TestChannel().PipeName, [1], HookPipeClient.ConnectTimeoutMs);

        Assert.False(sent);
        Assert.InRange(elapsed.ElapsedMilliseconds, 0, HookPipeClient.ConnectTimeoutMs + 500);
    }

    /// <summary>With no overlay, --no-start returns at once and creates nothing: no mutex, no pipe, no log.</summary>
    [Fact]
    public void Deliver_NoStartWithoutOverlay_CreatesNothing()
    {
        string home = Path.Combine(Path.GetTempPath(), "csp-nostart-" + Guid.NewGuid().ToString("N"));
        PulsePaths paths = new(home);
        PulseChannel channel = PulseChannel.For(paths);
        Stopwatch elapsed = Stopwatch.StartNew();

        HookOutcome outcome = HookRunner.Deliver(new HookMessage("SessionEnd", ThreadId, null, DateTimeOffset.UtcNow), channel, noStart: true, paths);

        Assert.Equal(HookOutcome.NotRunning, outcome);
        Assert.InRange(elapsed.ElapsedMilliseconds, 0, 200);
        Assert.False(channel.IsOverlayRunning());
        Assert.False(Directory.Exists(home));
    }

    /// <summary>An overlay that holds the mutex but has no pipe yet (the cold-start race) costs the hook only its connect limit.</summary>
    [Fact]
    public void Deliver_MutexWithoutPipe_GivesUpWithinTheLimit()
    {
        string home = Path.Combine(Path.GetTempPath(), "csp-race-" + Guid.NewGuid().ToString("N"));
        PulsePaths paths = new(home);
        PulseChannel channel = PulseChannel.For(paths);
        using Mutex starting = new(initiallyOwned: true, channel.MutexName);
        Stopwatch elapsed = Stopwatch.StartNew();

        try
        {
            HookOutcome outcome = HookRunner.Deliver(new HookMessage("UserPromptSubmit", ThreadId, null, DateTimeOffset.UtcNow), channel, noStart: false, paths);

            Assert.Equal(HookOutcome.NotDelivered, outcome);
            Assert.InRange(elapsed.ElapsedMilliseconds, HookPipeClient.ConnectTimeoutMs - 50, HookPipeClient.ConnectTimeoutMs + 500);
        }
        finally
        {
            starting.ReleaseMutex();

            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>The exit waits for a seen window to be gone for the grace period and for a recheck that finds none.</summary>
    [Fact]
    public void Exit_AfterGraceAndEmptyRecheck()
    {
        DateTimeOffset t0 = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
        LastWindowExit exit = new(TimeSpan.FromSeconds(3));

        exit.OnAnchor(AnchorState.NotFound, t0);
        Assert.False(exit.Pending);

        exit.OnAnchor(AnchorState.Attached, t0.AddSeconds(1));
        exit.OnAnchor(AnchorState.Minimized, t0.AddSeconds(2));
        Assert.False(exit.Pending);

        exit.OnAnchor(AnchorState.NotFound, t0.AddSeconds(3));
        Assert.True(exit.Pending);
        Assert.False(exit.ShouldRecheck(t0.AddSeconds(5)));
        Assert.True(exit.ShouldRecheck(t0.AddSeconds(6)));
        Assert.True(exit.OnRecheck(0, t0.AddSeconds(6)));
    }

    /// <summary>A window back within the grace period cancels the exit; a cloaked window found by the recheck postpones it.</summary>
    [Fact]
    public void Exit_CancelledByReturnOrCloakedWindow()
    {
        DateTimeOffset t0 = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
        LastWindowExit exit = new(TimeSpan.FromSeconds(3));
        exit.OnAnchor(AnchorState.Background, t0);

        exit.OnAnchor(AnchorState.NotFound, t0.AddSeconds(1));
        exit.OnAnchor(AnchorState.Attached, t0.AddSeconds(2));
        Assert.False(exit.Pending);
        Assert.False(exit.ShouldRecheck(t0.AddSeconds(10)));

        exit.OnAnchor(AnchorState.NotFound, t0.AddSeconds(10));
        Assert.False(exit.OnRecheck(1, t0.AddSeconds(13)));
        Assert.False(exit.ShouldRecheck(t0.AddSeconds(15)));
        Assert.True(exit.ShouldRecheck(t0.AddSeconds(16)));
    }

    private static PulseChannel TestChannel() => PulseChannel.For(new PulsePaths(Path.Combine(Path.GetTempPath(), "csp-pipe-" + Guid.NewGuid().ToString("N"))));

    private static HookPayload Read(string json) => HookPayloadReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string UserPromptSubmit(string prompt) =>
        "{\"session_id\":\"s-1\",\"turn_id\":\"t-1\",\"transcript_path\":" + Quote(TranscriptPath) + ",\"cwd\":\"C:\\\\repo\","
        + "\"hook_event_name\":\"UserPromptSubmit\",\"model\":\"gpt-5\",\"permission_mode\":\"default\",\"prompt\":" + Quote(prompt) + "}\n";

    private static string SessionStart(string source) =>
        "{\"session_id\":\"s-1\",\"transcript_path\":" + Quote(TranscriptPath) + ",\"cwd\":\"C:\\\\repo\",\"hook_event_name\":\"SessionStart\","
        + "\"model\":\"gpt-5\",\"permission_mode\":\"default\",\"source\":" + Quote(source) + "}";

    /// <summary>Returns one byte per read.</summary>
    /// <param name="data">The bytes.</param>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
    }

    /// <summary>A stream whose position shows how much was read.</summary>
    /// <param name="data">The bytes.</param>
    private sealed class CountingStream(byte[] data) : MemoryStream(data);
}
