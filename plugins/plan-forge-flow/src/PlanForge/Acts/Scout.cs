using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PlanForge.Prompts;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// One bounded, read-only reconnaissance turn. The complete answer is returned to the orchestrator
/// and appended to the run's Scout report.
/// </summary>
internal sealed class Scout
{
    internal const int MaxQuestionLength = 8000;

    private readonly IVendor _vendor;
    private readonly PromptLibrary _prompts;

    public Scout(IVendor vendor, PromptLibrary prompts)
    {
        _vendor = vendor;
        _prompts = prompts;
    }

    /// <summary>Set when the question's Fast turn was served at standard speed for part of it.</summary>
    internal string? SpeedWarning { get; private set; }

    /// <summary>Runs a bounded question, guards its plain strings and records the complete answer.</summary>
    /// <param name="run">The Run receiving the answer.</param>
    /// <param name="question">The bounded reconnaissance question.</param>
    /// <param name="sessionMode">Whether to start fresh or continue the Scout session.</param>
    /// <param name="ct">Cancels the Scout turn.</param>
    public async Task<ScoutReport> RunAsync(RunDirectory run,
                                            string question,
                                            string sessionMode,
                                            CancellationToken ct)
    {
        ValidateQuestion(question);
        ValidateSessionMode(sessionMode);
        SensitiveInput.Guard(question, "the Scout question");

        RunState? state = null;
        ScoutState? selected = null;
        string? suppliedToken = null;
        IVendorSession? session = null;
        ScoutFlight? flight = null;

        try
        {
            state = run.ReadState();
            selected = RequireSelection(state);
            flight = ScoutFlight.Enter(run);
            suppliedToken = SafeToken(selected.SessionId);
            var resumeToken = sessionMode == "continue" ? suppliedToken : null;

            // A fresh call cuts the old anchor before a Vendor process can be created. If the
            // process never reports a new token, the abandoned anchor cannot return to state.
            if (sessionMode == "fresh")
            {
                state = state with { Scout = selected with { SessionId = null } };
                run.WriteState(state);
            }

            var prompt = Compose(question, state.WorkspaceRoot);
            await using var started = await _vendor.StartAsync(
                new RoleSpec(VendorRole.Scout,
                             _prompts.Load(_vendor.Id, VendorRole.Scout),
                             WorkerTools: WorkerTools.Effective(state.WorkerTools),
                             Telemetry: new WorkerTelemetryContext(run.TelemetryPath, run.Log, "scout")),
                new Selection(selected.Model!, selected.Effort, selected.Fast), resumeToken, ct).ConfigureAwait(false);
            session = started;

            var report = await started.RunAsync(prompt, Schemas.ScoutReport, ct).ConfigureAwait(false);
            SpeedWarning = started.SpeedWarning;

            // The complete report is guarded before the atomic append, so the previous
            // report survives a sensitive Vendor answer.
            try
            {
                SensitiveInput.Guard(report.Summary, "the Scout response");
                foreach (var category in ScoutCategories.Of(report))
                {
                    foreach (var item in category.Items)
                    {
                        SensitiveInput.Guard(item.Text, "the Scout response");
                        SensitiveInput.Guard(item.Source, "the Scout response");
                    }
                }
            }
            catch (SensitiveContentException)
            {
                throw new ScoutSensitiveOutputException();
            }

            var anchor = ResumeAnchor(started, suppliedToken, sessionMode);
            var locators = ScoutLocators.Of(state.WorkspaceRoot, report);
            var number = 0;

            // The state is read again here, not taken from the start of the turn: the Orchestrator and
            // other Scout turns wrote it while this one ran. Numbering and appending under one lock
            // keeps the numbers increasing in the file when turns finish together.
            lock (flight.Gate)
            {
                var kept = flight.Overlapped ? null : anchor;
                run.UpdateState(current =>
                {
                    number = current.ScoutAnswers + 1;
                    return Settled(current, selected, scout => scout with { SessionId = kept, LastFailure = null })
                        with { ScoutAnswers = number };
                });
                run.AppendScoutAnswer(new ScoutAnswerEntry(number, question, report, locators));
            }

            run.AppendFlowScoutOutcome("completed", question);
            run.Log.Write("info", "scout", "scout.completed",
                ("vendor", _vendor.Id), ("model", selected.Model), ("effort", selected.Effort),
                ("fast", selected.Fast ? "true" : "false"),
                ("sessionMode", ActualSessionMode(resumeToken)), ("report", run.ScoutAnswersPath));

            return report;
        }
        catch (OperationCanceledException)
        {
            PersistCancellation(run, state, selected, suppliedToken, session, sessionMode, question, flight);
            throw;
        }
        catch (Exception error)
        {
            var failure = FailureFor(error);
            PersistFailure(run, state, selected, suppliedToken, session, sessionMode, question, failure, flight);
            throw new ScoutException(failure);
        }
        finally
        {
            flight?.Dispose();
        }
    }

