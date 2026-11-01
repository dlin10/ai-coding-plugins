using PlanForge.Advisors;
using PlanForge.Advisors.Jev;
using PlanForge.Advisors.MicrosoftDecision;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Microsoft-Decision-1's configuration contract, with expectations independent of discovery code.</summary>
public sealed class MicrosoftDecisionDescriptorTests
{
    /// <summary>The endpoint variable.</summary>
    private const string ENDPOINT = "PLANFORGE_MSDECISION_ENDPOINT";

    /// <summary>The credential variable.</summary>
    private const string KEY = "PLANFORGE_MSDECISION_API_KEY";

    /// <summary>The deployment variable.</summary>
    private const string DEPLOYMENT = "PLANFORGE_MSDECISION_DEPLOYMENT";

    /// <summary>The three variables in reporting order, with a valid value for each.</summary>
    private static readonly (string Name, string Value)[] VALID =
        [(ENDPOINT, "https://resource.services.ai.azure.invalid"), (KEY, "distinctive-secret"), (DEPLOYMENT, "Decision-1")];

    /// <summary>All eight subsets of the variables: none is absent, all is configured, any other names exactly the unset ones.</summary>
    [Fact]
    public void Every_subset_of_the_three_variables_gets_its_expected_answer()
    {
        var catalog = new AdvisorCatalog([new MicrosoftDecisionAdvisorDescriptor()]);
        for (var mask = 0; mask < 8; mask++)
        {
            var environment = VALID.Where((_, index) => (mask & 1 << index) != 0).ToDictionary(variable => variable.Name, variable => variable.Value);
            var found = catalog.Discover(name => environment.GetValueOrDefault(name));
            if (mask == 0)
            {
                Assert.Empty(found.Configured);
                Assert.Empty(found.PartlyConfigured);
            }
            else if (mask == 7)
            {
                Assert.Equal(new ConfiguredAdvisor("microsoft-decision", "Decision-1"), Assert.Single(found.Configured));
                Assert.Empty(found.PartlyConfigured);
            }
            else
            {
                Assert.Empty(found.Configured);
                var partial = Assert.Single(found.PartlyConfigured);
                Assert.Equal("microsoft-decision", partial.Id);
                Assert.Equal(VALID.Where((_, index) => (mask & 1 << index) == 0).Select(variable => new AdvisorProblem(variable.Name, "missing")),
                             partial.Problems);
            }
        }
    }

    /// <summary>Empty and whitespace values count as unset for every variable.</summary>
    /// <param name="blank">The blank value.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void Blank_values_are_unset(string blank)
    {
        var catalog = new AdvisorCatalog([new MicrosoftDecisionAdvisorDescriptor()]);
        var found = catalog.Discover(_ => blank);
        Assert.Empty(found.Configured);
        Assert.Empty(found.PartlyConfigured);
    }

    /// <summary>The endpoint is a plain https address, possibly a gateway's, and never a Foundry project endpoint.</summary>
    /// <param name="endpoint">The configured value.</param>
    /// <param name="valid">Whether it configures the Advisor.</param>
    [Theory]
    [InlineData("https://resource.services.ai.azure.com", true)]
    [InlineData("https://resource.services.ai.azure.com/", true)]
    [InlineData("https://resource.services.ai.azure.com:8443", true)]
    [InlineData("https://gateway.example/apim/decision/", true)]
    [InlineData("https://gateway.example/api/projectsx", true)]
    [InlineData("http://resource.services.ai.azure.com", false)]
    [InlineData("ftp://resource.services.ai.azure.com", false)]
    [InlineData("resource.services.ai.azure.com", false)]
    [InlineData("/providers/microsoft/v1/systemone", false)]
    [InlineData("https://user:password@resource.services.ai.azure.com", false)]
    [InlineData("https://resource.services.ai.azure.com?api-version=1", false)]
    [InlineData("https://resource.services.ai.azure.com/?", false)]
    [InlineData("https://resource.services.ai.azure.com/#fragment", false)]
    [InlineData(" https://resource.services.ai.azure.com", false)]
    [InlineData("https://resource.services.ai.azure.com/api/projects/my-project", false)]
    [InlineData("https://resource.services.ai.azure.com/API/Projects/my-project/", false)]
    [InlineData("https://resource.services.ai.azure.com/api/projects", false)]
    public void Endpoint_validity(string endpoint, bool valid) => AssertValidity(ENDPOINT, endpoint, valid);

