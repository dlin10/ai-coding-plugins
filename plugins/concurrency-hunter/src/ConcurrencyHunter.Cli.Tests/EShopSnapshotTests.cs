using System.Text.Json;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Serialization;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresEShopFactAttribute : FactAttribute
{
    public const string VARIABLE = "CH_ESHOP_ROOT";

    public RequiresEShopFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VARIABLE)))
            Skip = $"{VARIABLE} is not set. Set it to an eShopOnContainers checkout to run this read-only eval.";
    }
}

public sealed class EShopSnapshotTests
{
    private const string SOLUTION = "src/eShopOnContainers-ServicesAndWebApps.sln";

    [RequiresEShopFact]
    public async Task EShop_measurement_matches_the_snapshot()
    {
        var target = Path.Combine(Environment.GetEnvironmentVariable(RequiresEShopFactAttribute.VARIABLE)!, SOLUTION);

        var (measurement, _) = await MetricsCommand.MeasureAsync(target, AnalysisLimits.Default, MetricsCommand.LoadWithMsBuildAsync,
                                                                 CancellationToken.None);

        var fresh = MetricsCommandTests.Normalize(JsonSerializer.Serialize(new MeasurementSnapshot(measurement.Counts, measurement.Coverage),
                                                                           MetricsJsonContext.Default.MeasurementSnapshot));
        var recorded = MetricsCommandTests.Normalize(await File.ReadAllTextAsync(RepositoryFiles.FindRepositoryFile(
            "plugins", "concurrency-hunter", "src", "ConcurrencyHunter.Cli.Tests", "Snapshots", "eshop.json")));
        Assert.True(recorded == fresh, "The eShop measurement differs from Snapshots/eshop.json:\n" + MetricsCommandTests.UnifiedDiff(recorded, fresh));
    }
}
