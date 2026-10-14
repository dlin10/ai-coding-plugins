using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace PlanForge.Orchestration;

/// <summary>What a look for a collector on the export's port came to.</summary>
/// <param name="Answering">Whether a collector of this protocol answers there.</param>
/// <param name="Start">The start this look made, when it started one.</param>
internal sealed record CollectorLookup(bool Answering, CollectorStart? Start);

/// <summary>
/// The telemetry export Usage source (R7): the Host session's API-request events, kept by the
/// Telemetry collector on the export's port. Every request to the collector has 2 seconds; a close
/// first waits the host's export interval plus one second, so the response that made the declaring
/// call has arrived.
/// </summary>
/// <remarks>
/// A phase opening registers the session; one that finds no collector answering starts one through
/// the starter, within 10 seconds, and a restart that fails stops further restarts for five minutes
/// on the source's clock. A close never restarts: a collector that does not know the session answers
/// 404, which is <c>session_unknown</c>, and the session is registered again at once.
/// </remarks>
internal sealed class TelemetryUsageSource : IUsageSource
{
    private const string RESTARTED = "orchestrator.collector.restarted";
    private const string NO_TELEMETRY_EVENTS = "no_telemetry_events";
    private const string SESSION_UNKNOWN = "session_unknown";
    private const string SOURCE_TIMEOUT = "source_timeout";
    private const string REGISTRATION_FAILED = "registration_failed";

    private static readonly TimeSpan REQUEST_DEADLINE = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RESTART_DEADLINE = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RESTART_PAUSE = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan POLL = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SETTLE_MARGIN = TimeSpan.FromSeconds(1);

    private static readonly HttpClient HTTP = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly Lazy<DateTimeOffset> PROCESS_START = new(() =>
    {
        using var process = Process.GetCurrentProcess();
        return TelemetryCollector.ProcessStartTime(process);
    });

    private readonly ExportEndpoint _endpoint;
    private readonly Func<int, CollectorStart> _start;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private DateTimeOffset? _noRestartUntil;

    /// <summary>Creates the source over the export's endpoint.</summary>
    /// <param name="endpoint">Where the host exports, and its interval.</param>
    /// <param name="start">Starts a collector on a port: the starter, or a test's stand-in.</param>
    /// <param name="time">The clock the deadlines, the poll and the restart pause are measured on.</param>
    public TelemetryUsageSource(ExportEndpoint endpoint, Func<int, CollectorStart> start, TimeProvider time)
    {
        _endpoint = endpoint;
        _start = start;
        _time = time;
    }

    public string? Name => "telemetry";

    /// <summary>The host's export interval plus one second.</summary>
    public TimeSpan SettleDelay => _endpoint.Interval + SETTLE_MARGIN;

    /// <summary>The usage request and, after a 404, the registration again.</summary>
    public TimeSpan ReadDeadline => REQUEST_DEADLINE * 2 + SETTLE_MARGIN;

    public bool RegistersAtOpening => true;