    /// <summary>The deployment name is one that is safe to report and log as the requested model.</summary>
    /// <param name="deployment">The configured value.</param>
    /// <param name="valid">Whether it configures the Advisor.</param>
    [Theory]
    [InlineData("Decision-1", true)]
    [InlineData("Microsoft-Decision-1", true)]
    [InlineData("decision_1.v2", true)]
    [InlineData("d", true)]
    [InlineData("-decision", false)]
    [InlineData(".decision", false)]
    [InlineData("decision 1", false)]
    [InlineData("decision/1", false)]
    [InlineData("decision\n1", false)]
    [InlineData("decision-1\n", false)]
    public void Deployment_validity(string deployment, bool valid) => AssertValidity(DEPLOYMENT, deployment, valid);

    /// <summary>A deployment name longer than 64 characters is invalid; 64 is the longest accepted.</summary>
    [Fact]
    public void Deployment_names_are_at_most_sixty_four_characters()
    {
        AssertValidity(DEPLOYMENT, new string('d', 64), true);
        AssertValidity(DEPLOYMENT, new string('d', 65), false);
    }

    /// <summary>An invalid value is reported by its variable's name only.</summary>
    [Fact]
    public void An_invalid_endpoint_is_named_and_never_reported()
    {
        const string endpoint = "http://distinctive-host-4b1d.invalid";
        var environment = VALID.ToDictionary(variable => variable.Name, variable => variable.Value);
        environment[ENDPOINT] = endpoint;
        var partial = Assert.Single(AdvisorCatalog.Default.Discover(name => environment.GetValueOrDefault(name)).PartlyConfigured);
        Assert.Equal([new AdvisorProblem(ENDPOINT, "invalid")], partial.Problems);
        Assert.DoesNotContain("distinctive-host", partial.ToString());
    }

    /// <summary>The production catalogue keeps the three credentials secret, in catalogue order, and every other variable public.</summary>
    [Fact]
    public void The_default_catalogue_keeps_only_the_credentials_secret() =>
        Assert.Equal(["PLANFORGE_JEV_API_KEY", KEY, "PLANFORGE_OPENAI_API_KEY"], AdvisorCatalog.Default.SecretVariables);

    /// <summary>A vendor's own Azure variables never configure the Advisor.</summary>
    [Fact]
    public void Azure_variables_do_not_configure_it()
    {
        var environment = new Dictionary<string, string?>
        {
            ["AZURE_ENDPOINT"] = "https://resource.services.ai.azure.com", ["AZURE_API_KEY"] = "vendor-secret",
            ["AZURE_OPENAI_API_KEY"] = "vendor-secret", ["DEPLOYMENT_NAME"] = "Decision-1"
        };
        var found = AdvisorCatalog.Default.Discover(name => environment.GetValueOrDefault(name));
        Assert.Empty(found.Configured);
        Assert.Empty(found.PartlyConfigured);
    }

    /// <summary>With Jev configured too, the Orchestrator must choose, and each id selects its own Advisor.</summary>
    [Fact]
    public void Both_Advisors_configured_require_a_choice_and_each_id_selects_its_own()
    {
        var environment = VALID.ToDictionary(variable => variable.Name, variable => variable.Value);
        environment["PLANFORGE_JEV_API_KEY"] = "jev-secret";
        string? Lookup(string name) => environment.GetValueOrDefault(name);

        var error = Assert.Throws<ArgumentRejectedException>(() => AdvisorCatalog.Default.Select(Lookup, null));
        Assert.Equal("several Advisors are configured: jev, microsoft-decision; select an Advisor", error.Message);
        using var jev = Assert.IsType<JevAdvisor>(AdvisorCatalog.Default.Select(Lookup, "jev"));
        using var microsoft = Assert.IsType<MicrosoftDecisionAdvisor>(AdvisorCatalog.Default.Select(Lookup, "microsoft-decision"));
        Assert.Equal("Decision-1", microsoft.RequestedModel);
    }

    /// <summary>Checks that one variable's value alone decides between configured and invalid.</summary>
    /// <param name="variable">The variable under test.</param>
    /// <param name="value">Its value.</param>
    /// <param name="valid">Whether the Advisor is configured.</param>
    private static void AssertValidity(string variable, string value, bool valid)
    {
        var environment = VALID.ToDictionary(entry => entry.Name, entry => entry.Value);
        environment[variable] = value;
        var found = new AdvisorCatalog([new MicrosoftDecisionAdvisorDescriptor()]).Discover(name => environment.GetValueOrDefault(name));
        if (valid)
        {
            Assert.Single(found.Configured);
            Assert.Empty(found.PartlyConfigured);
        }
        else
        {
            Assert.Empty(found.Configured);
            Assert.Equal([new AdvisorProblem(variable, "invalid")], Assert.Single(found.PartlyConfigured).Problems);
        }
    }
}
