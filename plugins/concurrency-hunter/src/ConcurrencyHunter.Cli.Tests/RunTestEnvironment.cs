using System.Security.Cryptography;
using System.Text;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Runs;

namespace ConcurrencyHunter.Cli.Tests;

internal sealed class RunTestEnvironment : IDisposable
{
    internal RunTestEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), $"concurrency-hunter-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        SolutionPath = Path.Combine(Root, "Demo.slnx");
        File.WriteAllText(SolutionPath, "<Solution />");
        BundleRoot = Path.Combine(Root, "bundles");
        Time = new ManualTimeProvider(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));
    }

    internal string Root { get; }
    internal string SolutionPath { get; }
    internal string BundleRoot { get; }
    internal ManualTimeProvider Time { get; }

    internal RunRegistry Registry(AnalysisStep? step = null, string? bundleRoot = null) =>
        new(Time, bundleRoot ?? BundleRoot, step ?? SuccessfulAnalysisAsync);

    internal static async Task<RunAnalysis> SuccessfulAnalysisAsync(string target,
                                                                     CancellationToken cancellationToken)
    {
        var solution = FixtureSolution.Create(("Controller.cs", """
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public void Set() { _value = 1; }
            }
            """), ("Startup.cs", """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Microsoft.Extensions.DependencyInjection;
            public static class Startup
            {
                public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                {
                    services.AddControllers();
                    app.MapControllers();
                }
            }
            """));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(
            solution,
            @"C:\fixture",
            cancellationToken);
        return new RunAnalysis(false, false, 1, 1, [], result, [], 0.1, 0.2);
    }

    internal static string ValidNarrative => """
        Race evidence [E:F1.A]
        ## Remediation
        - Protect the field
          - Check: verify manually under load
        """;

    internal string RepositoryHash() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToLowerInvariant())))[..12]
               .ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, true);
    }
}
