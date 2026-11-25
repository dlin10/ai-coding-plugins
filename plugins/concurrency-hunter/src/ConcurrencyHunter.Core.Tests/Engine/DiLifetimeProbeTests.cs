using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of what makes a service's state shared (issue 533): DI lifetimes, keyed and instance registrations, a
/// captive dependency and a scoped object leaving its scope, each with what run time calls for. A singleton is one instance for the
/// application, including one registered as an instance; a scoped service is one per request or per <c>CreateScope</c>; a transient one
/// is created per resolution, so one a singleton takes in its constructor lives, and is shared, as long as the singleton (the captive
/// dependency of Dependency injection guidelines, which scope validation in Development reports for a scoped one); keyed services
/// follow the lifetimes of their registration (Dependency injection in .NET, keyed services; that a keyed singleton is one instance is
/// inferred from it). A scope created per piece of background work gives that work its own scoped instances (Use scoped services within
/// a BackgroundService), and the container disposes the scoped services it created when the scope ends, at the end of the request,
/// whatever work still uses them (Dependency injection, disposal of services). Work a constructor starts with <c>Task.Run</c> runs on
/// the pool from the call on, concurrently with the code that resolved the service, whatever its lifetime (Task.Run). Every truth is the run-time behaviour of the program the
/// fixture stands for, its registrations taking effect as written in the suite's <c>Startup</c> shape. Each probe runs through the
/// whole analysis (<see cref="HostedServiceProbe.AnalyzeAsync(string, ProbeInputs)"/>) and names each finding's pair of sides by
/// marked site; a probe the analysis answers otherwise is named in <see cref="Differences"/> with its answer and why.</summary>
public sealed class DiLifetimeProbeTests
{
    /// <summary>Why a keyed singleton's field makes no access.</summary>
    private const string KEYED_IS_NOT_ANALYSED =
        "issue 432: keyed registrations are not analysed (PLAN, known limits; 5g run E2), so the [FromKeyedServices] parameter binds " +
        "no object the heap knows and the counter's field makes no access";

    /// <summary>Why the container's disposal of a scoped service makes no access.</summary>
    private const string CONTAINER_DISPOSAL_IS_NOT_MODELLED =
        "limit: nothing models the call the DI container makes, implicitly, to Dispose of the scoped services it created when their " +
        "scope ends at the end of the request; no program code calls Session.Dispose, so it runs in no execution and its write is not " +
        "collected (AspNetCoreRootProvider only keeps a controller's own IDisposable.Dispose out of its action roots)";

    /// <summary>The suite's startup: controllers registered and mapped, then the case's registrations.</summary>
    /// <param name="registrations">Statements appended to <c>Configure</c>.</param>
    private static string Startup(string registrations) => $$"""

        public static class Startup
        {
            public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
            {
                services.AddControllers();
                app.MapControllers();
                {{registrations}}
            }
        }
        """;

    /// <summary>A controller incrementing a counter it takes, with the given parameter attributes, registered as given.</summary>
    /// <param name="parameter">The controller's constructor parameter.</param>
    /// <param name="registration">The registration of the counter.</param>
    private static string Counted(string parameter, string registration) => $$"""
        public sealed class Counter { public int Value; }
        [ApiController, Route("hit")]
        public class HitController({{parameter}}) : ControllerBase { [HttpPost] public void Post() { /*@post*/counter.Value++; } }
        """ + Startup(registration);

    /// <summary>A controller calling a service that increments a counter it takes in its constructor, both registered as given.</summary>
    /// <param name="registrations">The registrations of the service and the counter.</param>
    private static string Captured(string registrations) => """
        public sealed class Counter { public int Value; }
        public sealed class Reports(Counter counter) { public void Hit() { /*@hit*/counter.Value++; } }
        [ApiController, Route("hit")]
        public class HitController(Reports reports) : ControllerBase { [HttpPost] public void Post() => reports.Hit(); }
        """ + Startup(registrations);

    /// <summary>A worker whose parallel iterations each create a scope and increment the counter it resolves, registered as given.</summary>
    /// <param name="registration">The registration of the counter.</param>
    private static string ScopePerIteration(string registration) => """
        public sealed class Counter { public int Value; }
        public sealed class Worker(IServiceScopeFactory scopes) : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                Parallel.For(0, 4, _ => { using var scope = scopes.CreateScope(); var counter = scope.ServiceProvider.GetRequiredService<Counter>(); /*@inc*/counter.Value++; });
                return Task.CompletedTask;
            }
        }
        """ + Startup(registration + " services.AddHostedService<Worker>();");

    /// <summary>A service whose constructor starts work that writes its field, read by a controller action, registered as given.</summary>
    /// <param name="registration">The registration of the service.</param>
    private static string Loader(string registration) => """
        public sealed class Loader
        {
            private int _loaded;
            public Loader() => _ = Task.Run(() => { /*@load*/_loaded = 1; });
            public int Read() => /*@read*/_loaded;
        }
        [ApiController, Route("load")]
        public class LoadController(Loader loader) : ControllerBase { [HttpGet] public int Get() => loader.Read(); }
        """ + Startup(registration);

    private const string LOAD_AGAINST_READ = "DCA1001 unprotected _loaded: Read Loader.Read @read | Write Loader..ctor @load";

    private const string POST_SELF = "DCA1002 unprotected Value: ReadModifyWrite HitController.Post @post | ReadModifyWrite HitController.Post @post";
    private const string HIT_SELF = "DCA1002 unprotected Value: ReadModifyWrite Reports.Hit @hit | ReadModifyWrite Reports.Hit @hit";

