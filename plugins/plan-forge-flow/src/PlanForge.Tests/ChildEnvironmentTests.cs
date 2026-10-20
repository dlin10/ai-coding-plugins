using PlanForge.Acts;
using PlanForge.Infrastructure;
using PlanForge.Mcp;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Isolates tests that temporarily change the server process environment from all other collections.</summary>
[CollectionDefinition("Child environment", DisableParallelization = true)]
public sealed class ChildEnvironmentCollection;

/// <summary>Advisor credentials stay in the server while ordinary child and gate settings keep working.</summary>
[Collection("Child environment")]
public sealed class ChildEnvironmentTests : IDisposable
{
    /// <summary>The server-only Advisor credential name.</summary>
    private const string KEY = "PLANFORGE_JEV_API_KEY";

    /// <summary>The non-secret pinned-model name.</summary>
    private const string MODEL = "PLANFORGE_JEV_MODEL";

    /// <summary>A distinctive test value whose presence would reveal a credential leak.</summary>
    private const string SECRET = "child-environment-secret-f63e8b";

    /// <summary>An ordinary variable used to check inherited and overlaid settings.</summary>
    private const string OTHER = "PLANFORGE_CHILD_ENVIRONMENT_PROBE";

    /// <summary>The plan refused before approval can save a credential.</summary>
    private const string PLAN = "# Plan\n\n## Approach\n\n1. Keep credentials in the server.\n";

    /// <summary>The isolated directory for test Runs and gate processes.</summary>
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-child-environment-" + Guid.NewGuid().ToString("n"));

    /// <summary>Creates the isolated workspace before any child is launched.</summary>
    public ChildEnvironmentTests() => Directory.CreateDirectory(_workspace);

    /// <summary>Removes the isolated workspace after all children have finished.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>An inherited Advisor credential is removed from the built environment.</summary>
    [Fact]
    public Task An_inherited_credential_is_absent() => WithEnvironment(KEY, SECRET, () =>
    {
        AssertNoCredential(StreamingProcess.Build(Spec()).Environment.Keys);
        return Task.CompletedTask;
    });

    /// <summary>An explicit overlay cannot pass the Advisor credential to a child.</summary>
    [Fact]
    public Task An_overlaid_credential_is_absent() => WithEnvironment(KEY, null, () =>
    {
        var info = StreamingProcess.Build(Spec(new Dictionary<string, string> { [KEY] = SECRET }));
        AssertNoCredential(info.Environment.Keys);
        return Task.CompletedTask;
    });

    /// <summary>Filtering compares actual environment keys without regard to case after the overlay.</summary>
    [Fact]
    public Task A_lower_case_overlaid_credential_is_absent() => WithEnvironment(KEY, SECRET, () =>
    {
        var info = StreamingProcess.Build(Spec(new Dictionary<string, string> { [KEY.ToLowerInvariant()] = SECRET }));
        AssertNoCredential(info.Environment.Keys);
        return Task.CompletedTask;
    });

    /// <summary>Removing the child credential leaves the server's inherited credential available.</summary>
    [Fact]
    public Task The_server_keeps_its_credential() => WithEnvironment(KEY, SECRET, () =>
    {
        StreamingProcess.Build(Spec());
        Assert.Equal(SECRET, Environment.GetEnvironmentVariable(KEY));
        return Task.CompletedTask;
    });

    /// <summary>Unrelated inherited variables still reach child start information.</summary>
    [Fact]
    public Task Other_inherited_variables_are_kept() => WithEnvironment(OTHER, "inherited", () =>
    {
        Assert.Equal("inherited", StreamingProcess.Build(Spec()).Environment[OTHER]);
        return Task.CompletedTask;
    });

    /// <summary>Unrelated overlays still replace inherited values.</summary>
    [Fact]
    public Task Other_overlaid_variables_are_kept() => WithEnvironment(OTHER, "inherited", () =>
    {
        var info = StreamingProcess.Build(Spec(new Dictionary<string, string> { [OTHER] = "overlaid" }));
        Assert.Equal("overlaid", info.Environment[OTHER]);
        return Task.CompletedTask;
    });

    /// <summary>The non-secret model is preserved both when inherited and when overlaid.</summary>
    [Fact]
    public Task The_Advisor_model_is_kept() => WithEnvironment(MODEL, "jev-1.13.0", () =>
    {
        Assert.Equal("jev-1.13.0", StreamingProcess.Build(Spec()).Environment[MODEL]);
        var info = StreamingProcess.Build(Spec(new Dictionary<string, string> { [MODEL] = "jev-1.14.0" }));
        Assert.Equal("jev-1.14.0", info.Environment[MODEL]);
        return Task.CompletedTask;
    });

