using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of ASP.NET Core state shared across requests (issue 506), each with what run time calls for: every request
/// runs concurrently with every other, so a minimal-API handler that captures a local of the method mapping it shares that local, and
/// what it holds, with every request to any handler capturing it; a convention-based middleware is constructed once and its fields
/// are shared by every request, an <c>IMiddleware</c> is resolved per request with the lifetime it is registered with (a singleton is
/// shared, a transient is not), and an inline middleware's lambda shares what it captures, all in the pipeline of every request; a
/// filter attribute's instance is part of the action's metadata and runs for every request to the action ("Filters in ASP.NET Core":
/// a factory's <c>IsReusable</c> only hints at reuse; that a plain attribute instance is shared is inferred from the attribute being
/// metadata, not read in the docs). The services are registered in <c>ConfigureServices</c> and the pipeline is built in
/// <c>Configure</c>, as the host composes them. <c>UseMiddleware</c> and <c>IMiddleware</c> come from the framework's
/// <c>Microsoft.AspNetCore.Http.Abstractions</c> stub, <c>ActionFilterAttribute</c> from its <c>Microsoft.AspNetCore.Mvc.Core</c> stub
/// and the filter contracts from <see cref="StubAssemblies.MVC_ABSTRACTIONS"/>. Every probe runs through the whole analysis
/// (<see cref="HostedServiceProbe.AnalyzeAsync(string, ProbeInputs)"/>) and names each finding's pair of sites; a probe the analysis
/// answers otherwise is named in <see cref="Differences"/> with its answer and why, and where the claim is that no root is discovered,
/// <see cref="RootClaims"/> asserts the roots themselves.</summary>
public sealed class AspNetCoreProbeTests
{
    /// <summary>Why an object reached through a minimal-API handler's captured local makes no access.</summary>
    private const string CAPTURED_OBJECT_IS_NOT_SEEN =
        "issue 515: an object the mapping method creates and a minimal-API handler reaches through a " +
        "captured local makes no access in the handler, so neither its fields nor a collection it is pair; a value held in the " +
        "closure itself pairs between requests to one handler but not between two handlers that capture it";

    /// <summary>Why middleware and filter state shared across requests is never paired.</summary>
    private const string NO_MIDDLEWARE_OR_FILTER_ROOTS =
        "limit: SPEC 11 gives ASP.NET Core roots for controllers, minimal APIs and gRPC only, so a middleware's InvokeAsync, an " +
        "inline middleware's lambda (handed to IApplicationBuilder.Use, an opaque call) and a filter's methods run in no execution, " +
        "though the routed action they wrap does";

    /// <summary>A side of a pair in a handler a minimal API maps, which stands in the mapping method.</summary>
    private const string CONFIGURE = "Startup.Configure";

    /// <summary>A <c>Startup</c> whose <c>Configure</c> maps endpoints with the given statements, after a factory registration in
    /// <c>ConfigureServices</c> so that startup is walked.</summary>
    /// <param name="statements">The statements of <c>Configure</c>.</param>
    private static string Endpoints(string statements) => $$"""
        public sealed class Marker { }
        public static class Startup
        {
            public static void ConfigureServices(IServiceCollection services) => services.AddSingleton<Marker>(_ => new Marker());
            public static void Configure(IEndpointRouteBuilder app)
            {
                {{statements}}
            }
        }
        """;

    /// <summary>A <c>Startup</c> that registers the given services and builds the pipeline with the given statements.</summary>
    /// <param name="services">The registrations of <c>ConfigureServices</c>.</param>
    /// <param name="statements">The statements of <c>Configure</c>.</param>
    private static string Pipeline(string services, string statements) => $$"""
        public sealed class Marker { }
        public static class Startup
        {
            public static void ConfigureServices(IServiceCollection services) { services.AddSingleton<Marker>(_ => new Marker()); {{services}} }
            public static void Configure(IApplicationBuilder app)
            {
                {{statements}}
            }
        }
        """;

