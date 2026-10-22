using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanForge.Acts;
using PlanForge.Infrastructure;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Orchestration;

/// <summary>
/// The one owner of Orchestrator phases: <c>forge.begin</c> opens <c>interview</c>, a declaration
/// through <c>forge.log.append</c> closes the open phase and opens the next, and every close appends
/// one <c>orchestrator.usage</c> record. See docs/adr/0032.
/// </summary>
/// <remarks>
/// The open phase lives in the run folder's <c>orchestrator-phase.json</c> rather than in
/// <c>state.json</c>, which a background Job rewrites while a poll runs, so that a later server
/// process can close what this one opened. A Run's lock covers that file's read and write, the local
/// read of the position (<c>state.json</c> and the approved plan) and the in-memory tool time of the
/// closing phase, and nothing else: it is released before any wait, source read or collector request, so a declaration
/// waiting on its source never holds up another call of the Run. Every failure is best effort — it
/// writes one <c>forge.log</c> entry and never changes the call's result.
/// </remarks>
internal sealed class OrchestratorPhases
{
    internal const string NO_HOST_SESSION = "no_host_session";
    internal const string SOURCE_FAILED = "source_failed";
    internal const string FILE_NAME = "orchestrator-phase.json";

    private const string INTERVIEW = "interview";
    internal const string DONE = "done";
    internal const string OPEN = "open";
    private const string NONE = "none";
    internal const string LOG_SOURCE = "telemetry";
    internal const string FAILED = "orchestrator.telemetry.failed";

    private const string EVENT = "orchestrator.usage";

    /// <summary>The valid values of <c>phase</c>, compared case-sensitively.</summary>
    internal static readonly string[] Names =
        [INTERVIEW, "planning", "plan_revision", "approval", "build", "code_review", "wrap_up", DONE];

    private readonly IUsageSource _source;
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the owner of a process's phases, with its call clock and its Host session owner.</summary>
    /// <param name="time">The clock phases and calls are measured with.</param>
    /// <param name="source">Where tokens are read from at a close.</param>
    /// <param name="environment">Reads the server process's environment variables.</param>
    public OrchestratorPhases(TimeProvider time, IUsageSource source, Func<string, string?> environment)
    {
        Clock = new CallClock(time);
        Hosts = new HostSessions(environment);
        _source = source;
    }

    /// <summary>
    /// The process's owner: the one <c>Program.cs</c> creates, and until then one that reads no
    /// environment, for calls that never passed the filter.
    /// </summary>
    public static OrchestratorPhases Process { get; set; } = new(TimeProvider.System, NoUsageSource.Instance, _ => null);

    public CallClock Clock { get; }

    /// <summary>The process's one answer to what the Host session is.</summary>
    public HostSessions Hosts { get; }

    /// <summary>Tells the source a call arrived, for its choice (R6); never throws.</summary>
    /// <param name="call">The call, already observed by <see cref="Hosts"/>.</param>
    internal async Task ArrivedAsync(OrchestratorCall call)
    {
        try
        {
            await _source.CallArrivedAsync(Hosts.Current(), call).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort (R10): the call proceeds whatever the source made of it.
        }
    }

    /// <summary>Tells the source a call ended; never throws.</summary>
    /// <param name="call">The call.</param>
    internal async Task EndedAsync(OrchestratorCall call)
    {
        try
        {
            await _source.CallEndedAsync(call).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort (R10): the call's result stands.
        }
    }

    /// <summary>Refuses a <c>phase</c> that is not one of <see cref="Names"/>.</summary>
    /// <param name="phase">The declared phase.</param>
    public static void Validate(string phase)
    {
        if (!Names.Contains(phase, StringComparer.Ordinal))
            throw new ArgumentRejectedException(
                $"phase must be exactly one of {string.Join(", ", Names)} (case-sensitive); '{phase}' is not");
    }

    /// <summary>Opens <c>interview</c> for a Run <c>forge.begin</c> just created, at the call's arrival.</summary>
    /// <param name="run">The new Run.</param>
    /// <param name="call">The <c>forge.begin</c> call.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    public Task BeginAsync(RunDirectory run, OrchestratorCall call, CancellationToken ct) =>
        DeclareAsync(run, call, INTERVIEW, ct);

