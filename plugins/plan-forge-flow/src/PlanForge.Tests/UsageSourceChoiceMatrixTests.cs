using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Infrastructure;
using PlanForge.Orchestration;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The choice of the Usage source (R5, R6, R9) walked as a matrix: the Host session, the profile of
/// its kind, the collector on the endpoint's port, and — for a Codex thread — when it becomes known.
/// Every cell has a fresh choice owner, its own Run, port and environment; the profile is a string the
/// test writes and the collector an in-process one, a stand-in that answers otherwise, or the real
/// starter over a stand-in job object. No cell reads a real profile or environment variable.
/// </summary>
public sealed class UsageSourceChoiceMatrixTests : IDisposable
{
    private const string CHOSEN = "orchestrator.source.chosen";
    private const string TELEMETRY = "telemetry";
    private const string TRANSCRIPT = "transcript";
    private const string RUN_ID = "20261014-090000-matrix";

    /// <summary>
    /// How much faster the choice's own timers run in a cell that waits out the choice's ten seconds, so
    /// the wait is five. Every other cell runs on the system clock: its deadlines are not what it tests,
    /// and shortened they were what a loaded CI runner overran — a profile read that had not started
    /// when two seconds were up made an <c>ExportOff</c> cell count no profile read.
    /// </summary>
    private const int HURRY = 2;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "planforge-choice-matrix", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private enum HostKind { ClaudeCode, CodexThread, None }

    private enum Profile { Absent, Loopback4, Localhost, Loopback6, RemoteHost, OtherPath, OtherProtocol, ExportOff }

    /// <summary>
    /// Axis C. <c>StartableInside</c>: the job object refuses breakaway and leaves its processes on close;
    /// <c>StartableInsideKillsOnClose</c>: it refuses breakaway and ends its processes on close;
    /// <c>Unstartable</c>: it refuses breakaway and no process can be created inside it either.
    /// </summary>
    private enum Collector { Answering, OtherProtocol, StartableOutside, StartableInside, StartableInsideKillsOnClose, Unstartable, Foreign }

    private enum Known { AtFirstCall, AtLaterCall }

    /// <summary>One cell of the matrix.</summary>
    /// <param name="Host">Axis A, the Host session.</param>
    /// <param name="Profile">Axis B, the profile of that kind.</param>
    /// <param name="Collector">Axis C, what is on the endpoint's port.</param>
    /// <param name="Known">Axis D, for a Codex thread only: when it becomes known.</param>
    private sealed record Cell(HostKind Host, Profile Profile, Collector Collector, Known? Known);

    // ---- The matrix ----

    [Fact]
    public async Task Every_cell_chooses_what_R5_and_R6_expect()
    {
        await WalkAsync(Cells(), DecideAsync);
    }

    [Fact]
    public void Every_axis_value_occurs()
    {
        var cells = Cells().ToList();

        Assert.Equal(Enum.GetValues<HostKind>(), cells.Select(cell => cell.Host).Distinct().Order());
        Assert.Equal(Enum.GetValues<Profile>(), cells.Select(cell => cell.Profile).Distinct().Order());
        Assert.Equal(Enum.GetValues<Collector>(), cells.Select(cell => cell.Collector).Distinct().Order());
        Assert.Equal(Enum.GetValues<Known>(),
                     cells.Where(cell => cell.Host == HostKind.CodexThread).Select(cell => cell.Known!.Value).Distinct().Order());
        Assert.All(cells.Where(cell => cell.Host != HostKind.CodexThread), cell => Assert.Null(cell.Known));
        Assert.Equal(56 + 112 + 56, cells.Count);
    }

