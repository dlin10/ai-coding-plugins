using PlanForge.Advisors;
using PlanForge.Advisors.Jev;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>R4's complete configuration and selection axes, with expectations independent of discovery code.</summary>
public sealed class AdvisorDiscoveryMatrixTests
{
    /// <summary>The real Jev credential variable.</summary>
    private const string KEY = "PLANFORGE_JEV_API_KEY";

    /// <summary>The real Jev model variable.</summary>
    private const string MODEL = "PLANFORGE_JEV_MODEL";

    /// <summary>R4: unset, empty and whitespace credentials are all unset; only set is configured.</summary>
    private static readonly string[] KEY_AXIS = ["unset", "empty", "whitespace", "set"];

    /// <summary>
    /// R4 discovery table: columns are unset, empty, whitespace and set key. Blank models default;
    /// pinned models are valid; aliases and a version without the Jev prefix are invalid.
    /// No combinations are excluded and no expectation covers a wider cell.
    /// </summary>
    private static readonly DiscoveryRow[] DISCOVERY =
    [
        new("unset", null, ["absent", "absent", "absent", "jev-1.13.0"]),
        new("empty", "", ["absent", "absent", "absent", "jev-1.13.0"]),
        new("jev-1.14.0", "jev-1.14.0", ["missing", "missing", "missing", "jev-1.14.0"]),
        new("jev-latest", "jev-latest", ["missing,invalid", "missing,invalid", "missing,invalid", "invalid"]),
        new("jev-preview", "jev-preview", ["missing,invalid", "missing,invalid", "missing,invalid", "invalid"]),
        new("1.13.0", "1.13.0", ["missing,invalid", "missing,invalid", "missing,invalid", "invalid"])
    ];

    /// <summary>
    /// R4 selection table: columns configure neither B nor C, B alone, and both. An explicit A
    /// takes priority over their count, unknown ids are refused first, and omitted counts A absent.
    /// No combinations are excluded and no expectation covers a wider cell.
    /// </summary>
    private static readonly SelectionRow[] SELECTION =
    [
        new("omitted", null, "absent", ["no Advisor is configured; the Evidence check stays manual", "B",
                                       "several Advisors are configured: B, C; select an Advisor"]),
        new("A configured", "A", "configured", ["A", "A", "A"]),
        new("A partly configured", "A", "partial", ["Advisor 'A' is partly configured: A_KEY: missing, A_MODEL: invalid",
                                                    "Advisor 'A' is partly configured: A_KEY: missing, A_MODEL: invalid",
                                                    "Advisor 'A' is partly configured: A_KEY: missing, A_MODEL: invalid"]),
        new("A absent", "A", "absent", ["Advisor 'A' is absent; required variables: A_KEY",
                                       "Advisor 'A' is absent; required variables: A_KEY",
                                       "Advisor 'A' is absent; required variables: A_KEY"]),
        new("unknown id", "unknown", "absent", ["unknown Advisor 'unknown'; known Advisors: A, B, C",
                                                "unknown Advisor 'unknown'; known Advisors: A, B, C",
                                                "unknown Advisor 'unknown'; known Advisors: A, B, C"])
    ];

    /// <summary>R4: every one of the 24 real-descriptor cells returns its declared discovery shape.</summary>
    [Fact]
    public void Every_discovery_cell_gets_its_expected_answer()
    {
        var catalog = new AdvisorCatalog([new JevAdvisorDescriptor()]);
        foreach (var row in DISCOVERY)
        for (var keyIndex = 0; keyIndex < KEY_AXIS.Length; keyIndex++)
        {
            var environment = new Dictionary<string, string?>
            {
                [KEY] = KEY_AXIS[keyIndex] switch { "unset" => null, "empty" => "", "whitespace" => " \t\r\n", _ => "distinctive-secret" },
                [MODEL] = row.Model
            };
            var found = catalog.Discover(name => environment.GetValueOrDefault(name));
            var expected = row.Expected[keyIndex];
            if (expected.StartsWith("jev-", StringComparison.Ordinal))
            {
                Assert.Equal(new ConfiguredAdvisor("jev", expected), Assert.Single(found.Configured));
                Assert.Empty(found.PartlyConfigured);
            }
            else if (expected == "absent")
            {
                Assert.Empty(found.Configured);
                Assert.Empty(found.PartlyConfigured);
            }
            else
            {
                Assert.Empty(found.Configured);
                var partial = Assert.Single(found.PartlyConfigured);
                Assert.Equal("jev", partial.Id);
                var problems = expected.Split(',').Select(problem => new AdvisorProblem(problem == "missing" ? KEY : MODEL, problem));
                Assert.Equal(problems, partial.Problems);
            }
        }
        Assert.Equal([KEY], catalog.SecretVariables);
    }

    /// <summary>R4: every selection cell either constructs only the selected Advisor or refuses without construction.</summary>
    [Fact]
    public void Every_selection_cell_gets_its_expected_answer()
    {
        foreach (var row in SELECTION)
        for (var otherCount = 0; otherCount <= 2; otherCount++)
        {
            var created = new List<string>();
            var catalog = new AdvisorCatalog([new FakeDescriptor("A", created), new FakeDescriptor("B", created), new FakeDescriptor("C", created)]);
            var environment = new Dictionary<string, string?>();
            if (row.AState == "configured") environment["A_KEY"] = "secret-A";
            if (row.AState == "partial") environment["A_MODEL"] = "invalid";
            if (otherCount >= 1) environment["B_KEY"] = "secret-B";
            if (otherCount >= 2) environment["C_KEY"] = "secret-C";
            var expected = row.Expected[otherCount];
            if (expected is "A" or "B" or "C")
            {
                var selected = catalog.Select(name => environment.GetValueOrDefault(name), row.Argument);
                Assert.Equal(expected, selected.Id);
                Assert.Equal("model-1", selected.RequestedModel);
                Assert.Equal([expected], created);
            }
            else
            {
                var error = Assert.Throws<ArgumentRejectedException>(() => catalog.Select(name => environment.GetValueOrDefault(name), row.Argument));
                Assert.Equal(expected, error.Message);
                Assert.Empty(created);
            }
        }
    }