    /// <summary>
    /// Applies a valid declaration at the call's arrival: closes the open phase, if any, and opens
    /// <paramref name="phase"/> unless it is <c>done</c>. Never throws.
    /// </summary>
    /// <param name="run">The Run the call serves.</param>
    /// <param name="call">The declaring call.</param>
    /// <param name="phase">The declared phase, already validated.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    public async Task DeclareAsync(RunDirectory run, OrchestratorCall call, string phase, CancellationToken ct)
    {
        call.Serve(run);
        var failures = new List<string>();
        var applied = Apply(run, call, phase, failures);
        foreach (var kind in failures) LogFailure(run, kind, phase);
        if (applied is null) return;

        // A close lost to cancellation has written its one entry; registering would only fail and write a second.
        var cancelled = applied.Closed is { } closed && !await CloseAsync(run, closed, applied.Boundary, applied.Tools, ct).ConfigureAwait(false);
        if (applied.Opened is { } opened && !cancelled) await OpenedAsync(run, opened, ct).ConfigureAwait(false);
    }

    /// <summary>A test seam: runs inside the Run's lock right after a declaration writes the phase file, with the declared phase.</summary>
    internal Action<string>? PhaseFileWritten { get; set; }

    /// <summary>
    /// Applies the declaration under the Run's lock: the position read and the closing phase's tool
    /// time measured as the opening is applied, then the phase file written.
    /// </summary>
    /// <param name="run">The Run.</param>
    /// <param name="call">The declaring call.</param>
    /// <param name="phase">The declared phase.</param>
    /// <param name="failures">Receives the failure kinds.</param>
    private Applied? Apply(RunDirectory run, OrchestratorCall call, string phase, List<string> failures)
    {
        var path = Path.Combine(run.Path, FILE_NAME);
        lock (_locks.GetOrAdd(Path.GetFullPath(path), _ => new object()))
        {
            // Taken when the opening is applied (R2): a round or task that moved while this call waited for the lock counts.
            var position = phase == DONE ? Position.None : ReadPosition(run, phase, failures);
            var (open, failure) = Read(path);
            if (failure == "phase_file_unreadable")
            {
                failures.Add(failure);
                return null;
            }

            if (failure is not null) failures.Add(failure);

            // Applied one at a time: a declaration that arrived before the open phase opened is
            // applied at that opening, so a phase never runs backwards and may last zero seconds.
            var boundary = open?.OpenedAt is { } openedAt && openedAt > call.Arrival ? openedAt : call.Arrival;
            var session = Hosts.Current();

            // Marked before any close of the new phase can read it, so even one that overtakes this call's
            // registration knows the registration covers the phase from its opening (R9).
            var next = phase == DONE
                ? new OrchestratorPhaseFile(NONE)
                : new OrchestratorPhaseFile(OPEN, phase, boundary, Local(boundary), position.Round, position.TaskNumber,
                                            position.TaskCount, session?.Id,
                                            Registering: session is not null && _source.RegistersAtOpening ? true : null);
            try
            {
                AtomicFile.Write(path, JsonSerializer.Serialize(next, OrchestratorPhaseJson.Default.OrchestratorPhaseFile));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failures.Add("phase_file_unwritable");
                return null;
            }

            // Measured here, so no later declaration's Forget can drop a call this close has not counted (R4).
            var tools = TimeSpan.Zero;
            if (open?.OpenedAt is { } closedAt && call.WorkspaceRoot is { } workspaceRoot && call.RunId is { } runId)
            {
                tools = Clock.Busy(workspaceRoot, runId, closedAt, boundary);
                Clock.Forget(workspaceRoot, runId, boundary);
            }

            PhaseFileWritten?.Invoke(phase);
            return new Applied(open, phase == DONE ? null : next, boundary, tools);
        }
    }

