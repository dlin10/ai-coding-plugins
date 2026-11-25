using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of the generic host's lifecycle on .NET 10 (issue 506), each with what run time calls for. A
/// <c>BackgroundService</c> runs all of <c>ExecuteAsync</c> on a background thread, its synchronous part included, so that part overlaps
/// the hosted services started after it ("BackgroundService runs all of ExecuteAsync as a Task", .NET 10 breaking changes); the
/// startup code that registers the services runs before the host starts any of them; and by default the host starts hosted services
/// one at a time in their order of registration, starting none until the previous <c>StartAsync</c> completes
/// (<c>HostOptions.ServicesStartConcurrently</c>, default <c>false</c>; "Background tasks with hosted services in ASP.NET Core"), so
/// two synchronous starts never overlap. The lifecycle uncertainty of SPEC 11 and ADR 0042 is scoped to the methods of one service; the
/// class marks uncertainty so that a probe's answer shows whether a finding carries one. Every probe runs through the whole analysis (<see cref="HostedServiceProbe.AnalyzeAsync(string,
/// ProbeInputs)"/>), names each finding's pair of sides and marks a finding that carries an uncertainty; a probe the analysis answers
/// otherwise is named in <see cref="Differences"/> with its answer and why.</summary>
public sealed class HostLifecycleProbeTests
{
    /// <summary>Why two synchronous <c>StartAsync</c> bodies pair.</summary>
    private const string START_ORDER_IS_NOT_MODELLED =
        "issue 519: the host starts hosted services one after another, but the analysis models no order " +
        "between two services' lifecycle methods, so their StartAsync bodies pair, a false race; the lifecycle uncertainty of SPEC 11 " +
        "and ADR 0042 covers the methods of one service only, so this finding carries none";

    /// <summary>What the probes give besides their source: uncertain findings are marked.</summary>
    private static readonly ProbeInputs MarkUncertain = new([], [], true);

    /// <summary>A shared state, a <c>BackgroundService</c> writing it in <c>ExecuteAsync</c> and a hosted service registered after it
    /// writing it in <c>StartAsync</c>.</summary>
    /// <param name="execute">The declaration of <c>ExecuteAsync</c> after <c>protected override</c>, which writes <c>state.Value</c>.</param>
    private static string WorkerThenStarter(string execute) => $$"""
        public sealed class State { public int Value; }
        public sealed class Worker(State state) : BackgroundService
        {
            protected override {{execute}}
        }
        public sealed class Starter(State state) : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken) { state.Value = 2; return Task.CompletedTask; }
            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
        public static class Startup
        {
            public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
            {
                services.AddSingleton<State>(); services.AddHostedService<Worker>(); services.AddHostedService<Starter>();
            }
        }
        """;

    /// <summary>The race of the worker's write with the later service's start.</summary>
    private const string EXECUTE_AGAINST_START = "DCA1001 unprotected Value: Write Starter.StartAsync | Write Worker.ExecuteAsync";

    /// <summary>Each probe by name: its source and the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/>
    /// writes them.</summary>
    private static readonly Dictionary<string, (string Source, string[] Truth)> Probes = new(StringComparer.Ordinal)
    {
        ["execute-before-its-first-await-against-a-later-start"] =
            (WorkerThenStarter("Task ExecuteAsync(CancellationToken stoppingToken) { state.Value = 1; return Task.CompletedTask; }"), [EXECUTE_AGAINST_START]),
        ["execute-after-its-first-await-against-a-later-start"] =
            (WorkerThenStarter("async Task ExecuteAsync(CancellationToken stoppingToken) { await Task.Yield(); state.Value = 1; }"), [EXECUTE_AGAINST_START]),
        ["execute-before-its-first-await-against-startup"] = ("""
            public static class Shared { public static int Value; }
            public sealed class Worker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { Shared.Value = 1; return Task.CompletedTask; }
            }
            public sealed class Marker { }
            public static class Startup
            {
                public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                {
                    services.AddSingleton<Marker>(_ => new Marker());
                    Shared.Value = 3;
                    services.AddHostedService<Worker>();
                }
            }
            """, []),
        ["two-starts-in-sequence"] = ("""
            public sealed class State { public int Value; }
            public sealed class FirstStarter(State state) : IHostedService
            {
                public Task StartAsync(CancellationToken cancellationToken) { state.Value = 1; return Task.CompletedTask; }
                public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public sealed class SecondStarter(State state) : IHostedService
            {
                public Task StartAsync(CancellationToken cancellationToken) { state.Value = 2; return Task.CompletedTask; }
                public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public static class Startup
            {
                public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                {
                    services.AddSingleton<State>(); services.AddHostedService<FirstStarter>(); services.AddHostedService<SecondStarter>();
                }
            }
            """, [])
    };

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["two-starts-in-sequence"] = new(["DCA1001 unprotected Value: Write FirstStarter.StartAsync | Write SecondStarter.StartAsync"], START_ORDER_IS_NOT_MODELLED)
    };

    /// <summary>For each probe whose truth is that two writes do not pair: the two accesses, as
    /// <see cref="HostedServiceProbe.DescribeAccess"/> writes them, that must both be collected for the empty pair to mean an order.</summary>
    private static readonly Dictionary<string, string[]> AccessClaims = new(StringComparer.Ordinal)
    {
        ["execute-before-its-first-await-against-startup"] = ["Write Value Startup.Configure", "Write Value Worker.ExecuteAsync"],
        ["two-starts-in-sequence"] = ["Write Value FirstStarter.StartAsync", "Write Value SecondStarter.StartAsync"]
    };

    /// <summary>The names of the probes.</summary>
    public static TheoryData<string> Names() => new(Probes.Keys);

    /// <summary>The names of the probes with a claim about accesses.</summary>
    public static TheoryData<string> AccessClaimNames() => new(AccessClaims.Keys);

    /// <summary>Both writes a probe says do not pair are collected.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(AccessClaimNames))]
    public async Task Probe_collects_both_writes_its_truth_orders(string name)
    {
        var answer = await HostedServiceProbe.AnalyzeAsync(Probes[name].Source, MarkUncertain);

        HostedServiceProbe.AssertAccessClaims(answer, AccessClaims[name], []);
    }

    /// <summary>The probe's findings are what run time calls for, or the answer its difference pins, read as sets.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(Names))]
    public async Task Probe_answers_as_run_time_calls_for_or_as_its_named_difference(string name)
    {
        var (source, truth) = Probes[name];

        var answer = await HostedServiceProbe.AnalyzeAsync(source, MarkUncertain);

        HostedServiceProbe.AssertAnswer(answer.Findings, truth, Differences.GetValueOrDefault(name));
    }

    /// <summary>A difference names a probe, pins an answer whose set differs from its truth and says why.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth() =>
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);
}
