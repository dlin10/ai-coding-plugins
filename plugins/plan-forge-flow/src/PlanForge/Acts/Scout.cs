using System.Text;
using System.Text.Json;
using PlanForge.Prompts;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// One bounded, read-only reconnaissance turn. The full answer is kept as the latest run document;
/// only the bounded digest crosses back to the orchestrator.
/// </summary>
internal sealed class Scout
{
    internal const int MaxQuestionLength = 4000;
    internal const int DigestSummaryLength = 800;
    internal const int DigestItemsPerCategory = 3;
    internal const int DigestItemTextLength = 400;
    internal const int DigestSourceLength = 300;

    private readonly IVendor _vendor;
    private readonly PromptLibrary _prompts;

    public Scout(IVendor vendor, PromptLibrary prompts)
    {
        _vendor = vendor;
        _prompts = prompts;
    }

    /// <summary>Set when the question's Fast turn was served at standard speed for part of it.</summary>
    internal string? SpeedWarning { get; private set; }

    public async Task<ScoutDigest> RunAsync(RunDirectory run,
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

        try
        {
            state = run.ReadState();
            selected = RequireSelection(state);
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
            var rendered = Render(report);

            // The complete report is guarded before the atomic replacement, so the previous
            // snapshot survives a sensitive Vendor answer.
            try
            {
                SensitiveInput.Guard(rendered, "the Scout response");
            }
            catch (SensitiveContentException)
            {
                throw new ScoutSensitiveOutputException();
            }

            run.WriteScoutReport(rendered);

            var anchor = ResumeAnchor(started, suppliedToken, sessionMode);
            run.WriteState(state with
            {
                Scout = selected with { SessionId = anchor, LastFailure = null }
            });
            run.AppendFlowScoutOutcome("completed", question);
            run.Log.Write("info", "scout", "scout.completed",
                ("vendor", _vendor.Id), ("model", selected.Model), ("effort", selected.Effort),
                ("fast", selected.Fast ? "true" : "false"),
                ("sessionMode", ActualSessionMode(resumeToken)), ("report", run.ScoutReportPath));

            return ToDigest(report);
        }
        catch (OperationCanceledException)
        {
            PersistCancellation(run, state, selected, suppliedToken, session, sessionMode, question);
            throw;
        }
        catch (Exception error)
        {
            var failure = FailureFor(error);
            PersistFailure(run, state, selected, suppliedToken, session, sessionMode, question, failure);
            throw new ScoutException(failure);
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

    internal static ScoutDigest ToDigest(ScoutReport report)
    {
        var clipped = false;

        var summary = Clip(report.Summary, DigestSummaryLength, ref clipped);
        var confirmedFacts = Clip(report.ConfirmedFacts, ref clipped);
        var materialAssumptions = Clip(report.MaterialAssumptions, ref clipped);
        var openDecisions = Clip(report.OpenDecisions, ref clipped);
        var likelyChangeSurface = Clip(report.LikelyChangeSurface, ref clipped);
        var verificationEvidence = Clip(report.VerificationEvidence, ref clipped);

        return new ScoutDigest(summary, confirmedFacts, materialAssumptions, openDecisions,
                               likelyChangeSurface, verificationEvidence, clipped);
    }

    private static IReadOnlyList<ScoutItem> Clip(IReadOnlyList<ScoutItem> items, ref bool clipped)
    {
        var count = Math.Min(items.Count, DigestItemsPerCategory);
        if (count != items.Count) clipped = true;

        var result = new ScoutItem[count];
        for (var index = 0; index < count; index++)
        {
            var item = items[index];
            var text = Clip(item.Text, DigestItemTextLength, ref clipped);
            var source = ClipSource(item.SourceKind, item.Source, ref clipped);
            result[index] = new ScoutItem { Text = text, SourceKind = item.SourceKind, Source = source };
        }

        return result;
    }

    private static string Clip(string text, int length, ref bool clipped)
    {
        if (text.Length <= length) return text;
        clipped = true;
        return text[..length];
    }

    private static string ClipSource(string kind, string source, ref bool clipped)
    {
        if (source.Length <= DigestSourceLength) return source;
        clipped = true;

        if (kind is "repository")
        {
            var separator = source.LastIndexOfAny([':', '#']);
            if (separator > 0)
            {
                var suffix = source[separator..];
                if (IsLocatorSuffix(suffix))
                {
                    var pathLength = DigestSourceLength - suffix.Length - 1;
                    return pathLength >= 0
                        ? source[..Math.Min(separator, pathLength)] + "…" + suffix
                        : "[truncated]";
                }
            }
        }

        return source[..(DigestSourceLength - 1)] + "…";
    }

    private static bool IsLocatorSuffix(string suffix) =>
        suffix.Length > 1 &&
        (suffix[0] == '#' ||
         (suffix[0] == ':' && suffix[1] is >= '1' and <= '9' && suffix[2..].All(char.IsDigit)));

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

    private static string Render(ScoutReport report)
    {
        var text = new StringBuilder()
            .AppendLine("# Scout report")
            .AppendLine()
            .AppendLine("## Summary")
            .AppendLine()
            .AppendLine(report.Summary)
            .AppendLine();

        Section(text, "Confirmed facts", report.ConfirmedFacts);
        Section(text, "Material assumptions", report.MaterialAssumptions);
        Section(text, "Open decisions", report.OpenDecisions);
        Section(text, "Likely change surface", report.LikelyChangeSurface);
        Section(text, "Verification evidence", report.VerificationEvidence);

        return text.ToString();

        static void Section(StringBuilder text, string heading, IReadOnlyList<ScoutItem> items)
        {
            text.Append("## ").AppendLine(heading).AppendLine();
            foreach (var item in items)
                text.Append("- ").Append(item.Text).Append(" — ").Append(item.SourceKind)
                    .Append(':').Append(' ').AppendLine(item.Source);
            text.AppendLine();
        }
    }

    private static ScoutFailure FailureFor(Exception error) =>
        error switch
        {
            ScoutSensitiveOutputException => ScoutFailures.SensitiveOutput,
            JsonException => ScoutFailures.InvalidOutput,
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

    private static void PersistCancellation(RunDirectory run,
                                            RunState? state,
                                            ScoutState? selected,
                                             string? suppliedToken,
                                             IVendorSession? session,
                                             string sessionMode,
                                             string question)
    {
        if (state is null || selected is null) return;

        try
        {
            var anchor = session is null ? sessionMode == "continue" ? suppliedToken : null
                                         : ResumeAnchor(session, suppliedToken, sessionMode);
            run.WriteState(state with { Scout = selected with { SessionId = anchor } });
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
                                ScoutFailure failure)
    {
        if (state is null || selected is null) return;

        var anchor = session is null ? sessionMode == "continue" ? suppliedToken : null
                                     : ResumeAnchor(session, suppliedToken, sessionMode);
        try
        {
            run.WriteState(state with
            {
                Scout = selected with { SessionId = anchor, LastFailure = failure }
            });
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
}
