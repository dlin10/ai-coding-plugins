using System.Globalization;
using PlanForge.Diagnostics;
using PlanForge.Run;
using PlanForge.Vendors.Codex;

namespace PlanForge.Orchestration;

/// <summary>
/// The one choice of a server process's Usage source (R6), made once, at the first tool call at which
/// the Host session is known: the telemetry export when the host's profile configures it and a
/// Telemetry collector of this protocol answers on its port — found there, or started there by this
/// server — within 10 seconds; the transcript otherwise. A call that arrives while the choice is being
/// made waits for that same choice. The choice and its reason are written once, to the
/// <c>forge.log</c> of the Run the deciding call serves, as that call ends — or, when that call was
/// refused before it opened a Run it names that cannot be found from its <c>workspaceRoot</c> alone,
/// at the end of the first later call of this process that serves that Run.
/// </summary>
/// <remarks>
/// <para>
/// One instance per server process, created in <c>Program.cs</c>; a test creates its own, with its own
/// environment and profile reader, so nothing here reads the real profile in the ordinary suite. The
/// profile is Claude Code's <c>settings.json</c> in its config dir and Codex's <c>config.toml</c> in its
/// home; the variables <c>PLANFORGE_CLAUDE_SETTINGS</c> and <c>PLANFORGE_CODEX_CONFIG</c> name other
/// files instead — test seams for the Integration tests, which give a real host's server test-owned
/// profile files.
/// </para>
/// <para>
/// When the telemetry export is chosen, the session is registered as part of the choice: by the phase
/// opening the deciding call makes, or else as that call ends — always after any close's read, so a
/// collector that lost the session is seen at that close (R9).
/// </para>
/// </remarks>
internal sealed class UsageSourceChoice : IUsageSource
{
    private const string CLAUDE_SETTINGS_VARIABLE = "PLANFORGE_CLAUDE_SETTINGS";
    private const string CODEX_CONFIG_VARIABLE = "PLANFORGE_CODEX_CONFIG";
    private const string CHOSEN = "orchestrator.source.chosen";
    private const string TELEMETRY = "telemetry";
    private const string TRANSCRIPT = "transcript";

    private static readonly TimeSpan CHOICE_DEADLINE = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan REGISTRATION_DEADLINE = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NO_SOURCE_DEADLINE = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly Func<string, string?> _environment;
    private readonly Func<string, string?> _readFile;
    private readonly Func<int, CollectorStart> _start;
    private readonly Lock _gate = new();
    private Task<Choice>? _choosing;
    private OrchestratorCall? _deciding;
    private PendingEntry? _pending;
    private bool _registrationDue;

    /// <summary>Creates the owner of one process's choice.</summary>
    /// <param name="time">The clock the choice's deadline and the restart pause are measured on.</param>
    /// <param name="environment">Reads the server process's environment variables.</param>
    /// <param name="readFile">Reads a profile file's text; null when it is absent or unreadable.</param>
    /// <param name="start">Starts a collector on a port: the starter, or a test's stand-in.</param>
    public UsageSourceChoice(TimeProvider time, Func<string, string?> environment, Func<string, string?> readFile,
                             Func<int, CollectorStart> start)
    {
        _time = time;
        _environment = environment;
        _readFile = readFile;
        _start = start;
    }

    /// <summary>The server process's own choice: the real environment, profile files and starter.</summary>
    public static UsageSourceChoice ForProcess() =>
        new(TimeProvider.System, Environment.GetEnvironmentVariable, ReadProfile, port => CollectorStarter.Start(port));

    /// <summary>The chosen source, once the choice is made; null before it or without a Host session.</summary>
    private IUsageSource? Chosen => _choosing is { IsCompletedSuccessfully: true } choosing ? choosing.Result.Source : null;

    public string? Name => Chosen?.Name;

    public TimeSpan SettleDelay => Chosen?.SettleDelay ?? TimeSpan.Zero;

    public TimeSpan ReadDeadline => Chosen?.ReadDeadline ?? NO_SOURCE_DEADLINE;

    public bool RegistersAtOpening => Chosen?.RegistersAtOpening ?? false;