    /// <summary>Records the closed phase; false when the call was cancelled first and the record is lost.</summary>
    /// <param name="run">The Run.</param>
    /// <param name="closed">The phase file as it was before the declaration.</param>
    /// <param name="boundary">The instant the declaration was applied at.</param>
    /// <param name="tools">The closed phase's tool time, measured when the boundary was applied.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    private async Task<bool> CloseAsync(RunDirectory run, OrchestratorPhaseFile closed, DateTimeOffset boundary, TimeSpan tools,
                                        CancellationToken ct)
    {
        var openedAt = closed.OpenedAt!.Value;

        // Truncated before subtracting, so the three durations still add up to the second.
        var wall = Seconds(boundary - openedAt);
        tools = Seconds(tools);

        var session = Hosts.Current();
        UsageReading reading;
        if (session is null)
        {
            reading = new UsageReading(null, Missing: NO_HOST_SESSION);
        }
        else
        {
            try
            {
                if (_source.SettleDelay > TimeSpan.Zero)
                    await Task.Delay(_source.SettleDelay, Clock.Time, ct).ConfigureAwait(false);

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(_source.ReadDeadline);
                reading = await _source.ReadAsync(new UsageWindow(session, openedAt, boundary, closed.RegisteredAt, closed.Registering == true),
                                                  deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The phase file already moved on, so this phase's record is lost.
                LogFailure(run, "cancelled", closed.Phase);
                return false;
            }
            catch (OperationCanceledException)
            {
                reading = new UsageReading(null, Missing: SOURCE_FAILED, FailureKind: "source_timeout");
            }
            catch (Exception)
            {
                reading = new UsageReading(null, Missing: SOURCE_FAILED, FailureKind: SOURCE_FAILED);
            }
        }

        // However the cancellation came — during the write, with no source, or after a source that returned —
        // the phase file already moved on, so this phase's record is lost, with one entry (R10).
        if (ct.IsCancellationRequested)
        {
            LogFailure(run, "cancelled", closed.Phase);
            return false;
        }

        // A record that says source_failed has a failure behind it, whichever source left out its kind.
        var failed = reading.Tokens is null && (reading.Missing ?? SOURCE_FAILED) == SOURCE_FAILED;
        if ((reading.FailureKind ?? (failed ? SOURCE_FAILED : null)) is { } kind) LogFailure(run, kind, closed.Phase);
        if (reading.RegistrationFailureKind is { } registration) LogFailure(run, registration, closed.Phase);

        var client = Hosts.Client;
        var tokens = reading.Tokens;
        var incomplete = reading.Incomplete || !string.Equals(closed.HostSessionId, session?.Id, StringComparison.Ordinal);

        WorkerTelemetryFile.Append(run.TelemetryPath, run.Log,
            new OrchestratorUsageRecord(closed.OpenedAtLocal!,
                                        EVENT,
                                        closed.Phase!,
                                        closed.Round,
                                        closed.TaskNumber,
                                        closed.TaskCount,
                                        client,
                                        session?.Id,
                                        session is null ? null : _source.Name,
                                        VendorAttempt.FormatDuration(wall - tools),
                                        VendorAttempt.FormatDuration(wall),
                                        VendorAttempt.FormatDuration(tools),
                                        tokens?.InputTokens,
                                        tokens?.CacheReadTokens,
                                        tokens?.CacheCreationTokens,
                                        tokens?.OutputTokens,
                                        tokens?.ReasoningTokens,
                                        tokens?.CostUsd,
                                        incomplete,
                                        tokens is null ? reading.Missing ?? SOURCE_FAILED : null));
        return true;
    }

    /// <summary>
    /// Tells the source a phase opened, after any close's read; a registration it reports replaces the
    /// opening's <c>registering</c> mark in the phase file, so whichever process closes the phase knows
    /// it, and a registration that did not happen clears the mark. A phase already closed keeps what its
    /// close read.
    /// </summary>
    /// <param name="run">The Run.</param>
    /// <param name="opened">The phase file as the opening wrote it.</param>
    /// <param name="ct">Cancels the call on behalf of the MCP host.</param>
    private async Task OpenedAsync(RunDirectory run, OrchestratorPhaseFile opened, CancellationToken ct)
    {
        DateTimeOffset? registered = null;
        try
        {
            // The source keeps the deadlines: 2 seconds to register, after a restart of up to 10 (R10).
            registered = await _source.PhaseOpenedAsync(Hosts.Current(), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            LogFailure(run, "registration_failed", opened.Phase);
        }

        if (registered is null && opened.Registering != true) return;

        var path = Path.Combine(run.Path, FILE_NAME);
        string? failure = null;
        lock (_locks.GetOrAdd(Path.GetFullPath(path), _ => new object()))
        {
            // A reread that fails loses the marker, but never silently (R10).
            var (open, readFailure) = Read(path);
            if (readFailure is not null)
            {
                failure = readFailure;
            }
            else if (open is not null && open.Phase == opened.Phase && open.OpenedAt == opened.OpenedAt)
            {
                try
                {
                    AtomicFile.Write(path, JsonSerializer.Serialize(open with { RegisteredAt = registered, Registering = null },
                                                                    OrchestratorPhaseJson.Default.OrchestratorPhaseFile));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    failure = "phase_file_unwritable";
                }
            }
        }

        if (failure is not null) LogFailure(run, failure, opened.Phase);
    }

    /// <summary>
    /// The open phase, or null when none is open — the file absent, or holding the explicit
    /// no-phase state — with the failure kind when the file could not be read or held neither.
    /// </summary>
    /// <param name="path">The phase file.</param>
    private static (OrchestratorPhaseFile? Open, string? Failure) Read(string path)
    {
        string json;
        try
        {
            if (!File.Exists(path)) return (null, null);
            json = AtomicFile.Read(path);
        }
        catch (FileNotFoundException)
        {
            return (null, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (null, "phase_file_unreadable");
        }

        try
        {
            var file = JsonSerializer.Deserialize(json, OrchestratorPhaseJson.Default.OrchestratorPhaseFile);
            if (file is { State: NONE }) return (null, null);
            if (file is { State: OPEN, Phase: { } phase, OpenedAt: not null, OpenedAtLocal: not null }
                && phase != DONE && Names.Contains(phase, StringComparer.Ordinal))
                return (file, null);
        }
        catch (JsonException)
        {
        }

        return (null, "phase_file_invalid");
    }

    /// <summary>
    /// Where the Run stands for the phases that carry a position; omitted, with
    /// <c>position_unavailable</c>, when the state or the approved plan cannot be read.
    /// </summary>
    /// <param name="run">The Run.</param>
    /// <param name="phase">The phase being opened.</param>
    /// <param name="failures">Receives the failure kind.</param>
    private static Position ReadPosition(RunDirectory run, string phase, List<string> failures)
    {
        if (phase is not ("plan_revision" or "code_review" or "build")) return Position.None;

        try
        {
            var state = run.ReadState();
            if (phase == "plan_revision") return new Position(state.ReviewRounds, null, null);
            if (phase == "code_review") return new Position(state.CodeReviewRounds, null, null);
            if (!state.Approved) throw new NotApprovedException(run.RunId);

            var count = PlanTasks.Parse(run.ReadPlan()).Count;
            return state.TasksCompleted < count ? new Position(null, state.TasksCompleted + 1, count) : Position.None;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            failures.Add("position_unavailable");
            return Position.None;
        }
    }

    private string Local(DateTimeOffset at) =>
        VendorAttempt.FormatLocalTimestamp(TimeZoneInfo.ConvertTime(at, Clock.Time.LocalTimeZone));

    private static TimeSpan Seconds(TimeSpan span) => TimeSpan.FromSeconds(Math.Max(0, (long)span.TotalSeconds));

    private static void LogFailure(RunDirectory run, string kind, string? phase) =>
        run.Log.Write("warn", LOG_SOURCE, FAILED, ("kind", kind), ("phase", phase));

    private sealed record Position(int? Round, int? TaskNumber, int? TaskCount)
    {
        public static Position None { get; } = new(null, null, null);
    }

    /// <summary>What applying a declaration did under the Run's lock.</summary>
    /// <param name="Closed">The phase the declaration closed, if one was open.</param>
    /// <param name="Opened">The phase it opened, unless it was <c>done</c>.</param>
    /// <param name="Boundary">The instant the declaration was applied at.</param>
    /// <param name="Tools">The closed phase's tool time up to the boundary, measured as it was applied.</param>
    private sealed record Applied(OrchestratorPhaseFile? Closed, OrchestratorPhaseFile? Opened, DateTimeOffset Boundary, TimeSpan Tools);
}

/// <summary>The run folder's <c>orchestrator-phase.json</c>: the open phase, or the explicit no-phase state.</summary>
/// <param name="State"><c>open</c> while a phase is open; <c>none</c> after <c>done</c>.</param>
/// <param name="Phase">The open phase.</param>
/// <param name="OpenedAt">Its opening, as a UTC instant.</param>
/// <param name="OpenedAtLocal">Its opening in local time, as the record's <c>at</c>.</param>
/// <param name="Round">The round the Run was at when the phase opened.</param>
/// <param name="TaskNumber">The task being built when the phase opened.</param>
/// <param name="TaskCount">The approved plan's task count.</param>
/// <param name="HostSessionId">The Host session current at the opening.</param>
/// <param name="RegisteredAt">The collector registration the opening call obtained, when it made one.</param>
/// <param name="Registering">
/// True from the opening until the opening call's registration settles: that call registers the
/// session, so the registration covers the phase from its opening even for a close that overtakes it.
/// </param>
internal sealed record OrchestratorPhaseFile(string State,
                                             string? Phase = null,
                                             DateTimeOffset? OpenedAt = null,
                                             string? OpenedAtLocal = null,
                                             int? Round = null,
                                             int? TaskNumber = null,
                                             int? TaskCount = null,
                                             string? HostSessionId = null,
                                             DateTimeOffset? RegisteredAt = null,
                                             bool? Registering = null);

[JsonSourceGenerationOptions(WriteIndented = true,
                             PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OrchestratorPhaseFile))]
internal sealed partial class OrchestratorPhaseJson : JsonSerializerContext;
