using System.Runtime.CompilerServices;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Roots;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class HostingRootProviderTests
{
    private const string ONE = "multiplicity=AtMostOnce overlap=Serialized scope=scope:Fixture";
    private const string TOKEN = "parameters=[cancellationToken:RequestData]";

    private const string Usings = """
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;

        """;

    [Fact]
    public void Hosting_BackgroundServiceExecute_Root()
    {
        var result = Run(Case("""
            public sealed class Worker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            public static class Registrations { public static void Register(IServiceCollection services) => services.AddHostedService<Worker>(); }
            """) with
        {
            ExpectedRoots = [$"hosted-execute Worker.ExecuteAsync(CancellationToken) receiver=HostedService parameters=[stoppingToken:RequestData] {ONE}"]
        });

        var root = Assert.Single(result.Roots);
        Assert.Equal("hosting:execute:Fixture:T:Worker:M:Worker.ExecuteAsync(System.Threading.CancellationToken)", root.StableRootId);
        Assert.Equal("body:Fixture:M:Worker.ExecuteAsync(System.Threading.CancellationToken)", root.Entry.BodyKey);
    }

    [Fact]
    public void Hosting_StopOverrideWithoutBaseCall_Root()
    {
        Run(Case("""
            public sealed class ProgressWorker : BackgroundService
            {
                private string? _lastItem;
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { _lastItem = "item"; return Task.CompletedTask; }
                public override Task StopAsync(CancellationToken cancellationToken) { System.GC.KeepAlive(_lastItem); return Task.CompletedTask; }
            }
            public static class Registrations { public static void Register(IServiceCollection services) => services.AddHostedService<ProgressWorker>(); }
            """) with
        {
            ExpectedRoots =
            [
                $"hosted-execute ProgressWorker.ExecuteAsync(CancellationToken) receiver=HostedService parameters=[stoppingToken:RequestData] {ONE}",
                $"hosted-stop ProgressWorker.StopAsync(CancellationToken) receiver=HostedService {TOKEN} {ONE}"
            ]
        });
    }

    [Fact]
    public void Hosting_HostedServiceStartAndStop_Roots()
    {
        Run(Case("""
            public sealed class Warmup : IHostedService
            {
                public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public static class Registrations { public static void Register(IServiceCollection services) => services.AddHostedService<Warmup>(); }
            """) with
        {
            ExpectedRoots =
            [
                $"hosted-start Warmup.StartAsync(CancellationToken) receiver=HostedService {TOKEN} {ONE}",
                $"hosted-stop Warmup.StopAsync(CancellationToken) receiver=HostedService {TOKEN} {ONE}"
            ]
        });
    }

    [Fact]
    public void Hosting_LifecycleService_EveryKindRoot()
    {
        Run(Case("""
            public sealed class Lifecycle : IHostedLifecycleService
            {
                public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public static class Registrations { public static void Register(IServiceCollection services) => services.AddSingleton<IHostedService, Lifecycle>(); }
            """) with
        {
            ExpectedRoots =
            [
                $"hosted-start Lifecycle.StartAsync(CancellationToken) receiver=HostedService {TOKEN} {ONE}",
                $"hosted-stop Lifecycle.StopAsync(CancellationToken) receiver=HostedService {TOKEN} {ONE}",
                $"hosted-starting Lifecycle.StartingAsync(CancellationToken) receiver=HostedService {TOKEN} {ONE}",
                $"hosted-started Lifecycle.StartedAsync(CancellationToken) receiver=HostedService {TOKEN} {ONE}",
                $"hosted-stopping Lifecycle.StoppingAsync(CancellationToken) receiver=HostedService {TOKEN} {ONE}",
                $"hosted-stopped Lifecycle.StoppedAsync(CancellationToken) receiver=HostedService {TOKEN} {ONE}"
            ]
        });
    }

    [Fact]
    public void Hosting_ExplicitInterfaceImplementation_Root()
    {
        Run(Case("""
            public sealed class Explicit : IHostedService
            {
                Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public static class Registrations { public static void Register(IServiceCollection services) => services.AddHostedService<Explicit>(); }
            """) with
        {
            ExpectedRoots =
            [
                $"hosted-start Explicit.Microsoft.Extensions.Hosting.IHostedService.StartAsync(CancellationToken) receiver=HostedService {TOKEN} {ONE}",
                $"hosted-stop Explicit.Microsoft.Extensions.Hosting.IHostedService.StopAsync(CancellationToken) receiver=HostedService {TOKEN} {ONE}"
            ]
        });
    }

    [Fact]
    public void Hosting_OverriddenBaseExecute_OnlyMostDerivedRoot()
    {
        Run(Case("""
            public abstract class PollingWorker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            public sealed class FastWorker : PollingWorker
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Delay(1, stoppingToken);
            }
            public static class Registrations { public static void Register(IServiceCollection services) => services.AddHostedService<FastWorker>(); }
            """) with
        {
            ExpectedRoots = [$"hosted-execute FastWorker.ExecuteAsync(CancellationToken) receiver=HostedService parameters=[stoppingToken:RequestData] {ONE}"]
        });
    }

    [Fact]
    public void Hosting_ReimplementedInterface_OnlyReimplementationRoot()
    {
        Run(Case("""
            public class BaseService : IHostedService
            {
                public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public sealed class DerivedService : BaseService, IHostedService
            {
                public new Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public static class Registrations { public static void Register(IServiceCollection services) => services.AddHostedService<DerivedService>(); }
            """) with
        {
            ExpectedRoots =
            [
                $"hosted-start DerivedService.StartAsync(CancellationToken) receiver=HostedService {TOKEN} {ONE}",
                $"hosted-stop BaseService.StopAsync(CancellationToken) receiver=HostedService {TOKEN} {ONE}"
            ]
        });
    }

    [Fact]
    public void Hosting_UnregisteredBackgroundService_NoRoots()
    {
        Run(Case("""
            public sealed class Worker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            """));
    }

    [Fact]
    public void Hosting_DoubleRegistration_UnknownPolicy()
    {
        Run(Case("""
            public sealed class Worker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            public static class Registrations
            {
                public static void Register(IServiceCollection services) =>
                    services.AddHostedService<Worker>().AddSingleton<IHostedService, Worker>();
            }
            """) with
        {
            ExpectedRoots =
            [
                "hosted-execute Worker.ExecuteAsync(CancellationToken) receiver=HostedService parameters=[stoppingToken:RequestData] " +
                "multiplicity=Unknown overlap=Unknown scope=scope:Fixture"
            ],
            ExpectedDiagnostics = ["UnresolvedBinding Worker"]
        });
    }

    [Fact]
    public void Hosting_ConstructedGenericHostedService_Unsupported()
    {
        Run(Case("""
            public sealed class Worker<T> : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            public static class Registrations { public static void Register(IServiceCollection services) => services.AddHostedService<Worker<int>>(); }
            """) with
        {
            ExpectedDiagnostics = ["UnsupportedPattern Worker<int>"]
        });
    }

    [Fact]
    public void Hosting_ReceiverWithDiArguments_Root()
    {
        var result = Run(Case("""
            public sealed class Ledger { public string? LastEntry { get; set; } }
            public sealed class LedgerResetWorker : BackgroundService
            {
                private readonly Ledger _ledger;
                public LedgerResetWorker(Ledger ledger) => _ledger = ledger;
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { _ledger.LastEntry = null; return Task.CompletedTask; }
            }
            public static class Registrations
            {
                public static void Register(IServiceCollection services) => services.AddSingleton<Ledger>().AddHostedService<LedgerResetWorker>();
            }
            """) with
        {
            ExpectedRoots = [$"hosted-execute LedgerResetWorker.ExecuteAsync(CancellationToken) receiver=HostedService parameters=[stoppingToken:RequestData] {ONE}"]
        });

        Assert.Contains(Assert.Single(result.Roots).DiscoveryEvidence, evidence => evidence.Kind == "registration" && evidence.Source!.Path == "Case.cs");
    }

    [Fact]
    public void Hosting_RegisteredTypeWithoutLifecycleBody_Unsupported()
    {
        Run(Case("""
            public sealed class Quiet : IHostedService
            {
                public extern Task StartAsync(CancellationToken cancellationToken);
                public extern Task StopAsync(CancellationToken cancellationToken);
            }
            public static class Registrations { public static void Register(IServiceCollection services) => services.AddHostedService<Quiet>(); }
            """) with
        {
            ExpectedDiagnostics = ["UnsupportedPattern Quiet"]
        });
    }

    [Fact]
    public void Hosting_Version7_NotChecked()
    {
        Run(Case("""
            public sealed class Worker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            """) with
        {
            AssemblyVersions = new Dictionary<string, int> { [StubAssemblies.HOSTING_ABSTRACTIONS] = 7 },
            ExpectedStatus = RootDiscoveryStatus.NotChecked,
            ExpectedDiagnostics = ["UnsupportedAssemblyVersion Microsoft.Extensions.Hosting.Abstractions"]
        });
    }

    [Fact]
    public void Hosting_NoHostingReference_CheckedEmpty()
    {
        var result = Run(new ProviderCase(nameof(Hosting_NoHostingReference_CheckedEmpty),
                                          [("Case.cs", "public sealed class Worker { public void Run() { } }")])
        {
            OmittedAssemblies = [StubAssemblies.HOSTING_ABSTRACTIONS]
        });

        Assert.Equal(RootDiscoveryStatus.Checked, result.Status);
    }

    [Fact]
    public void Hosting_LineShift_StableIds()
    {
        ProviderFixture.AssertStableIds(new HostingRootProvider(), Case("""
            public sealed class Worker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
                public override Task StartAsync(CancellationToken cancellationToken) => base.StartAsync(cancellationToken);
            }
            public static class Registrations { public static void Register(IServiceCollection services) => services.AddHostedService<Worker>(); }
            """));
    }

    [Fact]
    public void Hosting_SameNamedServicesInTwoProjects_KeepsBothRoots()
    {
        var compilations = DiIndexTests.TwoAssembliesWithSameNamedTypes().Projects
                                       .Select(project => project.GetCompilationAsync().GetAwaiter().GetResult()!)
                                       .ToArray();
        var index = DiIndexBuilder.Build(ProviderFixture.SCOPE_ID, compilations, @"C:\fixture", CancellationToken.None);

        var result = new HostingRootProvider().Discover(
            new RootDiscoveryContext(ProviderFixture.SCOPE_ID, compilations, @"C:\fixture", index, CancellationToken.None));

        Assert.Empty(result.Diagnostics);
        Assert.Equal(["hosting:execute:Alpha:T:Workers.SyncWorker:M:Workers.SyncWorker.ExecuteAsync(System.Threading.CancellationToken)",
                      "hosting:execute:Beta:T:Workers.SyncWorker:M:Workers.SyncWorker.ExecuteAsync(System.Threading.CancellationToken)"],
                     result.Roots.Select(root => root.StableRootId).Order(StringComparer.Ordinal));
        Assert.All(result.Roots, root => Assert.Equal(
            $"hosted-execute Workers.SyncWorker.ExecuteAsync(CancellationToken) receiver=HostedService parameters=[stoppingToken:RequestData] {ONE}",
            ProviderFixture.Format(root)));
        Assert.Equal(["Alpha", "Beta"], result.Roots.Select(root => root.Entry.Source.Path.Split('\\', '/')[0]).Order(StringComparer.Ordinal));
    }

    private static RootDiscoveryResult Run(ProviderCase testCase) => ProviderFixture.Run(new HostingRootProvider(), testCase);

    private static ProviderCase Case(string source, [CallerMemberName] string name = "") => new(name, [("Case.cs", Usings + source)]);
}
