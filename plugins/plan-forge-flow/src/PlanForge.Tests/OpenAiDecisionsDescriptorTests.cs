using PlanForge.Advisors;
using PlanForge.Advisors.Jev;
using PlanForge.Advisors.MicrosoftDecision;
using PlanForge.Advisors.OpenAiDecisions;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>OpenAI Decisions' configuration contract, with expectations independent of discovery code.</summary>
public sealed class OpenAiDecisionsDescriptorTests
{
    /// <summary>The credential variable.</summary>
    private const string KEY = "PLANFORGE_OPENAI_API_KEY";

    /// <summary>The optional model variable.</summary>
    private const string MODEL = "PLANFORGE_OPENAI_MODEL";

    /// <summary>The model requested when the model variable is unset or blank.</summary>
    private const string DEFAULT_MODEL = "gpt-6-luna";

    /// <summary>The credential's three states: unset, blank and set.</summary>
    private static readonly string?[] KEYS = [null, " \t", "distinctive-openai-secret-71c4"];

    /// <summary>The model's four states: unset, blank, valid and invalid.</summary>
    private static readonly string?[] MODELS = [null, "", "gpt-6.1-luna", "gpt/6"];

    /// <summary>
    /// All twelve cells of key {unset, blank, set} × model {unset, blank, valid, invalid}: absent when nothing non-blank is set,
    /// configured with the default or the valid model when the key is set and the model is not invalid, and otherwise partly
    /// configured naming exactly the missing key and the invalid model.
    /// </summary>
    [Fact]
    public void Every_combination_of_the_two_variables_gets_its_expected_answer()
    {
        var catalog = new AdvisorCatalog([new OpenAiDecisionsAdvisorDescriptor()]);
        for (var key = 0; key < KEYS.Length; key++)
        {
            for (var model = 0; model < MODELS.Length; model++)
            {
                var environment = new Dictionary<string, string?> { [KEY] = KEYS[key], [MODEL] = MODELS[model] };
                var found = catalog.Discover(name => environment.GetValueOrDefault(name));
                var cell = $"key {key}, model {model}";
                var expected = (key, model) switch
                {
                    (< 2, < 2) => "absent",
                    (< 2, 2) => $"partly {KEY}:missing",
                    (< 2, 3) => $"partly {KEY}:missing {MODEL}:invalid",
                    (2, < 2) => $"configured {DEFAULT_MODEL}",
                    (2, 2) => "configured gpt-6.1-luna",
                    _ => $"partly {MODEL}:invalid"
                };
                Assert.True(expected == Describe(found), $"{cell}: expected '{expected}', got '{Describe(found)}'");
            }
        }
    }

    /// <summary>Model names on both sides of the rule, 64 characters being the longest accepted.</summary>
    public static TheoryData<string, bool> ModelNames() => new()
    {
        { "gpt-6-luna", true }, { "gpt-6.1-luna", true }, { "a_b-c.d", true }, { new string('g', 64), true },
        { new string('g', 65), false }, { "-gpt-6-luna", false }, { ".gpt-6-luna", false }, { "gpt 6", false },
        { "gpt/6", false }, { "gpt-6-lüna", false }
    };

    /// <summary>The model is a name safe to report and log: 1–64 ASCII letters, digits, '.', '_' or '-', starting with a letter or digit.</summary>
    /// <param name="model">The configured value.</param>
    /// <param name="valid">Whether it configures the Advisor.</param>
    [Theory]
    [MemberData(nameof(ModelNames))]
    public void Model_validity(string model, bool valid) => AssertValidity(model, valid);

    /// <summary>An invalid model is reported by its variable's name only, and the credential never at all.</summary>
    [Fact]
    public void An_invalid_model_is_named_and_never_reported()
    {
        const string model = "distinctive/model-3f9e";
        var environment = new Dictionary<string, string?> { [KEY] = KEYS[2], [MODEL] = model };
        var partial = Assert.Single(AdvisorCatalog.Default.Discover(name => environment.GetValueOrDefault(name)).PartlyConfigured);
        Assert.Equal("openai-decisions", partial.Id);
        Assert.Equal([new AdvisorProblem(MODEL, "invalid")], partial.Problems);
        Assert.DoesNotContain("distinctive/model", partial.ToString());
        Assert.DoesNotContain(KEYS[2]!, partial.ToString());

        var error = Assert.Throws<ArgumentRejectedException>(() =>
            AdvisorCatalog.Default.Select(name => environment.GetValueOrDefault(name), "openai-decisions"));
        Assert.Equal($"Advisor 'openai-decisions' is partly configured: {MODEL}: invalid", error.Message);
    }

