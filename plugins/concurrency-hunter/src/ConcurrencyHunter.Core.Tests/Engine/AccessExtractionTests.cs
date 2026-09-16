using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Ir;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class AccessExtractionTests
{
    private const string Shared = """
        public sealed class Relay { public string? Target; public Relay? Inner; }
        public struct Point { public int X; }

        """;

    [Fact]
    public void Static_field_write_is_a_process_access_with_its_member_key()
    {
        var run = Analyze("""
            public class HitsController : ControllerBase
            {
                private static int _hits;
                public void Post() { _hits = 1; }
            }
            """ + Startup());

        var access = run.Single("_hits", AccessOperation.Write);
        Assert.Equal(("Fixture", "scope:Fixture", "static:HitsController"), (access.Resource.Assembly, access.Resource.Scope, access.Resource.Region));
        Assert.Equal(["_hits"], access.Resource.AccessPath);
        Assert.Equal(new MemberKey("HitsController", "_hits", IrFieldKind.Field), access.Resource.Member);
        Assert.Equal(OwnershipKind.Shared, access.Ownership);
        Assert.Equal("HitsController.Post()", access.Symbol);
        Assert.Equal(("aspnetcore", "controller-action", "scope:Fixture"), (access.Root.ProviderId, access.Root.RootKind, access.Root.Scope));
        Assert.Equal(["root", "access"], access.CodeFlow.Select(step => step.Kind));
    }

    [Fact]
    public void Access_through_a_member_inherited_from_a_closed_generic_base_is_bound()
    {
        var run = Analyze("""
            public sealed class Gate { public int Value; }
            public abstract class BaseController<T> : ControllerBase where T : class
            {
                protected readonly T _gate;
                protected BaseController(T gate) { _gate = gate; }
            }
            public class GateController : BaseController<Gate>
            {
                public GateController(Gate gate) : base(gate) { }
                public void Post() => _gate.Value = 1;
            }
            """ + Startup("services.AddSingleton<Gate>();"));

        var access = run.Single("Value", AccessOperation.Write);
        Assert.Equal("di:Gate@Singleton", access.Resource.Region);
        Assert.Equal(OwnershipKind.Shared, access.Ownership);
        Assert.Equal(0, run.Counter(CoverageCounters.NO_RECEIVER_OBJECT));
    }

    [Fact]
    public void Read_modify_write_is_one_access_and_its_load_is_not_separate()
    {
        var run = Analyze("""
            public class HitsController : ControllerBase
            {
                private static int _hits;
                public void Post() => _hits++;
            }
            """ + Startup());

        Assert.Equal(AccessOperation.ReadModifyWrite, Assert.Single(run.Of("_hits")).Operation);
    }

    [Fact]
    public void Bound_singleton_member_of_a_controller_is_a_di_access()
    {
        var run = Analyze(Shared + """
            public class RelayController : ControllerBase
            {
                private readonly Relay _relay;
                public RelayController(Relay relay) => _relay = relay;
                public void Post(string target) => _relay.Target = target;
            }
            """ + Startup("services.AddSingleton<Relay>();"));

        var access = run.Single("Target", AccessOperation.Write);
        Assert.Equal("di:Relay@Singleton", access.Resource.Region);
        Assert.Contains(access.BindingEvidence, evidence => evidence.Kind == "registration");
        Assert.Equal("receiver:RelayController", run.Single("_relay", AccessOperation.Read).Resource.Region);
        Assert.Contains(run.Accesses("_relay"), store => store is { Operation: AccessOperation.Write, IsConstructionLocal: true });
    }

    [Fact]
    public void Hosted_receiver_field_is_a_singleton_access_registered_as_hosted_service()
    {
        var run = Analyze("""
            public sealed class ProgressWorker : BackgroundService
            {
                private string? _lastItem;
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { _lastItem = "item"; return Task.CompletedTask; }
            }
            """ + Startup("services.AddHostedService<ProgressWorker>();"));

        var access = run.Single("_lastItem", AccessOperation.Write);
        Assert.Equal("di:ProgressWorker@Singleton", access.Resource.Region);
        Assert.StartsWith("di|Microsoft.Extensions.Hosting.Abstractions:Microsoft.Extensions.Hosting.IHostedService|", access.Resource.RegionId,
                          StringComparison.Ordinal);
        Assert.Contains(access.BindingEvidence, evidence => evidence.Text.StartsWith("AddHostedService", StringComparison.Ordinal));
        Assert.Equal("hosting", access.Root.ProviderId);
    }

    [Fact]
    public void From_services_parameter_bound_to_a_singleton_is_a_di_access()
    {
        var run = Analyze(Shared + """
            [ApiController]
            public class RelayController : ControllerBase
            {
                public void Post([FromServices] Relay relay, string target) => relay.Target = target;
            }
            """ + Startup("services.AddSingleton<Relay>();"));

        Assert.Equal("di:Relay@Singleton", run.Single("Target", AccessOperation.Write).Resource.Region);
    }

    [Fact]
    public void From_services_parameter_captured_by_a_lambda_inside_a_local_function_is_a_di_access()
    {
        var run = Analyze(Shared + """
            public class RelayController : ControllerBase
            {
                public void Post([FromServices] Relay relay, string target)
                {
                    Local();
                    void Local()
                    {
                        Action write = () => relay.Target = target;
                        write();
                    }
                }
            }
            """ + Startup("services.AddSingleton<Relay>();"));

        var access = run.Single("Target", AccessOperation.Write);
        Assert.Equal("di:Relay@Singleton", access.Resource.Region);
        Assert.Equal("RelayController.Post(Relay, string)", access.Symbol);
    }

    [Fact]
    public void From_services_struct_parameter_binds_nothing()
    {
        var run = Analyze(Shared + """
            public class PointController : ControllerBase
            {
                public int Get([FromServices] Point point) => point.X;
            }
            """ + Startup("services.AddSingleton(typeof(Point));"));

        Assert.Empty(run.Of("X"));
    }

    [Fact]
    public void Request_data_parameter_binds_nothing()
    {
        var run = Analyze(Shared + """
            public class RelayController : ControllerBase
            {
                public void Post(Relay relay) => relay.Target = "x";
            }
            """ + Startup());

        Assert.Empty(run.Of("Target"));
    }

    [Fact]
    public void Minimal_api_lambda_parameter_bound_to_a_singleton_is_a_di_access()
    {
        var run = Analyze(Shared + """
            public static class Endpoints
            {
                public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                {
                    services.AddSingleton<Relay>();
                    app.MapPost("/relay", (Relay relay) => relay.Target = "x");
                }
            }
            """);

        var access = run.Single("Target", AccessOperation.Write);
        Assert.Equal("di:Relay@Singleton", access.Resource.Region);
        Assert.Equal("minimal-api", access.Root.RootKind);
    }

    [Fact]
    public void Auto_property_access_keys_on_the_backing_field()
    {
        var run = Analyze("""
            public sealed class VisitorStats { public string? LastPath { get; set; } }
            public class VisitsController : ControllerBase
            {
                private readonly VisitorStats _stats;
                public VisitsController(VisitorStats stats) => _stats = stats;
                public void Post(string path) => _stats.LastPath = path;
            }
            """ + Startup("services.AddSingleton<VisitorStats>();"));

        var access = run.Single("LastPath", AccessOperation.Write);
        Assert.Equal(new MemberKey("VisitorStats", "LastPath", IrFieldKind.PropertyBackingField), access.Resource.Member);
        Assert.Equal("Case.cs", access.Source.Path);
    }

    [Fact]
    public void Access_carries_its_region_ownership_and_evidence_chain()
    {
        var run = Analyze(Shared + """
            public class RelayController : ControllerBase
            {
                public void Post() { var relay = new Relay(); relay.Target = "local"; GC.KeepAlive(relay); }
            }
            public sealed class RelayWorker(Relay relay) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { relay.Target = "worker"; return Task.CompletedTask; }
            }
            """ + Startup("services.AddSingleton<Relay>().AddHostedService<RelayWorker>();"));

        var local = Assert.Single(run.Of("Target"), access => access.Root.ProviderId == "aspnetcore");
        Assert.Equal(OwnershipKind.ThreadConfined, local.Ownership);
        Assert.NotEmpty(local.OwnershipEvidence);
        var shared = Assert.Single(run.Of("Target"), access => access.Root.ProviderId == "hosting");
        Assert.Equal(OwnershipKind.Shared, shared.Ownership);
        Assert.NotEmpty(shared.OwnershipEvidence);
        Assert.NotEqual(local.Resource.Identity, shared.Resource.Identity);
    }
}
