using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Probe fixtures of what runs code concurrently (issue 533): timers, the generic host, request roots, library callbacks,
/// producers and events, each with what run time calls for. A <c>System.Threading.Timer</c> callback runs on the thread pool and may
/// run on two threads at once when slower than its period, whichever overload sets the period (Timer class remarks); a
/// <c>TimeProvider.CreateTimer</c> callback carries the same warning (TimeProvider.CreateTimer remarks); a <c>System.Timers.Timer</c>'s
/// <c>Elapsed</c> may be raised again while a handler runs and can occur after <c>Stop</c> or <c>Dispose</c>, because its raise is
/// always queued to the pool (Timer.SynchronizingObject and Elapsed remarks). Hosted services start one at a time in registration
/// order, the web server being itself a hosted service (.NET Generic Host for ASP.NET Core), and a hosted service's <c>StartAsync</c> is
/// called before the server is started (Background tasks with hosted services in ASP.NET Core); the fixture's registrations stand for
/// registrations on <c>WebApplicationBuilder.Services</c>, which <c>Build</c> follows with the server's own hosted service, so every
/// service the fixture registers starts before the server (the order the Demo's <c>hosted-start-vs-action</c> also has); by default
/// services stop one at a time in reverse order (<c>HostOptions.ServicesStopConcurrently</c>, default <c>false</c>); callbacks on <c>ApplicationStopping</c> run while requests may still be in flight (IHostApplicationLifetime). Each
/// request runs concurrently with every other, and a SignalR hub is created for each invocation, invocations of different connections
/// running at once (Use hubs in ASP.NET Core SignalR; HubOptions). <c>CancellationTokenSource.Cancel</c> runs registered callbacks
/// synchronously on the cancelling thread, and disposing a registration waits for its running callback (CancellationTokenSource.Cancel;
/// CancellationTokenRegistration.Dispose); <c>FileSystemWatcher</c> handlers run on pool threads (FileSystemWatcher.SynchronizingObject);
/// <c>HttpClient.SendAsync</c> may be called concurrently, so one handler chain serves concurrent requests (HttpClient remarks);
/// an <c>ActionBlock</c> processes one message at a time unless <c>MaxDegreeOfParallelism</c> is raised
/// (ExecutionDataflowBlockOptions.MaxDegreeOfParallelism); an async iterator runs its body only as an enumerator moves, so two
/// enumerations run it twice and a sequence created and never enumerated runs none of it (C# specification, async iterators); a C# event's raise invokes its handlers on the raising thread (C# specification, field-like events).
/// Every truth is the run-time behaviour of the program the fixture stands for, its registrations and mappings taking effect as written
/// in the suite's <c>Startup</c> shape. Each probe runs through the whole analysis (<see cref="HostedServiceProbe.AnalyzeAsync(string,
/// ProbeInputs)"/>) and names each finding's pair of sides by marked site; a probe the analysis answers otherwise is named in
/// <see cref="Differences"/> with its answer and why.</summary>
public sealed class ConcurrencySourceProbeTests
{
    /// <summary>Why a request's read does not pair with the timer its scoped service's constructor started.</summary>
    internal const string SCOPED_CONSTRUCTOR_ORDERED =
        "issue 542: the pair of the action's read with the work its scoped service's " +
        "constructor started is not reported though both accesses are collected, while the same class as a singleton, and work started " +
        "from a method the action calls, pair; read in the code, not asserted by a test: the scoped service is constructed inside the " +
        "action's root execution, HappensBefore.Path asks whether the action's read precedes the constructor's spawn anchor, and " +
        "Flow.Precedes accepts it when it finds no path from the anchor back to the read, the construction entry and the root entry " +
        "being two starts with no edge between them";

    /// <summary>Why a hosted service's synchronous start pairs with a request.</summary>
    private const string START_BEFORE_SERVER_IS_NOT_MODELLED =
        "issue 540: each lifecycle method is a root of its own; HostingRootProvider gives it empty " +
        "OrderingConstraints, which nothing reads (SPEC 11), and the only order between roots is the end of startup, which holds " +
        "constructions only (ADR 0006), so StartAsync's synchronous write pairs with every request, though a service registered before " +
        "the server's own hosted service completes StartAsync before the server starts; ConflictFindings names the lifecycle " +
        "uncertainty only for two methods of one service, so this pair carries none";

