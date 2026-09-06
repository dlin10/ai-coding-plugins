using CacheDetective.Graph;
using CacheDetective.Rules;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// What names a finding, and what a <c>notDefects</c> entry names. Both the behaviour snapshot and the
/// demo metrics read findings from a graph, and an identity that drops a half — the project, the parent
/// of a stale pair, the external source — collapses two findings into one and loses one of them from
/// every count taken afterwards.
/// </summary>
public sealed class FindingIdentityTests
{
    /// <summary>
    /// Two stale-parent findings that differ only in the parent key. The metrics identity used to name
    /// only the child, so the pair collapsed into one row and one of the two vanished from every count.
    /// </summary>
    [Fact]
    public void Two_stale_parent_findings_that_share_a_child_are_two_findings()
    {
        // The identity names the parent in the key slot and the child as the target, so a pair that
        // differs only in the parent is two rows. Naming only the child collapsed them into one, and one
        // of the two vanished from every count the metrics took.
        var first = new FindingIdentity(StaleParentKeyFinding.Rule, "Shop.slnx", "App.Get()", "Catalog", "first:{id}",
                                        "redis", "child:{id}@redis", Confidence.Confirmed, false);
        var second = first with { Template = "second:{id}" };

        Assert.Equal(first.Target, second.Target);
        Assert.NotEqual(first.Key, second.Key);
        Assert.Equal(2, new[] { first, second }.Select(finding => finding.Key).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The external source is the target of an EXTERNAL_NO_TTL finding, so two keys served by
    /// different endpoints are two findings rather than one.</summary>
    [Fact]
    public void The_external_source_tells_two_no_ttl_findings_apart()
    {
        var weather = new FindingIdentity(ExternalNoTtlFinding.Rule, "Shop.slnx", "App.Get()", "Notifications",
                                          "weather:today", "memory", "external:http:Notifications:-:GET today",
                                          Confidence.Confirmed, false);
        var rates = weather with { Target = "external:http:Notifications:-:GET rates" };

        Assert.NotEqual(weather.Key, rates.Key);
    }

    /// <summary>The project is part of the identity: two handlers of the same symbol in different
    /// projects are two findings.</summary>
    [Fact]
    public void The_project_tells_two_identities_apart()
    {
        var left = new FindingIdentity("UNGUARDED_WRITE", "Shop.slnx", "App.Get()", "Catalog", "product:{id}", "memory",
                                       "dbo.Products", Confidence.Confirmed, false);
        var right = left with { Project = "Pricing" };

        Assert.NotEqual(left.Key, right.Key);
    }

    /// <summary>
    /// A notDefects entry names a place that is known to be correct, so a finding of <em>any</em> rule
    /// sitting on that handler is the failure it exists to catch. Comparing the rule and the target as
    /// well let a wrong finding of another rule slip past the very entry meant to stop it.
    /// </summary>
    [Fact]
    public void A_finding_of_another_rule_at_a_not_defect_handler_fails()
    {
        var notDefect = new ExpectedFinding("UNGUARDED_WRITE", "Shop.slnx", "App.Safe()", "Catalog", "price:{id}", "memory",
                                            "dbo.Prices", null);
        var planted = new ExpectedFinding(OrphanInvalidationFinding.Rule, "Shop.slnx", "App.Safe()", "Catalog",
                                          "other:{id}", "redis", "other:{id}@redis", Confidence.Likely);
        var expected = new ExpectedFindings([], [notDefect], []);

        var error = Assert.Throws<InvalidOperationException>(() => DemoMetrics.AssertNoNotDefects(expected, [planted]));

        Assert.Contains("notDefects", error.Message, StringComparison.Ordinal);
    }
}
