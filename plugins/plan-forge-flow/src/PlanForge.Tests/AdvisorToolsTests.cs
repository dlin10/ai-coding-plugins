using System.Text.Json;
using PlanForge.Infrastructure;
using PlanForge.Mcp;
using PlanForge.Orchestration;
using PlanForge.Repo;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Advisor discovery reaches both tool results without exposing credentials or persisting configuration.</summary>
public sealed class AdvisorToolsTests : IDisposable
{
    /// <summary>The server-only credential variable.</summary>
    private const string KEY = "PLANFORGE_JEV_API_KEY";

    /// <summary>The optional pinned-model variable.</summary>
    private const string MODEL = "PLANFORGE_JEV_MODEL";

    /// <summary>An isolated repository used only by this test instance.</summary>
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-advisor-tools-" + Guid.NewGuid().ToString("n"));

    /// <summary>Removes the isolated fixture after tool calls have finished.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>R4: begin and status carry the configured Jev id and its requested model.</summary>
    [Fact]
    public async Task Begin_and_status_carry_a_configured_Jev_with_its_model()
    {
        var environment = new Dictionary<string, string?> { [KEY] = "test-credential", [MODEL] = "jev-1.14.0" };
        var (begin, status, _) = await Results(environment);
        foreach (var json in new[] { begin, status })
        {
            using var result = JsonDocument.Parse(json);
            var advisors = result.RootElement.GetProperty("advisors");
            var configured = Assert.Single(advisors.GetProperty("configured").EnumerateArray());
            Assert.Equal("jev", configured.GetProperty("id").GetString());
            Assert.Equal("jev-1.14.0", configured.GetProperty("model").GetString());
            Assert.Empty(advisors.GetProperty("partlyConfigured").EnumerateArray());
            Assert.Equal("advisors", result.RootElement.EnumerateObject().Last().Name);
        }
    }

    /// <summary>R4: begin and status carry Microsoft-Decision-1 with its deployment as the model, and never its credential.</summary>
    [Fact]
    public async Task Begin_and_status_carry_a_configured_Microsoft_Decision_with_its_deployment()
    {
        const string secret = "distinctive-msdecision-credential-27ad0e";
        var (begin, status, run) = await Results(new()
        {
            ["PLANFORGE_MSDECISION_ENDPOINT"] = "https://resource.services.ai.azure.invalid",
            ["PLANFORGE_MSDECISION_API_KEY"] = secret,
            ["PLANFORGE_MSDECISION_DEPLOYMENT"] = "Decision-1"
        });
        foreach (var json in new[] { begin, status })
        {
            using var result = JsonDocument.Parse(json);
            var configured = Assert.Single(result.RootElement.GetProperty("advisors").GetProperty("configured").EnumerateArray());
            Assert.Equal("microsoft-decision", configured.GetProperty("id").GetString());
            Assert.Equal("Decision-1", configured.GetProperty("model").GetString());
            Assert.DoesNotContain(secret, json);
        }
        Assert.DoesNotContain(secret, AtomicFile.Read(run.DiagnosticLogPath));
        Assert.DoesNotContain(secret, AtomicFile.Read(Path.Combine(run.Path, "state.json")));
    }

    /// <summary>R4: a model without a credential is partly configured, reporting only the missing name.</summary>
    [Fact]
    public async Task Begin_and_status_carry_a_partly_configured_Jev_with_its_problem()
    {
        var (begin, status, _) = await Results(new() { [MODEL] = "jev-1.14.0" });
        foreach (var json in new[] { begin, status })
        {
            using var result = JsonDocument.Parse(json);
            var advisors = result.RootElement.GetProperty("advisors");
            Assert.Empty(advisors.GetProperty("configured").EnumerateArray());
            var partial = Assert.Single(advisors.GetProperty("partlyConfigured").EnumerateArray());
            Assert.Equal("jev", partial.GetProperty("id").GetString());
            var problem = Assert.Single(partial.GetProperty("problems").EnumerateArray());
            Assert.Equal(KEY, problem.GetProperty("variable").GetString());
            Assert.Equal("missing", problem.GetProperty("problem").GetString());
            Assert.Equal(["variable", "problem"], problem.EnumerateObject().Select(property => property.Name));
        }
    }

