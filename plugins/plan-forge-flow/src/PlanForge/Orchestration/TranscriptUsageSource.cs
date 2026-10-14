using System.Globalization;
using System.Text.Json;
using PlanForge.Vendors.Codex;

namespace PlanForge.Orchestration;

/// <summary>
/// The transcript Usage source (R8): the Host session's own files, read while the host keeps writing
/// them. Claude Code: <c>&lt;config dir&gt;\projects\*\&lt;session&gt;.jsonl</c> and its
/// <c>&lt;session&gt;\subagents\*.jsonl</c>, each response counted once in the phase that holds its
/// earliest line. Codex: <c>&lt;home&gt;\sessions\**\rollout-*-&lt;thread&gt;.jsonl</c>, the growth of
/// its <c>token_count</c> totals. Counters go through <see cref="UsageCounters"/>.
/// </summary>
/// <remarks>
/// An unparseable last line is one the host is still writing, and is ignored; any other unparseable
/// line, a missing or unreadable file answers <c>source_failed</c>, and a read past its deadline
/// <c>source_timeout</c>.
/// </remarks>
internal sealed class TranscriptUsageSource : IUsageSource
{
    private const string SOURCE_TIMEOUT = "source_timeout";
    private const string SYNTHETIC_MODEL = "<synthetic>";

    private static readonly TimeSpan SETTLE_DELAY = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan READ_DEADLINE = TimeSpan.FromSeconds(10);

    private readonly string _claudeConfigDirectory;
    private readonly string _codexHome;
    private readonly Func<string, CancellationToken, Task<string>> _readFile;

    /// <summary>Creates a source over the two hosts' roots.</summary>
    /// <param name="claudeConfigDirectory">Claude Code's config dir.</param>
    /// <param name="codexHome">Codex's home.</param>
    /// <param name="readFile">Reads a whole file; shared reading of the real file when null.</param>
    /// <param name="readDeadline">The longest a read may take; 10 seconds when null.</param>
    public TranscriptUsageSource(string claudeConfigDirectory, string codexHome,
                                 Func<string, CancellationToken, Task<string>>? readFile = null,
                                 TimeSpan? readDeadline = null)
    {
        _claudeConfigDirectory = claudeConfigDirectory;
        _codexHome = codexHome;
        _readFile = readFile ?? ReadSharedAsync;
        ReadDeadline = readDeadline ?? READ_DEADLINE;
    }

    /// <summary>The source over the roots the server process's environment names.</summary>
    /// <param name="environment">Reads the server process's environment variables.</param>
    public static TranscriptUsageSource ForProcess(Func<string, string?> environment) =>
        new(ClaudeConfigDirectory.Resolve(environment), CodexLaunch.Home(environment));

    public string? Name => "transcript";

    /// <summary>A host may write the lines of the response that made the declaring call after that call arrives.</summary>
    public TimeSpan SettleDelay => SETTLE_DELAY;

    public TimeSpan ReadDeadline { get; }

