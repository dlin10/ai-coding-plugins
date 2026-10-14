using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlanForge.Diagnostics;
using PlanForge.Run;

namespace PlanForge.Orchestration;

/// <summary>
/// One tool call as the Orchestrator's telemetry sees it: when it arrived, the Run it names, and the
/// host it came from. Ambient for the call's async flow, so a tool method finds it without a
/// parameter of its own.
/// </summary>
internal sealed class OrchestratorCall
{
    private static readonly AsyncLocal<OrchestratorCall?> AMBIENT = new();

    private readonly CallClock.Ticket _ticket;

    private OrchestratorCall(OrchestratorPhases phases, DateTimeOffset arrival, string? workspaceRoot, string? runId,
                             string? client, string? threadId)
    {
        Phases = phases;
        Arrival = arrival;
        WorkspaceRoot = workspaceRoot;
        RunId = runId;
        Client = client;
        ThreadId = threadId;
        _ticket = phases.Clock.Enter(arrival, workspaceRoot, runId);
    }

    /// <summary>The call in flight on this async flow, or null outside one.</summary>
    public static OrchestratorCall? Current => AMBIENT.Value;

    public OrchestratorPhases Phases { get; }

    public DateTimeOffset Arrival { get; }

    public string? WorkspaceRoot { get; private set; }

    public string? RunId { get; private set; }

    public string? Client { get; }

    public string? ThreadId { get; }

    /// <summary>The log of the Run the call serves, once a tool has opened that Run.</summary>
    public RunLog? Log { get; private set; }

    /// <summary>The call-tool filter registered in <c>Program.cs</c>.</summary>
    /// <param name="phases">The process's phase owner, whose clock counts the call.</param>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Filter(OrchestratorPhases phases) =>
        next => (request, ct) => InvokeAsync(phases, Argument(request, "workspaceRoot"), Argument(request, "runId"),
                                             request?.Server?.ClientInfo?.Name, ThreadIdOf(request?.Params?.Meta),
                                             () => next(request!, ct));

    /// <summary>
    /// The filter's logic without SDK objects: stamps the arrival, enters the Run's clock, makes the
    /// call ambient for <paramref name="next"/>, and leaves the clock however the call ends. The
    /// process's Usage source hears of the call's arrival — where its choice is made or waited for —
    /// and of its end, both inside the call's time.
    /// </summary>
    /// <typeparam name="T">The call's result.</typeparam>
    /// <param name="phases">The phase owner whose clock counts the call.</param>
    /// <param name="workspaceRoot">The call's <c>workspaceRoot</c> argument, if any.</param>
    /// <param name="runId">The call's <c>runId</c> argument, if any.</param>
    /// <param name="client">The MCP client's name, if known.</param>
    /// <param name="threadId">The <c>threadId</c> the call's <c>_meta</c> carries, if any.</param>
    /// <param name="next">The rest of the call.</param>
    public static async ValueTask<T> InvokeAsync<T>(OrchestratorPhases phases,
                                                    string? workspaceRoot,
                                                    string? runId,
                                                    string? client,
                                                    string? threadId,
                                                    Func<ValueTask<T>> next)
    {
        using var scope = Arrive(phases, workspaceRoot, runId, client, threadId);
        await phases.ArrivedAsync(scope.Call).ConfigureAwait(false);
        try
        {
            return await next().ConfigureAwait(false);
        }
        finally
        {
            await phases.EndedAsync(scope.Call).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The ambient call, or — for a call that never passed the filter, such as a test calling a tool
    /// method directly — a call entered now on the process's phase owner, left when the scope ends.
    /// </summary>
    /// <param name="workspaceRoot">The call's <c>workspaceRoot</c>.</param>
    /// <param name="runId">The call's <c>runId</c>, if it names one.</param>
    public static Scope Join(string? workspaceRoot, string? runId) =>
        Current is { } current ? new Scope(current, entered: false, null) : Arrive(OrchestratorPhases.Process, workspaceRoot, runId, null, null);

    /// <summary>Names the Run the call belongs to; <c>forge.begin</c> names the Run it creates.</summary>
    /// <param name="workspaceRoot">The Run's workspace root.</param>
    /// <param name="runId">The Run's id.</param>
    public void Attach(string workspaceRoot, string runId)
    {
        if (RunId is null)
        {
            WorkspaceRoot = workspaceRoot;
            RunId = runId;
        }

        _ticket.Attach(workspaceRoot, runId);
    }

    /// <summary>Notes the Run the call serves, once it is open, so its log can take the call's telemetry entries.</summary>
    /// <param name="run">The Run.</param>
    public void Serve(RunDirectory run) => Log ??= run.Log;

    private static Scope Arrive(OrchestratorPhases phases, string? workspaceRoot, string? runId, string? client, string? threadId)
    {
        phases.Hosts.Observe(client, threadId);
        var call = new OrchestratorCall(phases, phases.Clock.Time.GetUtcNow(), workspaceRoot, runId, client, threadId);
        var previous = AMBIENT.Value;
        AMBIENT.Value = call;
        return new Scope(call, entered: true, previous);
    }

    private static string? Argument(RequestContext<CallToolRequestParams>? request, string name) =>
        request?.Params?.Arguments is { } arguments && arguments.TryGetValue(name, out var value)
                                                    && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The <c>threadId</c> a call's <c>_meta</c> carries, when it is a non-empty string.</summary>
    /// <param name="meta">The call's <c>_meta</c>.</param>
    internal static string? ThreadIdOf(JsonObject? meta) =>
        meta?["threadId"] is JsonValue value && value.TryGetValue<string>(out var id) && id.Length > 0 ? id : null;

    /// <summary>The call's stay on its async flow; disposing a scope that entered the call leaves the clock.</summary>
    /// <param name="call">The call.</param>
    /// <param name="entered">Whether this scope entered the call, and so leaves it.</param>
    /// <param name="previous">The ambient call to restore when an entered scope ends.</param>
    internal sealed class Scope(OrchestratorCall call, bool entered, OrchestratorCall? previous) : IDisposable
    {
        private bool _disposed;

        public OrchestratorCall Call => call;

        public void Dispose()
        {
            if (_disposed || !entered) return;
            _disposed = true;
            AMBIENT.Value = previous;
            call._ticket.Leave();
        }
    }
}
