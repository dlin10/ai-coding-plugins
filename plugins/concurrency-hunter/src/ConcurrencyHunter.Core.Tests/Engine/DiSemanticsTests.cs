using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class DiSemanticsTests
{
    private const string GATE = "Fixture:Gate";

    [Fact]
    public void Locator_call_carries_the_constant_type_key()
    {
        var calls = ServiceCalls("""
            _services.GetRequiredService<Gate>();
            _services.GetService<Gate>();
            _services.GetServices<Gate>();
            """);

        Assert.Equal([(IrServiceCallKind.Locator, GATE, IrProviderKind.InjectedProvider),
                      (IrServiceCallKind.Locator, GATE, IrProviderKind.InjectedProvider),
                      (IrServiceCallKind.LocatorAll, GATE, IrProviderKind.InjectedProvider)],
                     calls.Select(call => (call.Kind, call.ServiceTypeKey, call.Provider)));
        Assert.All(calls, call => Assert.Equal("locator:" + GATE, call.Locator));
    }

    [Fact]
    public void Typeof_locator_call_carries_the_type_key()
    {
        var calls = ServiceCalls("""
            _services.GetRequiredService(typeof(Gate));
            _services.GetService(typeof(Gate));
            """);

        Assert.Equal([(IrServiceCallKind.Locator, GATE, IrProviderKind.InjectedProvider),
                      (IrServiceCallKind.Locator, GATE, IrProviderKind.InjectedProvider)],
                     calls.Select(call => (call.Kind, call.ServiceTypeKey, call.Provider)));
    }

    [Fact]
    public void Locator_call_with_a_variable_type_carries_no_key()
    {
        var calls = ServiceCalls("""
            Type type = typeof(Gate);
            _services.GetRequiredService(type);
            Resolve<Gate>();
            """, "private T Resolve<T>() where T : notnull => _services.GetRequiredService<T>();");

        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Equal((IrServiceCallKind.Locator, (string?)null, (string?)null), (call.Kind, call.ServiceTypeKey, call.Locator)));
    }

    [Fact]
    public void Create_scope_and_create_async_scope_are_scope_creations()
    {
        var calls = ServiceCalls("""
            _scopes.CreateScope();
            _scopes.CreateAsyncScope();
            _services.CreateScope();
            _services.CreateAsyncScope();
            """);

        Assert.Equal([(IrServiceCallKind.ScopeCreation, null, null),
                      (IrServiceCallKind.ScopeCreation, null, null),
                      (IrServiceCallKind.ScopeCreation, null, IrProviderKind.InjectedProvider),
                      (IrServiceCallKind.ScopeCreation, null, IrProviderKind.InjectedProvider)],
                     calls.Select(call => (call.Kind, call.ServiceTypeKey, call.Provider)));
    }

    [Fact]
    public void Request_services_host_services_and_scope_provider_receivers_are_recognised()
    {
        var calls = ServiceCalls("""
            HttpContext.RequestServices.GetRequiredService<Gate>();
            using var scope = _scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<Gate>();
            var provider = scope.ServiceProvider;
            provider.GetService<Gate>();
            Helpers.FromHost(null!);
            Helpers.FromApplication(null!);
            Helpers.Touch(_services);
            _other.GetService<Gate>();
            """,
            "private IServiceProvider _other = null!; public void Set(IServiceProvider other) => _other = other;",
            """
            public static class Helpers
            {
                public static void FromHost(IHost host) => host.Services.GetRequiredService<Gate>();
                public static void FromApplication(IApplicationBuilder app) => app.ApplicationServices.GetRequiredService<Gate>();
                public static void Touch(IServiceProvider sp) => sp.GetRequiredService<Gate>();
            }
            public class LedgerController(IServiceProvider services) : ControllerBase
            {
                public void Get() => services.GetService<Gate>();
            }
            """);

        Assert.Equal([(IrServiceCallKind.Locator, IrProviderKind.RequestServices),
                      (IrServiceCallKind.ScopeCreation, null),
                      (IrServiceCallKind.Locator, IrProviderKind.ScopeServiceProvider),
                      (IrServiceCallKind.Locator, IrProviderKind.ScopeServiceProvider),
                      (IrServiceCallKind.Locator, null),
                      (IrServiceCallKind.Locator, IrProviderKind.ApplicationServices),
                      (IrServiceCallKind.Locator, IrProviderKind.HostServices),
                      (IrServiceCallKind.Locator, null),
                      (IrServiceCallKind.Locator, IrProviderKind.InjectedProvider)],
                     calls.Select(call => (call.Kind, call.Provider)));
    }

    [Fact]
    public void Locator_singleton_from_action_and_worker_pairs()
    {
        var run = Analyze(COUNTER + Locating("HitController", "_services.GetRequiredService<Counter>().Value = 1;") +
                          LocatingWorker("_services.GetRequiredService<Counter>().Value = 0;") +
                          Startup("services.AddSingleton<Counter>(); services.AddHostedService<Worker>();"));

        Assert.Contains(run.PairsOn("Value"), pair => pair.First.Symbol != pair.Second.Symbol &&
                                                       pair.Resource.Region == "di:Counter@Singleton");
        Assert.Equal(0, run.Counter(CoverageCounters.UNRESOLVED_LOCATOR));
    }

    [Fact]
    public void Locator_scoped_in_a_created_scope_is_another_region_than_the_request()
    {
        var run = Analyze(COUNTER + Injecting("Counter") + ScopeWorker("using var scope = _scopes.CreateScope();") +
                          Startup("services.AddScoped<Counter>(); services.AddHostedService<Worker>();"));

        Assert.Equal(2, run.Accesses("Value").Select(access => access.Resource.RegionId).Distinct().Count());
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Locator_scoped_in_an_async_scope_is_another_region_than_the_request()
    {
        var run = Analyze(COUNTER + Injecting("Counter") + ScopeWorker("var scope = _scopes.CreateAsyncScope();") +
                          Startup("services.AddScoped<Counter>(); services.AddHostedService<Worker>();"));

        Assert.Equal(2, run.Accesses("Value").Select(access => access.Resource.RegionId).Distinct().Count());
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Locator_scoped_from_a_hosted_service_provider_is_the_root_scope_region()
    {
        var run = Analyze(COUNTER + LocatingWorker("_services.GetRequiredService<Counter>().Value = 0;") + """
            public sealed class Other(Counter counter) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken token) { counter.Value = 2; return Task.CompletedTask; }
            }
            """ + Startup("services.AddScoped<Counter>(); services.AddHostedService<Worker>(); services.AddHostedService<Other>();"));

        var region = Assert.Single(run.Accesses("Value").Select(access => access.Resource.RegionId).Distinct());
        Assert.Equal("root-scope", Region(run, region).Context);
    }

    [Fact]
    public void Locator_from_request_services_resolves_in_the_request_scope()
    {
        var run = Analyze(COUNTER + """
            public class GateController(Counter counter) : ControllerBase
            {
                public void Get() { HttpContext.RequestServices.GetRequiredService<Counter>().Value = 1; counter.Value = 2; }
            }
            """ + Startup("services.AddScoped<Counter>();"));

        var region = Assert.Single(run.Accesses("Value").Select(access => access.Resource.RegionId).Distinct());
        Assert.StartsWith("invocation:root:", Region(run, region).Context, StringComparison.Ordinal);
    }

    [Fact]
    public void Locator_from_a_minimal_api_handler_provider_resolves_in_the_request_scope()
    {
        var run = Analyze(COUNTER + Startup("""
            services.AddScoped<Counter>();
            app.MapPost("/count", (IServiceProvider sp) => sp.GetRequiredService<Counter>().Value = 1);
            """));

        var region = Assert.Single(run.Accesses("Value").Select(access => access.Resource.RegionId).Distinct());
        Assert.StartsWith("invocation:root:", Region(run, region).Context, StringComparison.Ordinal);
        Assert.Equal(0, run.Counter(CoverageCounters.UNRESOLVED_LOCATOR));
    }

    [Fact]
    public void Locator_from_host_services_resolves_in_the_root_scope()
    {
        var run = Analyze(COUNTER + Locating("GateController", "Helpers.FromHost(null!);") + """
            public static class Helpers { public static void FromHost(IHost host) => host.Services.GetRequiredService<Counter>().Value = 1; }
            """ + Startup("services.AddScoped<Counter>();"));

        Assert.Equal("root-scope", Region(run, Assert.Single(run.Accesses("Value")).Resource.RegionId!).Context);
    }

    [Fact]
    public void Locator_from_application_services_resolves_in_the_root_scope()
    {
        var run = Analyze(COUNTER + Locating("GateController", "Helpers.FromApplication(null!);") + """
            public static class Helpers
            {
                public static void FromApplication(IApplicationBuilder app) => app.ApplicationServices.GetRequiredService<Counter>().Value = 1;
            }
            """ + Startup("services.AddScoped<Counter>();"));

        Assert.Equal("root-scope", Region(run, Assert.Single(run.Accesses("Value")).Resource.RegionId!).Context);
    }

    [Fact]
    public void Locator_on_a_provider_injected_into_a_scoped_service_resolves_in_the_constructing_scope()
    {
        var run = Analyze(COUNTER + RESOLVER + Injecting("Counter", "Resolver resolver", "resolver.Touch();") +
                          Startup("services.AddScoped<Counter>(); services.AddScoped<Resolver>();"));

        var region = Assert.Single(run.Accesses("Value").Select(access => access.Resource.RegionId).Distinct());
        Assert.StartsWith("invocation:root:", Region(run, region).Context, StringComparison.Ordinal);
        Assert.Equal(2, run.Accesses("Value").Count);
    }

    [Fact]
    public void Locator_on_a_provider_injected_into_a_singleton_resolves_in_the_root_scope()
    {
        var run = Analyze(COUNTER + RESOLVER + Injecting("Resolver", body: "counter.Touch();") + """
            public sealed class Other(Counter counter) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken token) { counter.Value = 2; return Task.CompletedTask; }
            }
            """ + Startup("services.AddScoped<Counter>(); services.AddSingleton<Resolver>(); services.AddHostedService<Other>();"));

        var region = Assert.Single(run.Accesses("Value").Select(access => access.Resource.RegionId).Distinct());
        Assert.Equal("root-scope", Region(run, region).Context);
        Assert.Equal(2, run.Accesses("Value").Count);
    }

    [Fact]
    public void Locator_on_a_provider_injected_into_a_transient_resolves_in_the_resolving_scope()
    {
        var run = Analyze(COUNTER + RESOLVER + Injecting("Counter", "Resolver resolver", "resolver.Touch();") +
                          Startup("services.AddScoped<Counter>(); services.AddTransient<Resolver>();"));

        var region = Assert.Single(run.Accesses("Value").Select(access => access.Resource.RegionId).Distinct());
        Assert.StartsWith("invocation:root:", Region(run, region).Context, StringComparison.Ordinal);
        Assert.Equal(2, run.Accesses("Value").Count);
    }

    [Fact]
    public void Locator_on_a_provider_from_an_unknown_source_is_opaque_and_counted()
    {
        var run = Analyze(COUNTER + Locating("GateController",
                                             "((IServiceProvider)AppDomain.CurrentDomain.GetData(\"services\")!).GetRequiredService<Counter>().Value = 1;") +
                          Startup("services.AddSingleton<Counter>();"));

        Assert.Equal(1, run.Counter(CoverageCounters.UNRESOLVED_LOCATOR));
        Assert.Empty(run.Accesses("Value"));
    }

    [Fact]
    public void Locator_on_an_ordinary_method_provider_parameter_is_opaque_and_counted()
    {
        var run = Analyze(COUNTER + Locating("GateController", "Helpers.Touch(_services);") + """
            public static class Helpers { public static void Touch(IServiceProvider sp) => sp.GetRequiredService<Counter>().Value = 1; }
            public sealed class Other(Counter counter) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken token) { counter.Value = 2; return Task.CompletedTask; }
            }
            """ + Startup("services.AddSingleton<Counter>(); services.AddHostedService<Other>();"));

        Assert.Equal(1, run.Counter(CoverageCounters.UNRESOLVED_LOCATOR));
        Assert.DoesNotContain(run.Accesses("Value"), access => access.Symbol == "Helpers.Touch(IServiceProvider)");
        Assert.DoesNotContain(run.PairsOn("Value"), pair => pair.First.Symbol != pair.Second.Symbol);
    }

    [Fact]
    public void Bound_get_required_service_by_type_resolves()
    {
        var run = Analyze(COUNTER + Locating("GateController", "((Counter)_services.GetRequiredService(typeof(Counter))).Value = 1;") +
                          Startup("services.AddSingleton<Counter>();"));

        Assert.Equal("di:Counter@Singleton", Assert.Single(run.Accesses("Value")).Resource.Region);
        Assert.Equal(0, run.Counter(CoverageCounters.UNRESOLVED_LOCATOR));
    }

    [Fact]
    public void Bound_get_service_by_type_resolves()
    {
        var run = Analyze(COUNTER + Locating("GateController", "((Counter)_services.GetService(typeof(Counter))!).Value = 1;") +
                          Startup("services.AddSingleton<Counter>();"));

        Assert.Equal("di:Counter@Singleton", Assert.Single(run.Accesses("Value")).Resource.Region);
        Assert.Equal(0, run.Counter(CoverageCounters.UNRESOLVED_LOCATOR));
    }

    [Fact]
    public void Locator_transient_is_fresh_per_call_site()
    {
        var run = Analyze(COUNTER + Locating("GateController", """
            _services.GetRequiredService<Counter>().Value = 1;
            _services.GetRequiredService<Counter>().Value = 2;
            """) + LocatingWorker("_services.GetRequiredService<Counter>().Value = 0;") +
                          Startup("services.AddTransient<Counter>(); services.AddHostedService<Worker>();"));

        Assert.Equal(3, run.Accesses("Value").Select(access => access.Resource.RegionId).Distinct().Count());
        Assert.Empty(run.PairsOn("Value"));
    }

    [Fact]
    public void Locator_of_an_unbound_service_is_opaque_and_counted()
    {
        var run = Analyze(COUNTER + Locating("GateController", "_services.GetService<Counter>()!.Value = 1;") + Startup());

        Assert.Equal(1, run.Counter(CoverageCounters.UNRESOLVED_LOCATOR));
        Assert.Empty(run.Accesses("Value"));
    }

    [Fact]
    public void Locator_with_a_variable_type_is_opaque_and_counted()
    {
        var run = Analyze(COUNTER + Locating("GateController", "var type = typeof(Counter); ((Counter)_services.GetService(type)!).Value = 1;") +
                          Startup("services.AddSingleton<Counter>();"));

        Assert.Equal(1, run.Counter(CoverageCounters.UNRESOLVED_LOCATOR));
        Assert.Empty(run.Accesses("Value"));
    }

    [Fact]
    public void Unresolved_locator_reached_from_two_roots_is_counted_once()
    {
        var run = Analyze(COUNTER + Locating("GateController", "Helpers.Probe(_services);") + Locating("OtherController", "Helpers.Probe(_services);") + """
            public static class Helpers { public static void Probe(IServiceProvider sp) => GC.KeepAlive(sp.GetService<Counter>()); }
            """ + Startup());

        Assert.Equal(2, run.Execution.Heap.Instances("body:Fixture:M:Helpers.Probe(System.IServiceProvider)").Count);
        Assert.Equal(1, run.Counter(CoverageCounters.UNRESOLVED_LOCATOR));
    }

    [Fact]
    public void Get_services_resolves_every_registration_in_order()
    {
        var run = Analyze(PLUGINS + Locating("GateController", "GC.KeepAlive(_services.GetServices<IPlugin>());") +
                          Startup("services.AddSingleton<IPlugin, Gamma>(); services.AddSingleton<IPlugin, Alpha>(); services.AddScoped<IPlugin, Beta>();"));

        Assert.Equal(["di:Gamma@Singleton", "di:Alpha@Singleton", "di:Beta@Scoped"], Elements(run));
        Assert.Equal(0, run.Counter(CoverageCounters.UNRESOLVED_LOCATOR));
    }

    [Fact]
    public void Get_services_orders_heterogeneous_registrations_by_call_position()
    {
        var solution = Fixtures.FixtureSolution.Create(
            ("B.cs", Usings + PLUGINS + Locating("GateController", "GC.KeepAlive(_services.GetServices<IPlugin>());") +
                     Startup("services.AddSingleton<IPlugin, Alpha>();")),
            ("A.cs", Usings + """
                public static class Early { public static void Register(IServiceCollection services) => services.AddScoped<IPlugin, Beta>(); }
                """));
        var run = AnalyzeScope(solution, "scope:Fixture");

        Assert.Equal(["di:Beta@Scoped", "di:Alpha@Singleton"], Elements(run));
    }

    [Fact]
    public void Get_services_of_two_aliasing_registrations_returns_one_region()
    {
        var run = Analyze(PLUGINS + Locating("GateController", "GC.KeepAlive(_services.GetServices<IPlugin>());") + """
            public static class Shared { public static readonly Alpha Instance = new Alpha(); }
            """ + Startup("services.AddSingleton<IPlugin>(_ => Shared.Instance); services.AddSingleton<IPlugin>(_ => Shared.Instance);"));

        Assert.Equal(HeapRegionKind.Allocation, Region(run, Assert.Single(Collection(run))).Kind);
    }

    [Fact]
    public void Identical_registrations_are_two_regions_and_get_services_returns_both()
    {
        var run = Analyze(PLUGINS + Locating("GateController", "GC.KeepAlive(_services.GetServices<IPlugin>());") +
                          Startup("services.AddSingleton<IPlugin, Alpha>(); services.AddSingleton<IPlugin, Alpha>();"));

        Assert.Equal(["di:Alpha@Singleton", "di:Alpha@Singleton#2"], Elements(run));
    }

    [Fact]
    public void Factory_allocation_takes_the_registration_region_and_its_type_dispatches()
    {
        var run = Analyze(CLOCKS + Injecting("IClock", body: "counter.Tick();") + Startup("services.AddSingleton<IClock>(_ => new WallClock());"));

        var access = Assert.Single(run.Accesses("LastTick"));
        Assert.Equal("di:IClock@Singleton", access.Resource.Region);
        Assert.Equal(HeapRegionKind.Di, Region(run, access.Resource.RegionId!).Kind);
        Assert.Equal(0, run.Counter(CoverageCounters.NO_RECEIVER_OBJECT));
    }

    [Fact]
    public void Method_group_factory_on_an_instance_runs_on_its_receiver()
    {
        var run = Analyze(CLOCKS + Injecting("IClock", body: "counter.Tick();") + """
            public sealed class ClockBuilder { public IClock Build(IServiceProvider sp) => new WallClock(); }
            """ + Startup("var builder = new ClockBuilder(); services.AddSingleton<IClock>(builder.Build);"));

        var build = Assert.Single(run.Execution.Heap.Instances("body:Fixture:M:ClockBuilder.Build(System.IServiceProvider)"));
        Assert.Equal(HeapRegionKind.Allocation, Region(run, Assert.Single(build.Receivers)).Kind);
        Assert.Equal("di:IClock@Singleton", Assert.Single(run.Accesses("LastTick")).Resource.Region);
    }

    [Fact]
    public void Capturing_factory_lambda_returns_the_captured_region()
    {
        var run = Analyze(SHARED + """
            public class GateController(IShared first, IOther second) : ControllerBase { public void Get() { first.Write(); second.Write(); } }
            """ + Startup("var shared = new Shared(); services.AddSingleton<IShared>(shared); services.AddSingleton<IOther>(_ => shared);"));

        var heap = run.Execution.Heap.Heap;
        var construction = Assert.Single(heap.Constructions, construction => Region(run, construction.RegionId).Display == "receiver:GateController");
        var constructor = heap.Instances[Assert.Single(construction.ConstructorInstances)];
        Assert.Single(constructor.Parameters[0]);
        Assert.Equal(constructor.Parameters[0], constructor.Parameters[1]);
        Assert.Single(run.Accesses("Value"));
    }

    [Fact]
    public void Extension_method_parameter_flowing_into_a_registration_is_bound_from_its_startup_call()
    {
        var run = Analyze(SHARED + FEATURE + SHARED_USERS + """
            public static class Program
            {
                public static void Main(IServiceCollection services)
                {
                    GC.KeepAlive(services);
                    var shared = new Shared();
                    services.AddFeature(shared);
                }
            }
            """ + Startup("services.AddHostedService<Worker>();"));

        var region = Region(run, Assert.Single(run.Accesses("Value").Select(access => access.Resource.RegionId).Distinct()));
        Assert.Equal((HeapRegionKind.Allocation, "alloc:Program.Main(IServiceCollection)#Shared"), (region.Kind, region.Display));
        Assert.Contains(run.PairsOn("Value"), pair => pair.First.Root.RootId != pair.Second.Root.RootId);
    }

    [Fact]
    public void Registration_member_reached_through_an_intermediate_startup_call_is_only_bound_from_that_call()
    {
        var run = Analyze(SHARED + FEATURE + SHARED_USERS + """
            public static class Program
            {
                public static void Main(IServiceCollection services)
                {
                    GC.KeepAlive(services);
                    Configure(services);
                }

                private static void Configure(IServiceCollection services) => services.AddFeature(new Shared());
            }
            """ + Startup("services.AddHostedService<Worker>();"));

        var heap = run.Execution.Heap.Heap;
        Assert.Single(heap.Instances.Values, instance => instance.BodyId.Contains("FeatureExtensions.AddFeature", StringComparison.Ordinal));
        Assert.DoesNotContain(heap.Regions.Values, region => region.Kind == HeapRegionKind.Symbolic);
        var region = Region(run, Assert.Single(run.Accesses("Value").Select(access => access.Resource.RegionId).Distinct()));
        Assert.Equal(HeapRegionKind.Allocation, region.Kind);
        Assert.DoesNotContain(run.Accesses("Value"), access => access.Uncertainties.Any(uncertainty => uncertainty.Contains("unknown caller", StringComparison.Ordinal)));
    }

    [Fact]
    public void Unreached_registration_member_parameter_is_a_symbolic_region_with_an_uncertainty()
    {
        var run = Analyze(SHARED + FEATURE + SHARED_USERS + Startup("services.AddHostedService<Worker>();"));

        var access = run.Accesses("Value")[0];
        Assert.Equal(HeapRegionKind.Symbolic, Region(run, access.Resource.RegionId!).Kind);
        Assert.Contains(access.Uncertainties, uncertainty => uncertainty.Contains("unknown caller", StringComparison.Ordinal));
        Assert.NotEmpty(run.PairsOn("Value"));
    }

    [Fact]
    public void Factory_returning_a_static_object_aliases_it()
    {
        var run = Analyze(RATES + Injecting("Rates", body: "counter.Current = 1;") + Startup("services.AddSingleton<Rates>(_ => Rates.Instance);"));

        var access = Assert.Single(run.Accesses("Current"));
        Assert.Equal(HeapRegionKind.Allocation, Region(run, access.Resource.RegionId!).Kind);
    }

    [Fact]
    public void Factory_returning_another_registration_aliases_it()
    {
        var run = Analyze(RATES + Injecting("IRates", body: "counter.Set();") +
                          Startup("services.AddSingleton<Rates>(); services.AddSingleton<IRates>(sp => sp.GetRequiredService<Rates>());"));

        Assert.Equal("di:Rates@Singleton", Assert.Single(run.Accesses("Current")).Resource.Region);
        Assert.Equal(0, run.Counter(CoverageCounters.UNRESOLVED_LOCATOR));
    }

    [Fact]
    public void Factory_returning_no_region_keeps_its_own_region_with_an_uncertainty()
    {
        var run = Analyze(CLOCKS + Injecting("IClock", body: "counter.Tick();") + Startup("services.AddSingleton<IClock>(_ => null!);"));

        Assert.Equal(1, run.Counter(CoverageCounters.NO_RECEIVER_OBJECT));
        var clock = run.Execution.Heap.Region("di:IClock@Singleton");
        Assert.Contains(run.Execution.Heap.Heap.RegionUncertainties[clock.Identity],
                        uncertainty => uncertainty.Contains("factory result is unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void Factory_provider_parameter_resolves_in_the_triggering_scope()
    {
        var run = Analyze(COUNTER + WRAPPER + Injecting("Counter", "Wrapper wrapper", "wrapper.Touch();") +
                          Startup("services.AddScoped<Counter>(); services.AddScoped<Wrapper>(sp => new Wrapper(sp.GetRequiredService<Counter>()));"));

        var region = Assert.Single(run.Accesses("Value").Select(access => access.Resource.RegionId).Distinct());
        Assert.StartsWith("invocation:root:", Region(run, region).Context, StringComparison.Ordinal);
        Assert.Equal(2, run.Accesses("Value").Count);
    }

    [Fact]
    public void Factory_write_to_another_shared_region_belongs_to_the_triggering_execution()
    {
        var run = Analyze(STATS + TAG + Injecting("Tag", body: "GC.KeepAlive(counter);") + STATS_WORKER +
                          Startup("services.AddScoped<Tag>(_ => { Stats.Last = 1; return new Tag(); }); services.AddHostedService<Worker>();"));

        Assert.Contains(run.PairsOn("Last"), pair => pair.First.Root.RootId.Contains("GateController", StringComparison.Ordinal) !=
                                                     pair.Second.Root.RootId.Contains("GateController", StringComparison.Ordinal));
        Assert.Contains(run.Accesses("Last"), access => access.Symbol.StartsWith("Startup.Configure", StringComparison.Ordinal) &&
                                                        access.Root.RootId.Contains("GateController", StringComparison.Ordinal));
    }

    [Fact]
    public void Scoped_factory_constructs_per_request()
    {
        var run = Analyze(TAG + Injecting("Tag", body: "counter.Id = 1;") + Injecting("Tag", body: "counter.Id = 2;", controller: "OtherController") +
                          Startup("services.AddScoped<Tag>(_ => new Tag());"));

        Assert.Equal(2, run.Accesses("Id").Select(access => access.Resource.RegionId).Distinct().Count());
        Assert.Equal(2, run.Execution.Heap.Heap.Constructions.Count(construction => construction.RegionId.Contains("Fixture:Tag@Scoped", StringComparison.Ordinal)));
        Assert.Empty(run.PairsOn("Id"));
    }

    [Fact]
    public void Transient_factory_constructs_per_resolution()
    {
        var run = Analyze(TAG + """
            public class GateController(Tag first, Tag second) : ControllerBase { public void Get() { first.Id = 1; second.Id = 2; } }
            """ + Startup("services.AddTransient<Tag>(_ => new Tag());"));

        Assert.Equal(2, run.Accesses("Id").Select(access => access.Resource.RegionId).Distinct().Count());
        Assert.Equal(2, run.Execution.Heap.Heap.Constructions.Count(construction => construction.RegionId.Contains("Fixture:Tag@Transient", StringComparison.Ordinal)));
    }

    [Fact]
    public void Instance_registration_is_a_startup_construction_whose_static_write_precedes_actions()
    {
        var run = Analyze(SEED + """
            public class GateController : ControllerBase { public object? Get() => Seed.Origin; }
            """ + Startup("services.AddSingleton(new Seed());"));

        Assert.Empty(run.PairsOn("Origin"));
        Assert.Contains(run.Accesses("Origin"), access => access.Operation == ConcurrencyHunter.Analysis.AccessOperation.Write &&
                                                          access.ExecutionId == ExecutionModel.STARTUP);
        Assert.True(run.Skipped(InterproceduralPairing.SKIP_ORDERED) >= 1);
    }

    [Fact]
    public void Instance_registration_of_an_existing_region_aliases_it()
    {
        var run = Analyze(RATES + Injecting("Rates", body: "counter.Current = 1;") + Startup("services.AddSingleton<Rates>(Rates.Instance);"));

        Assert.Equal(HeapRegionKind.Allocation, Region(run, Assert.Single(run.Accesses("Current")).Resource.RegionId!).Kind);
    }

    [Fact]
    public void Instance_registration_with_no_region_keeps_its_own_region_with_an_uncertainty()
    {
        var run = Analyze(CLOCKS + Injecting("IClock", body: "counter.Tick();") +
                          Startup("services.AddSingleton<IClock>((IClock)Activator.CreateInstance(typeof(WallClock))!);"));

        Assert.Equal(1, run.Counter(CoverageCounters.NO_RECEIVER_OBJECT));
        var clock = run.Execution.Heap.Region("di:IClock@Singleton");
        Assert.Contains(run.Execution.Heap.Heap.RegionUncertainties[clock.Identity],
                        uncertainty => uncertainty.Contains("factory result is unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void Instance_registration_with_two_regions_resolves_to_both_with_an_uncertainty()
    {
        var run = Analyze(CLOCKS + Injecting("IClock", body: "counter.Tick();") + CLOCK_WORKER +
                          Startup("services.AddSingleton<IClock>(Flags.On ? new WallClock() : Sundial.Shared); services.AddHostedService<Worker>();"));

        AssertBothClocks(run);
    }

    [Fact]
    public void Interface_typed_instance_registration_dispatches_on_the_instance_type()
    {
        var run = Analyze(CLOCKS + Injecting("IClock", body: "counter.Tick();") + Startup("services.AddSingleton<IClock>(new WallClock());"));

        Assert.Single(run.Execution.Heap.Instances("body:Fixture:M:WallClock.Tick"));
        Assert.Equal("di:IClock@Singleton", Assert.Single(run.Accesses("LastTick")).Resource.Region);
        Assert.Equal(0, run.Counter(CoverageCounters.NO_RECEIVER_OBJECT));
    }

    [Fact]
    public void Hosted_service_reaching_a_factory_singleton_constructs_it_at_startup()
    {
        var run = Analyze(STATS + RATES + """
            public sealed class Worker(Rates rates) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken token) { GC.KeepAlive(rates); return Task.CompletedTask; }
            }
            public class GateController : ControllerBase { public object? Get() => Stats.Last; }
            """ + Startup("services.AddSingleton<Rates>(_ => { Stats.Last = 1; return new Rates(); }); services.AddHostedService<Worker>();"));

        var rates = run.Execution.Heap.Region("di:Rates@Singleton");
        var factory = Assert.Single(Assert.Single(run.Execution.Heap.Heap.Constructions, construction => construction.RegionId == rates.Identity).ConstructorInstances);
        Assert.Equal([ExecutionModel.STARTUP], run.Execution.Analysis.InstanceExecutions[factory]);
        Assert.Empty(run.PairsOn("Last"));
    }

    [Fact]
    public void Factory_returning_two_regions_resolves_to_both_with_an_uncertainty()
    {
        var run = Analyze(CLOCKS + Injecting("IClock", body: "counter.Tick();") + CLOCK_WORKER +
                          Startup("services.AddSingleton<IClock>(_ => Flags.On ? new WallClock() : Sundial.Shared); services.AddHostedService<Worker>();"));

        AssertBothClocks(run);
    }

    [Fact]
    public void Unsupported_registration_is_still_counted_as_unanalysed()
    {
        var run = Analyze(RATES + Startup("services.AddSingleton<Rates>(_ => new Rates()); services.AddSingleton(typeof(Rates), new Rates());"));

        Assert.Equal(1, run.Counter(CoverageCounters.UNANALYSED_REGISTRATION));
    }

    [Fact]
    public void Unsupported_registration_in_an_unreachable_member_is_not_counted()
    {
        var run = Analyze(RATES + """
            public static class Unused { public static void Register(IServiceCollection services) => services.AddSingleton(typeof(Rates), new Rates()); }
            """ + Startup("services.AddSingleton<Rates>(_ => new Rates());"));

        Assert.Equal(0, run.Counter(CoverageCounters.UNANALYSED_REGISTRATION));
    }

    [Fact]
    public void Registration_body_accesses_are_startup_accesses()
    {
        var run = Analyze(STATS + RATES + """
            public class GateController : ControllerBase { public object? Get() => Stats.Last; }
            """ + Startup("Stats.Last = 1; services.AddSingleton<Rates>(_ => new Rates());"));

        var write = Assert.Single(run.Accesses("Last"), access => access.Operation == ConcurrencyHunter.Analysis.AccessOperation.Write);
        Assert.Equal(ExecutionModel.STARTUP, write.ExecutionId);
        Assert.Empty(run.PairsOn("Last"));
        Assert.Contains(run.Execution.Analysis.InstanceExecutions,
                        pair => pair.Key.StartsWith("body:Fixture:M:Startup.Configure", StringComparison.Ordinal) && pair.Value.SetEquals([ExecutionModel.STARTUP]));
    }

    private const string COUNTER = "public sealed class Counter { public int Value; }\n";

    private const string RESOLVER = """
        public sealed class Resolver
        {
            private readonly IServiceProvider _sp;
            public Resolver(IServiceProvider sp) => _sp = sp;
            public void Touch() => _sp.GetRequiredService<Counter>().Value = 1;
        }

        """;

    private const string PLUGINS = """
        public interface IPlugin { }
        public sealed class Alpha : IPlugin { }
        public sealed class Beta : IPlugin { }
        public sealed class Gamma : IPlugin { }

        """;

    private const string CLOCKS = """
        public interface IClock { void Tick(); }
        public sealed class WallClock : IClock { public int LastTick; public void Tick() => LastTick = 1; }
        public sealed class Sundial : IClock { public static readonly Sundial Shared = new Sundial(); public int LastTick; public void Tick() => LastTick = 2; }
        public static class Flags { public static bool On; }

        """;

    private const string CLOCK_WORKER = """
        public sealed class Worker(IClock clock) : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken token) { clock.Tick(); return Task.CompletedTask; }
        }

        """;

    private const string SHARED = """
        public interface IShared { void Write(); }
        public interface IOther { void Write(); }
        public sealed class Shared : IShared, IOther { public int Value; public void Write() => Value = 1; }

        """;

    private const string FEATURE = """
        public static class FeatureExtensions
        {
            public static IServiceCollection AddFeature(this IServiceCollection services, Shared shared) => services.AddSingleton<IShared>(shared);
        }

        """;

    private const string SHARED_USERS = """
        public class GateController(IShared shared) : ControllerBase { public void Get() => shared.Write(); }
        public sealed class Worker(IShared shared) : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken token) { shared.Write(); return Task.CompletedTask; }
        }

        """;

    private const string RATES = """
        public interface IRates { void Set(); }
        public sealed class Rates : IRates { public static readonly Rates Instance = new Rates(); public int Current; public void Set() => Current = 1; }

        """;

    private const string WRAPPER = """
        public sealed class Wrapper
        {
            private readonly Counter _counter;
            public Wrapper(Counter counter) => _counter = counter;
            public void Touch() => _counter.Value = 1;
        }

        """;

    private const string STATS = "public static class Stats { public static int Last; }\n";

    private const string STATS_WORKER = """
        public sealed class Worker : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken token) { Stats.Last = 2; return Task.CompletedTask; }
        }

        """;

    private const string TAG = "public sealed class Tag { public int Id; }\n";

    private const string SEED = """
        public sealed class Seed
        {
            public static object? Origin;
            public Seed() => Origin = new object();
        }

        """;

    /// <summary>A controller holding an injected provider whose action runs <paramref name="action"/>.</summary>
    private static string Locating(string controller, string action) => $$"""
        public class {{controller}} : ControllerBase
        {
            private readonly IServiceProvider _services;
            public {{controller}}(IServiceProvider services) => _services = services;
            public void Get()
            {
                {{action}}
            }
        }

        """;

    /// <summary>A controller injecting <paramref name="service"/> as <c>counter</c>, with an optional extra parameter; its action
    /// writes <c>counter.Value</c> unless <paramref name="body"/> says otherwise.</summary>
    private static string Injecting(string service, string parameter = "", string statement = "", string body = "counter.Value = 2;",
                                    string controller = "GateController") => $$"""
        public class {{controller}}({{service}} counter{{(parameter.Length == 0 ? "" : ", " + parameter)}}) : ControllerBase
        {
            public void Get() { {{statement}} {{body}} }
        }

        """;

    private static string LocatingWorker(string body) => $$"""
        public sealed class Worker : BackgroundService
        {
            private readonly IServiceProvider _services;
            public Worker(IServiceProvider services) => _services = services;
            protected override Task ExecuteAsync(CancellationToken token)
            {
                {{body}}
                return Task.CompletedTask;
            }
        }

        """;

    private static string ScopeWorker(string create) => $$"""
        public sealed class Worker : BackgroundService
        {
            private readonly IServiceScopeFactory _scopes;
            public Worker(IServiceScopeFactory scopes) => _scopes = scopes;
            protected override Task ExecuteAsync(CancellationToken token)
            {
                {{create}}
                scope.ServiceProvider.GetRequiredService<Counter>().Value = 0;
                return Task.CompletedTask;
            }
        }

        """;

    private static HeapRegion Region(EngineRun run, string? regionId) => run.Execution.Heap.Heap.Regions[regionId!];

    private static IReadOnlyList<string> Collection(EngineRun run) => Assert.Single(run.Execution.Heap.Heap.Collections.Values);

    private static IReadOnlyList<string> Elements(EngineRun run) => Collection(run).Select(region => Region(run, region).Display).ToArray();

    /// <summary>A call on the injected clock reaches both clocks' methods, writes through it pair on both regions, and the regions
    /// carry the uncertainty that the registration is more than one object.</summary>
    private static void AssertBothClocks(EngineRun run)
    {
        Assert.NotEmpty(run.Execution.Heap.Instances("body:Fixture:M:WallClock.Tick"));
        Assert.NotEmpty(run.Execution.Heap.Instances("body:Fixture:M:Sundial.Tick"));
        var regions = run.PairsOn("LastTick").Select(pair => pair.Resource.RegionId).Distinct().ToArray();
        Assert.Equal(2, regions.Length);
        Assert.All(regions, region => Assert.Contains(run.Execution.Heap.Heap.RegionUncertainties[region!],
                                                      uncertainty => uncertainty.Contains("more than one object", StringComparison.Ordinal)));
    }

    /// <summary>The service calls of every reached body, by body id and operation, for a controller holding an injected provider
    /// and scope factory whose action runs <paramref name="action"/>.</summary>
    private static IReadOnlyList<IrServiceCall> ServiceCalls(string action, string members = "", string types = "")
    {
        var run = Reach($$"""
            public sealed class Gate { }
            public class GateController : ControllerBase
            {
                private readonly IServiceProvider _services;
                private readonly IServiceScopeFactory _scopes;
                public GateController(IServiceProvider services, IServiceScopeFactory scopes) { _services = services; _scopes = scopes; }
                public void Get()
                {
                    {{action}}
                }
                {{members}}
            }
            {{types}}
            """ + Startup("services.AddSingleton<Gate>();"));

        return run.Result.Bodies.Where(pair => run.Reaches(pair.Key))
                  .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                  .SelectMany(pair => pair.Value.Blocks.SelectMany(block => block.Operations))
                  .OfType<IrCallOperation>()
                  .Select(call => call.ServiceCall)
                  .OfType<IrServiceCall>()
                  .ToArray();
    }
}