    [Fact]
    public async Task A_cell_with_a_Host_session_keeps_its_first_choice_after_the_profile_the_collector_and_the_session_change()
    {
        await WalkAsync(Cells().Where(cell => cell.Host != HostKind.None), async (cell, world, problems) =>
        {
            await DecideAsync(cell, world, problems);
            var first = Expected(cell.Host, cell.Profile, cell.Collector);

            // A world in which a fresh choice would go the other way.
            world.Profile = first == TELEMETRY ? Profile.Absent : Profile.Loopback4;
            await world.ReleasePortAsync();
            if (first != TELEMETRY) world.Arrange(Collector.Answering);
            if (cell.Host == HostKind.ClaudeCode)
            {
                world.ClaudeSession = Guid.NewGuid().ToString();
                await world.CallAsync(world.Phases, HostSessions.CLAUDE_CODE, null);
            }
            else
            {
                await world.CallAsync(world.Phases, HostSessions.CODEX, "thread-" + Guid.NewGuid().ToString("n"));
            }

            Expect(problems, first, world.Choice.Name, "the second call's source");
            Expect(problems, 1, world.Chosen().Count, "choice entries after the second call");
            Expect(problems, 1, world.ProfileReads, "profile reads after the second call");

            // The changed world never waits out a choice: no profile, or a collector that answers.
            var fresh = world.NewOwner(TimeProvider.System);
            await world.CallAsync(fresh.Phases, cell.Host == HostKind.ClaudeCode ? HostSessions.CLAUDE_CODE : HostSessions.CODEX,
                                  "thread-" + Guid.NewGuid().ToString("n"));
            Expect(problems, first == TELEMETRY ? TRANSCRIPT : TELEMETRY, fresh.Choice.Name, "a fresh owner's source in the changed world");
        });
    }

    [Fact]
    public async Task A_cell_without_a_Host_session_makes_its_first_choice_at_a_later_call_carrying_a_threadId()
    {
        await WalkAsync(Cells().Where(cell => cell.Host == HostKind.None), async (cell, world, problems) =>
        {
            await DecideAsync(cell, world, problems);

            await world.CallAsync(world.Phases, HostSessions.CODEX, "thread-" + Guid.NewGuid().ToString("n"));

            var expected = Expected(HostKind.CodexThread, cell.Profile, cell.Collector);
            Expect(problems, expected, world.Choice.Name, "the source once a thread is known");
            CheckEntry(problems, cell with { Host = HostKind.CodexThread }, expected, world);
        });
    }

    // ---- The expectation ----

    /// <summary>R5 and R6: no source without a Host session; telemetry when the export is configured and a collector answers or is started; the transcript otherwise.</summary>
    /// <param name="host">The Host session.</param>
    /// <param name="profile">The profile of its kind.</param>
    /// <param name="collector">What is on the endpoint's port.</param>
    private static string? Expected(HostKind host, Profile profile, Collector collector)
    {
        if (host == HostKind.None) return null;
        var configured = profile is Profile.Loopback4 or Profile.Localhost or Profile.Loopback6;
        var answers = collector is Collector.Answering or Collector.StartableOutside or Collector.StartableInside
                                   or Collector.StartableInsideKillsOnClose;
        return configured && answers ? TELEMETRY : TRANSCRIPT;
    }

    /// <summary>
    /// Whether the cell's choice waits out its deadline: an export configured toward a port whose occupant
    /// never answers as a collector, which the look polls until the choice's ten seconds are up.
    /// </summary>
    /// <param name="cell">The cell.</param>
    private static bool WaitsOutTheChoice(Cell cell) =>
        cell.Profile is Profile.Loopback4 or Profile.Localhost or Profile.Loopback6
        && cell.Collector is Collector.OtherProtocol or Collector.Foreign;

    private static IEnumerable<Cell> Cells() =>
        from host in Enum.GetValues<HostKind>()
        from profile in Enum.GetValues<Profile>()
        from collector in Enum.GetValues<Collector>()
        from known in host == HostKind.CodexThread ? new Known?[] { Known.AtFirstCall, Known.AtLaterCall } : [null]
        select new Cell(host, profile, collector, known);