    public async Task<UsageReading> ReadAsync(UsageWindow window, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ReadDeadline);
        var token = deadline.Token;
        try
        {
            // Enumerating, parsing and grouping are synchronous: run off the caller, so the deadline bounds
            // them too; a reading that arrives after it is discarded.
            return await Task.Run(() => window.Session.Kind == HostSessions.CLAUDE_CODE ? ReadClaudeAsync(window, token) : ReadCodexAsync(window, token),
                                  CancellationToken.None)
                             .WaitAsync(token)
                             .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UsageReading(null, Missing: OrchestratorPhases.SOURCE_FAILED, FailureKind: SOURCE_TIMEOUT);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Failed();
        }
    }

    /// <summary>A transcript is never registered anywhere.</summary>
    /// <param name="session">The Host session at the opening.</param>
    /// <param name="ct">The caller's deadline.</param>
    public Task<DateTimeOffset?> PhaseOpenedAsync(HostSession? session, CancellationToken ct) => Task.FromResult<DateTimeOffset?>(null);

    /// <summary>Claude Code's responses, grouped by message id, each placed by the earliest timestamp of its lines.</summary>
    /// <param name="window">The session and the phase's window.</param>
    /// <param name="ct">The read's deadline.</param>
    private async Task<UsageReading> ReadClaudeAsync(UsageWindow window, CancellationToken ct)
    {
        var session = window.Session.Id;
        var projects = Path.Combine(_claudeConfigDirectory, "projects");
        if (!IsFileName(session) || !Directory.Exists(projects)) return Failed();

        var transcripts = Directory.EnumerateDirectories(projects)
                                   .Select(project => Path.Combine(project, session + ".jsonl"))
                                   .Where(File.Exists)
                                   .Order(StringComparer.Ordinal)
                                   .ToList();
        if (transcripts.Count == 0) return Failed();

        var files = new List<string>();
        foreach (var transcript in transcripts)
        {
            files.Add(transcript);
            var subagents = Path.Combine(Path.GetDirectoryName(transcript)!, session, "subagents");
            if (Directory.Exists(subagents))
                files.AddRange(Directory.EnumerateFiles(subagents, "*.jsonl").Order(StringComparer.Ordinal));
        }

        // The lines of one response repeat its usage: the response is kept once, with its earliest line's time.
        var responses = new Dictionary<string, (DateTimeOffset Earliest, JsonElement Usage)>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var line in await LinesAsync(file, ct).ConfigureAwait(false))
            {
                if (Text(line,"type") != "assistant" || !line.TryGetProperty("message", out var message)
                                                        || message.ValueKind != JsonValueKind.Object
                                                        || Text(message,"model") == SYNTHETIC_MODEL
                                                        || !message.TryGetProperty("usage", out var usage)
                                                        || usage.ValueKind != JsonValueKind.Object)
                    continue;

                var at = Timestamp(line);
                var id = Text(message,"id") ?? $"{file}#{responses.Count}";
                responses[id] = responses.TryGetValue(id, out var seen) && seen.Earliest < at ? seen with { Usage = usage } : (at, usage);
            }
        }

        var counters = new UsageCounters();
        foreach (var (earliest, usage) in responses.Values)
            counters.AddClaudeResponse(usage, earliest >= window.From && earliest < window.To);
        return counters.Reading();
    }

    /// <summary>Codex's rollout of the thread: its <c>token_count</c> events in file order.</summary>
    /// <param name="window">The session and the phase's window.</param>
    /// <param name="ct">The read's deadline.</param>
    private async Task<UsageReading> ReadCodexAsync(UsageWindow window, CancellationToken ct)
    {
        var thread = window.Session.Id;
        var sessions = Path.Combine(_codexHome, "sessions");
        if (!IsFileName(thread) || !Directory.Exists(sessions)) return Failed();

        var rollouts = Directory.EnumerateFiles(sessions, $"rollout-*-{thread}.jsonl", SearchOption.AllDirectories)
                                .Order(StringComparer.Ordinal)
                                .ToList();
        if (rollouts.Count == 0) return Failed();

        var events = new List<(DateTimeOffset At, JsonElement? Totals)>();
        foreach (var rollout in rollouts)
        {
            foreach (var line in await LinesAsync(rollout, ct).ConfigureAwait(false))
            {
                if (!line.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                                                                    || Text(payload,"type") != "token_count")
                    continue;

                JsonElement? totals = payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                                                                                    && info.TryGetProperty("total_token_usage", out var total)
                    ? total
                    : null;
                events.Add((Timestamp(line), totals));
            }
        }

        var counters = new UsageCounters();
        counters.AddCodexRollout(events, window.From, window.To);
        return counters.Reading();
    }

    /// <summary>The file's lines as JSON objects; an unparseable last line is still being written and is ignored.</summary>
    /// <param name="path">The file.</param>
    /// <param name="ct">The read's deadline.</param>
    private async Task<List<JsonElement>> LinesAsync(string path, CancellationToken ct)
    {
        var text = await _readFile(path, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0).ToArray();
        var parsed = new List<JsonElement>(lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            try
            {
                using var document = JsonDocument.Parse(lines[index]);
                if (document.RootElement.ValueKind == JsonValueKind.Object) parsed.Add(document.RootElement.Clone());
            }
            catch (JsonException) when (index == lines.Length - 1)
            {
            }
        }

        return parsed;
    }

    /// <summary>Reads a file the host may be writing, sharing it every way it allows.</summary>
    /// <param name="path">The file.</param>
    /// <param name="ct">The read's deadline.</param>
    private static async Task<string> ReadSharedAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                                bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A line's <c>timestamp</c>; a line that has none cannot be placed, and fails the read.</summary>
    /// <param name="line">The line.</param>
    private static DateTimeOffset Timestamp(JsonElement line) =>
        Text(line,"timestamp") is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : throw new FormatException("a usage line carries no readable timestamp");

    /// <summary>A string property, or null when it is absent or not a string.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property's name.</param>
    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Whether an id can stand in a file name as is, so it never reaches outside the folder or acts as a wildcard.</summary>
    /// <param name="id">The session or thread id.</param>
    private static bool IsFileName(string id) =>
        id.Length > 0 && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && id.IndexOfAny(['*', '?']) < 0 && id is not ("." or "..");

    private static UsageReading Failed() =>
        new(null, Missing: OrchestratorPhases.SOURCE_FAILED, FailureKind: OrchestratorPhases.SOURCE_FAILED);
}