    /// <summary>Why two hosted services' synchronous stops pair.</summary>
    private const string STOP_ORDER_IS_NOT_MODELLED =
        "issue 541: as issue 519 for starts, each StopAsync is a root with no order between roots " +
        "(HostingRootProvider's OrderingConstraints are empty and unread, SPEC 11), though by default the host stops services one at " +
        "a time in reverse order; the lifecycle uncertainty covers two methods of one service only, so the pair carries none";

    /// <summary>Why an async iterator created and never enumerated makes the accesses of its body.</summary>
    private const string ASYNC_ITERATOR_RUNS_AT_THE_CALL =
        "limit: the analysis walks an async iterator's body at the call, in the caller's execution, whether or not the sequence is " +
        "enumerated (pinned by SpawnExecutionTests.Async_iterator_call_is_no_spawn_and_its_access_pairs_with_the_caller_s_spawn), so a " +
        "sequence each request only creates makes the body's write in every request; which code walks it at the call is not established " +
        "(SPEC 2.13 runs an iterator body at enumeration)";

    /// <summary>Why a worker registered by a factory is no root.</summary>
    private const string FACTORY_WORKER_IS_UNSUPPORTED =
        "limit: DiIndex records AddHostedService with a factory as an unsupported registration (\"factory\") and adds no hosted " +
        "service (DiIndexTests.Hosted_service_factory_is_unsupported_and_not_counted), so the worker's ExecuteAsync is no root and " +
        "its write is not collected";

    /// <summary>Why a periodic <c>ITimer</c>'s callback does not overlap itself.</summary>
    private const string ITIMER_IS_NOT_A_TIMER =
        "limit: TimeProvider.CreateTimer is not recognised as a timer (IrLowering recognises System.Threading.Timer and " +
        "System.Timers.Timer only) and has no model, so its callback is a delegate handed to an unresolved call, run in an unknown " +
        "execution (SPEC 2.14, ADR 0011) that ExecutionModel.FinishUnknownDelegateCalls makes serialized when one once-running " +
        "execution hands it over at one site, as StartAsync does here; the periodic callback's pair with itself is lost";

    /// <summary>Why an <c>ActionBlock</c> with a raised degree of parallelism does not overlap itself.</summary>
    private const string ACTION_BLOCK_HANDED_ONCE =
        "limit: ActionBlock<T> has no model, so its action is a delegate handed to an unresolved call, run in an unknown execution " +
        "(SPEC 2.14, ADR 0011) that ExecutionModel.FinishUnknownDelegateCalls makes serialized when one once-running execution hands it " +
        "over at one site, as the constructor does here, whatever MaxDegreeOfParallelism says; the cause of issue 530 (PLINQ, Progress<T>)";

    /// <summary>Why a <c>DelegatingHandler</c>'s <c>SendAsync</c> makes no access.</summary>
    private const string OVERRIDE_CALLED_BY_THE_LIBRARY =
        "limit: HttpClient calls the handler chain's SendAsync inside the library; no root provider makes a DelegatingHandler override " +
        "a root (ProviderRegistry holds the ASP.NET Core and hosting providers only) and no library body is followed into a program " +
        "override, so the override is never reached and its accesses are not collected";

    /// <summary>Why a hub method or a page handler is no root.</summary>
    private const string NO_HUB_OR_PAGE_ROOTS =
        "limit: SPEC 11 gives ASP.NET Core roots for controller actions, minimal APIs and gRPC methods only (AspNetCoreRootProvider), " +
        "so a SignalR hub method and a Razor Pages handler are no roots and their accesses are not collected";

    /// <summary>Why a callback registered on a token does not pair with its creator over an object the creator made.</summary>
    private const string CAPTURE_STAYS_CONFINED =
        "limit: SPEC 2.14 (ADR 0011): a delegate handed to an unresolved call touches what it captures of its creator's own objects as " +
        "the creator does, so the box stays confined to the request and the callback's write does not pair with the request's, though " +
        "Cancel from another request runs the callback on that request's thread while this one writes";

    /// <summary>What the probes reference besides the fixture's framework stubs.</summary>
    private static readonly string[] SignalR = [StubAssemblies.SIGNALR_CORE, StubAssemblies.SIGNALR];

    private const string STATE = "public sealed class State { public int Value; }\n";

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

