namespace PlanForge.Orchestration;

/// <summary>The host's own conversation the Orchestrator runs in: a Claude Code session or a Codex thread.</summary>
/// <param name="Kind">Where the identity came from: <c>claude-code</c> or <c>codex</c>.</param>
/// <param name="Id">The session id, or the thread id.</param>
internal sealed record HostSession(string Kind, string Id);

/// <summary>What a Usage source is asked about at a close.</summary>
/// <param name="Session">The Host session current at the close.</param>
/// <param name="From">The phase's opening, included.</param>
/// <param name="To">The phase's closing, excluded.</param>
/// <param name="RegisteredAt">The registration time the opening call obtained, when it registered the session.</param>
/// <param name="Registering">
/// Whether the opening call was registering the session when the phase was read, its time not yet known:
/// that registration covers the phase from its opening all the same (R9).
/// </param>
internal sealed record UsageWindow(HostSession Session, DateTimeOffset From, DateTimeOffset To, DateTimeOffset? RegisteredAt,
                                   bool Registering = false);

/// <summary>A phase's tokens, as the Orchestrator usage record carries them.</summary>
/// <param name="InputTokens">Input tokens, cache reads and writes included.</param>
/// <param name="CacheReadTokens">Tokens read from the prompt cache.</param>
/// <param name="CacheCreationTokens">Tokens written to the prompt cache.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="ReasoningTokens">Reasoning tokens, where the source reports them.</param>
/// <param name="CostUsd">Cost in US dollars, where the source reports it.</param>
internal sealed record OrchestratorTokens(long? InputTokens = null,
                                          long? CacheReadTokens = null,
                                          long? CacheCreationTokens = null,
                                          long? OutputTokens = null,
                                          long? ReasoningTokens = null,
                                          double? CostUsd = null);

/// <summary>A Usage source's answer: counters, or the reason there are none.</summary>
/// <param name="Tokens">The counters, or null when none are recorded.</param>
/// <param name="Incomplete">Whether the counters cover only part of the phase.</param>
/// <param name="Missing">Why no counters are recorded: <c>no_host_session</c>, <c>source_failed</c> or <c>no_telemetry_events</c>.</param>
/// <param name="FailureKind">The <c>orchestrator.telemetry.failed</c> kind to log, when the answer is a failure.</param>
/// <param name="RegistrationFailureKind">
/// A second failure the read met, logged after <paramref name="FailureKind"/>: the re-registration after
/// an unknown session that failed or missed its deadline.
/// </param>
internal sealed record UsageReading(OrchestratorTokens? Tokens,
                                    bool Incomplete = false,
                                    string? Missing = null,
                                    string? FailureKind = null,
                                    string? RegistrationFailureKind = null);

/// <summary>Where Orchestrator tokens are read from, chosen once per server process.</summary>
internal interface IUsageSource
{
    /// <summary>The record's <c>usageSource</c>: <c>telemetry</c>, <c>transcript</c>, or null for none.</summary>
    string? Name { get; }

    /// <summary>How long a close waits before it reads, so that the response which made the declaring call is in.</summary>
    TimeSpan SettleDelay { get; }

    /// <summary>The longest a read may take; a read past it is a <c>source_timeout</c>.</summary>
    TimeSpan ReadDeadline { get; }

    /// <summary>The Host session's usage between the window's two instants.</summary>
    /// <param name="window">The session and the phase's window.</param>
    /// <param name="ct">The caller's deadline.</param>
    Task<UsageReading> ReadAsync(UsageWindow window, CancellationToken ct);

    /// <summary>
    /// Called once a phase opens, after any close's read; answers the registration time when it
    /// registered the session. The source keeps its own deadlines.
    /// </summary>
    /// <param name="session">The Host session at the opening, if there is one.</param>
    /// <param name="ct">The caller's deadline.</param>
    Task<DateTimeOffset?> PhaseOpenedAsync(HostSession? session, CancellationToken ct);

    /// <summary>Whether <see cref="PhaseOpenedAsync"/> registers a known Host session with a collector.</summary>
    bool RegistersAtOpening => false;

    /// <summary>Called as a tool call arrives, before it runs; never throws.</summary>
    /// <param name="session">The Host session as the call leaves it.</param>
    /// <param name="call">The call.</param>
    Task CallArrivedAsync(HostSession? session, OrchestratorCall call) => Task.CompletedTask;

    /// <summary>Called as a tool call ends, however it ends; never throws.</summary>
    /// <param name="call">The call.</param>
    Task CallEndedAsync(OrchestratorCall call) => Task.CompletedTask;
}

/// <summary>No source: every phase is recorded with time only.</summary>
internal sealed class NoUsageSource : IUsageSource
{
    public static NoUsageSource Instance { get; } = new();

    public string? Name => null;

    public TimeSpan SettleDelay => TimeSpan.Zero;

    public TimeSpan ReadDeadline => TimeSpan.FromSeconds(10);

    public Task<UsageReading> ReadAsync(UsageWindow window, CancellationToken ct) =>
        Task.FromResult(new UsageReading(null, Missing: OrchestratorPhases.NO_HOST_SESSION));

    public Task<DateTimeOffset?> PhaseOpenedAsync(HostSession? session, CancellationToken ct) =>
        Task.FromResult<DateTimeOffset?>(null);
}
