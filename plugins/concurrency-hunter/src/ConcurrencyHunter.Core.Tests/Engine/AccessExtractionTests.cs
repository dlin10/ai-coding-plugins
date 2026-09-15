using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
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
        Assert.Equal(SharingKeys.PROCESS, access.SharingKey);
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
        Assert.Equal("process:Fixture:Gate", access.SharingKey);
        Assert.Equal(0, run.Skipped(AccessExtraction.SKIP_UNBOUND_MEMBER));
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
        Assert.Equal("process:Fixture:Relay", access.SharingKey);
        Assert.Contains(access.BindingEvidence, evidence => evidence.Kind == "registration");
        Assert.Empty(run.Of("_relay"));
        Assert.Equal(1, run.Skipped(AccessExtraction.SKIP_LOCAL));
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
        Assert.Equal("process:Microsoft.Extensions.Hosting.Abstractions:Microsoft.Extensions.Hosting.IHostedService", access.SharingKey);
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
    public void Reassigned_action_parameter_binds_nothing()
    {
        var run = Analyze(Shared + """
            public class RelayController : ControllerBase
            {
                public void Post([FromServices] Relay relay, string target)
                {
                    relay.Target = target;
                    relay = new Relay();
                }
            }
            """ + Startup("services.AddSingleton<Relay>();"));

        Assert.Empty(run.Of("Target"));
        Assert.Equal(1, run.Skipped(AccessExtraction.SKIP_REASSIGNED_PARAMETER));
    }

    [Fact]
    public void Parameter_passed_by_ref_binds_nothing_and_its_lock_holds_nothing()
    {
        var run = Analyze(Shared + """
            public static class State { public static int Value; }
            public class RelayController : ControllerBase
            {
                public void Post([FromServices] Relay gate)
                {
                    Replace(ref gate);
                    lock (gate)
                    {
                        gate.Target = "locked";
                        State.Value = 1;
                    }
                }
                private static void Replace(ref Relay gate) => gate = new Relay();
            }
            """ + Startup("services.AddSingleton<Relay>();"));

        Assert.Empty(run.Of("Target"));
        Assert.True(run.Skipped(AccessExtraction.SKIP_REASSIGNED_PARAMETER) >= 1);
        var write = run.Single("Value", AccessOperation.Write);
        Assert.Contains("identity unknown", Assert.Single(write.HeldProtection), StringComparison.Ordinal);
        Assert.Empty(write.HeldProtectionIds);
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
        Assert.Equal(1, run.Skipped(AccessExtraction.SKIP_UNBOUND_PARAMETER));
    }

    [Fact]
    public void Instance_method_group_handler_field_binds_nothing()
    {
        var run = Analyze(Shared + """
            public sealed class RelayHandlers
            {
                private readonly Relay _relay;
                public RelayHandlers(Relay relay) => _relay = relay;
                public void Post(string target) => _relay.Target = target;
            }
            public static class Endpoints
            {
                public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                {
                    services.AddSingleton<Relay>();
                    var handlers = new RelayHandlers(new Relay());
                    app.MapPost("/relay", handlers.Post);
                }
            }
            """);

        Assert.Empty(run.Of("Target"));
        Assert.True(run.Skipped(AccessExtraction.SKIP_UNBOUND_MEMBER) >= 1);
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
        Assert.Equal(1, run.Skipped(AccessExtraction.SKIP_UNBOUND_PARAMETER));
    }

    [Fact]
    public void Allocated_and_local_objects_are_not_accesses()
    {
        var run = Analyze(Shared + """
            public class RelayController : ControllerBase
            {
                public void Post()
                {
                    new Relay().Target = "allocated";
                    Relay local = null!;
                    local.Target = "local";
                }
            }
            """ + Startup());

        Assert.Empty(run.Of("Target"));
        Assert.Equal(1, run.Skipped(AccessExtraction.SKIP_ALLOCATION));
        Assert.Equal(1, run.Skipped(AccessExtraction.SKIP_LOCAL));
    }

    [Fact]
    public void Call_result_and_nested_field_are_not_accesses()
    {
        var run = Analyze(Shared + """
            public class RelayController : ControllerBase
            {
                private readonly Relay _relay;
                public RelayController(Relay relay) => _relay = relay;
                public void Post()
                {
                    Current().Target = "call";
                    _relay.Inner!.Target = "nested";
                }
                private static Relay Current() => new();
            }
            """ + Startup("services.AddSingleton<Relay>();"));

        Assert.DoesNotContain(run.Of("Target"), access => access.Operation == AccessOperation.Write);
        Assert.Equal(1, run.Skipped(AccessExtraction.SKIP_CALL_RESULT));
        Assert.Equal(1, run.Skipped(AccessExtraction.SKIP_NESTED_FIELD));
        Assert.Single(run.Of("Inner"));
    }

    [Fact]
    public void Phi_of_two_services_is_not_an_access()
    {
        var run = Analyze(Shared + """
            [ApiController]
            public class RelayController : ControllerBase
            {
                public void Post([FromServices] Relay first, [FromServices] Relay second, bool useFirst)
                {
                    var chosen = useFirst ? first : second;
                    chosen.Target = "x";
                }
            }
            """ + Startup("services.AddSingleton<Relay>();"));

        Assert.Empty(run.Of("Target"));
        Assert.Equal(1, run.Skipped(AccessExtraction.SKIP_PHI));
    }

    [Fact]
    public void Unboxing_conversion_stops_the_trace_but_reference_conversion_does_not()
    {
        var run = Analyze(Shared + """
            public class PointController : ControllerBase
            {
                public int Get([FromServices] object boxed, [FromServices] object relay)
                {
                    ((Relay)relay).Target = "x";
                    return ((Point)boxed).X;
                }
            }
            """ + Startup("services.AddSingleton<object, Relay>();"));

        Assert.Empty(run.Of("X"));
        Assert.Equal(1, run.Skipped(AccessExtraction.SKIP_CONVERSION));
        Assert.Equal("di:Relay@Singleton", run.Single("Target", AccessOperation.Write).Resource.Region);
    }

    [Fact]
    public void Unbound_member_of_a_controller_is_not_an_access()
    {
        var run = Analyze(Shared + """
            public class RelayController : ControllerBase
            {
                private readonly Relay _relay = new();
                public void Post() => _relay.Target = "x";
            }
            """ + Startup("services.AddSingleton<Relay>();"));

        Assert.Empty(run.Of("Target"));
        Assert.Equal(1, run.Skipped(AccessExtraction.SKIP_UNBOUND_MEMBER));
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
}
