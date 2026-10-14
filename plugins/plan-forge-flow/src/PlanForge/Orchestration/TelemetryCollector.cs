using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlanForge.Orchestration;

/// <summary>
/// The Telemetry collector (R9): <c>planforge.exe --telemetry-collector --port &lt;n&gt;</c>, the binary's
/// second entry point, which serves no MCP. It takes OTLP/JSON logs from loopback callers and keeps,
/// for the Host sessions servers registered and only from their registration on, the event time and
/// usage fields of their API-request events; it answers servers' questions about them.
/// </summary>
/// <remarks>
/// <para>
/// An event's time is decided once, as it is admitted (R7): <c>event.timestamp</c>, else a non-zero
/// <c>timeUnixNano</c>, else <c>observedTimeUnixNano</c> — less <c>duration_ms</c> for a Claude Code
/// <c>claude_code.api_request</c> whose <c>duration_ms</c> is a valid count. Claude Code stamps that
/// event when the response stream ends but runs a tool call as soon as its block is ready, so the
/// request is kept at its start and counts in the phase it began in, as the transcript places a
/// response at its earliest line. The rule that drops events from before a session's registration
/// applies to this time; <c>duration_ms</c> itself is not kept.
/// </para>
/// <para>
/// Codex stamps <c>codex.sse_event</c> (<c>response.completed</c>) when the response stream ends and
/// carries no duration, but runs a tool call as soon as its output item is done. A completed response
/// therefore takes the time of the latest request start of its conversation at or before its own R7
/// time — a <c>codex.websocket_request</c> at its R7 time, or a <c>codex.api_request</c> carrying the
/// conversation at its R7 time less a valid <c>duration_ms</c> — and keeps its R7 time when there is
/// none. Request starts are kept for registered sessions as their time alone: no usage, no other
/// attribute, never counted as events. The pairing is made when usage is asked for, not on admission,
/// so a start that arrives in a later export batch than its completion still pairs; the registration
/// cut-off applies to the paired time, so a start is kept even from shortly before the registration,
/// for as long as its session's export brings it in after the session registered.
/// </para>
/// <para>
/// http.sys matches the Host header rather than binding loopback alone, so a request that reaches a
/// loopback prefix from another address is refused here. The collector lives while any registered
/// server process is alive — a process recognised by its id and start time, so a reused id is another
/// process — and ends a grace after the last one, or a grace after starting if none registers.
/// </para>
/// </remarks>
internal sealed class TelemetryCollector : IDisposable
{
    /// <summary>The protocol version a server checks before it uses a collector.</summary>
    internal const int PROTOCOL = 1;

    private const string COMMAND = "--telemetry-collector";
    private const string CLAUDE_EVENT = "claude_code.api_request";
    private const string CODEX_EVENT = "codex.sse_event";
    private const string CODEX_KIND = "response.completed";
    private const string CODEX_WEBSOCKET_REQUEST = "codex.websocket_request";
    private const string CODEX_API_REQUEST = "codex.api_request";
    private const string EMPTY_ANSWER = "{}";
    private const string COST_FIELD = "cost_usd";

    private static readonly TimeSpan DEFAULT_GRACE = TimeSpan.FromMinutes(5);

    /// <summary>The only attributes kept: the usage fields R7 reads, per vendor.</summary>
    private static readonly string[] CLAUDE_FIELDS = ["input_tokens", "cache_read_tokens", "cache_creation_tokens", "output_tokens", "cost_usd"];
    private static readonly string[] CODEX_FIELDS = ["input_token_count", "cached_token_count", "cache_write_token_count",
                                                     "output_token_count", "reasoning_token_count"];

    /// <summary>The OTLP <c>AnyValue</c> members a count or a cost may arrive in.</summary>
    private static readonly string[] VALUE_KINDS = ["stringValue", "intValue", "doubleValue"];
    private static readonly JsonElement INVALID_VALUE = JsonDocument.Parse("null").RootElement;

    private readonly HttpListener _listener = new();
    private readonly TimeSpan _grace;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, KeptSession> _sessions = new(StringComparer.Ordinal);
    private readonly List<RegisteredProcess> _processes = [];
    private DateTimeOffset _lastAlive;