    /// <summary>R4: the tables include each required axis value and all 24 discovery and 15 selection cells.</summary>
    [Fact]
    public void Every_axis_value_occurs_in_some_cell()
    {
        Assert.Equal(["unset", "empty", "whitespace", "set"], KEY_AXIS);
        Assert.Equal(["unset", "empty", "jev-1.14.0", "jev-latest", "jev-preview", "1.13.0"], DISCOVERY.Select(row => row.Axis));
        Assert.All(DISCOVERY, row => Assert.Equal(4, row.Expected.Length));
        Assert.Equal(24, DISCOVERY.Sum(row => row.Expected.Length));
        Assert.Equal(["omitted", "A configured", "A partly configured", "A absent", "unknown id"], SELECTION.Select(row => row.Axis));
        Assert.All(SELECTION, row => Assert.Equal(3, row.Expected.Length));
        Assert.Equal(15, SELECTION.Sum(row => row.Expected.Length));
    }

    /// <summary>R4 also treats a whitespace model as unset, and ignores a service's own credential.</summary>
    [Fact]
    public void Whitespace_models_default_and_vendor_credentials_do_not_configure_an_Advisor()
    {
        var environment = new Dictionary<string, string?> { [MODEL] = " \t", ["TYPESAFE_API_KEY"] = "vendor-secret" };
        Assert.Empty(AdvisorCatalog.Default.Discover(name => environment.GetValueOrDefault(name)).Configured);
        Assert.Empty(AdvisorCatalog.Default.Discover(name => environment.GetValueOrDefault(name)).PartlyConfigured);
        environment[KEY] = "server-secret";
        using var advisor = Assert.IsType<JevAdvisor>(AdvisorCatalog.Default.Select(name => environment.GetValueOrDefault(name), null));
        Assert.Equal("jev-1.13.0", advisor.RequestedModel);
    }

    /// <summary>One R4 discovery row, with key states in column order.</summary>
    /// <param name="Axis">The model axis label.</param>
    /// <param name="Model">The actual model environment value.</param>
    /// <param name="Expected">The four expected discovery results.</param>
    private sealed record DiscoveryRow(string Axis, string? Model, string[] Expected);

    /// <summary>One R4 selection row, with configured counts of B and C in column order.</summary>
    /// <param name="Axis">The argument axis label.</param>
    /// <param name="Argument">The explicit id or omitted argument.</param>
    /// <param name="AState">A's configuration state.</param>
    /// <param name="Expected">The three selected ids or refusal messages.</param>
    private sealed record SelectionRow(string Axis, string? Argument, string AState, string[] Expected);

    /// <summary>A descriptor with a required credential and optional validated model.</summary>
    /// <param name="id">The fake Advisor id.</param>
    /// <param name="created">Records construction, to prove refusals never construct an Advisor.</param>
    private sealed class FakeDescriptor(string id, List<string> created) : AdvisorDescriptor
    {
        /// <summary>The test id.</summary>
        public override string Id => id;

        /// <summary>The variables owned by this fake Advisor.</summary>
        public override IReadOnlyList<AdvisorVariable> Variables => [new(id + "_KEY", true, true), new(id + "_MODEL", false, false)];

        /// <summary>The fake default requested model.</summary>
        /// <param name="environment">The test environment lookup.</param>
        public override string Model(Func<string, string?> environment) => environment(id + "_MODEL") ?? "model-1";

        /// <summary>Constructs only a selected Advisor, without any request.</summary>
        /// <param name="environment">The test environment lookup.</param>
        public override IAdvisor Create(Func<string, string?> environment)
        {
            created.Add(id);
            return new FakeAdvisor(id, Model(environment));
        }

        /// <summary>Only the fake pinned model value is valid.</summary>
        /// <param name="variable">The variable name.</param>
        /// <param name="value">The set value.</param>
        protected override bool IsValid(string variable, string value) => variable != id + "_MODEL" || value == "model-1";
    }

    /// <summary>A selected fake that fails if discovery or selection tries to measure or ask.</summary>
    /// <param name="Id">The selected Advisor id.</param>
    /// <param name="RequestedModel">The selected model.</param>
    private sealed record FakeAdvisor(string Id, string RequestedModel) : IAdvisor
    {
        /// <summary>Selection never reads request limits.</summary>
        public AdvisorLimits Limits => throw new NotSupportedException();

        /// <summary>Selection never prices a request.</summary>
        public decimal InputUsdPerMillion => throw new NotSupportedException();

        /// <summary>Selection never measures a request.</summary>
        /// <param name="request">The forbidden request.</param>
        public AdviceSize Measure(AdviceRequest request) => throw new NotSupportedException();

        /// <summary>Selection never asks a question.</summary>
        /// <param name="request">The forbidden request.</param>
        /// <param name="ct">Caller cancellation.</param>
        public Task<AdviceResult> AskAsync(AdviceRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}