    public async Task<UsageReading> ReadAsync(UsageWindow window, CancellationToken ct)
    {
        // A collector that is gone refuses the connection; see ProbeAsync for why that is not awaited.
        if (!IsListening(_endpoint.Port)) return Failed(OrchestratorPhases.SOURCE_FAILED);

        try
        {
            using var request = Deadline(ct, REQUEST_DEADLINE);
            using var answer = await SendAsync(HttpMethod.Get, UsagePath(window), null, request.Token).ConfigureAwait(false);
            if (answer.StatusCode == HttpStatusCode.NotFound)
            {
                // A collector that restarted or lost the session: register it again at once. A registration
                // that fails or misses its deadline is a failure of its own, beside the unknown session (R10).
                string? registration = null;
                try
                {
                    using var again = Deadline(ct, REQUEST_DEADLINE);
                    await RegisterAsync(window.Session, again.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (!ct.IsCancellationRequested && error is not OutOfMemoryException)
                {
                    registration = REGISTRATION_FAILED;
                }

                return Failed(SESSION_UNKNOWN) with { RegistrationFailureKind = registration };
            }

            if (answer.StatusCode != HttpStatusCode.OK) return Failed(OrchestratorPhases.SOURCE_FAILED);

            var usage = await answer.Content.ReadFromJsonAsync(CollectorJson.Default.CollectorUsage, request.Token).ConfigureAwait(false);
            if (usage is null) return Failed(OrchestratorPhases.SOURCE_FAILED);

            // Coverage began at the registration, unless the call that opened the phase made it — or is still
            // making it, when this close overtook that call.
            var covered = window.RegisteredAt is { } own ? usage.RegisteredAt == own : window.Registering;
            var incomplete = usage.Incomplete || usage.RegisteredAt > window.From && !covered;
            if (usage.KeptEvents == 0) return new UsageReading(null, incomplete, NO_TELEMETRY_EVENTS);
            return usage.Tokens is null
                ? new UsageReading(null, incomplete, OrchestratorPhases.SOURCE_FAILED, OrchestratorPhases.SOURCE_FAILED)
                : new UsageReading(usage.Tokens, incomplete);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed(SOURCE_TIMEOUT);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException or NotSupportedException)
        {
            return Failed(OrchestratorPhases.SOURCE_FAILED);
        }
    }

    /// <summary>
    /// Registers the session within 2 seconds; when no collector answers, starts one first, within
    /// 10 seconds, and writes <c>orchestrator.collector.restarted</c> to the opening call's Run log.
    /// </summary>
    /// <param name="session">The Host session at the opening, if there is one.</param>
    /// <param name="ct">The caller's cancellation.</param>
    public async Task<DateTimeOffset?> PhaseOpenedAsync(HostSession? session, CancellationToken ct)
    {
        if (session is null) return null;

        using (var first = Deadline(ct, REQUEST_DEADLINE))
        {
            switch (await ProbeAsync(first.Token).ConfigureAwait(false))
            {
                case Probe.Answering:
                    return await RegisterAsync(session, first.Token).ConfigureAwait(false);
                case Probe.Silent:
                    throw new TimeoutException("the collector did not answer");
            }
        }

        lock (_gate)
        {
            if (_noRestartUntil is { } until && _time.GetUtcNow() < until)
                throw new InvalidOperationException("no collector answers, and a restart failed less than five minutes ago");
        }

        CollectorLookup lookup;
        using (var restart = Deadline(ct, RESTART_DEADLINE))
        {
            lookup = await LookAsync(restart.Token).ConfigureAwait(false);
        }

        if (!lookup.Answering)
        {
            lock (_gate) _noRestartUntil = _time.GetUtcNow() + RESTART_PAUSE;
            throw new InvalidOperationException("the collector could not be restarted");
        }

        if (lookup.Start is { } started)
        {
            OrchestratorCall.Current?.Log?.Write("info", OrchestratorPhases.LOG_SOURCE, RESTARTED,
                                                 [("port", _endpoint.Port.ToString(CultureInfo.InvariantCulture)), .. started.JobFields()]);
        }

        using var second = Deadline(ct, REQUEST_DEADLINE);
        return await RegisterAsync(session, second.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds a collector of this protocol on the port, or starts one there and waits for it to answer,
    /// until <paramref name="ct"/> ends the wait; only an absent collector is started, never a silent one.
    /// </summary>
    /// <param name="ct">The look's deadline.</param>
    public async Task<CollectorLookup> LookAsync(CancellationToken ct)
    {
        CollectorStart? started = null;
        try
        {
            while (true)
            {
                Probe probe;
                using (var attempt = Deadline(ct, REQUEST_DEADLINE))
                {
                    probe = await ProbeAsync(attempt.Token).ConfigureAwait(false);
                }

                if (probe == Probe.Answering) return new CollectorLookup(true, started);
                if (probe == Probe.Absent && started is null)
                {
                    // The starter is synchronous: run it off this flow, so the look's deadline bounds it too;
                    // a start that returns after the deadline is discarded.
                    started = await Task.Run(() => _start(_endpoint.Port), CancellationToken.None).WaitAsync(ct).ConfigureAwait(false);
                    if (started.Outcome != CollectorStartOutcome.Started) return new CollectorLookup(false, started);
                }

                await Task.Delay(POLL, _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new CollectorLookup(false, started);
        }
    }

    /// <summary>Registers the session with this server process; answers when the collector's coverage of it began.</summary>
    /// <param name="session">The Host session.</param>
    /// <param name="ct">The request's deadline.</param>
    public async Task<DateTimeOffset> RegisterAsync(HostSession session, CancellationToken ct)
    {
        var registration = new SessionRegistration(session.Id, Environment.ProcessId, PROCESS_START.Value);
        using var body = JsonContent.Create(registration, CollectorJson.Default.SessionRegistration);
        using var answer = await SendAsync(HttpMethod.Post, "planforge/sessions", body, ct).ConfigureAwait(false);
        if (answer.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"the collector answered {(int)answer.StatusCode} to a registration");
        var registered = await answer.Content.ReadFromJsonAsync(CollectorJson.Default.RegistrationAnswer, ct).ConfigureAwait(false);
        return registered?.RegisteredAt ?? throw new JsonException("the registration answer is empty");
    }

    /// <summary>Whether a collector of this protocol answers: absent on a refused connection or a foreign answer, silent when nothing comes back.</summary>
    /// <param name="ct">The request's deadline.</param>
    private async Task<Probe> ProbeAsync(CancellationToken ct)
    {
        // Windows retries a refused loopback connect for about two seconds, the whole of a request's
        // deadline, so a port nothing listens on is recognised from the listener table instead.
        if (!IsListening(_endpoint.Port)) return Probe.Absent;

        try
        {
            using var answer = await SendAsync(HttpMethod.Get, "planforge/collector", null, ct).ConfigureAwait(false);
            if (answer.StatusCode != HttpStatusCode.OK) return Probe.Absent;
            var identity = await answer.Content.ReadFromJsonAsync(CollectorJson.Default.CollectorIdentity, ct).ConfigureAwait(false);
            return identity?.Protocol == TelemetryCollector.PROTOCOL ? Probe.Answering : Probe.Absent;
        }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.ConnectionError)
        {
            return Probe.Absent;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            return Probe.Absent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Probe.Silent;
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            return Probe.Silent;
        }
    }

    /// <summary>One request on a connection of its own, so a collector restarted on the port is never reached through a stale one.</summary>
    /// <param name="method">The method.</param>
    /// <param name="path">The path and query, relative to the endpoint's origin.</param>
    /// <param name="content">The body, if any.</param>
    /// <param name="ct">The request's deadline.</param>
    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, $"http://{_endpoint.Host}:{_endpoint.Port.ToString(CultureInfo.InvariantCulture)}/{path}")
        {
            Content = content,
        };
        request.Headers.ConnectionClose = true;
        return HTTP.SendAsync(request, ct);
    }

    /// <summary>Whether anything listens on the port, http.sys included; true when the table cannot be read.</summary>
    /// <param name="port">The port.</param>
    private static bool IsListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(listener => listener.Port == port);
        }
        catch (NetworkInformationException)
        {
            return true;
        }
    }

    private static string UsagePath(UsageWindow window) =>
        $"planforge/usage?session={Uri.EscapeDataString(window.Session.Id)}"
        + $"&from={Uri.EscapeDataString(window.From.ToString("o", CultureInfo.InvariantCulture))}"
        + $"&to={Uri.EscapeDataString(window.To.ToString("o", CultureInfo.InvariantCulture))}";

    /// <summary>The caller's cancellation, or <paramref name="after"/> on the source's clock, whichever comes first.</summary>
    /// <param name="ct">The caller's cancellation.</param>
    /// <param name="after">The deadline.</param>
    private Bounded Deadline(CancellationToken ct, TimeSpan after)
    {
        var timeout = new CancellationTokenSource(after, _time);
        return new Bounded(timeout, CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token));
    }

    private static UsageReading Failed(string kind) => new(null, Missing: OrchestratorPhases.SOURCE_FAILED, FailureKind: kind);

    private enum Probe { Answering, Absent, Silent }

    /// <summary>A deadline's timer on the source's clock and the token linked to it, disposed together.</summary>
    /// <param name="timeout">Cancels at the deadline.</param>
    /// <param name="linked">The caller's cancellation and the deadline together.</param>
    private sealed class Bounded(CancellationTokenSource timeout, CancellationTokenSource linked) : IDisposable
    {
        public CancellationToken Token => linked.Token;

        public void Dispose()
        {
            linked.Dispose();
            timeout.Dispose();
        }
    }
}