    /// <summary>A hosted service with the given members and lifecycle bodies.</summary>
    /// <param name="name">The class name.</param>
    /// <param name="fields">Its fields and constructor.</param>
    /// <param name="start">The body of <c>StartAsync</c>.</param>
    /// <param name="stop">The body of <c>StopAsync</c>.</param>
    private static string Hosted(string name, string fields, string start, string stop = "return Task.CompletedTask;") => $$"""
        public sealed class {{name}} : IHostedService
        {
            {{fields}}
            public Task StartAsync(CancellationToken cancellationToken) { {{start}} }
            public Task StopAsync(CancellationToken cancellationToken) { {{stop}} }
        }
        """;

    /// <summary>A hosted service whose constructor creates a <c>System.Threading.Timer</c> that starts inactive, and whose start
    /// activates it with the given <c>Change</c>.</summary>
    /// <param name="change">The arguments of <c>Change</c>.</param>
    private static string Reactivated(string change) => STATE + Hosted("Ticker",
        "private readonly State _state; private readonly Timer _timer; public Ticker(State state) { _state = state; _timer = new Timer(_ => { /*@tick*/_state.Value++; }, null, Timeout.Infinite, Timeout.Infinite); }",
        $"_timer.Change({change}); return Task.CompletedTask;") + Startup("services.AddSingleton<State>(); services.AddHostedService<Ticker>();");

    /// <summary>A service whose own timer increments its field, read by a controller action, registered as given.</summary>
    /// <param name="start">Where the timer is created: <c>ctor</c> or <c>method</c>, a method the action calls first.</param>
    /// <param name="registration">The registration of the service.</param>
    private static string Poller(string start, string registration) => (start == "ctor" ? """
        public sealed class Poller
        {
            private int _ticks; private readonly Timer _timer;
            public Poller() => _timer = new Timer(_ => { /*@tick*/_ticks++; }, null, 0, 1000);
            public void Start() { }
            public int Read() => /*@read*/_ticks;
        }
        """ : """
        public sealed class Poller
        {
            private int _ticks; private Timer? _timer;
            public void Start() => _timer = new Timer(_ => { /*@tick*/_ticks++; }, null, 0, 1000);
            public int Read() => /*@read*/_ticks;
        }
        """) + """

        [ApiController, Route("poll")]
        public class PollController(Poller poller) : ControllerBase { [HttpGet] public int Get() { poller.Start(); return poller.Read(); } }
        """ + Startup(registration);

    /// <summary>A hosted service creating a <c>TimeProvider</c> timer in its start, with the given period.</summary>
    /// <param name="period">The period argument.</param>
    private static string ProvidedTimer(string period) => STATE + Hosted("Ticker",
        "private readonly State _state; private ITimer? _timer; public Ticker(State state) => _state = state;",
        $"_timer = TimeProvider.System.CreateTimer(_ => {{ /*@tick*/_state.Value++; }}, null, TimeSpan.Zero, {period}); return Task.CompletedTask;")
        + Startup("services.AddSingleton<State>(); services.AddHostedService<Ticker>();");

    /// <summary>A <c>BackgroundService</c> writing after a suspension, registered as given, against an action reading the state.</summary>
    /// <param name="registration">The registration of the worker.</param>
    private static string Worker(string registration) => STATE + """
        public sealed class Worker(State state) : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken stoppingToken) { await Task.Yield(); /*@work*/state.Value = 1; }
        }
        [ApiController, Route("state")]
        public class StateController(State state) : ControllerBase { [HttpGet] public int Get() => /*@get*/state.Value; }
        """ + Startup("services.AddSingleton<State>(); " + registration);

    /// <summary>An action registering a callback on a shared token over a box it creates, then writing the box; another action
    /// cancels the token.</summary>
    /// <param name="register">The registration statement, with <c>{0}</c> standing for the callback.</param>
    private static string Registered(string register) => """
        public sealed class Box { public int Value; }
        public sealed class Shutdown { public readonly CancellationTokenSource Source = new(); }
        [ApiController, Route("job")]
        public class JobController(Shutdown shutdown) : ControllerBase
        {
            [HttpPost] public void Post() { var box = new Box();
        """ + register.Replace("{0}", "() => { /*@callback*/box.Value = 1; }", StringComparison.Ordinal) + """
         /*@after*/box.Value = 2; }
            [HttpDelete] public void Delete() => shutdown.Source.Cancel();
        }
        """ + Startup("services.AddSingleton<Shutdown>();");

