using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Roots;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class SharingAndOverlapTests
{
    private const string Types = """
        public sealed class Draft { public string? Text; }
        public sealed class Buffer { public string? Text; }
        public static class State { public static int Value; }
        public static class Gates { public static readonly object G = new(); public static readonly object H = new(); }

        """;

    [Fact]
    public void Hosted_root_never_pairs_with_itself()
    {
        var run = Analyze(Types + """
            public sealed class Worker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { State.Value = 1; State.Value = 2; return Task.CompletedTask; }
            }
            """ + Startup("services.AddHostedService<Worker>();"));

        Assert.Empty(run.Pairs.Pairs);
        Assert.Equal(3, run.Pairs.Skips[AccessPairing.SKIP_NO_SELF_OVERLAP]);
    }

    [Fact]
    public void Two_lifecycle_roots_of_one_hosted_instance_pair()
    {
        var run = Analyze(Types + """
            public sealed class ProgressWorker : BackgroundService
            {
                private string? _lastItem;
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { _lastItem = "item"; return Task.CompletedTask; }
                public override Task StopAsync(CancellationToken cancellationToken) { GC.KeepAlive(_lastItem); return Task.CompletedTask; }
            }
            """ + Startup("services.AddHostedService<ProgressWorker>();"));

        var pair = Assert.Single(run.Pairs.Pairs);
        Assert.NotEqual(pair.First.Root.RootId, pair.Second.Root.RootId);
        Assert.Equal(PairProtection.UNPROTECTED, pair.Protection);
    }

    [Fact]
    public void Hosted_service_registered_twice_pairs_with_itself()
    {
        var run = Analyze(Types + """
            public sealed class Worker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { State.Value = 1; return Task.CompletedTask; }
            }
            """ + Startup("services.AddHostedService<Worker>(); services.AddSingleton<IHostedService, Worker>();"));

        var pair = Assert.Single(run.Pairs.Pairs);
        Assert.Same(pair.First, pair.Second);
    }

    [Fact]
    public void Repeated_http_root_pairs_its_write_with_itself()
    {
        var run = Analyze(Types + """
            public class VisitsController : ControllerBase
            {
                public void Post() => State.Value = 1;
            }
            """ + Startup());

        var pair = Assert.Single(run.Pairs.Pairs);
        Assert.Same(pair.First, pair.Second);
        Assert.Equal(1, run.Pairs.CandidatePairs);
    }

    [Fact]
    public void Scoped_object_reached_from_http_never_pairs()
    {
        var run = Analyze(Types + """
            public class NotesController : ControllerBase
            {
                private readonly Draft _draft;
                public NotesController(Draft draft) => _draft = draft;
                public void Post(string text) => _draft.Text = text;
                public string? Get() => _draft.Text;
            }
            """ + Startup("services.AddScoped<Draft>();"));

        Assert.All(run.Of("Text"), access => Assert.Equal(SharingKeys.INVOCATION, access.SharingKey));
        Assert.Empty(run.Pairs.Pairs);
        Assert.Equal(2, run.Pairs.Skips[AccessPairing.SKIP_INVOCATION]);
        Assert.Equal(1, run.Pairs.Skips[AccessPairing.SKIP_READ_READ]);
    }

    [Fact]
    public void Scoped_service_injected_into_two_hosted_services_pairs_across_them()
    {
        var run = Analyze(Types + """
            public sealed class FirstWorker(Draft draft) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { draft.Text = "first"; return Task.CompletedTask; }
            }
            public sealed class SecondWorker(Draft draft) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { draft.Text = "second"; return Task.CompletedTask; }
            }
            """ + Startup("services.AddScoped<Draft>().AddHostedService<FirstWorker>().AddHostedService<SecondWorker>();"));

        Assert.All(run.Of("Text"), access => Assert.Equal("root-scope:Fixture:Draft", access.SharingKey));
        var pair = Assert.Single(run.Pairs.Pairs);
        Assert.NotEqual(pair.First.Root.RootId, pair.Second.Root.RootId);
    }

    [Fact]
    public void Transient_member_used_by_two_lifecycle_roots_pairs()
    {
        var run = Analyze(Types + """
            public sealed class Worker : BackgroundService
            {
                private readonly Buffer _buffer;
                public Worker(Buffer buffer) => _buffer = buffer;
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { _buffer.Text = "run"; return Task.CompletedTask; }
                public override Task StopAsync(CancellationToken cancellationToken) { GC.KeepAlive(_buffer.Text); return Task.CompletedTask; }
            }
            """ + Startup("services.AddTransient<Buffer>().AddHostedService<Worker>();"));

        Assert.All(run.Of("Text"), access => Assert.Equal("hosted:Fixture:Worker:ctor:buffer", access.SharingKey));
        Assert.Single(run.Pairs.Pairs);
    }

    [Fact]
    public void Transient_of_a_hosted_service_with_unknown_multiplicity_does_not_self_pair()
    {
        var run = Analyze(Types + """
            public sealed class Worker : BackgroundService
            {
                private readonly Buffer _buffer;
                private string? _stage;
                public Worker(Buffer buffer) => _buffer = buffer;
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { _buffer.Text = "run"; _stage = "run"; return Task.CompletedTask; }
                public override Task StopAsync(CancellationToken cancellationToken) { GC.KeepAlive(_buffer.Text); return Task.CompletedTask; }
            }
            """ + Startup("services.AddTransient<Buffer>().AddHostedService<Worker>().AddSingleton<IHostedService, Worker>();"));

        Assert.All(run.Of("Text"), access => Assert.Equal(Multiplicity.Unknown, access.Root.Policy.Multiplicity));
        var text = run.Pairs.Pairs.Where(pair => pair.First.Resource.Member.Name == "Text").ToArray();
        Assert.DoesNotContain(text, pair => pair.First.Root.RootId == pair.Second.Root.RootId);
        Assert.Single(text);
        var stage = Assert.Single(run.Pairs.Pairs, pair => pair.First.Resource.Member.Name == "_stage");
        Assert.Same(stage.First, stage.Second);
    }

    [Fact]
    public void Two_fields_assigned_from_one_transient_parameter_pair()
    {
        var run = Analyze(Types + """
            public sealed class Worker : BackgroundService
            {
                private readonly Buffer _writer;
                private readonly Buffer _reader;
                public Worker(Buffer buffer) { _writer = buffer; _reader = buffer; }
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { _writer.Text = "run"; return Task.CompletedTask; }
                public override Task StopAsync(CancellationToken cancellationToken) { GC.KeepAlive(_reader.Text); return Task.CompletedTask; }
            }
            """ + Startup("services.AddTransient<Buffer>().AddHostedService<Worker>();"));

        Assert.Single(run.Pairs.Pairs);
    }

    [Fact]
    public void Two_transient_constructor_parameters_never_pair()
    {
        var run = Analyze(Types + """
            public sealed class Worker : BackgroundService
            {
                private readonly Buffer _writer;
                private readonly Buffer _reader;
                public Worker(Buffer first, Buffer second) { _writer = first; _reader = second; }
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { _writer.Text = "run"; return Task.CompletedTask; }
                public override Task StopAsync(CancellationToken cancellationToken) { GC.KeepAlive(_reader.Text); return Task.CompletedTask; }
            }
            """ + Startup("services.AddTransient<Buffer>().AddHostedService<Worker>();"));

        Assert.Empty(run.Pairs.Pairs);
        Assert.Equal(1, run.Pairs.Skips[AccessPairing.SKIP_DIFFERENT_SHARING]);
    }

    [Fact]
    public void Injected_hosted_service_parameter_pairs_with_the_worker_receiver()
    {
        var run = Analyze(Types + """
            public sealed class WarmupWorker : BackgroundService
            {
                public string? Stage;
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { Stage = "started"; return Task.CompletedTask; }
            }
            public class WarmupController : ControllerBase
            {
                public string? Get([FromServices] IHostedService worker) => ((WarmupWorker)worker).Stage;
            }
            """ + Startup("services.AddHostedService<WarmupWorker>();"));

        Assert.All(run.Of("Stage"), access => Assert.Equal("process:Microsoft.Extensions.Hosting.Abstractions:Microsoft.Extensions.Hosting.IHostedService", access.SharingKey));
        var pair = Assert.Single(run.Pairs.Pairs);
        Assert.NotEqual(pair.First.Root.ProviderId, pair.Second.Root.ProviderId);
    }

    [Fact]
    public void Two_service_registrations_of_one_implementation_never_pair()
    {
        var run = Analyze(Types + """
            public interface IFirst { }
            public interface ISecond { }
            public sealed class Gate : IFirst, ISecond { public int Value; }
            public class FirstController : ControllerBase
            {
                private readonly IFirst _gate;
                public FirstController(IFirst gate) => _gate = gate;
                public void Post() => ((Gate)_gate).Value = 1;
            }
            public class SecondController : ControllerBase
            {
                private readonly ISecond _gate;
                public SecondController(ISecond gate) => _gate = gate;
                public void Post() => ((Gate)_gate).Value = 2;
            }
            """ + Startup("services.AddSingleton<IFirst, Gate>(); services.AddSingleton<ISecond, Gate>();"));

        Assert.Single(run.Of("Value").Select(access => access.Resource.Identity).Distinct());
        Assert.Equal(["process:Fixture:IFirst", "process:Fixture:ISecond"], run.Of("Value").Select(access => access.SharingKey).Order());
        Assert.DoesNotContain(run.Pairs.Pairs, pair => pair.First.Root.RootId != pair.Second.Root.RootId);
    }

    [Fact]
    public void Singleton_field_and_the_derived_field_hiding_it_never_pair()
    {
        var run = Analyze(Types + """
            public class Named { public string? Name; }
            public sealed class Renamed : Named { public new string? Name; }
            public class NamesController : ControllerBase
            {
                private readonly Renamed _named;
                public NamesController(Renamed named) => _named = named;
                public void Post() { _named.Name = "derived"; ((Named)_named).Name = "base"; }
            }
            """ + Startup("services.AddSingleton<Renamed>();"));

        var writes = run.Of("Name");
        Assert.Equal(2, writes.Select(access => access.Resource.Identity).Distinct().Count());
        Assert.Equal(["Named", "Renamed"], writes.Select(access => access.Resource.Member.DeclaringType).Order());
        Assert.All(run.Pairs.Pairs, pair => Assert.Same(pair.First, pair.Second));
    }

    [Fact]
    public void Two_scopes_never_pair()
    {
        const string source = Types + """
            public class VisitsController : ControllerBase
            {
                public void Post() => State.Value = 1;
            }
            """;
        var first = Analyze(source + Startup(), "scope:First");
        var second = Analyze(source + Startup(), "scope:Second");

        var pairs = AccessPairing.Pair(first.Accesses.Concat(second.Accesses));

        Assert.All(pairs.Pairs, pair => Assert.Equal(pair.First.Resource.Scope, pair.Second.Resource.Scope));
        Assert.Equal(2, pairs.Pairs.Count);
        Assert.Equal(2, pairs.CandidatePairs);
    }

    [Fact]
    public void Singleton_written_by_an_action_and_a_worker_pairs_across_providers()
    {
        var run = Analyze(Types + """
            public sealed class VisitorStats { public string? LastPath { get; set; } }
            public class VisitsController(VisitorStats stats) : ControllerBase
            {
                public void Post(string path) => stats.LastPath = path;
            }
            public sealed class StatsResetWorker(VisitorStats stats) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { stats.LastPath = null; return Task.CompletedTask; }
            }
            """ + Startup("services.AddSingleton<VisitorStats>().AddHostedService<StatsResetWorker>();"));

        Assert.Contains(run.Pairs.Pairs, pair => pair.First.Root.ProviderId != pair.Second.Root.ProviderId);
        Assert.Contains(run.Pairs.Pairs, pair => ReferenceEquals(pair.First, pair.Second));
        Assert.Equal(2, run.Pairs.Pairs.Count);
    }

    [Fact]
    public void Common_single_object_lock_suppresses_and_other_holdings_classify_the_pair()
    {
        var run = Analyze(Types + """
            public sealed class Ledger { public string? Entry; public string? Mixed; public string? Crossed; }
            public class LedgerController(Ledger ledger) : ControllerBase
            {
                public void Post() { lock (Gates.G) { ledger.Entry = "a"; ledger.Crossed = "a"; } ledger.Mixed = "a"; }
            }
            public sealed class LedgerWorker(Ledger ledger) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    lock (Gates.G) { ledger.Entry = "b"; ledger.Mixed = "b"; }
                    lock (Gates.H) { ledger.Crossed = "b"; }
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Ledger>().AddHostedService<LedgerWorker>();"));

        var crossRoot = run.Pairs.Pairs.Where(pair => pair.First.Root.RootId != pair.Second.Root.RootId).ToArray();
        Assert.DoesNotContain(crossRoot, pair => pair.First.Resource.Member.Name == "Entry");
        Assert.Equal(PairProtection.PARTIAL, Assert.Single(crossRoot, pair => pair.First.Resource.Member.Name == "Mixed").Protection);
        Assert.Equal(PairProtection.DIFFERENT_IDENTITY, Assert.Single(crossRoot, pair => pair.First.Resource.Member.Name == "Crossed").Protection);
        Assert.True(run.Pairs.Suppressed >= 2);
    }

    [Fact]
    public void Read_read_is_never_a_pair_and_counts_add_up()
    {
        var run = Analyze(Types + """
            public class ReadsController : ControllerBase
            {
                public int First() => State.Value;
                public int Second() => State.Value;
            }
            """ + Startup());

        Assert.Empty(run.Pairs.Pairs);
        Assert.Equal(3, run.Pairs.CandidatePairs);
        Assert.Equal(run.Pairs.CandidatePairs, run.Pairs.Skips.Values.Sum() + run.Pairs.Suppressed + run.Pairs.Pairs.Count);
    }
}