    /// <summary>Makes the calls that lead to the cell's first choice, and checks it and its one entry.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="world">The cell's world.</param>
    /// <param name="problems">Where a difference from the expectation is written.</param>
    private static async Task DecideAsync(Cell cell, World world, List<string> problems)
    {
        var expected = Expected(cell.Host, cell.Profile, cell.Collector);
        switch (cell.Host)
        {
            case HostKind.ClaudeCode:
                await world.CallAsync(world.Phases, HostSessions.CLAUDE_CODE, null);
                break;
            case HostKind.CodexThread:
                if (cell.Known == Known.AtLaterCall)
                {
                    await world.CallAsync(world.Phases, HostSessions.CODEX, null);
                    Expect(problems, null, world.Choice.Name, "the source before the thread is known");
                    Expect(problems, 0, world.Chosen().Count, "choice entries before the thread is known");
                    Expect(problems, 0, world.ProfileReads, "profile reads before the thread is known");
                }

                await world.CallAsync(world.Phases, HostSessions.CODEX, "thread-" + Guid.NewGuid().ToString("n"));
                break;
            default:
                // A host that is not Claude Code and carries no thread, though the variable is set.
                await world.CallAsync(world.Phases, "other-mcp-client", null);
                break;
        }

        Expect(problems, expected, world.Choice.Name, "the source");
        if (expected is null)
        {
            Expect(problems, 0, world.Chosen().Count, "choice entries");
            Expect(problems, 0, world.ProfileReads, "profile reads");
        }
        else
        {
            CheckEntry(problems, cell, expected, world);
        }
    }

    /// <summary>The one choice entry, its source and Host session kind, and where a started collector runs.</summary>
    /// <param name="problems">Where a difference is written.</param>
    /// <param name="cell">The cell, with the Host session the choice was made for.</param>
    /// <param name="expected">The expected source.</param>
    /// <param name="world">The cell's world.</param>
    private static void CheckEntry(List<string> problems, Cell cell, string? expected, World world)
    {
        Expect(problems, 1, world.ProfileReads, "profile reads");
        var chosen = world.Chosen();
        Expect(problems, 1, chosen.Count, "choice entries");
        if (chosen.Count != 1) return;

        var fields = chosen[0];
        Expect(problems, expected, Field(fields, "usageSource"), "the entry's source");
        Expect(problems, cell.Host == HostKind.ClaudeCode ? HostSessions.CLAUDE_CODE : HostSessions.CODEX,
               Field(fields, "hostSession"), "the entry's Host session");
        if (expected != TELEMETRY) return;

        Expect(problems, cell.Collector == Collector.Answering ? "found" : "started", Field(fields, "collector"), "the entry's collector");
        if (cell.Collector == Collector.Answering) return;

        var inside = cell.Collector is Collector.StartableInside or Collector.StartableInsideKillsOnClose;
        Expect(problems, inside ? "true" : "false", Field(fields, "insideJob"), "the entry's job object");
        Expect(problems, inside ? cell.Collector == Collector.StartableInsideKillsOnClose ? "true" : "false" : null,
               Field(fields, "jobKillsOnClose"), "the entry's job object close");
    }

    private static string? Field(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Expect<T>(List<string> problems, T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) problems.Add($"{what}: expected {expected?.ToString() ?? "none"}, got {actual?.ToString() ?? "none"}");
    }

    /// <summary>
    /// Runs <paramref name="check"/> on every cell in a world of its own, eight at a time, and fails with
    /// every difference found, each in full: a collection assertion would print only the first fifty
    /// characters of the first, which names a cell and not what it got.
    /// </summary>
    /// <param name="cells">The cells.</param>
    /// <param name="check">The cell's calls and checks.</param>
    private async Task WalkAsync(IEnumerable<Cell> cells, Func<Cell, World, List<string>, Task> check)
    {
        var problems = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(cells, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (cell, _) =>
        {
            var found = new List<string>();
            await using (var world = new World(_root, cell))
            {
                try
                {
                    await check(cell, world, found);
                }
                catch (Exception error)
                {
                    found.Add(error.ToString());
                }
            }

            foreach (var problem in found) problems.Add($"{cell}: {problem}");
        });
        Assert.True(problems.IsEmpty, $"{problems.Count} differences:{Environment.NewLine}"
                                      + string.Join(Environment.NewLine, problems.Order(StringComparer.Ordinal)));
    }

    // ---- Profiles ----