    /// <summary>A singleton with an async iterator incrementing its field, and an action using the sequence as given.</summary>
    /// <param name="body">The body of the action, which returns <c>Task&lt;int&gt;</c>.</param>
    private static string Feed(string body) => """
        public sealed class Feed
        {
            public int Produced;
            public async IAsyncEnumerable<int> ReadAsync() { for (var i = 0; i < 3; i++) { await Task.Yield(); /*@produce*/Produced++; yield return i; } }
        }
        [ApiController, Route("feed")]
        public class FeedController(Feed feed) : ControllerBase { [HttpGet] public async Task<int> Get() {
        """ + body + " } }" + Startup("services.AddSingleton<Feed>();");

    /// <summary>A hosted service whose constructor builds an <c>ActionBlock</c> incrementing the state, with the given options.</summary>
    /// <param name="options">The trailing arguments of the block's constructor.</param>
    private static string Block(string options) => STATE + Hosted("Pump",
        $"private readonly System.Threading.Tasks.Dataflow.ActionBlock<int> _block; public Pump(State state) => _block = new(_ => {{ /*@item*/state.Value++; }}{options});",
        "_block.Post(1); _block.Post(2); return Task.CompletedTask;")
        + Startup("services.AddSingleton<State>(); services.AddHostedService<Pump>();");

    private const string TICK_SELF_IN_CTOR = "DCA1002 unprotected Value: ReadModifyWrite Ticker..ctor @tick | ReadModifyWrite Ticker..ctor @tick";
    private const string TICK_SELF_IN_START = "DCA1002 unprotected Value: ReadModifyWrite Ticker.StartAsync @tick | ReadModifyWrite Ticker.StartAsync @tick";
    private const string WORK_AGAINST_GET = "DCA1001 unprotected Value: Read StateController.Get @get | Write Worker.ExecuteAsync @work";

    /// <summary>The pairs of a poller whose timer is created in the given member.</summary>
    /// <param name="member">The member that creates the timer.</param>
    private static string[] PollerPairs(string member) =>
        [$"DCA1001 unprotected _ticks: Read Poller.Read @read | ReadModifyWrite Poller.{member} @tick",
         $"DCA1002 unprotected _ticks: ReadModifyWrite Poller.{member} @tick | ReadModifyWrite Poller.{member} @tick"];