    /// <summary>A middleware with a counter, as an <c>IMiddleware</c> or by convention.</summary>
    /// <param name="imiddleware">Whether it implements <c>IMiddleware</c>.</param>
    private static string CountingMiddleware(bool imiddleware) => imiddleware
        ? """
          public sealed class CountingMiddleware : IMiddleware
          {
              private int _count;
              public Task InvokeAsync(HttpContext context, RequestDelegate next) { _count++; return next(context); }
          }

          """
        : """
          public sealed class CountingMiddleware
          {
              private readonly RequestDelegate _next;
              private int _count;
              public CountingMiddleware(RequestDelegate next) => _next = next;
              public Task InvokeAsync(HttpContext context) { _count++; return _next(context); }
          }

          """;

    /// <summary>The finding run time calls for on a middleware's counter.</summary>
    private const string MIDDLEWARE_COUNT =
        "DCA1002 unprotected _count: ReadModifyWrite CountingMiddleware.InvokeAsync | ReadModifyWrite CountingMiddleware.InvokeAsync";

    /// <summary>The finding of two requests to the routed action, which shows that the action is a root.</summary>
    private const string ACTION_HITS = "DCA1002 unprotected Value: ReadModifyWrite CountedController.Post | ReadModifyWrite CountedController.Post";