    /// <summary>A real PowerShell child printing the variable receives no credential.</summary>
    [Fact]
    public Task A_real_child_does_not_see_the_credential() => WithEnvironment(KEY, SECRET, async () =>
    {
        var spec = Spec() with
        {
            Arguments = ["-NoProfile", "-Command", "Write-Output ('key=' + $env:PLANFORGE_JEV_API_KEY)"]
        };
        var output = await StreamingProcess.CollectAsync(spec, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(["key="], output);
    });

    /// <summary>A gate keeps its environment overlay while neither inheritance nor overlay supplies the key.</summary>
    [Fact]
    public Task A_gate_receives_its_environment_without_the_credential() => WithEnvironment(KEY, SECRET, async () =>
    {
        var environment = new Dictionary<string, string> { [OTHER] = "gate-value", [KEY.ToLowerInvariant()] = SECRET };
        var command = "Write-Output ('key=' + $env:PLANFORGE_JEV_API_KEY)\n"
                    + "Write-Output ('probe=' + $env:PLANFORGE_CHILD_ENVIRONMENT_PROBE)";
        var result = await GateRunner.RunAsync(new GateCommand("Gate", command), _workspace, environment,
                                                TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal("passed", result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["key=", "probe=gate-value"], result.Output!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    });

    /// <summary>Plan confirmation refuses a secret gate variable before saving its value anywhere.</summary>
    /// <param name="name">The casing of the forbidden gate variable supplied by the caller.</param>
    [Theory]
    [InlineData(KEY)]
    [InlineData("planforge_jev_api_key")]
    [InlineData("PlanForge_Jev_Api_Key")]
    public async Task Confirmation_refuses_secret_gate_names_without_leaking_the_value(string name)
    {
        var run = RunDirectory.Create(_workspace, "credential-refusal");
        run.WriteState(new RunState(run.RunId, _workspace, "Text", DateTimeOffset.Now, 0, 5));
        var statePath = Path.Combine(run.Path, "state.json");
        var before = AtomicFile.Read(statePath);

        var error = await Assert.ThrowsAsync<ArgumentRejectedException>(() =>
            ForgeTools.ConfirmPlan(SessionRoots.None, _workspace, run.RunId, true, CancellationToken.None,
                                     new Dictionary<string, string> { [name] = SECRET }, plan: PLAN));

        Assert.Contains(name, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SECRET, error.Message, StringComparison.Ordinal);
        Assert.Equal(before, AtomicFile.Read(statePath));
        Assert.DoesNotContain(SECRET, AtomicFile.Read(statePath), StringComparison.Ordinal);
        Assert.DoesNotContain(SECRET, AtomicFile.Read(run.DiagnosticLogPath), StringComparison.Ordinal);
        Assert.False(File.Exists(run.PlanPath));
    }

    /// <summary>Ordinary gate settings and the non-secret Advisor model remain accepted.</summary>
    [Fact]
    public void A_gate_environment_without_the_credential_is_accepted()
    {
        var environment = new Dictionary<string, string> { [OTHER] = "gate-value", [MODEL] = "jev-1.13.0" };

        var settings = GateSettings.Validate(environment, null);

        Assert.Equal("gate-value", settings.Environment![OTHER]);
        Assert.Equal("jev-1.13.0", settings.Environment[MODEL]);
    }

    /// <summary>Creates a redirected PowerShell launch specification for these environment tests.</summary>
    /// <param name="environment">The optional overlay applied after inheritance.</param>
    private ProcessSpec Spec(IReadOnlyDictionary<string, string>? environment = null) =>
        new("pwsh", [], _workspace, string.Empty, environment);

    /// <summary>Checks the environment's actual key names so differently cased duplicates cannot escape.</summary>
    /// <param name="names">The keys present in the built child environment.</param>
    private static void AssertNoCredential(IEnumerable<string> names) =>
        Assert.DoesNotContain(names, name => string.Equals(name, KEY, StringComparison.OrdinalIgnoreCase));

    /// <summary>Temporarily changes a real process variable and restores it even when the assertion fails.</summary>
    /// <param name="name">The variable to change for this test.</param>
    /// <param name="value">The temporary value, or null to unset it.</param>
    /// <param name="check">The test that consumes the temporary environment.</param>
    private static async Task WithEnvironment(string name, string? value, Func<Task> check)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            await check();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
