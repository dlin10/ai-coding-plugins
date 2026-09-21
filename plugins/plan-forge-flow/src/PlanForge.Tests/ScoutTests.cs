using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Acts;
using PlanForge.Diagnostics;
using PlanForge.Jobs;
using PlanForge.Mcp;
using PlanForge.Infrastructure;
using PlanForge.Prompts;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class ScoutTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-scout-" + Guid.NewGuid().ToString("N"));
    private readonly PromptLibrary _prompts;

    public ScoutTests()
    {
        Directory.CreateDirectory(_workspace);
        _prompts = new PromptLibrary(RepositoryPrompts());
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Digest_clips_the_summary_items_and_item_text_and_sets_the_flag()
    {
        var baseReport = ValidReport(new string('s', Scout.DigestSummaryLength + 1));
        var report = new ScoutReport
        {
            Summary = baseReport.Summary,
            ConfirmedFacts = Enumerable.Range(0, Scout.DigestItemsPerCategory + 1)
                .Select(index => Item($"fact-{index}" + new string('x', Scout.DigestItemTextLength + 1), "src/a.cs:1"))
                .ToArray(),
            MaterialAssumptions = baseReport.MaterialAssumptions,
            OpenDecisions = baseReport.OpenDecisions,
            LikelyChangeSurface = baseReport.LikelyChangeSurface,
            VerificationEvidence = baseReport.VerificationEvidence
        };

        var digest = Scout.ToDigest(report);

        Assert.True(digest.Truncated);
        Assert.Equal(Scout.DigestSummaryLength, digest.Summary.Length);
        Assert.Equal(Scout.DigestItemsPerCategory, digest.ConfirmedFacts.Count);
        Assert.Equal(Scout.DigestItemTextLength, digest.ConfirmedFacts[0].Text.Length);
        Assert.All(new[] { digest.ConfirmedFacts, digest.MaterialAssumptions, digest.OpenDecisions,
                           digest.LikelyChangeSurface, digest.VerificationEvidence }, category =>
            Assert.All(category, item => Assert.True(item.Source.Length <= Scout.DigestSourceLength)));
    }

    [Fact]
    public void Digest_clipping_preserves_a_multi_digit_repository_line_suffix()
    {
        var source = new string('a', Scout.DigestSourceLength + 20) + ":42";
        var report = new ScoutReport
        {
            Summary = "summary",
            ConfirmedFacts = [Item("fact", source)],
            MaterialAssumptions = [],
            OpenDecisions = [],
            LikelyChangeSurface = [],
            VerificationEvidence = []
        };

        var clipped = Assert.Single(Scout.ToDigest(report).ConfirmedFacts).Source;

        Assert.True(clipped.Length <= Scout.DigestSourceLength);
        Assert.EndsWith(":42", clipped, StringComparison.Ordinal);
    }

    [Fact]
    public void Scout_schema_has_exactly_five_arrays_and_rejects_additional_properties()
    {
        using var schema = JsonDocument.Parse(Schemas.ScoutReport.Json);
        var properties = schema.RootElement.GetProperty("properties");

        Assert.Equal(6, properties.EnumerateObject().Count());
        Assert.Equal("false", schema.RootElement.GetProperty("additionalProperties").GetRawText());
        Assert.Equal("false", schema.RootElement.GetProperty("$defs").GetProperty("scoutItem")
            .GetProperty("additionalProperties").GetRawText());
        Assert.Equal(5, properties.EnumerateObject().Count(property => property.Value.GetProperty("type").GetString() == "array"));
    }

    [Fact]
    public void A_report_carries_all_five_sourced_categories()
    {
        var report = ValidReport("summary");

        Assert.Equal("summary", report.Summary);
        Assert.All(new[] { report.ConfirmedFacts, report.MaterialAssumptions, report.OpenDecisions,
                           report.LikelyChangeSurface, report.VerificationEvidence }, category =>
            Assert.All(category, item => Assert.False(string.IsNullOrWhiteSpace(item.Source))));
    }

    [Fact]
    public void A_repository_line_locator_is_valid()
    {
        var item = DeserializeItem("src/PlanForge/Acts/Scout.cs:42");

        Assert.Equal("repository", item.SourceKind);
    }

    [Fact]
    public void A_repository_symbol_locator_is_valid()
    {
        var item = DeserializeItem("src/PlanForge/Acts/Scout.cs#Scout.RunAsync");

        Assert.Equal("src/PlanForge/Acts/Scout.cs#Scout.RunAsync", item.Source);
    }

    [Fact]
    public void An_absolute_external_locator_is_valid()
    {
        var item = DeserializeItem("https://example.com/reference");

        Assert.Equal("external", item.SourceKind);
    }

    [Theory]
    [InlineData("src/file.cs:0")]
    [InlineData("src/file.cs:")]
    [InlineData("src/file.cs#")]
    [InlineData(":42")]
    public void Malformed_repository_locators_are_rejected(string source)
    {
        Assert.False(SchemaInPrompt.TryExtract(ItemJson("repository", source),
                                               new VendorSchema<ScoutItem>(ItemSchema(), ContractJson.Default.ScoutItem),
                                               out _, out _));
    }

    [Theory]
    [InlineData("www.example.com/reference")]
    [InlineData("ftp://example.com/reference")]
    [InlineData("https://")]
    public void Malformed_external_locators_are_rejected(string source)
    {
        Assert.False(SchemaInPrompt.TryExtract(ItemJson("external", source),
                                               new VendorSchema<ScoutItem>(ItemSchema(), ContractJson.Default.ScoutItem),
                                               out _, out _));
    }

    [Fact]
    public void A_missing_category_is_a_structural_failure()
    {
        var json = JsonNode.Parse(ReportJson(ValidReport("summary")))!.AsObject();
        json.Remove("openDecisions");

        Assert.False(SchemaInPrompt.TryExtract(json.ToJsonString(), Schemas.ScoutReport, out _, out _));
    }

    [Fact]
    public void An_explicit_null_category_is_a_structural_failure()
    {
        var json = JsonNode.Parse(ReportJson(ValidReport("summary")))!.AsObject();
        json["openDecisions"] = null;

        Assert.False(SchemaInPrompt.TryExtract(json.ToJsonString(), Schemas.ScoutReport, out _, out _));
    }

    [Fact]
    public void An_explicit_null_item_member_is_a_structural_failure()
    {
        var json = JsonNode.Parse(ReportJson(ValidReport("summary")))!.AsObject();
        json["confirmedFacts"]![0]!["source"] = null;

        Assert.False(SchemaInPrompt.TryExtract(json.ToJsonString(), Schemas.ScoutReport, out _, out _));
    }

    [Fact]
    public async Task Scout_prompt_contains_only_the_question_and_repository_location()
    {
        var run = NewRun();
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(ValidReport("summary"), "new-session");

        await new Scout(vendor, _prompts).RunAsync(run, "Which entry points exist?", "fresh", CancellationToken.None);

        var session = Assert.Single(vendor.Sessions);
        Assert.Contains("Which entry points exist?", session.PromptText, StringComparison.Ordinal);
        Assert.Contains(_workspace, session.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAN.md", session.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("review-log", session.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("SCOUT.md", session.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("old selection", session.PromptText, StringComparison.Ordinal);
        Assert.Contains("Question: Which entry points exist?", File.ReadAllText(run.FlowLogPath),
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blank_question_is_rejected_before_starting_a_vendor()
    {
        var run = NewRun();
        var vendor = new RecordingVendor("codex");

        await Assert.ThrowsAsync<ArgumentRejectedException>(() =>
            ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, " ", "fresh",
                                CancellationToken.None, _ => vendor, _prompts));

        Assert.Empty(vendor.Sessions);
    }

    [Fact]
    public async Task An_overlong_question_is_rejected_before_starting_a_vendor()
    {
        var run = NewRun();
        var vendor = new RecordingVendor("codex");

        await Assert.ThrowsAsync<ArgumentRejectedException>(() =>
            ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId,
                                new string('q', Scout.MaxQuestionLength + 1), "fresh",
                                CancellationToken.None, _ => vendor, _prompts));

        Assert.Empty(vendor.Sessions);
    }

    [Fact]
    public async Task The_persisted_vendor_model_and_effort_are_reused_exactly()
    {
        var run = NewRun(new ScoutState(true, "codex", " model exactly ", " effort exactly "));
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(ValidReport("summary"), "session-1");

        await ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "bounded question", "fresh",
                                  CancellationToken.None, _ => vendor, _prompts);

        var session = Assert.Single(vendor.Sessions);
        Assert.Equal(" model exactly ", session.Selection.Model);
        Assert.Equal(" effort exactly ", session.Selection.Effort);
        Assert.Equal("session-1", run.ReadState().Scout!.SessionId);
    }

    [Fact]
    public async Task A_later_continue_call_receives_the_persisted_resume_token()
    {
        var run = NewRun(new ScoutState(true, "codex", "model", "low"));
        var first = new RecordingVendor("codex");
        first.Enqueue(ValidReport("first"), "session-1");
        await ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "first question", "fresh",
                                  CancellationToken.None, _ => first, _prompts);

        var second = new RecordingVendor("codex");
        second.Enqueue(ValidReport("second"), "session-2");
        await ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "second question", "continue",
                                  CancellationToken.None, _ => second, _prompts);

        Assert.Equal("session-1", Assert.Single(second.Sessions).StartedWithResumeToken);
        Assert.Equal("session-2", run.ReadState().Scout!.SessionId);
    }

    [Fact]
    public async Task Fresh_mode_clears_the_old_anchor_before_a_failed_vendor_call()
    {
        var run = NewRun(new ScoutState(true, "codex", "model", null, "old-session"));
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new VendorException("malicious stderr", 1), "malicious token");

        await Assert.ThrowsAsync<ScoutException>(() =>
            ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "fresh question", "fresh",
                                CancellationToken.None, _ => vendor, _prompts));

        Assert.Null(Assert.Single(vendor.Sessions).StartedWithResumeToken);
        Assert.Null(run.ReadState().Scout!.SessionId);
        Assert.Equal("vendor_failed", run.ReadState().Scout!.LastFailure!.Code);
    }

    [Fact]
    public async Task The_latest_report_replaces_the_previous_report_atomically()
    {
        var run = NewRun();
        var first = new RecordingVendor("codex");
        first.Enqueue(ValidReport("first report"), "one");
        await ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "first", "fresh",
                                  CancellationToken.None, _ => first, _prompts);
        var old = File.ReadAllText(run.ScoutReportPath);

        var second = new RecordingVendor("codex");
        second.Enqueue(ValidReport("second report"), "two");
        await ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "second", "continue",
                                  CancellationToken.None, _ => second, _prompts);

        var current = File.ReadAllText(run.ScoutReportPath);
        Assert.Contains("second report", current, StringComparison.Ordinal);
        Assert.DoesNotContain("first report", current, StringComparison.Ordinal);
        Assert.NotEqual(old, current);
    }

    [Fact]
    public async Task A_successful_call_clears_the_previous_failure()
    {
        var run = NewRun(new ScoutState(true, "codex", "model", null, null,
                                         new ScoutFailure("vendor_failed", "fixed failure")));
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(ValidReport("success"), "new-session");

        await ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "recover", "continue",
                                  CancellationToken.None, _ => vendor, _prompts);

        Assert.Null(run.ReadState().Scout!.LastFailure);
    }

    [Fact]
    public async Task A_sensitive_question_keeps_state_and_does_not_start_a_vendor()
    {
        var run = NewRun();
        var vendor = new RecordingVendor("codex");
        const string question = "token = sk-Lq83Hd0PzX7vNm41RbTuKcWy";

        await Assert.ThrowsAsync<SensitiveContentException>(() =>
            ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, question, "fresh",
                                CancellationToken.None, _ => vendor, _prompts));

        Assert.Empty(vendor.Sessions);
        Assert.Null(run.ReadState().Scout!.LastFailure);
    }

    [Fact]
    public async Task A_sensitive_report_preserves_the_previous_report_with_the_exact_safe_explanation()
    {
        var run = NewRun();
        var first = new RecordingVendor("codex");
        first.Enqueue(ValidReport("safe prior"), "one");
        await ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "first", "fresh",
                                  CancellationToken.None, _ => first, _prompts);
        var previous = File.ReadAllText(run.ScoutReportPath);

        var sensitive = new RecordingVendor("codex");
        sensitive.Enqueue(ValidReport("password=sk-Lq83Hd0PzX7vNm41RbTuKcWy"), "two");
        var error = await Assert.ThrowsAsync<ScoutException>(() =>
            ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "second", "continue",
                                CancellationToken.None, _ => sensitive, _prompts));

        Assert.Equal("sensitive_output: Scout response contained sensitive content, was not persisted, and the previous report was preserved.", error.Message);
        Assert.Equal(previous, File.ReadAllText(run.ScoutReportPath));
        Assert.Equal("sensitive_output", run.ReadState().Scout!.LastFailure!.Code);
    }

    [Fact]
    public async Task A_vendor_failure_has_only_the_fixed_safe_failure_in_direct_logs_and_state()
    {
        var run = NewRun();
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new VendorException("MALICIOUS STDERR and stack", 9), "MALICIOUS TOKEN");

        var error = await Assert.ThrowsAsync<ScoutException>(() =>
            ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "failure", "fresh",
                                CancellationToken.None, _ => vendor, _prompts));

        var log = AtomicFile.Read(run.DiagnosticLogPath);
        Assert.DoesNotContain("MALICIOUS", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("MALICIOUS", log, StringComparison.Ordinal);
        Assert.Equal("vendor_failed", run.ReadState().Scout!.LastFailure!.Code);
        Assert.DoesNotContain("MALICIOUS", JsonSerializer.Serialize(run.ReadState(), ForgeJson.Default.RunState), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_cancellation_is_reported_as_cancellation_without_a_failure()
    {
        var run = NewRun();
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new OperationCanceledException(), "after-cancel");

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "cancel", "fresh",
                                CancellationToken.None, _ => vendor, _prompts));

        Assert.Null(run.ReadState().Scout!.LastFailure);
        Assert.Contains("scout.cancelled", AtomicFile.Read(run.DiagnosticLogPath), StringComparison.Ordinal);
        var flowLog = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Scout cancelled", flowLog, StringComparison.Ordinal);
        Assert.Contains("Question: cancel", flowLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Background_cancellation_is_reported_without_creating_a_scout_failure()
    {
        var run = NewRun();
        var registry = new JobRegistry();
        var vendor = new BlockingScoutVendor();
        var start = JsonNode.Parse(await ForgeTools.StartWork(registry, SessionRoots.None, _workspace, run.RunId,
            "scout", null, null, null, null, null, null, null, false, "cancel", "fresh",
            CancellationToken.None, _ => vendor, _prompts))!;
        var jobId = start["jobId"]!.GetValue<string>();

        await ForgeTools.CancelWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None);
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None);
        var fetch = JsonNode.Parse(await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId,
                                                              jobId, CancellationToken.None))!;

        Assert.Equal("failed", fetch["state"]!.GetValue<string>());
        Assert.Equal("job was cancelled", fetch["error"]!.GetValue<string>());
        Assert.Null(run.ReadState().Scout!.LastFailure);
        Assert.Contains("scout.cancelled", AtomicFile.Read(run.DiagnosticLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cursor_invalid_output_is_corrected_on_its_single_retry()
    {
        var run = NewRun();
        var vendor = new RetryingScoutVendor(ValidReport("corrected"), failTwice: false);

        var json = await ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "retry", "fresh",
                                              CancellationToken.None, _ => vendor, _prompts);

        Assert.Contains("corrected", json, StringComparison.Ordinal);
        Assert.Equal(2, vendor.Session.Attempts);
    }

    [Fact]
    public async Task Two_structurally_invalid_attempts_fail_after_exactly_one_retry()
    {
        var run = NewRun();
        var vendor = new RetryingScoutVendor(ValidReport("unused"), failTwice: true);

        var error = await Assert.ThrowsAsync<ScoutException>(() =>
            ForgeTools.ScoutRun(SessionRoots.None, _workspace, run.RunId, "retry twice", "fresh",
                                CancellationToken.None, _ => vendor, _prompts));

        Assert.Equal(2, vendor.Session.Attempts);
        Assert.Equal("invalid_output", run.ReadState().Scout!.LastFailure!.Code);
        Assert.Contains("invalid_output", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_background_job_exposes_only_the_safe_failure_text()
    {
        var run = NewRun();
        var registry = new JobRegistry();
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(new VendorException("malicious stderr and stack", 1), "bad token");

        var start = JsonNode.Parse(await ForgeTools.StartWork(registry, SessionRoots.None, _workspace, run.RunId,
            "scout", null, null, null, null, null, null, null, false, "failure", "fresh",
            CancellationToken.None, _ => vendor, _prompts))!;
        var jobId = start["jobId"]!.GetValue<string>();
        await ForgeTools.PollWork(registry, SessionRoots.None, _workspace, run.RunId, jobId, CancellationToken.None);
        var fetch = JsonNode.Parse(await ForgeTools.FetchWork(registry, SessionRoots.None, _workspace, run.RunId,
                                                              jobId, CancellationToken.None))!;

        Assert.DoesNotContain("malicious", fetch.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vendor_failed", fetch["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("malicious", AtomicFile.Read(run.DiagnosticLogPath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("malicious", File.ReadAllText(run.FlowLogPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_successful_result_exposes_the_latest_report_document_metadata()
    {
        var run = NewRun();
        var vendor = new RecordingVendor("codex");
        vendor.Enqueue(ValidReport("document"), "session");

        using var result = JsonDocument.Parse(await ForgeTools.ScoutRun(
            SessionRoots.None, _workspace, run.RunId, "document", "fresh", CancellationToken.None, _ => vendor, _prompts));

        Assert.Equal(run.ScoutReportPath, result.RootElement.GetProperty("documents")
            .GetProperty("scout").GetProperty("path").GetString());
        Assert.Contains("document", File.ReadAllText(run.ScoutReportPath), StringComparison.Ordinal);
    }

    private RunDirectory NewRun(ScoutState? scout = null)
    {
        var runId = Guid.NewGuid().ToString("N");
        var run = RunDirectory.Create(_workspace, runId);
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now, 0, 5,
                                    Scout: scout ?? new ScoutState(true, "codex", "model", "low")));
        return run;
    }

    private static ScoutReport ValidReport(string summary) => new()
    {
        Summary = summary,
        ConfirmedFacts = [Item("fact", "src/PlanForge/Acts/Scout.cs:42")],
        MaterialAssumptions = [Item("assumption", "src/PlanForge/Acts/Scout.cs#Scout.RunAsync")],
        OpenDecisions = [Item("decision", "https://example.com/reference", "external")],
        LikelyChangeSurface = [Item("surface", "src/PlanForge/Run/RunDirectory.cs:1")],
        VerificationEvidence = [Item("verification", "src/PlanForge/Vendors/Contracts.cs#ScoutReport")]
    };

    private static ScoutItem Item(string text, string source, string kind = "repository") =>
        new() { Text = text, SourceKind = kind, Source = source };

    private static ScoutItem DeserializeItem(string source) =>
        JsonSerializer.Deserialize(ItemJson(source.StartsWith("http", StringComparison.Ordinal) ? "external" : "repository", source),
                                  ContractJson.Default.ScoutItem)!;

    private static string ItemJson(string kind, string source) =>
        $"{{\"text\":\"evidence\",\"sourceKind\":\"{kind}\",\"source\":\"{source}\"}}";

    private static string ReportJson(ScoutReport report) =>
        JsonSerializer.Serialize(report, ContractJson.Default.ScoutReport);

    private static string ItemSchema() =>
        "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"},\"sourceKind\":{\"type\":\"string\"},\"source\":{\"type\":\"string\"}},\"required\":[\"text\",\"sourceKind\",\"source\"],\"additionalProperties\":false}";

    private static string RepositoryPrompts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var prompts = Path.Combine(directory.FullName, "prompts");
            if (Directory.Exists(prompts) && File.Exists(Path.Combine(prompts, "scout-contract.md"))) return prompts;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("could not locate the prompts folder above the test binary");
    }

    private sealed class RetryingScoutVendor(ScoutReport report, bool failTwice) : IVendor
    {
        public string Id => "cursor";
        public VendorCatalog Catalog { get; } = new([], CatalogSource.Live);
        public RetryingScoutSession Session { get; } = new(report, failTwice);

        public Task<VendorReadiness> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new VendorReadiness(true, "test"));

        public Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken, CancellationToken ct)
        {
            Session.StartedWithResumeToken = resumeToken;
            return Task.FromResult<IVendorSession>(Session);
        }
    }

    private sealed class RetryingScoutSession(ScoutReport report, bool failTwice) : IVendorSession
    {
        public int Attempts { get; private set; }
        public string? StartedWithResumeToken { get; set; }
        public IAsyncEnumerable<VendorEvent> Events => Empty();
        public bool CanResume => true;
        public string? ResumeToken => "retry-session";

        public Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct)
        {
            Attempts++;
            var json = Attempts == 1 || failTwice ? "{\"summary\":null}" : ReportJson(report);
            if (SchemaInPrompt.TryExtract(json, schema, out T value, out _))
                return Task.FromResult(value);
            if (Attempts == 1) return RunAsync<T>(prompt, schema, ct);
            throw new JsonException("structural Scout output failed twice");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<VendorEvent> Empty()
        {
            yield break;
        }
    }

    private sealed class BlockingScoutVendor : IVendor
    {
        public string Id => "codex";
        public VendorCatalog Catalog { get; } = new([], CatalogSource.Live);
        public Task<VendorReadiness> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new VendorReadiness(true, "test"));
        public Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken, CancellationToken ct) =>
            Task.FromResult<IVendorSession>(new BlockingScoutSession());
    }

    private sealed class BlockingScoutSession : IVendorSession
    {
        public IAsyncEnumerable<VendorEvent> Events => Empty();
        public bool CanResume => true;
        public string? ResumeToken => "cancel-session";
        public async Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return default!;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static async IAsyncEnumerable<VendorEvent> Empty() { yield break; }
    }
}
