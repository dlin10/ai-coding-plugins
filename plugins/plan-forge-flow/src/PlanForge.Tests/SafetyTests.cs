using PlanForge.Prompts;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The two checks that survived the move to out-of-process workers: nothing secret leaves for a
/// vendor, and nothing is written outside the run folder.
/// </summary>
public sealed class SafetyTests
{
    [Theory]
    [InlineData(".env")]
    [InlineData("config/.env.production")]
    [InlineData("deploy/id_rsa")]
    [InlineData("certs/server.pem")]
    [InlineData("src/appsettings.Production.json")]
    [InlineData("infra/terraform.tfstate")]
    public void Names_a_sensitive_path(string path) => Assert.True(SensitiveInput.IsSensitivePath(path));

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("docs/adr/0002-mcp-server-surface-without-enforcement.md")]
    public void Leaves_an_ordinary_path_alone(string path) => Assert.False(SensitiveInput.IsSensitivePath(path));

    [Fact]
    public void Refuses_a_prompt_carrying_a_secret()
    {
        var diff = """
                   +const config = {
                   +  api_key: "sk-Lq83Hd0PzX7vNm41RbTuKcWy",
                   +};
                   """;

        var error = Assert.Throws<SensitiveContentException>(() => SensitiveInput.Guard(diff, "the diff under review"));
        Assert.Contains("the diff under review", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Passes_a_prompt_that_only_mentions_secrets()
    {
        SensitiveInput.Guard("Add a test that the token endpoint rejects an expired password.", "the plan");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("run/../../elsewhere")]
    public void Refuses_a_run_id_that_leaves_the_forge_folder(string runId)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "planforge-safety", Guid.NewGuid().ToString("n"));
        Assert.Throws<RunEscapedException>(() => RunDirectory.Open(workspace, runId));
    }

    [Fact]
    public void Appends_the_shared_roslyn_contract_to_a_critic_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "planforge-prompts", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(root, "claude"));
        File.WriteAllText(Path.Combine(root, "claude", "critic.md"), "judge");
        File.WriteAllText(Path.Combine(root, "claude", "builder.md"), "implement");
        File.WriteAllText(Path.Combine(root, "roslyn-contract.md"), "roslyn first");

        try
        {
            var prompts = new PromptLibrary(root);

            Assert.Contains("roslyn first", prompts.Load("claude", VendorRole.Critic), StringComparison.Ordinal);
            Assert.DoesNotContain("roslyn first", prompts.Load("claude", VendorRole.Builder), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
