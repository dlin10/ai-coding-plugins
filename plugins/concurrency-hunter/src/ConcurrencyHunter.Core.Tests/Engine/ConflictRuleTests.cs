using System.Text.Json;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ConflictRuleTests
{
    private const string Shared = """
        public static class State { public static int Value; }
        public static class Gates { public static readonly object G = new(); }
        public interface IFirst { }
        public interface ISecond { }
        public sealed class Gate : IFirst, ISecond { public int Value; }
        public sealed class Ledger { public string? Entry; }

        """;

    [Fact]
    public async Task Read_modify_write_against_itself_is_DCA1002()
    {
        var result = await Analyze(Shared + """
            public class HitsController : ControllerBase
            {
                private static int _hits;
                public void Post() => _hits++;
            }
            """ + Startup());

        var finding = Assert.Single(result.Findings);
        Assert.Equal("DCA1002", finding.RuleId);
        Assert.Equal((AccessOperation.ReadModifyWrite, AccessOperation.ReadModifyWrite), (finding.AccessA.Operation, finding.AccessB.Operation));
        Assert.Equal(["A reads `_hits`", "B writes `_hits`", "A writes a value computed from its stale read, overwriting B's update"],
                     finding.Scenario);
        Assert.Equal(["Path feasibility is not analyzed in this version."], finding.Uncertainty);
    }

    /// <summary>An update made atomically is an update all the same: a plain read-modify-write writes back what it read and
    /// overwrites it, so the sequence loses it (R1, TD-072).</summary>
    [Fact]
    public async Task A_read_modify_write_against_an_atomic_write_is_DCA1002()
    {
        var result = await Analyze(Shared + """
            public sealed class Gauge { public int Level; }
            public class BumpController : ControllerBase
            {
                private readonly Gauge _gauge;
                public BumpController(Gauge gauge) => _gauge = gauge;
                public void Post() => _gauge.Level = _gauge.Level + 1;
            }
            public sealed class ResetWorker : BackgroundService
            {
                private readonly Gauge _gauge;
                public ResetWorker(Gauge gauge) => _gauge = gauge;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    Volatile.Write(ref _gauge.Level, 0);
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Gauge>(); services.AddHostedService<ResetWorker>();"));

        var mixed = Assert.Single(result.Findings, finding => finding.AccessA.Operation == AccessOperation.AtomicWrite ||
                                                              finding.AccessB.Operation == AccessOperation.AtomicWrite);
        Assert.Equal("DCA1002", mixed.RuleId);
    }

    [Fact]
    public async Task Write_and_read_are_DCA1001_in_one_group()
    {
        var result = await Analyze(Shared + """
            public class VisitorController : ControllerBase
            {
                private static string? _lastVisitor;
                public void Post(string name) => _lastVisitor = name;
                public string? Get() => _lastVisitor;
            }
            """ + Startup());

        var group = Assert.Single(result.Groups);
        Assert.Equal("DCA1001", group.RuleId);
        Assert.Equal(["F1", "F2"], group.FindingIds);
        var writeRead = Assert.Single(result.Findings, finding => finding.AccessB.Operation != finding.AccessA.Operation);
        Assert.Contains("reads `_lastVisitor` at the same time", string.Join("|", writeRead.Scenario), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_modify_write_against_a_read_is_DCA1001_and_groups_by_rule()
    {
        var result = await Analyze(Shared + """
            public class HitsController : ControllerBase
            {
                private static int _hits;
                public void Post() => _hits++;
                public int Get() => _hits;
            }
            """ + Startup());

        Assert.Equal(["DCA1001", "DCA1002"], result.Groups.Select(group => group.RuleId).Order());
        var againstRead = Assert.Single(result.Findings, finding => finding.RuleId == "DCA1001");
        Assert.Contains(AccessOperation.Read, new[] { againstRead.AccessA.Operation, againstRead.AccessB.Operation });
    }

    [Fact]
    public async Task One_implementation_under_two_singleton_registrations_keeps_two_findings_in_two_groups()
    {
        var result = await Analyze(Shared + """
            public class GateController : ControllerBase
            {
                private readonly IFirst _first;
                private readonly ISecond _second;
                public GateController(IFirst first, ISecond second) { _first = first; _second = second; }
                public void Post() { ((Gate)_first).Value = 1; ((Gate)_second).Value = 2; }
            }
            """ + Startup("services.AddSingleton<IFirst, Gate>(); services.AddSingleton<ISecond, Gate>();"));

        Assert.Equal(2, result.Findings.Count);
        Assert.Equal(2, result.Groups.Select(group => group.Resource.Identity).Distinct().Count());
        Assert.Equal(2, result.Groups.Select(group => group.Fingerprint).Distinct().Count());
        Assert.Equal(2, result.Findings.Select(finding => finding.Fingerprint).Distinct().Count());
        Assert.All(result.Groups, group => Assert.Equal("di:Gate@Singleton", group.Resource.Region));
    }

    [Fact]
    public async Task Lock_on_a_per_request_object_is_a_different_identity_DCA1003()
    {
        var result = await Analyze(Shared + """
            public class GateController : ControllerBase
            {
                private readonly object _gate = new object();
                public void Post() { lock (_gate) { State.Value = 1; } }
            }
            """ + Startup());

        var finding = Assert.Single(result.Findings);
        Assert.Equal("DCA1003", finding.RuleId);
        Assert.Same(finding.AccessA, finding.AccessB);
        Assert.Equal("different-identity", finding.ProtectionResult);
        Assert.Single(finding.AccessA.HeldProtection);
        Assert.Empty(finding.AccessA.HeldProtectionIds);
    }

    [Fact]
    public async Task Common_singleton_lock_suppresses_controller_and_worker()
    {
        var result = await Analyze(Shared + """
            public class LedgerController(Ledger ledger) : ControllerBase
            {
                public void Post() { lock (ledger) { ledger.Entry = "request"; } }
            }
            public sealed class LedgerWorker(Ledger ledger) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { lock (ledger) { ledger.Entry = null; } return Task.CompletedTask; }
            }
            """ + Startup("services.AddSingleton<Ledger>().AddHostedService<LedgerWorker>();"));

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.Pairs.Suppressed);
    }

    [Fact]
    public async Task Locks_on_two_singleton_registrations_of_one_implementation_do_not_suppress()
    {
        var result = await Analyze(Shared + """
            public class FirstController(IFirst gate) : ControllerBase
            {
                public void Post() { lock (gate) { State.Value = 1; } }
            }
            public class SecondController(ISecond gate) : ControllerBase
            {
                public void Post() { lock (gate) { State.Value = 2; } }
            }
            """ + Startup("services.AddSingleton<IFirst, Gate>(); services.AddSingleton<ISecond, Gate>();"));

        var across = Assert.Single(result.Findings, finding => finding.AccessA.Root.RootId != finding.AccessB.Root.RootId);
        Assert.Equal("different-identity", across.ProtectionResult);
        Assert.Equal(2, result.Pairs.Suppressed);
    }

    [Fact]
    public async Task One_side_under_a_lock_is_partial()
    {
        var result = await Analyze(Shared + """
            public class StateController : ControllerBase
            {
                public void Locked() { lock (Gates.G) { State.Value = 1; } }
                public void Unlocked() { State.Value = 2; }
            }
            """ + Startup());

        var across = Assert.Single(result.Findings, finding => finding.AccessA.Root.RootId != finding.AccessB.Root.RootId);
        Assert.Equal("partial", across.ProtectionResult);
        Assert.Contains("A holds", Assert.Single(across.Evidence, item => item.Kind == "protection").Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_scopes_sharing_a_library_static_never_pair()
    {
        const string controller = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Routing;
            using Microsoft.Extensions.DependencyInjection;
            public class SyncController : ControllerBase { public void Post() => Shared.LastSync.Source = "web"; }
            public static class Startup
            {
                public static void Configure(IServiceCollection services, IEndpointRouteBuilder app) { services.AddControllers(); app.MapControllers(); }
            }
            """;
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind> { ["Web"] = OutputKind.ConsoleApplication, ["Api"] = OutputKind.ConsoleApplication },
                ProjectReferences = [("Web", "Library"), ("Api", "Library")]
            },
            ("Library", "LastSync.cs", "namespace Shared { public static class LastSync { public static string? Source; } }"),
            ("Web", "Program.cs", "System.Console.WriteLine();"),
            ("Web", "SyncController.cs", controller),
            ("Api", "Program.cs", "System.Console.WriteLine();"),
            ("Api", "SyncController.cs", controller));

        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);

        Assert.Equal(["Api", "Web"], result.Scopes.Select(scope => scope.Id));
        Assert.Equal(2, result.Groups.Count);
        Assert.All(result.Findings, finding => Assert.Equal(finding.AccessA.Resource.Scope, finding.AccessB.Resource.Scope));
        Assert.Equal(["Api", "Web"], result.Groups.Select(group => group.Resource.Scope));
    }

    [Fact]
    public async Task Same_named_singletons_from_two_assemblies_never_pair()
    {
        static string Library(string name) => Usings + $$"""
            namespace Shared { public sealed class Ledger : LedgerBase { } }
            public class {{name}}Controller(Shared.Ledger ledger) : ControllerBase { public void Post() => ledger.Entry = "{{name}}"; }
            public static class {{name}}Registrations
            {
                public static IServiceCollection Add{{name}}(this IServiceCollection services) => services.AddSingleton<Shared.Ledger>();
            }
            """;
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind> { ["App"] = OutputKind.ConsoleApplication },
                ProjectReferences = [("App", "Alpha"), ("App", "Beta"), ("Alpha", "Common"), ("Beta", "Common")]
            },
            ("Common", "LedgerBase.cs", "namespace Shared { public class LedgerBase { public string? Entry; } }"),
            ("Alpha", "Alpha.cs", Library("Alpha")),
            ("Beta", "Beta.cs", Library("Beta")),
            ("App", "Program.cs", Usings + """
                System.Console.WriteLine();
                public static class Startup
                {
                    public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                    {
                        services.AddControllers();
                        app.MapControllers();
                        services.AddAlpha();
                        services.AddBeta();
                    }
                }
                """));

        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);

        var findings = result.Findings.Where(finding => finding.Resource.Member.Name == "Entry").ToArray();
        Assert.Equal(2, findings.Length);
        Assert.All(findings, finding => Assert.Equal(finding.AccessA.Root.RootId, finding.AccessB.Root.RootId));
        Assert.Equal(2, findings.Select(finding => finding.GroupId).Distinct().Count());
        Assert.Equal(2, result.Groups.Count(group => group.Resource.Member.Name == "Entry"));
        Assert.DoesNotContain(result.Findings, finding => finding.AccessA.Root.RootId != finding.AccessB.Root.RootId);
    }

    [Fact]
    public async Task Static_field_of_closed_generics_over_same_named_types_never_pair()
    {
        static string Library(string name) => Usings + $$"""
            namespace Ns { public sealed class Payload { } }
            public class {{name}}Controller : ControllerBase { public void Post() => Shared.Box<Ns.Payload>.Value = new Ns.Payload(); }
            """;
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind> { ["App"] = OutputKind.ConsoleApplication },
                ProjectReferences = [("App", "Alpha"), ("App", "Beta"), ("Alpha", "Common"), ("Beta", "Common")]
            },
            ("Common", "Box.cs", "namespace Shared { public static class Box<T> { public static T? Value; } }"),
            ("Alpha", "Alpha.cs", Library("Alpha")),
            ("Beta", "Beta.cs", Library("Beta")),
            ("App", "Program.cs", Usings + """
                System.Console.WriteLine();
                public static class Startup
                {
                    public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                    {
                        services.AddControllers();
                        app.MapControllers();
                    }
                }
                """));

        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);

        var findings = result.Findings.Where(finding => finding.Resource.Member.Name == "Value").ToArray();
        Assert.Equal(2, findings.Length);
        Assert.All(findings, finding => Assert.Equal(finding.AccessA.Root.RootId, finding.AccessB.Root.RootId));
        Assert.All(findings, finding => Assert.Equal("static:Shared.Box<Ns.Payload>", finding.Resource.Region));
        Assert.Equal(2, findings.Select(finding => finding.GroupId).Distinct().Count());
        Assert.Equal(2, result.Groups.Count(group => group.Resource.Member.Name == "Value"));
        Assert.DoesNotContain(result.Findings, finding => finding.AccessA.Root.RootId != finding.AccessB.Root.RootId);
    }

    [Fact]
    public async Task Groups_are_ordered_by_scope_then_assembly_then_resource()
    {
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind> { ["Beta"] = OutputKind.ConsoleApplication },
                ProjectReferences = [("Beta", "Alpha")]
            },
            ("Alpha", "Zeta.cs", "public static class Zeta { public static int Value; }"),
            ("Beta", "Program.cs", "System.Console.WriteLine();"),
            ("Beta", "Controller.cs", Usings + """
                public static class Alpha { public static int Value; }
                public class WritesController : ControllerBase { public void Post() { Alpha.Value = 1; Zeta.Value = 2; } }
                """ + Startup()));

        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);

        Assert.Equal([("Alpha", "static:Zeta"), ("Beta", "static:Alpha")],
                     result.Groups.Select(group => (group.Resource.Assembly, group.Resource.Region)));
        Assert.Equal(["G1", "G2"], result.Groups.Select(group => group.GroupId));
        Assert.Equal(["F1", "F2"], result.Groups.SelectMany(group => group.FindingIds));
    }

    [Fact]
    public async Task Two_lifecycle_roots_of_one_instance_carry_the_lifecycle_uncertainty()
    {
        var result = await Analyze(Shared + """
            public sealed class ProgressWorker : BackgroundService
            {
                private string? _lastItem;
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { _lastItem = "item"; return Task.CompletedTask; }
                public override Task StopAsync(CancellationToken cancellationToken) { GC.KeepAlive(_lastItem); return Task.CompletedTask; }
            }
            public class StateController : ControllerBase { public void Post() => State.Value = 1; }
            public sealed class StateWorker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { State.Value = 2; return Task.CompletedTask; }
            }
            """ + Startup("services.AddHostedService<ProgressWorker>().AddHostedService<StateWorker>();"));

        var lifecycle = Assert.Single(result.Findings, finding => finding.Resource.Member.Name == "_lastItem");
        Assert.Equal(["Path feasibility is not analyzed in this version.",
                      "Ordering between lifecycle methods of one hosted service is not analyzed in this version."], lifecycle.Uncertainty);
        Assert.All(result.Findings.Where(finding => finding.Resource.Member.Name == "Value"),
                   finding => Assert.DoesNotContain(finding.Uncertainty, item => item.StartsWith("Ordering", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Unresolved_registration_uncertainty_reaches_the_finding()
    {
        var result = await Analyze(Shared + """
            public class LedgerController(Ledger ledger) : ControllerBase
            {
                public void Post() => ledger.Entry = "x";
            }
            """ + Startup("services.AddSingleton<Ledger>(); Type type = typeof(Gate); services.AddScoped(type);"));

        var finding = Assert.Single(result.Findings);
        Assert.Contains(finding.Uncertainty, item => item.StartsWith("An unresolved registration at Case.cs:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evidence_names_region_scope_binding_overlap_and_holdings()
    {
        var result = await Analyze(Shared + """
            public class LedgerController(Ledger ledger) : ControllerBase
            {
                public void Post() { lock (Gates.G) { ledger.Entry = "x"; } }
                public string? Get() => ledger.Entry;
                public void Put() => ledger.Entry = "y";
            }
            """ + Startup("services.AddSingleton<Ledger>();"));

        var across = Assert.Single(result.Findings, finding => finding.AccessA.Symbol == "LedgerController.Get()" &&
                                                               finding.AccessB.Symbol == "LedgerController.Post()");
        var evidence = across.Evidence.ToDictionary(item => item.Id[(item.Id.IndexOf('.') + 1)..], item => item);
        Assert.Equal(["A", "B", "O", "P", "R", "S"], evidence.Keys.Order());
        Assert.Equal(["access-a", "access-b", "resource", "overlap", "protection", "scenario"],
                     across.Evidence.Select(item => item.Kind));
        Assert.Contains("region di:Ledger@Singleton of scope solution", evidence["R"].Text, StringComparison.Ordinal);
        Assert.Contains("registers Ledger at Case.cs:", evidence["R"].Text, StringComparison.Ordinal);
        Assert.Contains("may run concurrently in scope solution", evidence["O"].Text, StringComparison.Ordinal);
        Assert.Contains("alloc:Gates..cctor()#object", evidence["P"].Text, StringComparison.Ordinal);
        Assert.Contains("no protection", evidence["P"].Text, StringComparison.Ordinal);

        var self = Assert.Single(result.Findings, finding => ReferenceEquals(finding.AccessA, finding.AccessB));
        Assert.Contains("may run concurrently with itself in scope solution", self.Evidence.Single(item => item.Kind == "overlap").Text,
                        StringComparison.Ordinal);
    }

    /// <summary>An open-region pair is reported on the closed side's resource, so the resource evidence must name that region's
    /// ownership, not the open side's.</summary>
    [Fact]
    public void Resource_evidence_names_the_ownership_of_the_reported_region()
    {
        var openResource = FindingTestData.Resource("static:Cache<T>", "Last") with { RegionId = "static:Fixture:Cache<Fixture:T>" };
        var closedResource = FindingTestData.Resource("static:Cache<Order>", "Last") with { RegionId = "static:Fixture:Cache<Fixture:Order>" };
        var open = FindingTestData.Access(openResource, AccessOperation.Write, "root-a", "Access.Store<T>(object)",
                                          new SourceSpan("Case.cs", 21, 1, 21, 40)) with
        {
            Ownership = OwnershipKind.Unknown,
            OwnershipEvidence = ["static:Cache<T> comes from a merged context."]
        };
        var closed = FindingTestData.Access(closedResource, AccessOperation.Read, "root-b", "CacheController.Get()",
                                            new SourceSpan("Case.cs", 25, 1, 25, 40)) with
        {
            Ownership = OwnershipKind.Shared,
            OwnershipEvidence = ["static:Cache<Order> is static storage."]
        };
        var pair = new AccessPair(open, closed, PairProtection.UNPROTECTED) { Resource = closedResource };

        var finding = Assert.Single(ConflictFindings.Create([pair], CancellationToken.None).Findings);

        Assert.Equal(closedResource, finding.Resource);
        // Sides are in site order: the read sorts before the write.
        Assert.Equal((closed, open), (finding.AccessA, finding.AccessB));
        var resource = Assert.Single(finding.Evidence, item => item.Kind == "resource").Text;
        Assert.Contains("ownership: Shared (static:Cache<Order> is static storage.)", resource, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown", resource, StringComparison.Ordinal);
        Assert.Equal(OwnershipKind.Shared, Assert.Single(ConflictFindings.Create([pair], CancellationToken.None).Groups).Ownership);
    }

    [Fact]
    public async Task Analysis_is_deterministic()
    {
        const string source = Shared + """
            public class VisitorController : ControllerBase
            {
                private static string? _lastVisitor;
                public void Post(string name) => _lastVisitor = name;
                public string? Get() => _lastVisitor;
                public void Increment() => State.Value++;
            }
            """;

        var first = await Analyze(source + Startup());
        var second = await Analyze(source + Startup());

        Assert.Equal(JsonSerializer.Serialize(first.Findings), JsonSerializer.Serialize(second.Findings));
        Assert.Equal(JsonSerializer.Serialize(first.Groups), JsonSerializer.Serialize(second.Groups));
    }

    [Fact]
    public async Task Duplicate_stable_id_from_a_second_provider_is_dropped_with_DiscoveryFailed()
    {
        const string source = Shared + """
            public class StateController : ControllerBase { public void Post() => State.Value = 1; }
            """;
        var builtIn = await Analyze(source + Startup());
        var registry = new ProviderRegistry(ProviderRegistry.BuiltIn.Providers.Append(new EchoProvider()));

        var result = await Analyze(source + Startup(), registry);

        Assert.Equal(builtIn.Roots.Count, result.Roots.Count);
        Assert.Equal(builtIn.Findings.Select(finding => finding.Fingerprint), result.Findings.Select(finding => finding.Fingerprint));
        var coverage = Assert.Single(result.Coverage);
        Assert.Equal(0, coverage.RootsPerProvider["echo"]);
        Assert.Contains(coverage.Diagnostics, diagnostic => diagnostic.StartsWith("echo: DiscoveryFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Coverage_counts_roots_registrations_skips_and_pairs()
    {
        var result = await Analyze(Shared + """
            public static class Unused { public static void Sweep() { Action clear = () => State.Value = 0; clear(); } }
            public class LedgerController(Ledger ledger) : ControllerBase
            {
                public void Post() { ledger.Entry = "x"; new Ledger().Entry = "local"; }
            }
            """ + Startup("services.AddSingleton<Ledger>();"));

        var coverage = Assert.Single(result.Coverage);
        // A member no root reaches is inventory with its nested bodies.
        Assert.Contains("body:Fixture:M:Unused.Sweep", coverage.OutsideLoweredSet);
        Assert.Contains("body:Fixture:M:Unused.Sweep#lambda1", coverage.OutsideLoweredSet);
        Assert.Equal("solution", coverage.ScopeId);
        Assert.Equal(1, coverage.RootsPerProvider["aspnetcore"]);
        Assert.Equal(0, coverage.RootsPerProvider["hosting"]);
        Assert.Equal(1, coverage.Registrations);
        Assert.True(coverage.Skips[CoverageCounters.REACHABLE_BODIES] > 0);
        Assert.Contains(ConcurrencyHunter.Scopes.ProcessScope.NO_EXECUTABLE_DIAGNOSTIC, coverage.Diagnostics);
        Assert.Equal(3, result.Pairs.Comparisons);
        Assert.Equal(1, result.Pairs.Skips[InterproceduralPairing.SKIP_CONFINED]);
    }

    [Fact]
    public async Task Wildcard_resource_lowers_resource_identity_to_Medium()
    {
        var chain = string.Concat(Enumerable.Range(1, 11).Select(level => $"public sealed class Level{level} {{ public Level{level + 1} Next {{ get; }} = new(); }}\n"));
        var result = await Analyze(Shared + chain + """
            public sealed class Level12 { public string? Value { get; set; } }
            public sealed class DeepChain
            {
                public Level1 First { get; } = new();
                public void Write(string value) => First.Next.Next.Next.Next.Next.Next.Next.Next.Next.Next.Next.Value = value;
            }
            public class DeepController(DeepChain chain) : ControllerBase { public void Put(string value) => chain.Write(value); }
            """ + Startup("services.AddSingleton<DeepChain>();"));

        var wildcard = result.Findings.Where(item => item.Resource.IsWildcard).ToArray();
        Assert.NotEmpty(wildcard);
        Assert.All(wildcard, finding => Assert.Equal(new FindingConfidence("Medium", 75, new ConfidenceComponents(10, 20, 20, 20, 5)), finding.Confidence));
        Assert.All(wildcard, finding => Assert.Contains("The resource is a wildcard: an access path longer than the analysis limit was collapsed.", finding.Uncertainty));
        Assert.All(result.Groups.Where(group => group.Resource.IsWildcard), group => Assert.Equal("Medium", group.ConfidenceLabel));
        Assert.DoesNotContain(result.Findings, finding => !finding.Resource.IsWildcard && finding.Confidence.Label != "High");
    }

    [Fact]
    public async Task Merged_contexts_are_uncertainty_without_a_penalty()
    {
        var result = await Analyze(Shared + """
            public static class Cache<T> { public static object? Last; }
            public static class Grow
            {
                public static void F<T>(object value, int depth) { Cache<T>.Last = value; if (depth > 0) F<System.Collections.Generic.List<T>>(value, depth - 1); }
            }
            public class GrowController : ControllerBase { public void Post() => Grow.F<int>(new object(), 3); }
            """ + Startup());

        var merged = result.Findings.Where(finding => finding.Uncertainty.Contains("Contexts of Grow.F<T>(object, int) were merged; the objects involved may be more than one."))
                           .ToArray();
        Assert.NotEmpty(merged);
        Assert.All(merged, finding => Assert.Equal(new FindingConfidence("High", 90, new ConfidenceComponents(25, 20, 20, 20, 5)), finding.Confidence));
    }

    [Fact]
    public async Task Evidence_names_ownership_and_its_evidence_chain()
    {
        var result = await Analyze(Shared + """
            public sealed class Tally { private int _count; public void Bump() => _count = _count + 1; }
            public class LedgerController(Ledger ledger, Tally tally) : ControllerBase
            {
                public void Post() { ledger.Entry = "x"; tally.Bump(); }
            }
            """ + Startup("services.AddSingleton<Ledger>(); services.AddSingleton<Tally>();"));

        var entry = Assert.Single(result.Findings, finding => finding.Resource.Member.Name == "Entry");
        var resource = Assert.Single(entry.Evidence, item => item.Kind == "resource").Text;
        Assert.Contains("in region di:Ledger@Singleton of scope solution; ownership: Shared (di:Ledger@Singleton is one container object for the whole scope (singleton).)",
                        resource, StringComparison.Ordinal);
        Assert.Contains("binding evidence: LedgerController.ledger holds constructor parameter ledger at Case.cs:", resource, StringComparison.Ordinal);

        var count = Assert.Single(result.Findings, finding => finding.Resource.Member.Name == "_count");
        var access = Assert.Single(count.Evidence, item => item.Kind == "access-a").Text;
        var read = Assert.Single(count.AccessA.ReadSources);
        Assert.Contains($"; reads it at {read.Source.Path}:{read.Source.StartLine} in Tally.Bump(); holds", access, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_interface_hosted_start_pairs_with_an_action_through_the_analyzer()
    {
        var result = await Analyze(Shared + """
            public sealed class LedgerWarmup(Ledger ledger) : IHostedService
            {
                Task IHostedService.StartAsync(CancellationToken cancellationToken) { ledger.Entry = "warm"; return Task.CompletedTask; }
                Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public class LedgerController(Ledger ledger) : ControllerBase
            {
                public string? Get() => ledger.Entry;
            }
            """ + Startup("services.AddSingleton<Ledger>().AddHostedService<LedgerWarmup>();"));

        var finding = Assert.Single(result.Findings);
        Assert.Equal(["aspnetcore", "hosting"], new[] { finding.AccessA.Root.ProviderId, finding.AccessB.Root.ProviderId }.Order());
        Assert.Contains(new[] { finding.AccessA, finding.AccessB },
                        access => access.Symbol == "LedgerWarmup.Microsoft.Extensions.Hosting.IHostedService.StartAsync(CancellationToken)" &&
                                  access.Operation == AccessOperation.Write);
        Assert.Equal("di:Ledger@Singleton", finding.Resource.Region);
    }

    [Fact]
    public async Task Same_named_executables_with_different_bodies_are_lowered_separately()
    {
        const string startup = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Routing;
            using Microsoft.Extensions.DependencyInjection;
            public static class State { public static int Value; }
            public static class Startup
            {
                public static void Configure(IServiceCollection services, IEndpointRouteBuilder app) { services.AddControllers(); app.MapControllers(); }
            }
            """;
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind> { ["First"] = OutputKind.ConsoleApplication, ["Second"] = OutputKind.ConsoleApplication },
                ProjectAssemblyNames = new Dictionary<string, string> { ["First"] = "App", ["Second"] = "App" }
            },
            ("First", "Program.cs", "System.Console.WriteLine();"),
            ("First", "Startup.cs", startup),
            ("First", "SyncController.cs", "public class SyncController : Microsoft.AspNetCore.Mvc.ControllerBase { public void Post() { State.Value = 1; } }"),
            ("Second", "Program.cs", "System.Console.WriteLine();"),
            ("Second", "Startup.cs", startup),
            ("Second", "SyncController.cs", "public class SyncController : Microsoft.AspNetCore.Mvc.ControllerBase { public void Post() { var local = 1; System.GC.KeepAlive(local); } }"));

        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);

        Assert.Equal(["App@First/First.csproj", "App@Second/Second.csproj"], result.Scopes.Select(scope => scope.Id));
        var finding = Assert.Single(result.Findings);
        Assert.Equal("App@First/First.csproj", finding.Resource.Scope);
        Assert.DoesNotContain(result.Accesses, access => access.Resource.Scope == "App@Second/Second.csproj");
    }

    [Fact]
    public async Task Read_inside_an_interpolated_string_pairs_with_a_writer()
    {
        var result = await Analyze(Shared + """
            public class StateController : ControllerBase
            {
                public void Post() => State.Value = 1;
                public string Get() => $"value={State.Value}";
            }
            """ + Startup());

        var finding = Assert.Single(result.Findings, item => item.AccessA.Operation == AccessOperation.Read || item.AccessB.Operation == AccessOperation.Read);
        Assert.Equal("DCA1001", finding.RuleId);
        Assert.Contains(new[] { finding.AccessA, finding.AccessB }, access => access.Symbol == "StateController.Get()");
    }

    [Fact]
    public async Task Write_under_lock_on_new_object_reports_held_protection_and_is_not_suppressed()
    {
        var result = await Analyze(Shared + """
            public class StateController : ControllerBase
            {
                public void Locked() { lock (new object()) { State.Value = 1; } }
                public void Unlocked() { State.Value = 2; }
            }
            """ + Startup());

        var across = Assert.Single(result.Findings, finding => finding.AccessA.Root.RootId != finding.AccessB.Root.RootId);
        Assert.Equal("partial", across.ProtectionResult);
        var locked = new[] { across.AccessA, across.AccessB }.Single(access => access.Symbol == "StateController.Locked()");
        var held = Assert.Single(locked.HeldProtection);
        Assert.Empty(locked.HeldProtectionIds);
        Assert.Contains(held, Assert.Single(across.Evidence, item => item.Kind == "protection").Text, StringComparison.Ordinal);
        Assert.Contains(result.Findings, finding => ReferenceEquals(finding.AccessA, finding.AccessB) && finding.AccessA.Symbol == "StateController.Locked()");
        Assert.Equal(0, result.Pairs.Suppressed);
    }

    private static async Task<AnalysisResult> Analyze(string source, ProviderRegistry? registry = null) =>
        await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Usings + source)), ROOT_DIRECTORY,
                                            registry ?? ProviderRegistry.BuiltIn, CancellationToken.None);

    /// <summary>Re-reports every controller root the ASP.NET Core provider found, under its own provider id.</summary>
    private sealed class EchoProvider : IExecutionRootProvider
    {
        public string ProviderId => "echo";

        public IReadOnlyList<SupportedAssemblyVersion> SupportedAssemblyVersions =>
            [SupportedAssemblyVersion.Framework("Microsoft.AspNetCore.Mvc.Core")];

        public RootDiscoveryResult Discover(RootDiscoveryContext context) =>
            new(RootDiscoveryStatus.Checked,
                new AspNetCoreRootProvider().Discover(context).Roots.Select(root => root with { ProviderId = ProviderId }).ToArray(),
                []);
    }
}