    /// <summary>Each probe by name: its source, the findings run time calls for, as <see cref="HostedServiceProbe.Describe"/> writes
    /// them, and the stubs it references besides the fixture's.</summary>
    private static readonly Dictionary<string, (string Source, string[] Truth, string[] Stubs)> Probes = new(StringComparer.Ordinal)
    {
        // Timers. Change(int, int) and the int constructor are pinned by the Demo (timer-change-reactivates, threading-timer-self-overlap).
        ["timer-change-by-timespans"] = (Reactivated("TimeSpan.Zero, TimeSpan.FromSeconds(1)"), [TICK_SELF_IN_CTOR], []),
        ["timer-created-with-timespans"] = (STATE + Hosted("Ticker",
            "private readonly State _state; private Timer? _timer; public Ticker(State state) => _state = state;",
            "_timer = new Timer(_ => { /*@tick*/_state.Value++; }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1)); return Task.CompletedTask;")
            + Startup("services.AddSingleton<State>(); services.AddHostedService<Ticker>();"), [TICK_SELF_IN_START], []),
        ["timer-started-in-scoped-constructor"] = (Poller("ctor", "services.AddScoped<Poller>();"), PollerPairs(".ctor"), []),
        ["timer-started-in-scoped-method"] = (Poller("method", "services.AddScoped<Poller>();"), PollerPairs("Start"), []),
        ["timer-started-in-singleton-constructor"] = (Poller("ctor", "services.AddSingleton<Poller>();"), PollerPairs(".ctor"), []),
        ["timer-in-a-static-field"] = ("""
            public static class Clock
            {
                public static int Ticks;
                private static readonly Timer Ticker = new(_ => { /*@tick*/Ticks++; }, null, 0, 1000);
                public static int Read() => /*@read*/Ticks;
            }
            [ApiController, Route("clock")]
            public class ClockController : ControllerBase { [HttpGet] public int Get() => Clock.Read(); }
            """ + Startup(""),
            ["DCA1001 unprotected Ticks: Read Clock.Read @read | ReadModifyWrite Clock..cctor @tick",
             "DCA1002 unprotected Ticks: ReadModifyWrite Clock..cctor @tick | ReadModifyWrite Clock..cctor @tick"], []),
        ["timers-timer-elapsed-after-stop"] = (STATE + Hosted("Ticker",
            "private readonly State _state; private readonly System.Timers.Timer _timer = new(100); public Ticker(State state) => _state = state;",
            "_timer.Elapsed += (_, _) => { /*@tick*/_state.Value++; }; _timer.Start(); return Task.CompletedTask;",
            "_timer.Stop(); /*@after*/_state.Value = 0; return Task.CompletedTask;")
            + Startup("services.AddSingleton<State>(); services.AddHostedService<Ticker>();"),
            [TICK_SELF_IN_START, "DCA1002 unprotected Value: ReadModifyWrite Ticker.StartAsync @tick | Write Ticker.StopAsync @after"], []),
        ["itimer-periodic"] = (ProvidedTimer("TimeSpan.FromSeconds(1)"), [TICK_SELF_IN_START], []),
        ["itimer-one-shot"] = (ProvidedTimer("Timeout.InfiniteTimeSpan"), [], []),

        // The host.
        ["hosted-start-before-requests"] = (STATE + Hosted("Seeder",
            "private readonly State _state; public Seeder(State state) => _state = state;",
            "/*@seed*/_state.Value = 1; return Task.CompletedTask;") + """
            [ApiController, Route("state")]
            public class StateController(State state) : ControllerBase { [HttpGet] public int Get() => /*@get*/state.Value; }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Seeder>();"), [], []),
        ["hosted-stops-in-sequence"] = (STATE
            + Hosted("FirstStopper", "private readonly State _state; public FirstStopper(State state) => _state = state;", "return Task.CompletedTask;", "/*@first*/_state.Value = 1; return Task.CompletedTask;")
            + Hosted("SecondStopper", "private readonly State _state; public SecondStopper(State state) => _state = state;", "return Task.CompletedTask;", "/*@second*/_state.Value = 2; return Task.CompletedTask;")
            + Startup("services.AddSingleton<State>(); services.AddHostedService<FirstStopper>(); services.AddHostedService<SecondStopper>();"), [], []),
        ["worker-registered-by-factory"] = (Worker("services.AddHostedService(sp => new Worker(sp.GetRequiredService<State>()));"), [WORK_AGAINST_GET], []),
        ["worker-registered-by-type"] = (Worker("services.AddHostedService<Worker>();"), [WORK_AGAINST_GET], []),
        ["stopping-callback-against-request"] = (STATE + Hosted("Flusher",
            "private readonly State _state; private readonly IHostApplicationLifetime _lifetime; public Flusher(State state, IHostApplicationLifetime lifetime) { _state = state; _lifetime = lifetime; }",
            "_lifetime.ApplicationStopping.Register(() => { /*@flush*/_state.Value = 0; }); return Task.CompletedTask;") + """
            [ApiController, Route("state")]
            public class StateController(State state) : ControllerBase { [HttpPost] public void Post() => /*@post*/state.Value = 1; }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Flusher>();"),
            ["DCA1001 unprotected Value: Write Flusher.StartAsync @flush | Write StateController.Post @post",
             "DCA1001 unprotected Value: Write StateController.Post @post | Write StateController.Post @post"], []),

        // Request roots the providers do not list.
        ["hub-method"] = ("""
            public sealed class Tally { public int Value; }
            public sealed class ChatHub(Tally tally) : Microsoft.AspNetCore.SignalR.Hub { public void Send(string message) { /*@send*/tally.Value++; } }
            """ + Startup("services.AddSignalR(); services.AddSingleton<Tally>(); app.MapHub<ChatHub>(\"/chat\");"),
            ["DCA1002 unprotected Value: ReadModifyWrite ChatHub.Send @send | ReadModifyWrite ChatHub.Send @send"], SignalR),

        // Library callbacks, producers and events.
        ["register-over-a-request-object"] = (Registered("shutdown.Source.Token.Register({0});"),
            ["DCA1001 unprotected Value: Write JobController.Post @after | Write JobController.Post @callback"], []),
        ["register-disposed-before-write"] = (Registered("using (shutdown.Source.Token.Register({0})) { }"), [], []),
        ["watcher-handler-against-request"] = (STATE + Hosted("Watcher",
            "private readonly State _state; private readonly System.IO.FileSystemWatcher _watcher = new(\".\"); public Watcher(State state) => _state = state;",
            "_watcher.Changed += (_, _) => { /*@changed*/_state.Value = 1; }; _watcher.EnableRaisingEvents = true; return Task.CompletedTask;") + """
            [ApiController, Route("state")]
            public class StateController(State state) : ControllerBase { [HttpPost] public void Post() => /*@post*/state.Value = 2; }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Watcher>();"),
            ["DCA1001 unprotected Value: Write StateController.Post @post | Write StateController.Post @post",
             "DCA1001 unprotected Value: Write StateController.Post @post | Write Watcher.StartAsync @changed"], []),
        ["delegating-handler-shared"] = ("""
            public sealed class CountingHandler : System.Net.Http.DelegatingHandler
            {
                public int Calls;
                public CountingHandler() : base(new System.Net.Http.HttpClientHandler()) { }
                protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    /*@send*/Calls++;
                    return base.SendAsync(request, cancellationToken);
                }
            }
            public sealed class Api { public readonly System.Net.Http.HttpClient Client = new(new CountingHandler()); }
            [ApiController, Route("proxy")]
            public class ProxyController(Api api) : ControllerBase { [HttpGet] public Task<string> Get() => api.Client.GetStringAsync("http://localhost/"); }
            """ + Startup("services.AddSingleton<Api>();"),
            ["DCA1002 unprotected Calls: ReadModifyWrite CountingHandler.SendAsync @send | ReadModifyWrite CountingHandler.SendAsync @send"], []),
        ["action-block-parallel"] = (Block(", new System.Threading.Tasks.Dataflow.ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 4 }"),
            ["DCA1002 unprotected Value: ReadModifyWrite Pump..ctor @item | ReadModifyWrite Pump..ctor @item"], []),
        ["action-block-serial"] = (Block(""), [], []),
        ["async-iterator-of-requests"] = (Feed("var n = 0; await foreach (var x in feed.ReadAsync()) n += x; return n;"),
            ["DCA1002 unprotected Produced: ReadModifyWrite Feed.ReadAsync @produce | ReadModifyWrite Feed.ReadAsync @produce"], []),
        ["async-iterator-created-only"] = (Feed("var sequence = feed.ReadAsync(); GC.KeepAlive(sequence); await Task.CompletedTask; return 0;"), [], []),
        ["event-raised-by-requests"] = (STATE + """
            public sealed class Bus { public event Action? Ping; public void Raise() => Ping?.Invoke(); }
            """ + Hosted("Listener",
            "private readonly State _state; private readonly Bus _bus; public Listener(State state, Bus bus) { _state = state; _bus = bus; }",
            "_bus.Ping += () => { /*@handler*/_state.Value++; }; return Task.CompletedTask;") + """
            [ApiController, Route("ping")]
            public class PingController(Bus bus) : ControllerBase { [HttpPost] public void Post() => bus.Raise(); }
            """ + Startup("services.AddSingleton<State>(); services.AddSingleton<Bus>(); services.AddHostedService<Listener>();"),
            ["DCA1002 unprotected Value: ReadModifyWrite Listener.StartAsync @handler | ReadModifyWrite Listener.StartAsync @handler"], [])
    };

    /// <summary>Inputs checked for what the analysis discovers and collects only, with no run-time truth: a Razor Pages page model
    /// routes only with a compiled Razor page (<c>@page</c>, <c>@model</c>), which a fixture cannot supply, so no request reaches its
    /// handler in the program the fixture stands for; the check shows the provider finds no root for the handler.</summary>
    private static readonly Dictionary<string, (string Source, string[] Stubs)> Checks = new(StringComparer.Ordinal)
    {
        ["page-handler"] = ("""
            public sealed class Tally { public int Value; }
            public sealed class IndexModel(Tally tally) : Microsoft.AspNetCore.Mvc.RazorPages.PageModel { public void OnPost() { /*@post*/tally.Value++; } }
            """ + Startup("services.AddRazorPages(); services.AddSingleton<Tally>(); app.MapRazorPages();"), [StubAssemblies.MVC_RAZOR_PAGES])
    };

    /// <summary>The source and stubs of a probe or a check.</summary>
    /// <param name="name">The probe's or check's name.</param>
    private static (string Source, string[] Stubs) Input(string name) =>
        Probes.TryGetValue(name, out var probe) ? (probe.Source, probe.Stubs) : Checks[name];

    /// <summary>The probes the analysis answers otherwise than run time calls for, with the answer it gives and why.</summary>
    private static readonly Dictionary<string, ProbeDifference> Differences = new(StringComparer.Ordinal)
    {
        ["timer-started-in-scoped-constructor"] = new([PollerPairs(".ctor")[1]], SCOPED_CONSTRUCTOR_ORDERED),
        ["itimer-periodic"] = new([], ITIMER_IS_NOT_A_TIMER),
        ["hosted-start-before-requests"] = new(["DCA1001 unprotected Value: Read StateController.Get @get | Write Seeder.StartAsync @seed"], START_BEFORE_SERVER_IS_NOT_MODELLED),
        ["hosted-stops-in-sequence"] = new(["DCA1001 unprotected Value: Write FirstStopper.StopAsync @first | Write SecondStopper.StopAsync @second"], STOP_ORDER_IS_NOT_MODELLED),
        ["worker-registered-by-factory"] = new([], FACTORY_WORKER_IS_UNSUPPORTED),
        ["hub-method"] = new([], NO_HUB_OR_PAGE_ROOTS),
        ["async-iterator-created-only"] = new(["DCA1002 unprotected Produced: ReadModifyWrite Feed.ReadAsync @produce | ReadModifyWrite Feed.ReadAsync @produce"],
                                              ASYNC_ITERATOR_RUNS_AT_THE_CALL),
        ["register-over-a-request-object"] = new([], CAPTURE_STAYS_CONFINED),
        ["delegating-handler-shared"] = new([], OVERRIDE_CALLED_BY_THE_LIBRARY),
        ["action-block-parallel"] = new([], ACTION_BLOCK_HANDED_ONCE)
    };

    /// <summary>For each probe whose claim rests on which accesses exist: the accesses that must be collected, as
    /// <see cref="HostedServiceProbe.DescribeAccess"/> writes them, and the resources at marked sites that must have none, as
    /// <c>resource @marker</c> (<c>*</c> for any resource). An ordered or missed pair is read only once both its sides, or the lost one,
    /// are asserted.</summary>
    private static readonly Dictionary<string, (string[] Collected, string[] Lost)> AccessClaims = new(StringComparer.Ordinal)
    {
        ["timer-started-in-scoped-constructor"] = (["Read _ticks Poller.Read @read", "ReadModifyWrite _ticks Poller..ctor @tick"], []),
        ["itimer-periodic"] = (["ReadModifyWrite Value Ticker.StartAsync @tick"], []),
        ["itimer-one-shot"] = (["ReadModifyWrite Value Ticker.StartAsync @tick"], []),
        ["hosted-start-before-requests"] = (["Read Value StateController.Get @get", "Write Value Seeder.StartAsync @seed"], []),
        ["hosted-stops-in-sequence"] = (["Write Value FirstStopper.StopAsync @first", "Write Value SecondStopper.StopAsync @second"], []),
        ["worker-registered-by-factory"] = (["Read Value StateController.Get @get"], ["* @work"]),
        ["hub-method"] = ([], ["* @send"]),
        ["page-handler"] = ([], ["* @post"]),
        ["register-over-a-request-object"] = (["Write Value JobController.Post @after", "Write Value JobController.Post @callback"], []),
        ["register-disposed-before-write"] = (["Write Value JobController.Post @after", "Write Value JobController.Post @callback"], []),
        ["delegating-handler-shared"] = ([], ["* @send"]),
        ["action-block-parallel"] = (["ReadModifyWrite Value Pump..ctor @item"], []),
        ["action-block-serial"] = (["ReadModifyWrite Value Pump..ctor @item"], []),
        ["async-iterator-created-only"] = (["ReadModifyWrite Produced Feed.ReadAsync @produce"], [])
    };

    /// <summary>The lifecycle probes whose difference is a pair with no uncertainty: read with uncertain findings marked, the answer is
    /// exactly that pair, unmarked.</summary>
    private static readonly string[] UnqualifiedLifecyclePairs = ["hosted-start-before-requests", "hosted-stops-in-sequence"];

    /// <summary>The probes whose claim is about roots: the roots that must be there, as <c>kind member</c>, and the members no root
    /// may have.</summary>
    private static readonly Dictionary<string, (string[] Present, string[] AbsentMembers)> RootClaims = new(StringComparer.Ordinal)
    {
        ["worker-registered-by-factory"] = ([], ["Worker.ExecuteAsync"]),
        ["worker-registered-by-type"] = (["hosted-execute Worker.ExecuteAsync"], []),
        ["hub-method"] = ([], ["ChatHub.Send"]),
        ["page-handler"] = ([], ["IndexModel.OnPost"])
    };

    /// <summary>The names of the probes.</summary>
    public static TheoryData<string> Names() => new(Probes.Keys);

    /// <summary>The names of the probes with a claim about accesses.</summary>
    public static TheoryData<string> AccessClaimNames() => new(AccessClaims.Keys);

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

    /// <summary>The probe collected the accesses its claim names and none on the resources it says were lost at their marked sites.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(AccessClaimNames))]
    public async Task Probe_collects_the_accesses_its_claim_names(string name)
    {
        var (collected, lost) = AccessClaims[name];
        var (source, stubs) = Input(name);

        var answer = await HostedServiceProbe.AnalyzeAsync(source, new ProbeInputs([], stubs, false));

        HostedServiceProbe.AssertAccessClaims(answer, collected, lost);
    }

    /// <summary>The roots of a probe whose claim is about roots are the ones it names, and none has a member it rules out.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(RootClaimNames))]
    public async Task Probe_discovers_the_roots_its_claim_names(string name)
    {
        var (present, absentMembers) = RootClaims[name];
        var (source, stubs) = Input(name);

        var answer = await HostedServiceProbe.AnalyzeAsync(source, new ProbeInputs([], stubs, false));

        Assert.All(present, root => Assert.Contains(root, answer.Roots));
        Assert.All(absentMembers, member => Assert.DoesNotContain(answer.Roots, root => root.EndsWith(" " + member, StringComparison.Ordinal)));
    }

    /// <summary>The names of the lifecycle probes whose difference is a pair with no uncertainty.</summary>
    public static TheoryData<string> UnqualifiedLifecyclePairNames() => new(UnqualifiedLifecyclePairs);

    /// <summary>Read with uncertain findings marked, a lifecycle probe's answer is exactly its difference's pair, unmarked: the pair
    /// carries no uncertainty.</summary>
    /// <param name="name">The probe's name.</param>
    [Theory]
    [MemberData(nameof(UnqualifiedLifecyclePairNames))]
    public async Task Lifecycle_pair_carries_no_uncertainty(string name)
    {
        var answer = await HostedServiceProbe.AnalyzeAsync(Probes[name].Source, new ProbeInputs([], [], true));

        Assert.Equal(HostedServiceProbe.Canonical(Differences[name].Answer), answer.Findings);
    }

    /// <summary>The pair of the stopping callback with the request carries an uncertainty naming the unresolved
    /// <c>CancellationToken.Register</c> that hands the callback over, and the request's pair with itself carries none.</summary>
    [Fact]
    public async Task Stopping_callback_pair_names_the_unresolved_Register()
    {
        var answer = await HostedServiceProbe.AnalyzeAsync(Probes["stopping-callback-against-request"].Source, new ProbeInputs());

        Assert.Contains(answer.UncertaintiesByFinding["DCA1001 unprotected Value: Write Flusher.StartAsync @flush | Write StateController.Post @post"],
                        line => line.Contains("System.Threading.CancellationToken.Register(Action)", StringComparison.Ordinal));
        Assert.Empty(answer.UncertaintiesByFinding["DCA1001 unprotected Value: Write StateController.Post @post | Write StateController.Post @post"]);
    }

    /// <summary>A difference names a probe, pins an answer whose set differs from its truth and says why.</summary>
    [Fact]
    public void Every_difference_names_a_probe_and_differs_from_its_truth() =>
        HostedServiceProbe.AssertDifferences(Probes.ToDictionary(probe => probe.Key, probe => probe.Value.Truth, StringComparer.Ordinal), Differences);
}