    private static string? ClaudeSettings(Profile profile, int port)
    {
        if (profile == Profile.Absent) return null;

        var env = new JsonObject
        {
            ["OTEL_LOGS_EXPORTER"] = "otlp",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = profile == Profile.OtherProtocol ? "http/protobuf" : "http/json",
            ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = Endpoint(profile, port),
        };
        if (profile != Profile.ExportOff) env["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1";
        return new JsonObject { ["env"] = env }.ToJsonString();
    }

    private static string? CodexConfig(Profile profile, int port) => profile switch
    {
        Profile.Absent => null,
        Profile.ExportOff => "[otel]\nexporter = \"none\"\n",
        _ => $"[otel]\nexporter = {{ otlp-http = {{ endpoint = \"{Endpoint(profile, port)}\", "
             + $"protocol = \"{(profile == Profile.OtherProtocol ? "binary" : "json")}\" }} }}\n",
    };

    private static string Endpoint(Profile profile, int port) => profile switch
    {
        Profile.Localhost => $"http://localhost:{port}/v1/logs",
        Profile.Loopback6 => $"http://[::1]:{port}/v1/logs",
        Profile.RemoteHost => $"http://192.0.2.10:{port}/v1/logs",
        Profile.OtherPath => $"http://127.0.0.1:{port}/v1/traces",
        _ => $"http://127.0.0.1:{port}/v1/logs",
    };

    // ---- A cell's world ----

    /// <summary>
    /// One cell's Run, port, environment, profile and collector, and its choice owner. The real starter
    /// runs over a stand-in job object: a creation with breakaway is refused or not, one without it
    /// succeeds or not, the limits query says whether the job ends its processes on close (reported
    /// only), and a created process is an in-process
    /// collector that serves the port or, finding it held, exits at once.
    /// </summary>
    private sealed class World : IAsyncDisposable
    {
        private readonly List<IAsyncDisposable> _held = [];
        private readonly bool _refuseBreakaway;
        private readonly bool _killOnClose;
        private readonly bool _createInside;
        private int _profileReads;

        public World(string root, Cell cell)
        {
            Root = Path.Combine(root, Guid.NewGuid().ToString("n"));
            Port = TelemetryCollectorTests.FreePort();
            Profile = cell.Profile;
            _refuseBreakaway = cell.Collector is Collector.StartableInside or Collector.StartableInsideKillsOnClose or Collector.Unstartable;
            _killOnClose = cell.Collector is Collector.StartableInsideKillsOnClose or Collector.Unstartable;
            _createInside = cell.Collector != Collector.Unstartable;
            RunDirectory.Create(Root, RUN_ID);
            Arrange(cell.Collector);
            (Choice, Phases) = NewOwner(WaitsOutTheChoice(cell) ? new Hurried(HURRY) : TimeProvider.System);
        }

        public string Root { get; }

        public int Port { get; }

        public Profile Profile { get; set; }

        public string ClaudeSession { get; set; } = Guid.NewGuid().ToString();

        public int ProfileReads => Volatile.Read(ref _profileReads);

        public UsageSourceChoice Choice { get; }

        public OrchestratorPhases Phases { get; }

        /// <summary>A fresh choice owner over this world, and the phase owner that tells it of calls.</summary>
        /// <param name="time">The clock the choice's own deadlines run on.</param>
        public (UsageSourceChoice Choice, OrchestratorPhases Phases) NewOwner(TimeProvider time)
        {
            var choice = new UsageSourceChoice(time, Variable, ReadProfile, Start);
            return (choice, new OrchestratorPhases(TimeProvider.System, choice, Variable));
        }

        /// <summary>A call naming the cell's Run, whose tool does nothing.</summary>
        /// <param name="phases">The phase owner the call passes through.</param>
        /// <param name="client">The MCP client's name.</param>
        /// <param name="threadId">The <c>threadId</c> the call carries, if any.</param>
        public Task CallAsync(OrchestratorPhases phases, string client, string? threadId) =>
            OrchestratorCall.InvokeAsync(phases, Root, RUN_ID, client, threadId, () => ValueTask.FromResult(0)).AsTask();

        /// <summary>The choice entries in the Run's log, their fields.</summary>
        public IReadOnlyList<JsonElement> Chosen()
        {
            var path = RunDirectory.Open(Root, RUN_ID).DiagnosticLogPath;
            if (!File.Exists(path)) return [];
            return [.. AtomicFile.Read(path)
                                 .Split('\n')
                                 .Select(line => line.TrimEnd('\r'))
                                 .Where(line => line.Length > 0)
                                 .Select(line => JsonDocument.Parse(line).RootElement)
                                 .Where(entry => entry.GetProperty("event").GetString() == CHOSEN)
                                 .Select(entry => entry.GetProperty("fields"))];
        }

        /// <summary>Puts on the port what axis C says is already there.</summary>
        /// <param name="collector">The axis value.</param>
        public void Arrange(Collector collector)
        {
            switch (collector)
            {
                case Collector.Answering:
                    var answering = new TelemetryCollector(Port);
                    Assert.True(answering.TryStart());
                    Hold(new Serving(answering));
                    break;
                case Collector.OtherProtocol:
                    Hold(new StandIn(Port, "application/json",
                                     $$"""{"protocol":{{TelemetryCollector.PROTOCOL + 1}},"processId":4,"startedAt":"2026-10-14T09:00:00+00:00"}"""));
                    break;
                case Collector.Foreign:
                    Hold(new StandIn(Port, "text/html", "<html><body>not a collector</body></html>"));
                    break;
            }
        }

        /// <summary>Stops whatever serves the port.</summary>
        public async Task ReleasePortAsync()
        {
            IAsyncDisposable[] held;
            lock (_held)
            {
                held = [.. _held];
                _held.Clear();
            }

            foreach (var item in held) await item.DisposeAsync();
        }

        public ValueTask DisposeAsync() => new(ReleasePortAsync());

        private void Hold(IAsyncDisposable item)
        {
            lock (_held) _held.Add(item);
        }

        private string? Variable(string name) => name switch
        {
            "CLAUDE_CODE_SESSION_ID" => ClaudeSession,
            "CLAUDE_CONFIG_DIR" => Path.Combine(Root, "claude"),
            "CODEX_HOME" => Path.Combine(Root, "codex"),
            _ => null,
        };

        private string? ReadProfile(string path)
        {
            Interlocked.Increment(ref _profileReads);
            if (!path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetFileName(path) switch
            {
                "settings.json" => ClaudeSettings(Profile, Port),
                "config.toml" => CodexConfig(Profile, Port),
                _ => null,
            };
        }

        private CollectorStart Start(int port) =>
            CollectorStarter.Start(port, new CollectorCommand(TelemetryCollectorTests.PlanForgeProgram, []), null, Create, () => _killOnClose);

        private bool Create(string fileName, string commandLine, int flags, out int processId, out int error)
        {
            var breakaway = (flags & CollectorStarter.CREATE_BREAKAWAY_FROM_JOB) != 0;
            if (_refuseBreakaway && breakaway || !breakaway && !_createInside)
            {
                processId = 0;
                error = CollectorStarter.ERROR_ACCESS_DENIED;
                return false;
            }

            var collector = new TelemetryCollector(Port);
            if (collector.TryStart()) Hold(new Serving(collector));
            else collector.Dispose();

            processId = Environment.ProcessId;
            error = 0;
            return true;
        }
    }

    /// <summary>An in-process collector, served until disposed.</summary>
    private sealed class Serving : IAsyncDisposable
    {
        private readonly TelemetryCollector _collector;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;

        public Serving(TelemetryCollector collector)
        {
            _collector = collector;
            _run = collector.RunAsync(_stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _run;
            _collector.Dispose();
            _stop.Dispose();
        }
    }

    /// <summary>Something on the port's three loopback prefixes that answers every request with one fixed body.</summary>
    private sealed class StandIn : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serving;

        public StandIn(int port, string contentType, string body)
        {
            foreach (var host in new[] { "127.0.0.1", "localhost", "[::1]" }) _listener.Prefixes.Add($"http://{host}:{port}/");
            _listener.Start();
            _serving = ServeAsync(contentType, Encoding.UTF8.GetBytes(body));
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Close();
            await _serving;
        }

        private async Task ServeAsync(string contentType, byte[] body)
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                try
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = contentType;
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException or IOException)
                {
                }
            }
        }
    }

    /// <summary>The system clock whose timers fire <paramref name="factor"/> times sooner.</summary>
    /// <param name="factor">How many times sooner.</param>
    private sealed class Hurried(int factor) : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, Shorten(dueTime), Shorten(period));

        private TimeSpan Shorten(TimeSpan span) => span <= TimeSpan.Zero ? span : span / factor;
    }
}