    public async Task<UsageReading> ReadAsync(UsageWindow window, CancellationToken ct)
    {
        var source = _choosing is { } choosing ? (await choosing.WaitAsync(ct).ConfigureAwait(false)).Source : null;
        return source is null
            ? new UsageReading(null, Missing: OrchestratorPhases.NO_HOST_SESSION)
            : await source.ReadAsync(window, ct).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> PhaseOpenedAsync(HostSession? session, CancellationToken ct)
    {
        if (Chosen is not { } source) return null;

        // An opening's registration is the choice's too, when it comes first.
        lock (_gate) _registrationDue = false;
        return await source.PhaseOpenedAsync(session, ct).ConfigureAwait(false);
    }

    /// <summary>Starts the choice at the first call with a Host session; any call waits for a choice being made.</summary>
    /// <param name="session">The Host session as the call leaves it.</param>
    /// <param name="call">The call.</param>
    public async Task CallArrivedAsync(HostSession? session, OrchestratorCall call)
    {
        Task<Choice>? choosing;
        lock (_gate)
        {
            if (_choosing is null && session is not null)
            {
                _deciding = call;
                _choosing = Task.Run(() => ChooseAsync(session));
            }

            choosing = _choosing;
        }

        // Bounded by the choice's own 10 seconds; it never throws.
        if (choosing is not null) await choosing.ConfigureAwait(false);
    }

    /// <summary>
    /// At the end of the deciding call: the choice's registration, if no opening made it, and the choice
    /// entry; at the end of a later call that serves the Run a held choice entry waits for, that entry.
    /// </summary>
    /// <param name="call">The call.</param>
    public async Task CallEndedAsync(OrchestratorCall call)
    {
        Task<Choice> choosing;
        bool register;
        PendingEntry? due = null;
        lock (_gate)
        {
            if (_pending is { } pending && call.Log is not null
                && string.Equals(CallClock.Key(call.WorkspaceRoot, call.RunId), pending.RunKey, StringComparison.OrdinalIgnoreCase))
            {
                due = pending;
                _pending = null;
            }
        }

        if (due is not null)
        {
            call.Log!.Write("info", OrchestratorPhases.LOG_SOURCE, CHOSEN, due.Fields);
            return;
        }

        lock (_gate)
        {
            if (_deciding is null || !ReferenceEquals(_deciding, call)) return;
            _deciding = null;
            choosing = _choosing!;
            register = _registrationDue;
            _registrationDue = false;
        }

        var choice = await choosing.ConfigureAwait(false);
        var log = call.Log ?? OpenLog(call);
        if (register && choice.Source is TelemetryUsageSource telemetry)
        {
            try
            {
                using var deadline = new CancellationTokenSource(REGISTRATION_DEADLINE, _time);
                await telemetry.RegisterAsync(choice.Session, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                log?.Write("warn", OrchestratorPhases.LOG_SOURCE, OrchestratorPhases.FAILED, ("kind", "registration_failed"));
            }
        }

        if (log is not null)
        {
            log.Write("info", OrchestratorPhases.LOG_SOURCE, CHOSEN, choice.Fields);
        }
        else if (CallClock.Key(call.WorkspaceRoot, call.RunId) is { } runKey)
        {
            // Refused or failed before it opened a Run this lookup cannot find — under a session root, say:
            // the entry waits for the first later call of this process that serves that Run.
            lock (_gate) _pending = new PendingEntry(runKey, choice.Fields);
        }
    }

    /// <summary>The choice itself, within 10 seconds; never throws.</summary>
    /// <param name="session">The Host session the choice is made for.</param>
    private async Task<Choice> ChooseAsync(HostSession session)
    {
        var claude = session.Kind == HostSessions.CLAUDE_CODE;
        try
        {
            using var deadline = new CancellationTokenSource(CHOICE_DEADLINE, _time);

            // Reading and parsing the profile are synchronous: run off this flow, so the deadline bounds them too.
            var verdict = await Task.Run(() => claude
                                             ? ExportProfile.Claude(_readFile(ProfilePath(CLAUDE_SETTINGS_VARIABLE, ClaudeConfigDirectory.Resolve(_environment), "settings.json")))
                                             : ExportProfile.Codex(_readFile(ProfilePath(CODEX_CONFIG_VARIABLE, CodexSandbox.Home(_environment), "config.toml"))),
                                     CancellationToken.None)
                                    .WaitAsync(deadline.Token)
                                    .ConfigureAwait(false);
            if (verdict.Endpoint is not { } endpoint) return Transcript(session, verdict.Reason);

            var telemetry = new TelemetryUsageSource(endpoint, _start, _time);
            var lookup = await telemetry.LookAsync(deadline.Token).ConfigureAwait(false);
            var port = endpoint.Port.ToString(CultureInfo.InvariantCulture);
            if (!lookup.Answering)
            {
                return Transcript(session, lookup.Start switch
                {
                    { Outcome: CollectorStartOutcome.CannotDetach } =>
                        "the server's job object refuses breakaway and a collector could not be started inside it",
                    { Outcome: CollectorStartOutcome.Failed } => "a collector could not be started",
                    _ => $"no collector answered on port {port} within 10 seconds",
                }, port);
            }

            lock (_gate) _registrationDue = true;
            (string, string?)[] started = lookup.Start is { } start
                ? [("collector", "started"), .. start.JobFields()]
                : [("collector", "found")];
            return new Choice(telemetry, session,
                              [("usageSource", TELEMETRY), ("hostSession", session.Kind), ("reason", verdict.Reason), ("port", port), .. started]);
        }
        catch (OperationCanceledException)
        {
            return Transcript(session, "the profile was not read within 10 seconds");
        }
        catch (Exception)
        {
            return Transcript(session, "the profile could not be read");
        }
    }

    private Choice Transcript(HostSession session, string reason, string? port = null) =>
        new(TranscriptUsageSource.ForProcess(_environment), session,
            [("usageSource", TRANSCRIPT), ("hostSession", session.Kind), ("reason", reason), ("port", port)]);

    private string ProfilePath(string variable, string directory, string file) =>
        _environment(variable) is { Length: > 0 } path ? path : Path.Combine(directory, file);

    /// <summary>The log of the Run a call names, when it exists; for a deciding call no tool served.</summary>
    /// <param name="call">The call.</param>
    private static RunLog? OpenLog(OrchestratorCall call)
    {
        if (call.WorkspaceRoot is not { } workspaceRoot || call.RunId is not { } runId) return null;
        try
        {
            return RunDirectory.Open(workspaceRoot, runId).Log;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadProfile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The process's one choice, as it is used and logged.</summary>
    /// <param name="Source">The chosen source.</param>
    /// <param name="Session">The Host session the choice was made for.</param>
    /// <param name="Fields">The choice entry's fields: the source, the reason, the port and the collector's start.</param>
    private sealed record Choice(IUsageSource Source, HostSession Session, (string Name, string? Value)[] Fields);

    /// <summary>A choice entry whose deciding call named a Run it could not reach, held for the first later call that serves it.</summary>
    /// <param name="RunKey">The Run's key, as <see cref="CallClock.Key"/> forms it from the deciding call's arguments.</param>
    /// <param name="Fields">The choice entry's fields.</param>
    private sealed record PendingEntry(string RunKey, (string Name, string? Value)[] Fields);
}