    internal static void ValidateQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentRejectedException("forge.scout.run requires a non-blank question");
        if (question.Length > MaxQuestionLength)
            throw new ArgumentRejectedException($"forge.scout.run question cannot exceed {MaxQuestionLength} characters");
    }

    internal static void ValidateSessionMode(string sessionMode)
    {
        if (sessionMode is not ("fresh" or "continue"))
            throw new ArgumentRejectedException("sessionMode must be exactly fresh or continue");
    }

    internal static ScoutState RequireSelection(RunState state) =>
        state.Scout is { Enabled: true, Vendor: { Length: > 0 }, Model: { Length: > 0 } } selected
            ? selected
            : throw new ArgumentRejectedException("Scout is not enabled for this run; select an exact Scout Vendor and model first");

    private static string Compose(string question, string workspaceRoot) =>
        new StringBuilder()
            .AppendLine("# Current bounded Scout question")
            .AppendLine()
            .AppendLine(question)
            .AppendLine()
            .AppendLine("# Working repository")
            .AppendLine()
            .AppendLine(workspaceRoot)
            .ToString();

    private static ScoutFailure FailureFor(Exception error) =>
        error switch
        {
            ScoutSensitiveOutputException => ScoutFailures.SensitiveOutput,
            JsonException => ScoutFailures.InvalidOutput,
            NoShellException noShell => ScoutFailures.NoShell(noShell.ErrorCode),
            VendorException => ScoutFailures.VendorFailed,
            _ => ScoutFailures.Unknown
        };

    private static string? ResumeAnchor(IVendorSession session, string? suppliedToken, string sessionMode) =>
        SafeSessionToken(session) ?? (sessionMode == "continue" ? suppliedToken : null);

    private static string? SafeSessionToken(IVendorSession session)
    {
        try
        {
            return session.CanResume ? SafeToken(session.ResumeToken) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SafeToken(string? token) =>
        token is { Length: > 0 and <= 256 }
        && !token.Any(char.IsWhiteSpace)
        && token.All(character => !char.IsControl(character))
            ? token
            : null;

    private static string ActualSessionMode(string? resumeToken) =>
        resumeToken is { Length: > 0 } ? "resumed" : "fresh";

    /// <summary>
    /// The state with what a turn leaves on its Scout choice, or as it is when the user chose another
    /// Scout while the turn ran: the turn's session and failure belong to the choice it ran with.
    /// </summary>
    /// <param name="current">The state as it stands at the write.</param>
    /// <param name="selected">The Scout choice the turn ran with.</param>
    /// <param name="change">What the turn leaves on that choice.</param>
    private static RunState Settled(RunState current, ScoutState selected, Func<ScoutState, ScoutState> change) =>
        current.Scout is { } scout
        && scout with { SessionId = null, LastFailure = null } == selected with { SessionId = null, LastFailure = null }
            ? current with { Scout = change(scout) }
            : current;

    private static void PersistCancellation(RunDirectory run,
                                            RunState? state,
                                            ScoutState? selected,
                                             string? suppliedToken,
                                             IVendorSession? session,
                                             string sessionMode,
                                             string question,
                                             ScoutFlight? flight)
    {
        if (state is null || selected is null) return;

        try
        {
            var anchor = flight is { Overlapped: true } ? null
                         : session is null ? sessionMode == "continue" ? suppliedToken : null
                         : ResumeAnchor(session, suppliedToken, sessionMode);
            run.UpdateState(current => Settled(current, selected, scout => scout with { SessionId = anchor }));
            run.AppendFlowScoutOutcome("cancelled", question);
            run.Log.Write("warn", "scout", "scout.cancelled", ("sessionMode", ActualSessionMode(suppliedToken)));
        }
        catch (Exception)
        {
            // Cancellation must retain its repository semantics even if a best-effort audit write
            // cannot complete.
        }
    }

    private void PersistFailure(RunDirectory run,
                                RunState? state,
                                ScoutState? selected,
                                string? suppliedToken,
                                IVendorSession? session,
                                string sessionMode,
                                string question,
                                ScoutFailure failure,
                                ScoutFlight? flight)
    {
        if (state is null || selected is null) return;

        var anchor = flight is { Overlapped: true } ? null
                     : session is null ? sessionMode == "continue" ? suppliedToken : null
                     : ResumeAnchor(session, suppliedToken, sessionMode);
        try
        {
            run.UpdateState(current => Settled(current, selected,
                                               scout => scout with { SessionId = anchor, LastFailure = failure }));
        }
        catch (Exception)
        {
            // The safe exception below still prevents the original failure from crossing the act
            // boundary when the run folder itself is unavailable.
        }

        try
        {
            run.AppendFlowScoutOutcome("failed", question, failure);
            run.Log.Write("error", "scout", "scout.failed",
                ("vendor", _vendor.Id), ("model", selected.Model), ("effort", selected.Effort),
                ("fast", selected.Fast ? "true" : "false"),
                ("sessionMode", ActualSessionMode(suppliedToken)), ("code", failure.Code),
                ("summary", failure.Summary));
        }
        catch (Exception)
        {
            // A failed audit must not replace the fixed failure that crosses the act boundary.
        }
    }
}

/// <summary>
/// One Scout turn in flight in this server process. Turns of one Run that overlap — fresh jobs side by
/// side, or a direct call beside a job — each leave the Run with no session anchor, because none of
/// their sessions is the one investigation a later <c>continue</c> would mean. See docs/adr/0044.
/// </summary>
internal sealed class ScoutFlight : IDisposable
{
    private static readonly ConcurrentDictionary<string, List<ScoutFlight>> FLIGHTS = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ScoutFlight> _peers;
    private bool _overlapped;

    private ScoutFlight(List<ScoutFlight> peers) => _peers = peers;

    /// <summary>The one lock every turn of the Run settles its answer under.</summary>
    public object Gate => _peers;

    /// <summary>Whether another turn of the Run was in flight at any moment of this one.</summary>
    public bool Overlapped
    {
        get
        {
            lock (_peers) return _overlapped;
        }
    }

    /// <summary>Records a turn of <paramref name="run"/> as in flight, and the turns already in flight as overlapped.</summary>
    /// <param name="run">The Run the turn belongs to.</param>
    public static ScoutFlight Enter(RunDirectory run)
    {
        var peers = FLIGHTS.GetOrAdd(Path.GetFullPath(run.Path), _ => []);
        lock (peers)
        {
            var flight = new ScoutFlight(peers) { _overlapped = peers.Count > 0 };
            foreach (var peer in peers) peer._overlapped = true;
            peers.Add(flight);
            return flight;
        }
    }

    public void Dispose()
    {
        lock (_peers) _peers.Remove(this);
    }
}

internal sealed class ScoutException(ScoutFailure failure)
    : Exception($"{failure.Code}: {failure.Summary}");

internal sealed class ScoutSensitiveOutputException()
    : Exception("Scout response contained sensitive content, was not persisted, and the previous report was preserved.");

internal static class ScoutFailures
{
    public static ScoutFailure VendorFailed { get; } =
        new("vendor_failed", "Scout vendor failed; the previous report was preserved.");

    public static ScoutFailure InvalidOutput { get; } =
        new("invalid_output", "Scout returned invalid structured output; the previous report was preserved.");

    public static ScoutFailure SensitiveOutput { get; } =
        new("sensitive_output", "Scout response contained sensitive content, was not persisted, and the previous report was preserved.");

    public static ScoutFailure Unknown { get; } =
        new("unknown", "Scout failed for an unknown reason; the previous report was preserved.");

    /// <summary>
    /// The Scout tried commands and its vendor started none, so its answer was written without
    /// reading the repository. Fixed text like every Scout failure, except for the vendor's error
    /// code, which <see cref="NoShellException"/> carries only when it is a bare identifier.
    /// </summary>
    /// <param name="errorCode">The vendor's error code, such as <c>helper_unknown_error</c>, or null.</param>
    public static ScoutFailure NoShell(string? errorCode) =>
        new("no_shell",
            $"Scout's worker could not start a shell{(errorCode is null ? string.Empty : $" ({errorCode})")}, "
            + "so it answered without reading the repository; the answer was discarded and the previous report was preserved.");
}
