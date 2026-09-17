using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Reporting;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A new root type is one provider class and one registration: no engine changes (SPEC 11).</summary>
public sealed class SyntheticProviderEndToEndTests
{
    private const string Source = """
        [System.AttributeUsage(System.AttributeTargets.Method)]
        public sealed class QueueHandlerAttribute : System.Attribute { }

        public static class Inventory { public static int Reserved; }

        public static class OrderQueue
        {
            [QueueHandler]
            public static void Reserve(int quantity) => Inventory.Reserved += quantity;

            public static void NotAHandler() => Inventory.Reserved = 0;
        }
        """;

    [Fact]
    public async Task Queue_handler_provider_registered_after_the_built_ins_yields_a_finding()
    {
        var result = await Analyze(new ProviderRegistry(ProviderRegistry.BuiltIn.Providers.Append(new QueueHandlerProvider())));

        Assert.Equal(["aspnetcore", "hosting", "queue"], result.Coverage.Single().RootsPerProvider.Keys);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("DCA1002", finding.RuleId);
        Assert.Equal("queue", finding.AccessA.Root.ProviderId);
        Assert.Equal("OrderQueue.Reserve(int)", finding.AccessA.Symbol);
        Assert.Equal("static:Inventory", finding.Resource.Region);

        var builtInOnly = await Analyze(ProviderRegistry.BuiltIn);
        Assert.Empty(builtInOnly.Findings);
    }

    [Fact]
    public async Task Queue_handler_finding_appears_in_the_rendered_report()
    {
        var result = await Analyze(new ProviderRegistry(ProviderRegistry.BuiltIn.Providers.Append(new QueueHandlerProvider())));

        var bundle = ReportRenderer.Render(ReportingTestData.CreateReport(result, new Dictionary<string, string>()));

        var finding = Assert.Single(result.Findings);
        Assert.Contains($"##### {finding.FindingId}", bundle.ReportMarkdown, StringComparison.Ordinal);
        Assert.Contains("OrderQueue.Reserve(int) performs read-modify-write", bundle.ReportMarkdown, StringComparison.Ordinal);
        Assert.Contains("queue handler OrderQueue.Reserve(int)", bundle.ReportMarkdown, StringComparison.Ordinal);
        Assert.Contains(finding.AccessA.Root.RootId, bundle.FindingsJson, StringComparison.Ordinal);
        Assert.Contains(finding.Fingerprint, bundle.FindingsJson, StringComparison.Ordinal);
    }

    private static Task<AnalysisResult> Analyze(ProviderRegistry registry) =>
        PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Queue.cs", Source)), EngineFixture.ROOT_DIRECTORY, registry,
                                      CancellationToken.None);

    /// <summary>Every source method marked <c>[QueueHandler]</c> is a root a message pump may run repeatedly and in parallel.</summary>
    private sealed class QueueHandlerProvider : IExecutionRootProvider
    {
        public string ProviderId => "queue";

        public IReadOnlyList<SupportedAssemblyVersion> SupportedAssemblyVersions =>
            [new SupportedAssemblyVersion("Fixture.Queues", new Version(1, 0), new Version(2, 0))];

        public RootDiscoveryResult Discover(RootDiscoveryContext context)
        {
            var roots = new List<ExecutionRootDescriptor>();
            foreach (var compilation in context.Compilations)
            {
                foreach (var method in Types(compilation.Assembly.GlobalNamespace).SelectMany(type => type.GetMembers().OfType<IMethodSymbol>()))
                {
                    if (!method.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == "QueueHandlerAttribute"))
                        continue;

                    var symbol = SymbolNames.Method(method);
                    var location = method.Locations[0].GetLineSpan();
                    roots.Add(new ExecutionRootDescriptor(
                        $"queue:{compilation.AssemblyName}:{method.GetDocumentationCommentId()}",
                        "queue-handler",
                        ProviderId,
                        new RootEntry(IrLowering.RootBodyId(method), symbol, $"queue handler {symbol}",
                                      new SourceSpan(Path.GetFileName(location.Path), location.StartLinePosition.Line + 1,
                                                     location.StartLinePosition.Character + 1, location.EndLinePosition.Line + 1,
                                                     location.EndLinePosition.Character + 1)),
                        new InstanceBindings(ReceiverKind.None,
                            method.Parameters.Select(parameter => new ParameterBinding(parameter.Name, SymbolNames.Type(parameter.Type),
                                                                                       ParameterBindingKind.RequestData)).ToArray()),
                        new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, context.ScopeId),
                        [],
                        [],
                        [],
                        []));
                }
            }

            return new RootDiscoveryResult(RootDiscoveryStatus.Checked, roots, []);
        }

        private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol @namespace) =>
            @namespace.GetTypeMembers().Concat(@namespace.GetNamespaceMembers().SelectMany(Types));
    }
}
