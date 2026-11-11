using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Providers.LibraryModels.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class SourcePreparationTests
{
    [Fact]
    public async Task Standalone_download_precedes_cpu_backstop_seconds_and_stage_clocks()
    {
        await using var server = new LoopbackSymbolServer();
        using var fixture = new Fixture(server);
        using var sources = fixture.Sources(server);
        var clock = new PreparationClock();
        fixture.Delay(server, clock);
        using var owned = fixture.Ownership.Open();

        var answer = ModelGenerator.GenerateWithin(fixture.Request, fixture.Install.Resolver(), TimeSpan.FromSeconds(1), clock, sources: sources);

        Assert.Equal(LibraryCodeKind.OriginalSources, Assert.IsType<LibraryCode>(answer.Generation.Code).Kind);
        Assert.NotEqual(GenerationReasons.TIMEOUT, answer.Reason);
        Assert.Equal(0, answer.Generation.Seconds);
        Assert.NotEmpty(answer.Generation.Stages);
        Assert.All(answer.Generation.Stages.Values, seconds => Assert.Equal(0, seconds));
        Assert.All(answer.Generation.Attempts.SelectMany(attempt => attempt.Stages.Values), seconds => Assert.Equal(0, seconds));
        Assert.True(clock.Reads > 0);
        Assert.True(clock.Timers > 0);
        Assert.False(clock.UsedDuringPreparation);
        Assert.NotEmpty(server.Requests);
    }

    [Fact]
    public async Task Run_download_precedes_member_cpu_backstop_and_spent()
    {
        await using var server = new LoopbackSymbolServer();
        using var fixture = new Fixture(server);
        using var sources = fixture.Sources(server);
        var clock = new PreparationClock();
        fixture.Delay(server, clock);
        var subject = fixture.Run(sources, clock, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));

        subject.Generate(LibraryModels.BuiltIn, [fixture.Candidate]);
        subject.EndScope();

        Assert.Equal(TimeSpan.Zero, subject.Spent);
        var answer = subject.AnswerOf(fixture.Candidate.Target)!;
        Assert.NotEqual(GenerationOutcomeKind.Timeout, answer.Outcome.Kind);
        Assert.NotEqual(GenerationOutcomeKind.Failed, answer.Outcome.Kind);
        Assert.Equal(LibraryCodeKind.OriginalSources, fixture.Generated!.Generation.Code!.Kind);
        Assert.False(clock.UsedDuringPreparation);
        Assert.NotEmpty(server.Requests);
    }

    [Fact]
    public async Task Run_download_precedes_the_remaining_run_budget_timer()
    {
        await using var server = new LoopbackSymbolServer();
        using var fixture = new Fixture(server);
        using var sources = fixture.Sources(server);
        var clock = new PreparationClock();
        fixture.Delay(server, clock);
        var subject = fixture.Run(sources, clock, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

        subject.Generate(LibraryModels.BuiltIn, [fixture.Candidate]);

        Assert.Equal(TimeSpan.Zero, subject.Spent);
        Assert.NotEqual(GenerationOutcomeKind.Budget, subject.AnswerOf(fixture.Candidate.Target)!.Outcome.Kind);
        Assert.Equal(LibraryCodeKind.OriginalSources, fixture.Generated!.Generation.Code!.Kind);
        Assert.False(clock.UsedDuringPreparation);
        Assert.NotEmpty(server.Requests);
    }

    [Fact]
    public async Task An_exhausted_run_budget_does_not_prepare_the_member()
    {
        await using var server = new LoopbackSymbolServer();
        using var fixture = new Fixture(server);
        using var sources = fixture.Sources(server);
        var clock = new PreparationClock();
        var subject = fixture.Run(sources, clock, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        subject.Generate(LibraryModels.BuiltIn, [fixture.Candidate]);

        Assert.Null(fixture.Generated);
        Assert.Empty(server.Requests);
        Assert.False(Directory.Exists(fixture.Library.Cache));
        Assert.Equal(0, clock.Reads);
        Assert.Equal(0, clock.Timers);
    }

    [Fact]
    public async Task Run_cancellation_during_preparation_propagates_and_writes_no_answer()
    {
        await using var server = new LoopbackSymbolServer();
        using var fixture = new Fixture(server);
        using var sources = fixture.Sources(server);
        using var cancelled = new CancellationTokenSource();
        server.RequestReceived = _ => cancelled.Cancel();
        var clock = new PreparationClock();
        using var pass = GenerationPass.Create(new GenerationSetup(ModelGeneration.Generate, fixture.Install.Resolver(), Time: clock, Sources: sources),
                                               fixture.Repository, cancelled.Token)!;

        var error = Assert.ThrowsAny<OperationCanceledException>(() => pass.Prepare(LibraryModels.BuiltIn, [fixture.Candidate]));

        Assert.True(error.CancellationToken.IsCancellationRequested);
        Assert.Equal(TimeSpan.Zero, pass.Spent);
        Assert.False(File.Exists(fixture.Candidate.Target.Key.PathUnder(fixture.Repository)));
        Assert.Equal(0, clock.Reads);
        Assert.Equal(0, clock.Timers);
        Assert.NotEmpty(server.Requests);

        // The standalone boundary also preserves cancellation rather than reporting a member timeout.
        using var standaloneSources = fixture.Sources(server);
        using var standaloneCancellation = new CancellationTokenSource();
        server.RequestReceived = _ => standaloneCancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ModelGenerator.GenerateWithin(fixture.Request, fixture.Install.Resolver(),
            TimeSpan.FromSeconds(1), clock, standaloneCancellation.Token, standaloneSources));
        Assert.Equal(0, clock.Reads);
        Assert.Equal(0, clock.Timers);
    }

    [Fact]
    public async Task A_fake_generator_with_a_source_provider_does_not_prepare()
    {
        await using var server = new LoopbackSymbolServer();
        using var fixture = new Fixture(server);
        using var sources = fixture.Sources(server);
        var called = 0;
        using var pass = GenerationPass.Create(new GenerationSetup(ModelGeneration.Generate, fixture.Install.Resolver(),
            (request, _, _) =>
            {
                called++;
                return ModelGenerator.Generate(request, fixture.Install.Resolver(), CancellationToken.None);
            }, Sources: sources), fixture.Repository, CancellationToken.None)!;

        pass.Prepare(LibraryModels.BuiltIn, [fixture.Candidate]);

        Assert.Equal(1, called);
        Assert.Empty(server.Requests);
        Assert.False(Directory.Exists(fixture.Library.Cache));
        Assert.True(File.Exists(fixture.Candidate.Target.Key.PathUnder(fixture.Repository)));
    }

    [Fact]
    public async Task Setup_without_sources_keeps_disk_only_generation_and_storage()
    {
        await using var server = new LoopbackSymbolServer();
        using var fixture = new Fixture(server);
        using var pass = GenerationPass.Create(new GenerationSetup(ModelGeneration.Generate, fixture.Install.Resolver()),
                                               fixture.Repository, CancellationToken.None)!;

        pass.Prepare(LibraryModels.BuiltIn, [fixture.Candidate]);

        Assert.Empty(server.Requests);
        Assert.False(Directory.Exists(fixture.Library.Cache));
        Assert.True(File.Exists(fixture.Candidate.Target.Key.PathUnder(fixture.Repository)));
        var stored = GeneratedModelFiles.Read(fixture.Repository, fixture.Candidate.Target.Key);
        Assert.Contains(fixture.Request.MemberId, stored.Entries.Keys);
    }

    private sealed class PreparationClock : TimeProvider
    {
        private readonly FakeClock _clock = new();
        internal int Reads;
        internal int Timers;
        internal bool UsedDuringPreparation;
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override long GetTimestamp()
        {
            Interlocked.Increment(ref Reads);
            return _clock.GetTimestamp();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref Timers);
            return _clock.CreateTimer(callback, state, dueTime, period);
        }
        internal void Downloading()
        {
            UsedDuringPreparation |= Reads != 0 || Timers != 0;
            _clock.Advance(TimeSpan.FromMinutes(10));
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal GenerationInstall Install { get; } = new();
        internal SourceLibrary Library { get; }
        internal string Repository => Path.Combine(Install.Root, "repository");
        internal GenerationRequest Request { get; } = new("library", "8.0", "M:C0.M", null, null);
        internal GenerationCandidate Candidate { get; }
        internal CompilationOwnership Ownership { get; } = new();
        internal GeneratedAnswer? Generated;

        internal Fixture(LoopbackSymbolServer server)
        {
            Library = new SourceLibrary(server.Address, documentCount: 1);
            Library.Serve(server);
            var directory = Install.SharedFramework("8.0.5", runtime: true,
                images: new Dictionary<string, byte[]> { ["library"] = File.ReadAllBytes(Library.Dll) });
            var compilation = CSharpCompilation.Create("App", references:
                [.. EmittedAssemblies.RuntimeReferences, MetadataReference.CreateFromFile(Path.Combine(directory, "library.dll"))]);
            var member = compilation.GetTypeByMetadataName("C0")!.GetMembers("M").OfType<IMethodSymbol>().Single();
            var target = Assert.IsAssignableFrom<IdentifiedGenerationTarget>(GenerationTargets.Of(member, compilation, Install.Resolver(), CancellationToken.None,
                                                                                                  generationFrameworkMajor: 8));
            Candidate = new GenerationCandidate(compilation, member, target, false);
        }

        internal LibrarySources Sources(LoopbackSymbolServer server) => new(false, Library.Cache,
            new Uri(server.Address, "symbols/"), new Uri(server.Address, "nuget/"), httpsOnly: false);

        internal void Delay(LoopbackSymbolServer server, PreparationClock clock)
        {
            foreach (var (path, reply) in server.Replies)
                server.Replies[path] = reply with { Delay = TimeSpan.FromMilliseconds(50) };
            server.RequestReceived = _ => clock.Downloading();
        }

        internal RunGeneration Run(LibrarySources sources, TimeProvider clock, TimeSpan runLimit, TimeSpan memberLimit) =>
            new(Repository, CancellationToken.None, runLimit, memberLimit, clock,
                (_, _, _) => throw new InvalidOperationException("The prepared generation must be used."), (_, _) => 0,
                prepare: (request, token) =>
                {
                    var prepared = ModelGenerator.Prepare(request, Install.Resolver(), sources, token);
                    return (() => prepared?.RunOnly ?? false, (dependencies, memberToken) =>
                    {
                        using var owned = Ownership.Open();
                        return Generated = ModelGenerator.Generate(request, Install.Resolver(), memberToken, dependencies, prepared: prepared);
                    });
                });

        public void Dispose()
        {
            LibraryCompilation.Release(Ownership.Created);
            Install.Dispose();
            Library.Dispose();
        }
    }
}
