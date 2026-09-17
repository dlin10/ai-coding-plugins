using System.Text.Json;
using ConcurrencyHunter.Reporting;
using ConcurrencyHunter.Runs;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

public sealed class RunLifecycleTests
{
    [Fact]
    public async Task Start_returns_while_the_analysis_runs()
    {
        using var environment = new RunTestEnvironment();
        var started = NewSignal();
        var registry = environment.Registry(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });

        var start = registry.Start(environment.SolutionPath);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(start.RunId);
        Assert.Equal("running", registry.Poll(start.RunId).State);
        environment.Time.Advance(TimeSpan.FromMinutes(30));
        await registry.WaitForAnalysisAsync(start.RunId);
    }

    [Fact]
    public async Task Completed_analysis_awaits_narrative()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();

        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);
        var poll = registry.Poll(start.RunId!);

        Assert.Equal("narrative", poll.Phase);
        Assert.Equal("awaiting_narrative", poll.State);
        Assert.Equal(1, poll.Findings);
        Assert.Equal(1, poll.Groups);
    }

    [Fact]
    public async Task Load_failure_fails_the_run_and_renders_Failed()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry((_, _) => Task.FromResult(
            new RunAnalysis(true, false, 1, 0, ["Demo.csproj"], null, ["load failed"], 0.1, 0)));

        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);
        var poll = registry.Poll(start.RunId!);
        var render = registry.Render(start.RunId!);

        Assert.Equal("failed", poll.State);
        Assert.Equal(RunStatus.Failed, render.Status);
        Assert.Equal(["LoadFailed"], render.Reasons);
    }

    [Fact]
    public void Unresolvable_target_creates_a_failed_run()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var missing = Path.Combine(environment.Root, "missing.slnx");

        var start = registry.Start(missing);
        var poll = registry.Poll(start.RunId!);
        var render = registry.Render(start.RunId!);

        Assert.Equal("failed", poll.State);
        Assert.Equal(RunStatus.Failed, render.Status);
        Assert.True(File.Exists(render.ReportPath));
    }

    [Fact]
    public async Task Second_start_is_refused_while_a_run_is_active()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });

        var first = registry.Start(environment.SolutionPath);
        var second = registry.Start(environment.SolutionPath);

        Assert.Equal("runActive", second.Error);
        Assert.Equal(first.RunId, second.RunId);
        environment.Time.Advance(TimeSpan.FromMinutes(30));
        await registry.WaitForAnalysisAsync(first.RunId!);
    }

    [Fact]
    public async Task Second_start_is_allowed_after_render_or_past_the_deadline()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();

        var first = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(first.RunId!);
        Assert.Null(registry.Render(first.RunId!).Error);
        var second = registry.Start(environment.SolutionPath);
        Assert.Null(second.Error);
        environment.Time.Advance(TimeSpan.FromMinutes(30));
        var third = registry.Start(environment.SolutionPath);

        Assert.Null(third.Error);
        Assert.NotEqual(second.RunId, third.RunId);
    }

    [Fact]
    public async Task Get_groups_and_render_before_analysis_completes_are_not_ready()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });

        var start = registry.Start(environment.SolutionPath);

        Assert.Equal("notReady", registry.GetGroups(start.RunId!, null).Error);
        Assert.Equal("notReady", registry.Render(start.RunId!).Error);
        environment.Time.Advance(TimeSpan.FromMinutes(30));
        await registry.WaitForAnalysisAsync(start.RunId!);
    }

    [Fact]
    public async Task Accepted_narrative_makes_the_run_CompleteWithFindings()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);

        var submission = registry.Submit(start.RunId!, "G1", RunTestEnvironment.ValidNarrative);
        var render = registry.Render(start.RunId!);

        Assert.Equal("accepted", submission.Status);
        Assert.Equal(RunStatus.CompleteWithFindings, render.Status);
        Assert.Equal(1, render.NarrativesAccepted);
    }

    [Fact]
    public async Task Missing_narrative_makes_the_run_Incomplete()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);

        var render = registry.Render(start.RunId!);

        Assert.Equal(RunStatus.Incomplete, render.Status);
        Assert.Contains("NarrativeMissing:G1", render.Reasons);
    }

    [Fact]
    public async Task Rejected_narrative_gets_one_retry_then_retry_is_exhausted()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);

        var first = registry.Submit(start.RunId!, "G1", "");
        var second = registry.Submit(start.RunId!, "G1", "");
        var third = registry.Submit(start.RunId!, "G1", RunTestEnvironment.ValidNarrative);

        Assert.Equal(1, first.AttemptsRemaining);
        Assert.Equal(0, second.AttemptsRemaining);
        Assert.Equal(["retryExhausted"], third.Reasons);
    }

    [Fact]
    public async Task Accepted_target_refuses_another_submission()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);

        Assert.Equal("accepted", registry.Submit(
            start.RunId!, "G1", RunTestEnvironment.ValidNarrative).Status);
        var repeated = registry.Submit(start.RunId!, "G1", RunTestEnvironment.ValidNarrative);

        Assert.Equal(["alreadyAccepted"], repeated.Reasons);
        Assert.Equal(0, repeated.AttemptsRemaining);
    }

    [Fact]
    public async Task Deadline_turns_get_groups_into_deadlineExceeded()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);

        environment.Time.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal("deadlineExceeded", registry.GetGroups(start.RunId!, null).Error);
    }

    [Fact]
    public async Task Submission_past_the_deadline_is_late_and_not_applied()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);
        environment.Time.Advance(TimeSpan.FromMinutes(30));

        var submission = registry.Submit(start.RunId!, "G1", RunTestEnvironment.ValidNarrative);
        var render = registry.Render(start.RunId!);

        Assert.Equal("late", submission.Status);
        Assert.Equal(0, render.NarrativesAccepted);
        Assert.Equal(1, render.LateResponses);
    }

    [Fact]
    public async Task Deadline_during_analysis_cancels_it_and_renders_Incomplete_with_OverallTimeout()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        var start = registry.Start(environment.SolutionPath);

        environment.Time.Advance(TimeSpan.FromMinutes(30));
        await registry.WaitForAnalysisAsync(start.RunId!);
        var render = registry.Render(start.RunId!);

        Assert.Equal(RunStatus.Incomplete, render.Status);
        Assert.Contains("OverallTimeout", render.Reasons);
    }

    [Fact]
    public async Task Render_writes_three_files_under_repository_hash_and_run_id()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);

        var render = registry.Render(start.RunId!);
        var expected = Path.Combine(environment.BundleRoot, environment.RepositoryHash(), start.RunId!);

        Assert.Equal(expected, render.BundlePath);
        Assert.Equal(Path.Combine(expected, "report.md"), render.ReportPath);
        Assert.Equal(
            ["findings.json", "report.md", "run-metadata.json"],
            Directory.GetFiles(expected).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Directory.GetDirectories(Path.GetDirectoryName(expected)!),
            path => Path.GetFileName(path).Contains(".partial-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_metadata_carries_the_pair_counters()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);

        var render = registry.Render(start.RunId!);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(render.BundlePath!, "run-metadata.json")));
        var pairs = json.RootElement.GetProperty("pairs");

        Assert.Equal(["buckets", "candidates", "cartesianBound", "comparisons", "largestBucket", "skips", "suppressed"],
                     pairs.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var comparisons = pairs.GetProperty("comparisons").GetInt32();
        Assert.True(comparisons > 0);
        Assert.InRange(pairs.GetProperty("candidates").GetInt32(), 0, comparisons);
        Assert.InRange(comparisons, 0, pairs.GetProperty("cartesianBound").GetInt32());
        Assert.InRange(pairs.GetProperty("largestBucket").GetInt32(), 1, int.MaxValue);
        Assert.InRange(pairs.GetProperty("buckets").GetInt32(), 1, int.MaxValue);
        Assert.InRange(pairs.GetProperty("suppressed").GetInt32(), 0, comparisons);
        Assert.Equal(JsonValueKind.Object, pairs.GetProperty("skips").ValueKind);
    }

    [Fact]
    public async Task Second_render_returns_the_first_result()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);

        var first = registry.Render(start.RunId!);
        environment.Time.Advance(TimeSpan.FromMinutes(45));
        var second = registry.Render(start.RunId!);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task Deadline_cancels_analysis_without_any_tool_call()
    {
        using var environment = new RunTestEnvironment();
        var cancelled = NewSignal();
        var registry = environment.Registry(async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            throw new InvalidOperationException("unreachable");
        });
        var start = registry.Start(environment.SolutionPath);

        environment.Time.Advance(TimeSpan.FromMinutes(30));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await registry.WaitForAnalysisAsync(start.RunId!);

        Assert.True(registry.Poll(start.RunId!).DeadlineExceeded);
    }

    [Fact]
    public async Task Expired_unrendered_run_stays_renderable_and_records_late_responses_after_a_new_start()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var first = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(first.RunId!);
        environment.Time.Advance(TimeSpan.FromMinutes(30));

        var second = registry.Start(environment.SolutionPath);
        var late = registry.Submit(first.RunId!, "G1", RunTestEnvironment.ValidNarrative);
        var rendered = registry.Render(first.RunId!);

        Assert.NotNull(second.RunId);
        Assert.Equal("late", late.Status);
        Assert.Equal(1, rendered.LateResponses);
        Assert.NotNull(rendered.BundlePath);
    }

    [Fact]
    public async Task Analysis_exception_after_loading_renders_Failed_with_AnalysisFailed_and_coverage()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry((_, _) => Task.FromResult(
            new RunAnalysis(false, true, 4, 3, ["Missing.csproj"], null, ["analysis broke"], 1.1, 2.2)));
        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);

        var poll = registry.Poll(start.RunId!);
        var render = registry.Render(start.RunId!);

        Assert.Equal(4, poll.ProjectsExpected);
        Assert.Equal(3, poll.ProjectsLoaded);
        Assert.Equal(RunStatus.Failed, render.Status);
        Assert.Equal(["AnalysisFailed"], render.Reasons);
    }

    [Fact]
    public async Task Load_failure_past_the_deadline_still_renders_Failed()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry((_, _) => Task.FromResult(
            new RunAnalysis(true, false, 1, 0, ["Missing.csproj"], null, ["load broke"], 1, 0)));
        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);
        environment.Time.Advance(TimeSpan.FromMinutes(30));

        var render = registry.Render(start.RunId!);

        Assert.Equal(RunStatus.Failed, render.Status);
        Assert.Equal(["LoadFailed"], render.Reasons);
    }

    [Fact]
    public async Task Render_failure_after_the_files_are_written_leaves_no_partial_bundle_and_can_be_retried()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);
        var repositoryRoot = Path.Combine(environment.BundleRoot, environment.RepositoryHash());
        Directory.CreateDirectory(repositoryRoot);
        var finalPath = Path.Combine(repositoryRoot, start.RunId!);
        File.WriteAllText(finalPath, "blocks the final directory");

        var failed = registry.Render(start.RunId!);

        Assert.Equal("renderFailed", failed.Error);
        Assert.Empty(Directory.GetDirectories(repositoryRoot, "*.partial-*"));
        Assert.NotEqual("done", registry.Poll(start.RunId!).State);
        File.Delete(finalPath);
        var retried = registry.Render(start.RunId!);
        Assert.Null(retried.Error);
        Assert.True(Directory.Exists(retried.BundlePath));
    }

    [Fact]
    public async Task Rejection_with_many_reasons_returns_20_and_the_total()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();
        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);
        var citations = string.Join(" ", Enumerable.Range(1, 25).Select(number => $"[E:Unknown{number}]"));
        var text = $"""
            {citations}
            ## Remediation
            - Protect the field
              - Check: verify manually under load
            """;

        var submission = registry.Submit(start.RunId!, "G1", text);

        Assert.Equal(20, submission.Reasons.Count);
        Assert.Equal(26, submission.ReasonsTotal);
    }

    [Fact]
    public async Task Load_exception_message_is_kept_as_a_diagnostic()
    {
        using var environment = new RunTestEnvironment();
        File.WriteAllText(environment.SolutionPath, "this is not a solution");

        var analysis = await SolutionAnalysis.RunAsync(environment.SolutionPath, CancellationToken.None);

        Assert.True(analysis.LoadFailed);
        Assert.NotEmpty(analysis.Diagnostics);
        Assert.Contains("MsBuildLoadException:", analysis.Diagnostics[0]);
        Assert.Contains("Data at the root level", analysis.Diagnostics[0]);
    }

    [Fact]
    public async Task Loader_initialization_exception_is_a_load_failure_not_an_analysis_failure()
    {
        var analysis = await SolutionAnalysis.RunAsync(
            @"C:\fixture\Demo.slnx",
            CancellationToken.None,
            (_, _) => throw new InvalidOperationException("loader initialization failed"));

        Assert.True(analysis.LoadFailed);
        Assert.False(analysis.AnalysisFailed);
        Assert.Equal("InvalidOperationException: loader initialization failed", Assert.Single(analysis.Diagnostics));
    }

    [Fact]
    public void Target_path_over_1024_bytes_is_refused_before_a_run_starts()
    {
        using var environment = new RunTestEnvironment();
        var registry = environment.Registry();

        var refused = registry.Start(@"C:\" + new string('я', 200));
        var accepted = registry.Start(environment.SolutionPath);

        Assert.Equal("targetPathTooLong", refused.Error);
        Assert.Null(refused.RunId);
        Assert.NotNull(accepted.RunId);
    }

    [Fact]
    public async Task Bundle_root_over_960_bytes_fails_render_without_writing()
    {
        using var environment = new RunTestEnvironment();
        var longRoot = Path.Combine(environment.Root, new string('я', 170));
        var registry = environment.Registry(bundleRoot: longRoot);
        var start = registry.Start(environment.SolutionPath);
        await registry.WaitForAnalysisAsync(start.RunId!);

        var render = registry.Render(start.RunId!);

        Assert.True(ResponseBudget.Weight(longRoot) > 960);
        Assert.Equal("renderFailed", render.Error);
        Assert.False(Directory.Exists(longRoot));
    }

    [Fact]
    public void Candidate_file_name_passed_back_under_its_directory_starts_the_run()
    {
        using var environment = new RunTestEnvironment();
        var directory = Path.Combine(environment.Root, new string('я', 60));
        Directory.CreateDirectory(directory);
        var firstName = new string('а', 20) + ".slnx";
        var secondName = new string('б', 20) + ".slnx";
        File.WriteAllText(Path.Combine(directory, firstName), "<Solution />");
        File.WriteAllText(Path.Combine(directory, secondName), "<Solution />");
        var registry = environment.Registry();

        var ambiguous = registry.Start(directory);
        var started = registry.Start(Path.Combine(directory, ambiguous.Candidates[0]));

        Assert.Equal(2, ambiguous.CandidatesTotal);
        Assert.Equal([firstName, secondName], ambiguous.Candidates);
        Assert.All(ambiguous.Candidates, candidate => Assert.DoesNotContain('…', candidate));
        Assert.NotNull(started.RunId);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