    /// <summary>OpenAI's own variables, without the PLANFORGE_ prefix, configure nothing in the production catalogue.</summary>
    [Fact]
    public void Foreign_openai_variables_configure_nothing()
    {
        var environment = new Dictionary<string, string?>
        {
            ["OPENAI_API_KEY"] = "vendor-secret", ["OPENAI_MODEL"] = DEFAULT_MODEL, ["OPENAI_BASE_URL"] = "https://api.openai.com/v1"
        };
        string? Lookup(string name) => environment.GetValueOrDefault(name);

        var found = AdvisorCatalog.Default.Discover(Lookup);
        Assert.Empty(found.Configured);
        Assert.Empty(found.PartlyConfigured);
        var error = Assert.Throws<ArgumentRejectedException>(() => AdvisorCatalog.Default.Select(Lookup, "openai-decisions"));
        Assert.Equal($"Advisor 'openai-decisions' is absent; required variables: {KEY}", error.Message);
    }

    /// <summary>With all three Advisors configured the Orchestrator must choose, and each id selects its own Advisor.</summary>
    [Fact]
    public void Three_configured_Advisors_require_a_choice_and_each_id_selects_its_own()
    {
        var environment = new Dictionary<string, string?>
        {
            ["PLANFORGE_JEV_API_KEY"] = "jev-secret",
            ["PLANFORGE_MSDECISION_ENDPOINT"] = "https://resource.services.ai.azure.invalid",
            ["PLANFORGE_MSDECISION_API_KEY"] = "msdecision-secret", ["PLANFORGE_MSDECISION_DEPLOYMENT"] = "Decision-1",
            [KEY] = KEYS[2], [MODEL] = "gpt-6.1-luna"
        };
        string? Lookup(string name) => environment.GetValueOrDefault(name);

        var error = Assert.Throws<ArgumentRejectedException>(() => AdvisorCatalog.Default.Select(Lookup, null));
        Assert.Equal("several Advisors are configured: jev, microsoft-decision, openai-decisions; select an Advisor", error.Message);
        using var jev = Assert.IsType<JevAdvisor>(AdvisorCatalog.Default.Select(Lookup, "jev"));
        using var microsoft = Assert.IsType<MicrosoftDecisionAdvisor>(AdvisorCatalog.Default.Select(Lookup, "microsoft-decision"));
        using var openAi = Assert.IsType<OpenAiDecisionsAdvisor>(AdvisorCatalog.Default.Select(Lookup, "openai-decisions"));
        Assert.Equal("openai-decisions", openAi.Id);
        Assert.Equal("gpt-6.1-luna", openAi.RequestedModel);
    }

    /// <summary>Describes a single-descriptor discovery as one comparable line, without any variable value.</summary>
    /// <param name="found">The discovery to describe.</param>
    private static string Describe(AdvisorDiscovery found) => (found.Configured, found.PartlyConfigured) switch
    {
        ([], []) => "absent",
        ([var configured], []) when configured.Id == "openai-decisions" => $"configured {configured.Model}",
        ([], [var partial]) when partial.Id == "openai-decisions" =>
            "partly " + string.Join(" ", partial.Problems.Select(problem => $"{problem.Variable}:{problem.Problem}")),
        _ => $"unexpected {found.Configured.Count} configured, {found.PartlyConfigured.Count} partly configured"
    };

    /// <summary>Checks that the model's value alone decides between configured with that model and invalid.</summary>
    /// <param name="model">The model under test, set together with a valid credential.</param>
    /// <param name="valid">Whether the Advisor is configured.</param>
    private static void AssertValidity(string model, bool valid)
    {
        var environment = new Dictionary<string, string?> { [KEY] = KEYS[2], [MODEL] = model };
        var found = new AdvisorCatalog([new OpenAiDecisionsAdvisorDescriptor()]).Discover(name => environment.GetValueOrDefault(name));
        Assert.Equal(valid ? $"configured {model}" : $"partly {MODEL}:invalid", Describe(found));
    }
}