    /// <summary>R4: an empty process configuration reports two empty lists, regardless of vendor credentials.</summary>
    [Fact]
    public async Task Begin_and_status_carry_empty_advisors_when_nothing_is_set()
    {
        var (begin, status, _) = await Results(new() { ["TYPESAFE_API_KEY"] = "vendor-secret", ["OPENAI_API_KEY"] = "other-secret" });
        foreach (var json in new[] { begin, status })
        {
            using var result = JsonDocument.Parse(json);
            var advisors = result.RootElement.GetProperty("advisors");
            Assert.Equal(["configured", "partlyConfigured"], advisors.EnumerateObject().Select(property => property.Name));
            Assert.Empty(advisors.GetProperty("configured").EnumerateArray());
            Assert.Empty(advisors.GetProperty("partlyConfigured").EnumerateArray());
        }
    }

    /// <summary>R5: a distinctive credential never appears in either result, the diagnostic log or saved state.</summary>
    [Fact]
    public async Task The_credential_never_appears_in_results_or_forge_log()
    {
        const string secret = "distinctive-advisor-credential-9c18f4";
        var (begin, status, run) = await Results(new() { [KEY] = secret });
        Assert.DoesNotContain(secret, begin);
        Assert.DoesNotContain(secret, status);
        Assert.DoesNotContain(secret, AtomicFile.Read(run.DiagnosticLogPath));
        Assert.DoesNotContain(secret, AtomicFile.Read(Path.Combine(run.Path, "state.json")));
    }

    /// <summary>R4: status discovers the environment at its own call, rather than reusing begin's discovery.</summary>
    [Fact]
    public async Task Status_reads_configuration_again_after_begin()
    {
        var environment = new Dictionary<string, string?> { [KEY] = "test-credential" };
        var (_, _, run) = await Results(environment);
        environment[MODEL] = "jev-preview";
        using (var result = JsonDocument.Parse(await ForgeTools.Status(SessionRoots.None, _workspace, run.RunId,
                                                                      CancellationToken.None, environment.GetValueOrDefault)))
        {
            var advisors = result.RootElement.GetProperty("advisors");
            Assert.Empty(advisors.GetProperty("configured").EnumerateArray());
            var partial = Assert.Single(advisors.GetProperty("partlyConfigured").EnumerateArray());
            var problem = Assert.Single(partial.GetProperty("problems").EnumerateArray());
            Assert.Equal(MODEL, problem.GetProperty("variable").GetString());
            Assert.Equal("invalid", problem.GetProperty("problem").GetString());
        }

        environment.Clear();
        using var absent = JsonDocument.Parse(await ForgeTools.Status(SessionRoots.None, _workspace, run.RunId,
                                                                     CancellationToken.None, environment.GetValueOrDefault));
        Assert.Empty(absent.RootElement.GetProperty("advisors").GetProperty("configured").EnumerateArray());
        Assert.Empty(absent.RootElement.GetProperty("advisors").GetProperty("partlyConfigured").EnumerateArray());
    }

    /// <summary>Begins an isolated Run without vendor probes and reads its status with the same test environment.</summary>
    /// <param name="environment">The test's process-environment substitute, never a tool argument.</param>
    private async Task<(string Begin, string Status, RunDirectory Run)> Results(Dictionary<string, string?> environment)
    {
        Directory.CreateDirectory(_workspace);
        var ct = CancellationToken.None;
        var git = new GitClient(_workspace);
        await git.OutputAsync(["init", "-q"], ct);
        await git.OutputAsync(["config", "user.email", "tests@example.invalid"], ct);
        await git.OutputAsync(["config", "user.name", "PlanForge Tests"], ct);
        AtomicFile.Write(Path.Combine(_workspace, "tracked.txt"), "original\n");
        await git.OutputAsync(["add", "tracked.txt"], ct);
        await git.OutputAsync(["commit", "-qm", "initial"], ct);

        var begin = await ForgeTools.Begin(SessionRoots.None, "advisor-tests", CapabilityProfile.Text, _ => { },
                                           _workspace, ct, environment: environment.GetValueOrDefault);
        using var result = JsonDocument.Parse(begin);
        var runId = result.RootElement.GetProperty("runId").GetString()!;
        var status = await ForgeTools.Status(SessionRoots.None, _workspace, runId, ct, environment.GetValueOrDefault);
        return (begin, status, RunDirectory.Open(_workspace, runId));
    }
}
