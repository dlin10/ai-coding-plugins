using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class RefCallTargetTests
{
    [Fact]
    public void Ref_returning_call_over_a_ref_local_as_target_lowers()
    {
        var body = Lower("public static void Target() { var x = 0; ref int first = ref x; At(ref first) = 1; }");

        Assert.NotEmpty(body.Blocks.SelectMany(block => block.Operations));
    }

    [Fact]
    public void Ref_returning_call_over_a_ref_parameter_as_target_lowers()
    {
        var body = Lower("public static void Target(ref int value) { At(ref value) = 1; }");

        Assert.NotEmpty(body.Blocks.SelectMany(block => block.Operations));
    }

    [Fact]
    public void Ref_returning_call_over_an_out_argument_as_target_lowers()
    {
        var body = Lower("public static void Target() { At(out var value) = 1; }",
                         "private static int Cell; public static ref int At(out int value) { value = 0; return ref Cell; }");

        Assert.NotEmpty(body.Blocks.SelectMany(block => block.Operations));
    }

    [Fact]
    public void Unsafe_Add_over_a_ref_local_as_target_lowers()
    {
        var body = Lower("public static void Target() { var values = new int[2]; ref int first = ref values[0]; " +
                         "System.Runtime.CompilerServices.Unsafe.Add(ref first, 1) = 1; }");

        Assert.NotEmpty(body.Blocks.SelectMany(block => block.Operations));
    }

    [Fact]
    public async Task Body_with_a_ref_call_target_keeps_its_other_accesses()
    {
        var result = await Analyze(Shared("var x = 0; Cells.At(ref x) = 1; _counter.Count++;"));
        var findings = result.Findings.Where(finding => finding.Resource.AccessPath.SequenceEqual(["Count"])).ToArray();

        Assert.Equal(2, findings.Length);
        Assert.All(findings, finding => Assert.Equal(("DCA1002", "High"), (finding.RuleId, finding.Confidence.Label)));
        Assert.Contains(findings, finding => finding.AccessA.Symbol == finding.AccessB.Symbol);
        Assert.Contains(findings, finding => finding.AccessA.Symbol != finding.AccessB.Symbol);
    }

    [Fact]
    public async Task Helper_with_a_ref_call_target_keeps_its_accesses()
    {
        var result = await Analyze(Shared("Bump();", "private void Bump() { var x = 0; Cells.At(ref x) = 1; _counter.Count++; }"));

        Assert.Contains(result.Findings, finding => finding.RuleId == "DCA1002" &&
                                                    finding.Resource.AccessPath.SequenceEqual(["Count"]) &&
                                                    finding.AccessA.Symbol.Contains("Bump", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ref_call_target_makes_no_lowering_diagnostic()
    {
        var result = await Analyze(Shared("var x = 0; Cells.At(ref x) = 1; _counter.Count++;"));

        Assert.DoesNotContain(result.Coverage.SelectMany(coverage => coverage.Diagnostics),
                              diagnostic => diagnostic.StartsWith("lowering:", StringComparison.Ordinal));
    }

    private static IrBody Lower(string target, string at = "public static ref int At(ref int value) => ref value;")
    {
        var solution = FixtureSolution.Create(("Case.cs", $"class Case {{ {at} {target} }}"));
        var compilation = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var method = compilation.GetTypeByMetadataName("Case")!.GetMembers("Target").OfType<IMethodSymbol>().Single();
        return IrLowering.Lower(method, compilation, ROOT_DIRECTORY, CancellationToken.None).Body;
    }

    private static Task<AnalysisResult> Analyze(string source) =>
        PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", source)), ROOT_DIRECTORY, CancellationToken.None);

    private static string Shared(string post, string extra = "") => Usings + $$"""
        public sealed class Counter { public int Count; }
        public static class Cells { public static ref int At(ref int value) => ref value; }

        [ApiController]
        public sealed class CounterController : ControllerBase
        {
            private readonly Counter _counter;
            public CounterController(Counter counter) => _counter = counter;
            [HttpPost("/counter")]
            public void Post() { {{post}} }
            {{extra}}
        }

        public sealed class CounterWorker : BackgroundService
        {
            private readonly Counter _counter;
            public CounterWorker(Counter counter) => _counter = counter;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                var x = 0;
                Cells.At(ref x) = 1;
                _counter.Count++;
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<Counter>(); services.AddHostedService<CounterWorker>();");
}