    /// <summary>Creates a collector for one port; nothing listens until <see cref="TryStart"/>.</summary>
    /// <param name="port">The loopback port to serve.</param>
    /// <param name="grace">How long it outlives the last registered process; five minutes when null.</param>
    public TelemetryCollector(int port, TimeSpan? grace = null)
    {
        _grace = grace ?? DEFAULT_GRACE;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://[::1]:{port}/");
        StartedAt = _lastAlive = DateTimeOffset.UtcNow;
    }

    /// <summary>When this collector started.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Whether <paramref name="args"/> start the collector rather than the MCP server.</summary>
    /// <param name="args">The process's arguments.</param>
    public static bool IsCommand(string[] args) => args is [COMMAND, ..];

    /// <summary>
    /// Runs the collector mode: <c>--telemetry-collector --port &lt;n&gt;</c>, and the test-only
    /// <c>--grace-seconds &lt;s&gt;</c>. A collector that cannot bind its port exits at once.
    /// </summary>
    /// <param name="args">The process's arguments.</param>
    /// <returns>0 when its lifetime ended, 1 when the port could not be bound, 2 for bad arguments.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        int? port = null;
        TimeSpan? grace = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) return 2;
            var value = args[++i];
            switch (args[i - 1])
            {
                case "--port" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is > 0 and <= 65535:
                    port = number;
                    break;
                case "--grace-seconds" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds):
                    grace = TimeSpan.FromSeconds(seconds);
                    break;
                default:
                    return 2;
            }
        }

        if (port is not { } chosen) return 2;
        using var collector = new TelemetryCollector(chosen, grace);
        if (!collector.TryStart()) return 1;
        await collector.RunAsync(CancellationToken.None);
        return 0;
    }

    /// <summary>The start time a server registers with, as the collector compares it.</summary>
    /// <param name="process">The process.</param>
    public static DateTimeOffset ProcessStartTime(Process process) => new(process.StartTime.ToUniversalTime(), TimeSpan.Zero);

    /// <summary>
    /// The one admission rule (R9): an event is kept only when it comes from a loopback caller, for a
    /// registered Host session, and is an API-request event — Claude Code's <c>claude_code.api_request</c>
    /// of any <c>query_source</c>, or Codex's <c>codex.sse_event</c> of kind <c>response.completed</c>.
    /// </summary>
    /// <param name="caller">The request's remote address.</param>
    /// <param name="registered">Whether the event's session is registered.</param>
    /// <param name="eventName">The event's name.</param>
    /// <param name="eventKind">Codex's <c>event.kind</c>, if any.</param>
    internal static bool Admits(IPAddress caller, bool registered, string? eventName, string? eventKind) =>
        IsLoopback(caller) && registered
                           && (eventName == CLAUDE_EVENT || eventName == CODEX_EVENT && eventKind == CODEX_KIND);

    /// <summary>Binds the three loopback prefixes; false when the port cannot be bound.</summary>
    public bool TryStart()
    {
        try
        {
            _listener.Start();
            return true;
        }
        catch (HttpListenerException)
        {
            return false;
        }
    }

    /// <summary>Serves until the lifetime ends or <paramref name="ct"/> is cancelled, then stops listening.</summary>
    /// <param name="ct">Stops the collector early.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        var serving = ServeAsync();
        try
        {
            await WatchLifetimeAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }

        _listener.Close();
        await serving;
    }

    public void Dispose() => _listener.Close();

    /// <summary>What is kept, for tests to check it holds usage fields only.</summary>
    internal IReadOnlyList<(string Session, DateTimeOffset At, IReadOnlyDictionary<string, JsonElement> Usage)> Kept()
    {
        lock (_gate)
        {
            return [.. _sessions.SelectMany(session => session.Value.Events.Select(e => (session.Key, e.At, e.Usage)))];
        }
    }

    /// <summary>A test seam: the Codex request starts kept, by session — a time, and nothing else.</summary>
    internal IReadOnlyList<(string Session, DateTimeOffset At)> KeptStarts()
    {
        lock (_gate)
        {
            return [.. _sessions.SelectMany(session => session.Value.Starts.Select(at => (session.Key, at)))];
        }
    }

    private async Task WatchLifetimeAsync(CancellationToken ct)
    {
        var tick = TimeSpan.FromMilliseconds(Math.Clamp(_grace.TotalMilliseconds / 4, 50, 1000));
        using var timer = new PeriodicTimer(tick);
        while (await timer.WaitForNextTickAsync(ct))
        {
            RegisteredProcess[] processes;
            lock (_gate) processes = [.. _processes];

            var dead = processes.Where(process => !IsAlive(process)).ToHashSet();
            var now = DateTimeOffset.UtcNow;
            lock (_gate)
            {
                _processes.RemoveAll(dead.Contains);
                if (processes.Length > dead.Count) _lastAlive = now;
                if (now - _lastAlive >= _grace) return;
            }
        }
    }

    private static bool IsAlive(RegisteredProcess registered)
    {
        try
        {
            using var process = Process.GetProcessById(registered.ProcessId);
            return !process.HasExited && ProcessStartTime(process) == registered.StartTime;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            return false;
        }
    }

    private async Task ServeAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var (status, body) = await AnswerAsync(context.Request);
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (Exception)
        {
            // A caller that went away, or a listener closing under the answer: nothing to keep.
            try { context.Response.Abort(); } catch (Exception) { }
        }
    }

    private async Task<(int Status, string Body)> AnswerAsync(HttpListenerRequest request)
    {
        var caller = request.RemoteEndPoint.Address;
        if (!IsLoopback(caller)) return (403, EMPTY_ANSWER);

        var path = request.Url?.AbsolutePath ?? "";
        return (request.HttpMethod, path) switch
        {
            ("POST", "/v1/logs") => await TakeLogsAsync(request, caller),
            (_, _) when path.StartsWith("/v1/", StringComparison.Ordinal) => (200, EMPTY_ANSWER),
            ("GET", "/planforge/collector") =>
                (200, JsonSerializer.Serialize(new CollectorIdentity(PROTOCOL, Environment.ProcessId, StartedAt), CollectorJson.Default.CollectorIdentity)),
            ("POST", "/planforge/sessions") => await RegisterAsync(request),
            ("GET", "/planforge/usage") => Usage(request),
            _ => (404, EMPTY_ANSWER),
        };
    }

    private async Task<(int, string)> TakeLogsAsync(HttpListenerRequest request, IPAddress caller)
    {
        List<CandidateEvent>? candidates;
        try
        {
            using var body = new MemoryStream();
            var gzip = request.Headers["Content-Encoding"] is { } encoding
                       && encoding.Trim().Equals("gzip", StringComparison.OrdinalIgnoreCase);
            await using (var input = gzip ? new GZipStream(request.InputStream, CompressionMode.Decompress) : request.InputStream)
            {
                await input.CopyToAsync(body);
            }

            body.Position = 0;
            using var document = await JsonDocument.ParseAsync(body);
            candidates = Candidates(document.RootElement);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException)
        {
            return (400, EMPTY_ANSWER);
        }

        // Parsed whole before anything is kept, so a request of the wrong shape changes nothing.
        if (candidates is null) return (400, EMPTY_ANSWER);
        lock (_gate)
        {
            foreach (var candidate in candidates)
            {
                KeptSession? session = null;
                var registered = candidate.Session is not null && _sessions.TryGetValue(candidate.Session, out session);

                // A Codex request start: its time alone, whenever it is, for pairing at a usage question.
                if (candidate.Name is CODEX_WEBSOCKET_REQUEST or CODEX_API_REQUEST)
                {
                    if (registered && candidate.At is { } start) session!.Starts.Add(start);
                    continue;
                }

                if (!Admits(caller, registered, candidate.Name, candidate.Kind)) continue;

                // Coverage starts at the registration: an event from before it is not kept.
                if (candidate.At is not { } at || at < session!.RegisteredAt) continue;
                session.Events.Add(new KeptEvent(candidate.Name == CODEX_EVENT, at, candidate.Usage));
            }
        }

        return (200, EMPTY_ANSWER);
    }

    private async Task<(int, string)> RegisterAsync(HttpListenerRequest request)
    {
        SessionRegistration? registration;
        try
        {
            registration = await JsonSerializer.DeserializeAsync(request.InputStream, CollectorJson.Default.SessionRegistration);
        }
        catch (JsonException)
        {
            return (400, EMPTY_ANSWER);
        }

        if (registration is not { Session: { Length: > 0 } id, ProcessId: > 0 }) return (400, EMPTY_ANSWER);

        DateTimeOffset registeredAt;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (!_sessions.TryGetValue(id, out var session)) _sessions[id] = session = new KeptSession(now);
            var process = new RegisteredProcess(registration.ProcessId, registration.ProcessStartTime.ToUniversalTime());
            if (!_processes.Contains(process)) _processes.Add(process);
            _lastAlive = now;
            registeredAt = session.RegisteredAt;
        }

        return (200, JsonSerializer.Serialize(new RegistrationAnswer(registeredAt), CollectorJson.Default.RegistrationAnswer));
    }

    private (int, string) Usage(HttpListenerRequest request)
    {
        var query = request.QueryString;
        if (query["session"] is not { Length: > 0 } id || !TryInstant(query["from"], out var from) || !TryInstant(query["to"], out var to))
        {
            return (400, EMPTY_ANSWER);
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session)) return (404, EMPTY_ANSWER);

            var counters = new UsageCounters();
            var inWindow = 0;
            var events = 0;
            foreach (var kept in session.Events)
            {
                // A Codex response began at its request's start; one that began before the registration is not covered.
                var at = kept.Codex ? session.StartOf(kept.At) : kept.At;
                if (at < session.RegisteredAt) continue;

                events++;
                var inPhase = at >= from && at < to;
                if (inPhase) inWindow++;
                JsonElement? Attribute(string name) => kept.Usage.TryGetValue(name, out var value) ? value : null;
                if (kept.Codex) counters.AddCodexExportEvent(Attribute, inPhase);
                else counters.AddClaudeExportEvent(Attribute, inPhase);
            }

            var reading = counters.Reading();
            var answer = new CollectorUsage(reading.Tokens, reading.Incomplete, inWindow, events, session.RegisteredAt);
            return (200, JsonSerializer.Serialize(answer, CollectorJson.Default.CollectorUsage));
        }
    }

    private static bool TryInstant(string? text, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant);

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    /// <summary>
    /// The log records of an OTLP/JSON logs request, each reduced to what admission and usage need;
    /// null when the envelope has the wrong shape.
    /// </summary>
    /// <param name="root">The request body.</param>
    private static List<CandidateEvent>? Candidates(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var candidates = new List<CandidateEvent>();
        if (!TryObjects(root, "resourceLogs", out var resourceLogs)) return null;
        foreach (var resourceLog in resourceLogs)
        {
            if (!TryObjects(resourceLog, "scopeLogs", out var scopeLogs)) return null;
            foreach (var scopeLog in scopeLogs)
            {
                if (!TryObjects(scopeLog, "logRecords", out var records)) return null;
                foreach (var record in records)
                {
                    if (Candidate(record) is not { } candidate) return null;
                    candidates.Add(candidate);
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// A log record reduced to what admission and usage need; null when any part the collector reads
    /// has the wrong shape, whether or not the event is one it keeps.
    /// </summary>
    /// <param name="record">The OTLP log record.</param>
    private static CandidateEvent? Candidate(JsonElement record)
    {
        if (!HasKind(record, "body", JsonValueKind.Object)
            || !HasKind(record, "timeUnixNano", JsonValueKind.String, JsonValueKind.Number)
            || !HasKind(record, "observedTimeUnixNano", JsonValueKind.String, JsonValueKind.Number))
        {
            return null;
        }

        if (!TryObjects(record, "attributes", out var attributes)) return null;
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            if (!attribute.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String) return null;
            if (!attribute.TryGetProperty("value", out var value)) continue;
            if (value.ValueKind != JsonValueKind.Object) return null;
            values[key.GetString()!] = Unwrap(value);
        }

        string? Text(string name) => values.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var bodyName = record.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object
                                                                   && body.TryGetProperty("stringValue", out var text)
                                                                   && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;
        var name = bodyName ?? Text("event.name");
        var start = name is CODEX_WEBSOCKET_REQUEST or CODEX_API_REQUEST;
        var codex = name == CODEX_EVENT || start;

        // A request start keeps no attribute but its time.
        IReadOnlyDictionary<string, JsonElement> usage = start
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : (codex ? CODEX_FIELDS : CLAUDE_FIELDS).Where(values.ContainsKey)
                                                    .ToDictionary(field => field, field => Validated(field, values[field]), StringComparer.Ordinal);
        var at = EventTime(record, Text("event.timestamp"));

        // Stamped as the request ends; a websocket request's duration_ms is its send, not the response's length.
        if (name is CLAUDE_EVENT or CODEX_API_REQUEST && values.TryGetValue("duration_ms", out var duration)) at = RequestStart(at, duration);
        return new CandidateEvent(name, Text("event.kind"), Text(codex ? "conversation.id" : "session.id"), at, usage);
    }

    /// <summary>
    /// A Claude Code request's start: its event time less a valid <c>duration_ms</c>. Claude Code stamps
    /// the event as the response stream ends, but runs a tool call as soon as its block is ready, so the
    /// response that makes a declaring call ends after the boundary; counted at its start, it belongs to
    /// the phase it began in, as the transcript places a response at its earliest line (R8). A Codex
    /// <c>codex.api_request</c>, stamped as its HTTP request ends, starts the same way. A duration that
    /// is not a valid count (R13), or reaches before the earliest time, leaves the event time as it is.
    /// </summary>
    /// <param name="at">The event time by R7's order.</param>
    /// <param name="duration">The unwrapped <c>duration_ms</c> attribute.</param>
    private static DateTimeOffset? RequestStart(DateTimeOffset? at, JsonElement duration)
    {
        if (at is not { } end || !UsageCounters.TryCount(duration, out var milliseconds)) return at;
        return milliseconds <= (end - DateTimeOffset.MinValue).Ticks / TimeSpan.TicksPerMillisecond
            ? end.AddTicks(-milliseconds * TimeSpan.TicksPerMillisecond)
            : at;
    }

    /// <summary>Event time (R7): <c>event.timestamp</c>, else a non-zero <c>timeUnixNano</c>, else <c>observedTimeUnixNano</c>.</summary>
    /// <param name="record">The log record.</param>
    /// <param name="timestamp">Its <c>event.timestamp</c> attribute, if any.</param>
    private static DateTimeOffset? EventTime(JsonElement record, string? timestamp)
    {
        if (timestamp is not null
            && DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            return at;
        }

        if (Nanos(record, "timeUnixNano") is { } time and not 0) return FromNanos(time);
        return Nanos(record, "observedTimeUnixNano") is { } observed and not 0 ? FromNanos(observed) : null;
    }

    private static ulong? Nanos(JsonElement record, string name) =>
        !record.TryGetProperty(name, out var value) ? null
        : value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number
        : value.ValueKind == JsonValueKind.String
          && ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed
        : null;

    private static DateTimeOffset FromNanos(ulong nanos) => DateTimeOffset.UnixEpoch.AddTicks((long)(nanos / 100));

    /// <summary>
    /// An attribute's value: <c>stringValue</c>, <c>intValue</c> or <c>doubleValue</c> as sent; any other
    /// kind — <c>boolValue</c>, <c>arrayValue</c>, <c>kvlistValue</c>, <c>bytesValue</c>, or none — is a
    /// present value that is not valid, handed on as JSON null so that <see cref="UsageCounters"/> marks
    /// its counter invalid (R13) and nothing of its content is kept.
    /// </summary>
    /// <param name="value">The OTLP <c>AnyValue</c> object.</param>
    private static JsonElement Unwrap(JsonElement value)
    {
        foreach (var name in VALUE_KINDS)
        {
            if (value.TryGetProperty(name, out var inner)) return inner;
        }

        return INVALID_VALUE;
    }

    /// <summary>
    /// What is kept of a usage field: the number <see cref="UsageCounters"/> finds valid, or JSON null
    /// marking a present value that is not valid — never the value as sent, so no other content is kept.
    /// </summary>
    /// <param name="field">The field's name; <c>cost_usd</c> is a cost, every other field a count.</param>
    /// <param name="value">The unwrapped value.</param>
    private static JsonElement Validated(string field, JsonElement value)
    {
        string? number = field == COST_FIELD
            ? UsageCounters.TryCost(value, out var cost) ? cost.ToString("R", CultureInfo.InvariantCulture) : null
            : UsageCounters.TryCount(value, out var count) ? count.ToString(CultureInfo.InvariantCulture) : null;
        if (number is null) return INVALID_VALUE;
        using var document = JsonDocument.Parse(number);
        return document.RootElement.Clone();
    }

    /// <summary>Whether an optional property, when present and not null, has one of the given kinds.</summary>
    /// <param name="owner">The object holding it.</param>
    /// <param name="name">The property's name.</param>
    /// <param name="kinds">The kinds it may have.</param>
    private static bool HasKind(JsonElement owner, string name, params JsonValueKind[] kinds) =>
        !owner.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null || kinds.Contains(value.ValueKind);

    /// <summary>An optional array of objects: absent or null reads as empty; anything else is the wrong shape.</summary>
    /// <param name="owner">The object holding it.</param>
    /// <param name="name">The property's name.</param>
    /// <param name="items">The objects.</param>
    private static bool TryObjects(JsonElement owner, string name, out List<JsonElement> items)
    {
        items = [];
        if (!owner.TryGetProperty(name, out var array) || array.ValueKind == JsonValueKind.Null) return true;
        if (array.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return false;
            items.Add(item);
        }

        return true;
    }

    private sealed record CandidateEvent(string? Name, string? Kind, string? Session, DateTimeOffset? At,
                                         IReadOnlyDictionary<string, JsonElement> Usage);

    private sealed record KeptEvent(bool Codex, DateTimeOffset At, IReadOnlyDictionary<string, JsonElement> Usage);

    private sealed class KeptSession(DateTimeOffset registeredAt)
    {
        public DateTimeOffset RegisteredAt { get; } = registeredAt;

        public List<KeptEvent> Events { get; } = [];

        /// <summary>The times of the session's Codex request starts, and nothing else of them.</summary>
        public List<DateTimeOffset> Starts { get; } = [];

        /// <summary>The latest request start at or before a completion's time, or that time when there is none.</summary>
        /// <param name="completed">The completion's R7 time.</param>
        public DateTimeOffset StartOf(DateTimeOffset completed)
        {
            var start = completed;
            var found = false;
            foreach (var candidate in Starts)
            {
                if (candidate > completed || found && candidate <= start) continue;
                start = candidate;
                found = true;
            }

            return start;
        }
    }

    private sealed record RegisteredProcess(int ProcessId, DateTimeOffset StartTime);
}

/// <summary>The collector's answer to <c>GET /planforge/collector</c>.</summary>
/// <param name="Protocol">The protocol version.</param>
/// <param name="ProcessId">The collector's process id.</param>
/// <param name="StartedAt">When the collector started.</param>
internal sealed record CollectorIdentity(int Protocol, int ProcessId, DateTimeOffset StartedAt);

/// <summary>A server's <c>POST /planforge/sessions</c>: a Host session and the server process it is for.</summary>
/// <param name="Session">The Host session's id: a Claude Code session or a Codex thread.</param>
/// <param name="ProcessId">The server's process id.</param>
/// <param name="ProcessStartTime">The server process's start time, as <see cref="TelemetryCollector.ProcessStartTime"/> gives it.</param>
internal sealed record SessionRegistration(string Session, int ProcessId, DateTimeOffset ProcessStartTime);

/// <summary>The answer to a registration: when the session was first registered.</summary>
/// <param name="RegisteredAt">The first registration time, kept across repeated registrations.</param>
internal sealed record RegistrationAnswer(DateTimeOffset RegisteredAt);

/// <summary>The answer to <c>GET /planforge/usage?session=&amp;from=&amp;to=</c>.</summary>
/// <param name="Tokens">The window's totals, normalized by <see cref="UsageCounters"/>; null when no counter is carried.</param>
/// <param name="Incomplete">Whether a value in the window was not valid or a sum overflowed.</param>
/// <param name="WindowEvents">The events within the window.</param>
/// <param name="KeptEvents">Every event kept for the session since its registration.</param>
/// <param name="RegisteredAt">When the collector's coverage of the session began.</param>
internal sealed record CollectorUsage(OrchestratorTokens? Tokens, bool Incomplete, int WindowEvents, int KeptEvents,
                                      DateTimeOffset RegisteredAt);

// Reflection-based serialization is off repo-wide, so the collector's payloads need a source-generated contract.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CollectorIdentity))]
[JsonSerializable(typeof(SessionRegistration))]
[JsonSerializable(typeof(RegistrationAnswer))]
[JsonSerializable(typeof(CollectorUsage))]
internal sealed partial class CollectorJson : JsonSerializerContext;