    /// <summary>Each probe by name: its source, the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes
    /// them, and the stub assemblies outside the default ones it references.</summary>
    private static readonly Dictionary<string, (string Source, string[] Truth, string[] Stubs)> Probes = new(StringComparer.Ordinal)
    {
        ["minimal-api-handler-increments-a-captured-local"] = (Endpoints("var hits = 0; app.MapGet(\"/hit\", () => ++hits);"),
            [$"DCA1002 unprotected hits: ReadModifyWrite {CONFIGURE} | ReadModifyWrite {CONFIGURE}"], []),
        ["minimal-api-handler-increments-its-own-local"] = (Endpoints("app.MapGet(\"/hit\", () => { var hits = 0; return ++hits; });"), [], []),
        ["minimal-api-handlers-write-and-read-a-captured-local"] =
            (Endpoints("var hits = 0; app.MapPost(\"/hit\", () => { /*@post*/hits = 1; }); app.MapGet(\"/hit\", () => { var seen = /*@get*/hits; return seen; });"),
             [$"DCA1001 unprotected hits: Read {CONFIGURE} @get | Write {CONFIGURE} @post", $"DCA1001 unprotected hits: Write {CONFIGURE} @post | Write {CONFIGURE} @post"], []),
        ["minimal-api-handler-adds-to-a-captured-list"] =
            (Endpoints("var seen = new List<string>(); app.MapPost(\"/seen\", (string name) => /*@add*/seen.Add(name));"),
             [$"DCA1001 unprotected seen structure: Write {CONFIGURE} @add | Write {CONFIGURE} @add", $"DCA1001 unprotected seen.[?]: Write {CONFIGURE} @add | Write {CONFIGURE} @add"], []),
        ["minimal-api-handlers-add-to-and-count-a-captured-list"] =
            (Endpoints("var seen = new List<string>(); app.MapPost(\"/seen\", (string name) => /*@add*/seen.Add(name)); app.MapGet(\"/seen\", () => /*@count*/seen.Count);"),
             [$"DCA1001 unprotected seen structure: Read {CONFIGURE} @count | Write {CONFIGURE} @add", $"DCA1001 unprotected seen structure: Write {CONFIGURE} @add | Write {CONFIGURE} @add",
              $"DCA1001 unprotected seen.[?]: Write {CONFIGURE} @add | Write {CONFIGURE} @add"], []),
        ["minimal-api-handler-increments-a-field-of-a-captured-object"] =
            ("public sealed class Box { public int Value; }\n" + Endpoints("var box = new Box(); app.MapPost(\"/box\", () => /*@inc*/box.Value++);"),
             [$"DCA1002 unprotected Value: ReadModifyWrite {CONFIGURE} @inc | ReadModifyWrite {CONFIGURE} @inc"], []),
        ["minimal-api-handlers-add-to-and-count-a-static-list"] =
            ("public static class Registry { public static readonly List<string> Seen = new(); }\n" +
             Endpoints("app.MapPost(\"/seen\", (string name) => /*@add*/Registry.Seen.Add(name)); app.MapGet(\"/seen\", () => /*@count*/Registry.Seen.Count);"),
             [$"DCA1001 unprotected Seen structure: Read {CONFIGURE} @count | Write {CONFIGURE} @add", $"DCA1001 unprotected Seen structure: Write {CONFIGURE} @add | Write {CONFIGURE} @add",
              $"DCA1001 unprotected Seen.[?]: Write {CONFIGURE} @add | Write {CONFIGURE} @add"], []),
        ["inline-middleware-increments-a-captured-local"] =
            (Pipeline(string.Empty, "var count = 0; app.Use(next => async context => { count++; await next(context); });"),
             ["DCA1002 unprotected count: ReadModifyWrite Startup.Configure | ReadModifyWrite Startup.Configure"], []),
        ["convention-middleware-increments-its-field"] = (CountingMiddleware(false) + Pipeline(string.Empty, "app.UseMiddleware<CountingMiddleware>();"),
                                                          [MIDDLEWARE_COUNT], []),
        ["singleton-imiddleware-increments-its-field"] =
            (CountingMiddleware(true) + Pipeline("services.AddSingleton<CountingMiddleware>();", "app.UseMiddleware<CountingMiddleware>();"),
             [MIDDLEWARE_COUNT], []),
        ["transient-imiddleware-increments-its-field"] =
            (CountingMiddleware(true) + Pipeline("services.AddTransient<CountingMiddleware>();", "app.UseMiddleware<CountingMiddleware>();"), [], []),
        ["filter-attribute-of-a-routed-action-increments-its-field"] = ("""
            public static class Hits { public static int Value; }
            public sealed class CountAttribute : Microsoft.AspNetCore.Mvc.Filters.ActionFilterAttribute
            {
                private int _count;
                public override void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context) => _count++;
            }
            [ApiController, Route("counted")]
            public class CountedController : ControllerBase
            {
                [HttpPost, Count] public void Post() => Hits.Value++;
            }
            public static class Startup
            {
                public static void ConfigureServices(IServiceCollection services) => services.AddControllers();
                public static void Configure(IEndpointRouteBuilder app) => app.MapControllers();
            }
            """, [ACTION_HITS, "DCA1002 unprotected _count: ReadModifyWrite CountAttribute.OnActionExecuting | ReadModifyWrite CountAttribute.OnActionExecuting"],
            [StubAssemblies.MVC_ABSTRACTIONS])
    };

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["minimal-api-handlers-write-and-read-a-captured-local"] =
            new([$"DCA1001 unprotected hits: Write {CONFIGURE} @post | Write {CONFIGURE} @post"], CAPTURED_OBJECT_IS_NOT_SEEN),
        ["minimal-api-handler-adds-to-a-captured-list"] = new([], CAPTURED_OBJECT_IS_NOT_SEEN),
        ["minimal-api-handlers-add-to-and-count-a-captured-list"] = new([], CAPTURED_OBJECT_IS_NOT_SEEN),
        ["minimal-api-handler-increments-a-field-of-a-captured-object"] = new([], CAPTURED_OBJECT_IS_NOT_SEEN),
        ["inline-middleware-increments-a-captured-local"] = new([], NO_MIDDLEWARE_OR_FILTER_ROOTS),
        ["convention-middleware-increments-its-field"] = new([], NO_MIDDLEWARE_OR_FILTER_ROOTS),
        ["singleton-imiddleware-increments-its-field"] = new([], NO_MIDDLEWARE_OR_FILTER_ROOTS),
        ["filter-attribute-of-a-routed-action-increments-its-field"] = new([ACTION_HITS], NO_MIDDLEWARE_OR_FILTER_ROOTS)
    };

    /// <summary>The probes whose claim is about roots: the roots that must be there, as <c>kind member</c>, and the members no root
    /// may have.</summary>
    private static readonly Dictionary<string, (string[] Present, string[] AbsentMembers)> RootClaims = new(StringComparer.Ordinal)
    {
        ["inline-middleware-increments-a-captured-local"] = ([], [CONFIGURE]),
        ["convention-middleware-increments-its-field"] = ([], ["CountingMiddleware.InvokeAsync"]),
        ["singleton-imiddleware-increments-its-field"] = ([], ["CountingMiddleware.InvokeAsync"]),
        ["filter-attribute-of-a-routed-action-increments-its-field"] = (["controller-action CountedController.Post"],
                                                                        ["CountAttribute.OnActionExecuting", "CountAttribute.OnActionExecuted"])
    };

    /// <summary>For each minimal-API probe of a missed pair: the accesses that were collected, as
    /// <see cref="HostedServiceProbe.DescribeAccess"/> writes them, and the resources at marked sites that have none, as
    /// <c>resource @marker</c>, so that the probe says which side of the missing pair is gone and which is there.</summary>
    private static readonly Dictionary<string, (string[] Collected, string[] Lost)> AccessClaims = new(StringComparer.Ordinal)
    {
        ["minimal-api-handlers-write-and-read-a-captured-local"] = ([$"Read hits {CONFIGURE} @get", $"Write hits {CONFIGURE} @post"], []),
        ["minimal-api-handler-adds-to-a-captured-list"] = ([$"Read seen {CONFIGURE} @add"], ["seen structure @add", "seen.[?] @add"]),
        ["minimal-api-handlers-add-to-and-count-a-captured-list"] = ([$"Read seen {CONFIGURE} @add", $"Read seen {CONFIGURE} @count"],
                                                                     ["seen structure @add", "seen.[?] @add", "seen structure @count"]),
        ["minimal-api-handler-increments-a-field-of-a-captured-object"] = ([$"Read box {CONFIGURE} @inc"], ["Value @inc"])
    };

    /// <summary>The names of the probes.</summary>
    public static TheoryData<string> Names() => new(Probes.Keys);

    /// <summary>The names of the probes with a claim about accesses.</summary>
    public static TheoryData<string> AccessClaimNames() => new(AccessClaims.Keys);

    /// <summary>The probe collected the accesses its claim names and none on the resources it says were lost at their marked sites.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(AccessClaimNames))]
    public async Task Probe_collects_the_accesses_its_claim_names(string name)
    {
        var (collected, lost) = AccessClaims[name];

        var answer = await HostedServiceProbe.AnalyzeAsync(Probes[name].Source, new ProbeInputs([], Probes[name].Stubs, false));

        HostedServiceProbe.AssertAccessClaims(answer, collected, lost);
    }

    /// <summary>The names of the probes with a claim about roots.</summary>
    public static TheoryData<string> RootClaimNames() => new(RootClaims.Keys);

    /// <summary>The probe's findings are what run time calls for, or the answer its difference pins, read as sets.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(Names))]
    public async Task Probe_answers_as_run_time_calls_for_or_as_its_named_difference(string name)
    {
        var (source, truth, stubs) = Probes[name];

        var answer = await HostedServiceProbe.AnalyzeAsync(source, new ProbeInputs([], stubs, false));

        HostedServiceProbe.AssertAnswer(answer.Findings, truth, Differences.GetValueOrDefault(name));
    }

    /// <summary>The roots of a probe whose claim is about roots are the ones it names, and none has a member it rules out.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(RootClaimNames))]
    public async Task Probe_discovers_the_roots_its_claim_names(string name)
    {
        var (present, absentMembers) = RootClaims[name];

        var answer = await HostedServiceProbe.AnalyzeAsync(Probes[name].Source, new ProbeInputs([], Probes[name].Stubs, false));

        Assert.All(present, root => Assert.Contains(root, answer.Roots));
        Assert.All(absentMembers, member => Assert.DoesNotContain(answer.Roots, root => root.EndsWith(" " + member, StringComparison.Ordinal)));
    }

    /// <summary>A difference names a probe, pins an answer whose set differs from its truth and says why.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth() =>
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);
}
