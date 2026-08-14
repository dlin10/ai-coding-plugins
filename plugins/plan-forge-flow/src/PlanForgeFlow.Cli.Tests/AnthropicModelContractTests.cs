using System.Text.Json;
using PlanForgeFlow.Claude;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;
using Xunit;
using ForgeWorkflow = PlanForgeFlow.Workflow.ForgeWorkflow;

namespace PlanForgeFlow.Tests;

public sealed class AnthropicModelContractTests
{
    public static IEnumerable<object[]> ModelEvidenceCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "anthropic-model-evidence-cases.json")));
        foreach (var item in document.RootElement.EnumerateArray())
        {
            yield return
            [
                item.GetProperty("alias").GetString()!,
                item.GetProperty("model").GetString()!,
                item.GetProperty("valid").GetBoolean(),
            ];
        }
    }

    public static TheoryData<string, string, string?> ValidSelections => new()
    {
        { "haiku", "none", "haiku" },
        { "sonnet", "low", "sonnet" },
        { "sonnet", "medium", "sonnet" },
        { "opus", "high", "opus" },
        { "fable", "xhigh", "fable" },
        { "fable", "max", "fable" },
        { "inherit", "none", null },
        { "inherit", "low", null },
        { "inherit", "medium", null },
        { "inherit", "high", null },
        { "inherit", "xhigh", null },
        { "inherit", "max", null },
    };

    [Theory]
    [MemberData(nameof(ValidSelections))]
    public void SelectionUsesExactAliasEffortMatrixAndOmitsInheritedAgentModel(string alias, string effort, string? agentModel)
    {
        var selection = AnthropicModels.Select(alias, effort);

        Assert.Equal(alias, selection.RequestedAlias);
        Assert.Equal(agentModel, selection.AgentModel);
        Assert.Equal(effort, selection.Effort);
    }

    [Theory]
    [InlineData("haiku", "low")]
    [InlineData("sonnet", "none")]
    [InlineData("opus", "none")]
    [InlineData("fable", "none")]
    [InlineData("other", "high")]
    [InlineData("SONNET", "high")]
    [InlineData("sonnet", "ultra")]
    public void SelectionRejectsUnsupportedAliasEffortPairs(string alias, string effort)
        => Assert.Throws<CliFailure>(() => AnthropicModels.Select(alias, effort));

    [Fact]
    public void EvidenceRequiresResolvedModelAndNormalizesMissingModelsUsed()
    {
        var selection = AnthropicModels.Select("sonnet", "high");
        var evidence = AnthropicModels.NormalizeEvidence(selection, "claude-sonnet-4-6", null);

        Assert.Equal("sonnet", evidence.RequestedAlias);
        Assert.Equal("claude-sonnet-4-6", evidence.ResolvedModel);
        Assert.Equal(["claude-sonnet-4-6"], evidence.ModelsUsed);
        Assert.Throws<CliFailure>(() => AnthropicModels.NormalizeEvidence(selection, null, null));
        Assert.Throws<CliFailure>(() => AnthropicModels.NormalizeEvidence(selection, string.Empty, null));
    }

    [Fact]
    public void FamilyAliasAllowsOnlyItsFamilyAndInheritPinsExactResolvedModel()
    {
        var sonnet = AnthropicModels.Select("sonnet", "max");
        var familyEvidence = AnthropicModels.NormalizeEvidence(sonnet, "claude-sonnet-4-6", ["claude-sonnet-4-6", "claude-sonnet-5"]);
        Assert.Equal(2, familyEvidence.ModelsUsed.Count);
        Assert.Throws<CliFailure>(() => AnthropicModels.NormalizeEvidence(sonnet, "claude-opus-4-6", ["claude-opus-4-6"]));
        Assert.Throws<CliFailure>(() => AnthropicModels.NormalizeEvidence(sonnet, "claude-sonnet-4-6", ["claude-sonnet-4-6", "claude-haiku-4-5"]));

        var inherit = AnthropicModels.Select("inherit", "none");
        var inheritedEvidence = AnthropicModels.NormalizeEvidence(inherit, "claude-opus-4-6", ["claude-opus-4-6"]);
        Assert.Equal("claude-opus-4-6", inheritedEvidence.ResolvedModel);
        Assert.Throws<CliFailure>(() => AnthropicModels.NormalizeEvidence(inherit, "claude-opus-4-6", ["claude-opus-4-6", "claude-opus-5"]));
    }

    [Theory]
    [MemberData(nameof(ModelEvidenceCases))]
    public void FamilyBoundaryContractMatchesClaudeHook(string alias, string model, bool valid)
    {
        var selection = AnthropicModels.Select(alias, alias == "haiku" ? "none" : "high");
        if (valid)
        {
            var evidence = AnthropicModels.NormalizeEvidence(selection, model, [model]);
            Assert.Equal(model, evidence.ResolvedModel);
        }
        else
        {
            Assert.Throws<CliFailure>(() => AnthropicModels.NormalizeEvidence(selection, model, [model]));
        }
    }

    [Fact]
    public void DoctorConflictsAreQualifiedByClaudeHost()
    {
        var workspace = CreateRepository();
        try
        {
            static Func<string, string?> EnvironmentReader(IReadOnlyDictionary<string, string> values)
                => name => values.GetValueOrDefault(name);

            var effortOverride = EnvironmentReader(new Dictionary<string, string>
            {
                ["CLAUDECODE"] = "1",
                ["CLAUDE_CODE_EFFORT_LEVEL"] = "high",
            });
            var modelOverride = EnvironmentReader(new Dictionary<string, string>
            {
                ["CLAUDE_CODE_REMOTE"] = "true",
                ["CLAUDE_CODE_SUBAGENT_MODEL"] = "opus",
            });
            var outsideClaude = EnvironmentReader(new Dictionary<string, string>
            {
                ["CLAUDE_CODE_EFFORT_LEVEL"] = "high",
                ["CLAUDE_CODE_SUBAGENT_MODEL"] = "opus",
            });

            Assert.Throws<CliFailure>(() => ForgeWorkflow.Doctor(workspace, HostKind.Claude, effortOverride));
            Assert.Throws<CliFailure>(() => ForgeWorkflow.Doctor(workspace, HostKind.Claude, modelOverride));
            Assert.True(ForgeWorkflow.Doctor(workspace, HostKind.Codex, effortOverride).Git.Ok);
            Assert.True(ForgeWorkflow.Doctor(workspace, HostKind.Codex, outsideClaude).Git.Ok);
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    [Theory]
    [InlineData("2.1.225 (Claude Code)", false)]
    [InlineData("2.1.226 (Claude Code)", true)]
    [InlineData("2.2.0-beta.1 (Claude Code)", true)]
    [InlineData("unparseable", false)]
    public void PersistentBuilderCapabilityRequiresMinimumClaudeVersion(string output, bool expected)
        => Assert.Equal(expected, ClaudeCapabilities.EvaluateVersion(output).Ok);

    [Fact]
    public void ClaudeDoctorReportsPersistentBuilderResumeCapability()
    {
        var workspace = CreateRepository();
        try
        {
            var unavailable = ForgeWorkflow.Doctor(workspace, HostKind.Claude, _ => null,
                                                   () => new ToolCheckData(false, "2.1.225", "unsupported"));
            var supported = ForgeWorkflow.Doctor(workspace, HostKind.Claude, _ => null,
                                                 () => new ToolCheckData(true, "2.1.226", null));
            var codex = ForgeWorkflow.Doctor(workspace, HostKind.Codex, _ => null,
                                             () => throw new InvalidOperationException("Codex must not probe Claude"));

            Assert.False(unavailable.Claude!.Ok);
            Assert.Equal("unsupported", unavailable.Claude.Error);
            Assert.True(supported.Claude!.Ok);
            Assert.Null(codex.Claude);
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    private static string CreateRepository()
    {
        var workspace = TestPaths.Unique("planforge-flow-tests");
        Directory.CreateDirectory(workspace);
        Assert.Equal(0, Infrastructure.Process.ProcessExecution.Run("git", ["-C", workspace, "init"]).ExitCode);
        return workspace;
    }
}
