using PlanForge.Acts;
using PlanForge.Prompts;
using PlanForge.Repo;
using PlanForge.Run;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using Xunit;
using Xunit.Abstractions;

namespace PlanForge.Tests;

/// <summary>
/// Drives a real vendor against a throwaway git repository: the builder edits files and the critic
/// judges the diff. Slow and paid for, so it is traited out of the default suite.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private const string Plan =
        """
        # Toy plan

        ## Approach

        1. In `math.js`, add and export a `subtract(a, b)` function that returns a minus b.
           Do not change the existing `add` function.
        """;

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "planforge-e2e", Guid.NewGuid().ToString("n"));
    private readonly ITestOutputHelper _output;
    private readonly GitClient _git;

    public EndToEndTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_repo);
        _git = new GitClient(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception error) when (error is DirectoryNotFoundException or UnauthorizedAccessException or IOException) { }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task A_run_builds_its_task_then_a_defect_is_caught_and_fixed()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = timeout.Token;
        var mathFile = Path.Combine(_repo, "math.js");

        await InitialCommitAsync(mathFile, ct);

        var run = RunDirectory.Create(_repo, "e2e");
        run.WritePlan(Plan);
        run.WriteState(new RunState("e2e", _repo, "Text", DateTimeOffset.Now,
            ReviewRounds: 0, ReviewRoundCap: 5, BaselineHead: "", Approved: true));

        var vendor = new ClaudeCliVendor(_repo);
        var prompts = new PromptLibrary(RepositoryPrompts());
        var selection = new Selection("sonnet", null);

        // --- Build: one task per call ---------------------------------------
        var outcome = await new Build(vendor, prompts).NextAsync(run, selection, ct);
        _output.WriteLine($"build: {outcome.Result?.Status} {outcome.TasksCompleted}/{outcome.TaskCount}");

        Assert.NotNull(outcome.Result);
        Assert.Equal(1, outcome.TasksCompleted);
        Assert.Contains("subtract", await File.ReadAllTextAsync(mathFile, ct), StringComparison.Ordinal);

        // --- Inject a defect the critic has to catch ------------------------
        var built = await File.ReadAllTextAsync(mathFile, ct);
        await File.WriteAllTextAsync(mathFile, built.Replace("a - b", "a + b", StringComparison.Ordinal), ct);
        Assert.DoesNotContain("a - b", await File.ReadAllTextAsync(mathFile, ct), StringComparison.Ordinal);

        // --- Code review: the whole loop, no orchestrator turn --------------
        var review = await new CodeReview(vendor, prompts, _git)
            .RunAsync(run, selection, selection, cap: 3, ct);

        _output.WriteLine($"review: verdict={review.Verdict?.Verdict} rounds={review.Rounds}");
        _output.WriteLine(await File.ReadAllTextAsync(mathFile, ct));

        Assert.NotNull(review.Verdict);
        Assert.True(review.Rounds >= 1, "the critic never ran");
        Assert.Contains("a - b", await File.ReadAllTextAsync(mathFile, ct), StringComparison.Ordinal);
    }

    private async Task InitialCommitAsync(string mathFile, CancellationToken ct)
    {
        await _git.OutputAsync(["init", "-q"], ct);
        await _git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await _git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        await File.WriteAllTextAsync(mathFile,
            """
            export function add(a, b) {
              return a + b;
            }
            """, ct);
        await _git.OutputAsync(["add", "math.js"], ct);
        await _git.OutputAsync(["commit", "-qm", "initial"], ct);
    }

    private static string RepositoryPrompts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var prompts = Path.Combine(directory.FullName, "prompts");
            if (Directory.Exists(prompts)) return prompts;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("could not locate the prompts folder above the test binary");
    }
}
