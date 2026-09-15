using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Xunit;
using Xunit.Sdk;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class ProviderFixtureTests
{
    private const string TwoWorkers = """
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.Hosting;
        namespace Demo;
        public sealed class FirstWorker : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
        }
        public sealed class SecondWorker : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
        }
        """;

    private static readonly string[] TwoWorkerRoots =
    [
        "test-worker Demo.FirstWorker.ExecuteAsync(CancellationToken) receiver=HostedService parameters=[stoppingToken:Unsupported] multiplicity=AtMostOnce overlap=Serialized scope=scope:Fixture flags=[Microsoft.Extensions.Hosting.Abstractions/10.0.0.0]",
        "test-worker Demo.SecondWorker.ExecuteAsync(CancellationToken) receiver=HostedService parameters=[stoppingToken:Unsupported] multiplicity=AtMostOnce overlap=Serialized scope=scope:Fixture flags=[Microsoft.Extensions.Hosting.Abstractions/10.0.0.0]"
    ];

    [Fact]
    public void Fixture_references_stubs_at_the_target_framework_or_the_case_version()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Hosting;
            public sealed class Worker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            """;

        ProviderFixture.Run(new WorkerProvider(), new ProviderCase("TestWorkers_Net9Framework_RootAtVersion9", [("Worker.cs", source)])
        {
            TargetFramework = "net9.0",
            ExpectedRoots =
            [
                "test-worker Worker.ExecuteAsync(CancellationToken) receiver=HostedService parameters=[stoppingToken:Unsupported] multiplicity=AtMostOnce overlap=Serialized scope=scope:Fixture flags=[Microsoft.Extensions.Hosting.Abstractions/9.0.0.0]"
            ]
        });
        ProviderFixture.Run(new WorkerProvider(), new ProviderCase("TestWorkers_HostingPinnedTo8_RootAtVersion8", [("Worker.cs", source)])
        {
            AssemblyVersions = new Dictionary<string, int> { [StubAssemblies.HOSTING_ABSTRACTIONS] = 8 },
            ExpectedRoots =
            [
                "test-worker Worker.ExecuteAsync(CancellationToken) receiver=HostedService parameters=[stoppingToken:Unsupported] multiplicity=AtMostOnce overlap=Serialized scope=scope:Fixture flags=[Microsoft.Extensions.Hosting.Abstractions/8.0.0.0]"
            ]
        });
    }

    [Fact]
    public void Roots_and_diagnostics_are_compared_ignoring_order_but_not_content()
    {
        var reversedExpectations = new ProviderCase("TestWorkers_TwoWorkers_TwoRoots", [("Workers.cs", TwoWorkers)])
        {
            ExpectedRoots = TwoWorkerRoots.Reverse().ToArray()
        };

        ProviderFixture.Run(new WorkerProvider(), reversedExpectations);
        ProviderFixture.Run(new WorkerProvider(WorkerProviderMode.Reversed), reversedExpectations);
        Assert.ThrowsAny<XunitException>(() => ProviderFixture.Run(new WorkerProvider(),
            reversedExpectations with { ExpectedRoots = [TwoWorkerRoots[0]] }));
        Assert.ThrowsAny<XunitException>(() => ProviderFixture.Run(new WorkerProvider(),
            reversedExpectations with { ExpectedRoots = [TwoWorkerRoots[0], TwoWorkerRoots[0]] }));
    }

    [Fact]
    public void Checked_empty_result_is_not_a_not_checked_result()
    {
        var noWorkers = new ProviderCase("TestWorkers_NoWorkers_CheckedEmpty", [("Empty.cs", "public sealed class Plain { }")]);
        var outOfRange = new ProviderCase("TestWorkers_Hosting7_NotChecked", [("Workers.cs", TwoWorkers)])
        {
            AssemblyVersions = new Dictionary<string, int> { [StubAssemblies.HOSTING_ABSTRACTIONS] = 7 },
            ExpectedStatus = RootDiscoveryStatus.NotChecked,
            ExpectedDiagnostics = ["UnsupportedAssemblyVersion Fixture"]
        };

        var empty = ProviderFixture.Run(new WorkerProvider(), noWorkers);
        var notChecked = ProviderFixture.Run(new WorkerProvider(), outOfRange);

        Assert.Empty(empty.Diagnostics);
        Assert.Empty(notChecked.Roots);
        Assert.ThrowsAny<XunitException>(() => ProviderFixture.Run(new WorkerProvider(),
            outOfRange with { ExpectedStatus = RootDiscoveryStatus.Checked, ExpectedDiagnostics = [] }));
    }

    [Fact]
    public void Stable_root_ids_survive_a_line_shift_and_line_based_ids_do_not()
    {
        var testCase = new ProviderCase("TestWorkers_LineShift_SameIds", [("Workers.cs", TwoWorkers)]);

        ProviderFixture.AssertStableIds(new WorkerProvider(), testCase);
        Assert.ThrowsAny<XunitException>(() => ProviderFixture.AssertStableIds(new WorkerProvider(WorkerProviderMode.LineBasedIds), testCase));
    }

    [Fact]
    public void Not_checked_result_without_a_diagnostic_is_refused()
    {
        var testCase = new ProviderCase("TestWorkers_SilentNotChecked_Refused", [("Workers.cs", TwoWorkers)]);

        Assert.Throws<ArgumentException>(() => ProviderFixture.Run(new WorkerProvider(WorkerProviderMode.NotCheckedWithoutDiagnostic), testCase));
    }

    [Fact]
    public void Case_name_must_be_provider_scenario_outcome()
    {
        var testCase = new ProviderCase("two workers", [("Workers.cs", TwoWorkers)]) { ExpectedRoots = TwoWorkerRoots };

        Assert.ThrowsAny<XunitException>(() => ProviderFixture.Run(new WorkerProvider(), testCase));
        Assert.ThrowsAny<XunitException>(() => ProviderFixture.Run(new WorkerProvider(), testCase with { Name = "testWorkers_Two_Roots" }));
        Assert.ThrowsAny<XunitException>(() => ProviderFixture.Run(new WorkerProvider(), testCase with { Name = "TestWorkers_TwoRoots" }));
    }

    private enum WorkerProviderMode
    {
        Normal,
        Reversed,
        LineBasedIds,
        NotCheckedWithoutDiagnostic
    }

    /// <summary>A root per concrete <c>BackgroundService</c> subclass, resolved by exact assembly identity.</summary>
    private sealed class WorkerProvider(WorkerProviderMode mode = WorkerProviderMode.Normal) : IExecutionRootProvider
    {
        private static readonly SupportedAssemblyVersion Hosting =
            SupportedAssemblyVersion.Framework(StubAssemblies.HOSTING_ABSTRACTIONS);

        public string ProviderId => "test-workers";

        public IReadOnlyList<SupportedAssemblyVersion> SupportedAssemblyVersions => [Hosting];

        public RootDiscoveryResult Discover(RootDiscoveryContext context)
        {
            if (mode == WorkerProviderMode.NotCheckedWithoutDiagnostic)
                return new RootDiscoveryResult(RootDiscoveryStatus.NotChecked, [], []);

            var roots = new List<ExecutionRootDescriptor>();
            var diagnostics = new List<RootDiscoveryDiagnostic>();
            foreach (var compilation in context.Compilations)
            {
                var assembly = ExactSymbols.FindReferencedAssembly(compilation, Hosting.AssemblyName);
                if (assembly is null)
                    continue;
                if (!ExactSymbols.IsInRange(assembly, Hosting))
                {
                    diagnostics.Add(new RootDiscoveryDiagnostic(ProviderId, RootDiscoveryDiagnosticCode.UnsupportedAssemblyVersion,
                        compilation.AssemblyName ?? "", $"{assembly.Identity.Name} {assembly.Identity.Version} is outside the supported range.", []));
                    continue;
                }

                var backgroundService = ExactSymbols.FindType(compilation, Hosting, "Microsoft.Extensions.Hosting.BackgroundService");
                foreach (var type in Types(compilation.Assembly.GlobalNamespace))
                {
                    if (type.IsAbstract || !DerivesFrom(type, backgroundService))
                        continue;
                    var method = type.GetMembers("ExecuteAsync").OfType<IMethodSymbol>().Single();
                    roots.Add(Root(context, compilation, assembly, method));
                }
            }

            if (mode == WorkerProviderMode.Reversed)
                roots.Reverse();
            return new RootDiscoveryResult(
                diagnostics.Count == 0 ? RootDiscoveryStatus.Checked : RootDiscoveryStatus.NotChecked, roots, diagnostics);
        }

        private ExecutionRootDescriptor Root(RootDiscoveryContext context, Compilation compilation, IAssemblySymbol hosting,
                                             IMethodSymbol method)
        {
            var lineSpan = method.Locations[0].GetLineSpan();
            var anchor = mode == WorkerProviderMode.LineBasedIds
                ? $"line{lineSpan.StartLinePosition.Line}"
                : method.GetDocumentationCommentId();
            var symbol = SymbolNames.Method(method);
            return new ExecutionRootDescriptor(
                $"test-worker:{compilation.AssemblyName}:{anchor}",
                "test-worker",
                ProviderId,
                new RootEntry($"body:{compilation.AssemblyName}:{method.GetDocumentationCommentId()}", symbol, $"worker {symbol}",
                              new SourceSpan(Path.GetRelativePath(context.RootDirectory, lineSpan.Path).Replace('\\', '/'),
                                             lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1,
                                             lineSpan.EndLinePosition.Line + 1, lineSpan.EndLinePosition.Character + 1)),
                new InstanceBindings(ReceiverKind.HostedService,
                    method.Parameters.Select(parameter => new ParameterBinding(parameter.Name, SymbolNames.Type(parameter.Type),
                                                                               ParameterBindingKind.Unsupported)).ToArray()),
                new InvocationPolicy(Multiplicity.AtMostOnce, SelfOverlap.Serialized, context.ScopeId),
                [],
                [],
                [new DiscoveryEvidence("E1", "base-type", "derives from BackgroundService", null)],
                [$"{hosting.Identity.Name}/{hosting.Identity.Version}"]);
        }

        private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol @namespace) =>
            @namespace.GetTypeMembers().Concat(@namespace.GetNamespaceMembers().SelectMany(Types));

        private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol? baseType)
        {
            for (var current = type.BaseType; current is not null && baseType is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType))
                    return true;
            }

            return false;
        }
    }
}