    /// <summary>Each probe by name: its source and the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/>
    /// writes them.</summary>
    private static readonly Dictionary<string, (string Source, string[] Truth)> Probes = new(StringComparer.Ordinal)
    {
        ["instance-registered-singleton"] = (Counted("Counter counter", "services.AddSingleton(new Counter());"), [POST_SELF]),
        ["keyed-singleton"] = (Counted("[FromKeyedServices(\"hits\")] Counter counter", "services.AddKeyedSingleton<Counter>(\"hits\");"), [POST_SELF]),
        ["transient-captured-by-singleton"] = (Captured("services.AddSingleton<Reports>(); services.AddTransient<Counter>();"), [HIT_SELF]),
        ["scoped-captured-by-singleton"] = (Captured("services.AddSingleton<Reports>(); services.AddScoped<Counter>();"), [HIT_SELF]),
        ["transient-taken-by-scoped"] = (Captured("services.AddScoped<Reports>(); services.AddTransient<Counter>();"), []),
        ["scope-per-parallel-iteration"] = (ScopePerIteration("services.AddScoped<Counter>();"), []),
        ["singleton-in-parallel-iterations"] = (ScopePerIteration("services.AddSingleton<Counter>();"),
            ["DCA1002 unprotected Value: ReadModifyWrite Worker.ExecuteAsync @inc | ReadModifyWrite Worker.ExecuteAsync @inc"]),
        ["scoped-disposed-under-work"] = ("""
            public sealed class Session : IDisposable { public int Value; public void Dispose() { /*@dispose*/Value = -1; } }
            [ApiController, Route("go")]
            public class GoController(Session session) : ControllerBase
            {
                [HttpPost] public void Post() { _ = Task.Run(async () => { await Task.Delay(100); /*@late*/session.Value++; }); }
            }
            """ + Startup("services.AddScoped<Session>();"),
            ["DCA1002 unprotected Value: ReadModifyWrite GoController.Post @late | Write Session.Dispose @dispose"]),
        ["spawn-in-scoped-constructor"] = (Loader("services.AddScoped<Loader>();"), [LOAD_AGAINST_READ]),
        ["spawn-in-singleton-constructor"] = (Loader("services.AddSingleton<Loader>();"), [LOAD_AGAINST_READ])
    };

    /// <summary>Inputs checked for what the analysis collects only, with no run-time truth: a keyed scoped service binds no object, as a
    /// keyed singleton does not (issue 432), so its empty answer says nothing about lifetimes; the check shows the binding is missing.
    /// It becomes a lifetime check once the counter's field is collected and two requests' counters are told apart.</summary>
    private static readonly Dictionary<string, string> Checks = new(StringComparer.Ordinal)
    {
        ["keyed-scoped-unbound"] = Counted("[FromKeyedServices(\"hits\")] Counter counter", "services.AddKeyedScoped<Counter>(\"hits\");")
    };

    /// <summary>The source of a probe or a check.</summary>
    /// <param name="name">The probe's or check's name.</param>
    private static string Input(string name) => Probes.TryGetValue(name, out var probe) ? probe.Source : Checks[name];

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["keyed-singleton"] = new([], KEYED_IS_NOT_ANALYSED),
        ["scoped-disposed-under-work"] = new([], CONTAINER_DISPOSAL_IS_NOT_MODELLED),
        ["spawn-in-scoped-constructor"] = new([], ConcurrencySourceProbeTests.SCOPED_CONSTRUCTOR_ORDERED)
    };

    /// <summary>For each probe whose claim rests on which accesses exist: the accesses that must be collected, as
    /// <see cref="HostedServiceProbe.DescribeAccess"/> writes them, and the resources at marked sites that must have none, as
    /// <c>resource @marker</c> (<c>*</c> for any resource).</summary>
    private static readonly Dictionary<string, (string[] Collected, string[] Lost)> AccessClaims = new(StringComparer.Ordinal)
    {
        ["keyed-singleton"] = (["Read counter HitController.Post @post"], ["Value @post"]),
        ["keyed-scoped-unbound"] = (["Read counter HitController.Post @post"], ["Value @post"]),
        ["transient-taken-by-scoped"] = (["ReadModifyWrite Value Reports.Hit @hit"], []),
        ["scope-per-parallel-iteration"] = (["ReadModifyWrite Value Worker.ExecuteAsync @inc"], []),
        ["scoped-disposed-under-work"] = (["ReadModifyWrite Value GoController.Post @late"], ["* @dispose"]),
        ["spawn-in-scoped-constructor"] = (["Read _loaded Loader.Read @read", "Write _loaded Loader..ctor @load"], [])
    };

    /// <summary>The names of the probes.</summary>
    public static TheoryData<string> Names() => new(Probes.Keys);

    /// <summary>The names of the probes with a claim about accesses.</summary>
    public static TheoryData<string> AccessClaimNames() => new(AccessClaims.Keys);

    /// <summary>The probe's findings are what run time calls for, or the answer its difference pins, read as sets.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(Names))]
    public async Task Probe_answers_as_run_time_calls_for_or_as_its_named_difference(string name)
    {
        var (source, truth) = Probes[name];

        var answer = await HostedServiceProbe.AnalyzeAsync(source, new ProbeInputs());

        HostedServiceProbe.AssertAnswer(answer.Findings, truth, Differences.GetValueOrDefault(name));
    }

    /// <summary>The probe collected the accesses its claim names and none on the resources it says were lost at their marked sites.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(AccessClaimNames))]
    public async Task Probe_collects_the_accesses_its_claim_names(string name)
    {
        var (collected, lost) = AccessClaims[name];

        var answer = await HostedServiceProbe.AnalyzeAsync(Input(name), new ProbeInputs());

        HostedServiceProbe.AssertAccessClaims(answer, collected, lost);
    }

    /// <summary>A difference names a probe, pins an answer whose set differs from its truth and says why.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth() =>
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);
}
